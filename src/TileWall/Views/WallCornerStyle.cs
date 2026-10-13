using Microsoft.UI.Xaml;
using TileWall.Core.Wall;

namespace TileWall.Views;

/// <summary>Wall-only geometry; dialog and editor control themes are independent.</summary>
internal static class WallCornerStyle
{
    public static double Radius(WallVisualStyle style) => style == WallVisualStyle.Win11 ? 4 : 0;

    public static CornerRadius Corners(WallVisualStyle style) => new(Radius(style));
}
