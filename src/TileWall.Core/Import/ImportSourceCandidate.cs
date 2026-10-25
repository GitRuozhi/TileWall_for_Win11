using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Wall;

namespace TileWall.Core.Import;

public enum ImportTargetAvailability { Unknown, Available, Missing }
public enum ImportInsertionPosition { Head, Tail }
public enum ImportFilterReason { None, AlreadyInGroup, MissingTarget, WindowsBuiltIn, Uninstall, Maintenance, DocumentOrWeb }
public enum ImportPlanStatus { NoChange, Ready, Rejected }

/// <summary>A real source group, including an unnamed group. Null SourceGroup alone means ungrouped.</summary>
public sealed record ImportSourceGroup(string Key, string? Title, int Order);

/// <summary>Read-only source facts. Geometry flags describe actual records, never placeholder values.</summary>
public sealed record ImportSourceCandidate(string SourceKey, string DisplayName, WallMember Member)
{
    public ImportSourceGroup? SourceGroup { get; init; }
    public int SourceOrder { get; init; }
    public LaunchIdentity? Identity { get; init; }
    public bool RequiresIdentity { get; init; }
    public bool HasRecordedPosition { get; init; }
    public bool HasRecordedSize { get; init; }
    public string? ErrorCode { get; init; }
    public ImportTargetAvailability TargetAvailability { get; init; }
    public StartMenuExclusion Exclusion { get; init; }
    public string RelativeDirectory { get; init; } = "";
    public string? Company { get; init; }
    public string SelectionIdentity => Identity?.Key ?? "object:" + SourceKey;
}

public sealed record ImportProjectedCandidate(ImportSourceCandidate Source, string GroupKey,
    IReadOnlyList<string> SourceKeys, string? ErrorCode, ImportFilterReason FilterReason,
    bool Visible, string? ReplacedMemberId)
{
    public string SelectionKey => Source.SourceKey;
    public bool CanAdd => ErrorCode is null && Visible;
}

public sealed record ImportProjectedGroup(string Key, string? Title, int Order,
    IReadOnlyList<ImportProjectedCandidate> Candidates, string? DestinationGroupId);

/// <summary>Grouping is fixed before filtering; counts and selection use these same representative rows.</summary>
public sealed record ImportSourceProjection(long Generation, IReadOnlyList<ImportProjectedGroup> Groups,
    IReadOnlyList<ImportProjectedCandidate> Candidates, bool AutoFilter, bool RecognizeGroups)
{
    public int FilteredCount => Candidates.Count(candidate => !candidate.Visible);
    public int InvalidCount => Candidates.Count(candidate => candidate.ErrorCode is not null);
    public IReadOnlyDictionary<ImportFilterReason, int> FilterCounts => Candidates
        .Where(candidate => !candidate.Visible).GroupBy(candidate => candidate.FilterReason)
        .ToDictionary(group => group.Key, group => group.Count());
}

public sealed record ImportAcceptedCandidate(string SourceKey, string MemberId, string GroupId, string? ReplacedMemberId);
public sealed record ImportSelectionMigration(IReadOnlyDictionary<string, string> Selected, int RemovedCount);

/// <summary>The proposal is the exact geometry shown by the selected-content preview and committed by the caller.</summary>
public sealed record ImportSourcePlan(ImportPlanStatus Status, WallProposal? Proposal,
    IReadOnlyList<ImportAcceptedCandidate> Accepted, WallRejection Rejection = WallRejection.None, string? Detail = null)
{
    public int AddedCount => Accepted.Count(candidate => candidate.ReplacedMemberId is null);
    public int OverwrittenCount => Accepted.Count(candidate => candidate.ReplacedMemberId is not null);
    public IReadOnlyList<ManagedMediaReference> ReferencedMedia => Proposal is null ? [] :
        Proposal.Document.Lanes.SelectMany(lane => lane.Groups).SelectMany(group => group.Members)
            .Where(member => Accepted.Any(item => item.MemberId == member.Id))
            .Select(member => member.Images.Media).OfType<ManagedMediaReference>()
            .DistinctBy(media => media.Sha256, StringComparer.OrdinalIgnoreCase).ToArray();
}
