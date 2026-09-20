namespace TileWall.Core.Imaging;

/// <summary>
/// 软模糊核（M6 设计 §5.4 备选方案的 Core 纯函数）：可分离 box blur × <see cref="Passes"/> 次
/// （三 pass box ≈ 高斯）。输入输出 = 紧凑像素缓冲（每像素 channelCount 字节），尺寸不变。
/// 边缘 = 夹取（clamp）采样。全零依赖、确定性、无 IO——T-BLURCORE 的断言对象。
/// </summary>
public static class SoftBlurBox
{
    /// <summary>pass 数（box blur ×3 近似高斯；§5.4）。</summary>
    public const int Passes = 3;

    /// <summary>
    /// 对像素缓冲做就地语义的三 pass box blur（返回新数组，输入不改）。
    /// radius ≤ 0 → 原样拷贝（恒等）。缓冲长度必须恰为 width·height·channelCount。
    /// </summary>
    public static byte[] Apply(byte[] pixels, int width, int height, int channelCount, int radius)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        if (pixels.Length != width * height * channelCount)
        {
            throw new ArgumentException($"缓冲长度 {pixels.Length} 与 {width}×{height}×{channelCount} 不符。", nameof(pixels));
        }

        var output = new byte[pixels.Length];
        if (radius <= 0)
        {
            Array.Copy(pixels, output, pixels.Length);
            return output;
        }

        var scratch = new byte[pixels.Length];
        var source = pixels;
        for (var pass = 0; pass < Passes; pass++)
        {
            BlurHorizontal(source, scratch, width, height, channelCount, radius);
            BlurVertical(scratch, output, width, height, channelCount, radius);
            source = output; // 下一 pass 以本轮输出为输入（scratch/output 交替复用）
        }

        return output;
    }

    private static void BlurHorizontal(byte[] source, byte[] target, int width, int height, int channels, int radius)
    {
        var window = (2 * radius) + 1;
        var rowStride = width * channels;
        for (var y = 0; y < height; y++)
        {
            var rowBase = y * rowStride;
            for (var c = 0; c < channels; c++)
            {
                var sum = 0L;
                // 初始窗口 [x−radius, x+radius]，列夹取
                for (var d = -radius; d <= radius; d++)
                {
                    var x = Math.Clamp(d, 0, width - 1);
                    sum += source[rowBase + (x * channels) + c];
                }

                for (var x = 0; x < width; x++)
                {
                    target[rowBase + (x * channels) + c] = (byte)(sum / window);
                    var add = Math.Clamp(x + radius + 1, 0, width - 1);
                    var remove = Math.Clamp(x - radius, 0, width - 1);
                    sum += source[rowBase + (add * channels) + c] - source[rowBase + (remove * channels) + c];
                }
            }
        }
    }

    private static void BlurVertical(byte[] source, byte[] target, int width, int height, int channels, int radius)
    {
        var window = (2 * radius) + 1;
        var rowStride = width * channels;
        for (var x = 0; x < width; x++)
        {
            var columnBase = x * channels;
            for (var c = 0; c < channels; c++)
            {
                var sum = 0L;
                for (var d = -radius; d <= radius; d++)
                {
                    var y = Math.Clamp(d, 0, height - 1);
                    sum += source[(y * rowStride) + columnBase + c];
                }

                for (var y = 0; y < height; y++)
                {
                    target[(y * rowStride) + columnBase + c] = (byte)(sum / window);
                    var add = Math.Clamp(y + radius + 1, 0, height - 1);
                    var remove = Math.Clamp(y - radius, 0, height - 1);
                    sum += source[(add * rowStride) + columnBase + c] - source[(remove * rowStride) + columnBase + c];
                }
            }
        }
    }
}
