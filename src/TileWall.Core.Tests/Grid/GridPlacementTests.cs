using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Grid;

/// <summary>T-PL：每个 PlacementError 的正/负例；跨栏必拒；尺寸越限（§4.4、A07）。</summary>
public class GridPlacementTests
{
    private static readonly WallGrid Wall = new(2, 9); // 16 × 9 基础格

    private static OccupancyMap EmptyOccupancy() => new(Wall);

    [Fact]
    public void Validate_ValidTile1x1_ReturnsNone()
    {
        Assert.Equal(PlacementError.None,
            GridPlacement.Validate(Wall, new GridRect(0, 0, 1, 1), ObjectKind.Tile, EmptyOccupancy()));
    }

    [Fact]
    public void Validate_OutOfWall_NegativeOriginAndOverflow()
    {
        Assert.Equal(PlacementError.OutOfWall,
            GridPlacement.Validate(Wall, new GridRect(-1, 0, 1, 1), ObjectKind.Tile, EmptyOccupancy()));
        Assert.Equal(PlacementError.OutOfWall,
            GridPlacement.Validate(Wall, new GridRect(15, 8, 1, 2), ObjectKind.Tile, EmptyOccupancy())); // 越下边
        Assert.Equal(PlacementError.OutOfWall,
            GridPlacement.Validate(Wall, new GridRect(16, 0, 1, 1), ObjectKind.Tile, EmptyOccupancy()));
        Assert.Equal(PlacementError.OutOfWall,
            GridPlacement.Validate(Wall, new GridRect(0, 0, 0, 1), ObjectKind.Tile, EmptyOccupancy())); // 零宽
    }

    [Fact]
    public void Validate_CrossesColumn_Rejected_PositiveExample_Accepted()
    {
        // Column=6, Width=3 → 基础格 6,7,8：8/8 与 6/8 不同栏 → 必拒（T-PL 固定断言）
        Assert.Equal(PlacementError.CrossesColumn,
            GridPlacement.Validate(Wall, new GridRect(6, 0, 3, 1), ObjectKind.Tile, EmptyOccupancy()));
        // 正例：同宽不跨栏
        Assert.Equal(PlacementError.None,
            GridPlacement.Validate(Wall, new GridRect(0, 0, 3, 1), ObjectKind.Tile, EmptyOccupancy()));
        Assert.Equal(PlacementError.None,
            GridPlacement.Validate(Wall, new GridRect(8, 4, 8, 2), ObjectKind.Tile, EmptyOccupancy())); // 整栏宽
    }

    [Fact]
    public void Validate_TileWidth9_AlwaysRejected()
    {
        // 宽 9 > 一栏八格（TileMaxCells）；因 9 格必跨栏，按固定次序先返回 CrossesColumn——拒绝本身是断言点。
        Assert.Equal(PlacementError.CrossesColumn,
            GridPlacement.Validate(Wall, new GridRect(0, 0, 9, 1), ObjectKind.Tile, EmptyOccupancy()));
        // TileTooWide 码由校验器独立谓词覆盖（见 ConfigValidatorTests.TileTooWide）。
    }

    [Fact]
    public void Validate_Group1Column_Rejected()
    {
        Assert.Equal(PlacementError.GroupSizeOutOfRange,
            GridPlacement.Validate(Wall, new GridRect(0, 0, 1, 4), ObjectKind.Group, EmptyOccupancy()));
    }

    [Fact]
    public void Validate_Group1Row_Rejected()
    {
        Assert.Equal(PlacementError.GroupSizeOutOfRange,
            GridPlacement.Validate(Wall, new GridRect(0, 0, 4, 1), ObjectKind.Group, EmptyOccupancy()));
    }

    [Fact]
    public void Validate_Group4x26_Rejected()
    {
        var wall = new WallGrid(2, 30);
        Assert.Equal(PlacementError.GroupSizeOutOfRange,
            GridPlacement.Validate(wall, new GridRect(0, 0, 4, 26), ObjectKind.Group, new OccupancyMap(wall)));
    }

    [Fact]
    public void Validate_Group9x4_AlwaysRejected()
    {
        // 宽 9 必跨栏 → 固定次序下返回 CrossesColumn
        Assert.Equal(PlacementError.CrossesColumn,
            GridPlacement.Validate(Wall, new GridRect(0, 0, 9, 4), ObjectKind.Group, EmptyOccupancy()));
    }

    [Fact]
    public void Validate_Group8x25_OnL4Wall_Accepted_WhenFree()
    {
        var wall = Layouts.Wall2x25;
        Assert.Equal(PlacementError.None,
            GridPlacement.Validate(wall, new GridRect(0, 0, 8, 25), ObjectKind.Group, new OccupancyMap(wall)));
    }

    [Fact]
    public void Validate_OverlapsOccupied_PositiveAndNegative()
    {
        var occupied = OccupancyMap.Build(Wall, [new GridRect(0, 0, 2, 2)]);
        Assert.Equal(PlacementError.OverlapsOccupied,
            GridPlacement.Validate(Wall, new GridRect(1, 1, 2, 2), ObjectKind.Tile, occupied));
        // 半开区间：贴边不占格 → 合法
        Assert.Equal(PlacementError.None,
            GridPlacement.Validate(Wall, new GridRect(2, 0, 1, 1), ObjectKind.Tile, occupied));
        Assert.Equal(PlacementError.None,
            GridPlacement.Validate(Wall, new GridRect(0, 2, 2, 1), ObjectKind.Tile, occupied));
    }

    [Fact]
    public void CanFitRect_EmptyWall_ReturnsRowMajorFirstFit()
    {
        var wall = Layouts.Wall2x9;
        Assert.True(GridPlacement.CanFitRect(wall, new GridSize(2, 2), new OccupancyMap(wall), out var first));
        Assert.Equal(new GridRect(0, 0, 2, 2), first);
    }

    [Fact]
    public void CanFitRect_AfterFilling_SkipsToNextFree()
    {
        var wall = Layouts.Wall2x9;
        var occupied = new OccupancyMap(wall);
        occupied.Fill(new GridRect(0, 0, 1, 1));
        Assert.True(GridPlacement.CanFitRect(wall, new GridSize(1, 1), occupied, out var first));
        Assert.Equal(new GridRect(1, 0, 1, 1), first);
    }

    [Fact]
    public void CanFitRect_NeverReturnsCrossingRect()
    {
        // 整栏 1-7 列占满，只有跨栏起点才重叠——first-fit 必须跳过跨栏起点
        var wall = Layouts.Wall2x9;
        var occupied = new OccupancyMap(wall);
        for (var row = 0; row < 9; row++)
        {
            for (var col = 1; col < 8; col++)
            {
                occupied.Fill(new GridRect(col, row, 1, 1));
            }

            for (var col = 9; col < 16; col++)
            {
                occupied.Fill(new GridRect(col, row, 1, 1));
            }
        }

        Assert.True(GridPlacement.CanFitRect(wall, new GridSize(1, 1), occupied, out var first));
        Assert.Equal(0, first.Column % 8); // 落点必在栏首
        Assert.Equal(new GridRect(0, 0, 1, 1), first);
    }

    [Fact]
    public void CanFitRect_L1EmptyWall_TrueForAllPlaceableSizes()
    {
        var wall = Layouts.Wall2x9;
        var empty = new OccupancyMap(wall);
        Assert.True(GridPlacement.CanFitRect(wall, new GridSize(1, 1), empty, out _));
        Assert.True(GridPlacement.CanFitRect(wall, new GridSize(4, 4), empty, out _));
        Assert.True(GridPlacement.CanFitRect(wall, new GridSize(8, 9), empty, out _)); // 整栏
        Assert.False(GridPlacement.CanFitRect(wall, new GridSize(8, 10), empty, out _)); // 行超墙
        Assert.False(GridPlacement.CanFitRect(wall, new GridSize(9, 2), empty, out _)); // 宽超栏
        Assert.False(GridPlacement.CanFitRect(wall, new GridSize(0, 1), empty, out _));
    }

    [Fact]
    public void CanFitRect_L2FullWall_AlwaysFalse()
    {
        var wall = Layouts.Wall2x9;
        var occupied = OccupancyMap.Build(wall, Layouts.FullOnes().Select(o => o.Bounds));
        Assert.False(GridPlacement.CanFitRect(wall, new GridSize(1, 1), occupied, out _));
        Assert.False(GridPlacement.CanFitRect(wall, new GridSize(2, 2), occupied, out _));
    }

    [Fact]
    public void CanPlaceBatch_AllOrNothing_AcceptsWhenAllFit()
    {
        var wall = Layouts.Wall2x9;
        Assert.True(GridPlacement.CanPlaceBatch(wall, [], [new GridSize(1, 1), new GridSize(2, 2), new GridSize(4, 4)]));
    }

    [Fact]
    public void CanPlaceBatch_RejectsAndDiscardsPartial_WhenAnyFails()
    {
        // 2×9 = 288 格的 1/8：改用小墙便于构造——1 栏 × 3 行（24 格），留一个 1×1 空位
        var wall = new WallGrid(1, 3);
        var existing = new List<GridRect>();
        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 8; col++)
            {
                if (col == 7 && row == 2)
                {
                    continue; // 唯一空位 (7,2)
                }

                existing.Add(new GridRect(col, row, 1, 1));
            }
        }

        // 首个 addition 恰好占掉唯一空位，第二个无处可放 → false，且调用方不得采用部分结果
        Assert.False(GridPlacement.CanPlaceBatch(wall, existing, [new GridSize(1, 1), new GridSize(2, 2)]));
        // 不放回试探：顺序不同结果相同（2×2 先来即失败）
        Assert.False(GridPlacement.CanPlaceBatch(wall, existing, [new GridSize(2, 2), new GridSize(1, 1)]));
        Assert.True(GridPlacement.CanPlaceBatch(wall, existing, [new GridSize(1, 1)]));
        Assert.False(GridPlacement.CanPlaceBatch(wall, existing, [new GridSize(1, 1), new GridSize(1, 1)]));
    }
}
