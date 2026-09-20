using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TileWall.Shell;

/// <summary>
/// 单槽模态会话（M4 设计 §7.2，任务书 C 条「简单方案」）：
/// 阻塞 = RootGrid.IsHitTestVisible=false + [wall-modal-mask] 遮罩（半透明、拦截残余命中，UIA 断言存在）
/// + 主墙各命令出口的防御性守卫（IsActive 检查）。同刻仅一个会话：重复请求只聚焦既有窗口（§3.3，V-08 断言点）。
/// 嵌套对话框（放弃确认、文件选择器）位于属性窗之前，关闭它们不提前解锁主墙（owned window 关系天然保证）。
/// 遮罩用 <see cref="Button"/>（有 UIA peer，§7.3 契约可断言；形状类无 peer 不可见）。
/// </summary>
public sealed class ModalSessionService
{
    private readonly Grid _host;
    private Window? _sessionWindow;
    private Button? _mask;

    public ModalSessionService(Grid host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <summary>存在活动会话 → true（各命令出口守卫依据）。</summary>
    public bool IsActive => _sessionWindow is not null;

    /// <summary>M6：会话开/关事件（轮播驱动的暂缓/恢复钩子，M6 设计 §2.2 挂接点）。</summary>
    public event Action? SessionOpened;

    public event Action? SessionClosed;

    /// <summary>打开会话窗口；已有活动会话时不叠开、只聚焦（§3.3「重复请求只聚焦」）。</summary>
    public void Open(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_sessionWindow is not null)
        {
            _sessionWindow.Activate();
            return;
        }

        _sessionWindow = window;
        _host.IsHitTestVisible = false;
        _mask = new Button
        {
            Background = new SolidColorBrush(Color.FromArgb(a: 0x50, r: 0, g: 0, b: 0)),
            BorderThickness = new Thickness(0),
            IsHitTestVisible = true,  // 拦截残余命中（点击只落遮罩）
            IsTabStop = false,        // 不参与键盘导航
        };
        AutomationProperties.SetAutomationId(_mask, "wall-modal-mask"); // §7.3：存在 = 阻塞中
        _host.Children.Add(_mask);
        window.Closed += OnSessionClosed;
        window.Activate();
        SessionOpened?.Invoke();
    }

    private void OnSessionClosed(object sender, WindowEventArgs args)
    {
        _sessionWindow = null;
        if (_mask is not null)
        {
            _host.Children.Remove(_mask);
            _mask = null;
        }

        _host.IsHitTestVisible = true;
        SessionClosed?.Invoke();
    }
}
