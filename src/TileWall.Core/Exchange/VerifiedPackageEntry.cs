using TileWall.Core.Entries;
using TileWall.Core.Wall;

namespace TileWall.Core.Exchange;

/// <summary>Only Reader creates these, after validation of the whole package.</summary>
public sealed class VerifiedPackageEntry
{
    private readonly byte[] _bytes;
    internal VerifiedPackageEntry(ManagedEntryKind kind, byte[] bytes, LaunchIdentity identity)
    {
        Kind = kind; _bytes = bytes.ToArray(); Identity = identity;
        Sha256 = WallPackageJson.Hash(_bytes);
    }
    public ManagedEntryKind Kind { get; }
    public string Sha256 { get; }
    public LaunchIdentity Identity { get; }
    public int Length => _bytes.Length;
    internal byte[] CopyBytes() => _bytes.ToArray();
}
