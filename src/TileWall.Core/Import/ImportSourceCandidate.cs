using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Wall;

namespace TileWall.Core.Import;

public enum ImportTargetAvailability { Unknown, Available, Missing }
public enum ImportInsertionPosition { Head, Tail }
public enum ImportPageSourceKind { StartMenu, Desktop, InstalledApplications, SystemFeatures, WallPackage, CurrentWall, RecycleBin }
public enum ImportSourcePolicy { Shortcut, DirectTarget, SystemFeature, Attributed, Application }
public enum ImportTargetKind { Unknown, SystemFeature, Program, File, Folder, WebLink, ProtocolLink, Component }
public enum ImportFilterReason { None, DuplicateTarget, MissingTarget, NonPrimaryEntry }
public enum ImportPlanStatus { NoChange, Ready, Rejected }

/// <summary>A real source group, including an unnamed group. Null SourceGroup alone means ungrouped.</summary>
public sealed record ImportSourceGroup(string Key, string? Title, int Order)
{
    public int LaneIndex { get; init; }
}

/// <summary>Read-only source facts. Geometry flags describe actual records, never placeholder values.</summary>
public sealed record ImportSourceCandidate(string SourceKey, string DisplayName, WallMember Member)
{
    public ImportPageSourceKind SourceKind { get; init; }
    public ImportSourcePolicy Policy { get; init; }
    public ImportTargetKind TargetKind { get; init; }
    /// <summary>False is an explicit no-target fact; null means not determined, never an inferred component.</summary>
    public bool? HasLaunchTarget { get; init; }
    public int SourcePriority { get; init; }
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
    IReadOnlyList<ImportProjectedCandidate> Candidates, string? DestinationGroupId)
{
    public bool PreserveSourceGroup { get; init; }
    public string? SourceGroupId { get; init; }
    public int SourceLaneIndex { get; init; }
}

public sealed record ImportSourceStatistics(int AcquiredCount, int FilteredCount, int CandidateCount,
    IReadOnlyDictionary<ImportTargetKind, int> ContentCounts,
    IReadOnlyDictionary<ImportFilterReason, int> PrimaryFilterCounts);

public sealed record ImportSourcePreview(IReadOnlyList<WallLane> Lanes, WallGeometry Geometry,
    IReadOnlyDictionary<string, ImportProjectedCandidate> CandidatesByMemberId, string? ErrorCode = null);

/// <summary>Filters precede grouping; attributed sources retain their original groups and geometry.</summary>
public sealed record ImportSourceProjection(long Generation, IReadOnlyList<ImportProjectedGroup> Groups,
    IReadOnlyList<ImportProjectedCandidate> Candidates, bool AutoFilter, bool RecognizeGroups)
{
    public ImportSourceStatistics Statistics { get; init; } = new(0, 0, 0,
        new Dictionary<ImportTargetKind, int>(), new Dictionary<ImportFilterReason, int>());
    public int FilteredCount => Statistics.FilteredCount;
    public int InvalidCount => Candidates.Count(candidate => candidate.ErrorCode is not null);
    public IReadOnlyDictionary<ImportFilterReason, int> FilterCounts => Statistics.PrimaryFilterCounts;
    public int CountSelected(IEnumerable<string> selectedSourceKeys)
    {
        var selected = selectedSourceKeys.ToHashSet(StringComparer.Ordinal);
        return Candidates.Count(candidate => candidate.CanAdd && candidate.SourceKeys.Any(selected.Contains));
    }
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
