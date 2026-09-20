using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Grid;

/// <summary>
/// T-DRAW / T-ERASE（M5 设计 §4–§5；B03/B04/B05）+ T-RESIZE（§7；B09/INV-P8/P9）。
/// 设计 §8.3 例证逐字成为测试基准；每次操作后断言 INV-P1–P5（PartitionAssert.AssertValid 内含）。
/// </summary>
public sealed class PartitionOpsTests
{
    // ————————————————————————————— 画墙 W-1（B03） —————————————————————————————

    [Fact]
    public void DrawWall_VerticalMidlineOf2x2_SplitsIntoTwo1x2_Columns()
    {
        var before = Mixed4x4With2x2Block();
        var after = PartitionOps.DrawWall(before, new WallEdge(true, 1, 0)).Layout!;

        PartitionAssert.DrawInvariants(before, after);
        Assert.Equal(14, after.PartitionCount);
        Assert.Contains(new GridRect(0, 0, 1, 2), after.Partitions);
        Assert.Contains(new GridRect(1, 0, 1, 2), after.Partitions);
        Assert.DoesNotContain(new GridRect(0, 0, 2, 2), after.Partitions);
        // 新线贯穿 p 整条高：Edges 恰新增 V(1,0) 与 V(1,1)
        var newEdges = PartitionLayoutTests.OrderEdges(after.Edges().Except(before.Edges()));
        Assert.Equal(PartitionLayoutTests.OrderEdges([new WallEdge(true, 1, 0), new WallEdge(true, 1, 1)]), newEdges);
    }

    [Fact]
    public void DrawWall_VerticalEdgeFromLowerCell_SameFullHeightLine()
    {
        var before = Mixed4x4With2x2Block();
        var viaLowerCell = PartitionOps.DrawWall(before, new WallEdge(true, 1, 1)).Layout!;

        PartitionAssert.DrawInvariants(before, viaLowerCell);
        var viaUpperCell = PartitionOps.DrawWall(before, new WallEdge(true, 1, 0)).Layout!;
        Assert.True(viaUpperCell.Equals(viaLowerCell), "同一线上的两个格触发的画墙结果应一致（线贯穿整条高）");
    }

    [Fact]
    public void DrawWall_HorizontalMidlineOf2x2_SplitsIntoTwo2x1_Rows()
    {
        var before = Mixed4x4With2x2Block();
        var after = PartitionOps.DrawWall(before, new WallEdge(false, 0, 1)).Layout!;

        PartitionAssert.DrawInvariants(before, after);
        Assert.Equal(14, after.PartitionCount);
        Assert.Contains(new GridRect(0, 0, 2, 1), after.Partitions);
        Assert.Contains(new GridRect(0, 1, 2, 1), after.Partitions);
        var newEdges = PartitionLayoutTests.OrderEdges(after.Edges().Except(before.Edges()));
        Assert.Equal(PartitionLayoutTests.OrderEdges([new WallEdge(false, 0, 1), new WallEdge(false, 1, 1)]), newEdges);
    }

    [Fact]
    public void DrawWall_OnSolidEdge_IsIgnoredAndLayoutUnchanged()
    {
        var drawn = PartitionOps.DrawWall(Mixed4x4With2x2Block(), new WallEdge(true, 1, 0)).Layout!;

        var fromUpperCell = PartitionOps.DrawWall(drawn, new WallEdge(true, 1, 0)); // 现已为实线
        Assert.Equal(PartitionOpStatus.SolidForDraw, fromUpperCell.Status);
        Assert.Null(fromUpperCell.Layout);

        var fromLowerCell = PartitionOps.DrawWall(drawn, new WallEdge(true, 1, 1));
        Assert.Equal(PartitionOpStatus.SolidForDraw, fromLowerCell.Status);
    }

    [Fact]
    public void DrawWall_OutOfDomainEdges_ReturnOutOfRange()
    {
        var layout = Mixed4x4With2x2Block();
        Assert.Equal(PartitionOpStatus.OutOfRange, PartitionOps.DrawWall(layout, new WallEdge(true, 0, 0)).Status); // 外框
        Assert.Equal(PartitionOpStatus.OutOfRange, PartitionOps.DrawWall(layout, new WallEdge(true, 4, 0)).Status); // 右外框
        Assert.Equal(PartitionOpStatus.OutOfRange, PartitionOps.DrawWall(layout, new WallEdge(false, 0, 0)).Status); // 顶外框
        Assert.Equal(PartitionOpStatus.OutOfRange, PartitionOps.DrawWall(layout, new WallEdge(false, 0, 4)).Status); // 底外框
        Assert.Equal(PartitionOpStatus.OutOfRange, PartitionOps.DrawWall(layout, new WallEdge(true, 1, 4)).Status);  // Y 越界
    }

    [Fact]
    public void DrawWall_ParallelLinesIn1xN_Block_ApplicableColumnByColumn()
    {
        // 3 宽块：V(1,·) 与 V(2,·) 逐一可画；每次只开一条线
        var before = new PartitionLayout(new GridSize(4, 2), [new(0, 0, 3, 2), new(3, 0, 1, 2)]);

        var step1 = PartitionOps.DrawWall(before, new WallEdge(true, 1, 0)).Layout!;
        PartitionAssert.DrawInvariants(before, step1);
        Assert.Contains(new GridRect(0, 0, 1, 2), step1.Partitions);
        Assert.Contains(new GridRect(1, 0, 2, 2), step1.Partitions);

        var step2 = PartitionOps.DrawWall(step1, new WallEdge(true, 2, 1)).Layout!;
        PartitionAssert.DrawInvariants(step1, step2);
        Assert.Contains(new GridRect(1, 0, 1, 2), step2.Partitions);
        Assert.Contains(new GridRect(2, 0, 1, 2), step2.Partitions);
        Assert.Equal(4, step2.PartitionCount);
    }

    [Fact]
    public void DrawWall_OtherPartitionsUnchangedCellByCell_B03()
    {
        var before = Mixed4x4With2x2Block();
        var after = PartitionOps.DrawWall(before, new WallEdge(true, 1, 0)).Layout!;

        var untouched = before.Partitions.Where(p => p != new GridRect(0, 0, 2, 2)).ToList();
        foreach (var p in untouched)
        {
            Assert.Contains(p, after.Partitions); // 其余分区逐格不变
        }
    }

    // ————————————————————————————— 拆墙 D-1（§8.3 例证；B04/B05） —————————————————————————————

    [Fact]
    public void EraseWall_DesignExample_TwoByOnePlusTwoOnes_MergesTo2x2()
    {
        // 设计 §8.3：上方 2×1 + 下方两块 1×1，拆 H(0,1)：R=BB=2×2 → 切穿右下 → 纳入 → 收敛 → 一块 2×2
        var before = new PartitionLayout(
            new GridSize(2, 2), [new(0, 0, 2, 1), new(0, 1, 1, 1), new(1, 1, 1, 1)]);
        var after = PartitionOps.EraseWall(before, new WallEdge(false, 0, 1)).Layout!;

        PartitionAssert.EraseInvariants(before, after, new GridRect(0, 0, 2, 2));
        Assert.Single(after.Partitions);
        Assert.Equal(new GridRect(0, 0, 2, 2), after.Partitions[0]);
    }

    [Fact]
    public void EraseWall_MinimalGrowth_LeavesUnrelatedPartitionsIntact()
    {
        // 3×2：顶 3×1 + 底三块 1×1。拆底内 V(1,1)：R=2×1 不触及顶行（最小性：不过度合并）
        var before = new PartitionLayout(
            new GridSize(3, 2), [new(0, 0, 3, 1), new(0, 1, 1, 1), new(1, 1, 1, 1), new(2, 1, 1, 1)]);
        var after = PartitionOps.EraseWall(before, new WallEdge(true, 1, 1)).Layout!;

        PartitionAssert.EraseInvariants(before, after, new GridRect(0, 1, 2, 1));
        Assert.Equal(3, after.PartitionCount);
        Assert.Contains(new GridRect(0, 0, 3, 1), after.Partitions); // 顶行原样
        Assert.Contains(new GridRect(2, 1, 1, 1), after.Partitions); // 右下原样

        // 再拆 H(0,1)：R=BB(顶,左下 2×1)=3×2 → 切穿右下 → 纳入 → 全并
        var final = PartitionOps.EraseWall(after, new WallEdge(false, 0, 1)).Layout!;
        PartitionAssert.EraseInvariants(after, final, new GridRect(0, 0, 3, 2));
        Assert.Single(final.Partitions);
    }

    [Fact]
    public void EraseWall_OnDashedEdge_IsIgnoredAndLayoutUnchanged()
    {
        var layout = new PartitionLayout(new GridSize(2, 2), [new(0, 0, 2, 2)]);

        var result = PartitionOps.EraseWall(layout, new WallEdge(true, 1, 0)); // 单块内部无墙
        Assert.Equal(PartitionOpStatus.DashedForErase, result.Status);
        Assert.Null(result.Layout);
    }

    [Fact]
    public void EraseWall_OutOfDomain_ReturnsOutOfRange()
    {
        var layout = new PartitionLayout(new GridSize(2, 2), [new(0, 0, 2, 1), new(0, 1, 1, 1), new(1, 1, 1, 1)]);
        Assert.Equal(PartitionOpStatus.OutOfRange, PartitionOps.EraseWall(layout, new WallEdge(true, 2, 0)).Status);
        Assert.Equal(PartitionOpStatus.OutOfRange, PartitionOps.EraseWall(layout, new WallEdge(false, 0, 2)).Status);
        Assert.Equal(PartitionOpStatus.OutOfRange, PartitionOps.EraseWall(layout, new WallEdge(true, 0, 0)).Status);
    }

    [Fact]
    public void EraseWall_ResultStaysWithinBounds_AtRightEdge()
    {
        // R ⊆ Bounds：拆 V(1,0) 合并顶行两格为 2×1，不波及底行与右列
        var before = new PartitionLayout(
            new GridSize(3, 2),
            [new(0, 0, 1, 1), new(1, 0, 1, 1), new(0, 1, 1, 1), new(1, 1, 1, 1), new(2, 0, 1, 2)]);
        var after = PartitionOps.EraseWall(before, new WallEdge(true, 1, 0)).Layout!;

        PartitionAssert.EraseInvariants(before, after, new GridRect(0, 0, 2, 1));
        Assert.Contains(new GridRect(2, 0, 1, 2), after.Partitions);
        Assert.All(after.Partitions, p =>
        {
            Assert.True(p.Right <= 3);
            Assert.True(p.Bottom <= 2);
        });
    }

    [Fact]
    public void EraseWall_OuterFrameNeverRemoved()
    {
        var before = new PartitionLayout(
            new GridSize(2, 2), [new(0, 0, 2, 1), new(0, 1, 1, 1), new(1, 1, 1, 1)]);
        var after = PartitionOps.EraseWall(before, new WallEdge(false, 0, 1)).Layout!;

        // 组外框不可拆：结果唯一块的边界恰为组边界
        Assert.Equal(new GridRect(0, 0, 2, 2), after.Partitions[0]);
    }

    [Fact]
    public void Preview_Erase_ReturnsFinalMergeExtent()
    {
        var layout = new PartitionLayout(
            new GridSize(2, 2), [new(0, 0, 2, 1), new(0, 1, 1, 1), new(1, 1, 1, 1)]);
        var session = MakeSession(layout);

        var preview = session.Preview(new WallEdge(false, 0, 1), EditTool.Erase);
        Assert.True(preview.Applicable);
        Assert.Equal(new GridRect(0, 0, 2, 2), preview.MergeExtent);
        Assert.Null(preview.NewLineExtent);
        Assert.True(layout.Equals(session.Current), "预览不改状态");
    }

    // ————————————————————————————— 行列增减 R-1/R-2（B09） —————————————————————————————

    [Fact]
    public void AddColumn_KeepsOldPartitionsAndAddsRightColumnOfOnes_INV_P8()
    {
        var before = PartitionLayout.FullGrid(new GridSize(4, 4));
        var result = PartitionOps.AddColumn(before);

        Assert.Equal(PartitionOpStatus.Applied, result.Status);
        var after = result.Layout!;
        PartitionAssert.GrowInvariants(before, after);
        Assert.Equal(new GridSize(5, 4), after.Size);
        for (var row = 0; row < 4; row++)
        {
            Assert.Equal(new GridRect(4, row, 1, 1), after.Partitions[16 + row]);
        }
    }

    [Fact]
    public void AddRow_KeepsOldPartitionsAndAddsBottomRowOfOnes_INV_P8()
    {
        var before = new PartitionLayout(new GridSize(2, 2), [new(0, 0, 2, 2)]);
        var after = PartitionOps.AddRow(before).Layout!;

        PartitionAssert.GrowInvariants(before, after);
        Assert.Equal(new GridSize(2, 3), after.Size);
        Assert.Equal(new GridRect(0, 0, 2, 2), after.Partitions[0]);
        Assert.Contains(new GridRect(0, 2, 1, 1), after.Partitions);
        Assert.Contains(new GridRect(1, 2, 1, 1), after.Partitions);
    }

    [Fact]
    public void RemoveColumn_TruncatesSpanningBlockStillRectangle()
    {
        var before = new PartitionLayout(new GridSize(5, 4), [new(0, 0, 2, 4), new(2, 0, 3, 4)]);
        var after = PartitionOps.RemoveColumn(before).Layout!;

        PartitionAssert.ShrinkInvariants(before, after);
        Assert.Equal(new GridSize(4, 4), after.Size);
        Assert.Equal(new GridRect(0, 0, 2, 4), after.Partitions[0]); // 原样保留
        Assert.Equal(new GridRect(2, 0, 2, 4), after.Partitions[1]); // 截短仍矩形
    }

    [Fact]
    public void RemoveColumn_DropsBlocksEntirelyBeyondNewBounds()
    {
        var before = new PartitionLayout(
            new GridSize(5, 4), [new(0, 0, 2, 4), new(2, 0, 2, 2), new(2, 2, 3, 2), new(4, 0, 1, 4)]);
        var after = PartitionOps.RemoveColumn(before).Layout!;

        PartitionAssert.ShrinkInvariants(before, after);
        Assert.Equal(new GridSize(4, 4), after.Size);
        Assert.DoesNotContain(new GridRect(4, 0, 1, 4), after.Partitions); // 整块越界丢弃
        Assert.Equal(new GridRect(2, 2, 2, 2), after.Partitions[2]);       // 被截分区
    }

    [Fact]
    public void RemoveRow_TruncatesAndKeepsCoverage()
    {
        var before = new PartitionLayout(new GridSize(2, 5), [new(0, 0, 2, 3), new(0, 3, 1, 2), new(1, 3, 1, 2)]);
        var after = PartitionOps.RemoveRow(before).Layout!;

        PartitionAssert.ShrinkInvariants(before, after);
        Assert.Equal(new GridSize(2, 4), after.Size);
        Assert.Equal(new GridRect(0, 0, 2, 3), after.Partitions[0]);
        Assert.Equal(new GridRect(0, 3, 1, 1), after.Partitions[1]);
        Assert.Equal(new GridRect(1, 3, 1, 1), after.Partitions[2]);
    }

    [Fact]
    public void Resize_AtLimits_RefusesAllFourDirections()
    {
        var max = PartitionLayout.FullGrid(new GridSize(8, 25));
        var min = PartitionLayout.FullGrid(new GridSize(2, 2));

        Assert.Equal(PartitionOpStatus.AtLimit, PartitionOps.AddColumn(max).Status);
        Assert.Equal(PartitionOpStatus.AtLimit, PartitionOps.AddRow(max).Status);
        Assert.Equal(PartitionOpStatus.AtLimit, PartitionOps.RemoveColumn(min).Status);
        Assert.Equal(PartitionOpStatus.AtLimit, PartitionOps.RemoveRow(min).Status);
    }

    [Fact]
    public void ShrinkThenGrow_DoesNotRestoreCutContent_B09()
    {
        var original = new PartitionLayout(new GridSize(4, 4), [new(0, 0, 2, 4), new(2, 0, 2, 4)]);

        var shrunk = PartitionOps.RemoveColumn(original).Layout!;
        Assert.Equal(new GridSize(3, 4), shrunk.Size);

        var regrown = PartitionOps.AddColumn(shrunk).Layout!;
        PartitionAssert.GrowInvariants(shrunk, regrown);

        Assert.False(regrown.Equals(original), "被裁内容即丢：再扩大不得自动恢复");
        // 恰为「幸存旧区 + 新 1×1 列」
        Assert.Equal(new GridRect(0, 0, 2, 4), regrown.Partitions[0]);
        Assert.Equal(new GridRect(2, 0, 1, 4), regrown.Partitions[1]);
        for (var row = 0; row < 4; row++)
        {
            Assert.Equal(new GridRect(3, row, 1, 1), regrown.Partitions[2 + row]);
        }
    }

    // ————————————————————————————— 夹具 —————————————————————————————

    /// <summary>4×4：左上 2×2 大块 + 其余 12 格 1×1（B03 场景）。</summary>
    private static PartitionLayout Mixed4x4With2x2Block()
    {
        var partitions = new List<GridRect> { new(0, 0, 2, 2) };
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                if (col < 2 && row < 2)
                {
                    continue;
                }

                partitions.Add(new GridRect(col, row, 1, 1));
            }
        }

        return new PartitionLayout(new GridSize(4, 4), partitions);
    }

    private static PartitionEditSession MakeSession(PartitionLayout layout) =>
        new(layout.Size, layout.Partitions);
}
