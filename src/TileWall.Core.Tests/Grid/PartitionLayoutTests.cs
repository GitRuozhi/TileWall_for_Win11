using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Grid;

/// <summary>
/// T-PINV（M5 设计 §3；INV-P1–P5）：FullGrid 工厂、Edges() 与手排剖分互推、Check() 负例逐码。
/// </summary>
public sealed class PartitionLayoutTests
{
    // ————————————————————————————— FullGrid 工厂（B02 基准） —————————————————————————————

    [Fact]
    public void FullGrid_FourByFour_IsSixteenUnitBlocks()
    {
        var layout = PartitionLayout.FullGrid(new GridSize(4, 4));

        Assert.Equal(16, layout.PartitionCount);
        Assert.All(layout.Partitions, p => Assert.Equal(new GridSize(1, 1), p.Size));
        var expected = new List<GridRect>();
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                expected.Add(new GridRect(col, row, 1, 1));
            }
        }

        Assert.True(expected.SequenceEqual(layout.Partitions), "FullGrid 应为行优先 1×1 序列");
        PartitionAssert.AssertValid(layout, "FullGrid 4×4");
    }

    [Fact]
    public void FullGrid_PartitionAt_RoundTripsEveryCell()
    {
        var layout = PartitionLayout.FullGrid(new GridSize(3, 2));
        for (var row = 0; row < 2; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                Assert.Equal(new GridRect(col, row, 1, 1), layout.PartitionAt(col, row));
            }
        }
    }

    [Fact]
    public void RecordEquality_IsValueBased()
    {
        var a = PartitionLayout.FullGrid(new GridSize(2, 2));
        var b = PartitionLayout.FullGrid(new GridSize(2, 2));
        var single = new PartitionLayout(new GridSize(2, 2), [new(0, 0, 2, 2)]);
        var viaVertical = PartitionOps.DrawWall(single, new WallEdge(true, 1, 0)).Layout!;
        var viaHorizontal = PartitionOps.DrawWall(single, new WallEdge(false, 0, 1)).Layout!;

        Assert.True(a.Equals(b), "同输入构造的布局应 record 值相等（撤销快照判等基础）");
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.False(single.Equals(viaVertical), "不同剖分布局不得值相等");
        Assert.NotEqual(single.GetHashCode(), viaVertical.GetHashCode());
        Assert.False(viaVertical.Equals(viaHorizontal), "横切与竖切结果不同");
    }

    // ————————————————————————————— Edges() ↔ 剖分互推（12 种手排剖分） —————————————————————————————

    public static TheoryData<string, GridSize, GridRect[], WallEdge[]> HandPartitions() => new()
    {
        // 1. 2×2 全 1×1：全部四条内部边
        { "2x2 全1x1", new GridSize(2, 2),
          [new(0, 0, 1, 1), new(1, 0, 1, 1), new(0, 1, 1, 1), new(1, 1, 1, 1)],
          [new(true, 1, 0), new(true, 1, 1), new(false, 0, 1), new(false, 1, 1)] },
        // 2. 2×2 单块：无内部边
        { "2x2 单块", new GridSize(2, 2), [new(0, 0, 2, 2)], [] },
        // 3. 2×2 两块 1×2 竖条
        { "2x2 双竖条", new GridSize(2, 2), [new(0, 0, 1, 2), new(1, 0, 1, 2)],
          [new(true, 1, 0), new(true, 1, 1)] },
        // 4. 2×2 两块 2×1 横条
        { "2x2 双横条", new GridSize(2, 2), [new(0, 0, 2, 1), new(0, 1, 2, 1)],
          [new(false, 0, 1), new(false, 1, 1)] },
        // 5. 2×2 混合：左列两块 + 右列一块
        { "2x2 左二右一", new GridSize(2, 2), [new(0, 0, 1, 1), new(0, 1, 1, 1), new(1, 0, 1, 2)],
          [new(true, 1, 0), new(true, 1, 1), new(false, 0, 1)] },
        // 6. 3×1 单块
        { "3x1 单块", new GridSize(3, 1), [new(0, 0, 3, 1)], [] },
        // 7. 3×3 左列 + 右 2×3
        { "3x3 左列右块", new GridSize(3, 3), [new(0, 0, 1, 3), new(1, 0, 2, 3)],
          [new(true, 1, 0), new(true, 1, 1), new(true, 1, 2)] },
        // 8. 3×3 顶条 + 左下 1×2 + 右下 2×2
        { "3x3 顶加下二", new GridSize(3, 3), [new(0, 0, 3, 1), new(0, 1, 1, 2), new(1, 1, 2, 2)],
          [new(false, 0, 1), new(false, 1, 1), new(false, 2, 1), new(true, 1, 1), new(true, 1, 2)] },
        // 9. 4×2 三竖条（块间墙在 x=2 与 x=3；x=1 在首块内部）
        { "4x2 三竖条", new GridSize(4, 2), [new(0, 0, 2, 2), new(2, 0, 1, 2), new(3, 0, 1, 2)],
          [new(true, 2, 0), new(true, 2, 1), new(true, 3, 0), new(true, 3, 1)] },
        // 10. 4×2 顶条 + 底二横块
        { "4x2 顶条下二", new GridSize(4, 2), [new(0, 0, 4, 1), new(0, 1, 2, 1), new(2, 1, 2, 1)],
          [new(false, 0, 1), new(false, 1, 1), new(false, 2, 1), new(false, 3, 1), new(true, 2, 1)] },
        // 11. 2×3 三横条
        { "2x3 三横条", new GridSize(2, 3), [new(0, 0, 2, 1), new(0, 1, 2, 1), new(0, 2, 2, 1)],
          [new(false, 0, 1), new(false, 1, 1), new(false, 0, 2), new(false, 1, 2)] },
        // 12. 3×3 全 1×1：12 条内部边
        { "3x3 全1x1", new GridSize(3, 3),
          [new(0, 0, 1, 1), new(1, 0, 1, 1), new(2, 0, 1, 1), new(0, 1, 1, 1), new(1, 1, 1, 1), new(2, 1, 1, 1),
           new(0, 2, 1, 1), new(1, 2, 1, 1), new(2, 2, 1, 1)],
          [new(true, 1, 0), new(true, 2, 0), new(true, 1, 1), new(true, 2, 1), new(true, 1, 2), new(true, 2, 2),
           new(false, 0, 1), new(false, 1, 1), new(false, 2, 1), new(false, 0, 2), new(false, 1, 2), new(false, 2, 2)] },
    };

    [Theory]
    [MemberData(nameof(HandPartitions))]
    public void Edges_MatchHandDerivedPartitionViews(string label, GridSize size, GridRect[] partitions, WallEdge[] expectedEdges)
    {
        var layout = new PartitionLayout(size, partitions);

        PartitionAssert.AssertValid(layout, $"手排剖分[{label}]");
        var actual = OrderEdges(layout.Edges());
        var expected = OrderEdges(expectedEdges);
        Assert.Equal(expected, actual); // 集合逐条相等（WallEdge record 值语义 + 规范序）
    }

    [Theory]
    [MemberData(nameof(HandPartitions))]
    public void IsSolid_IsConsistentWithEdges(string label, GridSize size, GridRect[] partitions, WallEdge[] expectedEdges)
    {
        var layout = new PartitionLayout(size, partitions);
        var solid = layout.Edges().ToHashSet();
        var expected = expectedEdges.ToHashSet();

        // 双重核对：IsSolid ↔ Edges() 互推，且与手排期望集合逐条一致
        foreach (var edge in AllInternalEdges(size))
        {
            Assert.True(solid.Contains(edge) == layout.IsSolid(edge), $"[{label}] IsSolid 与 Edges() 不一致：{edge}");
            Assert.True(solid.Contains(edge) == expected.Contains(edge), $"[{label}] Edges() 与手排期望不一致：{edge}");
        }
    }

    [Fact]
    public void FullGridEdges_CountMatchesInteriorBoundaryTotal()
    {
        var layout = PartitionLayout.FullGrid(new GridSize(4, 4));
        Assert.Equal((4 - 1) * 4 + 4 * (4 - 1), layout.Edges().Count()); // 3×4 竖 + 4×3 横 = 24
    }

    // ————————————————————————————— Check() 负例（逐码） —————————————————————————————

    [Fact]
    public void Check_ReportsOverlapOnly_ForOverlappingPartitionsWithFullCover()
    {
        var layout = new PartitionLayout(new GridSize(2, 2), [new(0, 0, 2, 2), new(0, 0, 1, 1)]);
        var check = layout.Check();

        Assert.False(check.IsValid);
        Assert.True(check.Contains(PartitionErrorCodes.Overlap), "应报 PARTITION_OVERLAP");
        Assert.False(check.Contains(PartitionErrorCodes.Cover), "完全覆盖时不应误报 COVER");
    }

    [Fact]
    public void Check_ReportsCoverOnly_ForMissingCornerWithoutHole()
    {
        // 缺角 (0,0)：行 0 无左侧覆盖、列 0 无上方覆盖 → 非孔洞，仅覆盖缺口
        var layout = new PartitionLayout(new GridSize(2, 2), [new(1, 0, 1, 1), new(0, 1, 1, 1), new(1, 1, 1, 1)]);
        var check = layout.Check();

        Assert.True(check.Contains(PartitionErrorCodes.Cover), "应报 PARTITION_COVER");
        Assert.False(check.Contains(PartitionErrorCodes.Hole), "缺角非孔洞");
        Assert.False(check.Contains(PartitionErrorCodes.Overlap));
    }

    [Fact]
    public void Check_ReportsHole_AndCover_ForInteriorGap()
    {
        // 中格 (1,1) 缺失：行 1 左右均有覆盖、列 1 上下均有覆盖 → COVER + HOLE
        var layout = new PartitionLayout(
            new GridSize(3, 3),
            [new(0, 0, 3, 1), new(0, 2, 3, 1), new(0, 1, 1, 1), new(2, 1, 1, 1)]);
        var check = layout.Check();

        Assert.True(check.Contains(PartitionErrorCodes.Cover));
        Assert.True(check.Contains(PartitionErrorCodes.Hole), "内部洞两侧有覆盖格 → PARTITION_HOLE");
    }

    [Fact]
    public void Check_ReportsOutOfBounds_ForOverrunNegativeAndZeroSized()
    {
        var overrun = new PartitionLayout(new GridSize(2, 2), [new(0, 0, 3, 1)]);
        Assert.True(overrun.Check().Contains(PartitionErrorCodes.OutOfBounds));

        var negative = new PartitionLayout(new GridSize(2, 2), [new(-1, 0, 1, 1)]);
        Assert.True(negative.Check().Contains(PartitionErrorCodes.OutOfBounds));

        var zeroWidth = new PartitionLayout(new GridSize(2, 2), [new(0, 0, 0, 1)]);
        Assert.True(zeroWidth.Check().Contains(PartitionErrorCodes.OutOfBounds));
    }

    [Fact]
    public void PartitionAt_ThrowsOnOutOfRangeAndHoleCells()
    {
        var layout = PartitionLayout.FullGrid(new GridSize(2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.PartitionAt(2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.PartitionAt(0, -1));

        var holed = new PartitionLayout(new GridSize(3, 3),
            [new(0, 0, 3, 1), new(0, 2, 3, 1), new(0, 1, 1, 1), new(2, 1, 1, 1)]);
        Assert.Throws<InvalidOperationException>(() => holed.PartitionAt(1, 1));
    }

    [Fact]
    public void IsSolid_ThrowsOutsideEdgeDomain()
    {
        var layout = PartitionLayout.FullGrid(new GridSize(2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.IsSolid(new WallEdge(true, 0, 0))); // 外框从不可操作
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.IsSolid(new WallEdge(false, 0, 0)));
    }

    private static IEnumerable<WallEdge> AllInternalEdges(GridSize size)
    {
        for (var y = 0; y < size.Rows; y++)
        {
            for (var x = 1; x < size.Columns; x++)
            {
                yield return new WallEdge(true, x, y);
            }
        }

        for (var y = 1; y < size.Rows; y++)
        {
            for (var x = 0; x < size.Columns; x++)
            {
                yield return new WallEdge(false, x, y);
            }
        }
    }

    /// <summary>边集规范序（WallEdge 非可比较类型，自定键排序后逐条比较）。</summary>
    internal static WallEdge[] OrderEdges(IEnumerable<WallEdge> edges) =>
        [.. edges.OrderBy(e => e.IsVertical).ThenBy(e => e.X).ThenBy(e => e.Y)];
}
