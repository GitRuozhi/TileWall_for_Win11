using System.Collections.ObjectModel;
using TileWall.Core.Entries;
using TileWall.Core.Wall;
using TileWall.Core.Storage;

namespace TileWall.Core.Exchange;

/// <summary>A committed selection with copied entry bytes and protected immutable original-image handles.</summary>
public sealed class WallPackageSnapshot : IDisposable
{
    private readonly IReadOnlyDictionary<string, Stream> _media;
    private bool _disposed;
    private IDisposable? _resourceLease;

    internal WallPackageSnapshot(string dataRoot, long generation, WallPackageContent content,
        IDictionary<string, WallPackageSnapshotEntry> entries, IDictionary<string, Stream> media,
        IEnumerable<string> protectedSourcePaths, IDictionary<string, WallPackageSnapshotGallery>? galleries = null)
    {
        DataRoot = dataRoot; Generation = generation; Content = WallPackageJson.Freeze(content);
        Entries = new ReadOnlyDictionary<string, WallPackageSnapshotEntry>(entries);
        _media = new ReadOnlyDictionary<string, Stream>(media);
        ProtectedSourcePaths = protectedSourcePaths.ToArray();
        Galleries = new ReadOnlyDictionary<string, WallPackageSnapshotGallery>(galleries ?? new Dictionary<string, WallPackageSnapshotGallery>(StringComparer.Ordinal));
    }

    public WallPackageContent Content { get; }
    public long Generation { get; }
    internal string DataRoot { get; }
    internal IReadOnlyList<string> ProtectedSourcePaths { get; }
    internal IReadOnlyDictionary<string, WallPackageSnapshotEntry> Entries { get; }
    internal IReadOnlyDictionary<string, WallPackageSnapshotGallery> Galleries { get; }
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
