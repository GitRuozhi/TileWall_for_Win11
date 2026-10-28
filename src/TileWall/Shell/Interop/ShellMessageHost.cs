using System.Runtime.InteropServices;

using TileWall.Core.Shell;

namespace TileWall.Shell.Interop;

/// <summary>托盘回调通知（v4 协议：事件在 lParam 低字、图标 ID 在高字；仅特定事件定义 wParam 坐标）。</summary>
public readonly record struct TrayNotification(uint Message, IntPtr WParam, IntPtr LParam)
{
    /// <summary>通知事件（WM_LBUTTONDBLCLK / WM_CONTEXTMENU / WM_MOUSEMOVE …）。</summary>
    public uint Event => Win32Api.LowWord(LParam);

    /// <summary>仅当 v4 协议为此事件定义 wParam 锚点时，提取有符号屏幕坐标。</summary>
    public bool TryGetAnchor(out int x, out int y) => NotifyIconV4Coordinates.TryDecode(Event, WParam, out x, out y);
}

/// <summary>
/// 共享隐藏宿主窗（M7 设计 §4.2）：UI 线程上的永不显示 Win32 顶层窗，一个 HWND 承载四路系统输入——
/// 托盘 v4 回调消息、WM_HOTKEY、二次启动激活消息、TaskbarCreated 广播。
/// 类名 TileWall_ShellHost_Class；窗名 TileWall.ShellHost.1（供二次启动 FindWindowW 定位，§7.1）。
/// 不调用 ShowWindow（隐藏顶层窗仍收广播；message-only 窗收不到 TaskbarCreated，故不用 HWND_MESSAGE）。
/// 全部事件在创建线程（UI 线程）触发，路由器无需跨线程封送。
/// </summary>
public sealed class ShellMessageHost : IDisposable
{
    public const string WindowClassName = "TileWall_ShellHost_Class";

    private static ShellMessageHost? _instance; // WNDPROC 为静态委托：进程内仅一例，经它回查实例

    private readonly Win32Api.WndProc _wndProc; // 根引用：防止委托被 GC 回收后原生回调野指针

    private bool _disposed;

    /// <summary>宿主窗句柄（热键注册、托盘 NIM_ADD、FindWindow 定位均用它）。</summary>
    public IntPtr Hwnd { get; }

    /// <summary>托盘回调注册消息（"TileWall.Tray.Callback"）。</summary>
    public uint TrayCallbackMessage { get; }

    /// <summary>二次启动激活注册消息（"TileWall.SingleInstance.Activate"）。</summary>
    public uint SingleInstanceActivateMessage { get; }

    /// <summary>Explorer 重启广播（"TaskbarCreated"，系统分配值）。</summary>
    public uint TaskbarCreatedMessage { get; }

    /// <summary>WM_HOTKEY for one of the two owned registration IDs.</summary>
    public event Action<int>? HotKeyPressed;

    /// <summary>托盘 v4 回调（单击无分支——B9 由分发表结构保证）。</summary>
    public event Action<TrayNotification>? TrayNotified;

    /// <summary>TaskbarCreated 广播 → 托盘图标重建（Explorer 重启自愈）。</summary>
    public event Action? TaskbarCreated;

    /// <summary>二次启动汇入 → Router.ShowOrFocus（与托盘双击同一条命令）。</summary>
    public event Action? ActivateRequested;

    internal Func<ExplorerAddMessage, bool>? AcceptPaths { get; set; }

    /// <summary>
    /// M8 显示环境变化广播（§6.2）：WM_DISPLAYCHANGE（分辨率/显示器变更）与 WM_SETTINGCHANGE
    /// （SPI_SETWORKAREA 等系统设置广播同路）→ 事件；路由器侧 500 ms 防抖合并连发后统一评估。
    /// 个别驱动/虚拟机漏报不影响正确性：每次墙显示前的兜底评估（R-2）保证「显示出的墙必适配」。
    /// </summary>
    public event Action? DisplayChanged;

    public ShellMessageHost(string windowName)
    {
        TrayCallbackMessage = Win32Api.RegisterWindowMessageW("TileWall.Tray.Callback");
        SingleInstanceActivateMessage = Win32Api.RegisterWindowMessageW("TileWall.SingleInstance.Activate");
        TaskbarCreatedMessage = Win32Api.RegisterWindowMessageW("TaskbarCreated");

        _wndProc = WndProc;
        var hInstance = Marshal.GetHINSTANCE(typeof(ShellMessageHost).Module);
        var windowClass = new Win32Api.WndClassExW
        {
            cbSize = (uint)Marshal.SizeOf<Win32Api.WndClassExW>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = WindowClassName,
        };
        if (Win32Api.RegisterClassExW(ref windowClass) == 0)
        {
            var registerError = Marshal.GetLastWin32Error();
            if (registerError != 1410) // 1410 = ERROR_CLASS_ALREADY_EXISTS（极端重入场景下复用旧类）
            {
                throw new InvalidOperationException($"RegisterClassExW 失败：{registerError}");
            }
        }

        Hwnd = Win32Api.CreateWindowExW(
            Win32Api.WsExToolWindow | Win32Api.WsExNoActivate,
            WindowClassName,
            windowName,
            Win32Api.WsPopup, // WS_POPUP 且从不 ShowWindow：隐藏的顶层窗，广播可达
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowExW 失败：{Marshal.GetLastWin32Error()}");
        }

        _instance = this;
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_instance is { } self && hwnd == self.Hwnd)
        {
            if (msg == Win32Api.WmHotkey)
            {
                var hotKeyId = wParam.ToInt64();
                if (hotKeyId is 1 or 2) self.HotKeyPressed?.Invoke((int)hotKeyId);
                return IntPtr.Zero;
            }

            if (msg == Win32Api.WmCopydata)
            {
                // M9 §4.3：负载只在本次投递期间有效——先同步拷贝并解码，再转 UI 线程事件；返回 1 表示已处理。
                // 先按私有魔数和声明 HWND 的进程映像路径过滤；这不认证真实发送进程。
                if (TryReceiveCopyData(wParam, lParam, out var message))
                {
                    if (self.AcceptPaths is null || !self.AcceptPaths(message)) return IntPtr.Zero;
                    return (IntPtr)1;
                }

                return IntPtr.Zero;
            }

            if (msg == self.TrayCallbackMessage)
            {
                self.TrayNotified?.Invoke(new TrayNotification(msg, wParam, lParam));
                return IntPtr.Zero;
            }

            if (msg == self.TaskbarCreatedMessage)
            {
                self.TaskbarCreated?.Invoke();
                return IntPtr.Zero;
            }

            if (msg == self.SingleInstanceActivateMessage)
            {
                self.ActivateRequested?.Invoke();
                return IntPtr.Zero;
            }

            if (msg == Win32Api.WmDisplaychange || msg == Win32Api.WmSettingchange)
            {
                self.DisplayChanged?.Invoke();
                return IntPtr.Zero;
            }
        }

        return Win32Api.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// M9 §4.3：按 dwData 魔数 'TWL1' + wParam 声明 HWND 所属进程的映像路径做路由过滤；
    /// 这不证明实际发送进程身份，也不作为 IPC 安全认证，
    /// 通过才从 COPYDATASTRUCT 同步拷出字节并解码校验；任一步失败按拒绝丢弃（返回 0，记 WARN 可追溯）。
    /// </summary>
    private static bool TryReceiveCopyData(IntPtr declaredWindowHwnd, IntPtr lParam, out ExplorerAddMessage message)
    {
        message = ExplorerAddMessage.Empty;
        if (lParam == IntPtr.Zero)
        {
            return false;
        }

        var cds = Marshal.PtrToStructure<Win32Api.CopyDataStruct>(lParam);

        if (cds.CbData <= 0 || cds.LpData == IntPtr.Zero || cds.CbData > ExplorerAddPayload.MaxPayloadBytes ||
            !ExplorerAddRoutingFilter.HasMagic(unchecked((uint)cds.DwData.ToInt64()))) return false;

        var declaredProcess = TryGetWindowProcess(declaredWindowHwnd, out var declaredWindowDescribe);
        var selfProcess = ShellProcessIdentity.Read((uint)Environment.ProcessId);
        var verdict = ExplorerAddRoutingFilter.Evaluate(unchecked((uint)cds.DwData.ToInt64()), declaredProcess, selfProcess);
        if (verdict != ExplorerAddRoutingVerdict.Accept)
        {
            AppLog.Warn("IpcRoute", $"WM_COPYDATA 声明 HWND 路由过滤拒绝（{verdict}）：{declaredWindowDescribe}");
            return false;
        }

        var payload = new byte[cds.CbData];
        Marshal.Copy(cds.LpData, payload, 0, cds.CbData);
        return ExplorerAddPayload.TryDecode(payload, out message);
    }

    /// <summary>
    /// 声明 HWND → 所属进程 ID → 进程映像路径。任一步失败返回 null（保守拒绝）。
    /// 过滤拒绝时返回 0 不处理，绝不因过滤失败崩溃或弹窗。
    /// </summary>
    private static ExplorerAddProcessIdentity? TryGetWindowProcess(IntPtr hwnd, out string describe)
    {
        describe = $"hwnd=0x{hwnd.ToInt64():X}";
        if (hwnd == IntPtr.Zero)
        {
            return null; // 发送方未提供窗口句柄：无从归属进程
        }

        if (Win32Api.GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0)
        {
            return null; // 句柄已失效或非窗口
        }

        describe += $" pid={pid}";
        var identity = ShellProcessIdentity.Read(pid);
        // Re-read window ownership after token/image sampling to reject a stale HWND.
        return Win32Api.GetWindowThreadProcessId(hwnd, out var currentPid) != 0 && currentPid == pid ? identity : null;
    }

    /// <summary>销毁宿主窗（R4；须在 UI 线程调用——退出序列保证）。类随进程注销，无需显式 Unregister。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Hwnd != IntPtr.Zero)
        {
            _ = Win32Api.DestroyWindow(Hwnd);
        }

        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }

        GC.KeepAlive(_wndProc);
    }
}
