using System.Collections.ObjectModel;
using TileWall.Core.Entries;
using TileWall.Core.Wall;
using TileWall.Core.Storage;

namespace TileWall.Core.Exchange;

/// <summary>A committed selection with copied entry bytes and protected immutable original-image handles.</summary>
public sealed class WallPackageSnapshot : IDisposable
{
    private readonly Dictionary<string, Stream> _media;
    private bool _disposed;
    private IDisposable? _resourceLease;

    internal WallPackageSnapshot(string dataRoot, long generation, WallPackageContent content,
        IDictionary<string, WallPackageSnapshotEntry> entries, IDictionary<string, Stream> media,
        IEnumerable<string> protectedSourcePaths, IDictionary<string, WallPackageSnapshotGallery>? galleries = null,
        IReadOnlyList<WallMember>? automaticImageMembers = null, IEnumerable<string>? lexicalProtectedSourcePaths = null)
    {
        DataRoot = dataRoot; Generation = generation; Content = WallPackageJson.Freeze(content);
        Entries = new ReadOnlyDictionary<string, WallPackageSnapshotEntry>(entries);
        _media = new Dictionary<string, Stream>(media, StringComparer.Ordinal);
        ProtectedSourcePaths = protectedSourcePaths.ToArray();
        LexicalProtectedSourcePaths = (lexicalProtectedSourcePaths ?? []).ToArray();
        AutomaticImageMembers = Array.AsReadOnly((automaticImageMembers ?? content.Members
            .Where(member => member.Images.Kind == MemberImageSourceKind.Automatic).Select(member => member.ToMember()).ToArray()).ToArray());
        AutomaticImagesCompleted = AutomaticImageMembers.Count == 0;
        Galleries = new ReadOnlyDictionary<string, WallPackageSnapshotGallery>(galleries ?? new Dictionary<string, WallPackageSnapshotGallery>(StringComparer.Ordinal));
    }

    public WallPackageContent Content { get; private set; }
    public long Generation { get; }
    public IReadOnlyList<WallMember> AutomaticImageMembers { get; }
    internal bool AutomaticImagesCompleted { get; private set; }
    internal string DataRoot { get; }
    internal IReadOnlyList<string> ProtectedSourcePaths { get; }
    internal IReadOnlyList<string> LexicalProtectedSourcePaths { get; }
    internal IReadOnlyDictionary<string, WallPackageSnapshotEntry> Entries { get; }
    internal IReadOnlyDictionary<string, WallPackageSnapshotGallery> Galleries { get; }
    public string? EntrySha256(string memberId)
    {
        EnsureAvailable();
        return Entries.GetValueOrDefault(memberId)?.Sha256;
    }

    /// <summary>Complete only the detached export projection. A null payload records a genuine absent foreground.</summary>
    public void CompleteAutomaticImages(IReadOnlyDictionary<string, PreparedMediaAsset?>? images = null,
        CancellationToken token = default)
    {
        EnsureAvailable(); token.ThrowIfCancellationRequested();
        if (AutomaticImagesCompleted && AutomaticImageMembers.Count != 0)
            throw new InvalidOperationException("Automatic export images have already been completed.");
        var ids = AutomaticImageMembers.Select(member => member.Id).ToHashSet(StringComparer.Ordinal);
        if (images is not null && !ids.SetEquals(images.Keys)) throw new InvalidDataException("Automatic image selection changed.");
        var additions = new Dictionary<string, MemoryStream>(StringComparer.Ordinal);
        try
        {
            var content = Content with { Members = Content.Members.Select(member =>
            {
                token.ThrowIfCancellationRequested();
                if (member.Images.Kind != MemberImageSourceKind.Automatic) return member;
                var asset = images?.GetValueOrDefault(member.Id);
                if (asset is not null)
                {
                    if (asset.Reference.Extension != ".png") throw new InvalidDataException("An exported automatic foreground must be PNG.");
                    if (_media.TryGetValue(asset.Reference.Sha256, out var existing) && existing.Length != asset.Reference.Length)
                        throw new InvalidDataException("Conflicting automatic image digest.");
                    if (!_media.ContainsKey(asset.Reference.Sha256) && !additions.ContainsKey(asset.Reference.Sha256))
                        additions.Add(asset.Reference.Sha256, new MemoryStream(asset.CopyBytes(), writable: false));
                }
                return member with { Images = asset is null ? new() { Kind = MemberImageSourceKind.None }
                    : new() { Kind = MemberImageSourceKind.Single, Media = asset.Reference } };
            }).ToArray() };
            WallPackageJson.ValidateContent(content);
            var limits = new WallPackageLimits();
            if (additions.Values.Sum(stream => stream.Length) > limits.MaximumPreparedMediaBytes ||
                _media.Values.Sum(stream => stream.Length) + additions.Values.Sum(stream => stream.Length) > limits.MaximumExpandedBytes)
                throw new InvalidDataException("Automatic foregrounds exceed the export preparation budget.");
            token.ThrowIfCancellationRequested();
            foreach (var (key, stream) in additions) _media.Add(key, stream);
            Content = WallPackageJson.Freeze(content); AutomaticImagesCompleted = true;
            additions.Clear();
        }
        finally { foreach (var stream in additions.Values) stream.Dispose(); }
    }
    internal void AttachResourceLease(IDisposable lease)
    {
        EnsureAvailable();
        if (_resourceLease is not null) throw new InvalidOperationException("Resource lease already attached.");
        _resourceLease = lease;
    }

    internal Stream Media(ManagedMediaReference reference)
    {
        EnsureAvailable();
        var stream = _media[reference.Sha256]; stream.Position = 0; return stream;
    }

    internal void EnsureAvailable() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { foreach (var stream in _media.Values) stream.Dispose(); }
        finally { _resourceLease?.Dispose(); _resourceLease = null; }
    }
}

internal sealed class WallPackageSnapshotGallery
{
    private readonly byte[] _manifestBytes;
    internal WallPackageSnapshotGallery(ManagedGalleryReference reference, ReadOnlyMemory<byte> manifestBytes)
    {
        _manifestBytes = manifestBytes.ToArray(); Reference = reference;
        Manifest = PreparedManagedGallery.ReadManifest(_manifestBytes, reference);
    }
    internal ManagedGalleryReference Reference { get; }
    internal ManagedGalleryManifest Manifest { get; }
    internal ReadOnlyMemory<byte> ManifestBytes => _manifestBytes.ToArray();
}

// Formal export observations are distinct from Reader-issued VerifiedPackageEntry.
internal sealed class WallPackageSnapshotEntry
{
    private readonly byte[] _bytes;
    internal WallPackageSnapshotEntry(byte[] bytes, LaunchIdentity identity)
    {
        _bytes = bytes.ToArray(); Identity = identity; Sha256 = WallPackageJson.Hash(_bytes);
    }
    internal LaunchIdentity Identity { get; }
    internal string Sha256 { get; }
    internal int Length => _bytes.Length;
    internal ReadOnlyMemory<byte> Bytes => _bytes;
}
