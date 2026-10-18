using TileWall.Core.Grid;

namespace TileWall.Core.Wall;

/// <summary>Creates one complete proposal for prepending whole Start menu groups before existing lanes.</summary>
public static class StartMenuImportPlanner
{
    private const int CellsPerLane = 8;
    private const int MaxColumns = 64;
    private const int MaxRows = 256;

    /// <summary>
    /// Plans a replacement import against an already-pruned snapshot. The snapshot generation must remain
    /// the expected generation so pruning and insertion can be committed as one transaction.
    /// With a viewport budget, keep the saved height if possible, then grow only as much as required.
    /// </summary>
    public static WallLayoutResult TryPlan(WallDocument prunedSnapshot, long expectedGeneration,
        IReadOnlyList<StartGroup> importedGroups, WallMetrics? metrics = null, WallViewportBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(prunedSnapshot);
        ArgumentNullException.ThrowIfNull(importedGroups);
        metrics ??= new WallMetrics();
        if (!metrics.IsValid) return Reject(WallRejection.InvalidAction, "Invalid wall metrics.");
        if (expectedGeneration != prunedSnapshot.Generation)
            return Reject(WallRejection.StaleGeneration, "Import was based on an older generation.");

        // Removing the only old automatic lanes may yield a transient zero-lane snapshot.
        // Validate its remaining contracts through a temporary empty lane; never persist that substitute.
        var validationSnapshot = prunedSnapshot.Columns == 0 && prunedSnapshot.Lanes.Count == 0
            ? prunedSnapshot with { Columns = 1, Lanes = [new WallLane { Groups = [] }] }
            : prunedSnapshot;
        var currentErrors = WallLayoutEngine.ValidateGeometry(validationSnapshot, metrics);
        if (currentErrors.Count > 0)
            return Reject(WallRejection.InvalidDocument, string.Join("; ", currentErrors));

        var groups = new List<StartGroup>(importedGroups.Count);
        foreach (var group in importedGroups)
        {
            if (group is null || group.Members is null) return Reject(WallRejection.InvalidAction);
            if (group.Members.Count == 0) continue;
            if (!TryArrangeMembers(group, out var arranged))
                return Reject(WallRejection.NoSpace, $"Group {group.Id} exceeds the structural member area.");
            groups.Add(arranged);
        }
        if (groups.Count == 0) return Reject(WallRejection.InvalidAction, "There are no non-empty import groups.");
        var batches = groups.Select(group => group.ImportSource?.BatchId).Distinct(StringComparer.Ordinal).ToArray();
        if (batches.Length != 1 || string.IsNullOrWhiteSpace(batches[0]))
            return Reject(WallRejection.InvalidAction, "Import groups must share a non-empty source batch.");
        // Saved height is the first choice. A viewport budget supplies the upper
        // bound for growth, never a requested height for an otherwise small import.
        var maxRows = Math.Min(MaxRows, budget?.MaxRows ?? prunedSnapshot.Rows);
        var maxColumns = Math.Min(MaxColumns, budget?.MaxColumns ?? MaxColumns);
        if (budget is not null)
        {
            while (maxRows > 0 && metrics.WallHeight(maxRows) > budget.MaxHeightDip + 1e-8) maxRows--;
            while (maxColumns > 0 && metrics.WallWidth(maxColumns) > budget.MaxWidthDip + 1e-8) maxColumns--;
        }
        if (maxRows < prunedSnapshot.Rows || maxColumns < 1)
            return Reject(WallRejection.WorkAreaTooSmall, "CONTENT_TOO_LARGE");
        foreach (var group in groups)
        {
            var height = metrics.TopMargin + metrics.Margin + metrics.TitleHeight + metrics.TitleToMembersGap +
                metrics.HeightOf(group.Members.Max(m => m.LocalBounds.Bottom));
            if (height > metrics.WallHeight(maxRows) + 1e-8)
                return Reject(WallRejection.NoSpace, "GROUP_TOO_TALL:" + (string.IsNullOrWhiteSpace(group.Title) ? group.Id : group.Title));
        }
        // Existing non-empty lanes are never repacked. Empty lanes are removed
        // only when needed to make room for the new whole-group lanes.
        var maxNewColumns = maxColumns - prunedSnapshot.Lanes.Count(lane => lane.Groups.Count != 0);
        if (maxNewColumns < 1) return Reject(WallRejection.NoSpace, "CONTENT_TOO_LARGE");
        for (var rows = prunedSnapshot.Rows; rows <= maxRows; rows++)
        {
            for (var columns = 1; columns <= maxNewColumns; columns++)
            {
                if (!TryPack(groups, columns, rows, metrics, out var lanes)) continue;
                var retained = MakeRoomForLanes(prunedSnapshot, columns, maxColumns);
                var proposal = BuildProposal(retained, expectedGeneration, lanes, columns, rows, metrics);
                return proposal.Proposal is { } complete && budget is not null
                    ? WallViewportPolicy.Validate(complete.Document, budget, metrics) : proposal;
            }
        }
        return Reject(WallRejection.NoSpace, "CONTENT_TOO_LARGE");
    }

    private static WallDocument MakeRoomForLanes(WallDocument snapshot, int addedColumns, int maxColumns)
    {
        var remaining = snapshot.Columns + addedColumns - maxColumns;
        if (remaining <= 0) return snapshot;
        var lanes = snapshot.Lanes.ToList();
        // Match ordinary resize's least-displacement rule for empty columns.
        for (var index = lanes.Count - 1; index >= 0 && remaining > 0; index--)
        {
            if (lanes[index].Groups.Count != 0) continue;
            lanes.RemoveAt(index);
            remaining--;
        }
        return snapshot with { Columns = lanes.Count, Lanes = lanes.ToArray() };
    }
    private static WallLayoutResult BuildProposal(WallDocument snapshot, long expectedGeneration,
        IReadOnlyList<IReadOnlyList<StartGroup>> newLanes,
        int addedColumns, int rows, WallMetrics metrics)
    {
        if (snapshot.Columns + addedColumns > MaxColumns || rows is < 1 or > MaxRows)
            return Reject(WallRejection.NoSpace, "The import exceeds the wall structure limit.");

        var lanes = newLanes.Select(groups => new WallLane
            { Groups = groups.ToArray(), ImportBatchId = groups.FirstOrDefault()?.ImportSource?.BatchId })
            .Concat(snapshot.Lanes).ToArray();
        var proposed = snapshot with
        {
            Generation = expectedGeneration + 1,
            Columns = snapshot.Columns + addedColumns,
            Rows = rows,
            Lanes = lanes,
        };
        var errors = WallLayoutEngine.ValidateGeometry(proposed, metrics);
        if (errors.Count > 0)
            return Reject(WallRejection.InvalidAction, string.Join("; ", errors));

        return new WallLayoutResult(new WallProposal(proposed, WallLayoutEngine.Measure(proposed, metrics)),
            WallRejection.None);
    }

    private static bool TryPack(IReadOnlyList<StartGroup> groups, int laneCount, int rows,
        WallMetrics metrics, out IReadOnlyList<IReadOnlyList<StartGroup>> lanes)
    {
        var output = Enumerable.Range(0, laneCount).Select(_ => new List<StartGroup>()).ToArray();
        var laneIndex = 0;
        var usedHeight = metrics.TopMargin + metrics.Margin;
        var availableHeight = metrics.WallHeight(rows);

        foreach (var group in groups)
        {
            var groupHeight = metrics.TitleHeight + metrics.TitleToMembersGap +
                metrics.HeightOf(group.Members.Max(member => member.LocalBounds.Bottom));
            var gap = output[laneIndex].Count == 0 ? 0 : metrics.GroupGap;
            if (usedHeight + gap + groupHeight > availableHeight + 1e-9)
            {
                laneIndex++;
                if (laneIndex >= laneCount)
                {
                    lanes = [];
                    return false;
                }
                usedHeight = metrics.TopMargin + metrics.Margin;
                gap = 0;
            }

            if (usedHeight + gap + groupHeight > availableHeight + 1e-9)
            {
                lanes = [];
                return false;
            }

            output[laneIndex].Add(group);
            usedHeight += gap + groupHeight;
        }

        lanes = output;
        return true;
    }

    private static bool TryArrangeMembers(StartGroup group, out StartGroup arranged)
    {
        var occupied = new bool[MaxRows, CellsPerLane];
        var members = new WallMember[group.Members.Count];
        for (var memberIndex = 0; memberIndex < group.Members.Count; memberIndex++)
        {
            var member = group.Members[memberIndex];
            if (member is null || member is not (TileMember or PuzzleMember or ClockMember) ||
                member.LocalBounds.Width is < 1 or > CellsPerLane ||
                member.LocalBounds.Height is < 1 or > MaxRows)
            {
                arranged = group;
                return false;
            }

            GridRect? placement = null;
            for (var row = 0; row + member.LocalBounds.Height <= MaxRows && placement is null; row++)
            for (var column = 0; column + member.LocalBounds.Width <= CellsPerLane; column++)
            {
                var candidate = new GridRect(column, row, member.LocalBounds.Width, member.LocalBounds.Height);
                if (Intersects(occupied, candidate)) continue;
                placement = candidate;
                Mark(occupied, candidate, true);
                break;
            }

            if (placement is null)
            {
                arranged = group;
                return false;
            }
            members[memberIndex] = WithBounds(member, placement.Value);
        }

        arranged = group with { Members = members };
        return true;
    }

    private static bool Intersects(bool[,] occupied, GridRect bounds)
    {
        for (var row = bounds.Row; row < bounds.Bottom; row++)
        for (var column = bounds.Column; column < bounds.Right; column++)
            if (occupied[row, column]) return true;
        return false;
    }

    private static void Mark(bool[,] occupied, GridRect bounds, bool value)
    {
        for (var row = bounds.Row; row < bounds.Bottom; row++)
        for (var column = bounds.Column; column < bounds.Right; column++)
            occupied[row, column] = value;
    }

    private static WallMember WithBounds(WallMember member, GridRect bounds) => member switch
    {
        TileMember tile => tile with { LocalBounds = bounds },
        PuzzleMember puzzle => puzzle with { LocalBounds = bounds },
        ClockMember clock => clock with { LocalBounds = bounds },
        _ => throw new InvalidOperationException("Unknown member type."),
    };

    private static WallLayoutResult Reject(WallRejection rejection, string? detail = null) =>
        new(null, rejection, detail);
}

