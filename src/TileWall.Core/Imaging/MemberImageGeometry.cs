using System.Text.Json;
using System.Text.Json.Serialization;
using TileWall.Core.Grid;

namespace TileWall.Core.Imaging;

public enum ImageDisplayMode { Fit, Fill, Original, Custom }

/// <summary>CustomScale is a multiplier of each image's fill scale. There is no translation.</summary>
public sealed record MemberImageDisplay
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }

    public ImageDisplayMode Mode { get; init; } = ImageDisplayMode.Fill;
    public double CustomScale { get; init; } = 1;
}

public readonly record struct MemberImagePlacement(double Scale, DipRect Bounds);

/// <summary>Shared by wall rendering and editor previews; source dimensions are EXIF-corrected pixels.</summary>
public static class MemberImageGeometry
{
    public const double MinimumCustomScale = 0.01;
    public const double MaximumCustomScale = 100;
    public static MemberImagePlacement Calculate(double canvasWidthDip, double canvasHeightDip,
        double imageWidthPixels, double imageHeightPixels, double rasterizationScale,
        MemberImageDisplay display)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (!Positive(canvasWidthDip) || !Positive(canvasHeightDip) || !Positive(imageWidthPixels) ||
            !Positive(imageHeightPixels) || !Positive(rasterizationScale) || !Positive(display.CustomScale) ||
            !Enum.IsDefined(display.Mode))
            throw new ArgumentOutOfRangeException(nameof(display), "Image geometry must be finite and positive.");

        var contain = Math.Min(canvasWidthDip / imageWidthPixels, canvasHeightDip / imageHeightPixels);
        var scale = display.Mode switch
        {
            ImageDisplayMode.Fit => contain,
            ImageDisplayMode.Fill => Math.Max(canvasWidthDip / imageWidthPixels, canvasHeightDip / imageHeightPixels),
            ImageDisplayMode.Original => 1 / rasterizationScale,
            ImageDisplayMode.Custom => display.CustomScale * Math.Max(canvasWidthDip / imageWidthPixels, canvasHeightDip / imageHeightPixels),
            _ => throw new ArgumentOutOfRangeException(nameof(display)),
        };
        var width = scale * imageWidthPixels;
        var height = scale * imageHeightPixels;
        if (!Positive(scale) || !Positive(width) || !Positive(height))
            throw new ArgumentOutOfRangeException(nameof(display), "Image placement overflow.");
        return new(scale, new DipRect((canvasWidthDip - width) / 2,
            (canvasHeightDip - height) / 2, width, height));
    }

    public static double CustomScaleFromImageScale(double imageScale, double canvasWidthDip,
        double canvasHeightDip, double imageWidthPixels, double imageHeightPixels)
    {
        var contain = Calculate(canvasWidthDip, canvasHeightDip, imageWidthPixels, imageHeightPixels,
            1, new MemberImageDisplay { Mode = ImageDisplayMode.Fill }).Scale;
        var value = imageScale / contain;
        if (!Positive(value)) throw new ArgumentOutOfRangeException(nameof(imageScale));
        return value;
    }

    /// <summary>Resolve one wheel/edge sample. Presets retain exact geometry; custom values use whole percentages of fill.</summary>
    public static MemberImageDisplay InteractiveDisplay(double imageScale, double canvasWidthDip,
        double canvasHeightDip, double imageWidthPixels, double imageHeightPixels, double rasterizationScale,
        bool wheel)
    {
        if (!Positive(imageScale)) throw new ArgumentOutOfRangeException(nameof(imageScale));
        var fill = Calculate(canvasWidthDip, canvasHeightDip, imageWidthPixels, imageHeightPixels,
            rasterizationScale, new MemberImageDisplay { Mode = ImageDisplayMode.Fill }).Scale;
        foreach (var mode in new[] { ImageDisplayMode.Fit, ImageDisplayMode.Fill, ImageDisplayMode.Original })
        {
            var preset = Calculate(canvasWidthDip, canvasHeightDip, imageWidthPixels, imageHeightPixels,
                rasterizationScale, new MemberImageDisplay { Mode = mode }).Scale;
            var near = wheel ? Math.Abs(imageScale / preset - 1) <= 0.025
                : Math.Abs(imageScale - preset) * Math.Max(imageWidthPixels, imageHeightPixels) * rasterizationScale / 2 <= 7;
            if (near) return new MemberImageDisplay { Mode = mode, CustomScale = preset / fill };
        }

        var percentage = Math.Clamp(Math.Round(imageScale / fill * 100, MidpointRounding.AwayFromZero),
            MinimumCustomScale * 100, MaximumCustomScale * 100);
        return new MemberImageDisplay { Mode = ImageDisplayMode.Custom, CustomScale = percentage / 100 };
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
}
