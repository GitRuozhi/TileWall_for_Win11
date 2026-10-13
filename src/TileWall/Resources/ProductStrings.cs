using System.Globalization;
using Microsoft.UI.Xaml;
using TileWall.Core.Wall;

namespace TileWall.Resources;

/// <summary>Product text selected explicitly or from the current Windows UI language.</summary>
internal static class ProductStrings
{
    private static ResourceDictionary? _chinese, _english;
    public static WallLanguageMode Language { get; private set; }
    public static void ApplyLanguage(WallLanguageMode mode) => Language = mode;

    public static string Get(string key)
    {
        var chinese = Language == WallLanguageMode.Chinese || Language == WallLanguageMode.System
            && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        var dictionary = chinese
            ? (_chinese ??= new ResourceDictionary { Source = new Uri("ms-appx:///Resources/ProductStrings.xaml") })
            : (_english ??= new ResourceDictionary { Source = new Uri("ms-appx:///Resources/ProductStrings.en.xaml") });
        return dictionary.TryGetValue(key, out var value) && value is string text ? text : key;
    }

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
