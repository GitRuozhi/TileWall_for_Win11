using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace TileWall.Shell;

/// <summary>
/// 墙可见性状态源（M6 设计 §8.2）：`IsVisible` + Shown/Hidden 事件。
/// M6 最小实现 = 墙窗口 AppWindow 的可见标志（AppWindow.Changed 的 DidVisibilityChange）；
/// 托盘/热键（后续里程碑）接入同一接口：收起 → Hidden → 停表；呼出 → Shown → 恢复检查——轮播零改动。
/// </summary>
public interface IWallVisibility
{
    bool IsVisible { get; }

    event Action? Shown;

    event Action? Hidden;
}

/// <summary>IWallVisibility 的墙窗口实现（AppWindow.IsVisible 真值源）。</summary>
public sealed class WallVisibility : IWallVisibility
{
    private readonly AppWindow _appWindow;
    private bool _isVisible;

    public WallVisibility(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _appWindow = window.AppWindow;
        _isVisible = _appWindow.IsVisible;
        _appWindow.Changed += OnAppWindowChanged;
    }

    public bool IsVisible => _isVisible;

    public event Action? Shown;

    public event Action? Hidden;

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidVisibilityChange)
        {
            return;
        }

        var now = sender.IsVisible;
        if (now == _isVisible)
        {
            return;
        }

        _isVisible = now;
        (now ? Shown : Hidden)?.Invoke();
    }
}
