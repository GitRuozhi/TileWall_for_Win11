using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace TileWall.Shell;

/// <summary>
/// A6/A7 显隐动画（M7 设计 §6.3；常量全部集中此处，Q2 校准轮只改常量）：
/// 呼出 = 200ms 淡入 + 上移（EaseOut）；收起 = 150ms 淡出（EaseIn）。
/// 路线注记：设计文本写 Composition/ScopedBatch；M6 实际动画路线为 Storyboard
/// （GroupVisualHost.cs:329「一个 ScopedBatch 语义 = 一个 Storyboard 同帧启动」），本类沿用 Storyboard——
/// 同具「打断即 Stop」的可打断语义（W1 等价实现），不新增 Composition 互操作面。
/// 物理可见性不在本类：SnapShow/SnapHide（AppWindow.Show/Hide）由状态机经宿主调度（W2）。
/// </summary>
public sealed class ShowHideAnimator
{
    /// <summary>A6 / 拍板 Q4：呼出 200ms。</summary>
    public const double ShowDurationMs = 200;

    /// <summary>A7 / 拍板 Q4：收起 150ms。</summary>
    public const double HideDurationMs = 150;

    /// <summary>A6「向上淡入」位移量：提案未定值 → 24 DIP 起步常量，真机观感校准项（§14 风险 6，随 Q2 拍板）。</summary>
    public const double ShowSlideDistanceDip = 24;

    private readonly UIElement _target;

    private readonly Func<double> _rasterizationScale;

    private Storyboard? _active;

    /// <summary>A6 动画自然完成（宿主须转调 WallShowMachine.ShowAnimationCompleted，§6.1 完成回调）。</summary>
    public event Action? ShowCompleted;

    /// <summary>A7 动画自然完成（宿主须转调 WallShowMachine.HideAnimationCompleted——此处才允许 SnapHide 撤销物理可见，W2）。</summary>
    public event Action? HideCompleted;

    public ShowHideAnimator(UIElement target, Func<double> rasterizationScale)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rasterizationScale);
        _target = target;
        _rasterizationScale = rasterizationScale;
        if (_target.RenderTransform is not TranslateTransform)
        {
            _target.RenderTransform = new TranslateTransform(); // Offset.Y 动画载体
        }
    }

    /// <summary>A6：淡入 + 上移（调用前物理已 SnapShow——动画只是视觉叠加）。</summary>
    public void BeginShow()
    {
        StopActive();
        var scale = SafeScale();
        var storyboard = new Storyboard();

        var opacity = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(ShowDurationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(opacity, _target);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        storyboard.Children.Add(opacity);

        var slide = new DoubleAnimation
        {
            From = ShowSlideDistanceDip * scale,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(ShowDurationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slide, _target);
        Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(TranslateTransform.Y)");
        storyboard.Children.Add(slide);

        BeginInternal(storyboard, isShow: true);
    }

    /// <summary>A7：淡出（收起只淡出，无位移——A7 仅规定时长与淡出）。</summary>
    public void BeginHide()
    {
        StopActive();
        var storyboard = new Storyboard();
        var opacity = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(HideDurationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(opacity, _target);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        storyboard.Children.Add(opacity);

        BeginInternal(storyboard, isShow: false);
    }

    /// <summary>统一启动：完成回调转事件（打断即 Stop，Stop 后 Completed 不触发——A8 打断格不误报完成）。</summary>
    private void BeginInternal(Storyboard storyboard, bool isShow)
    {
        storyboard.Completed += (sender, e) =>
        {
            if (ReferenceEquals(_active, storyboard))
            {
                _active = null;
            }

            if (isShow)
            {
                ShowCompleted?.Invoke();
            }
            else
            {
                HideCompleted?.Invoke();
            }
        };
        _active = storyboard;
        storyboard.Begin();
    }

    /// <summary>A8 打断：取消在飞动画（不排队、不补播反向动画；终态值由 Snap 方法复位）。</summary>
    public void Cancel() => StopActive();

    /// <summary>视觉复位（Snap 前调用：打断/无动画路径不留中间透明度与位移）。</summary>
    public void ResetVisual()
    {
        StopActive();
        _target.Opacity = 1;
        if (_target.RenderTransform is TranslateTransform translate)
        {
            translate.Y = 0;
        }
    }

    private void StopActive()
    {
        _active?.Stop();
        _active = null;
    }

    private double SafeScale()
    {
        var scale = _rasterizationScale();
        return scale > 0 && double.IsFinite(scale) ? scale : 1.0;
    }
}
