using TileWall.Core.Application;
using TileWall.Core.Entries;
using TileWall.Core.Exchange;
using TileWall.Core.Grid;
using TileWall.Core.Import;
using TileWall.Core.Wall;
using TileWall.Shell.Interop;

namespace TileWall.ApplicationModel;

/// <summary>Source adapters retain facts; grouping, filtering and placement belong to the common planner.</summary>
internal static class StartMenuImportProjection
{
    internal static ImportPageSource FromStartMenu(StartMenuCatalogSnapshot snapshot,
        Func<StartMenuCandidate, string> iconsDirectory, CancellationToken token)
    {
        var sources = snapshot.Sources.OrderBy(source => source.Candidate.Source)
            .ThenBy(source => source.Candidate.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
        var candidates = new List<ImportSourceCandidate>();
        var originals = new Dictionary<string, StartMenuImportCandidate>(StringComparer.Ordinal);
        foreach (var (source, index) in sources.Select((source, index) => (source, index)))
        {
            token.ThrowIfCancellationRequested();
            var candidate = source.Candidate;
            var error = source.ErrorCode ?? (!candidate.IsImportable ? "InvalidTarget" :
                !StartMenuSourceCache.IsHash(source.SourceSha256) ? "SourceChanged" :
                source.Identity?.IsUrl == true && !RegisteredUrlProtocols.IsSupported(source.Identity.Target) ? "UnsupportedProtocol" : null);
            var exclusion = source.Identity is null ? StartMenuExclusion.None : StartMenuSelectionRules.Classify(candidate.DisplayName, source.Identity);
            originals[candidate.SourceKey] = new(candidate, new("pending", "", 0), source.Identity, exclusion,
                error, source.Stale, source.Company, source.SourceSha256, iconsDirectory(candidate), source.IconKey);
            var id = "start-source-" + candidate.SourceKey;
            var kind = candidate.Kind == EntryKind.Lnk ? ManagedEntryKind.Lnk : ManagedEntryKind.Url;
            var folder = candidate.RelativeDir.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            candidates.Add(new(candidate.SourceKey, candidate.DisplayName, new TileMember
            {
                Id = id, LocalBounds = new GridRect(0, 0, 1, 1),
                Entry = new(id, $"Objects/{id}/entry.{(kind == ManagedEntryKind.Lnk ? "lnk" : "url")}", kind),
                TargetInput = candidate.FullPath, Visual = new MemberVisual { Title = candidate.DisplayName },
                ImportSource = new() { BatchId = "start-menu", GroupBucketKey = folder is null ? "ungrouped" : "folder:" + folder.ToUpperInvariant(), OriginGroupId = id,
                    SourceRoot = candidate.RootPath, RelativePath = candidate.RelativePath },
            })
            {
                SourceGroup = string.IsNullOrWhiteSpace(folder) ? null : new("folder:" + folder.ToUpperInvariant(), folder, 100),
                SourceOrder = index, Identity = source.Identity, RequiresIdentity = true,
                HasRecordedPosition = false, HasRecordedSize = false, ErrorCode = error,
                TargetAvailability = TargetAvailability(source.Identity), Exclusion = exclusion,
                RelativeDirectory = candidate.RelativeDir, Company = source.Company,
            });
        }
        return new(candidates.ToArray(), originals, Scanned: snapshot.ScannedCount, Skipped: snapshot.SkippedDirectories,
            ReadErrors: candidates.Count(candidate => candidate.ErrorCode is not null), Refreshing: snapshot.Refreshing, ErrorCode: snapshot.ErrorCode);
    }

    internal static ImportPageSource FromPackage(WallPackagePreview preview, CancellationToken token)
    {
        var members = preview.Content.Members.ToDictionary(member => member.Id, StringComparer.Ordinal);
        var groups = preview.Content.Groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var candidates = new List<ImportSourceCandidate>();
        foreach (var (group, order) in preview.Content.Lanes.SelectMany(lane => lane.GroupIds).Select(id => groups[id]).Select((group, order) => (group, order)))
        foreach (var (id, index) in group.MemberIds.Select((id, index) => (id, index)))
        {
            token.ThrowIfCancellationRequested();
            var member = members[id].ToMember();
            var identity = preview.Identities.GetValueOrDefault(id);
            var error = identity?.IsUrl == true && !identity.Key.StartsWith("opaque-url:", StringComparison.Ordinal) &&
                !RegisteredUrlProtocols.IsSupported(identity.Target) ? "UnsupportedProtocol" : null;
            candidates.Add(new(id, member.Visual.Title ?? id, member)
            {
                SourceGroup = new(group.Id, group.Title, order), SourceOrder = index, Identity = identity,
                RequiresIdentity = member.Entry is not null, HasRecordedPosition = true, HasRecordedSize = true,
                ErrorCode = error, TargetAvailability = TargetAvailability(identity),
            });
        }
        return new(candidates.ToArray(), new Dictionary<string, StartMenuImportCandidate>(), preview, candidates.Count);
    }

    internal static ImportTargetAvailability TargetAvailability(LaunchIdentity? identity)
    {
        // Exists conflates missing, denied and offline locations. A missing item
        // is confirmed only when its parent can actually be read. Never launch it.
        if (identity is null || identity.IsUrl || identity.Key.StartsWith("opaque:", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(identity.Target)) return ImportTargetAvailability.Unknown;
        try { _ = File.GetAttributes(identity.Target); return ImportTargetAvailability.Available; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            try
            {
                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(identity.Target));
                if (parent is not null && (File.GetAttributes(parent) & FileAttributes.Directory) != 0) return ImportTargetAvailability.Missing;
            }
            catch (Exception parentError) when (parentError is IOException or UnauthorizedAccessException or ArgumentException) { }
            return ImportTargetAvailability.Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return ImportTargetAvailability.Unknown; }
    }
}
