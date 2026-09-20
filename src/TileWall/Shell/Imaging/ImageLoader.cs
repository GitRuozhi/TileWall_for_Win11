using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TileWall.Shell.Imaging;

/// <summary>一次加载的成套位图资产（清晰前景 + 低清模糊后景）。</summary>
public sealed record GroupImageAssets(
    string ImageId,
    PixelSize SourcePixels,
    PixelSize BlurPixels,
    ImageSource SharpSource,
    ImageSource? BlurSource);

/// <summary>
/// 图片解码与降采样（M6 设计 §2.1 ImageLoader；§5.4 模糊后景 = G0 落地的零依赖软件方案：
/// 解码降采样（画布像素/8、上限 512）+ Core <see cref="SoftBlurBox"/> box blur×3，再编码 PNG 上屏）。
/// 大图按画布物理像素上限解码（§6.2 解码尺寸策略），杜绝大原图整幅进内存；
/// 失败一律返回 null（调用方接 CarouselScheduler.ReportLoadFailure 链，旧图与基准不动）。
/// 解码为 WinRT 异步链，不阻塞 UI 线程（任务书硬性要求 3：异步加载 + 占位）。
/// </summary>
public sealed class ImageLoader
{
    /// <summary>
    /// 加载一张候选图：返回 null = 加载失败（文件缺失/损坏/解码异常）。
    /// 必须在 UI 线程调用（产物为 UI 对象；await 续体回到调用线程）。
    /// </summary>
    public async Task<GroupImageAssets?> LoadAsync(string path, DipSize canvasDip, double rasterScale)
    {
        if (!Path.IsPathRooted(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var sourceWidth = decoder.OrientedPixelWidth;
            var sourceHeight = decoder.OrientedPixelHeight;
            if (sourceWidth == 0 || sourceHeight == 0)
            {
                return null;
            }

            // —— 清晰前景：按画布物理像素上限解码（BitmapImage 直接读源文件，XAML 缓存共享一份解码面） ——
            var sharpSource = CreateSharpSource(path, sourceWidth, sourceHeight, canvasDip, rasterScale);

            // —— 模糊后景：降采样 → Core 软模糊 → PNG 编码（§5.4 备选；变换固定 CoverFill，整组一张） ——
            var (blurWidth, blurHeight) = BlurTargetSize(sourceWidth, sourceHeight, canvasDip, rasterScale);
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)blurWidth,
                ScaledHeight = (uint)blurHeight,
                InterpolationMode = BitmapInterpolationMode.Linear,
            };
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            var blurred = SoftBlurBox.Apply(pixelData.DetachPixelData(), blurWidth, blurHeight, channelCount: 4, ImageDecodePolicy.BlurRadiusPx);

            var blurStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, blurStream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)blurWidth,
                (uint)blurHeight,
                96,
                96,
                blurred);
            await encoder.FlushAsync();
            blurStream.Seek(0);
            var blurSource = new BitmapImage();
            await blurSource.SetSourceAsync(blurStream);

            return new GroupImageAssets(
                path,
                new PixelSize(sourceWidth, sourceHeight),
                new PixelSize(blurWidth, blurHeight),
                sharpSource,
                blurSource);
        }
        catch (Exception ex) when (ex is
            FileNotFoundException or
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            System.Runtime.InteropServices.COMException or
            OperationCanceledException)
        {
            return null; // 失败链：旧图保留（§6.2）
        }
    }

    /// <summary>只读源图像素尺寸（属性窗预览编辑用；不解码模糊层）。</summary>
    public static async Task<PixelSize?> GetPixelSizeAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            return new PixelSize(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
        }
        catch (Exception ex) when (ex is
            FileNotFoundException or
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static BitmapImage CreateSharpSource(string path, double sourceWidth, double sourceHeight, DipSize canvasDip, double rasterScale)
    {
        var scale = rasterScale > 0 && double.IsFinite(rasterScale) ? rasterScale : 1.0;
        var maxEdge = (int)Math.Min(
            ImageDecodePolicy.SharpDecodeMaxEdgePx,
            Math.Ceiling(Math.Max(canvasDip.Width, canvasDip.Height) * scale));
        var bitmap = new BitmapImage(new Uri(path))
        {
            DecodePixelType = DecodePixelType.Physical, // 上限按物理像素计（DIP×光栅化），杜绝 DPI 放大超限
        };
        if (maxEdge >= 1)
        {
            if (sourceWidth >= sourceHeight)
            {
                bitmap.DecodePixelWidth = maxEdge; // 只给长边 → 保持纵横比降采样
            }
            else
            {
                bitmap.DecodePixelHeight = maxEdge;
            }
        }

        return bitmap;
    }

    private static (int Width, int Height) BlurTargetSize(double sourceWidth, double sourceHeight, DipSize canvasDip, double rasterScale)
    {
        var scale = rasterScale > 0 && double.IsFinite(rasterScale) ? rasterScale : 1.0;
        var targetWidth = Math.Clamp(
            canvasDip.Width * scale / ImageDecodePolicy.BlurTargetScaleDivisor,
            ImageDecodePolicy.BlurMinEdgePx,
            ImageDecodePolicy.BlurMaxEdgePx);
        var targetHeight = Math.Clamp(
            canvasDip.Height * scale / ImageDecodePolicy.BlurTargetScaleDivisor,
            ImageDecodePolicy.BlurMinEdgePx,
            ImageDecodePolicy.BlurMaxEdgePx);
        var fit = Math.Min(Math.Min(targetWidth / sourceWidth, targetHeight / sourceHeight), 1.0);
        return (
            Math.Max(1, (int)Math.Round(sourceWidth * fit)),
            Math.Max(1, (int)Math.Round(sourceHeight * fit)));
    }
}
