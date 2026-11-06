using TileWall.Core.Import;

namespace TileWall.Core.Wall;

/// <summary>Packs complete import destination groups without changing member-local geometry.</summary>
public static class StartMenuImportPlanner
{
    private const int MaxColumns = 64;
    private const int MaxRows = 256;

    public static WallLayoutResult TryPlan(WallDocument current, long expectedGeneration,
        IReadOnlyList<StartGroup> importedGroups, WallMetrics? metrics = null, WallViewportBudget? budget = null,
        ImportInsertionPosition insertionPosition = ImportInsertionPosition.Head)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(importedGroups);
        metrics ??= new WallMetrics();
        if (!metrics.IsValid || !Enum.IsDefined(insertionPosition)) return Reject(WallRejection.InvalidAction);
        if (expectedGeneration != current.Generation) return Reject(WallRejection.StaleGeneration);
        var errors = WallLayoutEngine.ValidateGeometry(current, metrics);
        if (errors.Count != 0) return Reject(WallRejection.InvalidDocument, string.Join("; ", errors));
        var groups = importedGroups.Where(group => group is not null && group.Members is { Count: > 0 }).ToArray();
        if (groups.Length != importedGroups.Count || groups.Length == 0) return Reject(WallRejection.InvalidAction);
        if (groups.Select(group => group.Id).Distinct(StringComparer.Ordinal).Count() != groups.Length)
            return Reject(WallRejection.DuplicateId);
        foreach (var group in groups)
        {
            if (group.Members.Any(member => member is null || !MemberSizeRules.IsValid(member)))
                return Reject(WallRejection.InvalidAction, "MEMBER_SIZE_LIMIT");
            if (group.Members.Any(member => member.LocalBounds.Column < 0 || member.LocalBounds.Row < 0 ||
                    member.LocalBounds.Width < 1 || member.LocalBounds.Height < 1 ||
                    (long)member.LocalBounds.Column + member.LocalBounds.Width > 8 ||
                    (long)member.LocalBounds.Row + member.LocalBounds.Height > MaxRows) ||
                group.Members.SelectMany((member, index) => group.Members.Skip(index + 1)
                    .Select(other => member.LocalBounds.Intersects(other.LocalBounds))).Any(overlap => overlap))
                return Reject(WallRejection.InvalidAction, "INVALID_SOURCE_GEOMETRY");
        }
        var involved = groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        var untouched = current.Lanes.SelectMany(lane => lane.Groups).Where(group => !involved.Contains(group.Id));
        var ordered = (insertionPosition == ImportInsertionPosition.Head ? groups.Concat(untouched) : untouched.Concat(groups)).ToArray();
        var maxRows = Math.Min(MaxRows, budget?.MaxRows ?? current.Rows);
        var maxColumns = Math.Min(MaxColumns, budget?.MaxColumns ?? MaxColumns);
        if (budget is not null)
        {
            while (maxRows > 0 && metrics.WallHeight(maxRows) > budget.MaxHeightDip + 1e-8) maxRows--;
            while (maxColumns > 0 && metrics.WallWidth(maxColumns) > budget.MaxWidthDip + 1e-8) maxColumns--;
        }
        if (maxRows < 1 || maxColumns < 1 ||
            insertionPosition == ImportInsertionPosition.Head && maxRows < current.Rows)
            return Reject(WallRejection.WorkAreaTooSmall, "CONTENT_TOO_LARGE");
        var contentColumns = maxColumns - (current.Settings.ExpandColumns ? 1 : 0);
        if (contentColumns < 1) return Reject(WallRejection.WorkAreaTooSmall, "CONTENT_TOO_LARGE");
        if (insertionPosition == ImportInsertionPosition.Tail)
            return TryTail(current, expectedGeneration, groups, involved, maxRows, contentColumns, metrics, budget);
        foreach (var group in ordered)
        {
            var height = metrics.TopMargin + metrics.Margin + metrics.TitleHeight + metrics.TitleToMembersGap +
                metrics.HeightOf(group.Members.Max(member => member.LocalBounds.Bottom));
            if (height > metrics.WallHeight(maxRows) + 1e-8)
                return Reject(WallRejection.NoSpace, "GROUP_TOO_TALL:" + (string.IsNullOrWhiteSpace(group.Title) ? group.Id : group.Title));
        }
        var firstLaneCount = Math.Max(1, Math.Min(current.Columns, contentColumns));
        for (var rows = current.Rows; rows <= maxRows; rows++)
        for (var columns = firstLaneCount; columns <= contentColumns; columns++)
        {
            if (!TryPack(ordered, columns, rows, metrics, out var lanes)) continue;
            var proposed = current with
            {
                Generation = expectedGeneration + 1,
                Columns = lanes.Count,
                Rows = rows,
                Lanes = lanes.Select(lane => new WallLane { Groups = lane, ImportBatchId = CommonImportBatchId(lane) }).ToArray(),
            };
            var normalized = WallLayoutEngine.NormalizeExpanded(proposed, metrics);
            if (normalized.Proposal is not { } proposal) return normalized;
            var validated = budget is null ? normalized : WallViewportPolicy.Validate(proposal, budget, metrics);
            if (validated.Proposal is not null) return validated;
        }
        return Reject(WallRejection.NoSpace, "CONTENT_TOO_LARGE");
    }

    private static bool TryPack(IReadOnlyList<StartGroup> groups, int laneCount, int rows,
        WallMetrics metrics, out IReadOnlyList<IReadOnlyList<StartGroup>> lanes)
    {
        var packed = WallLayoutEngine.ArrangeSourceLanes(groups, metrics.WallHeight(rows), metrics,
            maximumLanes: laneCount, allowOversizedGroups: false);
        lanes = packed?.Select(lane => lane.Groups).ToArray() ?? [];
        return packed is not null;
    }

    private static WallLayoutResult TryTail(WallDocument current, long generation, StartGroup[] incoming,
        HashSet<string> involved, int maxRows, int maxColumns, WallMetrics metrics, WallViewportBudget? budget)
    {
        var original = current.Lanes.Select(lane => lane with
            { Groups = lane.Groups.Where(group => !involved.Contains(group.Id)).ToArray() }).ToArray();
        var lastContent = Array.FindLastIndex(original, lane => lane.Groups.Count != 0);
        var prefix = original.Take(lastContent + 1).ToArray();
        var minimumColumns = Math.Max(1, Math.Min(current.Columns, maxColumns));
        var firstRows = Math.Min(current.Rows, maxRows);

        // Keep all recorded whitespace and every untouched lane first. An empty tail is
        // reused at lastContent + 1; it is never stranded between the two content blocks.
        for (var rows = firstRows; rows <= maxRows; rows++)
        {
            var appended = WallLayoutEngine.ArrangeSourceLanes(incoming, metrics.WallHeight(rows), metrics,
                allowOversizedGroups: false);
            if (appended is null || prefix.Length + appended.Count > maxColumns) continue;
            var lanes = prefix.Concat(appended.Select(lane => lane with
                { ImportBatchId = CommonImportBatchId(lane.Groups) })).ToList();
            var targetCount = Math.Max(minimumColumns, lanes.Count);
            while (lanes.Count < targetCount) lanes.Add(new WallLane { Groups = [] });
            var result = Finish(lanes, rows);
            if (result.Proposal is not null) return result;
        }

        // Reuse the wall's existing compaction order: necessary empty lanes and complete
        // blank member rows first, then (only if that fails) continuous whole-group reflow.
        // Start each probe from immutable input, because the fitter mutates its lane lists.
        var minimalIncoming = WallLayoutEngine.ArrangeSourceLanes(incoming.Select(WallLayoutEngine.CompactGroupFully).ToArray(),
            metrics.WallHeight(maxRows), metrics, allowOversizedGroups: false);
        var incomingById = incoming.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var incomingLanes = minimalIncoming?.Select(lane => lane.Groups.Select(group => incomingById[group.Id]).ToList())
            ?? incoming.Select(group => new List<StartGroup> { group });
        var seed = prefix.Select(lane => lane.Groups.ToList()).Concat(incomingLanes).ToArray();
        foreach (var reflow in new[] { false, true })
        for (var columns = minimumColumns; columns <= maxColumns; columns++)
        {
            var lanes = seed.Select(lane => lane.ToList()).ToList();
            if (WallLayoutEngine.FitImportLanes(lanes, columns, maxRows, current.Rows, metrics, reflow) != WallRejection.None)
                continue;
            var result = Finish(lanes.Select(lane => new WallLane
                { Groups = lane.ToArray(), ImportBatchId = CommonImportBatchId(lane) }).ToList(), maxRows, trimHeight: true);
            if (result.Proposal is not null) return result;
        }
        var tooTall = prefix.SelectMany(lane => lane.Groups).Concat(incoming).FirstOrDefault(group =>
            metrics.TopMargin + metrics.Margin + metrics.TitleHeight + metrics.TitleToMembersGap +
            metrics.HeightOf(WallLayoutEngine.MinimumGroupRows(group)) > metrics.WallHeight(maxRows) + 1e-8);
        return tooTall is not null
            ? Reject(WallRejection.NoSpace, "GROUP_TOO_TALL:" + (string.IsNullOrWhiteSpace(tooTall.Title) ? tooTall.Id : tooTall.Title))
            : Reject(WallRejection.NoSpace, "CONTENT_TOO_LARGE");

        WallLayoutResult Finish(IReadOnlyList<WallLane> lanes, int rows, bool trimHeight = false)
        {
            if (trimHeight)
            {
                // Fit against the full physical budget to remove only necessary gaps,
                // then discard newly introduced wall-tail whitespace without moving content.
                var required = WallLayoutEngine.MeasureSource(lanes, metrics).WallHeight;
                rows = Math.Max(firstRows, (int)Math.Ceiling(
                    (required - metrics.TopMargin - metrics.Margin + metrics.Gap) / metrics.Pitch - 1e-10));
            }
            var proposal = current with { Generation = generation + 1, Columns = lanes.Count, Rows = rows, Lanes = lanes.ToArray() };
            var result = WallLayoutEngine.NormalizeExpanded(proposal, metrics);
            return result.Proposal is { } normalized && budget is not null
                ? WallViewportPolicy.Validate(normalized, budget, metrics) : result;
        }
    }

    private static string? CommonImportBatchId(IReadOnlyList<StartGroup> groups)
    {
        var batchId = groups.FirstOrDefault()?.ImportSource?.BatchId;
        return !string.IsNullOrWhiteSpace(batchId) && groups.All(group => group.ImportSource?.BatchId == batchId) ? batchId : null;
    }

    private static WallLayoutResult Reject(WallRejection rejection, string? detail = null) => new(null, rejection, detail);
}
