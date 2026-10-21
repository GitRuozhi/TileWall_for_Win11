using TileWall.Core.Grid;

namespace TileWall.Core.Wall;

/// <summary>Reflows the complete active wall after a successful incremental Start Menu merge.</summary>
public static class StartMenuImportPlanner
{
    private const int CellsPerLane = 8;
    private const int MaxColumns = 64;
    private const int MaxRows = 256;

    /// <summary>
    /// Receives complete merged destination groups, including retained old members. Matching IDs replace
    /// their active groups; new groups join the complete active sequence. The result orders every active
    /// group by its stored Start Menu bucket, then packs that sequence into whole lanes.
    /// </summary>
    public static WallLayoutResult TryPlan(WallDocument current, long expectedGeneration,
        IReadOnlyList<StartGroup> importedGroups, WallMetrics? metrics = null, WallViewportBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(importedGroups);
        metrics ??= new WallMetrics();
        if (!metrics.IsValid) return Reject(WallRejection.InvalidAction, "Invalid wall metrics.");
        if (expectedGeneration != current.Generation)
            return Reject(WallRejection.StaleGeneration, "Import was based on an older generation.");

        var currentErrors = WallLayoutEngine.ValidateGeometry(current, metrics);
        if (currentErrors.Count > 0)
            return Reject(WallRejection.InvalidDocument, string.Join("; ", currentErrors));

        var groups = new List<StartGroup>(importedGroups.Count);
        foreach (var group in importedGroups)
        {
            if (group is null || group.Members is null) return Reject(WallRejection.InvalidAction);
            if (group.Members.Count == 0) continue;
            if (group.Members.Any(member => member is not null && !MemberSizeRules.IsValid(member)))
                return Reject(WallRejection.InvalidAction, "MEMBER_SIZE_LIMIT");
            if (!TryArrangeMembers(group, out var arranged))
                return Reject(WallRejection.NoSpace, $"Group {group.Id} exceeds the structural member area.");
            groups.Add(arranged);
        }
        if (groups.Count == 0) return Reject(WallRejection.InvalidAction, "There are no non-empty import groups.");
        if (groups.Select(group => group.Id).Distinct(StringComparer.Ordinal).Count() != groups.Count)
            return Reject(WallRejection.DuplicateId, "Duplicate destination group.");

        var replacements = groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var existingIds = current.Lanes.SelectMany(lane => lane.Groups)
            .Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        var activeGroups = current.Lanes.SelectMany(lane => lane.Groups)
            .Select(group => replacements.GetValueOrDefault(group.Id) ?? group).ToList();
        foreach (var group in groups)
            if (!existingIds.Contains(group.Id)) activeGroups.Add(group);
        if (activeGroups.Select(group => group.Id).Distinct(StringComparer.Ordinal).Count() != activeGroups.Count)
            return Reject(WallRejection.DuplicateId, "Duplicate destination group.");

        var orderedGroups = OrderActiveGroups(activeGroups).ToArray();
        // A viewport budget limits growth, never shrinks saved height for an import.
        var maxRows = Math.Min(MaxRows, budget?.MaxRows ?? current.Rows);
        var maxColumns = Math.Min(MaxColumns, budget?.MaxColumns ?? MaxColumns);
        if (budget is not null)
        {
            while (maxRows > 0 && metrics.WallHeight(maxRows) > budget.MaxHeightDip + 1e-8) maxRows--;
            while (maxColumns > 0 && metrics.WallWidth(maxColumns) > budget.MaxWidthDip + 1e-8) maxColumns--;
        }
        if (maxRows < current.Rows || maxColumns < 1)
            return Reject(WallRejection.WorkAreaTooSmall, "CONTENT_TOO_LARGE");

        var contentColumns = maxColumns - (current.Settings.ExpandColumns ? 1 : 0);
        if (contentColumns < 1)
            return Reject(WallRejection.WorkAreaTooSmall, "CONTENT_TOO_LARGE");
        foreach (var group in orderedGroups)
        {
            var height = metrics.TopMargin + metrics.Margin + metrics.TitleHeight + metrics.TitleToMembersGap +
                metrics.HeightOf(group.Members.Max(member => member.LocalBounds.Bottom));
            if (height > metrics.WallHeight(maxRows) + 1e-8)
                return Reject(WallRejection.NoSpace, "GROUP_TOO_TALL:" +
                    (string.IsNullOrWhiteSpace(group.Title) ? group.Id : group.Title));
        }

        // Preserve the current lane count and height when possible. If the complete sorted sequence needs
        // more room, add lanes before growing the wall vertically, matching import's existing growth rule.
        var firstLaneCount = Math.Max(1, Math.Min(current.Columns, contentColumns));
        for (var rows = current.Rows; rows <= maxRows; rows++)
        for (var columns = firstLaneCount; columns <= contentColumns; columns++)
        {
            if (!TryPack(orderedGroups, columns, rows, metrics, out var lanes)) continue;
            var proposal = BuildProposal(current, expectedGeneration, lanes, rows, metrics);
            var validated = proposal.Proposal is { } complete && budget is not null
                ? WallViewportPolicy.Validate(complete, budget, metrics) : proposal;
            if (validated.Proposal is not null) return validated;
        }

        return Reject(WallRejection.NoSpace, "CONTENT_TOO_LARGE");
    }

    private static IReadOnlyList<StartGroup> OrderActiveGroups(IReadOnlyList<StartGroup> groups) => groups
        .Select(group => (Group: group, Sort: GetSortKey(group)))
        .OrderBy(item => item.Sort.Category)
        .ThenBy(item => item.Sort.BucketOrder)
        .ThenBy(item => item.Sort.Title, StringComparer.OrdinalIgnoreCase)
        .Select(item => item.Group)
        .ToArray();

    private static GroupSortKey GetSortKey(StartGroup group)
    {
        var source = group.ImportSource;
        var bucket = source?.GroupBucketKey;
        if (group.UserClaimed || source?.Kind != ImportSourceKind.StartMenu || string.IsNullOrWhiteSpace(bucket))
            return GroupSortKey.Custom;

        if (bucket.StartsWith("letter:", StringComparison.OrdinalIgnoreCase) && bucket.Length == 8)
        {
            var initial = char.ToUpperInvariant(bucket[^1]);
            if (initial == '#') return new(GroupSortCategory.Letter, 0, group.Title);
            if (initial is >= 'A' and <= 'Z')
                return new(GroupSortCategory.Letter, initial - 'A' + 1, group.Title);
            return GroupSortKey.Custom;
        }

        if (bucket.StartsWith("folder:", StringComparison.OrdinalIgnoreCase) && bucket.Length > 7)
            return new(GroupSortCategory.Folder, 0, group.Title);
        if (bucket.StartsWith("preset:", StringComparison.OrdinalIgnoreCase) && bucket.Length > 7)
            return new(GroupSortCategory.Preset, 0, group.Title);
        return GroupSortKey.Custom;
    }

    private static WallLayoutResult BuildProposal(WallDocument snapshot, long expectedGeneration,
        IReadOnlyList<IReadOnlyList<StartGroup>> packedLanes, int rows, WallMetrics metrics)
    {
        if (packedLanes.Count is < 1 or > MaxColumns || rows is < 1 or > MaxRows)
            return Reject(WallRejection.NoSpace, "The import exceeds the wall structure limit.");

        var lanes = packedLanes.Select(groups => new WallLane
        {
            Groups = groups.ToArray(),
            ImportBatchId = CommonImportBatchId(groups),
        }).ToArray();
        var proposed = snapshot with
        {
            Generation = expectedGeneration + 1,
            Columns = lanes.Length,
            Rows = rows,
            Lanes = lanes,
        };
        return WallLayoutEngine.NormalizeExpanded(proposed, metrics);
    }

    private static string? CommonImportBatchId(IReadOnlyList<StartGroup> groups)
    {
        if (groups.Count == 0 || string.IsNullOrWhiteSpace(groups[0].ImportSource?.BatchId)) return null;
        var batchId = groups[0].ImportSource!.BatchId;
        return groups.All(group => string.Equals(group.ImportSource?.BatchId, batchId, StringComparison.Ordinal))
            ? batchId : null;
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

    private enum GroupSortCategory { Letter, Folder, Preset, Custom }

    private readonly record struct GroupSortKey(GroupSortCategory Category, int BucketOrder, string? Title)
    {
        public static GroupSortKey Custom { get; } = new(GroupSortCategory.Custom, 0, null);
    }

    private static WallLayoutResult Reject(WallRejection rejection, string? detail = null) =>
        new(null, rejection, detail);
}
