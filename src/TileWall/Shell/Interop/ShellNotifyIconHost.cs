using System.Runtime.InteropServices;

namespace TileWall.Shell.Interop;

/// <summary>
/// 托盘图标宿主（M7 设计 §4）：Shell_NotifyIconW P/Invoke（NOTIFYICON_VERSION_4 协议），零第三方依赖。
/// 回调分发表（§4.2）：
///   WM_LBUTTONDBLCLK → ShowOrFocusRequested（只显示/聚焦，绝不收起）；
///   WM_CONTEXTMENU   → 弹固定两项 HMENU（设置 / 退出 TileWall），TrackPopupMenuEx 前置
///                      SetForegroundWindow（KB135788：不前置则点击菜单外不消失），TPM_RETURNCMD 同步取回；
///   WM_LBUTTONDOWN / WM_LBUTTONUP / WM_MOUSEMOVE → 无分支（结构上不存在单击动作——B9 与
///                      「双击过程不得先触发单击」由此天然成立）；
///   TaskbarCreated 广播 → 重新 NIM_ADD（Explorer 重启自愈）。
/// 资源成对（§10）：NIM_ADD↔NIM_DELETE（R1）、LoadImageW↔DestroyIcon（R2）、CreatePopupMenu↔DestroyMenu（R3 即建即毁）。
/// </summary>
public sealed class ShellNotifyIconHost : IDisposable
{
    private const uint IconId = 1;

    private const int MenuIdSettings = 1;

    private const int MenuIdExit = 2;

    private readonly ShellMessageHost _host;

    private IntPtr _icon;

    private bool _iconFromFile; // 仅文件加载的图标可 DestroyIcon（系统共享图标不可销毁）

    private bool _added;

    private bool _disposed;

    /// <summary>托盘左键双击 → Router.ShowOrFocus。</summary>
    public event Action? ShowOrFocusRequested;

    /// <summary>托盘菜单「设置」→ Router.OpenSettings（C09：快捷键失效时的保底入口，无条件可用）。</summary>
    public event Action? SettingsRequested;

    /// <summary>托盘菜单「退出 TileWall」→ Router.RequestExit。</summary>
    public event Action? ExitRequested;

    /// <summary>NIM_ADD 是否成功（极端 shell 状态下为 false：托盘缺席但热键/右键入口仍可用，§4.2 失真路径）。</summary>
    public bool IsAdded => _added;

    public ShellNotifyIconHost(ShellMessageHost host, string iconFilePath)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrEmpty(iconFilePath);
        _host = host;
        _icon = LoadIconHandle(iconFilePath);
        _host.TrayNotified += OnTrayNotified;
        _host.TaskbarCreated += OnTaskbarCreated;
        Add();
    }

    /// <summary>NIM_ADD + NIM_SETVERSION（v4 协议）。</summary>
    private void Add()
    {
        var data = Win32Api.NewNotifyIconData(_host.Hwnd, IconId);
        data.uFlags = Win32Api.NifMessage | Win32Api.NifIcon | Win32Api.NifTip;
        data.uCallbackMessage = _host.TrayCallbackMessage;
        data.hIcon = _icon;
        data.szTip = TooltipText;
        _added = Win32Api.Shell_NotifyIconW(Win32Api.NimAdd, ref data);
        if (_added)
        {
            data.uVersion = Win32Api.NotifyIconVersion4;
            _ = Win32Api.Shell_NotifyIconW(Win32Api.NimSetversion, ref data);
        }
    }

    /// <summary>当前 tooltip 文本（快捷键未注册时呈现 C09 真实状态）。</summary>
    public string TooltipText { get; private set; } = "TileWall";

    /// <summary>更新 tooltip（NIM_MODIFY；未加上图标时仅记录文本，TaskbarCreated 重建时带上）。</summary>
    public void UpdateTooltip(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        TooltipText = text;
        if (!_added)
        {
            return;
        }

        var data = Win32Api.NewNotifyIconData(_host.Hwnd, IconId);
        data.uFlags = Win32Api.NifTip;
        data.szTip = text;
        _ = Win32Api.Shell_NotifyIconW(Win32Api.NimModify, ref data);
    }

    private void OnTrayNotified(TrayNotification notification)
    {
        switch (notification.Event)
        {
            case Win32Api.WmLbuttondblclk:
                ShowOrFocusRequested?.Invoke();
                break;
            case Win32Api.WmContextmenu:
                ShowMenu(notification.AnchorX, notification.AnchorY);
                break;
            default:
                // WM_LBUTTONDOWN/UP/MOUSEMOVE：无分支 = B9「单击动作一律不注册」（分发表里根本不存在）
                break;
        }
    }

    /// <summary>托盘右键菜单：固定两项、即建即毁（R3）；KB135788 时序硬验收（T-MANUAL-④）。</summary>
    private void ShowMenu(int anchorX, int anchorY)
    {
        var x = anchorX;
        var y = anchorY;
        if (x < 0 || y < 0)
        {
            if (!Win32Api.GetCursorPos(out var point)) // 键盘（Shift+F10）触发：坐标为 -1，退回光标处
            {
                return;
            }

            x = point.X;
            y = point.Y;
        }

        var menu = Win32Api.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = Win32Api.AppendMenuW(menu, Win32Api.MfString, (UIntPtr)MenuIdSettings, "设置");
            _ = Win32Api.AppendMenuW(menu, Win32Api.MfSeparator, UIntPtr.Zero, null);
            _ = Win32Api.AppendMenuW(menu, Win32Api.MfString, (UIntPtr)MenuIdExit, "退出 TileWall");

            _ = Win32Api.SetForegroundWindow(_host.Hwnd); // 经典 KB135788：缺此步点外不消失
            var command = Win32Api.TrackPopupMenuEx(menu, Win32Api.TpmReturncmd | Win32Api.TpmRightbutton, x, y, _host.Hwnd, IntPtr.Zero);
            switch (command)
            {
                case MenuIdSettings:
                    SettingsRequested?.Invoke();
                    break;
                case MenuIdExit:
                    ExitRequested?.Invoke();
                    break;
            }

            _ = Win32Api.PostMessageW(_host.Hwnd, Win32Api.WmNull, IntPtr.Zero, IntPtr.Zero); // 前置消息泵收尾
        }
        finally
        {
            _ = Win32Api.DestroyMenu(menu); // 即建即毁，不常驻
        }
    }

    private void OnTaskbarCreated()
    {
        if (!_added)
        {
            Add(); // Explorer 重启自愈（重建时带上最新 tooltip 文本）
        }
    }

    private IntPtr LoadIconHandle(string iconFilePath)
    {
        var small = Win32Api.GetSystemMetrics(Win32Api.SmCxsmicon);
        var size = Win32Api.GetSystemMetrics(Win32Api.SmCysmicon);
        var icon = Win32Api.LoadImageW(IntPtr.Zero, iconFilePath, Win32Api.ImageIcon, small, size, Win32Api.LrLoadfromfile);
        if (icon != IntPtr.Zero)
        {
            _iconFromFile = true; // 仅文件加载的图标可销毁
            return icon;
        }

        // 图标文件缺失/损坏 → 系统默认图标兜底（托盘功能不因资产缺失失效；共享图标不可销毁）
        _iconFromFile = false;
        return LoadSharedApplicationIcon();
    }

    /// <summary>IDI_APPLICATION（共享句柄，不可 DestroyIcon）。</summary>
    private static IntPtr LoadSharedApplicationIcon() => LoadIconW(IntPtr.Zero, (IntPtr)32512);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    /// <summary>摘除托盘（R1）+ 销毁图标（R2）。ExitCoordinator 释放序调用；进程退出路径全覆盖。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.TrayNotified -= OnTrayNotified;
        _host.TaskbarCreated -= OnTaskbarCreated;
        if (_added)
        {
            var data = Win32Api.NewNotifyIconData(_host.Hwnd, IconId);
            _ = Win32Api.Shell_NotifyIconW(Win32Api.NimDelete, ref data); // 不留幽灵图标
            _added = false;
        }

        if (_icon != IntPtr.Zero)
        {
            if (_iconFromFile)
            {
                _ = Win32Api.DestroyIcon(_icon);
            }

            _icon = IntPtr.Zero;
            _iconFromFile = false;
        }
    }
}
