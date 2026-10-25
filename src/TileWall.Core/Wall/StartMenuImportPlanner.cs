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
        if (maxRows < current.Rows || maxColumns < 1) return Reject(WallRejection.WorkAreaTooSmall, "CONTENT_TOO_LARGE");
        var contentColumns = maxColumns - (current.Settings.ExpandColumns ? 1 : 0);
        if (contentColumns < 1) return Reject(WallRejection.WorkAreaTooSmall, "CONTENT_TOO_LARGE");
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
                if (++laneIndex >= laneCount) { lanes = []; return false; }
                usedHeight = metrics.TopMargin + metrics.Margin;
                gap = 0;
            }
            if (usedHeight + gap + groupHeight > availableHeight + 1e-9) { lanes = []; return false; }
            output[laneIndex].Add(group);
            usedHeight += gap + groupHeight;
        }
        lanes = output.Select(lane => (IReadOnlyList<StartGroup>)lane.ToArray()).ToArray();
        return true;
    }

    private static string? CommonImportBatchId(IReadOnlyList<StartGroup> groups)
    {
        var batchId = groups.FirstOrDefault()?.ImportSource?.BatchId;
        return !string.IsNullOrWhiteSpace(batchId) && groups.All(group => group.ImportSource?.BatchId == batchId) ? batchId : null;
    }

    private static WallLayoutResult Reject(WallRejection rejection, string? detail = null) => new(null, rejection, detail);
}
