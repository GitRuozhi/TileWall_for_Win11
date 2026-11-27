using TileWall.Core.Grid;

namespace TileWall.Core.Wall;

public static partial class WallLayoutEngine
{
    internal static IReadOnlyList<WallMember>? ResizeGroupMembers(StartGroup group,
        IReadOnlyDictionary<string, GridSize> sizes, IReadOnlySet<string> selected, bool layoutLocked)
    {
        var ordered = MemberLayoutOrder.Order(group.Members).ToArray();
        var first = Array.FindIndex(ordered, member => selected.Contains(member.Id));
        if (first < 0 || sizes.Count == 0) return ordered;
        if (layoutLocked)
        {
            var fixedPositions = ordered.Select(Resize).ToArray();
            return fixedPositions.All(member => IsLocalRect(member.LocalBounds) &&
                member.LocalBounds.Bottom <= EffectiveGroupRows(group) &&
                !fixedPositions.Any(other => other.Id != member.Id && other.LocalBounds.Intersects(member.LocalBounds)))
                ? fixedPositions : null;
        }

        var placed = ordered.Take(first).ToList();
        var firstBounds = ordered[first].LocalBounds;
        var cell = firstBounds.Row * CellsPerLane + firstBounds.Column;
        foreach (var member in ordered.Skip(first).Select(Resize))
        {
            for (; cell < MaxRows * CellsPerLane; cell++)
            {
                var bounds = member.LocalBounds with { Column = cell % CellsPerLane, Row = cell / CellsPerLane };
                if (!IsLocalRect(bounds) || placed.Any(other => other.LocalBounds.Intersects(bounds))) continue;
                placed.Add(WithBounds(member, bounds));
                cell++;
                break;
            }
            if (placed.Count == 0 || placed[^1].Id != member.Id) return null;
        }
        return placed;

        WallMember Resize(WallMember member) =>
            member is TileMember && sizes.TryGetValue(member.Id, out var size)
                ? WithBounds(member, member.LocalBounds with { Width = size.Columns, Height = size.Rows }) : member;
    }

    internal static IReadOnlyList<WallMember>? MoveWithinEditedGroup(StartGroup group, string id,
        GridRect bounds, bool layoutLocked)
    {
        var member = group.Members.FirstOrDefault(member => member.Id == id);
        if (member is null || member.LocalBounds.Size != bounds.Size || !IsLocalRect(bounds)) return null;
        var otherMembers = group.Members.Where(other => other.Id != id).ToArray();
        var moved = WithBounds(member, bounds);
        var rows = EffectiveGroupRows(group);
        if (layoutLocked)
            return bounds.Bottom <= rows && !otherMembers.Any(other => other.LocalBounds.Intersects(bounds))
                ? [.. otherMembers, moved] : null;
        return TryPlaceMembers(otherMembers, moved, rows) ?? TryPlaceMembers(otherMembers, moved, MaxRows);
    }

    internal static WallLayoutResult ProposeGroupEdit(WallDocument current, string groupId,
        IReadOnlyList<WallMember> members, WallViewportBudget budget, WallMetrics metrics)
    {
        var source = current.Lanes.SelectMany(lane => lane.Groups).FirstOrDefault(group => group.Id == groupId);
        if (source is null) return Reject(WallRejection.ObjectNotFound);
        var previous = source.Members.ToDictionary(member => member.Id, StringComparer.Ordinal);
        if (previous.Count != members.Count || members.DistinctBy(member => member.Id).Count() != members.Count)
            return Reject(WallRejection.InvalidAction);
        var retained = new List<WallMember>(members.Count);
        foreach (var member in members)
        {
            if (!previous.TryGetValue(member.Id, out var original) || original.GetType() != member.GetType())
                return Reject(WallRejection.InvalidAction);
            retained.Add(member with
            {
                ImportSource = original.ImportSource,
                UserClaimed = original.UserClaimed || original.ImportSource is not null &&
                    (original.LocalBounds != member.LocalBounds || MemberPresentationRules.HasUserPropertyChanges(original, member)),
            });
        }
        var replacement = source with { Members = retained.ToArray() };
        if (current.Settings.LayoutLocked) replacement = PreserveGroupFootprint(source, replacement);
        var candidate = current with
        {
            Generation = checked(current.Generation + 1),
            Lanes = current.Lanes.Select(lane =>
                lane with { Groups = lane.Groups.Select(group => group.Id == groupId ? replacement : group).ToArray() }).ToArray(),
        };
        if (!current.Settings.LayoutLocked)
            candidate = candidate with { Rows = Math.Max(current.Rows, RowsForHeight(RequiredHeight(candidate, metrics), metrics)) };
        if (candidate.Rows > budget.MaxRows) return Reject(WallRejection.WorkAreaTooSmall, "CONTENT_TOO_LARGE");
        var errors = ValidateGeometry(candidate, metrics);
        if (errors.Count != 0) return Reject(WallRejection.InvalidAction, string.Join("; ", errors));
        var validated = WallViewportPolicy.Validate(candidate, budget, metrics);
        if (validated.Proposal is { } proposal && ValidateLockedChange(current, proposal.Document, metrics) is { } locked)
            return Reject(WallRejection.InvalidAction, locked);
        return validated;
    }
}
