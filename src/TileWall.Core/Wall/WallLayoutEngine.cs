using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Components;

namespace TileWall.Core.Wall;

public abstract record WallAction(long ExpectedGeneration);
public sealed record AddMemberAction(long ExpectedGeneration, WallMember Member, string? TargetGroupId,
    int LaneIndex = 0, int GroupInsertIndex = 0, string? NewGroupId = null) : WallAction(ExpectedGeneration);
public sealed record ReplaceMemberAction(long ExpectedGeneration, string MemberId,
    WallMember Replacement) : WallAction(ExpectedGeneration);
public sealed record AddBatchItem(WallMember Member, string? TargetGroupId,
    int LaneIndex = 0, int GroupInsertIndex = 0, string? NewGroupId = null);
public sealed record AddBatchAction(long ExpectedGeneration,
    IReadOnlyList<AddBatchItem> Items) : WallAction(ExpectedGeneration);
/// <summary>GroupInsertIndex is interpreted after removal of a now-empty source group.</summary>
public sealed record MoveMemberAction(long ExpectedGeneration, string MemberId, GridRect TargetBounds,
    string? TargetGroupId, int LaneIndex = 0, int GroupInsertIndex = 0, string? NewGroupId = null) : WallAction(ExpectedGeneration);
/// <summary>GroupInsertIndex is interpreted after removal of the moving group.</summary>
public sealed record MoveGroupAction(long ExpectedGeneration, string GroupId, int LaneIndex, int GroupInsertIndex) : WallAction(ExpectedGeneration);
public sealed record RemoveMemberAction(long ExpectedGeneration, string MemberId) : WallAction(ExpectedGeneration);
public sealed record UnpinMemberAction(long ExpectedGeneration, string MemberId) : WallAction(ExpectedGeneration);
public sealed record RenameGroupAction(long ExpectedGeneration, string GroupId, string? Title) : WallAction(ExpectedGeneration);
public sealed record ResizeWallAction(long ExpectedGeneration, int Columns, int Rows) : WallAction(ExpectedGeneration);

public enum WallRejection
{
    None,
    StaleGeneration,
    InvalidDocument,
    InvalidAction,
    ObjectNotFound,
    DuplicateId,
    NoSpace,
    WorkAreaTooSmall,
}

/// <summary>A complete document and geometry proposal. The caller persists Document before adopting it.</summary>
public sealed record WallProposal(WallDocument Document, WallGeometry Geometry);
public sealed record WallLayoutResult(WallProposal? Proposal, WallRejection Rejection, string? Detail = null)
{
    public bool Success => Proposal is not null;
}

/// <summary>Pure, deterministic layout decisions. No UI state or disk operations are performed here.</summary>
public static class WallLayoutEngine
{
    private const int CellsPerLane = 8;
    private const int MaxColumns = 64;
    private const int MaxRows = 256;

    /// <summary>Normalizes the same candidate that callers preview and commit; does not advance its generation.</summary>
    public static WallLayoutResult NormalizeExpanded(WallDocument candidate, WallMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        metrics ??= new();
        var errors = Validate(candidate);
        if (!metrics.IsValid) return Reject(WallRejection.InvalidAction, "Invalid metrics.");
        if (errors.Count != 0) return Reject(WallRejection.InvalidDocument, string.Join("; ", errors));
        if (candidate.Settings.ExpandColumns)
        {
            var occupied = candidate.Lanes.Where(lane => lane.Groups.Count > 0).ToArray();
            if (occupied.Length >= MaxColumns) return Reject(WallRejection.NoSpace, "COLUMN_LIMIT");
            if (candidate.Lanes.Count != occupied.Length + 1 || candidate.Lanes[^1].Groups.Count != 0)
                candidate = candidate with
                {
                    Columns = occupied.Length + 1,
                    Lanes = [.. occupied, new WallLane { Groups = [] }],
                };
        }
        errors = ValidateGeometry(candidate, metrics);
        return errors.Count == 0 ? new(new(candidate, MeasureValidated(candidate, metrics)), WallRejection.None)
            : Reject(WallRejection.InvalidDocument, string.Join("; ", errors));
    }

    public static WallLayoutResult TryApply(WallDocument current, WallAction action,
        double workWidthDip, double workHeightDip, WallMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(action);
        metrics ??= new WallMetrics();
        if (!metrics.IsValid)
        {
            return Reject(WallRejection.InvalidAction, "Invalid metrics.");
        }

        var errors = ValidateGeometry(current, metrics);
        if (errors.Count > 0)
        {
            return Reject(WallRejection.InvalidDocument, string.Join("; ", errors));
        }

        if (action.ExpectedGeneration != current.Generation)
        {
            return Reject(WallRejection.StaleGeneration, "Proposal was based on an older generation.");
        }

        var lanes = current.Lanes.Select(l => l.Groups.ToList()).ToList();
        var inactive = current.InactiveMembers.ToList();
        var laneSources = new Dictionary<List<StartGroup>, WallLane>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < lanes.Count; index++) laneSources.Add(lanes[index], current.Lanes[index]);
        WallRejection rejection;
        switch (action)
        {
            case AddMemberAction add:
                rejection = PlaceMember(lanes, add.Member, add.TargetGroupId, add.LaneIndex,
                    add.GroupInsertIndex, add.NewGroupId, current.Rows, metrics);
                break;
            case ReplaceMemberAction replace:
                rejection = ReplaceMember(lanes, replace, current.Rows, metrics);
                break;
            case AddBatchAction batch:
                rejection = AddBatch(lanes, batch, current.Rows, metrics);
                break;
            case MoveMemberAction move:
                rejection = MoveMember(lanes, move, current.Rows, metrics);
                break;
            case MoveGroupAction moveGroup:
                rejection = MoveGroup(lanes, moveGroup);
                break;
            case RemoveMemberAction remove:
                rejection = RemoveMember(lanes, remove.MemberId);
                break;
            case UnpinMemberAction unpin:
                var retained = FindMember(lanes, unpin.MemberId);
                rejection = retained is null ? WallRejection.ObjectNotFound : RemoveMember(lanes, unpin.MemberId);
                if (rejection == WallRejection.None) inactive.Add(retained!.Value.Member);
                break;
            case RenameGroupAction rename:
                rejection = RenameGroup(lanes, rename.GroupId, rename.Title);
                break;
            case ResizeWallAction resize:
                rejection = ResizeLanes(lanes, resize.Columns, resize.Rows, current.Rows, metrics);
                break;
            default:
                rejection = WallRejection.InvalidAction;
                break;
        }

        if (rejection != WallRejection.None)
        {
            return Reject(rejection);
        }

        var requestedRows = action is ResizeWallAction wallResize ? wallResize.Rows : current.Rows;
        var proposed = current with
        {
            Generation = current.Generation + 1,
            Columns = lanes.Count,
            Rows = requestedRows,
            Lanes = lanes.Select(groups => laneSources.TryGetValue(groups, out var source)
                ? source with { Groups = groups.ToArray() }
                : new WallLane { Groups = groups.ToArray() }).ToArray(),
            InactiveMembers = inactive.ToArray(),
        };
        errors = Validate(proposed);
        if (errors.Count > 0)
        {
            return Reject(WallRejection.InvalidAction, string.Join("; ", errors));
        }

        var requiredHeight = RequiredHeight(proposed, metrics);
        if (action is not ResizeWallAction)
        {
            var requiredRows = RowsForHeight(requiredHeight, metrics);
            // A temporary stack may exceed the structural height before empty rows or
            // whole groups are compacted. Never reject that intermediate arrangement.
            proposed = proposed with { Rows = Math.Max(proposed.Rows, Math.Min(MaxRows, requiredRows)) };
        }

        var capacity = FitCapacity(ref proposed, requiredHeight, action is not ResizeWallAction,
            workWidthDip, workHeightDip, metrics, out var capacityDetail);
        if (capacity != WallRejection.None) return Reject(capacity, capacityDetail);

        if (proposed.Settings.ExpandColumns)
        {
            var normalized = NormalizeExpanded(proposed, metrics);
            if (normalized.Proposal is null) return normalized;
            proposed = normalized.Proposal.Document;
        }

        errors = ValidateGeometry(proposed, metrics);
        if (errors.Count > 0)
        {
            return Reject(WallRejection.InvalidAction, string.Join("; ", errors));
        }

        return new WallLayoutResult(new WallProposal(proposed, MeasureValidated(proposed, metrics)), WallRejection.None);
    }

    /// <summary>Geometry-only batch probe; final TryApply still owns validation, placement and the proposal.</summary>
    internal static WallRejection ProbeCapacity(WallDocument candidate, double workWidthDip,
        double workHeightDip, WallMetrics metrics)
    {
        var requiredHeight = RequiredHeight(candidate, metrics);
        var requiredRows = RowsForHeight(requiredHeight, metrics);
        candidate = candidate with { Rows = Math.Max(candidate.Rows, Math.Min(MaxRows, requiredRows)) };
        return FitCapacity(ref candidate, requiredHeight, true, workWidthDip, workHeightDip, metrics, out _);
    }

    private static WallRejection FitCapacity(ref WallDocument proposed, double requiredHeight, bool allowCompression,
        double workWidthDip, double workHeightDip, WallMetrics metrics, out string? detail)
    {
        detail = null;
        var expanded = proposed.Settings.ExpandColumns;
        var occupiedColumns = proposed.Lanes.Count(lane => lane.Groups.Count > 0);
        if (expanded && occupiedColumns >= MaxColumns)
        {
            detail = "COLUMN_LIMIT";
            return WallRejection.NoSpace;
        }
        var projectedColumns = expanded ? occupiedColumns + 1 : proposed.Columns;
        if (allowCompression &&
            (metrics.WallWidth(projectedColumns) > workWidthDip + 1e-8 ||
             metrics.WallHeight(proposed.Rows) > workHeightDip + 1e-8 ||
             requiredHeight > metrics.WallHeight(proposed.Rows) + 1e-8))
        {
            var maxColumns = Math.Clamp((int)Math.Floor((workWidthDip - 2 * metrics.Margin + metrics.ColumnGap) /
                (metrics.LaneWidth + metrics.ColumnGap) + 1e-8), 0, MaxColumns);
            var maxRows = Math.Clamp((int)Math.Floor((workHeightDip - metrics.TopMargin - metrics.Margin + metrics.Gap) /
                metrics.Pitch + 1e-8), 0, MaxRows);
            var contentColumns = maxColumns - (expanded ? 1 : 0);
            if (contentColumns > 0 && maxRows > 0)
            {
                var fitted = proposed.Lanes.Select(lane => lane.Groups.ToList()).ToList();
                var sources = new Dictionary<List<StartGroup>, WallLane>(ReferenceEqualityComparer.Instance);
                for (var index = 0; index < fitted.Count; index++) sources.Add(fitted[index], proposed.Lanes[index]);
                var fitRows = Math.Min(proposed.Rows, maxRows);
                if (ResizeLanes(fitted, Math.Min(proposed.Columns, contentColumns), fitRows, proposed.Rows, metrics) == WallRejection.None)
                {
                    proposed = proposed with
                    {
                        Columns = fitted.Count, Rows = fitRows,
                        Lanes = fitted.Select(groups => sources.TryGetValue(groups, out var source)
                            ? source with { Groups = groups.ToArray() }
                            : new WallLane { Groups = groups.ToArray() }).ToArray(),
                    };
                    requiredHeight = RequiredHeight(proposed, metrics);
                }
            }
        }
        if (requiredHeight > metrics.WallHeight(proposed.Rows) + 1e-8)
            return WallRejection.NoSpace;

        projectedColumns = expanded ? proposed.Lanes.Count(lane => lane.Groups.Count > 0) + 1 : proposed.Columns;
        if (metrics.WallWidth(projectedColumns) <= workWidthDip + 1e-8 &&
            metrics.WallHeight(proposed.Rows) <= workHeightDip + 1e-8)
            return WallRejection.None;

        foreach (var group in proposed.Lanes.SelectMany(lane => lane.Groups))
            if (group.Members.Count > 0 && metrics.TopMargin + metrics.Margin + metrics.TitleHeight +
                metrics.TitleToMembersGap + metrics.HeightOf(MinimumGroupRows(group)) > workHeightDip + 1e-8)
            {
                detail = "GROUP_TOO_TALL:" + (string.IsNullOrWhiteSpace(group.Title) ? group.Id : group.Title);
                return WallRejection.NoSpace;
            }
        detail = "CONTENT_TOO_LARGE";
        return WallRejection.WorkAreaTooSmall;
    }

    public static IReadOnlyList<string> Validate(WallDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();
        if (document.SchemaVersion != WallDocument.CurrentSchemaVersion) errors.Add("SCHEMA_VERSION");
        if (document.Generation < 0) errors.Add("GENERATION");
        if (document.Generation == long.MaxValue) errors.Add("GENERATION_OVERFLOW");
        if (document.Columns is < 1 or > MaxColumns || document.Rows is < 1 or > MaxRows || document.Lanes is null ||
            document.Lanes.Count != document.Columns) return [.. errors, "WALL_STRUCTURE"];
        if (document.Settings is null) errors.Add("SETTINGS_NULL");
        else if (!Enum.IsDefined(document.Settings.AnchorCorner) || !Enum.IsDefined(document.Settings.LanguageMode)
            || !Enum.IsDefined(document.Settings.VisualStyle) ||
            !Enum.IsDefined(document.Settings.ThemeMode) || !Enum.IsDefined(document.Settings.BackgroundMode))
            errors.Add("SETTINGS_ENUM");
        else
        {
            if (document.Settings.WindowedColumns is < 1 or > MaxColumns ||
                document.Settings.WindowedRows is < 1 or > MaxRows)
                errors.Add("SETTINGS_WINDOWED_SIZE");
            if (document.Settings.WindowedViewportWidthDip is { } viewportWidth &&
                (!double.IsFinite(viewportWidth) || viewportWidth <= 0) ||
                document.Settings.ManualWindowedViewportWidthDip is { } manualWidth &&
                (!double.IsFinite(manualWidth) || manualWidth <= 0))
                errors.Add("SETTINGS_WINDOWED_VIEWPORT_WIDTH");
            if (document.Settings.BackgroundTransparency is < 0 or > 100)
                errors.Add("SETTINGS_TRANSPARENCY");
            if (document.Settings.BackgroundMode == WallBackgroundMode.Custom && document.Settings.BackgroundMedia is null &&
                string.IsNullOrWhiteSpace(document.Settings.BackgroundImagePath))
                errors.Add("SETTINGS_BACKGROUND_PATH");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lane in document.Lanes)
        {
            if (lane?.Groups is null) { errors.Add("LANE_NULL"); continue; }
            foreach (var group in lane.Groups)
            {
                if (group is null) { errors.Add("GROUP_NULL"); continue; }
                CheckId(group.Id, ids, errors);
                if (!string.IsNullOrWhiteSpace(group.Title) && !names.Add(group.Title.Trim())) errors.Add("DUPLICATE_GROUP_NAME");
                if (MemberPresentationRules.ValidateImportSource(group.ImportSource) is { } groupImportError)
                    errors.Add(groupImportError);
                if (group.Members is null || group.Members.Count == 0)
                {
                    errors.Add("EMPTY_GROUP");
                    continue;
                }

                var members = group.Members;
                for (var i = 0; i < members.Count; i++)
                {
                    var member = members[i];
                    if (member is null) { errors.Add("MEMBER_NULL"); continue; }
                    CheckId(member.Id, ids, errors);
                    var b = member.LocalBounds;
                    ValidateMember(member, errors);

                    for (var j = 0; j < i; j++)
                        if (members[j] is not null && IntersectsSafe(b, members[j].LocalBounds)) errors.Add("MEMBER_OVERLAP");
                }
            }
        }

        if (document.InactiveMembers is null) errors.Add("INACTIVE_NULL");
        else foreach (var member in document.InactiveMembers)
        {
            if (member is null) { errors.Add("MEMBER_NULL"); continue; }
            CheckId(member.Id, ids, errors);
            ValidateMember(member, errors);
        }
        return errors;
    }

    /// <summary>Checks persisted geometry against saved Rows, independent of the current monitor.</summary>
    public static IReadOnlyList<string> ValidateGeometry(WallDocument document, WallMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        metrics ??= new WallMetrics();
        var errors = Validate(document).ToList();
        if (!metrics.IsValid)
        {
            errors.Add("METRICS");
        }
        if (errors.Count > 0)
        {
            return errors;
        }

        if (RequiredHeight(document, metrics) > metrics.WallHeight(document.Rows))
        {
            errors.Add("WALL_GEOMETRY");
        }
        return errors;
    }

    public static WallGeometry Measure(WallDocument document, WallMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        metrics ??= new WallMetrics();
        if (!metrics.IsValid) throw new ArgumentException("Invalid wall metrics.", nameof(metrics));
        var errors = ValidateGeometry(document, metrics);
        if (errors.Count > 0) throw new ArgumentException($"Invalid wall document: {string.Join("; ", errors)}", nameof(document));
        return MeasureValidated(document, metrics);
    }

    // Only called after this exact immutable proposal has passed ValidateGeometry.
    private static WallGeometry MeasureValidated(WallDocument document, WallMetrics metrics)
    {
        var positions = new List<GroupPlacement>();
        for (var laneIndex = 0; laneIndex < document.Lanes.Count; laneIndex++)
        {
            var x = metrics.Margin + laneIndex * (metrics.LaneWidth + metrics.ColumnGap);
            var y = metrics.TopMargin;
            foreach (var group in document.Lanes[laneIndex].Groups)
            {
                var title = new DipRect(x, y, metrics.LaneWidth, metrics.TitleHeight);
                var membersY = y + metrics.TitleHeight + metrics.TitleToMembersGap;
                var occupiedRows = group.Members.Max(m => m.LocalBounds.Bottom);
                var height = metrics.TitleHeight + metrics.TitleToMembersGap + metrics.HeightOf(occupiedRows);
                var members = group.Members.Select(m => new MemberPlacement(m.Id,
                    metrics.MemberRect(m.LocalBounds, x, membersY))).ToArray();
                positions.Add(new GroupPlacement(group.Id, laneIndex, title,
                    new DipRect(x, y, metrics.LaneWidth, height), members));
                y += height + metrics.GroupGap;
            }
        }

        return new WallGeometry(metrics.WallWidth(document.Columns), metrics.WallHeight(document.Rows), positions);
    }

    private static double RequiredHeight(WallDocument document, WallMetrics metrics)
    {
        return document.Lanes.Max(lane => RequiredLaneHeight(lane.Groups, metrics));
    }

    private static int RowsForHeight(double requiredHeight, WallMetrics metrics)
    {
        var contentHeight = requiredHeight - metrics.TopMargin - metrics.Margin;
        if (contentHeight <= metrics.HeightOf(1)) return 1;
        return (int)Math.Ceiling((contentHeight + metrics.Gap) / metrics.Pitch - 1e-10);
    }

    private static double RequiredLaneHeight(IReadOnlyList<StartGroup> groups, WallMetrics metrics)
    {
        var height = metrics.TopMargin + metrics.Margin;
        foreach (var group in groups)
            height += metrics.TitleHeight + metrics.TitleToMembersGap +
                metrics.HeightOf(group.Members.Max(m => m.LocalBounds.Bottom)) + metrics.GroupGap;
        return groups.Count > 0 ? height - metrics.GroupGap : height;
    }

    private static WallRejection ReplaceMember(List<List<StartGroup>> lanes, ReplaceMemberAction action,
        int currentRows, WallMetrics metrics)
    {
        var location = FindMember(lanes, action.MemberId);
        if (location is null) return WallRejection.ObjectNotFound;
        var (laneIndex, groupIndex, original) = location.Value;
        if (action.Replacement is null ||
            !string.Equals(original.Id, action.Replacement.Id, StringComparison.Ordinal) ||
            original.GetType() != action.Replacement.GetType())
            return WallRejection.InvalidAction;

        var source = lanes[laneIndex][groupIndex];
        var userPropertyChanged = original is PuzzleMember && action.Replacement is PuzzleMember replacementPuzzle &&
            replacementPuzzle.Partitions is null
            ? true
            : MemberPresentationRules.HasUserPropertyChanges(original, action.Replacement);
        var replacement = action.Replacement with
        {
            ImportSource = original.ImportSource,
            UserClaimed = original.UserClaimed ||
                (original.ImportSource is not null && userPropertyChanged),
        };
        lanes[laneIndex][groupIndex] = source with
        {
            Members = source.Members.Where(member =>
                !string.Equals(member.Id, original.Id, StringComparison.Ordinal)).ToArray(),
        };
        var rejection = PlaceMember(lanes, replacement, source.Id, laneIndex, groupIndex,
            null, currentRows, metrics);
        if (rejection != WallRejection.None) return rejection;

        // A property edit does not change the member's relative ordering in its group.
        var placed = lanes[laneIndex][groupIndex];
        var byId = placed.Members.ToDictionary(member => member.Id, StringComparer.OrdinalIgnoreCase);
        lanes[laneIndex][groupIndex] = placed with
        {
            Members = source.Members.Select(member => byId[member.Id]).ToArray(),
        };
        return WallRejection.None;
    }

    private static WallRejection AddBatch(List<List<StartGroup>> lanes, AddBatchAction action,
        int currentRows, WallMetrics metrics)
    {
        if (action.Items is null || action.Items.Count == 0) return WallRejection.InvalidAction;
        foreach (var item in action.Items)
        {
            if (item is null) return WallRejection.InvalidAction;
            var rejection = PlaceMember(lanes, item.Member, item.TargetGroupId, item.LaneIndex,
                item.GroupInsertIndex, item.NewGroupId, currentRows, metrics);
            if (rejection != WallRejection.None) return rejection;
        }
        return WallRejection.None;
    }

    private static WallRejection MoveMember(List<List<StartGroup>> lanes, MoveMemberAction action,
        int currentRows, WallMetrics metrics)
    {
        var location = FindMember(lanes, action.MemberId);
        if (location is null) return WallRejection.ObjectNotFound;
        var (sourceLane, sourceGroup, member) = location.Value;
        var source = lanes[sourceLane][sourceGroup];
        var remaining = source.Members.Where(m =>
            !string.Equals(m.Id, action.MemberId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (remaining.Length == 0 && !string.Equals(source.Id, action.TargetGroupId, StringComparison.OrdinalIgnoreCase))
            lanes[sourceLane].RemoveAt(sourceGroup);
        else lanes[sourceLane][sourceGroup] = source with { Members = remaining };
        var sameGroup = string.Equals(source.Id, action.TargetGroupId, StringComparison.OrdinalIgnoreCase);
        var moved = WithBounds(member, action.TargetBounds);
        if (member.ImportSource is not null)
        {
            var changedPosition = !sameGroup || member.LocalBounds != action.TargetBounds;
            moved = moved with { UserClaimed = member.UserClaimed || changedPosition };
        }
        return PlaceMember(lanes, moved, action.TargetGroupId, action.LaneIndex,
            action.GroupInsertIndex, action.NewGroupId, currentRows, metrics);
    }

    private static WallRejection PlaceMember(List<List<StartGroup>> lanes, WallMember member, string? targetGroupId,
        int laneIndex, int insertIndex, string? newGroupId, int currentRows, WallMetrics metrics)
    {
        if (member is null || !IsLocalRect(member.LocalBounds)) return WallRejection.InvalidAction;
        if (FindMember(lanes, member.Id) is not null || FindGroup(lanes, member.Id) is not null)
            return WallRejection.DuplicateId;
        if (targetGroupId is null)
        {
            if (string.IsNullOrWhiteSpace(newGroupId) || FindGroup(lanes, newGroupId) is not null ||
                FindMember(lanes, newGroupId) is not null || laneIndex < 0 || laneIndex >= MaxColumns)
                return WallRejection.InvalidAction;
            while (laneIndex >= lanes.Count) lanes.Add([]);
            if (insertIndex < 0 || insertIndex > lanes[laneIndex].Count) return WallRejection.InvalidAction;
            lanes[laneIndex].Insert(insertIndex, new StartGroup { Id = newGroupId, Members = [member] });
            return WallRejection.None;
        }

        var groupLocation = FindGroup(lanes, targetGroupId);
        if (groupLocation is null) return WallRejection.ObjectNotFound;
        var (targetLane, targetIndex) = groupLocation.Value;
        var group = lanes[targetLane][targetIndex];
        var currentBudget = MaxMemberRowsForGroup(lanes[targetLane], targetIndex, currentRows, metrics);
        var expandedBudget = MaxMemberRowsForGroup(lanes[targetLane], targetIndex, MaxRows, metrics);
        var placed = TryPlaceMembers(group.Members, member, currentBudget)
            ?? TryPlaceMembers(group.Members, member, expandedBudget);
        if (placed is null) return WallRejection.NoSpace;
        lanes[targetLane][targetIndex] = group with { Members = placed };
        return WallRejection.None;
    }

    private static int MaxMemberRowsForGroup(IReadOnlyList<StartGroup> groups, int targetIndex,
        int wallRows, WallMetrics metrics)
    {
        if (wallRows < 1) return 0;
        var available = metrics.HeightOf(wallRows) - metrics.TitleHeight - metrics.TitleToMembersGap
            - (groups.Count - 1) * metrics.GroupGap;
        for (var i = 0; i < groups.Count; i++)
        {
            if (i == targetIndex) continue;
            available -= metrics.TitleHeight + metrics.TitleToMembersGap +
                metrics.HeightOf(groups[i].Members.Max(m => m.LocalBounds.Bottom));
        }
        return (int)Math.Clamp(Math.Floor((available + metrics.Gap) / metrics.Pitch), 0, MaxRows);
    }

    private static IReadOnlyList<WallMember>? TryPlaceMembers(IReadOnlyList<WallMember> existing,
        WallMember incoming, int maxRows)
    {
        if (maxRows < incoming.LocalBounds.Bottom) return null;
        var unaffected = existing.Where(m => !m.LocalBounds.Intersects(incoming.LocalBounds)).ToArray();
        var displaced = existing.Where(m => m.LocalBounds.Intersects(incoming.LocalBounds))
            .OrderBy(m => m.LocalBounds.Row).ThenBy(m => m.LocalBounds.Column)
            .ThenBy(m => m.Id, StringComparer.Ordinal).ToArray();
        var occupied = new List<WallMember>(unaffected.Length + displaced.Length + 1) { incoming };
        occupied.AddRange(unaffected);
        var moved = new Dictionary<string, WallMember>(StringComparer.Ordinal);
        var allDisplacedFit = true;
        foreach (var member in displaced)
        {
            var candidate = FindFreeLocalRect(member.LocalBounds, occupied, maxRows);
            if (candidate is null) { allDisplacedFit = false; break; }
            var relocated = WithBounds(member, candidate.Value);
            moved.Add(member.Id, relocated);
            occupied.Add(relocated);
        }
        if (allDisplacedFit)
            return [incoming, .. existing.Select(m => moved.GetValueOrDefault(m.Id) ?? m)];

        // If fixed neighbors leave no placement, allow a deterministic collision chain in this same budget.
        occupied = [incoming];
        var result = new List<WallMember>(existing.Count + 1) { incoming };
        foreach (var member in existing)
        {
            var final = member;
            if (occupied.Any(m => m.LocalBounds.Intersects(final.LocalBounds)))
            {
                var candidate = FindFreeLocalRect(member.LocalBounds, occupied, maxRows);
                if (candidate is null) return null;
                final = WithBounds(member, candidate.Value);
            }
            occupied.Add(final);
            result.Add(final);
        }
        return result;
    }

    private static GridRect? FindFreeLocalRect(GridRect original, IReadOnlyList<WallMember> occupied, int maxRows)
    {
        GridRect? best = null;
        var bestDistance = long.MaxValue;
        for (var row = 0; row + original.Height <= maxRows; row++)
        for (var col = 0; col + original.Width <= CellsPerLane; col++)
        {
            var candidate = new GridRect(col, row, original.Width, original.Height);
            if (occupied.Any(m => m.LocalBounds.Intersects(candidate))) continue;
            var distance = Math.Abs((long)row - original.Row) + Math.Abs((long)col - original.Column);
            if (distance >= bestDistance) continue;
            best = candidate;
            bestDistance = distance;
        }
        return best;
    }

    private static WallRejection MoveGroup(List<List<StartGroup>> lanes, MoveGroupAction action)
    {
        var location = FindGroup(lanes, action.GroupId);
        if (location is null) return WallRejection.ObjectNotFound;
        if (action.LaneIndex < 0 || action.LaneIndex >= MaxColumns) return WallRejection.InvalidAction;
        var (sourceLane, sourceIndex) = location.Value;
        var group = lanes[sourceLane][sourceIndex];
        var changedPosition = action.LaneIndex != sourceLane || action.GroupInsertIndex != sourceIndex;
        if (group.ImportSource is not null && changedPosition)
            group = group with { UserClaimed = true };
        lanes[sourceLane].RemoveAt(sourceIndex);
        while (action.LaneIndex >= lanes.Count) lanes.Add([]);
        var target = lanes[action.LaneIndex];
        if (action.GroupInsertIndex < 0 || action.GroupInsertIndex > target.Count) return WallRejection.InvalidAction;
        target.Insert(action.GroupInsertIndex, group);
        return WallRejection.None;
    }

    private static WallRejection RemoveMember(List<List<StartGroup>> lanes, string memberId)
    {
        var location = FindMember(lanes, memberId);
        if (location is null) return WallRejection.ObjectNotFound;
        var (laneIndex, groupIndex, _) = location.Value;
        var group = lanes[laneIndex][groupIndex];
        var remaining = group.Members.Where(m =>
            !string.Equals(m.Id, memberId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (remaining.Length == 0) lanes[laneIndex].RemoveAt(groupIndex);
        else lanes[laneIndex][groupIndex] = group with { Members = remaining };
        return WallRejection.None;
    }

    private static WallRejection RenameGroup(List<List<StartGroup>> lanes, string id, string? title)
    {
        var location = FindGroup(lanes, id);
        if (location is null) return WallRejection.ObjectNotFound;
        var (lane, group) = location.Value;
        var current = lanes[lane][group];
        title = title?.Trim();
        if (!string.IsNullOrEmpty(title) && lanes.SelectMany(items => items).Any(item =>
            !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Title?.Trim(), title, StringComparison.OrdinalIgnoreCase)))
            return WallRejection.InvalidAction;
        var changed = !string.Equals(current.Title, title, StringComparison.Ordinal);
        lanes[lane][group] = current with
        {
            Title = title,
            UserClaimed = current.UserClaimed || (current.ImportSource is not null && changed),
        };
        return WallRejection.None;
    }

    private static WallRejection ResizeLanes(List<List<StartGroup>> lanes, int columns, int rows,
        int currentRows, WallMetrics metrics)
    {
        var ordered = lanes.SelectMany(lane => lane).ToArray();
        var local = ResizeOriginalLanes(lanes, columns, rows, currentRows, metrics);
        if (local != WallRejection.NoSpace) return local;

        // Probe with each whole group's minimum height, but keep its original coordinates
        // until a final lane has been chosen. Never skip a taller group to fill a hole.
        var minimum = ordered.Select(CompactGroupFully).ToArray();
        var reflowed = new List<List<StartGroup>>();
        var next = 0;
        while (next < ordered.Length)
        {
            if (reflowed.Count >= columns) return WallRejection.NoSpace;
            var start = next;
            var probe = new List<StartGroup>();
            while (next < ordered.Length)
            {
                probe.Add(minimum[next]);
                if (RequiredLaneHeight(probe, metrics) > metrics.WallHeight(rows) + 1e-8)
                {
                    probe.RemoveAt(probe.Count - 1);
                    break;
                }
                next++;
            }
            if (next == start) return WallRejection.NoSpace;
            var final = ordered[start..next].ToList();
            var fit = ResizeOriginalLanes([final], 1, rows, currentRows, metrics);
            if (fit != WallRejection.None) return fit;
            reflowed.Add(final);
        }
        while (reflowed.Count < columns) reflowed.Add([]);
        lanes.Clear();
        lanes.AddRange(reflowed);
        return WallRejection.None;
    }

    private static StartGroup CompactGroupFully(StartGroup group)
    {
        var occupied = new bool[group.Members.Max(member => member.LocalBounds.Bottom)];
        foreach (var member in group.Members)
            for (var row = member.LocalBounds.Row; row < member.LocalBounds.Bottom; row++) occupied[row] = true;
        var before = new int[occupied.Length + 1];
        for (var row = 0; row < occupied.Length; row++) before[row + 1] = before[row] + (occupied[row] ? 0 : 1);
        return group with { Members = group.Members.Select(member => member with
            { LocalBounds = member.LocalBounds with { Row = member.LocalBounds.Row - before[member.LocalBounds.Row] } }).ToArray() };
    }

    internal static int MinimumGroupRows(StartGroup group)
    {
        var occupied = new bool[group.Members.Max(member => member.LocalBounds.Bottom)];
        foreach (var member in group.Members)
            for (var row = member.LocalBounds.Row; row < member.LocalBounds.Bottom; row++) occupied[row] = true;
        return occupied.Count(row => row);
    }

    private static WallRejection ResizeOriginalLanes(List<List<StartGroup>> lanes, int columns, int rows,
        int currentRows, WallMetrics metrics)
    {
        if (columns is < 1 or > MaxColumns || rows is < 1 or > MaxRows)
            return WallRejection.InvalidAction;
        // Remove only the necessary empty lanes, starting at the right to move the least content.
        for (var i = lanes.Count - 1; i >= 0 && lanes.Count > columns; i--)
            if (lanes[i].Count == 0) lanes.RemoveAt(i);
        if (lanes.Count > columns) return WallRejection.NoSpace;
        while (lanes.Count < columns) lanes.Add([]);

        var availableHeight = metrics.WallHeight(rows);
        foreach (var lane in lanes)
        {
            // Wall-tail whitespace already disappears with Rows. Only move members if they do not fit.
            var requiredHeight = RequiredLaneHeight(lane, metrics);
            for (var g = lane.Count - 1; g >= 0 && requiredHeight > availableHeight; g--)
            {
                var group = lane[g];
                var occupied = new bool[group.Members.Max(m => m.LocalBounds.Bottom)];
                foreach (var member in group.Members)
                    for (var row = member.LocalBounds.Row; row < member.LocalBounds.Bottom; row++)
                        occupied[row] = true;

                var removed = new bool[occupied.Length];
                var removedCount = 0;
                for (var row = occupied.Length - 1; row >= 0 &&
                    requiredHeight - removedCount * metrics.Pitch > availableHeight; row--)
                {
                    if (occupied[row]) continue;
                    removed[row] = true;
                    removedCount++;
                }
                if (removedCount == 0) continue;

                var removedBefore = new int[occupied.Length + 1];
                for (var row = 0; row < occupied.Length; row++)
                    removedBefore[row + 1] = removedBefore[row] + (removed[row] ? 1 : 0);
                lane[g] = group with
                {
                    Members = group.Members.Select(member =>
                    {
                        var bounds = member.LocalBounds;
                        var shift = removedBefore[bounds.Row];
                        return shift == 0 ? member : WithBounds(member,
                            new GridRect(bounds.Column, bounds.Row - shift, bounds.Width, bounds.Height));
                    }).ToArray(),
                };
                requiredHeight = RequiredLaneHeight(lane, metrics);
            }
            if (requiredHeight > availableHeight) return WallRejection.NoSpace;
        }
        return WallRejection.None;
    }

    private static (int Lane, int Group)? FindGroup(IReadOnlyList<List<StartGroup>> lanes, string id)
    {
        for (var l = 0; l < lanes.Count; l++)
        for (var g = 0; g < lanes[l].Count; g++)
            if (string.Equals(lanes[l][g].Id, id, StringComparison.OrdinalIgnoreCase)) return (l, g);
        return null;
    }

    private static (int Lane, int Group, WallMember Member)? FindMember(IReadOnlyList<List<StartGroup>> lanes, string id)
    {
        for (var l = 0; l < lanes.Count; l++)
        for (var g = 0; g < lanes[l].Count; g++)
        foreach (var member in lanes[l][g].Members)
            if (string.Equals(member.Id, id, StringComparison.OrdinalIgnoreCase)) return (l, g, member);
        return null;
    }

    private static bool IsLocalRect(GridRect rect) => rect.Column >= 0 && rect.Row >= 0 &&
        rect.Width >= 1 && rect.Height >= 1 && (long)rect.Column + rect.Width <= CellsPerLane &&
        (long)rect.Row + rect.Height <= MaxRows;

    private static bool IntersectsSafe(GridRect a, GridRect b) =>
        (long)a.Column < (long)b.Column + b.Width && (long)b.Column < (long)a.Column + a.Width &&
        (long)a.Row < (long)b.Row + b.Height && (long)b.Row < (long)a.Row + a.Height;

    private static WallMember WithBounds(WallMember member, GridRect bounds) => member switch
    {
        TileMember tile => tile with { LocalBounds = bounds },
        PuzzleMember puzzle => puzzle with { LocalBounds = bounds },
        ClockMember clock => clock with { LocalBounds = bounds },
        _ => throw new InvalidOperationException("Unknown member type."),
    };

    private static void ValidateMember(WallMember member, List<string> errors)
    {
        if (member is not (TileMember or PuzzleMember or ClockMember)) errors.Add("MEMBER_KIND");
        var b = member.LocalBounds;
        if (b.Column < 0 || b.Row < 0 || b.Width < 1 || b.Height < 1 ||
            (long)b.Column + b.Width > CellsPerLane || (long)b.Row + b.Height > MaxRows)
            errors.Add("MEMBER_BOUNDS");
        if (MemberPresentationRules.Validate(member) is { } presentationError)
            errors.Add(presentationError);
        if (!MemberSizeRules.IsValid(member)) errors.Add("MEMBER_SIZE_LIMIT");
        if (member.Entry is { } entry &&
            (!string.Equals(entry.ObjectId, member.Id, StringComparison.Ordinal) ||
             !Enum.IsDefined(entry.Kind) ||
             !EntryPathGuard.IsEntryRelativePathAllowed(EntryPathGuard.VirtualRoot,
                 member.Id, entry.RelativePath) ||
             entry.RelativePath is null ||
             !entry.RelativePath.EndsWith(entry.Kind == ManagedEntryKind.Lnk ? ".lnk" : ".url",
                 StringComparison.OrdinalIgnoreCase))) errors.Add("ENTRY_REFERENCE");
        if (member is ClockMember && member.Entry is not null) errors.Add("CLOCK_ENTRY");
        if (member is ClockMember clock &&
            !ClockFormatOptions.IsValid(clock.TimeFormat, clock.DateFormat))
            errors.Add("CLOCK_FORMAT");
        if (member is PuzzleMember puzzle)
        {
            if (!MemberSizeRules.IsPuzzleSize(b.Width, b.Height) || puzzle.Partitions is null)
                errors.Add("PUZZLE_SIZE");
            else if (puzzle.Partitions.Any(p => p.Column < 0 || p.Row < 0 || p.Width < 1 || p.Height < 1 ||
                (long)p.Column + p.Width > b.Width || (long)p.Row + p.Height > b.Height) ||
                !new PartitionLayout(b.Size, puzzle.Partitions).Check().IsValid)
                errors.Add("PUZZLE_PARTITIONS");
        }

    }

    private static void CheckId(string id, HashSet<string> ids, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            errors.Add("EMPTY_ID");
            return;
        }
        if (!EntryPathGuard.IsObjectIdValid(id) || EntryNames.Validate(id).Count > 0) errors.Add("INVALID_ID");
        if (!ids.Add(id)) errors.Add("DUPLICATE_ID");
    }

    private static WallLayoutResult Reject(WallRejection rejection, string? detail = null) =>
        new(null, rejection, detail);
}



