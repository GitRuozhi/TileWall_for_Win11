using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Imaging;

/// <summary>Verifies partition clips preserve one centered source mapping and the source gap between cells.</summary>
public sealed class TransformAlignmentTests
{
    private static readonly WallMetrics Metrics = new();
    private static readonly PixelSize Source = new(400, 300);

    public static IEnumerable<object[]> NamedPartitions()
    {
        yield return [PartitionLayout.FullGrid(new GridSize(2, 2)).Partitions, "FullGrid(2×2)"];
        yield return [PartitionLayout.FullGrid(new GridSize(4, 4)).Partitions, "FullGrid(4×4)"];
        yield return [new List<GridRect> { new(0, 0, 2, 1), new(0, 1, 1, 2), new(1, 1, 2, 2) }, "L shape"];
    }

    [Theory]
    [MemberData(nameof(NamedPartitions))]
    public void NamedPartitions_ClipsMatchOneCenteredSourceMapping(IReadOnlyList<GridRect> partitions, string _)
    {
        AssertIsAligned(ExtentOf(partitions), partitions);
    }

    [Fact]
    public void RandomLegalPartitions_500Cases_ClipsStayAlignedAndGapStepsMatch()
    {
        var random = new Random(20260921);
        for (var caseIndex = 0; caseIndex < 500; caseIndex++)
        {
            var size = new GridSize(2 + caseIndex % 4, 2 + caseIndex % 3);
            AssertIsAligned(size, RandomPartition(size, random));
        }
    }

    [Fact]
    public void WallRemoval_MergedPartitionReproducesVisibleSourcePoints()
    {
        var before = new List<GridRect>
        {
            new(0, 0, 1, 1), new(1, 0, 1, 1),
            new(0, 1, 1, 1), new(1, 1, 1, 1),
        };
        var after = new List<GridRect>
        {
            new(0, 0, 2, 1), new(0, 1, 1, 1), new(1, 1, 1, 1),
        };
        var canvas = CanvasSize(new GridSize(2, 2));
        var transform = CenteredTransform(canvas,
            new MemberImageDisplay { Mode = ImageDisplayMode.Custom, CustomScale = 1.4 });
        var oldRects = before.Select(PartitionRect).Take(2).ToArray();
        var mergedRect = PartitionRect(after[0]);
        var mergedClip = SharedCanvasTransform.ClipFor(transform, Source, mergedRect);
        Assert.NotNull(mergedClip);

        var sampled = 0;
        foreach (var oldRect in oldRects)
        {
            var oldClip = SharedCanvasTransform.ClipFor(transform, Source, oldRect);
            if (oldClip is null) continue;

            for (var x = oldClip.DestInCanvas.X; x < oldClip.DestInCanvas.X + oldClip.DestInCanvas.Width; x += 4)
            for (var y = oldClip.DestInCanvas.Y; y < oldClip.DestInCanvas.Y + oldClip.DestInCanvas.Height; y += 4)
            {
                var point = new DipPoint(x, y);
                Assert.True(Contains(mergedRect, point));
                Assert.True(ContainsIn(mergedClip, point));
                var oldSource = SourcePoint(oldClip, point);
                var mergedSource = SourcePoint(mergedClip!, point);
                Assert.Equal(oldSource.X, mergedSource.X, 6);
                Assert.Equal(oldSource.Y, mergedSource.Y, 6);
                sampled++;
            }
        }

        Assert.True(sampled > 0);
    }

    private static void AssertIsAligned(GridSize size, IReadOnlyList<GridRect> partitions)
    {
        var canvas = CanvasSize(size);
        var random = new Random(size.Columns * 100 + size.Rows);
        var transform = RandomCenteredTransform(canvas, random);
        var rects = partitions.Select(PartitionRect).ToList();
        var clips = rects.Select(rect => SharedCanvasTransform.ClipFor(transform, Source, rect)).ToList();

        foreach (var clip in clips)
        {
            if (clip is null) continue;
            Assert.Equal(1 / transform.Scale,
                clip.SourceRect.Width / clip.DestInCanvas.Width, 6);
            Assert.Equal(1 / transform.Scale,
                clip.SourceRect.Height / clip.DestInCanvas.Height, 6);
        }

        for (var x = 0.0; x < canvas.Width; x += 4)
        for (var y = 0.0; y < canvas.Height; y += 4)
        {
            var point = new DipPoint(x, y);
            var owner = -1;
            for (var i = 0; i < rects.Count; i++)
            {
                if (!Contains(rects[i], point)) continue;
                Assert.True(owner < 0, $"Canvas point ({x},{y}) belongs to overlapping partitions.");
                owner = i;

                if (!ContainsIn(clips[i], point)) continue;
                var source = SourcePoint(clips[i]!, point);
                Assert.InRange(source.X, 0, Source.Width);
                Assert.InRange(source.Y, 0, Source.Height);
                Assert.Equal((point.X - transform.OffsetX) / transform.Scale, source.X, 6);
                Assert.Equal((point.Y - transform.OffsetY) / transform.Scale, source.Y, 6);
            }
        }

        for (var i = 0; i < partitions.Count; i++)
        for (var j = 0; j < partitions.Count; j++)
        {
            if (i == j) continue;
            var a = partitions[i];
            var b = partitions[j];
            if (a.Right == b.Column && a.Row < b.Bottom && b.Row < a.Bottom)
                AssertGapStep(clips[i], clips[j], rects[i], rects[j], vertical: true, transform);
            if (a.Bottom == b.Row && a.Column < b.Right && b.Column < a.Right)
                AssertGapStep(clips[i], clips[j], rects[i], rects[j], vertical: false, transform);
        }
    }

    private static void AssertGapStep(
        PartitionClip? clipA, PartitionClip? clipB, DipRect rectA, DipRect rectB,
        bool vertical, ImageTransform transform)
    {
        if (clipA is null || clipB is null) return;
        if (vertical)
        {
            var seamA = rectA.X + rectA.Width;
            var seamB = rectB.X;
            if (clipA.DestInCanvas.X + clipA.DestInCanvas.Width < seamA || clipB.DestInCanvas.X > seamB)
                return;
            var sourceRightOfA = clipA.SourceRect.X + clipA.SourceRect.Width;
            Assert.Equal(Metrics.Gap / transform.Scale, clipB.SourceRect.X - sourceRightOfA, 6);
        }
        else
        {
            var seamA = rectA.Y + rectA.Height;
            var seamB = rectB.Y;
            if (clipA.DestInCanvas.Y + clipA.DestInCanvas.Height < seamA || clipB.DestInCanvas.Y > seamB)
                return;
            var sourceBottomOfA = clipA.SourceRect.Y + clipA.SourceRect.Height;
            Assert.Equal(Metrics.Gap / transform.Scale, clipB.SourceRect.Y - sourceBottomOfA, 6);
        }
    }

    private static List<GridRect> RandomPartition(GridSize size, Random random)
    {
        var rectangles = new List<GridRect> { new(0, 0, size.Columns, size.Rows) };
        var targetCount = 1 + random.Next(Math.Min(8, size.Columns * size.Rows));
        while (rectangles.Count < targetCount)
        {
            var index = -1;
            var bestArea = 0;
            for (var i = 0; i < rectangles.Count; i++)
            {
                var area = rectangles[i].Width * rectangles[i].Height;
                if (area > bestArea) { bestArea = area; index = i; }
            }
            if (index < 0) break;

            var rect = rectangles[index];
            var canSplitVertically = rect.Width >= 2;
            var canSplitHorizontally = rect.Height >= 2;
            if (!canSplitVertically && !canSplitHorizontally) break;

            if (canSplitVertically && (!canSplitHorizontally || random.Next(2) == 0))
            {
                var cut = 1 + random.Next(rect.Width - 1);
                rectangles[index] = new GridRect(rect.Column, rect.Row, cut, rect.Height);
                rectangles.Insert(index + 1, new GridRect(rect.Column + cut, rect.Row, rect.Width - cut, rect.Height));
            }
            else
            {
                var cut = 1 + random.Next(rect.Height - 1);
                rectangles[index] = new GridRect(rect.Column, rect.Row, rect.Width, cut);
                rectangles.Insert(index + 1, new GridRect(rect.Column, rect.Row + cut, rect.Width, rect.Height - cut));
            }
        }

        return rectangles;
    }

    private static ImageTransform RandomCenteredTransform(DipSize canvas, Random random)
    {
        var display = new MemberImageDisplay
        {
            Mode = (ImageDisplayMode)random.Next(Enum.GetValues<ImageDisplayMode>().Length),
            CustomScale = 0.65 + random.NextDouble() * 1.5,
        };
        return CenteredTransform(canvas, display);
    }

    private static ImageTransform CenteredTransform(DipSize canvas, MemberImageDisplay display)
    {
        var placement = MemberImageGeometry.Calculate(canvas.Width, canvas.Height,
            Source.Width, Source.Height, 1, display);
        return new ImageTransform(placement.Scale, placement.Bounds.X, placement.Bounds.Y);
    }

    private static DipPoint SourcePoint(PartitionClip clip, DipPoint canvasPoint) => new(
        clip.SourceRect.X + (canvasPoint.X - clip.DestInCanvas.X) *
        (clip.SourceRect.Width / clip.DestInCanvas.Width),
        clip.SourceRect.Y + (canvasPoint.Y - clip.DestInCanvas.Y) *
        (clip.SourceRect.Height / clip.DestInCanvas.Height));

    private static GridSize ExtentOf(IReadOnlyList<GridRect> partitions) =>
        new(partitions.Max(rect => rect.Right), partitions.Max(rect => rect.Bottom));

    private static DipSize CanvasSize(GridSize size) =>
        new(Metrics.WidthOf(size.Columns), Metrics.HeightOf(size.Rows));

    private static DipRect PartitionRect(GridRect rect) => Metrics.MemberRect(rect, 0, 0);

    private static bool Contains(DipRect rect, DipPoint point) =>
        rect.X <= point.X && point.X < rect.X + rect.Width &&
        rect.Y <= point.Y && point.Y < rect.Y + rect.Height;

    private static bool ContainsIn(PartitionClip? clip, DipPoint point) =>
        clip is { } value && Contains(value.DestInCanvas, point);
}
