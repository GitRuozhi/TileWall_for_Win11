using TileWall.Core.Grid;
using TileWall.Core.Imaging;
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
    private static readonly GridMetrics M = GridMetrics.Default; // 96/8 占位（拍板 Q2）

    // 画布（P1 §1.1 公式运行时计算）：4×4=408×408、5×4=512×408、8×25=824×2592
    private static readonly DipSize Canvas4x4 = SharedCanvas.CanvasSize(new GridSize(4, 4), M);
    private static readonly DipSize Canvas5x4 = SharedCanvas.CanvasSize(new GridSize(5, 4), M);
    private static readonly DipSize Canvas8x25 = SharedCanvas.CanvasSize(new GridSize(8, 25), M);

    private static readonly PixelSize Landscape = new(400, 300);
    private static readonly PixelSize Portrait = new(300, 400);
    private static readonly PixelSize Square = new(400, 400);

    // ————————————————————————————— T-XFORM-INIT（XF-1 适配矩阵） —————————————————————————————

    [Theory]
    [InlineData(400, 300, 408, 408, 1.36, -68, 0)]    // 横图入方画布：cover 横向溢出
    [InlineData(300, 400, 408, 408, 1.36, 0, -68)]    // 竖图入方画布：cover 纵向溢出
    [InlineData(400, 400, 408, 408, 1.02, 0, 0)]      // 方图入方画布：恰填满
    [InlineData(400, 300, 512, 408, 1.36, -16, 0)]    // 横图入长画布：横向溢出
    [InlineData(300, 400, 512, 408, 1.706666667, 0, -137.33333333)] // 竖图入长画布：cover 由宽比决定、纵向溢出
    public void CoverFill_Matrix_HasExactScaleAndCenteredOffset(
        double iw, double ih, double cw, double ch, double k, double ox, double oy)
    {
        var t = SharedCanvasTransform.DefaultTransform(new PixelSize(iw, ih), new DipSize(cw, ch), FitMode.CoverFill);
        Assert.Equal(k, t.Scale, 6);
        Assert.Equal(ox, t.OffsetX, 6);
        Assert.Equal(oy, t.OffsetY, 6);
    }

    [Theory]
    [InlineData(400, 300, 408, 408, 1.02, 0, 51)]     // 横图 fit：纵向留白
    [InlineData(300, 400, 408, 408, 1.02, 51, 0)]     // 竖图 fit：横向留白
    [InlineData(400, 400, 408, 408, 1.02, 0, 0)]
    [InlineData(400, 300, 512, 408, 1.28, 0, 12)]     // 长画布 fit：纵向留白
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
        Assert.Equal(824, Canvas8x25.Width, 9);   // 8·96 + 7·8
        Assert.Equal(2592, Canvas8x25.Height, 9); // 25·96 + 24·8
        Assert.Equal(408, Canvas4x4.Width, 9);
        Assert.Equal(512, Canvas5x4.Width, 9);
    }

    [Fact]
    public void PartitionRect_MatchesPresenterMath_ByteForByte()
    {
        // R_p 与 WallPresenter 现行定位（p.Column·Pitch）逐字一致（§3.1）
        var p = new GridRect(2, 1, 2, 3);
        var r = SharedCanvas.PartitionRect(p, M);
        Assert.Equal(2 * M.Pitch, r.X, 9);
        Assert.Equal(1 * M.Pitch, r.Y, 9);
        Assert.Equal(M.WidthOf(2), r.Width, 9);
        Assert.Equal(M.HeightOf(3), r.Height, 9);
    }

    // ————————————————————————————— T-XFORM-EDIT（XF-2/XF-3） —————————————————————————————

    [Theory]
    [InlineData(204, 204, 0.5)]
    [InlineData(204, 204, 1.25)]
    [InlineData(204, 204, 2)]
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
        // 2×1 组：画布 200×96；P0=(0,0,96,96)、P1=(104,0,96,96)，缝隙 x∈[96,104)
        var p0 = SharedCanvas.PartitionRect(new GridRect(0, 0, 1, 1), M);
        var p1 = SharedCanvas.PartitionRect(new GridRect(1, 0, 1, 1), M);
        Assert.Equal(M.Gap, p1.X - (p0.X + p0.Width), 9); // 缝隙恰 8 DIP

        var image = new PixelSize(400, 200);
        var canvas = SharedCanvas.CanvasSize(new GridSize(2, 1), M);
        var t = SharedCanvasTransform.DefaultTransform(image, canvas, FitMode.CoverFill); // k=0.5，覆盖整画布

        var clip0 = SharedCanvasTransform.ClipFor(t, image, p0)!;
        var clip1 = SharedCanvasTransform.ClipFor(t, image, p1)!;

        // 缝隙条带不属于任何 R_p（几何事实）：条带内点不在任何分块矩形内
        var stripPoint = new DipPoint(100, 48);
        Assert.False(Contains(p0, stripPoint));
        Assert.False(Contains(p1, stripPoint));

        // 共享边两侧源坐标差恰为 缝隙/k（XF-5②）：P0 源右端 → P1 源左端差 = 8/0.5 = 16
        var sourceGap = clip1.SourceRect.X - (clip0.SourceRect.X + clip0.SourceRect.Width);
        Assert.Equal(M.Gap / t.Scale, sourceGap, 9);
        Assert.Equal(208, clip1.SourceRect.X, 9); // (104−0)/0.5
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
            Assert.Null(SharedCanvasTransform.ClipFor(moved, image, SharedCanvas.PartitionRect(p, M)));
        }
    }

    private static bool Contains(DipRect r, DipPoint p) =>
        r.X <= p.X && p.X < r.X + r.Width && r.Y <= p.Y && p.Y < r.Y + r.Height;
}
