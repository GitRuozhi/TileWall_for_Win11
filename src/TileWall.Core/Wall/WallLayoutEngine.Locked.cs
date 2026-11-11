using TileWall.Core.Grid;

namespace TileWall.Core.Wall;

public static partial class WallLayoutEngine
{
    /// <summary>The persisted group footprint includes both content and explicitly retained blank rows.</summary>
    public static int EffectiveGroupRows(StartGroup group) => Math.Max(group.ReservedRows ?? 0,
        group.Members.Select(member => member.LocalBounds.Bottom).DefaultIfEmpty(0).Max());

    private static int ContentColumnCount(WallDocument document) =>
        document.Settings.LayoutLocked || document.Lanes.SelectMany(lane => lane.Groups).Any(group => group.ReservedRows is not null)
            ? document.Lanes.Select((lane, index) => (lane, index)).Where(pair => pair.lane.Groups.Count != 0)
                .Select(pair => pair.index + 1).DefaultIfEmpty(0).Max()
            : document.Lanes.Count(lane => lane.Groups.Count != 0);

    /// <summary>Checks the actual final geometry, including indirect group/lane movement.</summary>
    public static string? ValidateLockedChange(WallDocument current, WallDocument proposed, WallMetrics? metrics = null)
    {
        if (!current.Settings.LayoutLocked) return null;
        metrics ??= new();
        if (proposed.Settings.FullScreen != current.Settings.FullScreen ||
            proposed.Settings.MenuStart != current.Settings.MenuStart ||
            proposed.Settings.AnchorCorner != current.Settings.AnchorCorner ||
            proposed.Settings.WindowedViewportWidthDip != current.Settings.WindowedViewportWidthDip ||
            proposed.Rows != current.Rows || proposed.Columns < current.Columns ||
            proposed.Columns != current.Columns && !proposed.Settings.ExpandColumns)
            return "LAYOUT_LOCKED";
        var before = Measure(current, metrics);
        var after = Measure(proposed, metrics);
        var nextGroups = after.Groups.ToDictionary(group => group.GroupId, StringComparer.Ordinal);
        var nextMembers = after.Groups.SelectMany(group => group.Members.Select(member => (group.GroupId, member)))
            .ToDictionary(pair => pair.member.MemberId, StringComparer.Ordinal);
        var changed = 0;
        foreach (var group in before.Groups)
        {
            if (!nextGroups.TryGetValue(group.GroupId, out var next) || group.TitleBounds != next.TitleBounds ||
                group.LaneIndex != next.LaneIndex) return "LAYOUT_LOCKED";
            foreach (var member in group.Members)
                if (nextMembers.TryGetValue(member.MemberId, out var replacement) &&
                    (member.Bounds != replacement.member.Bounds || group.GroupId != replacement.GroupId) && ++changed > 1)
                    return "LAYOUT_LOCKED";
        }
        return null;
    }

    private static WallLayoutResult TryApplyLocked(WallDocument current, WallAction action,
        double width, double height, WallMetrics metrics)
    {
        // Retention has its own atomic rules; those rules retain source footprints below.
        if (action is UnpinMemberAction single)
            return RecycleBinRules.Unpin(current, [single.MemberId], false, width, height, metrics);
        if (action is UnpinMembersAction unpin)
            return RecycleBinRules.Unpin(current, unpin.MemberIds, unpin.ConfirmOverflow, width, height, metrics);
        if (action is RestoreRecycledMembersAction restore)
            return RecycleBinRules.Restore(current, restore, width, height, metrics);
        if (action is DeleteRecycledMembersAction delete)
            return RecycleBinRules.Delete(current, delete.MemberIds, width, height, metrics);
        if (action is ClearRecycleBinAction)
            return RecycleBinRules.Delete(current, current.InactiveMembers.Select(member => member.Id).ToArray(), width, height, metrics, true);
        if (action is SetRecycleBinEnabledAction enabled)
            return RecycleBinRules.SetEnabled(current, enabled.Enabled, width, height, metrics);

        var lanes = current.Lanes.Select(lane => lane.Groups.ToList()).ToList();
        string? detail = null;
        WallRejection rejection;
        switch (action)
        {
            case MoveMemberAction move:
            {
                // A locked drag targets an existing empty footprint; it never manufactures a fallback group.
                if (move.TargetGroupId is null) return Reject(WallRejection.InvalidAction, "LAYOUT_LOCKED");
                var location = FindMember(lanes, move.MemberId);
                if (location is null) return Reject(WallRejection.ObjectNotFound);
                var original = location.Value.Member;
                if (move.TargetBounds.Size != original.LocalBounds.Size) return Reject(WallRejection.InvalidAction);
                DetachKeepingFootprint(lanes, location.Value);
                var moved = WithBounds(original, move.TargetBounds) with
                { UserClaimed = original.UserClaimed || original.ImportSource is not null };
                rejection = PlaceWithoutAvoidance(lanes, moved, move.TargetGroupId, move.LaneIndex,
                    move.GroupInsertIndex, move.NewGroupId, current, metrics);
                break;
            }
            case ReplaceMemberAction replace:
            {
                var location = FindMember(lanes, replace.MemberId);
                if (location is null) return Reject(WallRejection.ObjectNotFound);
                var original = location.Value.Member;
                if (replace.Replacement is null || replace.Replacement.Id != original.Id ||
                    replace.Replacement.GetType() != original.GetType() || !MemberSizeRules.IsValid(replace.Replacement) ||
                    replace.Replacement.LocalBounds.Width is < 1 or > CellsPerLane ||
                    replace.Replacement.LocalBounds.Height is < 1 or > MaxRows) return Reject(WallRejection.InvalidAction);
                var replacement = replace.Replacement with
                {
                    ImportSource = original.ImportSource,
                    UserClaimed = original.UserClaimed || original.ImportSource is not null &&
                        (replace.Replacement is PuzzleMember { Partitions: null } ||
                         MemberPresentationRules.HasUserPropertyChanges(original, replace.Replacement)),
                };
                var sourceId = lanes[location.Value.Lane][location.Value.Group].Id;
                DetachKeepingFootprint(lanes, location.Value);
                // Resize is anchored at the old cell. Caller-supplied row/column cannot smuggle in a move.
                replacement = WithBounds(replacement, replacement.LocalBounds with
                { Column = original.LocalBounds.Column, Row = original.LocalBounds.Row });
                var inPlace = TryLockedPlacement(lanes, replacement, sourceId, location.Value.Lane,
                    location.Value.Group, null, current, width, height, metrics);
                if (inPlace.Proposal is not null) return inPlace;
                var originalGroup = current.Lanes[location.Value.Lane].Groups[location.Value.Group];
                var source = lanes[location.Value.Lane][location.Value.Group];
                var nearby = FindFreeLocalRect(replacement.LocalBounds, source.Members, EffectiveGroupRows(originalGroup));
                if (nearby is { } empty)
                {
                    var relocated = TryLockedPlacement(lanes, WithBounds(replacement, empty), sourceId,
                        location.Value.Lane, location.Value.Group, null, current, width, height, metrics);
                    if (relocated.Proposal is not null) return relocated with { Detail = "LAYOUT_MOVED_NEARBY" };
                }
                var bounds = MemberLayoutOrder.FindAppendBounds(source.Members.Select(member => member.LocalBounds).ToArray(),
                    replacement.LocalBounds.Width, replacement.LocalBounds.Height, current.Rows);
                if (bounds is { } end)
                {
                    var appended = TryLockedPlacement(lanes, WithBounds(replacement, end), sourceId,
                        location.Value.Lane, location.Value.Group, null, current, width, height, metrics);
                    if (appended.Proposal is not null) return appended with { Detail = "LAYOUT_MOVED_GROUP_END" };
                }
                // Last resort starts after all existing groups, never pushes a subsequent group.
                var lastLane = current.Lanes.Select((lane, index) => (lane, index))
                    .Where(pair => pair.lane.Groups.Count != 0).Select(pair => pair.index).DefaultIfEmpty(0).Max();
                var newId = AllocateLayoutGroupId(current, original.Id);
                for (var lane = lastLane; lane < (current.Settings.ExpandColumns ? MaxColumns - 1 : current.Columns); lane++)
                {
                    var tail = TryLockedPlacement(lanes, WithBounds(replacement, new(0, 0,
                            replacement.LocalBounds.Width, replacement.LocalBounds.Height)), null, lane,
                        lane < lanes.Count ? lanes[lane].Count : 0, newId, current, width, height, metrics);
                    if (tail.Proposal is not null) return tail with { Detail = "LAYOUT_MOVED_WALL_END" };
                }
                return Reject(WallRejection.NoSpace, "LAYOUT_LOCKED_NO_SPACE");
            }
            case AddMemberAction add:
                rejection = AppendWithoutAvoidance(lanes, add.Member, add.TargetGroupId, add.LaneIndex,
                    add.GroupInsertIndex, add.NewGroupId, current, metrics);
                break;
            case AddBatchAction batch:
                if (batch.Items is not { Count: > 0 }) return Reject(WallRejection.InvalidAction);
                rejection = WallRejection.None;
                foreach (var item in batch.Items)
                {
                    if (item is null) return Reject(WallRejection.InvalidAction);
                    rejection = AppendWithoutAvoidance(lanes, item.Member, item.TargetGroupId, item.LaneIndex,
                        item.GroupInsertIndex, item.NewGroupId, current, metrics);
                    if (rejection != WallRejection.None) break;
                }
                break;
            case RemoveMemberAction remove:
            {
                var location = FindMember(lanes, remove.MemberId);
                if (location is null) return Reject(WallRejection.ObjectNotFound);
                DetachKeepingFootprint(lanes, location.Value);
                rejection = WallRejection.None;
                break;
            }
            case RenameGroupAction rename:
                rejection = RenameGroup(lanes, rename.GroupId, rename.Title);
                break;
            case MoveGroupAction:
            case ResizeWallAction:
                return Reject(WallRejection.InvalidAction, "LAYOUT_LOCKED");
            default:
                return Reject(WallRejection.InvalidAction);
        }
        return rejection == WallRejection.None ? FinishLocked(current, lanes, width, height, metrics, detail)
            : Reject(rejection, rejection == WallRejection.NoSpace ? "LAYOUT_LOCKED_NO_SPACE" : null);
    }

    private static void DetachKeepingFootprint(List<List<StartGroup>> lanes,
        (int Lane, int Group, WallMember Member) location)
    {
        var group = lanes[location.Lane][location.Group];
        var remaining = group.Members.Where(member => member.Id != location.Member.Id).ToArray();
        lanes[location.Lane][location.Group] = group with
        {
            ReservedRows = remaining.Select(member => member.LocalBounds.Bottom).DefaultIfEmpty(0).Max() < EffectiveGroupRows(group)
                ? EffectiveGroupRows(group) : group.ReservedRows,
            Members = remaining,
        };
    }

    private static WallRejection AppendWithoutAvoidance(List<List<StartGroup>> lanes, WallMember member,
        string? groupId, int lane, int index, string? newId, WallDocument current, WallMetrics metrics)
    {
        if (member is null) return WallRejection.InvalidAction;
        if (groupId is not null)
        {
            var location = FindGroup(lanes, groupId);
            if (location is null) return WallRejection.ObjectNotFound;
            var bounds = MemberLayoutOrder.FindAppendBounds(lanes[location.Value.Lane][location.Value.Group].Members
                .Select(item => item.LocalBounds).ToArray(), member.LocalBounds.Width, member.LocalBounds.Height, current.Rows);
            if (bounds is null) return WallRejection.NoSpace;
            member = WithBounds(member, bounds.Value);
        }
        return PlaceWithoutAvoidance(lanes, member, groupId, lane, index, newId, current, metrics);
    }

    private static WallRejection PlaceWithoutAvoidance(List<List<StartGroup>> lanes, WallMember member,
        string? groupId, int lane, int index, string? newId, WallDocument current, WallMetrics metrics)
    {
        if (member is null || !IsLocalRect(member.LocalBounds) || !MemberSizeRules.IsValid(member))
            return WallRejection.InvalidAction;
        if (FindMember(lanes, member.Id) is not null || FindGroup(lanes, member.Id) is not null)
            return WallRejection.DuplicateId;
        if (groupId is null)
        {
            if (lane < 0 || lane >= (current.Settings.ExpandColumns ? MaxColumns - 1 : current.Columns) ||
                string.IsNullOrWhiteSpace(newId) || FindGroup(lanes, newId) is not null || FindMember(lanes, newId) is not null)
                return WallRejection.NoSpace;
            while (lanes.Count <= lane) lanes.Add([]);
            if (index != lanes[lane].Count) return WallRejection.NoSpace;
            lanes[lane].Add(new StartGroup { Id = newId, Members = [member] });
        }
        else
        {
            var location = FindGroup(lanes, groupId);
            if (location is null) return WallRejection.ObjectNotFound;
            var target = lanes[location.Value.Lane][location.Value.Group];
            if (target.Members.Any(other => other.LocalBounds.Intersects(member.LocalBounds))) return WallRejection.NoSpace;
            lanes[location.Value.Lane][location.Value.Group] = target with { Members = [.. target.Members, member] };
        }
        return WallRejection.None;
    }

    private static WallLayoutResult TryLockedPlacement(List<List<StartGroup>> original, WallMember member,
        string? groupId, int lane, int index, string? newId, WallDocument current, double width, double height, WallMetrics metrics)
    {
        var trial = original.Select(groups => groups.ToList()).ToList();
        var rejection = PlaceWithoutAvoidance(trial, member, groupId, lane, index, newId, current, metrics);
        return rejection == WallRejection.None ? FinishLocked(current, trial, width, height, metrics)
            : Reject(rejection);
    }

    private static WallLayoutResult FinishLocked(WallDocument current, List<List<StartGroup>> lanes,
        double width, double height, WallMetrics metrics, string? detail = null)
    {
        var next = current with
        {
            Generation = current.Generation + 1, Columns = lanes.Count,
            Lanes = lanes.Select((groups, index) => index < current.Lanes.Count
                ? current.Lanes[index] with { Groups = groups.ToArray() } : new WallLane { Groups = groups.ToArray() }).ToArray(),
        };
        var result = NormalizeExpanded(next, metrics);
        if (result.Proposal is not { } proposal) return result;
        if (ValidateLockedChange(current, proposal.Document, metrics) is { } error)
            return Reject(WallRejection.InvalidAction, error);
        var budget = new WallViewportBudget(width, height, MaxColumns, current.Rows, current.Settings.FullScreen);
        var validated = WallViewportPolicy.Validate(proposal, budget, metrics);
        return validated.Proposal is not null ? validated with { Detail = detail } : validated;
    }

    private static string AllocateLayoutGroupId(WallDocument current, string memberId)
    {
        var ids = current.EnumerateAllMembers().Select(member => member.Id)
            .Concat(current.Lanes.SelectMany(lane => lane.Groups).Select(group => group.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prefix = "layout-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(memberId))).ToLowerInvariant()[..24];
        var id = prefix;
        for (var suffix = 1; ids.Contains(id); suffix++) id = prefix + "-" + suffix;
        return id;
    }
}
