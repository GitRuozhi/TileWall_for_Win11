using TileWall.Core.Wall;

namespace TileWall.Core.Storage;

public sealed partial class WallStore
{
    public string ManagedMediaPath(ManagedMediaReference media)
    {
        var path = _paths.Media(media);
        _paths.Guard(path);
        return path;
    }

    private static IEnumerable<ManagedMediaReference> MediaReferences(WallDocument document) =>
        document.EnumerateAllMembers().Select(member => member.Images.Media)
            .Append(document.Settings.BackgroundMedia).OfType<ManagedMediaReference>();

    private static Dictionary<string, ManagedMediaReference> MediaById(WallDocument document)
    {
        var result = new Dictionary<string, ManagedMediaReference>(StringComparer.Ordinal);
        foreach (var media in MediaReferences(document))
        {
            if (!PreparedMediaAsset.IsValidReference(media)) throw new InvalidDataException("Invalid formal media reference.");
            if (result.TryGetValue(media.Sha256, out var existing) && existing != media)
                throw new InvalidDataException("Conflicting references for one media digest.");
            result[media.Sha256] = media;
        }
        return result;
    }

    private static IEnumerable<string> ValidateMediaReferences(WallDocument document)
    {
        foreach (var member in document.EnumerateAllMembers())
        {
            if (member.Images.Kind == MemberImageSourceKind.None &&
                (member.Images.Path is not null || member.Images.ManagedGallery is not null || member.Carousel is not null))
                yield return "NONE_IMAGE_SOURCE";
            if (member.ResourceOrigin == MemberResourceOrigin.Package &&
                !(member.Images.Kind == MemberImageSourceKind.None ||
                  member.Images.Kind == MemberImageSourceKind.Single && member.Images.Media is not null ||
                  member.Images.Kind == MemberImageSourceKind.Folder))
                yield return "PACKAGE_IMAGE_SOURCE";
            if (member.Images.Kind == MemberImageSourceKind.Single && member.Images.Media is null)
                yield return "SINGLE_MEDIA_REQUIRED";
            if (member.Images.Media is not null && (member.Images.Kind != MemberImageSourceKind.Single ||
                !PreparedMediaAsset.IsValidReference(member.Images.Media))) yield return "MEDIA_REFERENCE";
        }
        if (document.Settings.BackgroundMode == WallBackgroundMode.Custom && document.Settings.BackgroundMedia is null)
            yield return "BACKGROUND_MEDIA_REQUIRED";
        if (document.Settings.BackgroundMedia is { } background && !PreparedMediaAsset.IsValidReference(background))
            yield return "BACKGROUND_MEDIA_REFERENCE";
        var grouped = MediaReferences(document).GroupBy(media => media.Sha256, StringComparer.Ordinal);
        if (grouped.Any(group => group.Distinct().Count() != 1)) yield return "MEDIA_ID_CONFLICT";
    }

    private void CheckMediaReferences(WallDocument document, bool contents = true)
    {
        foreach (var media in MediaById(document).Values)
        {
            var path = _paths.Media(media);
            if (contents ? !Verify(path, media.Sha256, media.Length) : !_files.GetPathInfo(path).Exists)
                throw new IOException($"Referenced original media is missing or changed: {media.Sha256}");
        }
    }

    private List<WallJournalMedia> PlanMedia(string commitId, WallDocument current, WallDocument proposed,
        IReadOnlyList<PreparedMediaAsset> prepared)
    {
        var before = MediaById(current);
        var after = MediaById(proposed);
        var supplied = new Dictionary<string, PreparedMediaAsset>(StringComparer.Ordinal);
        foreach (var asset in prepared)
        {
            if (asset is null || !PreparedMediaAsset.IsValidReference(asset.Reference) ||
                !after.TryGetValue(asset.Reference.Sha256, out var referenced) || referenced != asset.Reference)
                throw new InvalidDataException("Prepared media is not referenced by the proposed wall.");
            if (supplied.TryGetValue(asset.Reference.Sha256, out var prior) && prior.Reference != asset.Reference)
                throw new InvalidDataException("Conflicting prepared media.");
            supplied[asset.Reference.Sha256] = asset;
        }
        var all = new Dictionary<string, ManagedMediaReference>(before, StringComparer.Ordinal);
        foreach (var media in after.Values)
        {
            if (all.TryGetValue(media.Sha256, out var existing) && existing != media)
                throw new InvalidDataException("Existing immutable media cannot change its extension or length.");
            all[media.Sha256] = media;
        }
        var result = new List<WallJournalMedia>();
        foreach (var media in all.Values.OrderBy(media => media.Sha256, StringComparer.Ordinal))
        {
            var path = _paths.Media(media);
            if (_files.Exists(path))
            {
                if (!Verify(path, media.Sha256, media.Length))
                {
                    if (before.ContainsKey(media.Sha256)) throw new CurrentMediaInvalidException(media.Sha256);
                    throw new InvalidDataException("Existing media bytes do not match their digest.");
                }
                result.Add(new(media, false, null));
            }
            else
            {
                if (before.ContainsKey(media.Sha256)) throw new CurrentMediaInvalidException(media.Sha256);
                if (!supplied.TryGetValue(media.Sha256, out var asset) || asset.Reference != media)
                    throw new InvalidDataException($"Original media has not been prepared: {media.Sha256}");
                result.Add(new(media, true, MediaStageRelativePath(commitId, media.Sha256)));
            }
        }
        return result;
    }

    private void StageMedia(WallJournal journal, IReadOnlyList<PreparedMediaAsset> prepared)
    {
        foreach (var operation in journal.Media.Where(media => media.NewlyInstalled))
        {
            var asset = prepared.First(item => item.Reference == operation.Asset);
            var staged = MediaStagePath(journal.CommitId, operation.Asset.Sha256);
            _files.WriteAllBytes(staged, asset.CopyBytes());
            if (!Verify(staged, operation.Asset.Sha256, operation.Asset.Length))
                throw new IOException("Staged original media differs from prepared bytes.");
        }
    }

    private void InstallMedia(WallJournal journal, WallCommitTimings? timings = null)
    {
        using var mediaInstallTiming = timings?.Measure("store.media.install");
        foreach (var operation in journal.Media.Where(media => media.NewlyInstalled))
        {
            var target = _paths.Media(operation.Asset);
            _files.CreateDirectory(Path.GetDirectoryName(target)!);
            // Content-addressed originals are immutable. A late destination is a conflict,
            // never an invitation to overwrite an asset shared by another snapshot.
            _files.Move(MediaStagePath(journal.CommitId, operation.Asset.Sha256), target, false);
        }
        VerifyJournalMedia(journal);
    }

    private void VerifyJournalMedia(WallJournal journal, HashSet<ManagedMediaReference>? verifiedInAudit = null)
    {
        foreach (var operation in journal.Media)
        {
            if (verifiedInAudit is not null && !verifiedInAudit.Add(operation.Asset)) continue;
            if (!Verify(_paths.Media(operation.Asset), operation.Asset.Sha256, operation.Asset.Length))
                throw new IOException($"Journal media is missing or changed: {operation.Asset.Sha256}");
        }
    }

    private static void VerifyMediaCoverage(WallJournal journal, WallDocument snapshot)
    {
        var covered = journal.Media.ToDictionary(media => media.Asset.Sha256, media => media.Asset, StringComparer.Ordinal);
        foreach (var media in MediaById(snapshot).Values)
            if (!covered.TryGetValue(media.Sha256, out var actual) || actual != media)
                throw new IOException("Original media journal coverage is incomplete.");
    }

    private void RollbackMedia(WallJournal journal)
    {
        var installed = journal.Media.Where(media => media.NewlyInstalled).ToArray();
        if (installed.Length == 0) return;
        var proposedPath = _paths.Under("Staging", journal.CommitId, "state.tmp");
        if (!_files.Exists(proposedPath))
            proposedPath = _paths.Under("Recovery", "Entries", journal.CommitId, "new.snapshot.json");
        if (!_files.Exists(proposedPath) || Hash(_files.ReadAllBytes(proposedPath)) != journal.NewHash ||
            !TryReadDocument(proposedPath, out var proposed, out _))
            throw new IOException("New media ownership cannot be verified for rollback.");
        var expected = MediaById(proposed!);
        var protectedIds = ProtectedMediaIds(journal.CommitId);
        foreach (var operation in installed)
        {
            if (!expected.TryGetValue(operation.Asset.Sha256, out var reference) || reference != operation.Asset)
                throw new IOException("New media is not owned by the transaction snapshot.");
            // An unreadable retained snapshot makes deletion unsafe. Leaving a known orphan
            // is preferable to deleting an asset that a current or previous document still owns.
            if (protectedIds is null || protectedIds.Contains(reference.Sha256)) continue;
            // A stage that still exists was never moved into this destination. In particular,
            // do not delete a late file that caused the immutable Move(overwrite:false) to fail.
            if (_files.Exists(MediaStagePath(journal.CommitId, reference.Sha256))) continue;
            var path = _paths.Media(reference);
            if (!_files.Exists(path)) continue;
            if (!Verify(path, reference.Sha256, reference.Length))
                throw new IOException("New media changed before rollback.");
            _files.Delete(path);
            var directory = Path.GetDirectoryName(path)!;
            if (_files.ListFiles(directory).Count == 0 && _files.ListDirectories(directory).Count == 0)
                _files.DeleteDirectory(directory);
        }
    }

    private HashSet<string>? ProtectedMediaIds(string excludedCommitId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var snapshots = new List<string> { _paths.Config, _paths.Previous };
        foreach (var directory in _files.ListDirectories(_paths.RecoveryEntries))
        {
            if (Path.GetFileName(directory) == excludedCommitId) continue;
            snapshots.Add(Path.Combine(directory, "old.snapshot.json"));
            snapshots.Add(Path.Combine(directory, "new.snapshot.json"));
        }
        foreach (var path in snapshots)
        {
            if (!_files.Exists(path)) continue;
            if (!TryReadDocument(path, out var document, out _)) return null;
            result.UnionWith(MediaById(document!).Keys);
        }
        return result;
    }

    private string MediaStagePath(string commitId, string sha256) =>
        _paths.Under("Staging", commitId, "media-" + sha256 + ".tmp");

    private static string MediaStageRelativePath(string commitId, string sha256) =>
        $"Staging/{commitId}/media-{sha256}.tmp";

    private sealed record WallJournalMedia(ManagedMediaReference Asset, bool NewlyInstalled, string? StagedPath);

    private sealed class CurrentMediaInvalidException(string id)
        : IOException($"Current original media is missing or changed: {id}") { }
}
