using TileWall.Core.Grid;
using Xunit;
using Xunit.Sdk;

namespace TileWall.Core.Tests.Fixtures;

/// <summary>
/// 分区操作不变式断言（M5 设计 §3.3 INV-P6/P7/P8/P9；T-DRAW/T-ERASE/T-RESIZE/T-FUZZ 共用）。
/// 每个助手先断言前后布局自身 Check() 无违规（INV-P10），再做结构差集核对。
/// </summary>
internal static class PartitionAssert
{
    /// <summary>INV-P6：画墙后恰一个分区被替换为两个矩形；其余逐条不变；分块数 +1；两矩形恰剖分原分区（线贯穿整条高/宽）。</summary>
    public static void DrawInvariants(PartitionLayout before, PartitionLayout after)
    {
        AssertValid(before, "画墙前");
        AssertValid(after, "画墙后");
        Assert.Equal(before.Size, after.Size);
        Assert.Equal(before.PartitionCount + 1, after.PartitionCount);

        var (removed, added) = Diff(before, after);
        Assert.True(removed.Count == 1, $"INV-P6：应恰有一个分区被替换，实际移除 {removed.Count} 个：{Describe(removed)}");
        Assert.True(added.Count == 2, $"INV-P6：应恰产生两个新矩形，实际新增 {added.Count} 个：{Describe(added)}");
        var original = removed[0];

        // 两新矩形都 ⊆ 原分区，两两不交，面积并恰等于原分区（⇒ 新线贯穿整条高/宽、终止于 p 边界）
        foreach (var piece in added)
        {
            Assert.True(original.Contains(piece), $"INV-P6：新矩形 {piece} 越出原分区 {original}");
        }

        Assert.False(added[0].Intersects(added[1]), $"INV-P6：两新矩形相交：{added[0]} / {added[1]}");
        Assert.Equal(original.Width * original.Height,
            (added[0].Width * added[0].Height) + (added[1].Width * added[1].Height)); // 面积并 == 原分区

        // 其余分区逐条不变（含顺序稳定：只替换发生处变化）
        var expectedRest = before.Partitions.Where(p => p != original).ToList();
        var actualRest = after.Partitions.Where(p => !added.Contains(p)).ToList();
        Assert.True(expectedRest.SequenceEqual(actualRest),
            $"INV-P6：其余分区被改动。\n期望：{Describe(expectedRest)}\n实际：{Describe(actualRest)}");
    }

    /// <summary>INV-P7：拆墙后恰一个新矩形 R 替换被合并集 merged（|merged| ≥ 2），R == BB(merged)，∪merged == R（无半切块）。</summary>
    public static void EraseInvariants(PartitionLayout before, PartitionLayout after, GridRect expectedExtent)
    {
        AssertValid(before, "拆墙前");
        AssertValid(after, "拆墙后");
        Assert.Equal(before.Size, after.Size);

        var (removed, added) = Diff(before, after);
        Assert.True(removed.Count >= 2, $"INV-P7：合并集应 ≥ 2 块，实际 {removed.Count}：{Describe(removed)}");
        Assert.True(added.Count == 1, $"INV-P7：应恰产生一个新矩形，实际 {added.Count}：{Describe(added)}");
        Assert.Equal(added[0], expectedExtent);

        var r = added[0];
        var mergedArea = 0;
        foreach (var p in removed)
        {
            Assert.True(r.Contains(p), $"INV-P7：被并分区 {p} 越出合并矩形 {r}（半切块）");
            mergedArea += p.Width * p.Height;
        }

        Assert.Equal(r.Width * r.Height, mergedArea); // ∪merged == R（两两不交 + 面积相等 ⇒ 无残留局部墙）

        // 分块数 −(|merged|−1)；其余分区逐条不变
        Assert.Equal(before.PartitionCount - removed.Count + 1, after.PartitionCount);
        var expectedRest = before.Partitions.Where(p => !removed.Contains(p)).ToList();
        var actualRest = after.Partitions.Where(p => p != r).ToList();
        Assert.True(expectedRest.SequenceEqual(actualRest),
            $"INV-P7：其余分区被改动。\n期望：{Describe(expectedRest)}\n实际：{Describe(actualRest)}");
    }

    /// <summary>INV-P8：扩大后旧分区集合逐条不变；新增区域全部为 1×1。</summary>
    public static void GrowInvariants(PartitionLayout before, PartitionLayout after)
    {
        AssertValid(before, "扩大前");
        AssertValid(after, "扩大后");
        var (_, added) = Diff(before, after);
        Assert.True(before.Partitions.SequenceEqual(after.Partitions.Take(before.Partitions.Count)),
            "INV-P8：旧分区被改动（应逐条不变）");
        Assert.True(added.Count > 0 && added.All(p => p.Width == 1 && p.Height == 1),
            $"INV-P8：新增区域应全为 1×1，实际：{Describe(added)}");
    }

    /// <summary>INV-P9：缩小后所有分区 ⊆ 新界、仍矩形、覆盖完备（Check 即含）；被截分区仍矩形由 Check+包含断言覆盖。</summary>
    public static void ShrinkInvariants(PartitionLayout before, PartitionLayout after)
    {
        AssertValid(after, "缩小后");
        Assert.True(after.Size.Columns < before.Size.Columns || after.Size.Rows < before.Size.Rows,
            "INV-P9：缩小后尺寸应严格变小");
        foreach (var p in after.Partitions)
        {
            Assert.True(p.Width >= 1 && p.Height >= 1, $"INV-P9：分区 {p} 非矩形（宽高 <1）");
        }
    }

    /// <summary>INV-P10：布局自身不变式全部成立（每次操作后调用）。</summary>
    public static void AssertValid(PartitionLayout layout, string phase)
    {
        var check = layout.Check();
        Assert.True(check.IsValid, $"{phase}不变式违规：{string.Join(",", check.Violations)}");
    }

    /// <summary>前后分区差集（值语义）：removed = 前−后，added = 后−前。</summary>
    public static (List<GridRect> Removed, List<GridRect> Added) Diff(PartitionLayout before, PartitionLayout after)
    {
        var removed = before.Partitions.Where(p => !after.Partitions.Contains(p)).ToList();
        var added = after.Partitions.Where(p => !before.Partitions.Contains(p)).ToList();
        return (removed, added);
    }

    private static string Describe(IEnumerable<GridRect> rects) => $"[{string.Join(", ", rects)}]";
}
