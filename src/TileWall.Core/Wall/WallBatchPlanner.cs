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
        double workWidthDip, double workHeightDip, WallMetrics? metrics = null,
        bool appendOnly = false, IReadOnlyDictionary<string, string>? launchIdentityKeys = null)
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

        var ordinary = TryPlanCore(current, members, targetGroupId, preferredLaneIndex, newGroupIdPrefix,
            workWidthDip, workHeightDip, metrics, allowCompression: false, appendOnly, launchIdentityKeys);
        return ordinary.Rejection == WallRejection.NoSpace
            ? TryPlanCore(current, members, targetGroupId, preferredLaneIndex, newGroupIdPrefix,
                workWidthDip, workHeightDip, metrics, allowCompression: true, appendOnly, launchIdentityKeys)
            : ordinary;
    }

    private static WallLayoutResult TryPlanCore(WallDocument current, IReadOnlyList<TileMember> members,
        string? targetGroupId, int? preferredLaneIndex, string newGroupIdPrefix,
        double workWidthDip, double workHeightDip, WallMetrics metrics, bool allowCompression,
        bool appendOnly, IReadOnlyDictionary<string, string>? launchIdentityKeys)
    {
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
        var laneOrder = Enumerable.Range(firstLane, current.Columns - firstLane).ToList();
        if (!appendOnly) laneOrder.AddRange(Enumerable.Range(0, firstLane));
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
        var groupIdentities = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (launchIdentityKeys is not null)
            foreach (var group in current.Lanes.SelectMany(lane => lane.Groups))
                foreach (var member in group.Members) RememberIdentity(group.Id, member.Id);
        var laneCursor = 0;
        var groupNumber = 1;

        foreach (var incoming in members)
        {
            if (placedIds.Contains(incoming.Id))
                return Reject(WallRejection.DuplicateId);
            if (activeGroupId is not null)
            {
                // A duplicate must not consume capacity or spill out of its requested
                // group simply because that group is full. Other groups keep their scope.
                if (launchIdentityKeys is not null && launchIdentityKeys.TryGetValue(incoming.Id, out var identity) &&
                    groupIdentities.TryGetValue(activeGroupId, out var seen) && seen.Contains(identity)) continue;
                var item = TryAddToGroup(lanes, incoming, activeGroupId, maxCandidateRows,
                    columns, current.Rows, workWidthDip, workHeightDip, metrics, current.Settings, allowCompression);
                if (item is not null)
                {
                    AcceptLocal(lanes, item, accepted, placedIds);
                    RememberIdentity(activeGroupId, incoming.Id);
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
                var fit = Fit(trial, trialColumns, current.Rows, workWidthDip, workHeightDip, metrics, current.Settings, allowCompression);
                if (fit == WallRejection.None)
                {
                    if (laneCursor >= laneOrder.Count) laneOrder.Add(lane);
                    reservedIds.Add(id);
                    columns = trialColumns;
                    lanes = trial;
                    AcceptLocal(lanes, item, accepted, placedIds);
                    activeGroupId = id;
                    RememberIdentity(id, incoming.Id);
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

        // An all-duplicate request has no mutation and must not advance generation.
        if (accepted.Count == 0)
            return new(new(current, WallLayoutEngine.Measure(current, metrics)), WallRejection.None);
        return WallLayoutEngine.TryApply(current,
            new AddBatchAction(current.Generation, accepted), workWidthDip, workHeightDip, metrics);

        void RememberIdentity(string groupId, string memberId)
        {
            if (launchIdentityKeys is null || !launchIdentityKeys.TryGetValue(memberId, out var identity)) return;
            if (!groupIdentities.TryGetValue(groupId, out var seen))
                groupIdentities[groupId] = seen = new HashSet<string>(StringComparer.Ordinal);
            seen.Add(identity);
        }
    }

    private static AddBatchItem? TryAddToGroup(List<SimLane> lanes, TileMember incoming, string groupId,
        int maxRows, int columns, int currentRows, double workWidthDip, double workHeightDip, WallMetrics metrics,
        WallSettings settings, bool allowCompression)
    {
        var location = FindGroup(lanes, groupId);
        if (location is null) return null;
        var (laneIndex, groupIndex) = location.Value;
        var group = lanes[laneIndex].Groups[groupIndex];
        if (maxRows < 1) return null;
        // An append beyond the viewport may still fit after existing empty rows
        // are compressed. Its uncompressed bounds must stay structurally valid.
        var searchRows = allowCompression
            ? Math.Max(maxRows, Math.Min(MaxRows, group.Bottom + incoming.LocalBounds.Height)) : maxRows;
        var bounds = MemberLayoutOrder.FindAppendBounds(group.Members, incoming.LocalBounds.Width,
            incoming.LocalBounds.Height, searchRows);
        if (bounds is null) return null;
        var trial = Copy(lanes);
        trial[laneIndex].Groups[groupIndex].Members.Add(bounds.Value);
        var fit = Fit(trial, columns, currentRows, workWidthDip, workHeightDip, metrics, settings, allowCompression);
        return fit == WallRejection.None
            ? new AddBatchItem(incoming with { LocalBounds = bounds.Value }, groupId) : null;
    }

    private static WallRejection Fit(List<SimLane> lanes, int columns, int currentRows,
        double workWidthDip, double workHeightDip, WallMetrics metrics, WallSettings settings, bool allowCompression)
    {
        var requiredHeight = lanes.Count == 0 ? metrics.TopMargin + metrics.Margin
            : lanes.Max(lane => LaneHeight(lane, metrics));
        var contentHeight = requiredHeight - metrics.TopMargin - metrics.Margin;
        var requiredRows = contentHeight <= metrics.HeightOf(1) ? 1
            : (int)Math.Ceiling((contentHeight + metrics.Gap) / metrics.Pitch - 1e-10);
        var rows = Math.Max(currentRows, requiredRows);
        var expanded = settings.ExpandColumns;
        var occupiedColumns = lanes.Count(lane => lane.Groups.Count > 0);
        if (expanded && occupiedColumns >= MaxLanes) return WallRejection.NoSpace;
        var projectedColumns = expanded ? occupiedColumns + 1 : columns;
        if (requiredRows <= MaxRows && requiredHeight <= metrics.WallHeight(rows) &&
            metrics.WallWidth(projectedColumns) <= workWidthDip + 1e-8 &&
            metrics.WallHeight(rows) <= workHeightDip + 1e-8)
            return WallRejection.None;
        if (!allowCompression) return requiredRows > MaxRows ? WallRejection.NoSpace : WallRejection.WorkAreaTooSmall;

        // A saved height, empty rows or whole-group arrangement may need the same
        // capacity compression as the final engine. Probe detached shapes only;
        // original coordinates and members remain owned by the final TryApply.
        var memberNumber = 0;
        var candidate = new WallDocument
        {
            Columns = columns,
            Rows = currentRows,
            Settings = settings,
            Lanes = lanes.Select(lane => new WallLane
            {
                Groups = lane.Groups.Select(group => new StartGroup
                {
                    Id = group.Id,
                    Title = group.Title,
                    Members = group.Members.Select(bounds => (WallMember)new TileMember
                    { Id = $"capacity-{memberNumber++}", LocalBounds = bounds }).ToArray(),
                }).ToArray(),
            }).ToArray(),
        };
        return WallLayoutEngine.ProbeCapacity(candidate, workWidthDip, workHeightDip, metrics);
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

