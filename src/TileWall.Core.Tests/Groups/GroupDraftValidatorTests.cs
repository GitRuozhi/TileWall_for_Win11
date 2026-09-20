using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Groups;
using Xunit;

namespace TileWall.Core.Tests.Groups;

/// <summary>
/// T-GDRAFT（M5 设计 §8.1）：创建态 firstFit / 满墙 GROUP_NO_FIT（A17）；编辑态锚定扩大三错误码；
/// 缩小恒合法；分区负例透传 PARTITION_*；尺寸越界 GROUP_SIZE_OUT_OF_RANGE。
/// </summary>
public sealed class GroupDraftValidatorTests
{
    private static readonly WallGrid Wall = new(2, 9); // 16 列 × 9 行

    [Fact]
    public void Create_FirstFit_SuggestsRowMajorSlot()
    {
        var draft = Draft(4, 4);

        var validation = GroupDraftValidator.Validate(draft, current: null, Wall, []);

        Assert.True(validation.IsValid);
        Assert.Equal(new GridRect(0, 0, 4, 4), validation.SuggestedRect);
    }

    [Fact]
    public void Create_FirstFit_SkipsOccupiedAndColumnBoundary()
    {
        // (0,0,2,2) 被占 → 4×4 放 (2,0)（同竖栏内、行优先）
        var draft = Draft(4, 4);
        var others = new[] { new GridRect(0, 0, 2, 2) };

        var validation = GroupDraftValidator.Validate(draft, null, Wall, others);

        Assert.True(validation.IsValid);
        Assert.Equal(new GridRect(2, 0, 4, 4), validation.SuggestedRect);
    }

    [Fact]
    public void Create_FullWall_ReturnsGroupNoFit_A17()
    {
        // 满墙：64 个 1×1 独立磁贴
        var others = new List<GridRect>();
        for (var row = 0; row < Wall.Rows; row++)
        {
            for (var col = 0; col < Wall.CellColumns; col++)
            {
                others.Add(new GridRect(col, row, 1, 1));
            }
        }

        var validation = GroupDraftValidator.Validate(Draft(4, 4), null, Wall, others);

        Assert.False(validation.IsValid);
        Assert.Contains(GroupDraftValidator.NoFit, validation.Errors); // 满墙不能借菜单绕过容量上限
        Assert.Null(validation.SuggestedRect);
    }

    [Fact]
    public void Edit_AnchoredEnlarge_ReportsPlaceConflictOnNeighbor()
    {
        var current = CurrentGroup(new GridRect(0, 0, 4, 4));
        var others = new[] { new GridRect(4, 0, 2, 2) };
        var draft = Draft(5, 4); // 锚定 (0,0) → (0,0,5,4) 撞 (4,0,2,2)

        var validation = GroupDraftValidator.Validate(draft, current, Wall, others);

        Assert.Contains(GroupDraftValidator.PlaceConflict, validation.Errors);
    }

    [Fact]
    public void Edit_AnchoredEnlarge_ReportsCrossesColumn()
    {
        // 锚定 (4,0) 扩到 5 宽 → 列 4–8 跨 8 边界（设计 §15.3 备案：贴界扩大被阻，用户需先拖组）
        var current = CurrentGroup(new GridRect(4, 0, 4, 4));
        var draft = Draft(5, 4);

        var validation = GroupDraftValidator.Validate(draft, current, Wall, []);

        Assert.Contains(GroupDraftValidator.PlaceCrossesColumn, validation.Errors);
    }

    [Fact]
    public void Edit_AnchoredEnlarge_ReportsOutOfWall()
    {
        var current = CurrentGroup(new GridRect(0, 5, 4, 4)); // 底部行 5–8
        var draft = Draft(4, 5); // 锚定 (0,5) → 底 10 > 9

        var validation = GroupDraftValidator.Validate(draft, current, Wall, []);

        Assert.Contains(GroupDraftValidator.PlaceOutOfWall, validation.Errors);
    }

    [Fact]
    public void Edit_Shrink_IsAlwaysLegal()
    {
        var current = CurrentGroup(new GridRect(0, 0, 4, 4));
        var others = new[] { new GridRect(4, 0, 4, 9), new GridRect(0, 4, 4, 5) };
        var draft = Draft(2, 2); // 足迹只减不增 → 恒合法

        var validation = GroupDraftValidator.Validate(draft, current, Wall, others);

        Assert.True(validation.IsValid, string.Join(",", validation.Errors));
        Assert.Equal(new GridRect(0, 0, 2, 2), validation.SuggestedRect);
    }

    [Fact]
    public void PartitionViolations_ArePassedThrough_WithDiskGateCodes()
    {
        var draft = new GroupEditDraft
        {
            Size = new GridSize(4, 4),
            Partitions = [new GridRect(0, 0, 4, 4), new GridRect(0, 0, 2, 2)], // 重叠
        };

        var validation = GroupDraftValidator.Validate(draft, null, Wall, []);

        Assert.Contains("PARTITION_OVERLAP", validation.Errors);
        Assert.DoesNotContain(GroupDraftValidator.NoFit, validation.Errors); // 分区错时不给误导性放置码
    }

    [Fact]
    public void SizeOutOfRange_IsReported()
    {
        var validation = GroupDraftValidator.Validate(Draft(9, 4), null, Wall, []);

        Assert.Contains(GroupDraftValidator.SizeOutOfRange, validation.Errors);
    }

    [Fact]
    public void InvalidBackdropColor_IsReported()
    {
        var draft = Draft(4, 4) with { Backdrop = BackdropKind.SolidColor, BackdropColorHex = "red" };

        var validation = GroupDraftValidator.Validate(draft, null, Wall, []);

        Assert.Contains(DraftValidator.BackgroundColorInvalid, validation.Errors);
    }

    [Fact]
    public void ValidSolidColor_Passes()
    {
        var draft = Draft(4, 4) with { Backdrop = BackdropKind.SolidColor, BackdropColorHex = "#1A2B3C" };

        var validation = GroupDraftValidator.Validate(draft, null, Wall, []);

        Assert.True(validation.IsValid, string.Join(",", validation.Errors));
    }

    private static GroupEditDraft Draft(int columns, int rows)
    {
        var partitions = new List<GridRect>(columns * rows);
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                partitions.Add(new GridRect(col, row, 1, 1));
            }
        }

        return new GroupEditDraft { Size = new GridSize(columns, rows), Partitions = partitions };
    }

    private static GroupObject CurrentGroup(GridRect bounds)
    {
        var partitions = new List<GridRect>(bounds.Width * bounds.Height);
        for (var row = 0; row < bounds.Height; row++)
        {
            for (var col = 0; col < bounds.Width; col++)
            {
                partitions.Add(new GridRect(col, row, 1, 1));
            }
        }

        return new GroupObject { Id = "g-cur", Bounds = bounds, Partitions = partitions };
    }
}
