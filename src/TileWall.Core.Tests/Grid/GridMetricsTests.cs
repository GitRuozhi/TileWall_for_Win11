using TileWall.Core.Grid;
using Xunit;

namespace TileWall.Core.Tests.Grid;

/// <summary>T-GM：GridMetrics 公式（设计 §3.3 五断言）+ 容量上限边界 + 自定义 metrics 自洽。</summary>
public class GridMetricsTests
{
    private static readonly GridMetrics M = GridMetrics.Default;

    [Fact]
    public void WallWidth_OneColumn_Is872()
    {
        Assert.Equal(872, M.WallWidth(1)); // 48 + 824
    }

    [Fact]
    public void WallWidth_TwoColumns_Is1720()
    {
        Assert.Equal(1720, M.WallWidth(2)); // 48 + 1648 + 24
    }

    [Fact]
    public void FourByFourGroup_DipSize_Is408()
    {
        Assert.Equal(408, M.WidthOf(4)); // 4×96 + 3×8
        Assert.Equal(408, M.HeightOf(4));
    }

    [Fact]
    public void WallHeight_NineRows_Is976_TenRows_Is1080()
    {
        Assert.Equal(976, M.WallHeight(9));   // 40 + 104×9 ≤ 1032
        Assert.Equal(1080, M.WallHeight(10)); // 1032 < 1080 → 1080p 最多 9 行
    }

    [Fact]
    public void MaxRowsFor_1080pWorkArea_Is9()
    {
        Assert.Equal(9, M.MaxRowsFor(1032));
    }

    [Fact]
    public void MaxRowsFor_1440pWorkArea_Is13()
    {
        Assert.Equal(13, M.MaxRowsFor(1392)); // (1392−40)/104 = 13
    }

    [Fact]
    public void MaxColumnsFor_1366_Is1()
    {
        Assert.Equal(1, M.MaxColumnsFor(1366)); // 872 ≤ 1366 < 1720
    }

    [Theory]
    [InlineData(1920, 2)]
    [InlineData(2560, 2)] // m=24 时 2560 容不下 3 栏（需 2568，P1 §1.2-4）
    [InlineData(871, 0)]  // 一栏都放不下 → 0
    [InlineData(872, 1)]  // 恰好放下 → 1
    public void MaxColumnsFor_Boundaries(double workWidth, int expected)
    {
        Assert.Equal(expected, M.MaxColumnsFor(workWidth));
    }

    [Theory]
    [InlineData(1032, 9)]
    [InlineData(976, 9)]  // 恰好
    [InlineData(975, 8)]
    [InlineData(48, 0)]   // 恰好只有两条边距 → 0
    [InlineData(47, 0)]
    public void MaxRowsFor_Boundaries(double workHeight, int expected)
    {
        Assert.Equal(expected, M.MaxRowsFor(workHeight));
    }

    [Fact]
    public void OriginConversion_CellX_CellY()
    {
        Assert.Equal(24, M.CellX(0));
        Assert.Equal(128, M.CellX(1));    // 24 + 104
        Assert.Equal(872, M.CellX(8));    // 24 + (824+24)——第二栏起点
        Assert.Equal(24, M.CellY(0));
        Assert.Equal(336, M.CellY(3));    // 24 + 3×104
        Assert.Equal(new DipPoint(872, 24), M.OriginOf(new GridRect(8, 0, 1, 1)));
    }

    [Fact]
    public void ReverseConversion_HitCores_MissGapsAndMargins()
    {
        Assert.Equal(0, M.ColumnFromX(24));        // 第 0 格芯起点（半开区间含左端）
        Assert.Equal(0, M.ColumnFromX(24 + 95.5));
        Assert.Equal(-1, M.ColumnFromX(24 + 96));  // 格间隙
        Assert.Equal(1, M.ColumnFromX(24 + 104));
        Assert.Equal(8, M.ColumnFromX(872));       // 第二栏首格（= 24 + 848）
        Assert.Equal(8, M.ColumnFromX(24 + 848));  // 栏间隙右端 = 第二栏首格芯起点
        Assert.Equal(-1, M.ColumnFromX(24 + 836)); // 栏间隙中段
        Assert.Equal(-1, M.ColumnFromX(24 + 824)); // 末格芯右端点（半开区间不含右端）→ 栏间隙
        Assert.Equal(-1, M.ColumnFromX(0));        // 左边距
        Assert.Equal(-1, M.ColumnFromX(double.NaN));
        Assert.Equal(-1, M.ColumnFromX(-5));

        Assert.Equal(0, M.RowFromY(24));
        Assert.Equal(3, M.RowFromY(24 + (3 * 104)));
        Assert.Equal(-1, M.RowFromY(24 + 96)); // 格间隙
        Assert.Equal(-1, M.RowFromY(0));
    }

    [Fact]
    public void ReverseConversion_RoundTrip_AllCells()
    {
        for (var column = 0; column < 16; column++)
        {
            Assert.Equal(column, M.ColumnFromX(M.CellX(column)));
        }

        for (var row = 0; row < 20; row++)
        {
            Assert.Equal(row, M.RowFromY(M.CellY(row)));
        }
    }

    [Fact]
    public void CustomMetrics_s80_FormulasRemainSelfConsistent()
    {
        var m = new GridMetrics { CellCore = 80, Gap = 8, ColumnGap = 24, Margin = 16 };
        Assert.Equal(88, m.Pitch);
        Assert.Equal(8 * 80 + 7 * 8, m.ColumnWidth);            // 696
        Assert.Equal(32 + 696, m.WallWidth(1));                 // 2m + 栏宽 = 728
        Assert.Equal(m.WallWidth(1) + m.ColumnWidth + m.ColumnGap, m.WallWidth(2)); // 1448
        Assert.Equal(m.Margin + (m.ColumnWidth + m.ColumnGap), m.CellX(8));
        Assert.Equal(m.Margin + 88, m.CellY(1));
        for (var n = 1; n <= 4; n++)
        {
            Assert.True(m.WallWidth(n) <= 3000);
        }
    }

    [Fact]
    public void RectSize_HalfOpen_WidthOfOneCell_IsCellCore()
    {
        Assert.Equal(96, M.WidthOf(1));
        Assert.Equal((2 * 96) + 8, M.WidthOf(2)); // 2×s + (2−1)×g = 200
    }
}
