using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Grid;

/// <summary>T-INIT：Q3 初始吸附三档基准 + 无解工作区 + 并列取小；A03 缩小判定与 MinShrink。</summary>
public class WallSizingTests
{
    // —— 初始吸附（拍板 Q3；P1 §1.2-6 三档，任务栏按 48 DIP 折算工作区）——

    [Fact]
    public void InitialForWorkArea_1366x768Screen_Is1Column5Rows()
    {
        // 1366×768 − 48 任务栏 → 工作区 1366×720；墙 872×560 = 49.65%
        Assert.Equal((1, 5), WallSizing.InitialForWorkArea(1366, 720, GridMetrics.Default));
    }

    [Fact]
    public void InitialForWorkArea_1080pScreen_Is2Columns5Rows()
    {
        // 1920×1080 − 48 → 1920×1032；墙 1720×560 = 48.6%
        Assert.Equal((2, 5), WallSizing.InitialForWorkArea(1920, 1032, GridMetrics.Default));
    }

    [Fact]
    public void InitialForWorkArea_1440pScreen_Is2Columns10Rows()
    {
        // 2560×1440 − 48 → 2560×1392；墙 1720×1080 = 52.1%
        Assert.Equal((2, 10), WallSizing.InitialForWorkArea(2560, 1392, GridMetrics.Default));
    }

    [Fact]
    public void InitialForWorkArea_TooSmallWorkArea_ReturnsZeroColumns()
    {
        // 连一栏八格都放不下 → (0,0)，UI 进入显示适配提示（设计 §4.5、A18）
        Assert.Equal((0, 0), WallSizing.InitialForWorkArea(800, 720, GridMetrics.Default));
        Assert.Equal((0, 0), WallSizing.InitialForWorkArea(1366, 100, GridMetrics.Default));
    }

    [Fact]
    public void InitialForWorkArea_EqualDiff_TakesSmallerWallArea()
    {
        // 构造：s=100,g=4,G=0,m=0 → W(n)=800n，H(r)=104r−4
        // 工作区 1600×514 → 半面积 411,200
        //   (1,4)=800×412=329,600 差 81,600；(2,3)=1600×308=492,800 差 81,600 —— 差值并列
        //   并列取墙面积较小者 → (1,4)
        var metrics = new GridMetrics { CellCore = 100, Gap = 4, ColumnGap = 0, Margin = 0 };
        Assert.Equal((1, 4), WallSizing.InitialForWorkArea(1600, 514, metrics));
    }

    [Fact]
    public void InitialForWorkArea_EqualDiffAndEqualArea_TakesEnumerationOrder()
    {
        // 构造：s=100,g=0,G=0,m=0 → 面积 = 80000·n·r
        // 工作区 1600×401 → 半面积 320,800；(1,4) 与 (2,2) 均 320,000，差均 800 —— 取枚举序 (1,4)
        var metrics = new GridMetrics { CellCore = 100, Gap = 0, ColumnGap = 0, Margin = 0 };
        Assert.Equal((1, 4), WallSizing.InitialForWorkArea(1600, 401, metrics));
    }

    // —— 手动缩小（设计 §4.2、A03）——

    [Fact]
    public void CanShrinkTo_ObjectInDroppedColumn_Rejected()
    {
        var current = Layouts.Wall2x9;
        var candidate = new WallGrid(1, 9);
        var rects = new List<GridRect> { new(8, 0, 1, 1) }; // 位于被收掉的第二栏
        Assert.False(WallSizing.CanShrinkTo(current, candidate, rects));
    }

    [Fact]
    public void CanShrinkTo_ObjectRowsBeyondCandidate_Rejected()
    {
        var current = Layouts.Wall2x9;
        var candidate = new WallGrid(2, 4);
        var rects = new List<GridRect> { new(0, 4, 2, 1) }; // 行 4 ≥ 候选行数
        Assert.False(WallSizing.CanShrinkTo(current, candidate, rects));
    }

    [Fact]
    public void CanShrinkTo_ExactlyContainingAllObjects_Accepted()
    {
        var current = Layouts.Wall2x9;
        var candidate = new WallGrid(2, 5);
        var rects = new List<GridRect> { new(0, 0, 4, 2), new(15, 4, 1, 1) }; // 最右列 15、最底行 4 恰在界内
        Assert.True(WallSizing.CanShrinkTo(current, candidate, rects));
    }

    [Fact]
    public void CanShrinkTo_EmptyWall_AcceptsDownTo1x1()
    {
        Assert.True(WallSizing.CanShrinkTo(Layouts.Wall2x9, new WallGrid(1, 1), []));
        Assert.False(WallSizing.CanShrinkTo(Layouts.Wall2x9, new WallGrid(0, 9), []));
        Assert.False(WallSizing.CanShrinkTo(Layouts.Wall2x9, new WallGrid(3, 9), [])); // 扩大不是缩小
    }

    [Fact]
    public void MinShrink_CollapsesToRightmostOccupiedColumnAndBottomRow()
    {
        var current = Layouts.Wall2x9;
        var rects = new List<GridRect> { new(0, 0, 2, 2), new(8, 3, 3, 1) }; // 最右基础格 10 → 2 栏；最底行 3
        var min = WallSizing.MinShrink(current, rects);
        Assert.Equal(new WallGrid(2, 4), min);
        Assert.True(WallSizing.CanShrinkTo(current, min, rects)); // MinShrink 结果必可通过 CanShrinkTo
    }

    [Fact]
    public void MinShrink_ObjectAtFarCorner_StaysAtCurrentSize()
    {
        var current = Layouts.Wall2x9;
        var rects = new List<GridRect> { new(15, 8, 1, 1) };
        Assert.Equal(new WallGrid(2, 9), WallSizing.MinShrink(current, rects));
    }

    [Fact]
    public void MinShrink_EmptyOrSingleColumnContent()
    {
        Assert.Equal(new WallGrid(1, 1), WallSizing.MinShrink(Layouts.Wall2x9, []));
        Assert.Equal(new WallGrid(1, 3), WallSizing.MinShrink(Layouts.Wall2x9, [new GridRect(7, 2, 1, 1)]));
    }
}
