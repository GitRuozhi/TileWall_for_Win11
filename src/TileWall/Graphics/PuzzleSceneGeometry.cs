using TileWall.Core.Grid;
using TileWall.Core.Imaging;

namespace TileWall.Graphics;

public sealed record PuzzlePartitionScene(DipRect Viewport, PartitionClip? SharpClip);

/// <summary>A puzzle owns one complete image plane; partitions are viewports in that plane.</summary>
public sealed record PuzzleSceneGeometry(
    DipSize Canvas,
    PixelSize SourcePixels,
    ImageTransform SharpTransform,
    ImageTransform BackgroundTransform,
    IReadOnlyList<PuzzlePartitionScene> Partitions)
{
    public static PuzzleSceneGeometry Create(DipSize canvas, PixelSize sourcePixels,
        IReadOnlyList<DipRect> partitionViewports, MemberImageDisplay display, double rasterScale)
    {
        var placed = MemberImageGeometry.Calculate(canvas.Width, canvas.Height,
            sourcePixels.Width, sourcePixels.Height, rasterScale, display);
        return Create(canvas, sourcePixels, partitionViewports,
            new ImageTransform(placed.Scale, placed.Bounds.X, placed.Bounds.Y));
    }

    public static PuzzleSceneGeometry Create(
        DipSize canvas,
        PixelSize sourcePixels,
        IReadOnlyList<DipRect> partitionViewports,
        ImageTransform sharpTransform)
    {
        if (!canvas.IsValid || !sourcePixels.IsValid)
            throw new ArgumentOutOfRangeException(nameof(canvas));
        ArgumentNullException.ThrowIfNull(partitionViewports);
        if (partitionViewports.Count == 0)
            throw new ArgumentException("A puzzle needs at least one partition.", nameof(partitionViewports));
        if (sharpTransform.Scale <= 0 || !double.IsFinite(sharpTransform.Scale) ||
            !double.IsFinite(sharpTransform.OffsetX) || !double.IsFinite(sharpTransform.OffsetY))
            throw new ArgumentOutOfRangeException(nameof(sharpTransform));

        var partitions = new List<PuzzlePartitionScene>(partitionViewports.Count);
        foreach (var rect in partitionViewports)
        {
            if (rect.Width <= 0 || rect.Height <= 0 || rect.X < 0 || rect.Y < 0 ||
                rect.X + rect.Width > canvas.Width || rect.Y + rect.Height > canvas.Height)
                throw new ArgumentOutOfRangeException(nameof(partitionViewports), "Partition lies outside its puzzle canvas.");
            partitions.Add(new PuzzlePartitionScene(
                rect, SharedCanvasTransform.ClipFor(sharpTransform, sourcePixels, rect)));
        }

        return new PuzzleSceneGeometry(canvas, sourcePixels, sharpTransform,
            SharedCanvasTransform.DefaultTransform(sourcePixels, canvas), partitions);
    }

    /// <summary>Round shared edges once, in canvas coordinates, so adjacent viewports agree at any DPI.</summary>
    public static (int X, int Y, int Width, int Height) ToPixelRect(DipRect rect, double rasterScale)
    {
        if (rasterScale <= 0 || !double.IsFinite(rasterScale))
            throw new ArgumentOutOfRangeException(nameof(rasterScale));
        var left = checked((int)Math.Round(rect.X * rasterScale, MidpointRounding.AwayFromZero));
        var top = checked((int)Math.Round(rect.Y * rasterScale, MidpointRounding.AwayFromZero));
        var right = checked((int)Math.Round((rect.X + rect.Width) * rasterScale, MidpointRounding.AwayFromZero));
        var bottom = checked((int)Math.Round((rect.Y + rect.Height) * rasterScale, MidpointRounding.AwayFromZero));
        return (left, top, right - left, bottom - top);
    }
}
