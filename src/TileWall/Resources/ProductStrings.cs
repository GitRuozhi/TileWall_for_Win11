using System.Globalization;
using Microsoft.UI.Xaml;
using TileWall.Core.Wall;

namespace TileWall.Resources;

/// <summary>Product text selected explicitly or from the current Windows UI language.</summary>
internal static class ProductStrings
{
    private static ResourceDictionary? _chinese, _english;
    private static string? _nativeLanguage;
    public static WallLanguageMode Language { get; private set; }
    public static void ApplyLanguage(WallLanguageMode mode)
    {
        var nativeLanguage = UsesChinese(mode) ? "zh-CN" : "en-US";
        if (_nativeLanguage != nativeLanguage)
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = nativeLanguage;
            _nativeLanguage = nativeLanguage;
        }
        Language = mode;
    }

    public static string Get(string key)
    {
        var dictionary = UsesChinese(Language)
            ? (_chinese ??= new ResourceDictionary { Source = new Uri("ms-appx:///Resources/ProductStrings.xaml") })
            : (_english ??= new ResourceDictionary { Source = new Uri("ms-appx:///Resources/ProductStrings.en.xaml") });
        return dictionary.TryGetValue(key, out var value) && value is string text ? text : key;
    }

    private static bool UsesChinese(WallLanguageMode mode) => mode == WallLanguageMode.Chinese
        || mode == WallLanguageMode.System
        && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

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
