using TileWall.Core.Configuration;
using TileWall.Core.Grid;
using Xunit;
using Xunit.Sdk;

namespace TileWall.Core.Tests.Fixtures;

/// <summary>布局对象逐字段深度比较（record 值相等对 IReadOnlyList 分区是引用比较，故自建谓词）。</summary>
internal static class LayoutAssert
{
    public static bool DeepEquals(LayoutObject? a, LayoutObject? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.GetType() != b.GetType())
        {
            return false;
        }

        if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal) || a.Bounds != b.Bounds)
        {
            return false;
        }

        return (a, b) switch
        {
            (TileObject t1, TileObject t2) =>
                Equals(t1.Visual, t2.Visual)
                && EntryEquals(t1.Entry, t2.Entry),
            (GroupObject g1, GroupObject g2) =>
                Equals(g1.Visual, g2.Visual)
                && EntryEquals(g1.Entry, g2.Entry)
                && Equals(g1.Carousel, g2.Carousel)
                && g1.Partitions.Count == g2.Partitions.Count
                && g1.Partitions.SequenceEqual(g2.Partitions),
            _ => false,
        };
    }

    public static void DeepEqual(LayoutObject expected, LayoutObject actual)
    {
        if (!DeepEquals(expected, actual))
        {
            throw new XunitException($"布局对象逐字段不一致。\n期望：{Describe(expected)}\n实际：{Describe(actual)}");
        }
    }

    public static void SameSequence(IReadOnlyList<LayoutObject> expected, IReadOnlyList<LayoutObject> actual)
    {
        if (expected.Count != actual.Count)
        {
            throw new XunitException($"对象数量不一致：期望 {expected.Count}，实际 {actual.Count}");
        }

        for (var i = 0; i < expected.Count; i++)
        {
            if (!DeepEquals(expected[i], actual[i]))
            {
                throw new XunitException($"第 {i} 个对象逐字段不一致。\n期望：{Describe(expected[i])}\n实际：{Describe(actual[i])}");
            }
        }
    }

    public static bool EntryEquals(EntryReference? a, EntryReference? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && string.Equals(a.RelativePath, b.RelativePath, StringComparison.Ordinal));

    public static string Describe(LayoutObject o)
    {
        var visual = $"ShowTitle={o.Visual.ShowTitle}, Title={o.Visual.TitleText ?? "<null>"}";
        return o switch
        {
            GroupObject g => $"[group] {o.Id} {o.Bounds} partitions=[{string.Join(",", g.Partitions)}] {visual}",
            _ => $"[tile] {o.Id} {o.Bounds} {visual}",
        };
    }
}

/// <summary>
/// 腾位结果不变式断言（设计 §7.3 INV-1..12；模糊测试逐例调用）。
/// INV-7（预览=提交单一路径）与 INV-9（重复执行一致）、INV-12（Moves 处理序）
/// 由调用方「跑两遍逐字段比对」覆盖；本类断言其余各项。
/// </summary>
internal static class RelocationInvariants
{
    public static void AssertAll(
        IReadOnlyList<LayoutObject> input,
        WallGrid wall,
        string draggedId,
        GridRect target,
        RelocationResult result)
    {
        Assert.True(result.Success, "仅对成功结果断言不变式。");
        Assert.NotNull(result.ResultObjects);
        var output = result.ResultObjects!;
        var inputById = input.ToDictionary(o => o.Id, StringComparer.Ordinal);
        var outputById = output.ToDictionary(o => o.Id, StringComparer.Ordinal);
        var movedIds = result.Moves.Select(m => m.ObjectId).ToHashSet(StringComparer.Ordinal);

        // INV-1 对象集合恒等（只移动，不增删）
        Assert.Equal(input.Count, output.Count);
        Assert.Equal(
            input.Select(o => o.Id).Order().ToArray(),
            output.Select(o => o.Id).Order().ToArray());

        // INV-2 / INV-3 全部在墙内、不跨竖栏（含被拖者新位）
        foreach (var o in output)
        {
            Assert.True(wall.Contains(o.Bounds), $"INV-2：{o.Id} 越出墙：{o.Bounds}");
            Assert.True(wall.InSingleColumn(o.Bounds), $"INV-3：{o.Id} 跨竖栏：{o.Bounds}");
        }

        // INV-4 两两矩形不相交
        for (var i = 0; i < output.Count; i++)
        {
            for (var j = i + 1; j < output.Count; j++)
            {
                Assert.False(output[i].Bounds.Intersects(output[j].Bounds),
                    $"INV-4：{output[i].Id} 与 {output[j].Id} 相交：{output[i].Bounds} / {output[j].Bounds}");
            }
        }

        // INV-5 组整体移动、永不拆分：分区为 Bounds 相对坐标（TileWallConfig「坐标相对 Bounds 左上角」、设计 §2.4），
        // Bounds 平移即整体刚性平移——分区列表逐字段不变（若随墙格位移平移，提交配置必触发 PARTITION_OUT_OF_BOUNDS）
        foreach (var o in output)
        {
            if (o is not GroupObject g || inputById[g.Id] is not GroupObject ig)
            {
                continue;
            }

            Assert.Equal(ig.Partitions.Count, g.Partitions.Count);
            for (var k = 0; k < ig.Partitions.Count; k++)
            {
                Assert.Equal(ig.Partitions[k], g.Partitions[k]);
            }
        }

        // INV-6 未进入冲突链的对象逐字段不变
        foreach (var o in output)
        {
            if (o.Id != draggedId && !movedIds.Contains(o.Id))
            {
                LayoutAssert.DeepEqual(inputById[o.Id], o);
            }
        }

        // INV-10 被拖对象落在 target
        Assert.Equal(target, outputById[draggedId].Bounds);

        // INV-11 每个被移动对象恰好移动一次，且存在链条（原位与 target 或某已放置新位相交）
        Assert.Equal(result.Moves.Count, movedIds.Count);
        foreach (var m in result.Moves)
        {
            Assert.NotEqual(m.From, m.To);
            var chained = m.From.Intersects(target)
                || result.Moves.Any(other => !ReferenceEquals(other, m) && m.From.Intersects(other.To));
            Assert.True(chained, $"INV-11：{m.ObjectId} 的移动缺乏冲突链依据：{m.From} → {m.To}");
        }
    }

    /// <summary>INV-9：两次执行逐字段一致（含 Moves 次序——INV-12 的可复现性观测面）。</summary>
    public static void AssertSameResult(RelocationResult first, RelocationResult second)
    {
        Assert.Equal(first.Success, second.Success);
        Assert.Equal(first.Reason, second.Reason);
        Assert.Equal(first.Moves.Count, second.Moves.Count);
        for (var i = 0; i < first.Moves.Count; i++)
        {
            Assert.Equal(first.Moves[i], second.Moves[i]);
        }

        if (first.ResultObjects is null)
        {
            Assert.Null(second.ResultObjects);
            return;
        }

        Assert.NotNull(second.ResultObjects);
        LayoutAssert.SameSequence(first.ResultObjects, second.ResultObjects!);
    }
}
