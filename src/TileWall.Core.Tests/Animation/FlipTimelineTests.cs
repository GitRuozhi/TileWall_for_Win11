using TileWall.Core.Animation;
using Xunit;

namespace TileWall.Core.Tests.Animation;

/// <summary>
/// T-FLIP（M6 设计 §11.1；A1–A5；V-04「组内时间线抽象单测」）：
/// EasedProgress 端点/中点/单调性；AngleDegAt 0→180 与 clamp；BackFaceAt 恰在 0.5 翻面；
/// 组级共享：单实例驱动 N=16/200 个块引用 → 同 now 同进度（A5 的构造性断言）；中断点推进到 1 → 终态。
/// </summary>
public sealed class FlipTimelineTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly FlipTimeline _timeline = new(); // 组级一个实例

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-100, 0)]     // 时钟异常/早到：clamp 0
    [InlineData(300, 0.5)]    // 中点：4·0.5³ = 0.5
    [InlineData(600, 1)]
    [InlineData(60000, 1)]    // 越界 clamp 1
    public void EasedProgress_EndpointsAndMidpoint(double elapsedMs, double expected)
    {
        Assert.Equal(expected, FlipTimeline.EasedProgress(TimeSpan.FromMilliseconds(elapsedMs)), 9);
    }

    [Fact]
    public void EasedProgress_IsMonotonic()
    {
        double previous = -1;
        for (var ms = 0.0; ms <= 600; ms += 5)
        {
            var value = FlipTimeline.EasedProgress(TimeSpan.FromMilliseconds(ms));
            Assert.True(value >= previous, $"easing 在 {ms} ms 处回落");
            Assert.InRange(value, 0, 1);
            previous = value;
        }
    }

    [Fact]
    public void AngleDegAt_ZeroToMaxAngle_WithClamp()
    {
        Assert.Equal(0, _timeline.AngleDegAt(Start, Start), 9);
        Assert.Equal(90, _timeline.AngleDegAt(Start, Start.AddMilliseconds(300)), 9);
        Assert.Equal(FlipTimeline.MaxAngleDeg, _timeline.AngleDegAt(Start, Start.AddMilliseconds(600)), 9);
        Assert.Equal(FlipTimeline.MaxAngleDeg, _timeline.AngleDegAt(Start, Start.AddSeconds(30)), 9); // 打断后推进 = 终态
    }

    [Fact]
    public void BackFaceAt_SwapsExactlyAtHalfTimeline()
    {
        Assert.False(FlipTimeline.BackFaceAt(TimeSpan.FromMilliseconds(299.9))); // 旧图面
        Assert.True(FlipTimeline.BackFaceAt(TimeSpan.FromMilliseconds(300)));    // 新图面
        Assert.Equal(TimeSpan.FromMilliseconds(FlipTimeline.DurationMs / 2), FlipTimeline.FaceSwapElapsed);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(200)]
    public void SingleInstance_DrivesAllBlocks_WithIdenticalProgress(int blockCount)
    {
        // A5 构造性断言：同一实例 + 同一 startUtc 对一切块给出同一进度（不存在块级时间参数）
        var now = Start.AddMilliseconds(310); // 已过切换点：全部块同面（back）
        var expected = _timeline.AngleDegAt(Start, now);
        var faces = 0;
        for (var i = 0; i < blockCount; i++)
        {
            Assert.Equal(expected, _timeline.AngleDegAt(Start, now), 9);
            if (FlipTimeline.BackFaceAt(now - Start))
            {
                faces++;
            }
        }

        Assert.Equal(blockCount, faces); // 同一时刻所有块同面（切换点一致）
    }

    [Fact]
    public void Constants_FreezeDecisionQ4()
    {
        // 拍板 Q4 / P1 §1.3 A1–A4：常量集中管理，校准轮只改常量
        Assert.Equal(600, FlipTimeline.DurationMs);
        Assert.Equal(180, FlipTimeline.MaxAngleDeg);
        Assert.Equal(1000, FlipTimeline.PerspectiveDepthDip);
        Assert.Equal(TimeSpan.FromMilliseconds(600), new FlipTimeline().Duration);
    }
}
