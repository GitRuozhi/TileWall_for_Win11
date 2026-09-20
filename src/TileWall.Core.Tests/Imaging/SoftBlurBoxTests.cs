using TileWall.Core.Imaging;
using Xunit;

namespace TileWall.Core.Tests.Imaging;

/// <summary>
/// T-BLURCORE（M6 设计 §11.1 T-BLURCORE；§5.4 备选方案的 Core 纯函数核）：
/// 常数图不变、单脉冲扩散半径、三 pass 幂等上限、输出尺寸不变、输入不改。
/// </summary>
public sealed class SoftBlurBoxTests
{
    private static byte[] Constant(int width, int height, byte value) =>
        Enumerable.Repeat(value, width * height * 4).ToArray();

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void ConstantImage_IsUnchanged(int radius)
    {
        var pixels = Constant(16, 12, 200);
        var blurred = SoftBlurBox.Apply(pixels, 16, 12, 4, radius);
        Assert.Equal(pixels, blurred); // 常数图在任何半径下不变
    }

    [Fact]
    public void OutputShape_MatchesInput_And_InputUntouched()
    {
        var pixels = new byte[10 * 8 * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 7);
        }

        var snapshot = (byte[])pixels.Clone();
        var blurred = SoftBlurBox.Apply(pixels, 10, 8, 4, 2);
        Assert.Equal(10 * 8 * 4, blurred.Length);
        Assert.Equal(snapshot, pixels); // 输入不改（纯函数）
    }

    [Fact]
    public void SinglePulse_Spreads_StaysWithinRadiusBound_And_LowersPeak()
    {
        const int width = 21;
        const int height = 1;
        var pixels = new byte[width * height * 4];
        pixels[(10 * 4) + 1] = 240; // 中心脉冲（G 通道）
        var blurred = SoftBlurBox.Apply(pixels, width, height, 4, 2);

        const int reach = 2 * SoftBlurBox.Passes; // 三 pass box、半径 2：能量至多扩散 2·3 像素
        for (var x = 0; x < width; x++)
        {
            var g = blurred[(x * 4) + 1];
            if (Math.Abs(x - 10) <= reach)
            {
                continue;
            }

            Assert.Equal(0, g); // 扩散半径有限
        }

        Assert.True(blurred[(10 * 4) + 1] < 240, "峰值必须被模糊摊薄");
        Assert.True(blurred[(10 * 4) + 1] > 0, "脉冲中心保留能量");
    }

    [Fact]
    public void RepeatedApply_Converges_FurtherFlatteningOnly()
    {
        var pixels = new byte[9 * 9 * 4];
        pixels[((4 * 9) + 4) * 4] = 255;
        var once = SoftBlurBox.Apply(pixels, 9, 9, 4, 1);
        var twice = SoftBlurBox.Apply(once, 9, 9, 4, 1);

        // 三 pass 幂等上限：越模糊越平，但不会放大极差
        var rangeOnce = once.Max() - (double)once.Min();
        var rangeTwice = twice.Max() - (double)twice.Min();
        Assert.True(rangeTwice <= rangeOnce);
    }

    [Fact]
    public void ZeroRadius_IsIdentityCopy_And_MismatchedLength_Throws()
    {
        var pixels = new byte[4 * 4 * 4];
        pixels[7] = 99;
        var result = SoftBlurBox.Apply(pixels, 4, 4, 4, 0);
        Assert.Equal(pixels, result);
        Assert.Throws<ArgumentException>(() => SoftBlurBox.Apply(new byte[10], 4, 4, 4, 1));
    }
}
