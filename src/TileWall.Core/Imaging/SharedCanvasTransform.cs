using TileWall.Core.Grid;

namespace TileWall.Core.Imaging;

/// <summary>One centered image placement in canvas DIPs.</summary>
public readonly record struct ImageTransform(double Scale, double OffsetX, double OffsetY);

/// <summary>
/// Shared canvas geometry for a puzzle image and its partition viewports. Every partition is
/// clipped from the same centered image placement, preserving source continuity across gaps.
/// </summary>
public static class SharedCanvasTransform
{
    /// <summary>Creates the centered cover-fill placement used for the puzzle background plane.</summary>
    public static ImageTransform DefaultTransform(PixelSize image, DipSize canvas)
    {
        Validate(image, canvas);

        var scale = Math.Max(canvas.Width / image.Width, canvas.Height / image.Height);
        return new ImageTransform(scale,
            (canvas.Width - image.Width * scale) / 2,
            (canvas.Height - image.Height * scale) / 2);
    }

    /// <summary>
    /// Intersects a partition viewport with the shared image bounds and maps the visible portion
    /// back to source pixels. Rectangles are half-open; no intersection returns null.
    /// </summary>
    public static PartitionClip? ClipFor(ImageTransform transform, PixelSize image, DipRect partitionRect)
    {
        if (!image.IsValid)
            throw new ArgumentOutOfRangeException(nameof(image), image, "Source pixel dimensions must be positive and finite.");
        if (transform.Scale <= 0 || !double.IsFinite(transform.Scale) ||
            !double.IsFinite(transform.OffsetX) || !double.IsFinite(transform.OffsetY))
            throw new ArgumentOutOfRangeException(nameof(transform), transform, "Image placement must be finite and positive.");
        if (!double.IsFinite(partitionRect.X) || !double.IsFinite(partitionRect.Y) ||
            !double.IsFinite(partitionRect.Width) || !double.IsFinite(partitionRect.Height) ||
            partitionRect.Width < 0 || partitionRect.Height < 0)
            throw new ArgumentOutOfRangeException(nameof(partitionRect), partitionRect, "Partition dimensions must be finite and nonnegative.");

        var imageLeft = transform.OffsetX;
        var imageTop = transform.OffsetY;
        var imageRight = transform.OffsetX + image.Width * transform.Scale;
        var imageBottom = transform.OffsetY + image.Height * transform.Scale;

        var x0 = Math.Max(partitionRect.X, imageLeft);
        var y0 = Math.Max(partitionRect.Y, imageTop);
        var x1 = Math.Min(partitionRect.X + partitionRect.Width, imageRight);
        var y1 = Math.Min(partitionRect.Y + partitionRect.Height, imageBottom);
        if (x1 <= x0 || y1 <= y0)
            return null;

        var width = x1 - x0;
        var height = y1 - y0;
        return new PartitionClip(
            new DipRect(x0 - partitionRect.X, y0 - partitionRect.Y, width, height),
            new DipRect(x0, y0, width, height),
            new DipRect((x0 - transform.OffsetX) / transform.Scale,
                (y0 - transform.OffsetY) / transform.Scale,
                width / transform.Scale, height / transform.Scale));
    }

    private static void Validate(PixelSize image, DipSize canvas)
    {
        if (!image.IsValid)
            throw new ArgumentOutOfRangeException(nameof(image), image, "Source pixel dimensions must be positive and finite.");
        if (!canvas.IsValid)
            throw new ArgumentOutOfRangeException(nameof(canvas), canvas, "Canvas DIP dimensions must be positive and finite.");
    }
}
