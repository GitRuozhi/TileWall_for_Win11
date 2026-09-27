using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Imaging;

/// <summary>
/// T-XFORM-INIT / T-XFORM-EDIT / T-XFORM-CLIP（M6 设计 §11.1；V-03）：
/// DefaultTransform 横/竖/方图 × 两档 × 方/长画布矩阵精确值与 8×25 极限画布（XF-1）；
/// ZoomAt 锚点不动性与界限截断（XF-2/XF-3）；Translate 复合可逆；Reset 幂等；
/// ClipFor 三态与缝隙条带不归于任何分块（XF-4/§10.3）。
/// </summary>
public sealed class SharedCanvasTransformTests
{
    private static readonly WallMetrics M = new(); // current 48/4/52 DIP geometry

    // Current 48/4/52 DIP geometry: 4×4=204×204, 5×4=256×204, 8×25=412×1296.
    private static readonly DipSize Canvas4x4 = CanvasSize(new GridSize(4, 4));
    private static readonly DipSize Canvas5x4 = CanvasSize(new GridSize(5, 4));
    private static readonly DipSize Canvas8x25 = CanvasSize(new GridSize(8, 25));

    private static readonly PixelSize Landscape = new(400, 300);
    private static readonly PixelSize Portrait = new(300, 400);
    private static readonly PixelSize Square = new(400, 400);

    // ————————————————————————————— T-XFORM-INIT（XF-1 适配矩阵） —————————————————————————————

    [Theory]
    [InlineData(400, 300, 204, 204, 0.68, -34, 0)]    // 横图入方画布：cover 横向溢出
    [InlineData(300, 400, 204, 204, 0.68, 0, -34)]    // 竖图入方画布：cover 纵向溢出
    [InlineData(400, 400, 204, 204, 0.51, 0, 0)]                      // 方图入方画布：恰填满
    [InlineData(400, 300, 256, 204, 0.68, -8, 0)]     // 横图入长画布：横向溢出
    [InlineData(300, 400, 256, 204, 0.853333333, 0, -68.66666667)]    // 竖图入长画布：cover 由宽比决定、纵向溢出
    public void CoverFill_Matrix_HasExactScaleAndCenteredOffset(
        double iw, double ih, double cw, double ch, double k, double ox, double oy)
    {
        var t = SharedCanvasTransform.DefaultTransform(new PixelSize(iw, ih), new DipSize(cw, ch), FitMode.CoverFill);
        Assert.Equal(k, t.Scale, 6);
        Assert.Equal(ox, t.OffsetX, 6);
        Assert.Equal(oy, t.OffsetY, 6);
    }

    [Theory]
    [InlineData(400, 300, 204, 204, 0.51, 0, 25.5)]   // 横图 fit：纵向留白
    [InlineData(300, 400, 204, 204, 0.51, 25.5, 0)]   // 竖图 fit：横向留白
    [InlineData(400, 400, 204, 204, 0.51, 0, 0)]
    [InlineData(400, 300, 256, 204, 0.64, 0, 6)]    // 长画布 fit：纵向留白
    public void FitAll_Matrix_HasExactScaleAndCenteredOffset(
        double iw, double ih, double cw, double ch, double k, double ox, double oy)
    {
        var t = SharedCanvasTransform.DefaultTransform(new PixelSize(iw, ih), new DipSize(cw, ch), FitMode.FitAll);
        Assert.Equal(k, t.Scale, 9);
        Assert.Equal(ox, t.OffsetX, 9);
        Assert.Equal(oy, t.OffsetY, 9);
    }

    [Fact]
    public void Canvas_8x25_ExtremeGroup_MatchesP1Formula()
    {
        Assert.Equal(412, Canvas8x25.Width, 9);   // 8·48 + 7·4
        Assert.Equal(1296, Canvas8x25.Height, 9); // 25·48 + 24·4
        Assert.Equal(204, Canvas4x4.Width, 9);
        Assert.Equal(256, Canvas5x4.Width, 9);
    }

    [Fact]
    public void PartitionRect_MatchesPresenterMath_ByteForByte()
    {
        // The member rectangle uses the same pitch as the current wall layout.
        var p = new GridRect(2, 1, 2, 3);
        var r = PartitionRect(p);
        Assert.Equal(2 * M.Pitch, r.X, 9);
        Assert.Equal(1 * M.Pitch, r.Y, 9);
        Assert.Equal(M.WidthOf(2), r.Width, 9);
        Assert.Equal(M.HeightOf(3), r.Height, 9);
    }

    // ————————————————————————————— T-XFORM-EDIT（XF-2/XF-3） —————————————————————————————

    [Theory]
    [InlineData(94, 94, 0.5)]
    [InlineData(94, 94, 1.25)]
    [InlineData(94, 94, 2)]
    [InlineData(0, 0, 0.5)]
    [InlineData(0, 0, 2)]
    [InlineData(123, 77, 1.25)]
    public void ZoomAt_AnchorImmutability_SourcePointUnderAnchorUnchanged(double ax, double ay, double factor)
    {
        var t = SharedCanvasTransform.DefaultTransform(Landscape, Canvas4x4, FitMode.CoverFill);
        var anchor = new DipPoint(ax, ay);
        var zoomed = SharedCanvasTransform.ZoomAt(t, factor, anchor, SharedCanvasTransform.CoverFillScale(Landscape, Canvas4x4));

        var before = SharedCanvasTransform.CanvasPointToSource(t, anchor);
        var after = SharedCanvasTransform.CanvasPointToSource(zoomed, anchor);
        Assert.Equal(before.X, after.X, 9);
        Assert.Equal(before.Y, after.Y, 9);
    }

    [Fact]
    public void ZoomAt_BoundsClamp_ReturnsExactLimit_NoException()
    {
        var baseScale = SharedCanvasTransform.CoverFillScale(Landscape, Canvas4x4);
        var t = SharedCanvasTransform.DefaultTransform(Landscape, Canvas4x4, FitMode.CoverFill);

        var maxed = SharedCanvasTransform.ZoomAt(t, 1e6, new DipPoint(0, 0), baseScale);
        Assert.Equal(SharedCanvasTransform.MaxScaleRatio * baseScale, maxed.Scale, 9);

        var mined = SharedCanvasTransform.ZoomAt(t, 1e-9, new DipPoint(0, 0), baseScale);
        Assert.Equal(SharedCanvasTransform.MinScaleRatio * baseScale, mined.Scale, 9);
    }

    [Fact]
    public void Translate_ComposeInverse_IsIdentity()
    {
        var t = SharedCanvasTransform.DefaultTransform(Landscape, Canvas4x4, FitMode.FitAll);
        var moved = SharedCanvasTransform.Translate(SharedCanvasTransform.Translate(t, 33.5, -12.25), -33.5, 12.25);
        Assert.Equal(t, moved);
    }

    [Fact]
    public void Reset_IsIdempotent_And_TwoModesMatchSpec()
    {
        // Reset 是「回到居中初始」的纯映射：同参数重复调用（Reset∘Reset）结果恒等
        var once = SharedCanvasTransform.Reset(Landscape, Canvas4x4, FitMode.CoverFill);
        var twice = SharedCanvasTransform.Reset(Landscape, Canvas4x4, FitMode.CoverFill);
        Assert.Equal(once, twice);
        Assert.Equal(SharedCanvasTransform.DefaultTransform(Landscape, Canvas4x4, FitMode.CoverFill), once);

        // 两档语义：FitAll 的 Reset 回完整适应档（§10 两档）
        var fitAll = SharedCanvasTransform.Reset(Landscape, Canvas4x4, FitMode.FitAll);
        Assert.Equal(SharedCanvasTransform.DefaultTransform(Landscape, Canvas4x4, FitMode.FitAll), fitAll);
        Assert.NotEqual(once, fitAll);
    }

    // ————————————————————————————— T-XFORM-CLIP（XF-4/缝隙） —————————————————————————————

    [Fact]
    public void ClipFor_ThreeStates_InsidePartialOutside()
    {
        var image = new PixelSize(200, 200);
        var canvas = new DipSize(200, 200);
        var t = SharedCanvasTransform.DefaultTransform(image, canvas, FitMode.CoverFill); // 恰填满 200×200

        // 完全在图内
        var inside = SharedCanvasTransform.ClipFor(t, image, new DipRect(10, 10, 50, 50));
        Assert.NotNull(inside);
        Assert.Equal(new DipRect(10, 10, 50, 50), inside!.DestInCanvas);
        Assert.Equal(new DipRect(0, 0, 50, 50), inside.DestInPartition); // 相对分块左上角
        Assert.Equal(new DipRect(10, 10, 50, 50), inside.SourceRect); // k=1 时源=画布

        // 与图部分相交（图被平移到左上角外溢）
        var shifted = SharedCanvasTransform.Translate(t, -30, -30); // 图占 [-30,170)²
        var partial = SharedCanvasTransform.ClipFor(shifted, image, new DipRect(100, 100, 100, 100));
        Assert.NotNull(partial);
        Assert.Equal(new DipRect(100, 100, 70, 70), partial!.DestInCanvas);

        // 完全在图外 → null（半开区间）
        var outside = SharedCanvasTransform.ClipFor(t, image, new DipRect(300, 300, 50, 50));
        Assert.Null(outside);
    }

    [Fact]
    public void ClipFor_GapStrip_BelongsToNoPartition_And_SourceStepsByGapOverK()
    {
        // 2×1 group: canvas 100×48; P0=(0,0,48,48), P1=(52,0,48,48), gap x∈[48,52)
        var p0 = PartitionRect(new GridRect(0, 0, 1, 1));
        var p1 = PartitionRect(new GridRect(1, 0, 1, 1));
        Assert.Equal(M.Gap, p1.X - (p0.X + p0.Width), 9); // 缝隙恰 4 DIP

        var image = new PixelSize(400, 200);
        var canvas = CanvasSize(new GridSize(2, 1));
        var t = SharedCanvasTransform.DefaultTransform(image, canvas, FitMode.CoverFill); // k=0.25, covers canvas

        var clip0 = SharedCanvasTransform.ClipFor(t, image, p0)!;
        var clip1 = SharedCanvasTransform.ClipFor(t, image, p1)!;

        // 缝隙条带不属于任何 R_p（几何事实）：条带内点不在任何分块矩形内
        var stripPoint = new DipPoint(50, 22);
        Assert.False(Contains(p0, stripPoint));
        Assert.False(Contains(p1, stripPoint));

        // Source gap between adjacent member rectangles equals 4/0.25 = 16 pixels.
        var sourceGap = clip1.SourceRect.X - (clip0.SourceRect.X + clip0.SourceRect.Width);
        Assert.Equal(M.Gap / t.Scale, sourceGap, 9);
        Assert.Equal(208, clip1.SourceRect.X, 9); // 52/0.25
    }

    [Fact]
    public void ClipFor_ImageTranslatedFullyOut_AllPartitionsNull()
    {
        var image = new PixelSize(400, 300);
        var canvas = Canvas4x4;
        var t = SharedCanvasTransform.DefaultTransform(image, canvas, FitMode.CoverFill);
        var moved = SharedCanvasTransform.Translate(t, canvas.Width * 2, 0); // 全出画布
        foreach (var p in PartitionLayout.FullGrid(new GridSize(4, 4)).Partitions)
        {
            Assert.Null(SharedCanvasTransform.ClipFor(moved, image, PartitionRect(p)));
        }
    }

    private static DipSize CanvasSize(GridSize size) => new(M.WidthOf(size.Columns), M.HeightOf(size.Rows));
    private static DipRect PartitionRect(GridRect rect) => M.MemberRect(rect, 0, 0);

    private static bool Contains(DipRect r, DipPoint p) =>
        r.X <= p.X && p.X < r.X + r.Width && r.Y <= p.Y && p.Y < r.Y + r.Height;
}
