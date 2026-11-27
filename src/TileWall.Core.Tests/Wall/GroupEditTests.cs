using TileWall.Core.Grid;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class GroupEditTests
{
    [Fact]
    public void ResizeCanMoveUnselectedMembersBetweenSelectedMembers()
    {
        static TileMember Tile(string id, int column) => new() { Id = id, LocalBounds = new(column, 0, 1, 1) };
        var group = new StartGroup { Id = "group", Members = [Tile("prefix", 0), Tile("first", 2),
            Tile("middle", 3), Tile("last", 4), Tile("suffix", 5)] };
        var sizes = new Dictionary<string, GridSize> { ["first"] = new(4, 2), ["last"] = new(4, 2) };
        var selected = sizes.Keys.ToHashSet(StringComparer.Ordinal);

        var moved = WallLayoutEngine.ResizeGroupMembers(group, sizes, selected, layoutLocked: false);

        Assert.NotNull(moved);
        Assert.Equal(new GridRect(0, 0, 1, 1), moved[0].LocalBounds);
        Assert.Equal(new GridRect(2, 0, 4, 2), moved[1].LocalBounds);
        Assert.Equal(new GridRect(6, 0, 1, 1), moved[2].LocalBounds);
        Assert.Equal(new GridRect(0, 2, 4, 2), moved[3].LocalBounds);
        Assert.Equal(new GridRect(4, 2, 1, 1), moved[4].LocalBounds);
        Assert.Null(WallLayoutEngine.ResizeGroupMembers(group, sizes, selected, layoutLocked: true));
    }
}
