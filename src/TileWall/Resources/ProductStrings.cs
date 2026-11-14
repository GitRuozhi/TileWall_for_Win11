using TileWall.Core.Settings;
using Microsoft.UI.Xaml;
using TileWall.Core.Wall;

namespace TileWall.Resources;

/// <summary>Product text selected explicitly or from the current Windows UI language.</summary>
internal static class ProductStrings
{
    private static readonly Dictionary<string, ResourceDictionary> Dictionaries = new(StringComparer.Ordinal);
    private static string? _nativeLanguage;
    public static WallLanguageMode Language { get; private set; }
    public static void ApplyLanguage(WallLanguageMode mode)
    {
        var nativeLanguage = WallLanguages.ResolveTag(mode);
        if (_nativeLanguage != nativeLanguage)
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = nativeLanguage;
            _nativeLanguage = nativeLanguage;
        }
        Language = mode;
    }

    public static string Get(string key) => Get(key, Language);

    public static string Get(string key, WallLanguageMode mode)
    {
        var dictionary = DictionaryFor(WallLanguages.ResolveTag(mode));
        if (dictionary.TryGetValue(key, out var value) && value is string text) return text;
        return DictionaryFor("en-US").TryGetValue(key, out value) && value is string fallback ? fallback : key;
    }

    private static ResourceDictionary DictionaryFor(string tag)
    {
        if (Dictionaries.TryGetValue(tag, out var dictionary)) return dictionary;
        // These dictionaries are selected explicitly. Keep their file names neutral:
        // MRT interprets dot-separated file-name segments as resource qualifiers.
        var fileName = tag switch
        {
            "zh-CN" => "ProductStrings.xaml",
            "zh-TW" => "ProductStringsTraditionalChinese.xaml",
            "ja-JP" => "ProductStringsJapanese.xaml",
            "ko-KR" => "ProductStringsKorean.xaml",
            "de-DE" => "ProductStringsGerman.xaml",
            "fr-FR" => "ProductStringsFrench.xaml",
            "es-ES" => "ProductStringsSpanish.xaml",
            _ => "ProductStrings.en.xaml",
        };
        dictionary = new ResourceDictionary { Source = new Uri($"ms-appx:///Resources/{fileName}") };
        Dictionaries.Add(tag, dictionary);
        return dictionary;
    }

    public static string SettingUpdatedMessage(string key, WallSettings settings) => key switch
    {
        "Settings.FullScreen" => Get(settings.FullScreen ? "Settings.FullScreenEnabled" : "Settings.FullScreenDisabled"),
        "Settings.ExpandColumns" => Get(settings.ExpandColumns ? "Settings.ExpandColumnsEnabled" : "Settings.ExpandColumnsDisabled"),
        _ => string.Format(Get("Settings.Updated"), Get(key)),
    };

    public static string WallRejectionReason(WallRejection rejection) => Get(rejection switch
    {
        WallRejection.None => "Wall.Rejection.None",
        WallRejection.StaleGeneration => "Wall.Rejection.StaleGeneration",
        WallRejection.InvalidDocument => "Wall.Rejection.InvalidDocument",
        WallRejection.InvalidAction => "Wall.Rejection.InvalidAction",
        WallRejection.ObjectNotFound => "Wall.Rejection.ObjectNotFound",
        WallRejection.DuplicateId => "Wall.Rejection.DuplicateId",
        WallRejection.NoSpace => "Wall.Rejection.NoSpace",
        WallRejection.WorkAreaTooSmall => "Wall.Rejection.WorkAreaTooSmall",
        _ => "Wall.Rejection.Unknown"
    });
}
