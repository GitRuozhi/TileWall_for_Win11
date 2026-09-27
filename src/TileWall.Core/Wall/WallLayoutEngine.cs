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

    public static WallLayoutResult TryApply(WallDocument current, WallAction action,
        double workWidthDip, double workHeightDip, WallMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(action);
        metrics ??= new WallMetrics();
        if (!metrics.IsValid || !double.IsFinite(workWidthDip) || !double.IsFinite(workHeightDip)
            || workWidthDip <= 0 || workHeightDip <= 0)
        {
            return Reject(WallRejection.InvalidAction, "Invalid metrics or work area.");
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
        WallRejection rejection;
        switch (action)
        {
            case AddMemberAction add:
                rejection = PlaceMember(lanes, add.Member, add.TargetGroupId, add.LaneIndex,
                    add.GroupInsertIndex, add.NewGroupId, current.Rows, workHeightDip, metrics);
                break;
            case ReplaceMemberAction replace:
                rejection = ReplaceMember(lanes, replace, current.Rows, workHeightDip, metrics);
                break;
            case AddBatchAction batch:
                rejection = AddBatch(lanes, batch, current.Rows, workHeightDip, metrics);
                break;
            case MoveMemberAction move:
                rejection = MoveMember(lanes, move, current.Rows, workHeightDip, metrics);
                break;
            case MoveGroupAction moveGroup:
                rejection = MoveGroup(lanes, moveGroup);
                break;
            case RemoveMemberAction remove:
                rejection = RemoveMember(lanes, remove.MemberId);
                break;
            case RenameGroupAction rename:
                rejection = RenameGroup(lanes, rename.GroupId, rename.Title);
                break;
            case ResizeWallAction resize:
                rejection = ResizeLanes(lanes, resize.Columns, resize.Rows);
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
            Lanes = lanes.Select(g => new WallLane { Groups = g.ToArray() }).ToArray(),
        };
        errors = Validate(proposed);
        if (errors.Count > 0)
        {
            return Reject(WallRejection.InvalidAction, string.Join("; ", errors));
        }

        if (metrics.WallWidth(proposed.Columns) > workWidthDip)
        {
            return Reject(WallRejection.WorkAreaTooSmall);
        }

        var requiredHeight = RequiredHeight(proposed, metrics);
        if (action is not ResizeWallAction)
        {
            while (metrics.WallHeight(proposed.Rows) < requiredHeight &&
                   metrics.WallHeight(proposed.Rows + 1) <= workHeightDip)
            {
                proposed = proposed with { Rows = proposed.Rows + 1 };
            }
        }

        if (metrics.WallHeight(proposed.Rows) > workHeightDip)
        {
            return Reject(WallRejection.WorkAreaTooSmall);
        }

        if (requiredHeight > metrics.WallHeight(proposed.Rows))
        {
            return Reject(WallRejection.NoSpace);
        }

        errors = ValidateGeometry(proposed, metrics);
        if (errors.Count > 0)
        {
            return Reject(WallRejection.InvalidAction, string.Join("; ", errors));
        }

        return new WallLayoutResult(new WallProposal(proposed, Measure(proposed, metrics)), WallRejection.None);
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
        else if (!Enum.IsDefined(document.Settings.AnchorCorner) || !Enum.IsDefined(document.Settings.LanguageMode))
            errors.Add("SETTINGS_ENUM");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lane in document.Lanes)
        {
            if (lane?.Groups is null) { errors.Add("LANE_NULL"); continue; }
            foreach (var group in lane.Groups)
            {
                if (group is null) { errors.Add("GROUP_NULL"); continue; }
                CheckId(group.Id, ids, errors);
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
                    if (member is not (TileMember or PuzzleMember or ClockMember)) errors.Add("MEMBER_KIND");
                    var b = member.LocalBounds;
                    if (b.Column < 0 || b.Row < 0 || b.Width < 1 || b.Height < 1 ||
                        (long)b.Column + b.Width > CellsPerLane || (long)b.Row + b.Height > MaxRows)
                        errors.Add("MEMBER_BOUNDS");
                    if (member.Visual is null) errors.Add("VISUAL_NULL");
                    if (member.Entry is { } entry &&
                        (!string.Equals(entry.ObjectId, member.Id, StringComparison.Ordinal) ||
                         !Enum.IsDefined(entry.Kind) ||
                         !EntryPathGuard.IsEntryRelativePathAllowed(EntryPathGuard.VirtualRoot,
                             member.Id, entry.RelativePath) ||
                         entry.RelativePath is null ||
                         !entry.RelativePath.EndsWith(entry.Kind == ManagedEntryKind.Lnk ? ".lnk" : ".url",
                             StringComparison.OrdinalIgnoreCase))) errors.Add("ENTRY_REFERENCE");
                    if (member is ClockMember && member.Entry is not null) errors.Add("CLOCK_ENTRY");
                    if (member is TileMember tile && (tile.Foreground is not (AutoEntryIcon or CustomImageIcon or NoForegroundIcon) ||
                        tile.Foreground is CustomImageIcon { SourcePath: null or "" })) errors.Add("FOREGROUND");
                    if (member is ClockMember clock &&
                        !ClockFormatOptions.IsValid(clock.TimeFormat, clock.DateFormat))
                        errors.Add("CLOCK_FORMAT");
                    if (member is PuzzleMember puzzle)
                    {
                        if (puzzle.Images is null || !Enum.IsDefined(puzzle.Images.Kind) ||
                            puzzle.Images.ImagePaths is null || puzzle.Images.ImagePaths.Any(path => path is null) ||
                            puzzle.Images.Transforms is null)
                            errors.Add("PUZZLE_IMAGES");
                        else if (puzzle.Images.Transforms.Any(t => t is null || string.IsNullOrWhiteSpace(t.ImageId) ||
                            !Enum.IsDefined(t.Fit) ||
                            !double.IsFinite(t.Transform.Scale) || t.Transform.Scale <= 0 ||
                            !double.IsFinite(t.Transform.OffsetX) || !double.IsFinite(t.Transform.OffsetY)))
                            errors.Add("PUZZLE_TRANSFORMS");
                        else if (puzzle.Images.Transforms.Select(t => t.ImageId)
                            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != puzzle.Images.Transforms.Count)
                            errors.Add("PUZZLE_TRANSFORMS");
                        if (b.Width is < 2 or > 8 || b.Height is < 2 or > 25 || puzzle.Partitions is null)
                            errors.Add("PUZZLE_SIZE");
                        else if (puzzle.Partitions.Any(p => p.Column < 0 || p.Row < 0 || p.Width < 1 || p.Height < 1 ||
                            (long)p.Column + p.Width > b.Width || (long)p.Row + p.Height > b.Height) ||
                            !new PartitionLayout(b.Size, puzzle.Partitions).Check().IsValid)
                            errors.Add("PUZZLE_PARTITIONS");
                    }

                    for (var j = 0; j < i; j++)
                        if (members[j] is not null && IntersectsSafe(b, members[j].LocalBounds)) errors.Add("MEMBER_OVERLAP");
                }
            }
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
        var positions = new List<GroupPlacement>();
        for (var laneIndex = 0; laneIndex < document.Lanes.Count; laneIndex++)
        {
            var x = metrics.Margin + laneIndex * (metrics.LaneWidth + metrics.ColumnGap);
            var y = metrics.Margin;
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
        var maximum = metrics.Margin;
        foreach (var lane in document.Lanes)
        {
            var height = metrics.Margin;
            foreach (var group in lane.Groups)
                height += metrics.TitleHeight + metrics.TitleToMembersGap +
                    metrics.HeightOf(group.Members.Max(m => m.LocalBounds.Bottom)) + metrics.GroupGap;
            if (lane.Groups.Count > 0) height -= metrics.GroupGap;
            maximum = Math.Max(maximum, height);
        }

        return maximum + metrics.Margin;
    }

    private static WallRejection ReplaceMember(List<List<StartGroup>> lanes, ReplaceMemberAction action,
        int currentRows, double workHeightDip, WallMetrics metrics)
    {
        var location = FindMember(lanes, action.MemberId);
        if (location is null) return WallRejection.ObjectNotFound;
        var (laneIndex, groupIndex, original) = location.Value;
        if (action.Replacement is null ||
            !string.Equals(original.Id, action.Replacement.Id, StringComparison.Ordinal) ||
            original.GetType() != action.Replacement.GetType())
            return WallRejection.InvalidAction;

        var source = lanes[laneIndex][groupIndex];
        lanes[laneIndex][groupIndex] = source with
        {
            Members = source.Members.Where(member =>
                !string.Equals(member.Id, original.Id, StringComparison.Ordinal)).ToArray(),
        };
        var rejection = PlaceMember(lanes, action.Replacement, source.Id, laneIndex, groupIndex,
            null, currentRows, workHeightDip, metrics);
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
        int currentRows, double workHeightDip, WallMetrics metrics)
    {
        if (action.Items is null || action.Items.Count == 0) return WallRejection.InvalidAction;
        foreach (var item in action.Items)
        {
            if (item is null) return WallRejection.InvalidAction;
            var rejection = PlaceMember(lanes, item.Member, item.TargetGroupId, item.LaneIndex,
                item.GroupInsertIndex, item.NewGroupId, currentRows, workHeightDip, metrics);
            if (rejection != WallRejection.None) return rejection;
        }
        return WallRejection.None;
    }

    private static WallRejection MoveMember(List<List<StartGroup>> lanes, MoveMemberAction action,
        int currentRows, double workHeightDip, WallMetrics metrics)
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
        var moved = WithBounds(member, action.TargetBounds);
        return PlaceMember(lanes, moved, action.TargetGroupId, action.LaneIndex,
            action.GroupInsertIndex, action.NewGroupId, currentRows, workHeightDip, metrics);
    }

    private static WallRejection PlaceMember(List<List<StartGroup>> lanes, WallMember member, string? targetGroupId,
        int laneIndex, int insertIndex, string? newGroupId, int currentRows,
        double workHeightDip, WallMetrics metrics)
    {
        if (member is null || !IsLocalRect(member.LocalBounds)) return WallRejection.InvalidAction;
        if (FindMember(lanes, member.Id) is not null || FindGroup(lanes, member.Id) is not null)
            return WallRejection.DuplicateId;
        if (targetGroupId is null)
        {
            if (string.IsNullOrWhiteSpace(newGroupId) || FindGroup(lanes, newGroupId) is not null ||
                FindMember(lanes, newGroupId) is not null || laneIndex < 0 || laneIndex > lanes.Count)
                return WallRejection.InvalidAction;
            if (laneIndex == lanes.Count) lanes.Add([]);
            if (insertIndex < 0 || insertIndex > lanes[laneIndex].Count) return WallRejection.InvalidAction;
            lanes[laneIndex].Insert(insertIndex, new StartGroup { Id = newGroupId, Members = [member] });
            return WallRejection.None;
        }

        var groupLocation = FindGroup(lanes, targetGroupId);
        if (groupLocation is null) return WallRejection.ObjectNotFound;
        var (targetLane, targetIndex) = groupLocation.Value;
        var group = lanes[targetLane][targetIndex];
        var currentBudget = MaxMemberRowsForGroup(lanes[targetLane], targetIndex, currentRows, metrics);
        var maxWallRows = (int)Math.Clamp(Math.Floor((workHeightDip - 2 * metrics.Margin + metrics.Gap) /
            metrics.Pitch), 0, MaxRows);
        var expandedBudget = MaxMemberRowsForGroup(lanes[targetLane], targetIndex, maxWallRows, metrics);
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
        if (action.LaneIndex < 0 || action.LaneIndex > lanes.Count) return WallRejection.InvalidAction;
        var (sourceLane, sourceIndex) = location.Value;
        var group = lanes[sourceLane][sourceIndex];
        lanes[sourceLane].RemoveAt(sourceIndex);
        if (action.LaneIndex == lanes.Count) lanes.Add([]);
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
        lanes[lane][group] = lanes[lane][group] with { Title = title };
        return WallRejection.None;
    }

    private static WallRejection ResizeLanes(List<List<StartGroup>> lanes, int columns, int rows)
    {
        if (columns is < 1 or > MaxColumns || rows is < 1 or > MaxRows)
            return WallRejection.InvalidAction;
        if (columns < lanes.Count && lanes.Skip(columns).Any(l => l.Count > 0)) return WallRejection.NoSpace;
        while (lanes.Count > columns) lanes.RemoveAt(lanes.Count - 1);
        while (lanes.Count < columns) lanes.Add([]);
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

    private static void CheckId(string id, HashSet<string> ids, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            errors.Add("EMPTY_ID");
            return;
        }
        if (!EntryPathGuard.IsObjectIdValid(id) || EntryNames.Validate(id).Count > 0 ||
            IsSuperscriptDeviceName(id)) errors.Add("INVALID_ID");
        if (!ids.Add(id)) errors.Add("DUPLICATE_ID");
    }

    private static bool IsSuperscriptDeviceName(string id)
    {
        var stem = id.Split('.')[0];
        return stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            stem[3] is '\u00B9' or '\u00B2' or '\u00B3';
    }

    private static WallLayoutResult Reject(WallRejection rejection, string? detail = null) =>
        new(null, rejection, detail);
}
