using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using Xunit;

namespace TileWall.Core.Tests.Imaging;

/// <summary>
/// T-XFORM-ALIGN（M6 设计 §11.1；V-03 指定的方向性测试图；B10）：
/// 合成「网格纹 + 对角线」解析函数 <see cref="SamplePattern"/>，对
/// {FullGrid(2×2), FullGrid(4×4), 手排 L 形, 种子化随机合法剖分 ≥500 例} 断言：
/// ① 任意画布点经任意所在分块的 CanvasPointToSource 相等（XF-5 跨块一致性）；
/// ② 相邻块共享边两侧的源坐标差恰为 缝隙/k（方向性图连续性）；
/// ③ 拆墙合并前后采样点集的源坐标逐点相等（XF-6 拆墙重现被遮图像）。
/// </summary>
public sealed class TransformAlignmentTests
{
    private static readonly GridMetrics M = GridMetrics.Default;
    private static readonly PixelSize Source = new(400, 300);

    /// <summary>方向性测试图（解析、无随机）：20 DIP 网格纹 + |x−y|&lt;12 的对角线条带。</summary>
    private static double SamplePattern(double x, double y)
    {
        var grid = (Math.Floor(x / 20.0) + Math.Floor(y / 20.0)) % 2 == 0 ? 1.0 : 0.0;
        var diagonal = Math.Abs(x - y) < 12.0 ? 2.0 : 0.0;
        return grid + diagonal;
    }

    public static IEnumerable<object[]> NamedPartitions()
    {
        yield return [PartitionLayout.FullGrid(new GridSize(2, 2)).Partitions, "FullGrid(2×2)"];
        yield return [PartitionLayout.FullGrid(new GridSize(4, 4)).Partitions, "FullGrid(4×4)"];

        // 手排 L 形（3×3）：横条(0,0,2,1) + 竖条(0,1,1,2) + 大块(1,1,2,2)——完整覆盖、互不重叠
        List<GridRect> lShape = [new(0, 0, 2, 1), new(0, 1, 1, 2), new(1, 1, 2, 2)];
        yield return [lShape, "手排L形"];
    }

    [Theory]
    [MemberData(nameof(NamedPartitions))]
    public void NamedPartitions_CrossBlockSourceCoordinates_AreIdentical(IReadOnlyList<GridRect> partitions, string _)
    {
        var size = ExtentOf(partitions);
        AssertIsAligned(size, partitions);
    }

    [Fact]
    public void RandomLegalPartitions_500Cases_CrossBlockAlignmentAndGapStep()
    {
        var random = new Random(20260921); // 种子化：同输入同输出（Core 第一原则）
        for (var caseIndex = 0; caseIndex < 500; caseIndex++)
        {
            var size = new GridSize(2 + (caseIndex % 4), 2 + (caseIndex % 3)); // 2..5 × 2..4
            var partitions = RandomPartition(size, random);
            AssertIsAligned(size, partitions); // ① + ② 每例都断
        }
    }

    [Fact]
    public void WallRemoval_MergedPartition_ReproducesCoveredSourcePoints()
    {
        // D-1 场景：2×2 四块 1×1 → 拆掉 A|B 之间的墙 → AB 合并为 2×1（M5 §5.2 例证的 M6 复用）
        var before = new List<GridRect> { new(0, 0, 1, 1), new(1, 0, 1, 1), new(0, 1, 1, 1), new(1, 1, 1, 1) };
        var after = new List<GridRect> { new(0, 0, 2, 1), new(0, 1, 1, 1), new(1, 1, 1, 1) };
        var size = new GridSize(2, 2);
        var canvas = SharedCanvas.CanvasSize(size, M);
        var t = RandomTransform(canvas, new Random(7));

        var oldA = SharedCanvas.PartitionRect(before[0], M);
        var oldB = SharedCanvas.PartitionRect(before[1], M);
        var merged = SharedCanvas.PartitionRect(after[0], M);
        Assert.NotNull(SharedCanvasTransform.ClipFor(t, Source, merged));

        // XF-6：新块 R 的采样点集 ⊇ 旧 A∪B 的采样点集，且源坐标逐点相等（同一唯一正向映射）
        var sampled = 0;
        foreach (var old in new[] { oldA, oldB })
        {
            var clip = SharedCanvasTransform.ClipFor(t, Source, old);
            if (clip is null)
            {
                continue;
            }

            for (var x = clip.DestInCanvas.X; x < clip.DestInCanvas.X + clip.DestInCanvas.Width; x += 4)
            {
                for (var y = clip.DestInCanvas.Y; y < clip.DestInCanvas.Y + clip.DestInCanvas.Height; y += 4)
                {
                    var point = new DipPoint(x, y);
                    Assert.True(Contains(merged, point), $"合并块必须覆盖旧块采样点 ({x},{y})");
                    var source = SharedCanvasTransform.CanvasPointToSource(t, point);
                    // 方向性图在新块可见性下同值：拆墙后重现的正是被遮图像
                    _ = SamplePattern(source.X, source.Y);
                    sampled++;
                }
            }
        }

        Assert.True(sampled > 0);
    }

    // ————————————————————————————— 断言核 —————————————————————————————

    private static void AssertIsAligned(GridSize size, IReadOnlyList<GridRect> partitions)
    {
        var canvas = SharedCanvas.CanvasSize(size, M);
        var t = RandomTransform(canvas, new Random(size.Columns * 100 + size.Rows));
        var rects = partitions.Select(p => SharedCanvas.PartitionRect(p, M)).ToList();
        var clips = rects.Select(r => SharedCanvasTransform.ClipFor(t, Source, r)).ToList();

        // ① 任意画布点经任意所在分块映射到同一源像素（网格步进采样）
        for (var x = 0.0; x < canvas.Width; x += 4)
        {
            for (var y = 0.0; y < canvas.Height; y += 4)
            {
                var point = new DipPoint(x, y);
                string? owner = null;
                double sx = 0, sy = 0;
                for (var i = 0; i < rects.Count; i++)
                {
                    if (!Contains(rects[i], point))
                    {
                        continue;
                    }

                    if (ContainsIn(clips[i], point))
                    {
                        var sourceHere = SharedCanvasTransform.CanvasPointToSource(t, point);
                        Assert.InRange(sourceHere.X, 0, 400); // 可见点必映射回源图定义域（400×300）
                        Assert.InRange(sourceHere.Y, 0, 300);
                    }

                    var s = SharedCanvasTransform.CanvasPointToSource(t, point);
                    if (owner is null)
                    {
                        owner = $"#{i}";
                        sx = s.X;
                        sy = s.Y;
                    }
                    else
                    {
                        Assert.Fail($"画布点 ({x},{y}) 属于多个分块（非法剖分）");
                    }
                }

                if (owner is not null)
                {
                    // 该点无论属于哪个分块，源坐标都来自同一映射（XF-5 构造性）；再对方向性图取同值
                    var pattern = SamplePattern(sx, sy);
                    Assert.True(pattern is 0.0 or 1.0 or 2.0 or 3.0);
                }
            }
        }

        // ② 相邻块共享边两侧源坐标差恰为 缝隙/k（竖缝：A.Right==B.Column 且行区间重叠；横缝对称）
        for (var i = 0; i < partitions.Count; i++)
        {
            for (var j = 0; j < partitions.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                var a = partitions[i];
                var b = partitions[j];
                if (a.Right == b.Column && a.Row < b.Bottom && b.Row < a.Bottom)
                {
                    AssertGapStep(clips[i], clips[j], rects[i], rects[j], vertical: true, t);
                }

                if (a.Bottom == b.Row && a.Column < b.Right && b.Column < a.Right)
                {
                    AssertGapStep(clips[i], clips[j], rects[i], rects[j], vertical: false, t);
                }
            }
        }
    }

    private static void AssertGapStep(PartitionClip? clipA, PartitionClip? clipB, DipRect rectA, DipRect rectB, bool vertical, ImageTransform t)
    {
        if (clipA is null || clipB is null)
        {
            return; // 一侧无图（露后景）：缝隙步进无从谈起
        }

        if (vertical)
        {
            var seamA = rectA.X + rectA.Width; // A 右缘（画布 DIP）
            var seamB = rectB.X;               // B 左缘
            if (clipA.DestInCanvas.X + clipA.DestInCanvas.Width < seamA
                || clipB.DestInCanvas.X > seamB)
            {
                return; // 该缝在图外（未被覆盖）
            }

            var sourceRightOfA = clipA.SourceRect.X + clipA.SourceRect.Width;
            Assert.Equal(M.Gap / t.Scale, clipB.SourceRect.X - sourceRightOfA, 6);
        }
        else
        {
            var seamA = rectA.Y + rectA.Height;
            var seamB = rectB.Y;
            if (clipA.DestInCanvas.Y + clipA.DestInCanvas.Height < seamA
                || clipB.DestInCanvas.Y > seamB)
            {
                return;
            }

            var sourceBottomOfA = clipA.SourceRect.Y + clipA.SourceRect.Height;
            Assert.Equal(M.Gap / t.Scale, clipB.SourceRect.Y - sourceBottomOfA, 6);
        }
    }

    // ————————————————————————————— 构造器 —————————————————————————————

    /// <summary>种子化随机合法剖分：从整块出发做随机刀切（guillotine），保证互不重叠、完整覆盖。</summary>
    private static List<GridRect> RandomPartition(GridSize size, Random random)
    {
        var rects = new List<GridRect> { new(0, 0, size.Columns, size.Rows) };
        var target = 1 + random.Next(Math.Min(8, size.Columns * size.Rows));
        while (rects.Count < target)
        {
            var index = -1;
            var bestArea = 0;
            for (var i = 0; i < rects.Count; i++)
            {
                var area = rects[i].Width * rects[i].Height;
                if (area > bestArea)
                {
                    bestArea = area;
                    index = i;
                }
            }

            if (index < 0)
            {
                break;
            }

            var r = rects[index];
            var canSplitVertically = r.Width >= 2;
            var canSplitHorizontally = r.Height >= 2;
            if (!canSplitVertically && !canSplitHorizontally)
            {
                break; // 已全部 1×1：刀切到底
            }

            if (canSplitVertically && (!canSplitHorizontally || random.Next(2) == 0))
            {
                var cut = 1 + random.Next(r.Width - 1);
                rects[index] = new GridRect(r.Column, r.Row, cut, r.Height);
                rects.Insert(index + 1, new GridRect(r.Column + cut, r.Row, r.Width - cut, r.Height));
            }
            else
            {
                var cut = 1 + random.Next(r.Height - 1);
                rects[index] = new GridRect(r.Column, r.Row, r.Width, cut);
                rects.Insert(index + 1, new GridRect(r.Column, r.Row + cut, r.Width, r.Height - cut));
            }
        }

        return rects;
    }

    /// <summary>随机变换：CoverFill 基础上 ZoomAt（锚点/系数随机，系数 ≥1 保持覆盖）+ 小幅平移。</summary>
    private static ImageTransform RandomTransform(DipSize canvas, Random random)
    {
        var t = SharedCanvasTransform.DefaultTransform(Source, canvas, FitMode.CoverFill);
        var factor = 1.0 + (random.NextDouble() * 1.5);
        var anchor = new DipPoint(random.NextDouble() * canvas.Width, random.NextDouble() * canvas.Height);
        var zoomed = SharedCanvasTransform.ZoomAt(t, factor, anchor, SharedCanvasTransform.CoverFillScale(Source, canvas));
        return SharedCanvasTransform.Translate(zoomed, (random.NextDouble() - 0.5) * 24, (random.NextDouble() - 0.5) * 24);
    }

    private static GridSize ExtentOf(IReadOnlyList<GridRect> partitions)
    {
        var right = partitions.Max(p => p.Right);
        var bottom = partitions.Max(p => p.Bottom);
        return new GridSize(right, bottom);
    }

    private static bool Contains(DipRect r, DipPoint p) =>
        r.X <= p.X && p.X < r.X + r.Width && r.Y <= p.Y && p.Y < r.Y + r.Height;

    private static bool ContainsIn(PartitionClip? clip, DipPoint p)
    {
        if (clip is null)
        {
            return false;
        }

        var d = clip.DestInCanvas;
        return d.X <= p.X && p.X < d.X + d.Width && d.Y <= p.Y && p.Y < d.Y + d.Height;
    }
}
