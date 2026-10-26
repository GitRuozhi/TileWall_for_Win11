using System.Security.Cryptography;
using System.Text;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Wall;

namespace TileWall.Core.Import;

/// <summary>Shared import-page grouping, filtering, overwrite and geometry policy. Performs no IO.</summary>
public static class ImportSourcePlanner
{
    public static ImportSourceProjection Project(WallDocument current, IReadOnlyList<ImportSourceCandidate> candidates,
        bool recognizeGroups = true, bool autoFilter = true,
        IReadOnlyDictionary<string, string>? existingIdentityKeys = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Any(candidate => candidate is null || string.IsNullOrWhiteSpace(candidate.SourceKey)) ||
            candidates.Select(candidate => candidate.SourceKey).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
            throw new ArgumentException("Source keys must be nonempty and unique.", nameof(candidates));
        existingIdentityKeys ??= new Dictionary<string, string>();
        var sourceRows = candidates.OrderBy(candidate => candidate.SourcePriority)
            .ThenBy(candidate => candidate.SourceOrder).ThenBy(candidate => candidate.SourceKey, StringComparer.Ordinal)
            .Select(candidate => new SourceRow(candidate, SourceError(candidate), [candidate.SourceKey])).ToArray();
        var representatives = new Dictionary<string, int>(StringComparer.Ordinal);
        var duplicateKeys = new Dictionary<int, List<string>>();
        for (var index = 0; index < sourceRows.Length; index++)
        {
            var row = sourceRows[index];
            if (!autoFilter || row.Error is not null || row.Source.Policy == ImportSourcePolicy.Attributed) continue;
            var reason = FilterReason(row.Source);
            if (reason == ImportFilterReason.None && row.Source.Identity is { } identity)
            {
                if (representatives.TryGetValue(identity.Key, out var representative))
                {
                    reason = ImportFilterReason.DuplicateTarget;
                    if (!duplicateKeys.TryGetValue(representative, out var keys))
                        duplicateKeys.Add(representative, keys = [sourceRows[representative].Source.SourceKey]);
                    keys.Add(row.Source.SourceKey);
                }
                else representatives.Add(identity.Key, index);
            }
            sourceRows[index] = row with { FilterReason = reason };
        }
        foreach (var (index, keys) in duplicateKeys) sourceRows[index] = sourceRows[index] with { Keys = keys.ToArray() };
        // Only the remaining source objects contribute to family/folder counts.
        // Invalid rows remain visible diagnostics, never duplicate representatives.
        var remaining = sourceRows.Where(row => row.FilterReason == ImportFilterReason.None).ToArray();
        var assigned = AssignGroups(remaining.Select(row => new ImportGroupingItem(row.Source.DisplayName,
            row.Source.SourceGroup, row.Source.Identity, row.Error is null, row.Source.RelativeDirectory,
            row.Source.Company, row.Source.SelectionIdentity, row.Source.Policy)).ToArray(), recognizeGroups);
        var destinations = current.Lanes.SelectMany(lane => lane.Groups)
            .Where(group => !string.IsNullOrWhiteSpace(group.Title))
            .ToDictionary(group => StartMenuSourceCache.GroupNameKey(group.Title), StringComparer.Ordinal);
        var rows = remaining.Select((row, index) => (Row: row, Group: assigned[index])).ToArray();
        var projectedGroups = new List<ImportProjectedGroup>();
        foreach (var merged in rows.GroupBy(item => string.IsNullOrWhiteSpace(item.Group.Title)
                     ? "unnamed:" + item.Group.Key : "name:" + StartMenuSourceCache.GroupNameKey(item.Group.Title), StringComparer.Ordinal)
                     .OrderBy(group => group.Min(item => item.Group.Category))
                     .ThenBy(group => group.Min(item => item.Group.Order))
                     .ThenBy(group => group.First().Group.Title, StringComparer.OrdinalIgnoreCase))
        {
            var first = merged.OrderBy(item => item.Group.Category).ThenBy(item => item.Group.Order).First().Group;
            var name = StartMenuSourceCache.GroupNameKey(first.Title);
            var destination = name.Length == 0 || merged.All(item => item.Row.Source.SourceKind == ImportPageSourceKind.RecycleBin)
                ? null : destinations.GetValueOrDefault(name);
            var destinationIds = new Dictionary<string, string>(StringComparer.Ordinal);
            if (destination is not null)
                foreach (var member in destination.Members)
                    if (existingIdentityKeys.TryGetValue(member.Id, out var identity))
                        destinationIds.TryAdd(identity, member.Id);
            var groupKey = merged.Key;
            var projected = merged.GroupBy(item => item.Row.Error is null
                    ? item.Row.Source.SelectionIdentity : "invalid:" + item.Row.Source.SourceKey, StringComparer.Ordinal)
                .Select(group =>
                {
                    var representative = group.First().Row;
                    var source = representative.Source;
                    var replaced = source.Identity is { } identity ? destinationIds.GetValueOrDefault(identity.Key) : null;
                    return new ImportProjectedCandidate(source, groupKey, group.SelectMany(item => item.Row.Keys).ToArray(),
                        representative.Error, ImportFilterReason.None, true, replaced);
                });
            var attributed = merged.All(item => item.Row.Source.Policy == ImportSourcePolicy.Attributed);
            var ordered = attributed ? projected.OrderBy(candidate => candidate.Source.SourceOrder) :
                projected.OrderBy(candidate => candidate.Source.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(candidate => candidate.Source.SourcePriority).ThenBy(candidate => candidate.Source.SourceOrder);
            var sourceGroup = merged.First().Row.Source.SourceGroup;
            projectedGroups.Add(new(groupKey, first.Title?.Trim(), projectedGroups.Count, ordered.ToArray(), destination?.Id)
            {
                PreserveSourceGroup = attributed, SourceGroupId = attributed ? sourceGroup?.Key : null,
                SourceLaneIndex = attributed ? sourceGroup?.LaneIndex ?? 0 : 0,
            });
        }
        var hidden = sourceRows.Where(row => row.FilterReason != ImportFilterReason.None)
            .Select(row => new ImportProjectedCandidate(row.Source, "filtered:" + row.Source.SourceKey, [row.Source.SourceKey],
                row.Error, row.FilterReason, false, null));
        var result = projectedGroups.SelectMany(group => group.Candidates).Concat(hidden).ToArray();
        var contentCounts = Enum.GetValues<ImportTargetKind>().Where(kind => kind != ImportTargetKind.Unknown)
            .ToDictionary(kind => kind, kind => candidates.Count(candidate => ContentKind(candidate) == kind));
        var filters = Enum.GetValues<ImportFilterReason>().Where(reason => reason != ImportFilterReason.None)
            .ToDictionary(reason => reason, reason => sourceRows.Count(row => row.FilterReason == reason));
        return new(current.Generation, projectedGroups.ToArray(), result, autoFilter, recognizeGroups)
        {
            Statistics = new(candidates.Count, filters.Values.Sum(), projectedGroups.Sum(group => group.Candidates.Count),
                contentCounts, filters),
        };
    }

    public static ImportSelectionMigration MigrateSelection(IReadOnlyDictionary<string, string> previous,
        ImportSourceProjection projection)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(projection);
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in projection.Candidates.Where(candidate => candidate.CanAdd))
        {
            var matches = candidate.SourceKeys.Where(key => previous.GetValueOrDefault(key) == candidate.Source.SelectionIdentity).ToArray();
            if (matches.Length == 0) continue;
            // Preserve the selected provenance through a merge so splitting the row cannot select another source.
            foreach (var key in matches)
            {
                selected[key] = candidate.Source.SelectionIdentity;
                kept.Add(key);
            }
        }
        return new(selected, previous.Keys.Count(key => !kept.Contains(key)));
    }

    public static ImportSourcePlan TryPlan(WallDocument current, long expectedGeneration,
        ImportSourceProjection projection, IEnumerable<string> selectedSourceKeys, GridSize defaultSize,
        ImportInsertionPosition insertionPosition = ImportInsertionPosition.Head,
        WallMetrics? metrics = null, WallViewportBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(selectedSourceKeys);
        if (expectedGeneration != current.Generation || projection.Generation != current.Generation)
            return Reject(WallRejection.StaleGeneration, "SOURCE_CHANGED");
        if (!Enum.IsDefined(insertionPosition) || !MemberSizeRules.IsTileSize(defaultSize.Columns, defaultSize.Rows))
            return Reject(WallRejection.InvalidAction, "MEMBER_SIZE_LIMIT");
        var selected = selectedSourceKeys.ToHashSet(StringComparer.Ordinal);
        var known = projection.Candidates.SelectMany(candidate => candidate.SourceKeys).ToHashSet(StringComparer.Ordinal);
        if (selected.Any(key => !known.Contains(key))) return Reject(WallRejection.InvalidAction, "SOURCE_CHANGED");
        var rows = projection.Candidates.Where(candidate => candidate.SourceKeys.Any(selected.Contains)).ToArray();
        if (rows.Any(candidate => candidate.ErrorCode is not null))
            return Reject(WallRejection.InvalidAction, rows.First(candidate => candidate.ErrorCode is not null).ErrorCode);
        rows = rows.Where(candidate => candidate.CanAdd).ToArray();
        if (rows.Length == 0) return new(ImportPlanStatus.NoChange, null, []);
        var existingGroups = current.Lanes.SelectMany(lane => lane.Groups).ToDictionary(group => group.Id, StringComparer.Ordinal);
        var usedIds = current.EnumerateAllMembers().Select(member => member.Id)
            .Concat(existingGroups.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var incoming = new List<StartGroup>();
        var accepted = new List<ImportAcceptedCandidate>();
        foreach (var projectedGroup in projection.Groups)
        {
            var selectedRows = rows.Where(candidate => candidate.GroupKey == projectedGroup.Key).ToArray();
            if (selectedRows.Length == 0) continue;
            var old = projectedGroup.DestinationGroupId is { } destinationId ? existingGroups.GetValueOrDefault(destinationId) : null;
            if (projectedGroup.DestinationGroupId is not null && old is null)
                return Reject(WallRejection.StaleGeneration, "SOURCE_CHANGED");
            var groupId = old?.Id ?? AllocateId("import-group-", projectedGroup.Key, usedIds);
            var built = BuildGroup(projectedGroup, selectedRows, defaultSize, old, groupId, usedIds, preview: false);
            if (built.Group is null) return Reject(built.Rejection, built.Detail);
            incoming.Add(built.Group); accepted.AddRange(built.Accepted);
        }
        var layout = StartMenuImportPlanner.TryPlan(current, expectedGeneration, incoming, metrics, budget, insertionPosition);
        return layout.Proposal is { } proposal ? new(ImportPlanStatus.Ready, proposal, accepted.ToArray()) :
            Reject(layout.Rejection, layout.Detail);
    }

    /// <summary>Complete source layout at an empty destination baseline. It is never a formal proposal or capacity admission.</summary>
    public static ImportSourcePreview BuildSourcePreview(ImportSourceProjection projection, GridSize defaultSize,
        WallMetrics? metrics = null, double? availableHeightDip = null, bool singleColumn = false)
    {
        ArgumentNullException.ThrowIfNull(projection);
        metrics ??= new();
        if (!metrics.IsValid || !MemberSizeRules.IsTileSize(defaultSize.Columns, defaultSize.Rows))
            throw new ArgumentException("Invalid source preview size or metrics.");
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<(ImportProjectedGroup Projection, StartGroup Group)>();
        var map = new Dictionary<string, ImportProjectedCandidate>(StringComparer.Ordinal);
        foreach (var projectedGroup in projection.Groups)
        {
            var id = projectedGroup.PreserveSourceGroup && projectedGroup.SourceGroupId is { } sourceId && used.Add(sourceId)
                ? sourceId : AllocateId("preview-group-", projectedGroup.Key, used);
            var rows = projectedGroup.Candidates.Select(candidate => candidate.ErrorCode is null ? candidate : candidate with
            {
                Source = candidate.Source with
                {
                    Member = new TileMember { Id = candidate.Source.Member.Id, LocalBounds = new(0, 0, 1, 1),
                        TargetInput = candidate.Source.Member.TargetInput, Visual = candidate.Source.Member.Visual }, HasRecordedPosition = false, HasRecordedSize = false,
                },
            }).ToArray();
            var built = BuildGroup(projectedGroup, rows, defaultSize, null, id, used, preview: true);
            if (built.Group is null)
                return new([], WallLayoutEngine.MeasureSource([], metrics), map, built.Detail);
            groups.Add((projectedGroup, built.Group));
            var originals = projectedGroup.Candidates.ToDictionary(candidate => candidate.Source.SourceKey, StringComparer.Ordinal);
            foreach (var accepted in built.Accepted) map.Add(accepted.MemberId, originals[accepted.SourceKey]);
        }
        IReadOnlyList<WallLane> lanes;
        if (singleColumn)
        {
            IEnumerable<(ImportProjectedGroup Projection, StartGroup Group)> orderedGroups = groups.All(item => item.Projection.PreserveSourceGroup)
                ? groups.OrderBy(item => item.Projection.SourceLaneIndex)
                : groups;
            lanes = [new WallLane { Groups = orderedGroups.Select(item => item.Group).ToArray() }];
        }
        else if (groups.Count != 0 && groups.All(item => item.Projection.PreserveSourceGroup))
        {
            var count = groups.Max(item => item.Projection.SourceLaneIndex) + 1;
            lanes = Enumerable.Range(0, count).Select(index => new WallLane
            {
                Groups = groups.Where(item => item.Projection.SourceLaneIndex == index).Select(item => item.Group).ToArray(),
            }).ToArray();
        }
        else lanes = WallLayoutEngine.ArrangeSourceLanes(groups.Select(item => item.Group).ToArray(),
            availableHeightDip ?? metrics.WallHeight(12), metrics) ?? [];
        return new(lanes, WallLayoutEngine.MeasureSource(lanes, metrics), map);
    }

    private static GroupBuildResult BuildGroup(ImportProjectedGroup group, IReadOnlyList<ImportProjectedCandidate> rows,
        GridSize defaultSize, StartGroup? old, string groupId, HashSet<string> usedIds, bool preview)
    {
        var replaced = rows.Select(candidate => candidate.ReplacedMemberId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var members = old?.Members.Where(member => !replaced.Contains(member.Id)).ToList() ?? [];
        // The overwritten member's old slot remains part of the append baseline.
        var baseline = old?.Members.Select(member => member.LocalBounds.Bottom).DefaultIfEmpty(0).Max() ?? 0;
        var maximumRows = preview ? int.MaxValue / 2 : 256;
        var accepted = new List<ImportAcceptedCandidate>();
        var occupied = new HashSet<(int Column, int Row)>();
        foreach (var member in members) Occupy(member.LocalBounds);
        var recordedGroups = rows.Where(candidate => candidate.Source.HasRecordedPosition)
            .Select(candidate => candidate.Source.SourceGroup?.Key ?? "ungrouped").ToHashSet(StringComparer.Ordinal);
        // Original folders are grouping evidence, not layout blocks. Once pure
        // sources share a final group, keep their stable order in one continuous
        // sequence. Only recorded source geometry needs an independent baseline.
        foreach (var block in rows.GroupBy(candidate => recordedGroups.Contains(candidate.Source.SourceGroup?.Key ?? "ungrouped")
                     ? "recorded:" + (candidate.Source.SourceGroup?.Key ?? "ungrouped") : "sequential", StringComparer.Ordinal))
        {
            var placed = new List<WallMember>();
            foreach (var candidate in block.Where(candidate => candidate.Source.HasRecordedPosition))
            {
                var sized = WithSize(candidate.Source, defaultSize);
                if (!TryRecordedBounds(sized.LocalBounds, baseline, maximumRows, out var bounds))
                    return new(null, [], WallRejection.NoSpace, "INVALID_SOURCE_GEOMETRY");
                if (!MemberSizeRules.IsValid(sized) || !IsFree(bounds))
                    return new(null, [], WallRejection.InvalidAction, "INVALID_SOURCE_GEOMETRY");
                Add(candidate, sized, bounds);
            }
            GridRect? previous = null;
            foreach (var candidate in block.Where(candidate => !candidate.Source.HasRecordedPosition))
            {
                var sized = WithSize(candidate.Source, defaultSize);
                if (!MemberSizeRules.IsValid(sized)) return new(null, [], WallRejection.InvalidAction, "MEMBER_SIZE_LIMIT");
                var bounds = FindSequentialBounds(sized.LocalBounds.Width, sized.LocalBounds.Height, baseline, previous);
                if (bounds is null) return new(null, [], WallRejection.NoSpace, "CONTENT_TOO_LARGE");
                Add(candidate, sized, bounds.Value); previous = bounds.Value;
            }
            members.AddRange(placed);
            if (placed.Count != 0) baseline = placed.Max(member => member.LocalBounds.Bottom);

            void Add(ImportProjectedCandidate candidate, WallMember sized, GridRect bounds)
            {
                var id = preview
                    ? usedIds.Add(candidate.Source.Member.Id) ? candidate.Source.Member.Id : AllocateId("preview-member-", candidate.Source.SourceKey, usedIds)
                    : candidate.ReplacedMemberId ?? AllocateId("import-member-", candidate.Source.SourceKey, usedIds);
                placed.Add(Remap(sized, id, groupId, group.Key) with { LocalBounds = bounds });
                Occupy(bounds);
                accepted.Add(new(candidate.Source.SourceKey, id, groupId, preview ? null : candidate.ReplacedMemberId));
            }
        }
        var metadata = rows.Select(candidate => candidate.Source.Member.ImportSource).FirstOrDefault(source => source is not null);
        return new((old ?? new StartGroup { Id = groupId, Title = group.Title, Members = [] }) with
        {
            Members = members.ToArray(), ImportSource = metadata is null ? old?.ImportSource : metadata with
                { OriginGroupId = groupId, GroupBucketKey = group.Key, SourceRoot = null, RelativePath = null },
        }, accepted.ToArray());

        bool IsFree(GridRect bounds)
        {
            for (var row = bounds.Row; row < bounds.Bottom; row++)
            for (var column = bounds.Column; column < bounds.Right; column++)
                if (occupied.Contains((column, row))) return false;
            return true;
        }
        void Occupy(GridRect bounds)
        {
            for (var row = bounds.Row; row < bounds.Bottom; row++)
            for (var column = bounds.Column; column < bounds.Right; column++) occupied.Add((column, row));
        }
        GridRect? FindSequentialBounds(int width, int height, int startRow, GridRect? last)
        {
            for (var row = last?.Row ?? startRow; row <= maximumRows - height; row++)
            for (var column = 0; column <= 8 - width; column++)
            {
                if (last is { } prior && row == prior.Row && column <= prior.Column) continue;
                var bounds = new GridRect(column, row, width, height);
                if (IsFree(bounds)) return bounds;
            }
            return null;
        }
    }

    internal static IReadOnlyList<ImportGroupAssignment> AssignGroups(IReadOnlyList<ImportGroupingItem> items, bool recognizeGroups)
    {
        var presets = items.Select(item => item.Policy != ImportSourcePolicy.Attributed && recognizeGroups && item.Identity is { } identity
            ? StartMenuPresetRules.Match(item.DisplayName, item.RelativeDirectory, identity, item.Company) : null).ToArray();
        var presetCounts = items.Select((item, index) => (Item: item, Preset: presets[index]))
            .Where(pair => pair.Item.CanAdd && pair.Preset is not null).GroupBy(pair => pair.Preset!.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Item.IdentityKey).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);
        var folderCounts = items.Select((item, index) => (Item: item, Preset: presets[index]))
            .Where(pair => pair.Item.CanAdd && pair.Item.Policy != ImportSourcePolicy.Attributed &&
                pair.Item.SourceGroup is not null && pair.Preset is null)
            .GroupBy(pair => pair.Item.SourceGroup!.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Item.IdentityKey).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);
        return items.Select((item, index) =>
        {
            if (item.Policy == ImportSourcePolicy.Attributed)
                return new ImportGroupAssignment(item.SourceGroup?.Key ?? "attributed", item.SourceGroup?.Title,
                    item.SourceGroup?.Order ?? 0, -1);
            if (!recognizeGroups && item.SourceGroup is { } original)
                return new ImportGroupAssignment(original.Key, original.Title, original.Order, 1);
            if (presets[index] is { } preset)
            {
                if (presetCounts.GetValueOrDefault(preset.Key) > 1)
                    return new ImportGroupAssignment(preset.Key, preset.Title, preset.Order, 2);
            }
            else if (item.SourceGroup is { } source && folderCounts.GetValueOrDefault(source.Key) > 1)
                return new ImportGroupAssignment(source.Key, source.Title, source.Order, 1);
            var initial = StartMenuSelectionRules.Initial(item.DisplayName);
            return new ImportGroupAssignment("letter:" + initial, initial.ToString(), initial == '#' ? 0 : initial - 'A' + 1, 0);
        }).ToArray();
    }

    private static string? SourceError(ImportSourceCandidate candidate)
    {
        if (candidate.ErrorCode is not null) return candidate.ErrorCode;
        if (!Enum.IsDefined(candidate.SourceKind) || !Enum.IsDefined(candidate.Policy) ||
            !Enum.IsDefined(candidate.TargetKind) || !Enum.IsDefined(candidate.TargetAvailability) ||
            candidate.SourceGroup is { LaneIndex: < 0 or > 63 }) return "INVALID_SOURCE_FACTS";
        if (candidate.Member is not (TileMember or PuzzleMember or ClockMember)) return "INVALID_SOURCE_MEMBER";
        if (candidate.RequiresIdentity && candidate.Identity is null) return "InvalidTarget";
        var bounds = candidate.Member.LocalBounds;
        if (candidate.HasRecordedSize && (!MemberSizeRules.IsValid(candidate.Member) || bounds.Width is < 1 or > 8 || bounds.Height is < 1 or > 12))
            return "MEMBER_SIZE_LIMIT";
        if (candidate.HasRecordedPosition && (bounds.Column is < 0 or > 7 || bounds.Row is < 0 or > 255 ||
                candidate.HasRecordedSize && ((long)bounds.Column + bounds.Width > 8 || (long)bounds.Row + bounds.Height > 256)))
            return "INVALID_SOURCE_GEOMETRY";
        return null;
    }

    private static ImportFilterReason FilterReason(ImportSourceCandidate source) =>
        source.TargetAvailability == ImportTargetAvailability.Missing ? ImportFilterReason.MissingTarget :
        (source.Policy is ImportSourcePolicy.Shortcut or ImportSourcePolicy.Application) && source.Exclusion != StartMenuExclusion.None
            ? ImportFilterReason.NonPrimaryEntry : ImportFilterReason.None;

    private static ImportTargetKind ContentKind(ImportSourceCandidate source) => source.HasLaunchTarget == false
        ? ImportTargetKind.Component : source.TargetKind == ImportTargetKind.Component
            ? ImportTargetKind.Unknown : source.TargetKind;

    private static WallMember WithSize(ImportSourceCandidate source, GridSize defaultSize) => source.HasRecordedSize ? source.Member :
        source.Member with { LocalBounds = new(source.Member.LocalBounds.Column, source.Member.LocalBounds.Row, defaultSize.Columns, defaultSize.Rows) };

    private static bool TryRecordedBounds(GridRect bounds, int baseline, int maximumRows, out GridRect translated)
    {
        translated = bounds;
        if (bounds.Column < 0 || bounds.Row < 0 || bounds.Width < 1 || bounds.Height < 1 ||
            (long)bounds.Column + bounds.Width > 8 || (long)bounds.Row + bounds.Height + baseline > maximumRows) return false;
        translated = bounds.Translate(0, baseline);
        return true;
    }

    private static WallMember Remap(WallMember source, string id, string groupId, string bucketKey) => source with
    {
        Id = id,
        Entry = source.Entry is { } entry ? new(id, $"Objects/{id}/entry.{(entry.Kind == ManagedEntryKind.Lnk ? "lnk" : "url")}", entry.Kind) : null,
        ImportSource = source.ImportSource is { } metadata ? metadata with { OriginGroupId = groupId, GroupBucketKey = bucketKey } : null,
    };

    private static string AllocateId(string prefix, string key, HashSet<string> used)
    {
        var stem = prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..24];
        var id = stem;
        var suffix = 1;
        while (!used.Add(id)) id = stem + "-" + suffix++;
        return id;
    }

    private static ImportSourcePlan Reject(WallRejection rejection, string? detail = null) =>
        new(ImportPlanStatus.Rejected, null, [], rejection, detail);
    private sealed record SourceRow(ImportSourceCandidate Source, string? Error, IReadOnlyList<string> Keys,
        ImportFilterReason FilterReason = ImportFilterReason.None);
    private sealed record GroupBuildResult(StartGroup? Group, IReadOnlyList<ImportAcceptedCandidate> Accepted,
        WallRejection Rejection = WallRejection.None, string? Detail = null);
}

internal sealed record ImportGroupingItem(string DisplayName, ImportSourceGroup? SourceGroup, LaunchIdentity? Identity,
    bool CanAdd, string RelativeDirectory, string? Company, string IdentityKey, ImportSourcePolicy Policy);
internal sealed record ImportGroupAssignment(string Key, string? Title, int Order, int Category);
