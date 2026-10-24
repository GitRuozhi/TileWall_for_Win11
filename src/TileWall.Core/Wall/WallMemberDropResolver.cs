using TileWall.Core.Grid;

namespace TileWall.Core.Wall;

/// <summary>Resolves the pointer's group and a member's local drop position.</summary>
internal static class WallMemberDropResolver
{
    public static GroupPlacement? FindGroup(WallGeometry geometry, int lane, DipPoint pointer) =>
        geometry.Groups.FirstOrDefault(group => group.LaneIndex == lane &&
            pointer.Y >= group.Bounds.Y && pointer.Y < group.Bounds.Y + group.Bounds.Height);

    public static GridRect ResolveBounds(GroupPlacement target, DipPoint pointer, DipPoint grabOffset,
        GridSize memberSize, WallMetrics metrics)
    {
        var membersY = target.TitleBounds.Y + metrics.TitleHeight + metrics.TitleToMembersGap;
        var column = (int)Math.Round((pointer.X - grabOffset.X - target.Bounds.X) / metrics.Pitch);
        var row = (int)Math.Round((pointer.Y - grabOffset.Y - membersY) / metrics.Pitch);
        var bottom = target.Bounds.Y + target.Bounds.Height;

        // A centered grab on a one-cell tile cannot otherwise reach the next row
        // before its pointer leaves the group and activates new-group insertion.
        // Keep this strip inside the group so the actual inter-group gap is untouched.
        var edgeHeight = Math.Min(metrics.Cell / 4, bottom - membersY);
        if (pointer.Y >= bottom - edgeHeight && pointer.Y < bottom)
        {
            var occupiedRows = (int)Math.Round((bottom - membersY + metrics.Gap) / metrics.Pitch);
            row = Math.Max(row, occupiedRows);
        }

        return new GridRect(column, row, memberSize.Columns, memberSize.Rows);
    }
}
