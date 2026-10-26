using TileWall.Core.Grid;
using TileWall.Core.Import;

namespace TileWall.Core.Wall;

/// <summary>Pure retention and restore rules. Selection order never changes FIFO or wall order.</summary>
public static class RecycleBinRules
{
    public const int Capacity = 200;
    public const int WarningCount = 160;

    public static IReadOnlyList<WallMember> OrderedActive(WallDocument current, IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        return current.Lanes.SelectMany(lane => lane.Groups).SelectMany(group => MemberLayoutOrder.Order(group.Members))
            .Where(member => selected.Contains(member.Id)).ToArray();
    }

    public static IReadOnlyList<WallMember> Overflow(WallDocument current, IEnumerable<string> ids)
    {
        if (!current.RecycleBinEnabled) return [];
        var joined = current.InactiveMembers.Concat(OrderedActive(current, ids)).ToArray();
        return joined.Take(Math.Max(0, joined.Length - Capacity)).ToArray();
    }

    public static string UniqueGroupTitle(WallDocument current, string title)
    {
        var names = current.Lanes.SelectMany(lane => lane.Groups).Select(group => group.Title?.Trim())
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(title)) return title;
        for (var number = 2; ; number++)
        {
            var candidate = $"{title} ({number})";
            if (!names.Contains(candidate)) return candidate;
        }
    }

    internal static WallLayoutResult Unpin(WallDocument current, IReadOnlyList<string> ids, bool confirmOverflow,
        double width, double height, WallMetrics metrics)
    {
        if (!ValidSelection(ids)) return Reject();
        var members = OrderedActive(current, ids);
        if (members.Count != ids.Count) return new(null, WallRejection.ObjectNotFound);
        var overflow = Overflow(current, ids);
        if (overflow.Count != 0 && !confirmOverflow)
            return new(null, WallRejection.InvalidAction, "RECYCLE_OVERFLOW:" + overflow.Count);
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        var retained = current.RecycleBinEnabled ? current.InactiveMembers.Concat(members.Select(member => member with
        {
            LocalBounds = new GridRect(0, 0, member.LocalBounds.Width, member.LocalBounds.Height),
            ImportSource = null,
            UserClaimed = member.UserClaimed || member.ImportSource is not null,
        })).Skip(overflow.Count).ToArray() : current.InactiveMembers;
        var next = current with
        {
            Generation = current.Generation + 1,
            InactiveMembers = retained,
            Lanes = current.Lanes.Select(lane => lane with { Groups = lane.Groups.Select(group => group with
                { Members = group.Members.Where(member => !selected.Contains(member.Id)).ToArray() })
                .Where(group => group.Members.Count != 0).ToArray() }).ToArray(),
        };
        return WallLayoutEngine.FinishContentChange(next, width, height, metrics);
    }

    internal static WallLayoutResult Delete(WallDocument current, IReadOnlyList<string> ids,
        double width, double height, WallMetrics metrics, bool allowEmpty = false)
    {
        if (!(allowEmpty && ids.Count == 0) && !ValidSelection(ids)) return Reject();
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        if (current.InactiveMembers.Count(member => selected.Contains(member.Id)) != ids.Count)
            return new(null, WallRejection.ObjectNotFound);
        return WallLayoutEngine.FinishContentChange(current with { Generation = current.Generation + 1,
            InactiveMembers = current.InactiveMembers.Where(member => !selected.Contains(member.Id)).ToArray() }, width, height, metrics);
    }

    internal static WallLayoutResult SetEnabled(WallDocument current, bool enabled, double width, double height, WallMetrics metrics) =>
        WallLayoutEngine.FinishContentChange(current with { Generation = current.Generation + 1,
            RecycleBinEnabled = enabled, InactiveMembers = enabled ? current.InactiveMembers : [] }, width, height, metrics);

    internal static WallLayoutResult Restore(WallDocument current, RestoreRecycledMembersAction action,
        double width, double height, WallMetrics metrics)
    {
        if (!ValidSelection(action.MemberIds) || string.IsNullOrWhiteSpace(action.NewGroupId) || string.IsNullOrWhiteSpace(action.GroupTitle)) return Reject();
        var selected = action.MemberIds.ToHashSet(StringComparer.Ordinal);
        var members = current.InactiveMembers.Where(member => selected.Contains(member.Id)).ToArray();
        if (members.Length != selected.Count) return new(null, WallRejection.ObjectNotFound);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var placed = new List<WallMember>();
        foreach (var member in members)
        {
            if (member.Entry is not null)
            {
                if (!action.LaunchIdentityKeys.TryGetValue(member.Id, out var identity) || string.IsNullOrWhiteSpace(identity))
                    return new(null, WallRejection.InvalidAction, "RECYCLE_IDENTITY_UNAVAILABLE");
                if (!identities.Add(identity)) return new(null, WallRejection.InvalidAction, "RECYCLE_DUPLICATE_LAUNCH");
            }
            var bounds = MemberLayoutOrder.FindAppendBounds(placed.Select(item => item.LocalBounds).ToArray(),
                member.LocalBounds.Width, member.LocalBounds.Height, 256);
            if (bounds is null) return new(null, WallRejection.NoSpace);
            placed.Add(member with { LocalBounds = bounds.Value });
        }
        var detached = current with { InactiveMembers = current.InactiveMembers.Where(member => !selected.Contains(member.Id)).ToArray() };
        var group = new StartGroup { Id = action.NewGroupId, Title = UniqueGroupTitle(current, action.GroupTitle), Members = placed.ToArray() };
        var budget = new WallViewportBudget(width, height, WallViewportPolicy.CompleteColumns(width, metrics),
            Math.Clamp((int)Math.Floor((height - metrics.TopMargin - metrics.Margin + metrics.Gap) / metrics.Pitch + 1e-8), 0, 256), current.Settings.FullScreen);
        return StartMenuImportPlanner.TryPlan(detached, current.Generation, [group], metrics, budget, ImportInsertionPosition.Tail);
    }

    private static bool ValidSelection(IReadOnlyList<string>? ids) => ids is { Count: > 0 } &&
        ids.All(id => !string.IsNullOrWhiteSpace(id)) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count;
    private static WallLayoutResult Reject() => new(null, WallRejection.InvalidAction);
}
