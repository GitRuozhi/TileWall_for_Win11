using System.Globalization;

namespace TileWall.Core.Wall;

public readonly record struct MemberBackgroundAppearance(byte A, byte R, byte G, byte B, double Opacity);

/// <summary>Neutral Win10-style colors chosen for this product; opacity affects only the background layer.</summary>
public static class MemberBackgroundRules
{
    public static MemberBackgroundAppearance GetColor(MemberVisual visual, bool dark)
    {
        ArgumentNullException.ThrowIfNull(visual);
        var opacity = 1 - Math.Clamp(visual.BackgroundTransparency, 0, 100) / 100d;
        if (visual.BackgroundMode == MemberBackgroundMode.None) return new(0, 0, 0, 0, 0);
        // Product calibration, rather than a claim of exact RGB measured from a Microsoft reference.
        uint argb = dark ? 0xFF333333 : 0xFFD9D9D9;
        if (visual.BackgroundMode == MemberBackgroundMode.Color && visual.BackgroundColor is { } color &&
            color.Length is 7 or 9 && color[0] == '#' &&
            uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
            argb = color.Length == 7 ? parsed | 0xFF000000 : parsed;
        return new((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, opacity);
    }
}
