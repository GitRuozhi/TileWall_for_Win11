using TileWall.Core.Import;

namespace TileWall.ApplicationModel;

/// <summary>Reprojects one completed scan snapshot for the import dialog's display switches.</summary>
internal static class StartMenuImportProjection
{
    internal static IReadOnlyList<StartMenuImportCandidate> Project(
        IReadOnlyList<StartMenuImportCandidate> candidates, bool autoTrim, bool recognizeGroups)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var visible = candidates.Where(candidate => !candidate.IsDuplicate && !candidate.AlreadyPresent &&
            (!autoTrim || candidate.Exclusion == StartMenuExclusion.None)).ToArray();
        var buckets = StartMenuGroupingRules.Assign(visible.Select(candidate => new StartMenuGroupingItem(
            candidate.Candidate, candidate.Identity, candidate.CanAdd, candidate.Company)).ToArray(), recognizeGroups);

        return visible.Select((candidate, index) => candidate with { Bucket = buckets[index] })
            .OrderBy(candidate => candidate.Bucket.Order)
            .ThenBy(candidate => candidate.Bucket.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.Candidate.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
