using TileWall.Core.Application;
using TileWall.Core.Entries;
using TileWall.Core.Exchange;
using TileWall.Core.Grid;
using TileWall.Core.Import;
using TileWall.Core.Wall;
using TileWall.Shell.Interop;

namespace TileWall.ApplicationModel;

/// <summary>Source adapters retain facts; grouping, filtering and placement belong to the common planner.</summary>
internal sealed class StartMenuImportProjection
{
    private const int MaximumAvailabilityEntries = 50000;
    private readonly object _availabilitySync = new();
    private readonly Dictionary<string, (string Fact, TargetObservation Observation)> _availability = new(StringComparer.Ordinal);
    private readonly object _projectionSync = new();
    private IReadOnlyList<ImportSourceCandidate> _startCandidates = [], _packageCandidates = [];
    private string? _packageSha256;

    internal void BeginPage()
    {
        // Source bytes are reusable; external target availability is an observation of this page.
        lock (_availabilitySync) _availability.Clear();
    }

    internal ImportPageSource FromStartMenu(StartMenuCatalogSnapshot snapshot,
        Func<StartMenuCandidate, string> iconsDirectory, CancellationToken token)
    {
        var sources = snapshot.Sources.OrderBy(source => source.Candidate.Source)
            .ThenBy(source => source.Candidate.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
        var candidates = new List<ImportSourceCandidate>();
        var originals = new Dictionary<string, StartMenuImportCandidate>(StringComparer.Ordinal);
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (source, index) in sources.Select((source, index) => (source, index)))
        {
            token.ThrowIfCancellationRequested();
            var candidate = source.Candidate;
            var error = source.ErrorCode ?? (source.Stale ? "SourceChanged" : !candidate.IsImportable ? "InvalidTarget" :
                !StartMenuSourceCache.IsHash(source.SourceSha256) ? "SourceChanged" : null);
            var exclusion = source.Identity is null ? StartMenuExclusion.None : StartMenuSelectionRules.Classify(candidate.DisplayName, source.Identity,
                targetIsDirectory: source.TargetIsDirectory);
            var fact = source.SourceSha256 + "|" + source.Identity?.Key + "|" + source.TargetAvailability + "|" + source.TargetIsDirectory + "|" +
                string.Join("|", source.Dependencies.Select(dependency => FormattableString.Invariant(
                    $"{dependency.Path}:{dependency.Length}:{dependency.LastWriteUtcTicks}:{dependency.Available}")));
            facts.Add(candidate.SourceKey, fact);
            originals[candidate.SourceKey] = new(candidate, new("pending", "", 0), source.Identity, exclusion,
                error, source.Stale, source.Company, source.SourceSha256, iconsDirectory(candidate), source.IconKey);
            var id = "start-source-" + candidate.SourceKey;
            var kind = candidate.Kind == EntryKind.Lnk ? ManagedEntryKind.Lnk : ManagedEntryKind.Url;
            var folder = candidate.RelativeDir.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            candidates.Add(new(candidate.SourceKey, candidate.DisplayName, new TileMember
            {
                Id = id, LocalBounds = new GridRect(0, 0, 1, 1),
                Entry = new(id, $"Objects/{id}/entry.{(kind == ManagedEntryKind.Lnk ? "lnk" : "url")}", kind),
                TargetInput = source.Identity is { } target && !target.Key.StartsWith("opaque", StringComparison.Ordinal)
                    ? target.Target : candidate.FullPath,
                SourceShortcutPath = candidate.FullPath, TargetDisplayName = candidate.DisplayName,
                Visual = new MemberVisual { Title = candidate.DisplayName },
                ImportSource = new() { BatchId = "start-menu", GroupBucketKey = folder is null ? "ungrouped" : "folder:" + folder.ToUpperInvariant(), OriginGroupId = id,
                    SourceRoot = candidate.RootPath, RelativePath = candidate.RelativePath },
            })
            {
                SourceKind = ImportPageSourceKind.StartMenu, Policy = ImportSourcePolicy.Shortcut, HasLaunchTarget = true,
                SourcePriority = (int)candidate.Source, TargetKind = ContentKind(source.Identity, true, source.TargetAvailability == ImportTargetAvailability.Available ? source.TargetIsDirectory : null),
                SourceGroup = string.IsNullOrWhiteSpace(folder) ? null : new("folder:" + folder.ToUpperInvariant(), folder, 100),
                SourceOrder = index, Identity = source.Identity, RequiresIdentity = true,
                HasRecordedPosition = false, HasRecordedSize = false, ErrorCode = error,
                TargetAvailability = source.TargetAvailability, Exclusion = exclusion,
                RelativeDirectory = candidate.RelativeDir, Company = source.Company,
            });
        }
        return new(ReuseCandidates(candidates), originals, Scanned: snapshot.ScannedCount, Skipped: snapshot.SkippedDirectories,
            ReadErrors: candidates.Count(candidate => candidate.ErrorCode is not null), Refreshing: snapshot.Refreshing, ErrorCode: snapshot.ErrorCode)
            { AvailabilityFacts = facts, AvailabilityPrepared = true, IconPreparing = snapshot.IconPreparing };
    }

    internal ImportPageSource FromPackage(WallPackagePreview preview, CancellationToken token)
    {
        var members = preview.Content.Members.ToDictionary(member => member.Id, StringComparer.Ordinal);
        var groups = preview.Content.Groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var candidates = new List<ImportSourceCandidate>();
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (lane, laneIndex) in preview.Content.Lanes.Select((lane, index) => (lane, index)))
        foreach (var (group, order) in lane.GroupIds.Select(id => groups[id]).Select((group, order) => (group, order)))
        foreach (var (id, index) in group.MemberIds.Select((id, index) => (id, index)))
        {
            token.ThrowIfCancellationRequested();
            var member = members[id].ToMember();
            var identity = preview.Identities.GetValueOrDefault(id);
            var error = identity?.IsUrl == true && !identity.Key.StartsWith("opaque-url:", StringComparison.Ordinal) &&
                !RegisteredUrlProtocols.IsSupported(identity.Target) ? "UnsupportedProtocol" : null;
            var fact = "package|" + preview.PackageSha256 + "|" + identity?.Key;
            facts.Add(id, fact);
            candidates.Add(new(id, member.Visual.Title ?? id, member)
            {
                SourceKind = ImportPageSourceKind.WallPackage, Policy = ImportSourcePolicy.Attributed,
                HasLaunchTarget = member.Entry is not null, TargetKind = ContentKind(identity, member.Entry is not null),
                SourceGroup = new(group.Id, group.Title, order) { LaneIndex = laneIndex }, SourceOrder = index, Identity = identity,
                RequiresIdentity = member.Entry is not null, HasRecordedPosition = true, HasRecordedSize = true,
                ErrorCode = error, TargetAvailability = ImportTargetAvailability.Unknown,
            });
        }
        return new(ReuseCandidates(candidates, preview.PackageSha256), new Dictionary<string, StartMenuImportCandidate>(), preview, candidates.Count)
            { AvailabilityFacts = facts, AvailabilityPrepared = true };
    }

    internal static ImportTargetKind ContentKind(LaunchIdentity? identity, bool hasTarget, bool? targetIsDirectory = null)
    {
        if (!hasTarget) return ImportTargetKind.Component;
        if (identity is null || identity.Key.StartsWith("opaque", StringComparison.Ordinal)) return ImportTargetKind.Unknown;
        if (StartMenuSelectionRules.Classify("", identity) == StartMenuExclusion.WindowsBuiltIn) return ImportTargetKind.SystemFeature;
        if (identity.IsUrl) return identity.Target.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || identity.Target.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
            ? ImportTargetKind.WebLink : ImportTargetKind.ProtocolLink;
        if (!Path.IsPathFullyQualified(identity.Target)) return identity.Key.StartsWith("shell:", StringComparison.Ordinal) ? ImportTargetKind.Program : ImportTargetKind.Unknown;
        if (targetIsDirectory == true) return ImportTargetKind.Folder;
        if (targetIsDirectory is null) return ImportTargetKind.Unknown;
        return Path.GetExtension(identity.Target).ToLowerInvariant() is ".exe" or ".com" ? ImportTargetKind.Program : ImportTargetKind.File;
    }

    private IReadOnlyList<ImportSourceCandidate> ReuseCandidates(List<ImportSourceCandidate> candidates, string? packageSha256 = null)
    {
        lock (_projectionSync)
        {
            var previous = packageSha256 is null ? _startCandidates :
                packageSha256 == _packageSha256 ? _packageCandidates : [];
            var old = previous.ToDictionary(candidate => candidate.SourceKey, StringComparer.Ordinal);
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                if (!old.TryGetValue(candidate.SourceKey, out var prior)) continue;
                // A package digest fixes its member records. Start-menu members
                // are generated only from title, entry path and provenance.
                var sameMember = packageSha256 is not null || candidate.Member.TargetInput == prior.Member.TargetInput &&
                    candidate.Member.ImportSource == prior.Member.ImportSource;
                if (sameMember && (candidate with { Member = prior.Member }) == prior) candidates[index] = prior;
            }
            var result = candidates.Count == previous.Count && candidates.Select((candidate, index) =>
                ReferenceEquals(candidate, previous[index])).All(same => same) ? previous : candidates.ToArray();
            if (packageSha256 is null) _startCandidates = result;
            else { _packageSha256 = packageSha256; _packageCandidates = result; }
            return result;
        }
    }

    internal async Task<ImportPageSource> PrepareAvailabilityAsync(ImportPageSource source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (source.AvailabilityPrepared || source.Package is not null) return source with { AvailabilityPrepared = true };
        // The main window retains bounded source facts across pages. Progress-only
        // catalog publications reuse observations; changed entry/dependency facts
        // require IO. Uncertain locations never become confirmed missing by cache.
        var observed = new Dictionary<string, TargetObservation>(StringComparer.Ordinal);
        var pending = new List<ImportSourceCandidate>();
        lock (_availabilitySync)
        {
            foreach (var candidate in source.Candidates)
                if (_availability.TryGetValue(candidate.SourceKey, out var cached) &&
                    cached.Fact == source.AvailabilityFacts[candidate.SourceKey])
                    observed.Add(candidate.SourceKey, cached.Observation);
                else pending.Add(candidate);
        }
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
            (candidate, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                var observation = ObserveTarget(candidate.Identity);
                cancellation.ThrowIfCancellationRequested();
                lock (_availabilitySync)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (_availability.Count >= MaximumAvailabilityEntries && !_availability.ContainsKey(candidate.SourceKey))
                        _availability.Remove(_availability.Keys.First());
                    _availability[candidate.SourceKey] = (source.AvailabilityFacts[candidate.SourceKey], observation);
                    observed.Add(candidate.SourceKey, observation);
                }
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var changed = false;
        var candidates = source.Candidates.Select(candidate =>
        {
            // Another canceled page read can finish with an older fact. Keep this
            // result tied to the observations made for this exact source snapshot.
            var observation = observed[candidate.SourceKey];
            var kind = ContentKind(candidate.Identity, candidate.HasLaunchTarget == true, observation.IsDirectory);
            if (observation.Availability == candidate.TargetAvailability && kind == candidate.TargetKind) return candidate;
            changed = true;
            return candidate with { TargetAvailability = observation.Availability, TargetKind = kind };
        }).ToArray();
        var result = source with { Candidates = changed ? candidates : source.Candidates, AvailabilityPrepared = true };
        if (changed)
            lock (_projectionSync)
            {
                if (ReferenceEquals(_startCandidates, source.Candidates)) _startCandidates = result.Candidates;
                if (ReferenceEquals(_packageCandidates, source.Candidates)) _packageCandidates = result.Candidates;
            }
        return result;
    }

    internal readonly record struct TargetObservation(ImportTargetAvailability Availability, bool? IsDirectory);

    internal static ImportTargetAvailability TargetAvailability(LaunchIdentity? identity) => ObserveTarget(identity).Availability;

    internal static TargetObservation ObserveTarget(LaunchIdentity? identity)
    {
        // One attributes read supplies availability and type; unknown is never guessed from a source folder/name.
        if (identity is null || identity.IsUrl || identity.Key.StartsWith("opaque", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(identity.Target)) return new(ImportTargetAvailability.Unknown, null);
        try
        {
            var attributes = File.GetAttributes(identity.Target);
            return new(ImportTargetAvailability.Available, (attributes & FileAttributes.Directory) != 0);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            try
            {
                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(identity.Target));
                if (parent is not null && (File.GetAttributes(parent) & FileAttributes.Directory) != 0)
                    return new(ImportTargetAvailability.Missing, null);
            }
            catch (Exception parentError) when (parentError is IOException or UnauthorizedAccessException or ArgumentException) { }
            return new(ImportTargetAvailability.Unknown, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(ImportTargetAvailability.Unknown, null); }
    }
}
