using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Imaging;

public sealed class SharedCanvasTransformTests
{
    private static readonly WallMetrics Metrics = new();

    [Theory]
    [InlineData(400, 300, 204, 204, 0.68, -34, 0)]
    [InlineData(300, 400, 204, 204, 0.68, 0, -34)]
    [InlineData(400, 400, 204, 204, 0.51, 0, 0)]
    public void DefaultTransform_CoverFill_IsCentered(
        double imageWidth, double imageHeight, double canvasWidth, double canvasHeight,
        double expectedScale, double expectedX, double expectedY)
    {
        var transform = SharedCanvasTransform.DefaultTransform(
            new PixelSize(imageWidth, imageHeight), new DipSize(canvasWidth, canvasHeight));

        Assert.Equal(expectedScale, transform.Scale, 8);
        Assert.Equal(expectedX, transform.OffsetX, 8);
        Assert.Equal(expectedY, transform.OffsetY, 8);
    }

    [Fact]
    public void ClipFor_InsidePartialAndOutside_UsesCenteredContainPlacement()
    {
        var image = new PixelSize(400, 200);
        var canvas = new DipSize(200, 200);
        var fit = MemberImageGeometry.Calculate(canvas.Width, canvas.Height,
            image.Width, image.Height, 1, new MemberImageDisplay());
        var transform = new ImageTransform(fit.Scale, fit.Bounds.X, fit.Bounds.Y);

        var inside = SharedCanvasTransform.ClipFor(transform, image, new DipRect(10, 60, 50, 50));
        Assert.NotNull(inside);
        Assert.Equal(new DipRect(10, 60, 50, 50), inside!.DestInCanvas);
        Assert.Equal(new DipRect(0, 0, 50, 50), inside.DestInPartition);
        Assert.Equal(new DipRect(20, 20, 100, 100), inside.SourceRect);

        // The image occupies [50,150) vertically, so the top half of this viewport exposes background.
        var partial = SharedCanvasTransform.ClipFor(transform, image, new DipRect(0, 0, 200, 100));
        Assert.NotNull(partial);
        Assert.Equal(new DipRect(0, 50, 200, 50), partial!.DestInCanvas);
        Assert.Equal(new DipRect(0, 50, 200, 50), partial.DestInPartition);
        Assert.Equal(new DipRect(0, 0, 400, 100), partial.SourceRect);

        var outside = SharedCanvasTransform.ClipFor(transform, image, new DipRect(20, 0, 50, 25));
        Assert.Null(outside);
    }

    [Fact]
    public void ClipFor_GapStripBelongsToNoPartition_AndSourceStepMatchesGap()
    {
        var first = PartitionRect(new GridRect(0, 0, 1, 1));
        var second = PartitionRect(new GridRect(1, 0, 1, 1));
        var gap = second.X - (first.X + first.Width);
        Assert.Equal(Metrics.Gap, gap, 9);

        var image = new PixelSize(400, 200);
        var canvas = CanvasSize(new GridSize(2, 1));
        var transform = SharedCanvasTransform.DefaultTransform(image, canvas);
        var firstClip = SharedCanvasTransform.ClipFor(transform, image, first)!;
        var secondClip = SharedCanvasTransform.ClipFor(transform, image, second)!;

        var stripPoint = new DipPoint(first.X + first.Width + gap / 2, 22);
        Assert.False(Contains(first, stripPoint));
        Assert.False(Contains(second, stripPoint));

        var sourceGap = secondClip.SourceRect.X -
            (firstClip.SourceRect.X + firstClip.SourceRect.Width);
        Assert.Equal(gap / transform.Scale, sourceGap, 9);
    }

    private static DipSize CanvasSize(GridSize size) =>
        new(Metrics.WidthOf(size.Columns), Metrics.HeightOf(size.Rows));

    private static DipRect PartitionRect(GridRect rect) => Metrics.MemberRect(rect, 0, 0);

    private static bool Contains(DipRect rect, DipPoint point) =>
        rect.X <= point.X && point.X < rect.X + rect.Width &&
        rect.Y <= point.Y && point.Y < rect.Y + rect.Height;
}
