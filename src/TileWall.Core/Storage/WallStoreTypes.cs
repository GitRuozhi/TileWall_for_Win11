using TileWall.Core.Wall;
using TileWall.Core.Exchange;

namespace TileWall.Core.Storage;

public enum WallOpenStatus
{
    Fresh, Loaded, RecoveredPrevious,
    CorruptBlocked, UnsupportedFormatBlocked, RecoveryBlocked, OpenFailed
}

public sealed record WallOpenResult(WallOpenStatus Status, WallDocument? Document,
    IReadOnlyList<string> Diagnostics)
{
    // A fact about this Open call, never persisted or inferred from an empty wall.
    public bool InitializedFresh { get; init; }
}

public abstract record EntryChange(string MemberId)
{
    public sealed record Keep(string MemberId) : EntryChange(MemberId);
    public sealed record CopyShortcut(string MemberId, string SourcePath, string? ExpectedSourceSha256 = null) : EntryChange(MemberId);
    public sealed record FromPackage(string MemberId, VerifiedPackageEntry Source) : EntryChange(MemberId);
    public sealed record CreateLink(string MemberId, string TargetPath) : EntryChange(MemberId);
    public sealed record CreateUrl(string MemberId, string Url) : EntryChange(MemberId);
    public sealed record ChangeTarget(string MemberId, string TargetPathOrUrl) : EntryChange(MemberId);
    public sealed record Rename(string MemberId) : EntryChange(MemberId);
    public sealed record Remove(string MemberId) : EntryChange(MemberId);
}

public sealed record WallCommitRequest(long ExpectedGeneration, WallDocument Proposed,
    IReadOnlyList<EntryChange> EntryChanges, IReadOnlyList<PreparedMediaAsset>? MediaAssets = null,
    IReadOnlyDictionary<string, string>? ExpectedEntrySha256 = null);

public enum WallCommitStatus { Rejected, Committed, CommittedNeedsCleanup, RecoveryBlocked }

public sealed record WallCommitResult(WallCommitStatus Status, WallDocument? CommittedDocument,
    IReadOnlyList<string> Diagnostics);

public sealed record WallPlanResult(WallCommitRequest? Request, IReadOnlyList<string> Diagnostics);
