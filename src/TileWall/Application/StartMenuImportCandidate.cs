using TileWall.Core.Entries;
using TileWall.Core.Import;

namespace TileWall.ApplicationModel;

internal sealed record StartMenuImportCandidate(StartMenuCandidate Candidate, StartMenuBucket Bucket,
    LaunchIdentity? Identity, StartMenuExclusion Exclusion, bool IsDuplicate, bool AlreadyPresent, bool Replacing,
    string? Error, bool ReadFailed = false, string? Company = null)
{
    public string SelectionKey => Identity?.Key ?? Candidate.FullPath;
    public bool CanAdd => Candidate.IsImportable && Identity is not null && !IsDuplicate && !AlreadyPresent && Error is null;
}
