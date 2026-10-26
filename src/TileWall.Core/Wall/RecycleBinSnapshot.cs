namespace TileWall.Core.Wall;

/// <summary>A request-local recycle-bin observation; its identity belongs to the observed committed generation.</summary>
public sealed record RecycleBinSnapshot(long Generation, string ManifestSha256, IReadOnlyList<WallMember> Members);
