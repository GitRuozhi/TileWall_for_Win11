namespace TileWall.Core.Exchange;

public sealed record WallPackageManifest
{
    public const string CurrentFormatId = "TileWall.WallPackage";
    public const int CurrentFormatVersion = 2;
    public string FormatId { get; init; } = CurrentFormatId;
    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public int WallSchemaVersion { get; init; } = 4;
    public required string AppVersion { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public required string ContentFingerprint { get; init; }
    public required IReadOnlyList<WallPackageFile> Files { get; init; }
}

public sealed record WallPackageFile(string Path, long Length, string Sha256, string Purpose);

public sealed record WallPackageLimits
{
    public int MaximumEntries { get; init; } = 10_000;
    public long MaximumExpandedBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    public long MaximumFileBytes { get; init; } = 1024L * 1024 * 1024;
    public int MaximumManifestBytes { get; init; } = 4 * 1024 * 1024;
    public int MaximumContentBytes { get; init; } = 32 * 1024 * 1024;
    public int MaximumMembers { get; init; } = 10_000;
    public int MaximumGroups { get; init; } = 10_000;
    public int MaximumReferences { get; init; } = 20_000;
    public long MaximumMetadataBytes { get; init; } = 16L * 1024 * 1024;
    public long MaximumPreparedMediaBytes { get; init; } = 128L * 1024 * 1024;
    internal void Validate()
    {
        if (MaximumEntries < 1 || MaximumExpandedBytes < 1 || MaximumFileBytes < 1 ||
            MaximumManifestBytes < 1 || MaximumContentBytes < 1 || MaximumPreparedMediaBytes < 0 ||
            MaximumMembers < 1 || MaximumGroups < 1 || MaximumReferences < 1 || MaximumMetadataBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(WallPackageLimits));
    }
}

public enum WallPackageReadStatus { Valid, UnsupportedFormat, InvalidPackage, IoFailure, Cancelled }
public sealed record WallPackageReadResult(WallPackageReadStatus Status, WallPackagePreview? Preview, string? Diagnostic = null);

public enum WallPackageWriteStatus { Saved, InvalidPackage, IoFailure, Cancelled, ResultUnknown, DestinationChanged }
public sealed record WallPackageWriteResult(WallPackageWriteStatus Status, string? ContentFingerprint = null, string? Diagnostic = null,
    string? PreservedPath = null);
