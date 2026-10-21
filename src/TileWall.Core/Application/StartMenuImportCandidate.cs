using TileWall.Core.Entries;
using TileWall.Core.Import;

namespace TileWall.Core.Application;

internal sealed record StartMenuSelectionSource(string SourceKey, string IdentityKey, SourceKind Source);

internal sealed record StartMenuImportCandidate(StartMenuCandidate Candidate, StartMenuBucket Bucket,
    LaunchIdentity? Identity, StartMenuExclusion Exclusion, string? ErrorCode = null, bool Stale = false,
    string? Company = null, string? SourceSha256 = null, string? IconDirectory = null, string? IconKey = null)
{
    public string SelectionKey => Candidate.SourceKey;
    public bool CanAdd => Candidate.IsImportable && Identity is not null && ErrorCode is null &&
        StartMenuSourceCache.IsHash(SourceSha256);
    public IReadOnlyList<StartMenuSelectionSource> RepresentedSources { get; init; } = [];
    public IEnumerable<StartMenuSelectionSource> SelectionSources => RepresentedSources.Count != 0 ? RepresentedSources :
        Identity is { } identity ? [new(SelectionKey, identity.Key, Candidate.Source)] : [];
}
