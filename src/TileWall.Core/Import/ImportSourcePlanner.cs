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
        var sourceRows = candidates.OrderBy(candidate => candidate.SourceGroup?.Order ?? int.MaxValue)
            .ThenBy(candidate => candidate.SourceOrder).ThenBy(candidate => candidate.SourceKey, StringComparer.Ordinal)
            .Select(candidate => new SourceRow(candidate, SourceError(candidate), [candidate.SourceKey])).ToArray();
        // Count distinct valid members within a real original group before any filters or selection.
        var reduced = sourceRows.GroupBy(row => (Group: row.Source.SourceGroup?.Key ?? "ungrouped",
                Identity: row.Error is null ? row.Source.SelectionIdentity : "invalid:" + row.Source.SourceKey))
            .Select(group => group.First() with { Keys = group.SelectMany(row => row.Keys).ToArray() }).ToArray();
        var assigned = AssignGroups(reduced.Select(row => new ImportGroupingItem(row.Source.DisplayName,
            row.Source.SourceGroup, row.Source.Identity, row.Error is null, row.Source.RelativeDirectory,
            row.Source.Company, row.Source.SelectionIdentity)).ToArray(), recognizeGroups);
        var destinations = current.Lanes.SelectMany(lane => lane.Groups)
            .Where(group => !string.IsNullOrWhiteSpace(group.Title))
            .ToDictionary(group => StartMenuSourceCache.GroupNameKey(group.Title), StringComparer.Ordinal);
        var rows = reduced.Select((row, index) => (Row: row, Group: assigned[index])).ToArray();
        var projectedGroups = new List<ImportProjectedGroup>();
        foreach (var merged in rows.GroupBy(item => string.IsNullOrWhiteSpace(item.Group.Title)
                     ? "unnamed:" + item.Group.Key : "name:" + StartMenuSourceCache.GroupNameKey(item.Group.Title), StringComparer.Ordinal)
                     .OrderBy(group => group.Min(item => item.Group.Category))
                     .ThenBy(group => group.Min(item => item.Group.Order))
                     .ThenBy(group => group.First().Group.Title, StringComparer.OrdinalIgnoreCase))
        {
            var first = merged.First().Group;
            var name = StartMenuSourceCache.GroupNameKey(first.Title);
            var destination = name.Length == 0 ? null : destinations.GetValueOrDefault(name);
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
                    var reason = representative.Error is null ? FilterReason(source, replaced) : ImportFilterReason.None;
                    return new ImportProjectedCandidate(source, groupKey, group.SelectMany(item => item.Row.Keys).ToArray(),
                        representative.Error, reason, !autoFilter || reason == ImportFilterReason.None, replaced);
                }).ToArray();
            projectedGroups.Add(new(groupKey, first.Title?.Trim(), projectedGroups.Count, projected, destination?.Id));
        }
        return new(current.Generation, projectedGroups.ToArray(), projectedGroups.SelectMany(group => group.Candidates).ToArray(),
            autoFilter, recognizeGroups);
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
            var replaced = selectedRows.Select(candidate => candidate.ReplacedMemberId).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var members = old?.Members.Where(member => !replaced.Contains(member.Id)).ToList() ?? [];
            // Deliberately include the old slots of overwritten members in the append baseline.
            var baseline = old?.Members.Select(member => member.LocalBounds.Bottom).DefaultIfEmpty(0).Max() ?? 0;
            foreach (var block in selectedRows.GroupBy(candidate => candidate.Source.SourceGroup?.Key ?? "ungrouped", StringComparer.Ordinal))
            {
                var placed = new List<WallMember>();
                foreach (var candidate in block.Where(candidate => candidate.Source.HasRecordedPosition))
                {
                    var source = candidate.Source;
                    var sized = WithSize(source, defaultSize);
                    if (!TryRecordedBounds(sized.LocalBounds, baseline, out var bounds))
                        return Reject(WallRejection.NoSpace, "INVALID_SOURCE_GEOMETRY");
                    var id = candidate.ReplacedMemberId ?? AllocateId("import-member-", source.SourceKey, usedIds);
                    var member = Remap(sized, id, groupId, projectedGroup.Key) with { LocalBounds = bounds };
                    if (!MemberSizeRules.IsValid(member) || placed.Any(item => item.LocalBounds.Intersects(bounds)) ||
                        members.Any(item => item.LocalBounds.Intersects(bounds)))
                        return Reject(WallRejection.InvalidAction, "INVALID_SOURCE_GEOMETRY");
                    placed.Add(member);
                    accepted.Add(new(source.SourceKey, id, groupId, candidate.ReplacedMemberId));
                }
                // Missing positions have no implied source origin. Pack in stable row-major order below this block's baseline.
                var occupied = members.Select(member => member.LocalBounds).Concat(placed.Select(member => member.LocalBounds)).ToList();
                GridRect? previousSequential = null;
                foreach (var candidate in block.Where(candidate => !candidate.Source.HasRecordedPosition))
                {
                    var source = candidate.Source;
                    var sized = WithSize(source, defaultSize);
                    if (!MemberSizeRules.IsValid(sized)) return Reject(WallRejection.InvalidAction, "MEMBER_SIZE_LIMIT");
                    var bounds = FindSequentialBounds(occupied, sized.LocalBounds.Width, sized.LocalBounds.Height, baseline, previousSequential);
                    if (bounds is null) return Reject(WallRejection.NoSpace, "CONTENT_TOO_LARGE");
                    var id = candidate.ReplacedMemberId ?? AllocateId("import-member-", source.SourceKey, usedIds);
                    placed.Add(Remap(sized, id, groupId, projectedGroup.Key) with { LocalBounds = bounds.Value });
                    occupied.Add(bounds.Value);
                    previousSequential = bounds.Value;
                    accepted.Add(new(source.SourceKey, id, groupId, candidate.ReplacedMemberId));
                }
                members.AddRange(placed);
                if (placed.Count != 0) baseline = placed.Max(member => member.LocalBounds.Bottom);
            }
            var sourceMetadata = selectedRows.Select(candidate => candidate.Source.Member.ImportSource).FirstOrDefault(source => source is not null);
            incoming.Add((old ?? new StartGroup { Id = groupId, Title = projectedGroup.Title, Members = [] }) with
            {
                Members = members.ToArray(),
                ImportSource = sourceMetadata is null ? old?.ImportSource : sourceMetadata with
                    { OriginGroupId = groupId, GroupBucketKey = projectedGroup.Key, SourceRoot = null, RelativePath = null },
            });
        }
        var layout = StartMenuImportPlanner.TryPlan(current, expectedGeneration, incoming, metrics, budget, insertionPosition);
        return layout.Proposal is { } proposal ? new(ImportPlanStatus.Ready, proposal, accepted.ToArray()) :
            Reject(layout.Rejection, layout.Detail);
    }

    internal static IReadOnlyList<ImportGroupAssignment> AssignGroups(IReadOnlyList<ImportGroupingItem> items, bool recognizeGroups)
    {
        var counts = items.Where(item => item.CanAdd && item.SourceGroup is not null)
            .GroupBy(item => item.SourceGroup!.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.IdentityKey).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);
        var preserved = items.Select(item => item.SourceGroup is { } source &&
            (!recognizeGroups || counts.GetValueOrDefault(source.Key) > 1) ? source : null).ToArray();
        var presets = items.Select((item, index) => preserved[index] is null && recognizeGroups && item.Identity is { } identity
            ? StartMenuPresetRules.Match(item.DisplayName, item.RelativeDirectory, identity, item.Company) : null).ToArray();
        var presetCounts = items.Select((item, index) => (Item: item, Preset: presets[index]))
            .Where(pair => pair.Item.CanAdd && pair.Preset is not null).GroupBy(pair => pair.Preset!.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Item.IdentityKey).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);
        return items.Select((item, index) =>
        {
            if (preserved[index] is { } source) return new ImportGroupAssignment(source.Key, source.Title, source.Order, 0);
            if (presets[index] is { } preset && presetCounts.GetValueOrDefault(preset.Key) > 1)
                return new ImportGroupAssignment(preset.Key, preset.Title, preset.Order, 1);
            var initial = StartMenuSelectionRules.Initial(item.DisplayName);
            return new ImportGroupAssignment("letter:" + initial, initial.ToString(), initial == '#' ? 0 : initial - 'A' + 1, 2);
        }).ToArray();
    }

    private static string? SourceError(ImportSourceCandidate candidate)
    {
        if (candidate.ErrorCode is not null) return candidate.ErrorCode;
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

    private static ImportFilterReason FilterReason(ImportSourceCandidate source, string? replaced) => replaced is not null
        ? ImportFilterReason.AlreadyInGroup : source.Identity is not null && source.TargetAvailability == ImportTargetAvailability.Missing
            ? ImportFilterReason.MissingTarget : source.Exclusion switch
            {
                StartMenuExclusion.WindowsBuiltIn => ImportFilterReason.WindowsBuiltIn,
                StartMenuExclusion.Uninstall => ImportFilterReason.Uninstall,
                StartMenuExclusion.Maintenance => ImportFilterReason.Maintenance,
                StartMenuExclusion.DocumentOrWeb => ImportFilterReason.DocumentOrWeb,
                _ => ImportFilterReason.None,
            };

    private static WallMember WithSize(ImportSourceCandidate source, GridSize defaultSize) => source.HasRecordedSize ? source.Member :
        source.Member with { LocalBounds = new(source.Member.LocalBounds.Column, source.Member.LocalBounds.Row, defaultSize.Columns, defaultSize.Rows) };

    private static bool TryRecordedBounds(GridRect bounds, int baseline, out GridRect translated)
    {
        translated = bounds;
        if (bounds.Column < 0 || bounds.Row < 0 || bounds.Width < 1 || bounds.Height < 1 ||
            (long)bounds.Column + bounds.Width > 8 || (long)bounds.Row + bounds.Height + baseline > 256) return false;
        translated = bounds.Translate(0, baseline);
        return true;
    }

    private static GridRect? FindSequentialBounds(IReadOnlyList<GridRect> occupied, int width, int height, int baseline, GridRect? previous)
    {
        if (width is < 1 or > 8 || height is < 1 or > 12) return null;
        for (var row = previous?.Row ?? baseline; row + height <= 256; row++)
        for (var column = 0; column + width <= 8; column++)
        {
            if (previous is { } last && row == last.Row && column <= last.Column) continue;
            var bounds = new GridRect(column, row, width, height);
            if (!occupied.Any(item => item.Intersects(bounds))) return bounds;
        }
        return null;
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
    private sealed record SourceRow(ImportSourceCandidate Source, string? Error, IReadOnlyList<string> Keys);
}

internal sealed record ImportGroupingItem(string DisplayName, ImportSourceGroup? SourceGroup, LaunchIdentity? Identity,
    bool CanAdd, string RelativeDirectory, string? Company, string IdentityKey);
internal sealed record ImportGroupAssignment(string Key, string? Title, int Order, int Category);
