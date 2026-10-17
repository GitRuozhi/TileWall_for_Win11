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
        var snapshot = current;
        var activeGroupId = targetGroupId;
        var laneCursor = 0;
        var groupNumber = 1;

        foreach (var incoming in members)
        {
            if (activeGroupId is not null)
            {
                var attempt = TryAddToGroup(snapshot, incoming, activeGroupId,
                    maxCandidateRows, workWidthDip, workHeightDip, metrics, out var item);
                if (attempt?.Success == true)
                {
                    Accept(attempt, item!, accepted, ref snapshot);
                    continue;
                }
                if (attempt is not null) return attempt;
                activeGroupId = null;
            }

            var placed = false;
            var id = NextGroupId(newGroupIdPrefix, reservedIds, ref groupNumber);
            while (laneCursor < MaxLanes)
            {
                var lane = laneCursor < laneOrder.Count ? laneOrder[laneCursor] : snapshot.Columns;
                if (lane >= MaxLanes) break;
                var item = new AddBatchItem(incoming with { LocalBounds = new GridRect(0, 0,
                        incoming.LocalBounds.Width, incoming.LocalBounds.Height) },
                    null, lane, lane < snapshot.Columns ? snapshot.Lanes[lane].Groups.Count : 0, id);
                var attempt = WallLayoutEngine.TryApply(snapshot,
                    new AddBatchAction(snapshot.Generation, [item]), workWidthDip, workHeightDip, metrics);
                if (attempt.Success)
                {
                    if (laneCursor >= laneOrder.Count) laneOrder.Add(lane);
                    reservedIds.Add(id);
                    Accept(attempt, item, accepted, ref snapshot);
                    activeGroupId = id;
                    placed = true;
                    break;
                }
                if (attempt.Rejection is not (WallRejection.NoSpace or WallRejection.WorkAreaTooSmall))
                    return attempt;
                if (lane == snapshot.Columns && laneCursor >= laneOrder.Count) break;
                laneCursor++;
            }
            if (!placed) return Reject(WallRejection.NoSpace, $"Tile {accepted.Count + 1} does not fit.");
        }

        // Every probe was disposable. Only this single complete action is offered for commit.
        return WallLayoutEngine.TryApply(current,
            new AddBatchAction(current.Generation, accepted), workWidthDip, workHeightDip, metrics);
    }

    private static WallLayoutResult? TryAddToGroup(WallDocument snapshot, TileMember incoming,
        string groupId, int maxRows, double workWidthDip, double workHeightDip, WallMetrics metrics,
        out AddBatchItem? acceptedItem)
    {
        acceptedItem = null;
        var group = snapshot.Lanes.SelectMany(lane => lane.Groups).First(candidate =>
            string.Equals(candidate.Id, groupId, StringComparison.OrdinalIgnoreCase));
        var occupied = new bool[maxRows, CellsPerLane];
        foreach (var member in group.Members)
        for (var row = member.LocalBounds.Row; row < Math.Min(member.LocalBounds.Bottom, maxRows); row++)
        for (var col = member.LocalBounds.Column; col < member.LocalBounds.Right; col++)
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
            var item = new AddBatchItem(incoming with { LocalBounds = bounds }, groupId);
            var attempt = WallLayoutEngine.TryApply(snapshot,
                new AddBatchAction(snapshot.Generation, [item]), workWidthDip, workHeightDip, metrics);
            if (attempt.Success)
            {
                acceptedItem = item;
                return attempt;
            }
            if (attempt.Rejection is not (WallRejection.NoSpace or WallRejection.WorkAreaTooSmall))
                return attempt;
            return null; // Higher rows cannot fix a height or work-area failure.
        }
        return null;
    }

    private static string NextGroupId(string prefix, HashSet<string> reserved, ref int number)
    {
        string id;
        do { id = $"{prefix}-{number++:D4}"; } while (reserved.Contains(id));
        return id;
    }

    private static void Accept(WallLayoutResult result, AddBatchItem item,
        List<AddBatchItem> accepted, ref WallDocument snapshot)
    {
        accepted.Add(item);
        snapshot = result.Proposal!.Document;
    }

    private static WallLayoutResult Reject(WallRejection rejection, string? detail = null) =>
        new(null, rejection, detail);
}

