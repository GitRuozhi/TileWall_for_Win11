using System.Collections.Frozen;
using TileWall.Core.Wall;
using TileWall.Core.Exchange;
using TileWall.Core.Entries;

namespace TileWall.Core.Storage;

public enum WallOpenStatus
{
    Fresh, Loaded, RecoveredPrevious,
    CorruptBlocked, UnsupportedFormatBlocked, RecoveryBlocked, OpenFailed
}

public enum WallOpenBlockReason
{
    None, InitializationInterrupted, UnattributedFiles, InvalidSnapshot,
    UnsupportedFormat, RecoveryMaterials, ReferencedAssets, UnsafePath, StorageAccess
}

public sealed record WallOpenResult(WallOpenStatus Status, WallDocument? Document,
    IReadOnlyList<string> Diagnostics)
{
    public WallOpenBlockReason BlockReason { get; init; }
    // A fact about this Open call, never persisted or inferred from an empty wall.
    public bool InitializedFresh { get; init; }
    public bool WasUpgraded { get; init; }
    public string? ManifestSha256 { get; init; }
    internal FrozenDictionary<string, string> LaunchCommands { get; init; } = FrozenDictionary<string, string>.Empty;
}

public abstract record EntryChange(string MemberId)
{
    public sealed record Keep(string MemberId) : EntryChange(MemberId);
    /// <summary>An argument override edits the staged LNK copy after checking the source fingerprint.</summary>
    public sealed record CopyShortcut(string MemberId, string SourcePath, string? ExpectedSourceSha256 = null,
        string? Arguments = null) : EntryChange(MemberId);
    public sealed record FromPackage(string MemberId, VerifiedPackageEntry Source) : EntryChange(MemberId);
    public sealed record CreateLink(string MemberId, string TargetPath, string? Arguments = null) : EntryChange(MemberId);
    public sealed record CreateShellLink(string MemberId, string ParsingName, string ExpectedIdentityKey) : EntryChange(MemberId)
    {
        public NativeShellTargetSnapshot? NativeTarget { get; init; }
    }
    public sealed record CreateUrl(string MemberId, string Url) : EntryChange(MemberId);
    /// <summary>Null arguments keep the current LNK arguments. An empty string clears them.</summary>
    public sealed record ChangeTarget(string MemberId, string TargetPathOrUrl, string? Arguments = null) : EntryChange(MemberId);
    public sealed record Rename(string MemberId) : EntryChange(MemberId);
    public sealed record Remove(string MemberId) : EntryChange(MemberId);
}

public sealed record WallCommitRequest(long ExpectedGeneration, WallDocument Proposed,
    IReadOnlyList<EntryChange> EntryChanges, IReadOnlyList<PreparedMediaAsset>? MediaAssets = null,
    IReadOnlyDictionary<string, string>? ExpectedEntrySha256 = null,
    IReadOnlyList<PreparedManagedGallery>? ManagedGalleries = null)
{
    public bool PreserveStoredRecycleBin { get; init; }
    public RecycleBinSnapshot? RecycleBin { get; init; }
    public bool ClearRecycleBin { get; init; }
    public string? ExpectedManifestSha256 { get; init; }
}

public enum WallCommitStatus { Rejected, Committed, CommittedNeedsCleanup, RecoveryBlocked }

public sealed record WallCommitResult(WallCommitStatus Status, WallDocument? CommittedDocument,
    IReadOnlyList<string> Diagnostics)
{
    public string? ManifestSha256 { get; init; }
    internal FrozenDictionary<string, string> LaunchCommands { get; init; } = FrozenDictionary<string, string>.Empty;
}

public sealed record WallPlanResult(WallCommitRequest? Request, IReadOnlyList<string> Diagnostics);
