using System.Globalization;
using TileWall.Core.Wall;

namespace TileWall.Core.Settings;

/// <summary>The supported product language identities; independent of XAML and persisted storage.</summary>
public static class WallLanguages
{
    public static IReadOnlyList<WallLanguageMode> Supported { get; } = Array.AsReadOnly(new[]
    {
        WallLanguageMode.English, WallLanguageMode.Chinese, WallLanguageMode.TraditionalChinese,
        WallLanguageMode.Japanese, WallLanguageMode.Korean, WallLanguageMode.German,
        WallLanguageMode.French, WallLanguageMode.Spanish,
    });

    public static string ResolveTag(WallLanguageMode mode, string? systemLanguage = null) => mode switch
    {
        WallLanguageMode.System => ResolveSystemTag(systemLanguage ?? CultureInfo.CurrentUICulture.Name),
        WallLanguageMode.Chinese => "zh-CN", WallLanguageMode.TraditionalChinese => "zh-TW",
        WallLanguageMode.Japanese => "ja-JP", WallLanguageMode.Korean => "ko-KR",
        WallLanguageMode.German => "de-DE", WallLanguageMode.French => "fr-FR",
        WallLanguageMode.Spanish => "es-ES", _ => "en-US",
    };

    public static string ResolveSystemTag(string? language)
    {
        var parts = (language ?? string.Empty).Split('-');
        var family = parts[0].ToLowerInvariant();
        if (family == "zh")
            return parts.Skip(1).Any(part => part.Equals("Hant", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("TW", StringComparison.OrdinalIgnoreCase) || part.Equals("HK", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("MO", StringComparison.OrdinalIgnoreCase)) ? "zh-TW" : "zh-CN";
        return family switch
        {
            "ja" => "ja-JP", "ko" => "ko-KR", "de" => "de-DE", "fr" => "fr-FR", "es" => "es-ES", _ => "en-US",
        };
    }

    // Command-line tags are a whitelist, not a culture name parser. Keep the two historical aliases.
    public static bool TryParseTag(string? tag, out WallLanguageMode mode)
    {
        mode = tag?.ToLowerInvariant() switch
        {
            "zh" or "zh-cn" => WallLanguageMode.Chinese, "en" or "en-us" => WallLanguageMode.English,
            "zh-tw" => WallLanguageMode.TraditionalChinese, "ja-jp" => WallLanguageMode.Japanese,
            "ko-kr" => WallLanguageMode.Korean, "de-de" => WallLanguageMode.German,
            "fr-fr" => WallLanguageMode.French, "es-es" => WallLanguageMode.Spanish, _ => WallLanguageMode.System,
        };
        return mode != WallLanguageMode.System;
    }

    public static string NativeName(WallLanguageMode mode) => mode switch
    {
        WallLanguageMode.Chinese => "简体中文", WallLanguageMode.TraditionalChinese => "繁體中文",
        WallLanguageMode.Japanese => "日本語", WallLanguageMode.Korean => "한국어",
        WallLanguageMode.German => "Deutsch", WallLanguageMode.French => "Français",
        WallLanguageMode.Spanish => "Español", _ => "English",
    };
}
