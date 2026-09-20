using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Grid;

/// <summary>
/// T-STROKE（M5 设计 §6；B06）：去抖、工具不匹配忽略、一次笔画一撤销单元、Undo/Redo 快照往返、单击同为一次单元。
/// 画墙类用例用「4×4 含一个 2×2 大块」夹具（FullGrid 所有内部边均为实线，画墙只在大块内适用）。
/// </summary>
public sealed class PartitionStrokeTests
{
    [Fact]
    public void Stroke_SameEdgeTwiceInOneStroke_FirstAppliedThenDebounced()
    {
        var session = MixedSession();

        session.BeginStroke();
        Assert.Equal(StrokeOutcome.Applied, session.Stroke(new WallEdge(true, 1, 0), EditTool.Draw));
        var afterFirst = session.Current;

        Assert.Equal(StrokeOutcome.Debounced, session.Stroke(new WallEdge(true, 1, 0), EditTool.Draw));
        Assert.True(afterFirst.Equals(session.Current), "去抖不改变布局");
    }

    [Fact]
    public void Stroke_DebouncedDoesNotReverseDrawnEdge_B06()
    {
        var session = MixedSession();

        session.BeginStroke();
        Assert.Equal(StrokeOutcome.Applied, session.Stroke(new WallEdge(true, 1, 0), EditTool.Draw));
        // 同一条线拖笔续扫到的相邻格 V(1,1)：已成实线 → 画墙忽略（不会重复开线）
        Assert.Equal(StrokeOutcome.SolidForDraw, session.Stroke(new WallEdge(true, 1, 1), EditTool.Draw));
        // 回扫同一边界：去抖 → 不会因再次经过而被拆墙删除（「不反复切换」）
        Assert.Equal(StrokeOutcome.Debounced, session.Stroke(new WallEdge(true, 1, 0), EditTool.Erase));
        session.EndStroke();

        Assert.Contains(new GridRect(0, 0, 1, 2), session.Current.Partitions); // 线仍在
        Assert.Equal(1, session.UndoDepth);
    }

    [Fact]
    public void Stroke_MultipleEdgesOneStroke_IsSingleUndoUnit()
    {
        // FullGrid 上连续拆两条实线 = 同一笔画两个生效边界，撤销栈恰 +1
        var session = FullGridSession();

        session.BeginStroke();
        Assert.Equal(StrokeOutcome.Applied, session.Stroke(new WallEdge(true, 1, 0), EditTool.Erase));
        Assert.Equal(StrokeOutcome.Applied, session.Stroke(new WallEdge(true, 1, 1), EditTool.Erase));
        Assert.Equal(StrokeOutcome.Debounced, session.Stroke(new WallEdge(true, 1, 0), EditTool.Erase)); // 回扫只去抖
        Assert.True(session.EndStroke());

        Assert.Equal(1, session.UndoDepth); // 一次连续笔画 = 一个撤销单元（§8.4）
        Assert.Equal(14, session.Current.PartitionCount); // 16 − 2
    }

    [Fact]
    public void Stroke_DrawAcrossSolidEdge_IgnoredAndNoUndoUnit()
    {
        var session = MixedSession();
        DrawOne(session, new WallEdge(true, 1, 0));
        Assert.Equal(1, session.UndoDepth);

        session.BeginStroke();
        Assert.Equal(StrokeOutcome.SolidForDraw, session.Stroke(new WallEdge(true, 1, 0), EditTool.Draw));
        Assert.Equal(StrokeOutcome.SolidForDraw, session.Stroke(new WallEdge(true, 1, 1), EditTool.Draw)); // 拆分后已成实线
        Assert.False(session.EndStroke(), "无生效边界 → 无撤销单元（栈不动，INV-P11）");
        Assert.Equal(1, session.UndoDepth);
    }

    [Fact]
    public void Stroke_EraseAcrossDashedEdge_IgnoredAndNoUndoUnit()
    {
        var session = MixedSession();

        session.BeginStroke();
        Assert.Equal(StrokeOutcome.DashedForErase, session.Stroke(new WallEdge(true, 1, 0), EditTool.Erase)); // 大块内部为虚线
        Assert.False(session.EndStroke());
        Assert.Equal(0, session.UndoDepth);
        Assert.Equal(13, session.Current.PartitionCount);
    }

    [Fact]
    public void Stroke_EraseSolidEdge_AppliesAndMergesBack()
    {
        var session = MixedSession();
        DrawOne(session, new WallEdge(true, 1, 0)); // 2×2 → 两块 1×2（14 块）

        session.BeginStroke();
        Assert.Equal(StrokeOutcome.Applied, session.Stroke(new WallEdge(true, 1, 0), EditTool.Erase));
        Assert.True(session.EndStroke());

        Assert.Equal(13, session.Current.PartitionCount); // 两块 1×2 并回一块 2×2
        Assert.Contains(new GridRect(0, 0, 2, 2), session.Current.Partitions);
        Assert.Equal(2, session.UndoDepth);
    }

    [Fact]
    public void Stroke_SingleClick_IsOneUndoUnit()
    {
        var session = FullGridSession();

        session.BeginStroke(); // 单击 = Begin+Stroke+End
        Assert.Equal(StrokeOutcome.Applied, session.Stroke(new WallEdge(true, 1, 0), EditTool.Erase));
        Assert.True(session.EndStroke());

        Assert.Equal(1, session.UndoDepth);
    }

    [Fact]
    public void Stroke_OutOfRangeEdge_ReturnsOutOfRangeAndChangesNothing()
    {
        var session = MixedSession();
        var before = session.Current;

        session.BeginStroke();
        Assert.Equal(StrokeOutcome.OutOfRange, session.Stroke(new WallEdge(true, 0, 0), EditTool.Draw));
        Assert.Equal(StrokeOutcome.OutOfRange, session.Stroke(new WallEdge(true, 4, 0), EditTool.Erase));
        Assert.Equal(StrokeOutcome.OutOfRange, session.Stroke(new WallEdge(false, 0, 0), EditTool.Draw));
        Assert.Equal(StrokeOutcome.OutOfRange, session.Stroke(new WallEdge(false, 0, 4), EditTool.Draw));
        Assert.False(session.EndStroke());
        Assert.True(before.Equals(session.Current), "失败输入返回原因且 Current 逐字段不变（INV-P10）");
    }

    [Fact]
    public void UndoRedo_SnapshotRoundTrip()
    {
        var session = MixedSession();
        var initial = session.Current;
        DrawOne(session, new WallEdge(true, 1, 0));
        var afterStroke = session.Current;
        Assert.False(initial.Equals(afterStroke));

        Assert.True(session.Undo());
        Assert.True(initial.Equals(session.Current), "Undo 后 Current == 上一快照（record 值相等，INV-P11）");
        Assert.False(session.CanUndo);
        Assert.True(session.CanRedo);

        Assert.True(session.Redo());
        Assert.True(afterStroke.Equals(session.Current), "Redo 对称");
        Assert.False(session.CanRedo);
        Assert.True(session.CanUndo);
    }

    [Fact]
    public void UndoRedo_MultiUnitOrdering()
    {
        var session = MixedSession();
        var initial = session.Current;
        DrawOne(session, new WallEdge(true, 1, 0));
        var s1 = session.Current;
        DrawOne(session, new WallEdge(false, 0, 1));
        var s2 = session.Current;

        session.Undo();
        Assert.True(s1.Equals(session.Current));
        session.Undo();
        Assert.True(initial.Equals(session.Current));
        session.Redo();
        Assert.True(s1.Equals(session.Current));
        session.Redo();
        Assert.True(s2.Equals(session.Current));
    }

    [Fact]
    public void ResizeButtons_EachClickIsOneUndoUnit_LimitReturnsFalse()
    {
        var session = MixedSession();

        Assert.True(session.AddColumn());
        Assert.Equal(1, session.UndoDepth);
        Assert.True(session.AddRow());
        Assert.Equal(2, session.UndoDepth);
        Assert.True(session.Undo());

        // 到界拒绝：栈与布局不动
        var tight = new PartitionEditSession(new GridSize(2, 2), PartitionLayout.FullGrid(new GridSize(2, 2)).Partitions);
        Assert.False(tight.RemoveColumn());
        Assert.False(tight.RemoveRow());
        Assert.Equal(0, tight.UndoDepth);
    }

    [Fact]
    public void UndoCapacity_DropsOldestBeyondLimit()
    {
        var session = new PartitionEditSession(
            new GridSize(4, 4), PartitionLayout.FullGrid(new GridSize(4, 4)).Partitions, undoCapacity: 2);
        EraseOne(session, new WallEdge(true, 1, 0));
        EraseOne(session, new WallEdge(true, 1, 1));
        EraseOne(session, new WallEdge(true, 2, 0));

        Assert.Equal(2, session.UndoDepth); // 容量 2：最旧快照被丢弃
        session.Undo();
        var secondState = session.Current;
        session.Undo();
        Assert.False(secondState.Equals(session.Current));
        Assert.False(session.Undo(), "已到栈底");
    }

    [Fact]
    public void Preview_HoverDoesNotMutateState()
    {
        var session = MixedSession();
        var before = session.Current;

        var drawPreview = session.Preview(new WallEdge(true, 1, 0), EditTool.Draw);
        Assert.True(drawPreview.Applicable);
        Assert.Equal(new GridRect(1, 0, 1, 2), drawPreview.NewLineExtent); // 贯穿大块整条高的竖线段

        var erasePreview = session.Preview(new WallEdge(true, 1, 0), EditTool.Erase);
        Assert.False(erasePreview.Applicable); // 虚线：拆墙不适用
        Assert.Equal(StrokeOutcome.DashedForErase, erasePreview.Outcome);

        Assert.True(before.Equals(session.Current), "悬停只出预览不改状态");
    }

    [Fact]
    public void Preview_MergeExtentMatchesEraseResult()
    {
        var partitions = new List<GridRect> { new(0, 0, 2, 1), new(0, 1, 1, 1), new(1, 1, 1, 1) };
        var session = new PartitionEditSession(new GridSize(2, 2), partitions);
        var before = session.Current;

        var preview = session.Preview(new WallEdge(false, 0, 1), EditTool.Erase);
        Assert.True(preview.Applicable);
        Assert.Equal(new GridRect(0, 0, 2, 2), preview.MergeExtent);

        session.BeginStroke();
        session.Stroke(new WallEdge(false, 0, 1), EditTool.Erase);
        session.EndStroke();
        Assert.Equal(preview.MergeExtent, PartitionAssert.Diff(before, session.Current).Added.Single());
    }

    // ————————————————————————————— 夹具 —————————————————————————————

    /// <summary>4×4：左上 2×2 大块 + 其余 12 格 1×1（13 块；画墙在大块内适用）。</summary>
    private static PartitionEditSession MixedSession()
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

        return new PartitionEditSession(new GridSize(4, 4), partitions);
    }

    private static PartitionEditSession FullGridSession() =>
        new(new GridSize(4, 4), PartitionLayout.FullGrid(new GridSize(4, 4)).Partitions);

    private static void DrawOne(PartitionEditSession session, WallEdge edge)
    {
        session.BeginStroke();
        Assert.Equal(StrokeOutcome.Applied, session.Stroke(edge, EditTool.Draw));
        Assert.True(session.EndStroke());
    }

    private static void EraseOne(PartitionEditSession session, WallEdge edge)
    {
        session.BeginStroke();
        Assert.Equal(StrokeOutcome.Applied, session.Stroke(edge, EditTool.Erase));
        Assert.True(session.EndStroke());
    }
}
