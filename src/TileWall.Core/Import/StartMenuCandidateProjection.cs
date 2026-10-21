using TileWall.Core.Application;

namespace TileWall.Core.Import;

internal sealed record StartMenuSourceSelectionMigration(IReadOnlyDictionary<string, string> Selected, int RemovedCount);

/// <summary>Display-name merging precedes group-local identity reduction. Stable source provenance survives regrouping.</summary>
internal static class StartMenuCandidateProjection
{
    internal static IReadOnlyList<StartMenuImportCandidate> Project(IReadOnlyList<StartMenuImportCandidate> candidates,
        bool autoFilter, bool recognizeGroups)
    {
        var visible = candidates.Where(candidate => !autoFilter || candidate.Exclusion == StartMenuExclusion.None)
            .OrderBy(candidate => candidate.Candidate.Source)
            .ThenBy(candidate => candidate.Candidate.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
        var assigned = StartMenuGroupingRules.Assign(visible.Select(candidate => new StartMenuGroupingItem(
            candidate.Candidate, candidate.Identity, candidate.CanAdd, candidate.Company)).ToArray(), recognizeGroups);
        var buckets = assigned.GroupBy(bucket => StartMenuSourceCache.GroupNameKey(bucket.Title), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group =>
            {
                var first = group.OrderBy(bucket => bucket.Order).ThenBy(bucket => bucket.Key, StringComparer.Ordinal).First();
                // Coalesce by displayed title without replacing source provenance with a synthetic title key.
                return new StartMenuBucket(first.Key, first.Title.Trim(), first.Order);
            }, StringComparer.Ordinal);
        var grouped = visible.Select((candidate, index) => candidate with
            { Bucket = buckets[StartMenuSourceCache.GroupNameKey(assigned[index].Title)] });
        return grouped.GroupBy(candidate => (candidate.Bucket.Key,
                Identity: candidate.CanAdd ? candidate.Identity!.Key : "invalid:" + candidate.SelectionKey))
            .Select(group =>
            {
                var representative = group.OrderBy(candidate => candidate.Candidate.Source)
                    .ThenBy(candidate => candidate.Candidate.FullPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(candidate => candidate.Candidate.FullPath, StringComparer.Ordinal).First();
                return representative with { RepresentedSources = group.SelectMany(candidate => candidate.SelectionSources).ToArray() };
            })
            .OrderBy(candidate => candidate.Bucket.Order)
            .ThenBy(candidate => candidate.Bucket.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.Candidate.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static StartMenuSourceSelectionMigration Migrate(IReadOnlyDictionary<string, string> previous,
        IReadOnlyList<StartMenuImportCandidate> previousProjection, IReadOnlyList<StartMenuImportCandidate> projection,
        IReadOnlyList<StartMenuImportCandidate> allCandidates)
    {
        var addable = projection.Where(candidate => candidate.CanAdd).SelectMany(candidate => candidate.SelectionSources)
            .ToDictionary(source => source.SourceKey, source => source.IdentityKey, StringComparer.Ordinal);
        var selected = previous.Where(pair => addable.TryGetValue(pair.Key, out var identity) && identity == pair.Value)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var migrated = new HashSet<string>(StringComparer.Ordinal);
        var allCurrent = allCandidates.ToDictionary(candidate => candidate.SelectionKey, StringComparer.Ordinal);
        foreach (var candidate in projection.Where(candidate => candidate.CanAdd && candidate.Candidate.Source == SourceKind.User))
        {
            if (previousProjection.Any(old => old.CanAdd && old.SelectionSources.Any(source => source.SourceKey == candidate.SelectionKey))) continue;
            var old = previousProjection.FirstOrDefault(old => old.CanAdd && old.Candidate.Source == SourceKind.Common &&
                StartMenuSourceCache.GroupNameKey(old.Bucket.Title) ==
                    StartMenuSourceCache.GroupNameKey(candidate.Bucket.Title) &&
                old.Identity!.Key == candidate.Identity!.Key &&
                old.SelectionSources.Any(source => previous.GetValueOrDefault(source.SourceKey) == source.IdentityKey));
            if (old is null) continue;
            var oldSelected = old.SelectionSources.Where(source => previous.GetValueOrDefault(source.SourceKey) == source.IdentityKey).ToArray();
            if (oldSelected.Any(source => allCurrent.TryGetValue(source.SourceKey, out var current) &&
                    current.Identity?.Key != source.IdentityKey)) continue;
            selected[candidate.SelectionKey] = candidate.Identity!.Key;
            foreach (var source in oldSelected) { selected.Remove(source.SourceKey); migrated.Add(source.SourceKey); }
        }
        return new(selected, previous.Count(pair => !selected.ContainsKey(pair.Key) && !migrated.Contains(pair.Key)));
    }
}
