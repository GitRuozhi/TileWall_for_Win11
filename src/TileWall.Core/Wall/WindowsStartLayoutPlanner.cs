using TileWall.Core.Grid;

namespace TileWall.Core.Wall;

internal sealed record WindowsStartImportGroup(string? Title, IReadOnlyList<TileMember> Members);

/// <summary>Plans an ordered batch of Windows Start pins at the tail of the current wall.</summary>
internal static class WindowsStartLayoutPlanner
{
    private const int FirstImportLane = 2;
    private const int MaximumColumns = 64;

    // The viewport budget describes physical capacity with expansion disabled. The
    // imported member IDs already identify the entries the caller will commit.
    internal static WallLayoutResult TryPlan(WallDocument current, IReadOnlyList<WindowsStartImportGroup> sourceGroups,
        WallViewportBudget visibleBudget, WallMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(sourceGroups);
        ArgumentNullException.ThrowIfNull(visibleBudget);
        var layoutMetrics = metrics ?? new WallMetrics();
        if (!layoutMetrics.IsValid || !double.IsFinite(visibleBudget.MaxWidthDip) ||
            !double.IsFinite(visibleBudget.MaxHeightDip))
            return Reject(WallRejection.InvalidAction);

        var errors = WallLayoutEngine.ValidateGeometry(current, layoutMetrics);
        if (errors.Count != 0) return Reject(WallRejection.InvalidDocument, string.Join("; ", errors));
        if (visibleBudget.FullScreen != current.Settings.FullScreen || sourceGroups.Any(group => group?.Members is null) ||
            sourceGroups.All(group => group.Members.Count == 0))
            return Reject(WallRejection.InvalidAction);

        var existingGroups = current.Lanes.SelectMany(lane => lane.Groups).ToArray();
        var usedIds = current.EnumerateAllMembers().Select(member => member.Id)
            .Concat(existingGroups.Select(group => group.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var member in sourceGroups.SelectMany(group => group.Members))
        {
            if (member is null || string.IsNullOrWhiteSpace(member.Id)) return Reject(WallRejection.InvalidAction);
            if (!usedIds.Add(member.Id)) return Reject(WallRejection.DuplicateId);
        }

        var rowLimit = Math.Min(current.Settings.LayoutLocked ? current.Rows : WallLayoutEngine.MaxRows,
            visibleBudget.MaxRows);
        while (rowLimit >= current.Rows && layoutMetrics.WallHeight(rowLimit) > visibleBudget.MaxHeightDip + 1e-8)
            rowLimit--;
        if (visibleBudget.MaxWidthDip <= 0 || rowLimit < current.Rows)
            return Reject(WallRejection.WorkAreaTooSmall, "CONTENT_TOO_LARGE");
        var rowsForMembers = rowLimit;
        var wallHeight = layoutMetrics.WallHeight(rowLimit);
        var groupHeader = layoutMetrics.TopMargin + layoutMetrics.Margin +
            layoutMetrics.TitleHeight + layoutMetrics.TitleToMembersGap;
        while (rowsForMembers > 0 && groupHeader + layoutMetrics.HeightOf(rowsForMembers) > wallHeight)
            rowsForMembers--;

        var lastOccupied = current.Lanes.Count - 1;
        while (lastOccupied >= 0 && current.Lanes[lastOccupied].Groups.Count == 0)
            lastOccupied--;
        var firstLane = Math.Max(FirstImportLane, lastOccupied + 1);
        var alreadyExpanded = current.Settings.ExpandColumns;

        var result = TrySize(2, alreadyExpanded);
        if (IsCapacityRejection(result)) result = TrySize(1, alreadyExpanded);
        if (!alreadyExpanded && IsCapacityRejection(result)) result = TrySize(1, expanded: true);
        return result;

        WallLayoutResult TrySize(int size, bool expanded)
        {
            var columnLimit = expanded ? MaximumColumns : Math.Min(visibleBudget.MaxColumns,
                WallViewportPolicy.CompleteColumns(visibleBudget.MaxWidthDip, layoutMetrics));
            if (!expanded && current.Settings.LayoutLocked)
                columnLimit = Math.Min(columnLimit, current.Columns);
            var laneLimit = columnLimit - firstLane - (expanded ? 1 : 0);
            if (!expanded && current.Columns > columnLimit)
                return Reject(WallRejection.WorkAreaTooSmall, "CONTENT_TOO_LARGE");
            if (laneLimit < 1)
                return Reject(WallRejection.NoSpace, expanded ? "COLUMN_LIMIT" : "CONTENT_TOO_LARGE");

            var tilesPerRow = WallLayoutEngine.CellsPerLane / size;
            var groupCapacity = tilesPerRow * (rowsForMembers / size);
            if (groupCapacity == 0) return Reject(WallRejection.NoSpace, "CONTENT_TOO_LARGE");

            // Only a group that exceeds the legal one-lane height is split. The
            // smaller height probes still pack complete groups in source order.
            var groups = BuildGroups(sourceGroups, current.Generation + 1, size, tilesPerRow,
                groupCapacity, usedIds, existingGroups);
            var budget = visibleBudget with { MaxRows = rowLimit, MaxColumns = columnLimit };
            if (expanded) budget = budget with { MaxWidthDip = layoutMetrics.WallWidth(columnLimit) };
            for (var rows = current.Rows; rows <= rowLimit; rows++)
            {
                var height = layoutMetrics.WallHeight(rows);
                var packed = WallLayoutEngine.ArrangeSourceLanes(groups, height, layoutMetrics,
                    maximumLanes: laneLimit, allowOversizedGroups: false);
                if (packed is null || packed.Any(lane => WallLayoutEngine.RequiredLaneHeight(lane.Groups, layoutMetrics) > height))
                    continue;

                var occupied = packed.TakeWhile(lane => lane.Groups.Count != 0).Count();
                var end = firstLane + occupied;
                var columns = expanded ? end + 1 : Math.Max(current.Columns, end);
                // Keep the current lane indices, including an empty second lane on
                // a narrow welcome wall. Expanded walls still have one empty tail.
                var lanes = new WallLane[columns];
                for (var index = 0; index < columns; index++)
                    lanes[index] = index < current.Columns ? current.Lanes[index] : new WallLane { Groups = [] };
                for (var index = 0; index < occupied; index++)
                    lanes[firstLane + index] = packed[index];
                var next = current with
                {
                    Generation = current.Generation + 1, Columns = columns, Rows = rows, Lanes = lanes,
                    Settings = current.Settings with { ExpandColumns = expanded },
                };
                var problems = WallLayoutEngine.ValidateGeometry(next, layoutMetrics);
                if (problems.Count != 0) return Reject(WallRejection.InvalidDocument, string.Join("; ", problems));
                if (WallLayoutEngine.ValidateLockedChange(current, next, layoutMetrics) is { } error)
                    return Reject(WallRejection.InvalidAction, error);
                return WallViewportPolicy.Validate(next, budget, layoutMetrics);
            }
            return Reject(WallRejection.NoSpace, expanded ? "COLUMN_LIMIT" : "CONTENT_TOO_LARGE");
        }
    }

    private static IReadOnlyList<StartGroup> BuildGroups(IReadOnlyList<WindowsStartImportGroup> sourceGroups,
        long generation, int size, int tilesPerRow, int groupCapacity, HashSet<string> usedIds,
        IReadOnlyList<StartGroup> existingGroups)
    {
        var ids = new HashSet<string>(usedIds, usedIds.Comparer);
        var titles = existingGroups.Select(group => group.Title?.Trim()).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = new List<StartGroup>();
        var number = 0;
        foreach (var source in sourceGroups)
        {
            for (var offset = 0; offset < source.Members.Count;)
            {
                var count = Math.Min(groupCapacity, source.Members.Count - offset);
                var members = new TileMember[count];
                for (var index = 0; index < count; index++)
                {
                    var bounds = new GridRect(index % tilesPerRow * size, index / tilesPerRow * size, size, size);
                    members[index] = source.Members[offset + index] with { LocalBounds = bounds };
                }
                string id;
                do { id = FormattableString.Invariant($"windows-start-{generation}-{++number}"); } while (!ids.Add(id));
                groups.Add(new() { Id = id, Title = ReserveTitle(source.Title), Members = members });
                offset += count;
            }
        }
        return groups;

        string? ReserveTitle(string? requested)
        {
            if (string.IsNullOrWhiteSpace(requested)) return null;
            var title = requested.Trim();
            var candidate = title;
            for (var suffix = 2; !titles.Add(candidate); suffix++)
                candidate = $"{title} ({suffix})";
            return candidate;
        }
    }

    private static bool IsCapacityRejection(WallLayoutResult result) =>
        result.Rejection is WallRejection.NoSpace or WallRejection.WorkAreaTooSmall;

    private static WallLayoutResult Reject(WallRejection rejection, string? detail = null) => new(null, rejection, detail);
}
