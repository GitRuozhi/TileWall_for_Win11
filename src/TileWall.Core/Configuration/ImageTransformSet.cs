using TileWall.Core.Imaging;

namespace TileWall.Core.Configuration;

/// <summary>
/// 逐图变换集合的纯操作（M6 设计 §9/§10；XF-7/XF-9）：
/// 查找按 OrdinalIgnoreCase（Windows 路径不区分大小写，存储保留原样）；
/// 落盘「仅偏离默认态才保存条目」——与 DefaultTransform 逐字段相等的条目被剔除（XF-9），
/// 「新图默认居中填满、不继承上一张」在磁盘上无条目可言（XF-7）。
/// </summary>
public static class ImageTransformSet
{
    /// <summary>
    /// 变换查找：有条目 → 条目值（Fit 一并返回）；无条目 → null（调用方用 DefaultTransform(CoverFill)，XF-7）。
    /// </summary>
    public static (ImageTransform Transform, FitMode Fit)? Find(
        IReadOnlyList<ImageTransformRecord> transforms, string imageId)
    {
        ArgumentNullException.ThrowIfNull(transforms);
        ArgumentException.ThrowIfNullOrEmpty(imageId);

        foreach (var record in transforms)
        {
            if (string.Equals(record.ImageId, imageId, StringComparison.OrdinalIgnoreCase))
            {
                return (new ImageTransform(record.Scale, record.OffsetX, record.OffsetY), record.Fit);
            }
        }

        return null;
    }

    /// <summary>
    /// 更新单图变换（组属性窗保存路径）：与该图默认态相等 → 剔除条目；否则按 ImageId 覆盖/追加。
    /// 其余图的条目原样保留（含悬置条目——§13 风险 8：不主动清理）。
    /// </summary>
    public static IReadOnlyList<ImageTransformRecord> Upsert(
        IReadOnlyList<ImageTransformRecord> transforms,
        string imageId,
        FitMode fit,
        ImageTransform value,
        PixelSize pixels,
        DipSize canvas)
    {
        ArgumentNullException.ThrowIfNull(transforms);
        ArgumentException.ThrowIfNullOrEmpty(imageId);

        var kept = transforms
            .Where(t => !string.Equals(t.ImageId, imageId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (SharedCanvasTransform.IsDefault(value, pixels, canvas, fit))
        {
            return kept; // 默认态不落条目（XF-9）：磁盘上没有条目可言
        }

        kept.Add(new ImageTransformRecord { ImageId = imageId, Fit = fit, Scale = value.Scale, OffsetX = value.OffsetX, OffsetY = value.OffsetY });
        return kept;
    }
}
