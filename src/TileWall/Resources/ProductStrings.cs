using Microsoft.UI.Xaml;
using TileWall.Core.Wall;

namespace TileWall.Resources;

/// <summary>Product text only. Both current language choices fall back to the shipped Chinese dictionary.</summary>
internal static class ProductStrings
{
    private static ResourceDictionary? _chinese;
    public static WallLanguageMode Language { get; private set; }

    public static void ApplyLanguage(WallLanguageMode mode) => Language = mode;

    public static string Get(string key)
    {
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var value) && value is string text)
            return text;
        _chinese ??= new ResourceDictionary { Source = new Uri("ms-appx:///Resources/ProductStrings.xaml") };
        return _chinese.TryGetValue(key, out value) && value is string fallback ? fallback : key;
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
