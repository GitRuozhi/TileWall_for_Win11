using TileWall.Core.Grid;

namespace TileWall.Core.Wall;

/// <summary>Plans an entire batch of ordinary tiles without changing the source wall.</summary>
public static class WallBatchPlanner
{
    private const int CellsPerLane = 8;
    private const int MaxRows = 256;
    private const int MaxLanes = 64;

    public static WallLayoutResult TryPlan(WallDocument current, IReadOnlyList<TileMember> members,
        string? targetGroupId, int? preferredLaneIndex, string newGroupIdPrefix,
        double workWidthDip, double workHeightDip, WallMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(members);
        metrics ??= new WallMetrics();
        if (members.Count == 0 || members.Any(member => member is null ||
                member.LocalBounds.Width is < 1 or > CellsPerLane || member.LocalBounds.Height is < 1 or > MaxRows) ||
            string.IsNullOrWhiteSpace(newGroupIdPrefix) || !metrics.IsValid)
            return Reject(WallRejection.InvalidAction);

        var errors = WallLayoutEngine.ValidateGeometry(current, metrics);
        if (errors.Count > 0) return Reject(WallRejection.InvalidDocument, string.Join("; ", errors));
        var targetLane = -1;
        if (targetGroupId is not null)
        {
            for (var lane = 0; lane < current.Lanes.Count; lane++)
                if (current.Lanes[lane].Groups.Any(group =>
                    string.Equals(group.Id, targetGroupId, StringComparison.OrdinalIgnoreCase)))
                {
                    targetLane = lane;
                    break;
                }
            if (targetLane < 0) return Reject(WallRejection.ObjectNotFound);
        }

        if (preferredLaneIndex is < 0 || preferredLaneIndex > current.Columns)
            return Reject(WallRejection.InvalidAction);
        var firstLane = targetLane >= 0 ? targetLane : preferredLaneIndex ?? 0;
        var laneOrder = Enumerable.Range(firstLane, current.Columns - firstLane)
            .Concat(Enumerable.Range(0, firstLane)).ToList();
        if (firstLane == current.Columns && current.Columns < MaxLanes)
            laneOrder.Insert(0, firstLane);

        var reservedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lane in current.Lanes)
        foreach (var group in lane.Groups)
        {
            reservedIds.Add(group.Id);
            foreach (var member in group.Members) reservedIds.Add(member.Id);
        }
        foreach (var member in members) reservedIds.Add(member.Id);

        var maxCandidateRows = Math.Clamp((int)Math.Floor((workHeightDip - metrics.TopMargin - metrics.Margin + metrics.Gap) /
            metrics.Pitch + 1e-8), 0, MaxRows);
        var accepted = new List<AddBatchItem>(members.Count);
        var lanes = current.Lanes.Select(lane => new SimLane
        {
            Groups = lane.Groups.Select(group => new SimGroup
            {
                Id = group.Id,
                Title = group.Title,
                Members = group.Members.Select(member => member.LocalBounds).ToList(),
            }).ToList(),
        }).ToList();
        var columns = current.Columns;
        var placedIds = new HashSet<string>(
            current.Lanes.SelectMany(lane => lane.Groups).SelectMany(group => group.Members).Select(member => member.Id),
            StringComparer.OrdinalIgnoreCase);
        var activeGroupId = targetGroupId;
        var laneCursor = 0;
        var groupNumber = 1;

        foreach (var incoming in members)
        {
            if (placedIds.Contains(incoming.Id))
                return Reject(WallRejection.DuplicateId);
            if (activeGroupId is not null)
            {
                var item = TryAddToGroup(lanes, incoming, activeGroupId, maxCandidateRows,
                    columns, current.Rows, workWidthDip, workHeightDip, metrics);
                if (item is not null)
                {
                    AcceptLocal(lanes, item, accepted, placedIds);
                    continue;
                }
                activeGroupId = null;
            }

            var placed = false;
            var id = NextGroupId(newGroupIdPrefix, reservedIds, ref groupNumber);
            while (laneCursor < MaxLanes)
            {
                var lane = laneCursor < laneOrder.Count ? laneOrder[laneCursor] : columns;
                if (lane >= MaxLanes) break;
                var item = new AddBatchItem(incoming with { LocalBounds = new GridRect(0, 0,
                        incoming.LocalBounds.Width, incoming.LocalBounds.Height) },
                    null, lane, lane < columns ? lanes[lane].Groups.Count : 0, id);
                var trial = Copy(lanes);
                var trialColumns = Math.Max(columns, lane + 1);
                while (trial.Count <= lane) trial.Add(new SimLane());
                trial[lane].Groups.Insert(item.GroupInsertIndex, new SimGroup
                {
                    Id = id,
                    Members = [item.Member.LocalBounds],
                });
                var fit = Fit(trial, trialColumns, current.Rows, workWidthDip, workHeightDip, metrics);
                if (fit == WallRejection.None)
                {
                    if (laneCursor >= laneOrder.Count) laneOrder.Add(lane);
                    reservedIds.Add(id);
                    columns = trialColumns;
                    lanes = trial;
                    AcceptLocal(lanes, item, accepted, placedIds);
                    activeGroupId = id;
                    placed = true;
                    break;
                }
                if (fit is not (WallRejection.NoSpace or WallRejection.WorkAreaTooSmall))
                    return Reject(fit);
                if (lane == columns && laneCursor >= laneOrder.Count) break;
                laneCursor++;
            }
            if (!placed) return Reject(WallRejection.NoSpace, $"Tile {accepted.Count + 1} does not fit.");
        }

        return WallLayoutEngine.TryApply(current,
            new AddBatchAction(current.Generation, accepted), workWidthDip, workHeightDip, metrics);
    }

    private static AddBatchItem? TryAddToGroup(List<SimLane> lanes, TileMember incoming, string groupId,
        int maxRows, int columns, int currentRows, double workWidthDip, double workHeightDip, WallMetrics metrics)
    {
        var location = FindGroup(lanes, groupId);
        if (location is null) return null;
        var (laneIndex, groupIndex) = location.Value;
        var group = lanes[laneIndex].Groups[groupIndex];
        if (maxRows < 1) return null;
        var occupied = new bool[maxRows, CellsPerLane];
        foreach (var member in group.Members)
        for (var row = member.Row; row < Math.Min(member.Bottom, maxRows); row++)
        for (var col = member.Column; col < member.Right; col++)
            occupied[row, col] = true;

        for (var row = 0; row + incoming.LocalBounds.Height <= maxRows; row++)
        for (var col = 0; col + incoming.LocalBounds.Width <= CellsPerLane; col++)
        {
            var bounds = new GridRect(col, row, incoming.LocalBounds.Width, incoming.LocalBounds.Height);
            var overlaps = false;
            for (var occupiedRow = row; occupiedRow < bounds.Bottom && !overlaps; occupiedRow++)
            for (var occupiedColumn = col; occupiedColumn < bounds.Right; occupiedColumn++)
                if (occupied[occupiedRow, occupiedColumn])
                {
                    overlaps = true;
                    break;
                }
            if (overlaps) continue;
            var trial = Copy(lanes);
            trial[laneIndex].Groups[groupIndex].Members.Add(bounds);
            var fit = Fit(trial, columns, currentRows, workWidthDip, workHeightDip, metrics);
            if (fit == WallRejection.None)
                return new AddBatchItem(incoming with { LocalBounds = bounds }, groupId);
            return null;
        }
        return null;
    }

    private static WallRejection Fit(List<SimLane> lanes, int columns, int currentRows,
        double workWidthDip, double workHeightDip, WallMetrics metrics)
    {
        var requiredHeight = lanes.Count == 0 ? metrics.TopMargin + metrics.Margin
            : lanes.Max(lane => LaneHeight(lane, metrics));
        var contentHeight = requiredHeight - metrics.TopMargin - metrics.Margin;
        var requiredRows = contentHeight <= metrics.HeightOf(1) ? 1
            : (int)Math.Ceiling((contentHeight + metrics.Gap) / metrics.Pitch - 1e-10);
        if (requiredRows > MaxRows) return WallRejection.NoSpace;
        var rows = Math.Max(currentRows, requiredRows);
        if (requiredHeight > metrics.WallHeight(rows)) return WallRejection.NoSpace;
        if (metrics.WallWidth(columns) > workWidthDip + 1e-8 || metrics.WallHeight(rows) > workHeightDip + 1e-8)
            return WallRejection.WorkAreaTooSmall;
        return WallRejection.None;
    }

    private static double LaneHeight(SimLane lane, WallMetrics metrics)
    {
        var height = metrics.TopMargin + metrics.Margin;
        foreach (var group in lane.Groups)
            height += metrics.TitleHeight + metrics.TitleToMembersGap + metrics.HeightOf(group.Bottom) + metrics.GroupGap;
        return lane.Groups.Count > 0 ? height - metrics.GroupGap : height;
    }

    private static (int Lane, int Group)? FindGroup(List<SimLane> lanes, string groupId)
    {
        for (var lane = 0; lane < lanes.Count; lane++)
        for (var group = 0; group < lanes[lane].Groups.Count; group++)
            if (string.Equals(lanes[lane].Groups[group].Id, groupId, StringComparison.OrdinalIgnoreCase))
                return (lane, group);
        return null;
    }

    private static List<SimLane> Copy(List<SimLane> lanes) => lanes.Select(lane => new SimLane
    {
        Groups = lane.Groups.Select(group => new SimGroup
        {
            Id = group.Id,
            Title = group.Title,
            Members = [.. group.Members],
        }).ToList(),
    }).ToList();

    private static void AcceptLocal(List<SimLane> lanes, AddBatchItem item,
        List<AddBatchItem> accepted, HashSet<string> placedIds)
    {
        if (item.TargetGroupId is not null)
        {
            var location = FindGroup(lanes, item.TargetGroupId)!.Value;
            lanes[location.Lane].Groups[location.Group].Members.Add(item.Member.LocalBounds);
        }
        accepted.Add(item);
        placedIds.Add(item.Member.Id);
    }

    private sealed class SimLane
    {
        public List<SimGroup> Groups { get; set; } = [];
    }

    private sealed class SimGroup
    {
        public string Id { get; set; } = "";
        public string? Title { get; set; }
        public List<GridRect> Members { get; set; } = [];
        public int Bottom => Members.Count == 0 ? 0 : Members.Max(member => member.Bottom);
    }

    private static string NextGroupId(string prefix, HashSet<string> reserved, ref int number)
    {
        string id;
        do { id = $"{prefix}-{number++:D4}"; } while (reserved.Contains(id));
        return id;
    }

    private static WallLayoutResult Reject(WallRejection rejection, string? detail = null) =>
        new(null, rejection, detail);
}

