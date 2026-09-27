using TileWall.Core.Grid;

namespace TileWall.Core.Imaging;

public readonly record struct PixelSize(double Width, double Height)
{
    public bool IsValid => Width > 0 && Height > 0 && double.IsFinite(Width) && double.IsFinite(Height);
}

public readonly record struct DipSize(double Width, double Height)
{
    public bool IsValid => Width > 0 && Height > 0 && double.IsFinite(Width) && double.IsFinite(Height);
}

public sealed record PartitionClip(DipRect DestInPartition, DipRect DestInCanvas, DipRect SourceRect);
