using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TileWall.Core.Entries;
using TileWall.Core.Configuration;
using TileWall.Core.Storage;
using TileWall.Core.Wall;

namespace TileWall.Core.Exchange;

/// <summary>Complete read-only validation. Platform shortcut parsing and image probes are injected.</summary>
public sealed class WallPackageReader(
    Func<ManagedEntryKind, ReadOnlyMemory<byte>, CancellationToken, Task<LaunchIdentity>> observeEntryAsync,
    Func<ReadOnlyMemory<byte>, string, CancellationToken, Task<bool>> verifyImageAsync)
{
    public async Task<WallPackageReadResult> ReadPreviewAsync(string packagePath, WallPackageLimits? limits = null,
        CancellationToken token = default)
    {
        limits ??= new(); limits.Validate();
        try
        {
            token.ThrowIfCancellationRequested();
            using var source = Open(packagePath);
            WallPackageZip.Preflight(source, limits, token);
            var fingerprint = Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
            source.Position = 0;
            using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var path = WallPackageZip.NormalizePath(entry.FullName);
                if (path != entry.FullName || !entries.TryAdd(path, entry) || entry.Length > limits.MaximumFileBytes ||
                    (total = checked(total + entry.Length)) > limits.MaximumExpandedBytes)
                    throw new InvalidDataException("Duplicate package path or excessive expansion.");
            }
            if (entries.Count > limits.MaximumEntries || !entries.TryGetValue("manifest.json", out var manifestEntry))
                throw new InvalidDataException("Missing package manifest.");
            var manifest = WallPackageJson.Deserialize<WallPackageManifest>(
                await WallPackageZip.ReadAsync(manifestEntry, limits.MaximumManifestBytes, token).ConfigureAwait(false));
            if (manifest.FormatId != WallPackageManifest.CurrentFormatId ||
                manifest.FormatVersion is not (2 or WallPackageManifest.CurrentFormatVersion) ||
                manifest.WallSchemaVersion != WallDocument.CurrentSchemaVersion)
                return new(WallPackageReadStatus.UnsupportedFormat, null, "This wall file requires a supported application version.");
            if (manifest.Files is null || !WallPackageJson.ValidHash(manifest.ContentFingerprint) ||
                string.IsNullOrWhiteSpace(manifest.AppVersion) || manifest.CreatedUtc.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Malformed manifest.");
            var files = new Dictionary<string, WallPackageFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                if (file is null || !WallPackageJson.ValidHash(file.Sha256) || file.Length < 0 ||
                    file.Length > limits.MaximumFileBytes || file.Purpose is not ("wall" or "entry" or "media" or "gallery"))
                    throw new InvalidDataException("Invalid file declaration.");
                var path = WallPackageZip.NormalizePath(file.Path);
                if (path != file.Path || path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                    !files.TryAdd(path, file) || !entries.TryGetValue(path, out var actual) || actual.FullName != path || actual.Length != file.Length)
                    throw new InvalidDataException("Manifest does not match ZIP files.");
            }
            if (files.Count + 1 != entries.Count || !files.TryGetValue("wall.json", out var wallFile) || wallFile.Purpose != "wall")
                throw new InvalidDataException("Missing or additional package files.");
            var wallBytes = await ReadDeclaredAsync(entries["wall.json"], wallFile, limits.MaximumContentBytes, token).ConfigureAwait(false);
            var content = WallPackageJson.Deserialize<WallPackageContent>(wallBytes, manifest.FormatVersion);
            WallPackageJson.ValidateContent(content, limits, token);
            content = WallPackageJson.Freeze(content);
            var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["wall.json"] = "wall" };
            foreach (var member in content.Members.Where(m => m.EntryKind is not null))
                expected.Add(WallPackageJson.EntryPath(member), "entry");
            var galleries = new Dictionary<string, ManagedGalleryManifest>(StringComparer.Ordinal);
            foreach (var gallery in content.Galleries)
            {
                var path = WallPackageJson.GalleryPath(gallery.Id);
                if (!files.TryGetValue(path, out var declared) || declared.Purpose != "gallery" || declared.Sha256 != gallery.ManifestSha256)
                    throw new InvalidDataException("Managed gallery manifest is missing or differs.");
                var bytes = await ReadDeclaredAsync(entries[path], declared, 1024 * 1024, token).ConfigureAwait(false);
                _ = WallPackageJson.Deserialize<ManagedGalleryManifest>(bytes);
                galleries.Add(gallery.Id, PreparedManagedGallery.ReadManifest(bytes, new(gallery.Id, gallery.ManifestSha256)));
                expected.Add(path, "gallery");
            }
            WallPackageJson.ValidateGalleryExpansion(content, galleries.Values, limits);
            var media = WallPackageJson.AllMedia(content, galleries.Values).GroupBy(WallPackageJson.MediaPath, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            foreach (var path in media.Keys) expected.Add(path, "media");
            if (files.Count != expected.Count || files.Any(pair => !expected.TryGetValue(pair.Key, out var purpose) || pair.Value.Purpose != purpose ||
                !expected.Keys.Contains(pair.Key, StringComparer.Ordinal)))
                throw new InvalidDataException("Content reference closure differs from manifest.");
            var identities = new Dictionary<string, LaunchIdentity>(StringComparer.Ordinal);
            foreach (var member in content.Members.Where(m => m.EntryKind is not null))
            {
                var path = WallPackageJson.EntryPath(member);
                var bytes = await ReadDeclaredAsync(entries[path], files[path], EntryObservation.MaximumBytes, token).ConfigureAwait(false);
                if (bytes.Length == 0) throw new InvalidDataException("Empty shortcut.");
                var identity = await ObserveEntryAsync(member.EntryKind!.Value, bytes, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (identity is null) throw new InvalidDataException("Shortcut identity is unavailable.");
                identities.Add(member.Id, identity);
            }
            foreach (var (path, references) in media)
            {
                var reference = references[0];
                if (files[path].Length != reference.Length || files[path].Sha256 != reference.Sha256)
                    throw new InvalidDataException("Media reference differs from its declaration.");
                var bytes = await ReadDeclaredAsync(entries[path], files[path], PreparedMediaAsset.MaximumBytes, token).ConfigureAwait(false);
                foreach (var extension in references.Select(item => item.Extension).Distinct(StringComparer.Ordinal))
                {
                    var valid = await verifyImageAsync(bytes, extension, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (!valid) throw new InvalidDataException("Original image is invalid or exceeds image budgets.");
                }
            }
            foreach (var group in content.Groups)
            {
                var keys = group.MemberIds.Where(identities.ContainsKey).Select(id => identities[id].Key).ToArray();
                if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
                    throw new InvalidDataException("Duplicate launch identities in one source group.");
            }
            if (WallPackageJson.Fingerprint(content, files.Values, manifest.FormatVersion) != manifest.ContentFingerprint)
                throw new InvalidDataException("Content snapshot fingerprint differs.");
            token.ThrowIfCancellationRequested();
            return new(WallPackageReadStatus.Valid, new WallPackagePreview(Path.GetFullPath(packagePath), content,
                manifest with { Files = Array.AsReadOnly(manifest.Files.ToArray()) }, fingerprint,
                new ReadOnlyDictionary<string, LaunchIdentity>(identities), new ReadOnlyDictionary<string, ManagedGalleryManifest>(galleries)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(WallPackageReadStatus.Cancelled, null); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or OverflowException or KeyNotFoundException)
        { return new(WallPackageReadStatus.InvalidPackage, null, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { return new(WallPackageReadStatus.IoFailure, null, ex.Message); }
    }

    public async Task<WallPackageReadResult> RevalidateAsync(WallPackagePreview preview, WallPackageLimits? limits = null,
        CancellationToken token = default)
    {
        var result = await ReadPreviewAsync(preview.PackagePath, limits, token).ConfigureAwait(false);
        return result.Status == WallPackageReadStatus.Valid && result.Preview!.PackageSha256 != preview.PackageSha256
            ? new(WallPackageReadStatus.InvalidPackage, null, "Wall file changed after preview.") : result;
    }

    public async Task<VerifiedPackageEntry> ReadEntryAsync(WallPackagePreview preview, string memberId,
        CancellationToken token = default)
    {
        using var import = await OpenImportAsync(preview, token).ConfigureAwait(false);
        return await import.ReadEntryAsync(memberId, token).ConfigureAwait(false);
    }

    /// <summary>One confirmation batch hashes and holds the package once. Dispose before returning from the accepted add task.</summary>
    public async Task<WallPackageImport> OpenImportAsync(WallPackagePreview preview, CancellationToken token = default)
    {
        var source = await OpenConfirmedAsync(preview, token).ConfigureAwait(false);
        try { return new(this, preview, source); }
        catch { source.Dispose(); throw; }
    }

    internal async Task<VerifiedPackageEntry> ReadEntryConfirmedAsync(WallPackagePreview preview, ZipArchive zip,
        string memberId, CancellationToken token)
    {
        var member = preview.Content.Members.Single(m => m.Id == memberId);
        if (member.EntryKind is not { } kind) throw new InvalidDataException("Member has no shortcut.");
        var path = WallPackageJson.EntryPath(member);
        var file = preview.Manifest.Files.Single(f => f.Path == path);
        var bytes = await ReadDeclaredAsync(zip.GetEntry(path)!, file, EntryObservation.MaximumBytes, token).ConfigureAwait(false);
        var observed = await ObserveEntryAsync(kind, bytes, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (observed.Key != preview.Identities[memberId].Key) throw new InvalidDataException("Shortcut identity changed.");
        return new(kind, bytes, observed);
    }

    public async Task<IReadOnlyList<PreparedMediaAsset>> PrepareMediaAsync(WallPackagePreview preview,
        IEnumerable<ManagedMediaReference> required, IEnumerable<ManagedMediaReference>? reusable = null,
        WallPackageLimits? limits = null, CancellationToken token = default)
    {
        limits ??= new(); limits.Validate();
        var references = WallPackageMediaBudget.Plan(required, reusable ?? [], limits.MaximumPreparedMediaBytes);
        // Budget the whole accepted batch before opening or probing a single original.
        using var import = await OpenImportAsync(preview, token).ConfigureAwait(false);
        return await import.PrepareMediaAsync(references, [], limits, token).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<PreparedMediaAsset>> PrepareMediaConfirmedAsync(WallPackagePreview preview,
        ZipArchive zip, IEnumerable<ManagedMediaReference> required, IEnumerable<ManagedMediaReference> reusable,
        WallPackageLimits limits, CancellationToken token)
    {
        var references = WallPackageMediaBudget.Plan(required, reusable, limits.MaximumPreparedMediaBytes);
        var result = new List<PreparedMediaAsset>();
        foreach (var reference in references)
        {
            var path = WallPackageJson.MediaPath(reference);
            var file = preview.Manifest.Files.SingleOrDefault(f => f.Path == path && f.Length == reference.Length && f.Sha256 == reference.Sha256)
                ?? throw new InvalidDataException("Accepted media is not in the verified package.");
            var bytes = await ReadDeclaredAsync(zip.GetEntry(path)!, file, PreparedMediaAsset.MaximumBytes, token).ConfigureAwait(false);
            var valid = await verifyImageAsync(bytes, reference.Extension, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!valid) throw new InvalidDataException("Invalid image.");
            result.Add(PreparedMediaAsset.FromVerifiedImage(bytes, reference.Extension));
        }
        return result.AsReadOnly();
    }

    internal async Task<WallPackagePreparedResources> PrepareResourcesConfirmedAsync(WallPackagePreview preview,
        ZipArchive zip, IEnumerable<ManagedMediaReference> requiredMedia, IEnumerable<ManagedGalleryReference> requiredGalleries,
        IEnumerable<ManagedMediaReference> reusableMedia, IEnumerable<ManagedGalleryReference> reusableGalleries,
        WallPackageLimits limits, CancellationToken token)
    {
        var singles = requiredMedia.Distinct().ToArray();
        var existingGalleries = reusableGalleries.ToHashSet();
        var galleryReferences = requiredGalleries.Distinct().ToArray();
        foreach (var gallery in galleryReferences)
            if (!PreparedManagedGallery.IsValidReference(gallery) || !preview.Galleries.ContainsKey(gallery.ContentId) ||
                !preview.Content.Galleries.Any(item => item.Id == gallery.ContentId && item.ManifestSha256 == gallery.ManifestSha256))
                throw new InvalidDataException("Accepted gallery is outside the verified package.");
        var newGalleries = galleryReferences.Where(gallery => !existingGalleries.Contains(gallery)).ToArray();
        var required = singles.Concat(newGalleries.SelectMany(gallery => preview.Galleries[gallery.ContentId].Files.Select(file => file.Media))).Distinct().ToArray();
        var known = WallPackageJson.AllMedia(preview.Content, preview.Galleries.Values).ToHashSet();
        if (required.Any(reference => !known.Contains(reference))) throw new InvalidDataException("Accepted original is outside the verified package.");
        var reusable = reusableMedia.ToArray();
        // Gallery and Single preparation share one digest budget. Do this before reading any original.
        var existing = reusable.Select(reference => reference.Sha256).ToHashSet(StringComparer.Ordinal);
        var all = required.Concat(reusable).ToArray();
        if (all.Any(reference => !PreparedMediaAsset.IsValidReference(reference)) || all.GroupBy(reference => reference.Sha256, StringComparer.Ordinal)
            .Any(group => group.Select(reference => reference.Length).Distinct().Count() != 1))
            throw new InvalidDataException("Invalid or conflicting original-image digest references.");
        var newBytes = required.DistinctBy(reference => reference.Sha256, StringComparer.Ordinal)
            .Where(reference => !existing.Contains(reference.Sha256)).Sum(reference => reference.Length);
        if (newBytes > limits.MaximumPreparedMediaBytes) throw new InvalidDataException("Selected original images exceed the combined preparation budget.");
        var prepared = new Dictionary<string, PreparedMediaAsset>(StringComparer.Ordinal);
        async Task<PreparedMediaAsset> Get(ManagedMediaReference reference)
        {
            token.ThrowIfCancellationRequested();
            if (prepared.TryGetValue(reference.Sha256, out var cached)) return cached.WithExtension(reference.Extension);
            var path = WallPackageJson.MediaPath(reference);
            var file = preview.Manifest.Files.SingleOrDefault(file => file.Path == path && file.Length == reference.Length && file.Sha256 == reference.Sha256)
                ?? throw new InvalidDataException("Accepted original is missing from the verified package.");
            var bytes = await ReadDeclaredAsync(zip.GetEntry(path)!, file, PreparedMediaAsset.MaximumBytes, token).ConfigureAwait(false);
            if (!await verifyImageAsync(bytes, reference.Extension, token).ConfigureAwait(false)) throw new InvalidDataException("Invalid original image.");
            token.ThrowIfCancellationRequested();
            var asset = PreparedMediaAsset.FromVerifiedImage(bytes, reference.Extension);
            prepared.Add(reference.Sha256, asset); return asset;
        }
        var media = new List<PreparedMediaAsset>();
        foreach (var reference in singles.Where(reference => !existing.Contains(reference.Sha256))) media.Add(await Get(reference).ConfigureAwait(false));
        var galleries = new List<PreparedManagedGallery>();
        foreach (var reference in newGalleries)
        {
            var files = new List<PreparedManagedGalleryFile>();
            foreach (var file in preview.Galleries[reference.ContentId].Files)
                files.Add(new(file.RelativePath, await Get(file.Media).ConfigureAwait(false)));
            var gallery = PreparedManagedGallery.FromVerifiedFiles(files);
            if (gallery.Reference != reference) throw new InvalidDataException("Prepared gallery differs from its verified content ID.");
            galleries.Add(gallery);
        }
        token.ThrowIfCancellationRequested();
        return new(media.AsReadOnly(), galleries.AsReadOnly());
    }

    internal async Task<PreparedMediaAsset> ReadGalleryImageConfirmedAsync(WallPackagePreview preview,
        ZipArchive zip, string galleryId, CancellationToken token)
    {
        if (!preview.Galleries.TryGetValue(galleryId, out var gallery)) throw new InvalidDataException("Unknown package gallery.");
        var reference = gallery.Files[0].Media;
        return (await PrepareMediaConfirmedAsync(preview, zip, [reference], [], new(), token).ConfigureAwait(false)).Single();
    }

    private async Task<LaunchIdentity> ObserveEntryAsync(ManagedEntryKind kind, byte[] bytes, CancellationToken token)
    {
        try { return await observeEntryAsync(kind, bytes, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or JsonException)
        { throw new InvalidDataException("Shortcut structure could not be read.", ex); }
    }

    private static Stream Open(string path) => new LocalFileStore().OpenReadProtected(path);

    private static async Task<Stream> OpenConfirmedAsync(WallPackagePreview preview, CancellationToken token)
    {
        var stream = Open(preview.PackagePath);
        try
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            if (hash != preview.PackageSha256) throw new InvalidDataException("Wall file changed after preview.");
            stream.Position = 0; return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static async Task<byte[]> ReadDeclaredAsync(ZipArchiveEntry entry, WallPackageFile declared, long maximum,
        CancellationToken token)
    {
        var bytes = await WallPackageZip.ReadAsync(entry, maximum, token).ConfigureAwait(false);
        if (bytes.LongLength != declared.Length || WallPackageJson.Hash(bytes) != declared.Sha256)
            throw new InvalidDataException("Package payload length or SHA-256 differs.");
        return bytes;
    }
}

/// <summary>A short confirmation scope; calls are sequential because ZIP shares one protected stream.</summary>
public sealed class WallPackageImport : IDisposable
{
    private readonly WallPackageReader _reader;
    private readonly WallPackagePreview _preview;
    private readonly Stream _source;
    private readonly ZipArchive _zip;
    private bool _disposed;
    internal WallPackageImport(WallPackageReader reader, WallPackagePreview preview, Stream source)
    {
        _reader = reader; _preview = preview; _source = source;
        _zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
    }
    public Task<VerifiedPackageEntry> ReadEntryAsync(string memberId, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _reader.ReadEntryConfirmedAsync(_preview, _zip, memberId, token);
    }
    public Task<IReadOnlyList<PreparedMediaAsset>> PrepareMediaAsync(IEnumerable<ManagedMediaReference> required,
        IEnumerable<ManagedMediaReference>? reusable = null, WallPackageLimits? limits = null,
        CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        limits ??= new(); limits.Validate();
        return _reader.PrepareMediaConfirmedAsync(_preview, _zip, required, reusable ?? [], limits, token);
    }
    public Task<WallPackagePreparedResources> PrepareResourcesAsync(IEnumerable<ManagedMediaReference> requiredMedia,
        IEnumerable<ManagedGalleryReference> requiredGalleries, IEnumerable<ManagedMediaReference>? reusableMedia = null,
        IEnumerable<ManagedGalleryReference>? reusableGalleries = null, WallPackageLimits? limits = null,
        CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        limits ??= new(); limits.Validate();
        return _reader.PrepareResourcesConfirmedAsync(_preview, _zip, requiredMedia, requiredGalleries,
            reusableMedia ?? [], reusableGalleries ?? [], limits, token);
    }
    public Task<PreparedMediaAsset> ReadGalleryImageAsync(string galleryId, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _reader.ReadGalleryImageConfirmedAsync(_preview, _zip, galleryId, token);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _zip.Dispose(); _source.Dispose();
    }
}

public sealed record WallPackagePreparedResources(IReadOnlyList<PreparedMediaAsset> MediaAssets,
    IReadOnlyList<PreparedManagedGallery> ManagedGalleries);

public sealed class WallPackagePreview
{
    internal WallPackagePreview(string path, WallPackageContent content, WallPackageManifest manifest, string hash,
        IReadOnlyDictionary<string, LaunchIdentity> identities, IReadOnlyDictionary<string, ManagedGalleryManifest>? galleries = null)
    { PackagePath = path; Content = content; Manifest = manifest; PackageSha256 = hash; Identities = identities;
        Galleries = galleries ?? new ReadOnlyDictionary<string, ManagedGalleryManifest>(new Dictionary<string, ManagedGalleryManifest>(StringComparer.Ordinal)); }
    public string PackagePath { get; }
    public WallPackageContent Content { get; }
    public WallPackageManifest Manifest { get; }
    public string PackageSha256 { get; }
    public IReadOnlyDictionary<string, LaunchIdentity> Identities { get; }
    public IReadOnlyDictionary<string, ManagedGalleryManifest> Galleries { get; }
}

public static class WallPackageMediaBudget
{
    public static IReadOnlyList<ManagedMediaReference> Plan(IEnumerable<ManagedMediaReference> required,
        IEnumerable<ManagedMediaReference> reusable, long maximumBytes = 128L * 1024 * 1024)
    {
        if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var all = new Dictionary<string, ManagedMediaReference>(StringComparer.Ordinal);
        var existing = new HashSet<string>(StringComparer.Ordinal);
        void Add(ManagedMediaReference media)
        {
            if (!PreparedMediaAsset.IsValidReference(media) || all.TryGetValue(media.Sha256, out var old) && old != media)
                throw new InvalidDataException("Invalid or conflicting media digest reference.");
            all[media.Sha256] = media;
        }
        foreach (var media in reusable) { Add(media); existing.Add(media.Sha256); }
        var selected = new Dictionary<string, ManagedMediaReference>(StringComparer.Ordinal);
        long total = 0;
        foreach (var media in required)
        {
            Add(media);
            if (existing.Contains(media.Sha256) || !selected.TryAdd(media.Sha256, media)) continue;
            total = checked(total + media.Length);
            if (total > maximumBytes) throw new InvalidDataException("Selected original images exceed the preparation budget.");
        }
        return Array.AsReadOnly(selected.Values.OrderBy(m => m.Sha256, StringComparer.Ordinal).ToArray());
    }
}
