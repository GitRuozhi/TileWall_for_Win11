using System.Globalization;
using TileWall.Core.Components;
using Xunit;

namespace TileWall.Core.Tests.Components;

/// <summary>
/// T-CLOCK-FMT（M8 设计 §4.2 / 拍板 Q9）：FixedClock 矩阵——午夜 00:00、23:59、跨日、跨年、
/// 12/24 小时文化、dddd 随文化；输出恒两行、时间恒 HH:mm。
/// 时区确定性：用「本机时区墙钟时间 + 该时区偏移」构造 DateTimeOffset，ToLocalTime() 还原同一墙钟值。
/// </summary>
public sealed class ClockTextFormatterTests
{
    /// <summary>以本机时区的墙钟时间构造等价 DateTimeOffset（Format 的 ToLocalTime 为恒等变换）。</summary>
    private static DateTimeOffset LocalAt(int year, int month, int day, int hour, int minute)
    {
        var unspecified = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, TimeZoneInfo.Local.GetUtcOffset(unspecified));
    }

    [Theory]
    [InlineData(2026, 1, 1, 0, 0)]   // 午夜 00:00（跨日边界起点）
    [InlineData(2025, 12, 31, 23, 59)] // 23:59（跨日/跨年前最后一分钟）
    [InlineData(2026, 6, 15, 13, 5)] // 下午 13:05（24 小时制过午）
    public void Format_TimeLine_IsAlwaysHHmm(int year, int month, int day, int hour, int minute)
    {
        var (timeLine, _) = ClockTextFormatter.Format(LocalAt(year, month, day, hour, minute), CultureInfo.InvariantCulture);
        Assert.Equal($"{hour:D2}:{minute:D2}", timeLine); // 恒 HH:mm，5 字符、冒号分隔
        Assert.Equal(5, timeLine.Length);
        Assert.Equal(':', timeLine[2]);
    }

    [Fact]
    public void Format_DateLine_UsesYearSlashMonthSlashDayWithNoLeadingZeros()
    {
        var (_, dateLine) = ClockTextFormatter.Format(LocalAt(2026, 1, 1, 0, 0), CultureInfo.InvariantCulture);
        Assert.Equal("2026/1/1 Thursday", dateLine); // yyyy/M/d（月日不补零）+ dddd（不随区域补零）
    }

    [Fact]
    public void Format_MidnightAnd2359_CrossDayBoundary()
    {
        var before = ClockTextFormatter.Format(LocalAt(2025, 12, 31, 23, 59), CultureInfo.InvariantCulture);
        var after = ClockTextFormatter.Format(LocalAt(2026, 1, 1, 0, 0), CultureInfo.InvariantCulture);
        Assert.Equal("23:59", before.TimeLine);
        Assert.Equal("00:00", after.TimeLine);
        Assert.Equal("2025/12/31 Wednesday", before.DateLine); // 跨日：日期行落到前一天
        Assert.Equal("2026/1/1 Thursday", after.DateLine);
    }

    [Fact]
    public void Format_CrossYear_RollsYearInDateLine()
    {
        var (_, lastOfYear) = ClockTextFormatter.Format(LocalAt(2025, 12, 31, 23, 59), CultureInfo.InvariantCulture);
        var (_, firstOfNext) = ClockTextFormatter.Format(LocalAt(2026, 1, 1, 0, 0), CultureInfo.InvariantCulture);
        Assert.StartsWith("2025/", lastOfYear);
        Assert.StartsWith("2026/", firstOfNext); // 跨年：年份行随之翻转
    }

    [Fact]
    public void Format_WeekdayName_FollowsCulture()
    {
        var zhCn = new CultureInfo("zh-CN");
        var deDe = new CultureInfo("de-DE");
        var invariant = CultureInfo.InvariantCulture;
        // 2026-01-01 是星期四（Thursday / Donnerstag / 星期四）
        var (_, invariantDate) = ClockTextFormatter.Format(LocalAt(2026, 1, 1, 9, 30), invariant);
        var (_, zhDate) = ClockTextFormatter.Format(LocalAt(2026, 1, 1, 9, 30), zhCn);
        var (_, deDate) = ClockTextFormatter.Format(LocalAt(2026, 1, 1, 9, 30), deDe);
        Assert.Contains("Thursday", invariantDate);
        Assert.Contains("星期四", zhDate);
        Assert.Contains("Donnerstag", deDate);
    }

    [Fact]
    public void Format_TwelveHourCulture_StillEmits24HourTimeLine()
    {
        var enUs = new CultureInfo("en-US"); // 偏好 12 小时制的文化
        var (timeLine, _) = ClockTextFormatter.Format(LocalAt(2026, 3, 8, 13, 5), enUs);
        Assert.Equal("13:05", timeLine); // HH:mm 为字面格式串：不受文化 12/24 小时制影响（拍板 Q9 单一样式）
        Assert.DoesNotContain("PM", timeLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Format_DefaultCultureOverload_DoesNotThrow()
    {
        var (timeLine, dateLine) = ClockTextFormatter.Format(LocalAt(2026, 1, 1, 0, 0));
        Assert.False(string.IsNullOrWhiteSpace(timeLine)); // 输出恒两行（重载路径同构）
        Assert.False(string.IsNullOrWhiteSpace(dateLine));
    }
}
