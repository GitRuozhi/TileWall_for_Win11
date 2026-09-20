namespace TileWall.Shell.Imaging;

/// <summary>
/// 图像解码与模糊补底的占位常量（M6 设计 §5.4/§13 风险 5）：全部为拍板 Q2 同批真机校准的
/// 观感/内存占位值，校准轮只改常量。
/// </summary>
public static class ImageDecodePolicy
{
    /// <summary>清晰前景解码上限（源最长边像素；防 10000 px 原图整幅进内存，§13 风险 5 建议 ≤4096）。</summary>
    public const int SharpDecodeMaxEdgePx = 4096;

    /// <summary>模糊后景解码目标 = 画布物理像素 / 该除数（§5.4 备选「画布像素 / 8」）。</summary>
    public const double BlurTargetScaleDivisor = 8;

    /// <summary>模糊层像素上限（最长边；§13 风险 5 建议 ≤512）。</summary>
    public const int BlurMaxEdgePx = 512;

    /// <summary>模糊层像素下限（避免极小画布退化到 0）。</summary>
    public const int BlurMinEdgePx = 8;

    /// <summary>box blur 半径（像素，占位；真机观感校准项 T-MANUAL①）。</summary>
    public const int BlurRadiusPx = 12;
}
