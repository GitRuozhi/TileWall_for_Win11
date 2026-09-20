namespace TileWall.Core.Animation;

/// <summary>
/// 组级翻转时间线（M6 设计 §7.1；P1 §1.3 A1–A5 定值冻结于常量，拍板 Q4 允许真机数值微调——只改常量）。
/// 一次切换 = 一个实例 + 一个 startUtc（组级两个字段，不存在块级时间参数）→
/// 「同组共用同一开始/进度/结束时刻」在数据结构上成立（A5）。
/// 只用 now − start 单向差（承 M5 时钟纪律）：睡眠/挂钟跳动最多造成单次动画提前/滞后收尾，无状态破坏。
/// </summary>
public sealed record FlipTimeline
{
    /// <summary>A2：翻转时长 600 ms。</summary>
    public const double DurationMs = 600;

    /// <summary>A1：绕 Y 轴 0→180°。</summary>
    public const double MaxAngleDeg = 180;

    /// <summary>
    /// A4（拍板 Q4）：透视距离 1000 DIP——G0 裁决记录与校准点（Core 不做投影数学）。
    /// G0 结论（2026-09-21 实现期）：渲染落地走 M6 设计 §7.3 备胎 B「XAML PlaneProjection 固定透视」
    /// （GroupVisualHost 双面 PlaneProjection），A4 的 1000 DIP 未直接生效——PlaneProjection 无深度
    /// 参数，透视距离为系统固定值，与拍板 Q4 存在偏差。该偏差待人类裁决：接受备胎 B 观感，
    /// 或回主路径（PerspectiveTransform3D{Depth=1000} + CompositeTransform3D，需真机验证其
    /// Storyboard 可动画性）或备胎 A（关键帧联动 Scale 近似弱透视）——三者均只需改
    /// GroupVisualHost.BuildFlipStoryboard/BuildUnit，本常量即唯一校准输入。
    /// 设计 §14 S0 要求的 G0 裁决文档回写（技术设计 §13 风险 1）因 docs/ 目录对实现侧只读，
    /// 由主代理负责落盘。
    /// </summary>
    public const double PerspectiveDepthDip = 1000;

    /// <summary>面切换点 = 时间线中点（90°，块侧对观众）；按时间线判定、非渲染帧（T4/T5 可单测）。</summary>
    public static TimeSpan FaceSwapElapsed => TimeSpan.FromMilliseconds(DurationMs / 2);

    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);

    /// <summary>
    /// easing 进度（A3：CubicEase EaseInOut 解析式）：clamp 后 t&lt;0.5 → 4t³；否则 1−(−2t+2)³/2。
    /// 单调、Eased(0)=0、Eased(Duration)=1（T-FLIP 断言）。
    /// </summary>
    public static double EasedProgress(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return 0;
        }

        var t = elapsed.TotalMilliseconds / DurationMs;
        if (t >= 1)
        {
            return 1;
        }

        return t < 0.5
            ? 4 * t * t * t
            : 1 - (Math.Pow((-2 * t) + 2, 3) / 2);
    }

    /// <summary>角度：MaxAngleDeg × EasedProgress（同组全部分块由同一实例驱动——A5）。</summary>
    public double AngleDegAt(DateTimeOffset startUtc, DateTimeOffset nowUtc) =>
        MaxAngleDeg * EasedProgress(nowUtc - startUtc);

    /// <summary>
    /// 面可见性：elapsed &lt; 中点 → 旧图面（FrontFace）；≥ 中点 → 新图面（BackFace）。
    /// 切换点固定在 0.5（90°），按时间线而非渲染帧判定 → 可单测（§7.1）。
    /// </summary>
    public static bool BackFaceAt(TimeSpan elapsed) => elapsed >= FaceSwapElapsed;
}
