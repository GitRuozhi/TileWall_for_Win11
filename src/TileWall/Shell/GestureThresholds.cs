namespace TileWall.Shell;

/// <summary>
/// 手势参数常量（P1 §2.1、拍板 Q5 确认；M3 设计 §5.1——不散落魔法数）。
/// </summary>
public static class GestureThresholds
{
    /// <summary>B1：鼠标拖动启动位移（DIP）。</summary>
    public const double DragStartMouseDip = 8;

    /// <summary>触摸拖动启动位移（DIP，拍板 Q8 尽力兼容）。</summary>
    public const double DragStartTouchDip = 12;

    /// <summary>B4：悬停预览触发延时（毫秒）。</summary>
    public const int PreviewHoverDelayMs = 250;

    /// <summary>B5：预览位移动画时长（毫秒）。</summary>
    public const int PreviewMoveDurationMs = 200;

    /// <summary>B6：ghost 不透明度。</summary>
    public const double GhostOpacity = 0.7;

    /// <summary>B6：拖动中原位元素降隐不透明度。</summary>
    public const double OriginDimOpacity = 0.3;
}
