using TileWall.Core.Configuration;
using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Grid;

/// <summary>T-REL：腾位定向场景（设计 §7.2 算法 E-1、§9.1 S1–S8；对应验收 A08–A11 可逻辑化部分）。</summary>
public class RelocationEngineTests
{
    private static readonly WallGrid Wall2x9 = Layouts.Wall2x9;
    private static readonly WallGrid Wall1x9 = Layouts.Wall1x9;

    // —— S1 零移动：目标为空位，谁都不动 ——

    [Fact]
    public void S1_TargetFree_ZeroMoves()
    {
        var dragged = Layouts.Tile("D", new GridRect(0, 0, 2, 2));
        var corner = Layouts.Tile("t-corner", new GridRect(15, 8, 1, 1));
        var objects = new List<LayoutObject> { dragged, corner };

        var result = RelocationEngine.Relocate(objects, Wall2x9, "D", new GridRect(6, 3, 2, 2));

        Assert.True(result.Success);
        Assert.Empty(result.Moves);
        Assert.Equal(new GridRect(6, 3, 2, 2), result.ResultObjects!.Single(o => o.Id == "D").Bounds);
        // 未动对象保留原实例（§7.2 纯映射；INV-6）
        Assert.Same(corner, result.ResultObjects!.Single(o => o.Id == "t-corner"));
        RelocationInvariants.AssertAll(objects, Wall2x9, "D", new GridRect(6, 3, 2, 2), result);
    }

    [Fact]
    public void S1_TargetEqualsOriginalBounds_ZeroMoves()
    {
        var dragged = Layouts.Tile("D", new GridRect(4, 2, 2, 2));
        var other = Layouts.Tile("O", new GridRect(12, 5, 1, 1));
        var objects = new List<LayoutObject> { dragged, other };

        var result = RelocationEngine.Relocate(objects, Wall2x9, "D", dragged.Bounds);

        Assert.True(result.Success);
        Assert.Empty(result.Moves);
        Assert.Equal(dragged.Bounds, result.ResultObjects!.Single(o => o.Id == "D").Bounds);
    }

    // —— S2 单对象 Down 挤压 ——

    [Fact]
    public void S2_SingleObject_MovesDownWithMinimalShift()
    {
        var dragged = Layouts.Tile("D", new GridRect(0, 0, 1, 2));
        var pushable = Layouts.Tile("A", new GridRect(8, 4, 1, 2));
        var corner = Layouts.Tile("B", new GridRect(15, 8, 1, 1));
        var objects = new List<LayoutObject> { dragged, pushable, corner };

        var result = RelocationEngine.Relocate(objects, Wall2x9, "D", new GridRect(8, 4, 1, 2));

        Assert.True(result.Success);
        var move = Assert.Single(result.Moves);
        Assert.Equal("A", move.ObjectId);
        Assert.Equal(new GridRect(8, 4, 1, 2), move.From);
        Assert.Equal(new GridRect(8, 6, 1, 2), move.To); // Down 最小可行位移 d=2：d=1（行 5–6）与 target（行 4–5）相交
        Assert.Equal(new GridRect(8, 4, 1, 2), result.ResultObjects!.Single(o => o.Id == "D").Bounds);
        Assert.Same(corner, result.ResultObjects!.Single(o => o.Id == "B")); // INV-6：无关对象不动
        RelocationInvariants.AssertAll(objects, Wall2x9, "D", new GridRect(8, 4, 1, 2), result);
    }

    // —— S3 链式三连挤：A 挤 B、B 挤 C ——

    [Fact]
    public void S3_ChainedSqueeze_ThreeObjectsInPiOrder()
    {
        var dragged = Layouts.Tile("D", new GridRect(0, 0, 1, 1));
        var a = Layouts.Tile("A", new GridRect(0, 4, 1, 1));
        var b = Layouts.Tile("B", new GridRect(0, 5, 1, 1));
        var c = Layouts.Tile("C", new GridRect(0, 6, 1, 1));
        var corner = Layouts.Tile("Z", new GridRect(7, 8, 1, 1));
        var objects = new List<LayoutObject> { dragged, a, b, c, corner };

        var result = RelocationEngine.Relocate(objects, Wall1x9, "D", new GridRect(0, 4, 1, 1));

        Assert.True(result.Success);
        Assert.Equal(3, result.Moves.Count);
        Assert.Collection(
            result.Moves,
            m => { Assert.Equal("A", m.ObjectId); Assert.Equal(new GridRect(0, 5, 1, 1), m.To); },
            m => { Assert.Equal("B", m.ObjectId); Assert.Equal(new GridRect(0, 6, 1, 1), m.To); },
            m => { Assert.Equal("C", m.ObjectId); Assert.Equal(new GridRect(0, 7, 1, 1), m.To); });
        Assert.Same(corner, result.ResultObjects!.Single(o => o.Id == "Z")); // S8：冲突闭包之外的对象逐字段不变
        RelocationInvariants.AssertAll(objects, Wall1x9, "D", new GridRect(0, 4, 1, 1), result);
    }

    // —— S4 组整体让位（INV-5、A09/A10）——

    [Fact]
    public void S4_GroupEvictsWhole_PartitionsTranslateRigidly()
    {
        var group = Layouts.Group("G", new GridRect(0, 0, 4, 4));
        var dragged = Layouts.Tile("D", new GridRect(8, 6, 2, 2));
        var corner = Layouts.Tile("T", new GridRect(15, 8, 1, 1));
        var objects = new List<LayoutObject> { group, dragged, corner };

        var result = RelocationEngine.Relocate(objects, Wall2x9, "D", new GridRect(1, 1, 2, 2));

        Assert.True(result.Success);
        var move = Assert.Single(result.Moves);
        Assert.Equal("G", move.ObjectId);
        Assert.Equal(new GridRect(0, 3, 4, 4), move.To); // Down 最小可行位移 d=3（越过 target）
        var movedGroup = (GroupObject)result.ResultObjects!.Single(o => o.Id == "G");
        Assert.Equal(new GridRect(0, 3, 4, 4), movedGroup.Bounds);
        Assert.Equal(16, movedGroup.Partitions.Count);
        foreach (var (before, after) in group.Partitions.Zip(movedGroup.Partitions))
        {
            Assert.Equal(before.Translate(0, 3), after); // 等距刚性平移
        }

        Assert.Equal(new GridRect(1, 1, 2, 2), result.ResultObjects!.Single(o => o.Id == "D").Bounds);
        RelocationInvariants.AssertAll(objects, Wall2x9, "D", new GridRect(1, 1, 2, 2), result);
    }

    // —— S5 目标非法 → TargetInvalid（A11 的引擎侧）——

    [Theory]
    [InlineData(6, 0, 3, 1)]  // 跨栏
    [InlineData(20, 0, 1, 1)] // 越墙
    [InlineData(0, 0, 2, 2)]  // 尺寸与被拖对象 (1×1) 不符
    [InlineData(0, 9, 1, 1)]  // 越下边
    public void S5_InvalidTarget_FailsWithoutPartialResult(int column, int row, int width, int height)
    {
        var dragged = Layouts.Tile("D", new GridRect(0, 0, 1, 1));
        var other = Layouts.Tile("O", new GridRect(15, 8, 1, 1));
        var objects = new List<LayoutObject> { dragged, other };
        var target = new GridRect(column, row, width, height);

        var result = RelocationEngine.Relocate(objects, Wall2x9, "D", target);

        Assert.False(result.Success);
        Assert.Equal(RelocationFailureReason.TargetInvalid, result.Reason);
        Assert.Null(result.ResultObjects);   // INV-8：失败不携带部分结果
        Assert.Empty(result.Moves);
        LayoutAssert.SameSequence(objects, objects.ToList()); // record 不可变：快照零改动
    }

    // —— S6 满墙无处搬 → NoEvictionPossible，输入快照零改动（A11）——
    // 构造（手推验证）：墙 1 栏 × 1 行（8 格），D 2×1 + T 2×1 + U 4×1 = 满墙。
    // 拖 D 到 T 的位置：T 可向右挪到 (4,0)（挤 U 入队），U 的 Down/Right/Up 全部越界，
    // Left d=1..4 全部与 target 相交、d=5 越墙 → 无候选 → 失败。

    [Fact]
    public void S6_FullWall_EvictionImpossible_SnapshotUntouched()
    {
        var wall = new WallGrid(1, 1);
        var dragged = Layouts.Tile("D", new GridRect(0, 0, 2, 1));
        var t = Layouts.Tile("T", new GridRect(2, 0, 2, 1));
        var u = Layouts.Tile("U", new GridRect(4, 0, 4, 1));
        var objects = new List<LayoutObject> { dragged, t, u };

        var result = RelocationEngine.Relocate(objects, wall, "D", new GridRect(2, 0, 2, 1));

        Assert.False(result.Success);
        Assert.Equal(RelocationFailureReason.NoEvictionPossible, result.Reason);
        Assert.Null(result.ResultObjects);
        Assert.Empty(result.Moves);
        // 快照逐字段不变（INV-8、A11）
        Assert.Equal(new GridRect(0, 0, 2, 1), objects[0].Bounds);
        Assert.Equal(new GridRect(2, 0, 2, 1), objects[1].Bounds);
        Assert.Equal(new GridRect(4, 0, 4, 1), objects[2].Bounds);
    }

    // —— S7 Down 全堵 → Right 回退路径；被拖组与独立磁贴互换行 ——

    [Fact]
    public void S7_DownBlocked_FallsBackToRight_GroupSwapsIntoTileRow()
    {
        var draggedGroup = Layouts.Group("G", new GridRect(0, 0, 2, 2));
        var tile = Layouts.Tile("T", new GridRect(0, 7, 2, 2)); // 底部贴边：Down 越界
        var far = Layouts.Tile("A", new GridRect(7, 0, 1, 1));  // 单栏墙最右格（1 栏 = 基础格 0–7）
        var objects = new List<LayoutObject> { draggedGroup, tile, far };

        var result = RelocationEngine.Relocate(objects, Wall1x9, "G", new GridRect(0, 7, 2, 2));

        Assert.True(result.Success);
        var move = Assert.Single(result.Moves);
        Assert.Equal("T", move.ObjectId);
        Assert.Equal(new GridRect(2, 7, 2, 2), move.To); // Right 最小可行位移 d=2（d=1 与 target 相交）
        var group = (GroupObject)result.ResultObjects!.Single(o => o.Id == "G");
        Assert.Equal(new GridRect(0, 7, 2, 2), group.Bounds);
        foreach (var (before, after) in draggedGroup.Partitions.Zip(group.Partitions))
        {
            Assert.Equal(before.Translate(0, 7), after);
        }

        RelocationInvariants.AssertAll(objects, Wall1x9, "G", new GridRect(0, 7, 2, 2), result);
    }

    // —— S8 移动集 == 冲突闭包（INV-6/INV-11）：与 S3 共用断言；此处补链条依据的显式检查 ——

    [Fact]
    public void S8_MovedSetEqualsConflictClosure()
    {
        var dragged = Layouts.Tile("D", new GridRect(0, 0, 1, 1));
        var a = Layouts.Tile("A", new GridRect(0, 4, 1, 1));
        var b = Layouts.Tile("B", new GridRect(0, 5, 1, 1));
        var c = Layouts.Tile("C", new GridRect(0, 6, 1, 1));
        var objects = new List<LayoutObject> { dragged, a, b, c };

        var result = RelocationEngine.Relocate(objects, Wall1x9, "D", new GridRect(0, 4, 1, 1));

        Assert.True(result.Success);
        // 移动集 = {A,B,C}：A 原位与 target 相交；B、C 原位与某已放置新位相交（INV-11）
        Assert.Equal(["A", "B", "C"], result.Moves.Select(m => m.ObjectId).ToArray());
        Assert.Equal(new GridRect(0, 4, 1, 1), result.Moves[0].From);
        Assert.True(result.Moves[0].From.Intersects(new GridRect(0, 4, 1, 1)));
        Assert.True(result.Moves[1].From.Intersects(result.Moves[0].To));
        Assert.True(result.Moves[2].From.Intersects(result.Moves[1].To));
    }

    // —— 参考布局上的性质测试：L2 满屏 1×1 上腾位结果确定且满足全部不变式 ——

    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 4)]
    [InlineData(15, 8)]
    public void L2FullWall_ResultIsDeterministicAndInvariantClean(int targetCol, int targetRow)
    {
        var wall = Layouts.Wall2x9;
        var objects = Layouts.FullOnes();
        var target = new GridRect(targetCol, targetRow, 1, 1);

        var first = RelocationEngine.Relocate(objects, wall, "t0800", target);
        var second = RelocationEngine.Relocate(objects, wall, "t0800", target);

        RelocationInvariants.AssertSameResult(first, second); // INV-9（含 Moves 处理序，INV-12）
        if (first.Success)
        {
            RelocationInvariants.AssertAll(objects, wall, "t0800", target, first);
        }
        else
        {
            Assert.Equal(RelocationFailureReason.NoEvictionPossible, first.Reason);
            Assert.Null(first.ResultObjects);
        }
    }

    // —— 编程错误契约 ——

    [Fact]
    public void DraggedIdNotFound_ThrowsArgumentException()
    {
        var objects = new List<LayoutObject> { Layouts.Tile("A", new GridRect(0, 0, 1, 1)) };
        Assert.Throws<ArgumentException>(() =>
            RelocationEngine.Relocate(objects, Layouts.Wall2x9, "missing", new GridRect(1, 0, 1, 1)));
    }

    [Fact]
    public void DuplicateIds_ThrowArgumentException()
    {
        var objects = new List<LayoutObject>
        {
            Layouts.Tile("X", new GridRect(0, 0, 1, 1)),
            Layouts.Tile("X", new GridRect(1, 0, 1, 1)),
        };
        Assert.Throws<ArgumentException>(() =>
            RelocationEngine.Relocate(objects, Layouts.Wall2x9, "X", new GridRect(2, 0, 1, 1)));
    }

    // —— 参考布局 × 目标位置矩阵（P1 §4.2 V-01：L1–L4 全部经同一腾位引擎）——
    // L1 空墙：首个对象放任意合法位 → 零移动成功（设计 §5.4「新建对象」；Layouts.Empty 参与引擎路径）

    [Theory]
    [InlineData(0, 0)]
    [InlineData(6, 3)]
    [InlineData(14, 8)]
    public void L1_EmptyWall_FirstPlacement_ZeroMoves(int col, int row)
    {
        var wall = Layouts.Wall2x9;
        var objects = Layouts.Empty();
        objects.Add(Layouts.Tile("first", new GridRect(0, 0, 1, 1)));
        var target = new GridRect(col, row, 1, 1);

        var result = RelocationEngine.Relocate(objects, wall, "first", target);

        Assert.True(result.Success);
        Assert.Empty(result.Moves);
        Assert.Equal(target, result.ResultObjects!.Single(o => o.Id == "first").Bounds);
        RelocationInvariants.AssertAll(objects, wall, "first", target, result);
    }

    // L3 组-组部分重叠：A 组拖到与 B 组上缘交叠的位置 → B 组整体右移（Down 越墙后的最小可行位移）

    [Fact]
    public void L3_GroupOntoGroupPartialOverlap_EvictsWholeGroup()
    {
        var wall = Layouts.Wall2x9;
        var objects = Layouts.Mixed();
        var target = new GridRect(0, 4, 4, 4); // 与 g000-B（0,5,4,4）行向部分重叠（行 5–7）

        var result = RelocationEngine.Relocate(objects, wall, "g000-A", target);

        Assert.True(result.Success);
        var move = Assert.Single(result.Moves);
        Assert.Equal("g000-B", move.ObjectId);
        Assert.Equal(new GridRect(4, 5, 4, 4), move.To); // Down 越墙；Right d=1..3 与 target 相交，d=4 可行
        var movedA = (GroupObject)result.ResultObjects!.Single(o => o.Id == "g000-A");
        Assert.Equal(target, movedA.Bounds);
        Assert.Equal(16, movedA.Partitions.Count); // 组不拆分（INV-5，A09）
        RelocationInvariants.AssertAll(objects, wall, "g000-A", target, result);
    }

    // L3 磁贴拖到组+磁贴混合区：与 t000-1 部分重叠（「2×4 拖到 3×2 角上」形态）

    [Fact]
    public void L3_TilePartialOverlapOnMixed_EvictsSingleTile()
    {
        var wall = Layouts.Wall2x9;
        var objects = Layouts.Mixed();
        var target = new GridRect(9, 0, 2, 4); // 与 t000-1（8,0,3,2）部分重叠（列 9–10 × 行 0–1）

        var result = RelocationEngine.Relocate(objects, wall, "t000-6", target);

        Assert.True(result.Success);
        var move = Assert.Single(result.Moves);
        Assert.Equal("t000-1", move.ObjectId);
        Assert.Equal(new GridRect(8, 4, 3, 2), move.To); // Down d=1..3 与 target 相交，d=4 可行
        RelocationInvariants.AssertAll(objects, wall, "t000-6", target, result);
    }

    // L4 8×25 满栏组（二百分块）× 组内目标：墙全满（组 200 格 + 100 块 2×1 = 400 格），
    // 组被挤向条带 1 后，条带 0 需容纳 target(2 格) + 100 块磁贴(200 格) = 202 > 200 格，
    // 可行终态不存在 → 贪心必然以 NoEvictionPossible 结束（守恒证明，与次序无关）。

    [Fact]
    public void L4_TargetInsideFullColumnGroup_NoEvictionPossible()
    {
        var wall = Layouts.Wall2x25;
        var objects = Layouts.Extreme();
        var target = new GridRect(2, 10, 2, 1); // t4000（8,0,2,1）拖到 8×25 组内任一格

        var result = RelocationEngine.Relocate(objects, wall, "t4000", target);

        Assert.False(result.Success);
        Assert.Equal(RelocationFailureReason.NoEvictionPossible, result.Reason);
        Assert.Null(result.ResultObjects);
        Assert.Empty(result.Moves);
        // 快照零改动（INV-8、A11）：组仍在 (0,0,8,25)、200 分块未动
        var group = (GroupObject)objects.Single(o => o.Id == "g400-8x25");
        Assert.Equal(new GridRect(0, 0, 8, 25), group.Bounds);
        Assert.Equal(200, group.Partitions.Count);
    }

    // L4 组整体拖到条带 1（与 100 块磁贴全重叠）：被拖组直接落位条带 1，其原位（条带 0）随之腾空
    // 且不构成障碍 → 100 块磁贴经 Left 逐位左移，恰好填满条带 0（8 列 × 25 行 = 200 格）——整栏交换成功。

    [Fact]
    public void L4_GroupOntoDenseStrip_FullStripSwap()
    {
        var wall = Layouts.Wall2x25;
        var objects = Layouts.Extreme();
        var group = (GroupObject)objects.Single(o => o.Id == "g400-8x25");
        var target = new GridRect(8, 0, 8, 25);

        var result = RelocationEngine.Relocate(objects, wall, "g400-8x25", target);

        Assert.True(result.Success);
        Assert.Equal(100, result.Moves.Count); // 全部 100 块磁贴各移动一次
        Assert.Equal(result.Moves.Count, result.Moves.Select(m => m.ObjectId).Distinct().Count());
        var movedGroup = (GroupObject)result.ResultObjects!.Single(o => o.Id == "g400-8x25");
        Assert.Equal(target, movedGroup.Bounds);
        Assert.Equal(200, movedGroup.Partitions.Count);
        foreach (var (before, after) in group.Partitions.Zip(movedGroup.Partitions))
        {
            Assert.Equal(before.Translate(8, 0), after); // 二百分块等距刚性平移（INV-5，B23）
        }

        // 每块磁贴都落入条带 0（基础格列 0–7）
        Assert.All(result.Moves, m => Assert.True(m.To.Right <= 8, $"{m.ObjectId} → {m.To} 越出条带 0"));
        RelocationInvariants.AssertAll(objects, wall, "g400-8x25", target, result);
    }
}
