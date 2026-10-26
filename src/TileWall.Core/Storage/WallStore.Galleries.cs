using TileWall.Core.Wall;

namespace TileWall.Core.Storage;

public sealed partial class WallStore
{
    public string ManagedGalleryPath(ManagedGalleryReference gallery)
    {
        var path = _paths.Gallery(gallery); _paths.Guard(path); return path;
    }

    private static IEnumerable<ManagedGalleryReference> GalleryReferences(WallDocument document) =>
        document.EnumerateAllMembers().Select(member => member.Images.ManagedGallery).OfType<ManagedGalleryReference>().Distinct();

    private static IEnumerable<string> ValidateGalleryReferences(WallDocument document)
    {
        foreach (var member in document.EnumerateAllMembers())
            if (member.Images.ManagedGallery is { } gallery &&
                (member.Images.Kind != MemberImageSourceKind.Folder || member.Images.Media is not null || !PreparedManagedGallery.IsValidReference(gallery)))
                yield return "GALLERY_REFERENCE";
    }

    private ManagedGalleryManifest CheckGallery(ManagedGalleryReference gallery, string? directory = null,
        ICollection<Stream>? protectedHandles = null)
    {
        directory ??= _paths.Gallery(gallery);
        _paths.GuardTree(directory);
        byte[] Read(string path)
        {
            if (protectedHandles is null) return _files.ReadAllBytes(path);
            var stream = _files.OpenReadProtected(path); protectedHandles.Add(stream);
            if (stream.Length > PreparedMediaAsset.MaximumBytes) throw new InvalidDataException("Oversized managed gallery file.");
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); stream.Position = 0; return bytes;
        }
        var manifest = PreparedManagedGallery.ReadManifest(Read(Path.Combine(directory, "manifest.json")), gallery);
        var expected = manifest.Files.Select(file => file.RelativePath).Append("manifest.json").Order(StringComparer.Ordinal).ToArray();
        if (_files.ListDirectories(directory).Count != 0 || !_files.ListFiles(directory).Select(Path.GetFileName)
            .Order(StringComparer.Ordinal).SequenceEqual(expected)) throw new InvalidDataException("Managed gallery has missing or extra files.");
        foreach (var file in manifest.Files)
        {
            var bytes = Read(Path.Combine(directory, file.RelativePath));
            if (bytes.LongLength != file.Media.Length || Hash(bytes) != file.Media.Sha256)
                throw new IOException("Managed gallery original differs from its immutable digest.");
        }
        return manifest;
    }

    private void CheckGalleryReferences(WallDocument document)
    {
        foreach (var gallery in GalleryReferences(document)) _ = CheckGallery(gallery);
    }

    private List<WallJournalGallery> PlanGalleries(string commitId, WallDocument current, WallDocument proposed,
        IReadOnlyList<PreparedManagedGallery> prepared)
    {
        var before = GalleryReferences(current).ToHashSet();
        var after = GalleryReferences(proposed).ToHashSet();
        if (prepared.Any(gallery => gallery is null || !after.Contains(gallery.Reference)))
            throw new InvalidDataException("Prepared gallery is outside the proposed reference closure.");
        var supplied = prepared.GroupBy(gallery => gallery.Reference).ToDictionary(group => group.Key, group => group.First());
        var result = new List<WallJournalGallery>();
        foreach (var gallery in before.Union(after).OrderBy(gallery => gallery.ContentId, StringComparer.Ordinal))
        {
            if (_files.GetPathInfo(_paths.Gallery(gallery)).Exists)
            {
                _ = CheckGallery(gallery); result.Add(new(gallery, false));
            }
            else
            {
                if (before.Contains(gallery)) throw new CurrentMediaInvalidException(gallery.ContentId);
                if (!supplied.ContainsKey(gallery)) throw new InvalidDataException("Managed gallery has not been prepared.");
                result.Add(new(gallery, true));
            }
        }
        return result;
    }

    private void StageGalleries(WallJournal journal, IReadOnlyList<PreparedManagedGallery> prepared)
    {
        foreach (var operation in journal.Galleries.Where(item => item.NewlyInstalled))
        {
            var gallery = prepared.First(item => item.Reference == operation.Reference);
            var directory = GalleryStagePath(journal.CommitId, operation.Reference.ContentId);
            _files.CreateDirectory(directory);
            foreach (var file in gallery.Files) _files.WriteAllBytes(Path.Combine(directory, file.RelativePath), file.Asset.CopyBytes());
            _files.WriteAllBytes(Path.Combine(directory, "manifest.json"), gallery.ManifestBytes.ToArray());
            _ = CheckGallery(gallery.Reference, directory);
        }
    }

    private void InstallGalleries(WallJournal journal)
    {
        foreach (var operation in journal.Galleries.Where(item => item.NewlyInstalled))
        {
            var staged = GalleryStagePath(journal.CommitId, operation.Reference.ContentId);
            _ = CheckGallery(operation.Reference, staged);
            _files.CreateDirectory(_paths.Sources);
            _files.MoveDirectory(staged, _paths.Gallery(operation.Reference));
        }
        VerifyJournalGalleries(journal);
    }

    private void VerifyJournalGalleries(WallJournal journal)
    {
        foreach (var operation in journal.Galleries) _ = CheckGallery(operation.Reference);
    }

    private static void VerifyGalleryCoverage(WallJournal journal, WallDocument document)
    {
        var covered = journal.Galleries.Select(item => item.Reference).ToHashSet();
        if (GalleryReferences(document).Any(reference => !covered.Contains(reference)))
            throw new InvalidDataException("Gallery journal coverage is incomplete.");
    }

    private void RollbackGalleries(WallJournal journal)
    {
        foreach (var operation in journal.Galleries.Where(item => item.NewlyInstalled))
        {
            // An existing staged directory means a failed non-overwriting publish owns no destination.
            if (_files.GetPathInfo(GalleryStagePath(journal.CommitId, operation.Reference.ContentId)).Exists) continue;
            var directory = _paths.Gallery(operation.Reference);
            if (!_files.GetPathInfo(directory).Exists) continue;
            using var protection = CaptureProtectedResourceSet(journal.CommitId);
            if (protection.Sources.Contains(operation.Reference.ContentId)) continue;
            _ = CheckGallery(operation.Reference);
            _files.DeleteDirectory(directory);
        }
    }

    private void ValidatePreparedResourceBudget(WallDocument current, IReadOnlyList<PreparedMediaAsset> media,
        IReadOnlyList<PreparedManagedGallery> galleries)
    {
        var existing = MediaById(current).Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var gallery in GalleryReferences(current))
            existing.UnionWith(CheckGallery(gallery).Files.Select(file => file.Media.Sha256));
        var supplied = media.Concat(galleries.SelectMany(gallery => gallery.Files.Select(file => file.Asset)))
            .GroupBy(asset => asset.Reference.Sha256, StringComparer.Ordinal).Select(group => group.First())
            .Where(asset => !existing.Contains(asset.Reference.Sha256));
        if (supplied.Sum(asset => asset.Reference.Length) > PreparedManagedGallery.MaximumTotalBytes)
            throw new InvalidDataException("Combined prepared original bytes exceed 128 MiB.");
    }

    private string GalleryStagePath(string commitId, string contentId) => _paths.Under("Staging", commitId, "source-" + contentId);
    private sealed record WallJournalGallery(ManagedGalleryReference Reference, bool NewlyInstalled);
}
