using TileWall.Core.Wall;

namespace TileWall.ApplicationModel;

internal enum ResizeZone { Top, Bottom, Left, Right, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>Fullscreen exit hit regions and physical-point direction rules; no wall state changes.</summary>
internal static class FullscreenResizeGesture
{
    internal const double EdgeThicknessDip = 16;
    internal const double CornerSizeDip = 32;
    internal const double ExitThresholdDip = 16;

    internal static bool IsEnabled(ResizeZone zone, AnchorCorner anchor) => zone switch
    {
        ResizeZone.Top => anchor is AnchorCorner.BottomLeft or AnchorCorner.BottomRight,
        ResizeZone.Bottom => anchor is AnchorCorner.TopLeft or AnchorCorner.TopRight,
        ResizeZone.Left => anchor is AnchorCorner.TopRight or AnchorCorner.BottomRight,
        ResizeZone.Right => anchor is AnchorCorner.TopLeft or AnchorCorner.BottomLeft,
        ResizeZone.TopLeft => anchor != AnchorCorner.TopLeft,
        ResizeZone.TopRight => anchor != AnchorCorner.TopRight,
        ResizeZone.BottomLeft => anchor != AnchorCorner.BottomLeft,
        ResizeZone.BottomRight => anchor != AnchorCorner.BottomRight,
        _ => false,
    };

    internal static AnchorCorner PointerAnchor(ResizeZone zone) => zone switch
    {
        ResizeZone.Top or ResizeZone.Left or ResizeZone.TopLeft => AnchorCorner.BottomRight,
        ResizeZone.TopRight => AnchorCorner.BottomLeft,
        ResizeZone.BottomLeft => AnchorCorner.TopRight,
        ResizeZone.Bottom or ResizeZone.Right or ResizeZone.BottomRight => AnchorCorner.TopLeft,
        _ => throw new ArgumentOutOfRangeException(nameof(zone)),
    };

    internal static bool ShouldExit(ResizeZone zone, double physicalDeltaX, double physicalDeltaY, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(physicalDeltaX) || !double.IsFinite(physicalDeltaY)) return false;
        var x = physicalDeltaX / scale;
        var y = physicalDeltaY / scale;
        return zone switch
        {
            ResizeZone.Top => y > ExitThresholdDip,
            ResizeZone.Bottom => y < -ExitThresholdDip,
            ResizeZone.Left => x > ExitThresholdDip,
            ResizeZone.Right => x < -ExitThresholdDip,
            ResizeZone.TopLeft => x > ExitThresholdDip || y > ExitThresholdDip,
            ResizeZone.TopRight => x < -ExitThresholdDip || y > ExitThresholdDip,
            ResizeZone.BottomLeft => x > ExitThresholdDip || y < -ExitThresholdDip,
            ResizeZone.BottomRight => x < -ExitThresholdDip || y < -ExitThresholdDip,
            _ => false,
        };
    }
}
