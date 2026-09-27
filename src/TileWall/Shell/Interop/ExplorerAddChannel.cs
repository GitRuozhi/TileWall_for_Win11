using System.Runtime.InteropServices;

using TileWall.Core.Shell;

namespace TileWall.Shell.Interop;

/// <summary>
/// 携带负载的单实例通道发送端（M9 设计 §4.3）：FindWindowW 定位本模式宿主窗 + WM_COPYDATA
/// （数据由系统在投递期间跨进程封送）。C1/F-01：dwData 携带私有魔数（'TWL1'），wParam 携带
/// 发送方身份窗口句柄——接收端据此反查发送进程映像完成鉴权；身份窗口为本进程隐藏令牌窗
/// （<see cref="SenderWindowToken"/>，每次同步投递在当前线程创建并释放），COM/二次启动进程都无窗也由此获得身份。
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
        using var sender = SenderWindowToken.TryCreate();
        if (sender is null)
        {
            AppLog.Error("IpcAuth", "发送方身份窗口创建失败：本次投递已放弃");
            return false;
        }

        var payload = ExplorerAddPayload.Encode(paths, truncated);
        var buffer = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, buffer, payload.Length);
            var cds = new Win32Api.CopyDataStruct
            {
                DwData = new IntPtr(ExplorerAddAuth.Magic), // C1：私有魔数，接收端不符即拒
                CbData = payload.Length,
                LpData = buffer,
            };
            var sent = Win32Api.SendMessageTimeoutW(
                hostHwnd,
                Win32Api.WmCopydata,
                sender.Hwnd, // C1：wParam 带本次投递的发送窗口句柄，供对端反查进程映像
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
/// C1：发送方身份令牌窗（隐藏 0×0 顶层窗，类名 TileWall_Sender_Class）。
/// WM_COPYDATA 的鉴权需要「可归属进程的窗口句柄」作内核级发送方凭证；Explorer COM 进程与
/// 二次启动进程都没有常驻窗，这是唯一可行身份载体。它不是宿主窗：类名不同、不收任何业务消息
/// （WndProc 直通 DefWindowProc），不干扰 FindWindow 定位，也不构成「建窗副作用」红线所指的
/// 可见窗/XAML（红线本意：不弹墙、不碰宿主身份）。
/// </summary>
internal sealed class SenderWindowToken : IDisposable
{
    internal const string WindowClassName = "TileWall_Sender_Class";

    private static readonly Win32Api.WndProc WndProcStub = DefWindowProcThunk; // 根引用防 GC 后原生回调野指针

    private SenderWindowToken(IntPtr hwnd)
    {
        Hwnd = hwnd;
    }

    public IntPtr Hwnd { get; }

    public static SenderWindowToken? TryCreate()
    {
        var hInstance = Marshal.GetHINSTANCE(typeof(SenderWindowToken).Module);
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
                AppLog.Error("IpcAuth", $"身份窗注册失败：RegisterClassExW 错误码 {registerError}");
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
            AppLog.Error("IpcAuth", $"身份窗创建失败：CreateWindowExW 错误码 {Marshal.GetLastWin32Error()}");
            return null;
        }

        return new SenderWindowToken(hwnd);
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
