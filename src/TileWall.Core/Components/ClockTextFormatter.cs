using System.Globalization;

namespace TileWall.Core.Components;

/// <summary>Formats the clock's time and date lines using a bounded set of formats.</summary>
public static class ClockTextFormatter
{
    /// <summary>The default 24-hour time format.</summary>
    public const string TimeFormat = ClockFormatOptions.DefaultTimeFormat;

    /// <summary>The default date format, including a culture-specific weekday name.</summary>
    public const string DateFormat = ClockFormatOptions.DefaultDateFormat;

    public static (string TimeLine, string DateLine) Format(DateTimeOffset utcNow, CultureInfo? culture = null)
        => Format(utcNow, TimeFormat, DateFormat, culture);

    /// <summary>
    /// Formats a clock member. Unsupported persisted values safely fall back to the product defaults;
    /// callers that edit values should use <see cref="ClockFormatOptions.IsValid"/> for validation.
    /// </summary>
    public static (string TimeLine, string DateLine) Format(
        DateTimeOffset utcNow,
        string? timeFormat,
        string? dateFormat,
        CultureInfo? culture = null)
    {
        var cultureToUse = culture ?? CultureInfo.CurrentCulture;
        var local = utcNow.ToLocalTime(); // 显示值取本机当前时间（设计 §15.1）
        var safeTimeFormat = ClockFormatOptions.GetTimeFormatOrDefault(timeFormat);
        var safeDateFormat = ClockFormatOptions.GetDateFormatOrDefault(dateFormat);
        return (local.ToString(safeTimeFormat, cultureToUse), local.ToString(safeDateFormat, cultureToUse));
    }
}
