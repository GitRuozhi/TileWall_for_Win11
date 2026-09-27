namespace TileWall.Core.Components;

/// <summary>
/// The finite set of clock formats exposed by the basic clock editor.
/// Values are .NET custom date and time format strings, not arbitrary user input.
/// </summary>
public static class ClockFormatOptions
{
    public const string DefaultTimeFormat = "HH:mm";
    public const string DefaultDateFormat = "yyyy/M/d dddd";

    private static readonly IReadOnlyList<string> TimeFormats = Array.AsReadOnly(new[]
    {
        DefaultTimeFormat,
        "HH:mm:ss",
        "hh:mm tt",
        "hh:mm:ss tt",
    });

    private static readonly IReadOnlyList<string> DateFormats = Array.AsReadOnly(new[]
    {
        DefaultDateFormat,
        "yyyy/M/d",
        "M/d dddd",
        "yyyy年M月d日 dddd",
    });

    public static IReadOnlyList<string> SupportedTimeFormats => TimeFormats;
    public static IReadOnlyList<string> SupportedDateFormats => DateFormats;

    public static bool IsSupportedTimeFormat(string? format) =>
        format is not null && TimeFormats.Contains(format, StringComparer.Ordinal);

    public static bool IsSupportedDateFormat(string? format) =>
        format is not null && DateFormats.Contains(format, StringComparer.Ordinal);

    public static bool IsValid(string? timeFormat, string? dateFormat) =>
        IsSupportedTimeFormat(timeFormat) && IsSupportedDateFormat(dateFormat);

    internal static string GetTimeFormatOrDefault(string? format) =>
        IsSupportedTimeFormat(format) ? format! : DefaultTimeFormat;

    internal static string GetDateFormatOrDefault(string? format) =>
        IsSupportedDateFormat(format) ? format! : DefaultDateFormat;
}
