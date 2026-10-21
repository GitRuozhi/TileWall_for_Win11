using TileWall.Core.Grid;

namespace TileWall.Core.Wall;

/// <summary>Member order follows each rectangle's top-left cell, regardless of its size.</summary>
public static class MemberLayoutOrder
{
    public static IOrderedEnumerable<WallMember> Order(IEnumerable<WallMember> members) =>
        members.OrderBy(member => member.LocalBounds.Row).ThenBy(member => member.LocalBounds.Column);

    internal static GridRect? FindAppendBounds(IReadOnlyList<GridRect> existing, int width, int height, int maxRows)
    {
        var last = existing.OrderBy(bounds => bounds.Row).ThenBy(bounds => bounds.Column).LastOrDefault();
        var firstRow = existing.Count == 0 ? 0 : last.Row;
        for (var row = firstRow; row + height <= maxRows; row++)
        for (var column = 0; column + width <= 8; column++)
        {
            if (existing.Count > 0 && row == last.Row && column <= last.Column) continue;
            var candidate = new GridRect(column, row, width, height);
            if (!existing.Any(bounds => bounds.Intersects(candidate))) return candidate;
        }
        return null;
    }
}
