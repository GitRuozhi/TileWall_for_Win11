using TileWall.Core.Grid;

namespace TileWall.Core.Imaging;

/// <summary>适配基准两档（M6 设计 §4.1 / 产品 §10.4；无其他模式）。</summary>
public enum FitMode
{
    /// <summary>居中填满（默认）：等比放大至覆盖整块画布，溢出裁掉。</summary>
    CoverFill,

    /// <summary>完整适应：等比缩至整图完整可见，可能留白（留白露后景）。</summary>
    FitAll,
}

/// <summary>
/// 逐图变换不可变值（M6 设计 §4.1；持久化最小集）：等比因子 k（画布 DIP / 源像素）
/// 与图片左上角在共享画布中的 DIP 偏移。
/// 非破坏（XF-8，类型级保证）：本程序集无任何文件写出能力——变换 API 不触 IFileStore/File/Stream。
/// </summary>
public readonly record struct ImageTransform(double Scale, double OffsetX, double OffsetY);

/// <summary>
/// 共享画布变换纯函数族（M6 设计 §4.2；V-03 主对象）。全部无状态：同输入必同输出，
/// 无时钟/随机/IO/线程状态。跨块对齐由「唯一正向映射」<see cref="CanvasPointToSource"/> 构造性保证
/// （XF-5）——任何分块都不引入第二套映射，缝隙条带不属于任何分块矩形 → 天然遮盖/重现（§10.3）。
/// </summary>
public static class SharedCanvasTransform
{
    /// <summary>缩放界限（XF-3）：k ∈ [MinScaleRatio·k₀, MaxScaleRatio·k₀]，k₀ = CoverFill 居中因子。</summary>
    public const double MinScaleRatio = 0.1;
    public const double MaxScaleRatio = 10.0;

    /// <summary>初始变换（居中）：cover k=max(CW/iw, CH/ih)、fit k=min(...)；offset = 居中余量（可为负=溢出）。</summary>
    public static ImageTransform DefaultTransform(PixelSize image, DipSize canvas, FitMode mode)
    {
        Validate(image, canvas);
        var cover = Math.Max(canvas.Width / image.Width, canvas.Height / image.Height);
        var k = mode == FitMode.CoverFill ? cover : Math.Min(canvas.Width / image.Width, canvas.Height / image.Height);
        return new ImageTransform(k, (canvas.Width - (image.Width * k)) / 2, (canvas.Height - (image.Height * k)) / 2);
    }

    /// <summary>重置 = 当前 FitMode 的居中初始（XF 与 Reset∘Reset 幂等由同函数保证）。</summary>
    public static ImageTransform Reset(PixelSize image, DipSize canvas, FitMode mode) =>
        DefaultTransform(image, canvas, mode);

    /// <summary>
    /// 等比缩放，锚点不动性（XF-2）：锚点 a 处的画布点缩放前后对应同一源点。
    /// 界限：k' 截断在 [MinScaleRatio·baseScale, MaxScaleRatio·baseScale]（XF-3，越界返回界限值，无异常路径）；
    /// baseScale = DefaultTransform(同图, CoverFill).Scale（调用方经 <see cref="DefaultTransform"/> 或
    /// <see cref="CoverFillScale"/> 求得——设计 §4.2 签名的 factor/anchor 之外补 k₀ 参数，因界限定义依赖它）。
    /// </summary>
    public static ImageTransform ZoomAt(ImageTransform t, double factor, DipPoint anchor, double baseScale)
    {
        if (!double.IsFinite(baseScale) || baseScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseScale), baseScale, "基准因子必须为正有限值。");
        }

        if (!double.IsFinite(factor) || factor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "缩放系数必须为正有限值。");
        }

        var clamped = Math.Clamp(t.Scale * factor, MinScaleRatio * baseScale, MaxScaleRatio * baseScale);
        var f = t.Scale == 0 ? 1 : clamped / t.Scale;
        return new ImageTransform(
            clamped,
            anchor.X - ((anchor.X - t.OffsetX) * f),
            anchor.Y - ((anchor.Y - t.OffsetY) * f));
    }

    /// <summary>画布 DIP / 源像素 的 CoverFill 因子（XF-3 的 k₀）。</summary>
    public static double CoverFillScale(PixelSize image, DipSize canvas)
    {
        Validate(image, canvas);
        return Math.Max(canvas.Width / image.Width, canvas.Height / image.Height);
    }

    /// <summary>自由平移，不做边界钳制（§10.4 明文「清晰前景未覆盖的区域」是合法状态）。</summary>
    public static ImageTransform Translate(ImageTransform t, double dx, double dy) =>
        new(t.Scale, t.OffsetX + dx, t.OffsetY + dy);

    /// <summary>画布点 → 源像素（唯一正向映射；src = ((x−offset.x)/k, (y−offset.y)/k)）。</summary>
    public static DipPoint CanvasPointToSource(ImageTransform t, DipPoint canvasPoint) =>
        new((canvasPoint.X - t.OffsetX) / t.Scale, (canvasPoint.Y - t.OffsetY) / t.Scale);

    /// <summary>
    /// 分块裁剪换算（§4.2）：dest = 分块矩形 ∩ 图像画布矩形（半开区间），src = 同一正向映射的逆用。
    /// 缝隙条带不在任何分块矩形内 → 不产生裁剪；返回 null = 完全不相交（整块露后景）。
    /// </summary>
    public static PartitionClip? ClipFor(ImageTransform t, PixelSize image, DipRect partitionRect)
    {
        if (!image.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(image), image, "源图像素尺寸必须为正有限值。");
        }

        if (t.Scale <= 0 || !double.IsFinite(t.Scale))
        {
            throw new ArgumentOutOfRangeException(nameof(t), t, "变换因子必须为正有限值。");
        }

        var imageLeft = t.OffsetX;
        var imageTop = t.OffsetY;
        var imageRight = t.OffsetX + (image.Width * t.Scale);
        var imageBottom = t.OffsetY + (image.Height * t.Scale);

        var x0 = Math.Max(partitionRect.X, imageLeft);
        var y0 = Math.Max(partitionRect.Y, imageTop);
        var x1 = Math.Min(partitionRect.X + partitionRect.Width, imageRight);
        var y1 = Math.Min(partitionRect.Y + partitionRect.Height, imageBottom);
        if (x1 <= x0 || y1 <= y0)
        {
            return null; // 半开区间不相交（XF-4 第三态）
        }

        var destInCanvas = new DipRect(x0, y0, x1 - x0, y1 - y0);
        var destInPartition = new DipRect(x0 - partitionRect.X, y0 - partitionRect.Y, x1 - x0, y1 - y0);
        var sourceRect = new DipRect(
            (x0 - t.OffsetX) / t.Scale,
            (y0 - t.OffsetY) / t.Scale,
            (x1 - x0) / t.Scale,
            (y1 - y0) / t.Scale);
        return new PartitionClip(destInPartition, destInCanvas, sourceRect);
    }

    /// <summary>默认态判定（XF-9 落盘剔除的判定核心）：与同图同画布同档的居中初始逐字段相等。</summary>
    public static bool IsDefault(ImageTransform t, PixelSize image, DipSize canvas, FitMode mode) =>
        t == DefaultTransform(image, canvas, mode);

    private static void Validate(PixelSize image, DipSize canvas)
    {
        if (!image.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(image), image, "源图像素尺寸必须为正有限值。");
        }

        if (!canvas.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(canvas), canvas, "画布 DIP 尺寸必须为正有限值。");
        }
    }
}
