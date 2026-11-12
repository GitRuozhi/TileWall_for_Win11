using TileWall.Core.Wall;

namespace TileWall.ApplicationModel;

/// <summary>Physical presentation bounds, independent of the document's logical capacity.</summary>
internal readonly record struct WallPresentationBounds(int X, int Y, int Width, int Height);

internal static class WallPresentationGeometry
{
    internal static WallPresentationBounds ResolveArea(WallPresentationBounds screen,
        WallPresentationBounds? available, bool activateTaskbar)
    {
        if (!activateTaskbar || available is not { Width: > 0, Height: > 0 } area ||
            area.X < screen.X || area.Y < screen.Y ||
            (long)area.X + area.Width > (long)screen.X + screen.Width ||
            (long)area.Y + area.Height > (long)screen.Y + screen.Height)
            return screen;
        return area;
    }

    internal static WallPresentationBounds Anchor(WallPresentationBounds area, int width, int height,
        AnchorCorner anchor, bool fullScreen = false)
    {
        if (fullScreen) return area;
        width = Math.Clamp(width, 1, area.Width);
        height = Math.Clamp(height, 1, area.Height);
        var right = anchor is AnchorCorner.TopRight or AnchorCorner.BottomRight;
        var bottom = anchor is AnchorCorner.BottomLeft or AnchorCorner.BottomRight;
        return new(right ? area.X + area.Width - width : area.X,
            bottom ? area.Y + area.Height - height : area.Y, width, height);
    }

    internal static WallPresentationBounds Constrain(WallPresentationBounds rectangle, WallPresentationBounds area)
    {
        var width = Math.Clamp(rectangle.Width, 1, area.Width);
        var height = Math.Clamp(rectangle.Height, 1, area.Height);
        return new(Math.Clamp(rectangle.X, area.X, area.X + area.Width - width),
            Math.Clamp(rectangle.Y, area.Y, area.Y + area.Height - height), width, height);
    }
}
