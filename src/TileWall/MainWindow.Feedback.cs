using TileWall.Core.Grid;
using TileWall.Core.Wall;
using TileWall.Resources;

namespace TileWall;

public sealed partial class MainWindow
{
    private void ShowResizeFeedback(WallSettings before)
    {
        var messages = new List<string>(3);
        if (before.FullScreen != Settings.FullScreen)
            messages.Add(ProductStrings.SettingUpdatedMessage("Settings.FullScreen", Settings));
        if (before.ExpandColumns != Settings.ExpandColumns)
            messages.Add(ProductStrings.SettingUpdatedMessage("Settings.ExpandColumns", Settings));
        messages.Add(string.Format(ProductStrings.Get("Wall.ResizeSettled"), Document.Columns, Document.Rows));
        ShowInteractionFeedback(string.Join(Environment.NewLine, messages));
    }

    private void ShowCommittedDropFeedback(WallLayoutResult result, WallDocument before,
        string? memberId, string? groupId)
    {
        if (result.Proposal is null || !PlacementChanged(before, Document, memberId, groupId)) return;
        var message = result.Detail switch
        {
            "LAYOUT_MOVED_NEARBY" => ProductStrings.Get("Wall.LayoutMovedNearby"),
            "LAYOUT_MOVED_GROUP_END" => ProductStrings.Get("Wall.LayoutMovedGroupEnd"),
            "LAYOUT_MOVED_WALL_END" => ProductStrings.Get("Wall.LayoutMovedWallEnd"),
            _ => ProductStrings.Get("Wall.LayoutMoveCompleted"),
        };
        ShowInteractionFeedback(message);
    }

    private void ShowInteractionFeedback(string message)
    {
        if (string.IsNullOrEmpty(message) || !AppWindow.IsVisible || _viewBlocked || _windowClosing || ShutdownStarted) return;
        if (string.Equals(_statusOverlay.Text, ProductStrings.Get("Wall.CleanupPending"), StringComparison.Ordinal)) return;
        TransientStatus(message);
    }

    private static bool PlacementChanged(WallDocument before, WallDocument after, string? memberId, string? groupId)
    {
        if (memberId is not null)
            return FindMemberPlacement(before, memberId) is { } oldMember &&
                FindMemberPlacement(after, memberId) is { } newMember && oldMember != newMember;
        if (groupId is not null)
            return FindGroupPlacement(before, groupId) is { } oldGroup &&
                FindGroupPlacement(after, groupId) is { } newGroup && oldGroup != newGroup;
        return false;
    }

    private static MemberPlacement? FindMemberPlacement(WallDocument document, string memberId)
    {
        for (var laneIndex = 0; laneIndex < document.Lanes.Count; laneIndex++)
            for (var groupIndex = 0; groupIndex < document.Lanes[laneIndex].Groups.Count; groupIndex++)
            {
                var group = document.Lanes[laneIndex].Groups[groupIndex];
                foreach (var member in group.Members)
                    if (StringComparer.Ordinal.Equals(member.Id, memberId))
                        return new(laneIndex, group.Id, member.LocalBounds);
            }
        return null;
    }

    private static GroupPlacement? FindGroupPlacement(WallDocument document, string groupId)
    {
        for (var laneIndex = 0; laneIndex < document.Lanes.Count; laneIndex++)
            for (var groupIndex = 0; groupIndex < document.Lanes[laneIndex].Groups.Count; groupIndex++)
                if (StringComparer.Ordinal.Equals(document.Lanes[laneIndex].Groups[groupIndex].Id, groupId))
                    return new(laneIndex, groupIndex);
        return null;
    }

    private readonly record struct MemberPlacement(int LaneIndex, string GroupId, GridRect Bounds);
    private readonly record struct GroupPlacement(int LaneIndex, int GroupIndex);
}
