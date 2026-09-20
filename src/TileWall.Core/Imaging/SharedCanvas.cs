using TileWall.Core.Grid;

namespace TileWall.Core.Imaging;

/// <summary>源图像素尺寸（解码产物；M6 设计 §4.1）。</summary>
public readonly record struct PixelSize(double Width, double Height)
{
    public bool IsValid => Width > 0 && Height > 0 && double.IsFinite(Width) && double.IsFinite(Height);
}

/// <summary>画布 DIP 尺寸（组画布 = 组 Bounds 对应的整块 DIP 区域）。</summary>
public readonly record struct DipSize(double Width, double Height)
{
    public bool IsValid => Width > 0 && Height > 0 && double.IsFinite(Width) && double.IsFinite(Height);
}

/// <summary>
/// 分块裁剪结果（M6 设计 §4.2）：该分块可见的目标子矩形（含相对分块左上角与相对画布两个视图）
/// 与对应的源像素子矩形。null（不可空引用语义由调用方判别）= 分块与图像完全不相交 → 整块露后景。
/// </summary>
public sealed record PartitionClip(DipRect DestInPartition, DipRect DestInCanvas, DipRect SourceRect);

/// <summary>
/// 组画布几何（M6 设计 §3）：唯一画布几何来源，全部常量派生自 <see cref="GridMetrics"/>，
/// 与 WallPresenter 分组内定位数学（p.Column·Pitch）逐字一致（§3.1）。
/// 缝隙是画布真实条带、不属于任何分块矩形——「有墙遮住/拆墙重现」的构造性基础。
/// </summary>
public static class SharedCanvas
{
    /// <summary>
    /// 组画布尺寸：CW = W·s + (W−1)·g、CH = H·s + (H−1)·g
    /// （= GridMetrics.WidthOf/HeightOf；4×4 组 = 408×408 DIP，P1 §1.1）。
    /// </summary>
    public static DipSize CanvasSize(GridSize groupSize, GridMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        return new DipSize(metrics.WidthOf(groupSize.Columns), metrics.HeightOf(groupSize.Rows));
    }

    /// <summary>
    /// 分块画布矩形 R_p = (p.Column·Pitch, p.Row·Pitch, WidthOf(p.Width), HeightOf(p.Height))。
    /// 解析形式与 WallPresenter 分组内定位（Canvas.SetLeft(partition, p.Column·Pitch)）逐字一致（§3.1）。
    /// </summary>
    public static DipRect PartitionRect(GridRect partition, GridMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        return new DipRect(
            partition.Column * metrics.Pitch,
            partition.Row * metrics.Pitch,
            metrics.WidthOf(partition.Width),
            metrics.HeightOf(partition.Height));
    }
}
