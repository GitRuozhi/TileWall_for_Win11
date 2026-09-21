using System.Globalization;

namespace TileWall.Core.Components;

/// <summary>
/// 时间日期两行文本的单一默认样式（M8 设计 §4.2、拍板 Q9）：
/// 时间行恒 24 小时制 HH:mm（时间大字），日期行 yyyy/M/d dddd（日期小字），显示值取本机时区。
/// 纯函数：无任何「错过时长」状态——调用即求值当前值，结构上不可能补播（C16）。
/// 无格式选择器（Q9 作废 6 种格式清单）；文化由调用方传入（默认 CurrentCulture）。
/// </summary>
public static class ClockTextFormatter
{
    /// <summary>时间大字格式（恒 HH:mm，不受文化 12/24 小时制影响——格式串字面指定）。</summary>
    public const string TimeFormat = "HH:mm";

    /// <summary>日期小字格式（年/月/日 + 星期；月日不补零，星期名随文化）。</summary>
    public const string DateFormat = "yyyy/M/d dddd";

    public static (string TimeLine, string DateLine) Format(DateTimeOffset utcNow, CultureInfo? culture = null)
    {
        var cultureToUse = culture ?? CultureInfo.CurrentCulture;
        var local = utcNow.ToLocalTime(); // 显示值取本机当前时间（设计 §15.1）
        return (local.ToString(TimeFormat, cultureToUse), local.ToString(DateFormat, cultureToUse));
    }
}
