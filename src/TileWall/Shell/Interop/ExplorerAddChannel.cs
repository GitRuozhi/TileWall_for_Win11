using System.Runtime.InteropServices;

using TileWall.Core.Shell;

namespace TileWall.Shell.Interop;

/// <summary>
/// 携带负载的单实例通道发送端（M9 设计 §4.3）：FindWindowW 定位本模式宿主窗 + WM_COPYDATA
/// （数据由系统在投递期间跨进程封送）。dwData 携带私有魔数（'TWL1'），wParam 携带
/// 本进程临时 HWND，接收端据此查询其所属进程映像路径作路由过滤；该声明 HWND 不认证实际发送进程。
/// 临时窗口（<see cref="SenderWindowReference"/>）在当前线程创建并释放，供 COM/二次启动进程使用。
/// 供 <see cref="SingleInstanceGate.PostPaths"/>（二次启动进程）与 <see cref="ExplorerAddRouter"/>（-Embedding 进程）共用。
/// </summary>
internal static class ExplorerAddChannel
{
    private const uint SmtoAbortifhung = 0x0002;
    private const int SendTimeoutMs = 2000;

    /// <summary>定位本模式宿主窗（类名/窗名取 ShellMessageHost 的模式常量——packaged 带 .pkg 后缀）。</summary>
    public static bool TryFindHostWindow(string windowName, out IntPtr hwnd)
    {
        hwnd = Win32Api.FindWindowW(ShellMessageHost.WindowClassName, windowName);
        return hwnd != IntPtr.Zero;
    }

    /// <summary>向宿主窗投递路径批次（WM_COPYDATA）；找不到窗/超时/对端挂起 → false。</summary>
    public static bool TryPostPaths(IntPtr hostHwnd, IReadOnlyList<string> paths, bool truncated = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        using var senderWindow = SenderWindowReference.TryCreate();
        if (senderWindow is null)
        {
            AppLog.Error("IpcRoute", "声明 HWND 创建失败：本次投递已放弃");
            return false;
        }

        var payload = ExplorerAddPayload.Encode(paths, truncated);
        var buffer = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, buffer, payload.Length);
            var cds = new Win32Api.CopyDataStruct
            {
                DwData = new IntPtr(ExplorerAddRoutingFilter.Magic), // 私有魔数，接收端不符即拒
                CbData = payload.Length,
                LpData = buffer,
            };
            var sent = Win32Api.SendMessageTimeoutW(
                hostHwnd,
                Win32Api.WmCopydata,
                senderWindow.Hwnd, // wParam 声明本次投递的 HWND，供对端查询映像路径作路由过滤
                ref cds,
                SmtoAbortifhung,
                SendTimeoutMs,
                out var accepted);
            return sent != IntPtr.Zero && accepted != IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

}

/// <summary>
/// WM_COPYDATA 中声明 HWND 的临时引用窗（隐藏 0×0 顶层窗，类名 TileWall_Sender_Class）。
/// 接收端将它映射到所属进程映像路径进行路由过滤；这是路由线索，不是实际发送方凭证。
/// 它不是宿主窗：类名不同、不收任何业务消息（WndProc 直通 DefWindowProc），不干扰 FindWindow
/// 定位，也不创建可见窗/XAML。
/// </summary>
internal sealed class SenderWindowReference : IDisposable
{
    internal const string WindowClassName = "TileWall_Sender_Class";

    private static readonly Win32Api.WndProc WndProcStub = DefWindowProcThunk; // 根引用防 GC 后原生回调野指针

    private SenderWindowReference(IntPtr hwnd)
    {
        Hwnd = hwnd;
    }

    public IntPtr Hwnd { get; }

    public static SenderWindowReference? TryCreate()
    {
        var hInstance = Marshal.GetHINSTANCE(typeof(SenderWindowReference).Module);
        var windowClass = new Win32Api.WndClassExW
        {
            cbSize = (uint)Marshal.SizeOf<Win32Api.WndClassExW>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcStub),
            hInstance = hInstance,
            lpszClassName = WindowClassName,
        };
        if (Win32Api.RegisterClassExW(ref windowClass) == 0)
        {
            var registerError = Marshal.GetLastWin32Error();
            if (registerError != 1410) // 1410 = ERROR_CLASS_ALREADY_EXISTS（进程内二次创建直接复用）
            {
                AppLog.Error("IpcRoute", $"声明 HWND 窗口类注册失败：RegisterClassExW 错误码 {registerError}");
                return null;
            }
        }

        var hwnd = Win32Api.CreateWindowExW(
            Win32Api.WsExToolWindow | Win32Api.WsExNoActivate,
            WindowClassName,
            WindowClassName,
            Win32Api.WsPopup,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            AppLog.Error("IpcRoute", $"声明 HWND 创建失败：CreateWindowExW 错误码 {Marshal.GetLastWin32Error()}");
            return null;
        }

        return new SenderWindowReference(hwnd);
    }

    public void Dispose()
    {
        if (Hwnd != IntPtr.Zero)
        {
            _ = Win32Api.DestroyWindow(Hwnd);
        }
    }

    private static IntPtr DefWindowProcThunk(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        Win32Api.DefWindowProcW(hWnd, msg, wParam, lParam);
}
