using Windows.UI;

namespace TileWall.Graphics;

/// <summary>The resolved native material values used by one static desktop frame.</summary>
internal readonly record struct AcrylicSnapshotRecipe(Color Tint, float TintOpacity, float LuminosityOpacity)
{
    // WinUI's Acrylic GaussianBlurEffect uses a standard deviation of 30 DIPs.
    // Keep this physical scale independent of the bounded capture's pixel size.
    internal const double BlurSigmaDip = 30;

    internal static double BackgroundOpacity(int transparency) =>
        (100 - Math.Clamp(transparency - 6, 0, 94)) / 100d;
}
