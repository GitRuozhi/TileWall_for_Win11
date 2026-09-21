using System.Runtime.InteropServices;
using TileWall.Core.Shell;

namespace TileWall.Shell.Interop;

/// <summary>
/// 携带负载的单实例通道发送端（M9 设计 §4.3）：FindWindowW 定位本模式宿主窗 + WM_COPYDATA
/// （数据由系统在投递期间跨进程封送；发送方 wParam 传 NULL——COM/Explorer 进程无窗口，接收方不回包）。
/// 供 <see cref="SingleInstanceGate.PostPaths"/>（二次启动进程）与 <see cref="ExplorerAddRouter"/>（-Embedding 进程）共用。
/// </summary>
internal static class ExplorerAddChannel
{
    private const uint SmtoAbortifhung = 0x0002;
    private const int SendTimeoutMs = 2000;

    /// <summary>定位本模式宿主窗（类名/窗名取 ShellMessageHost 的模式常量——packaged 带 .pkg 后缀）。</summary>
    public static bool TryFindHostWindow(out IntPtr hwnd)
    {
        hwnd = Win32Api.FindWindowW(ShellMessageHost.WindowClassName, ShellMessageHost.WindowName);
        return hwnd != IntPtr.Zero;
    }

    /// <summary>向宿主窗投递路径批次（WM_COPYDATA）；找不到窗/超时/对端挂起 → false。</summary>
    public static bool TryPostPaths(IntPtr hostHwnd, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var payload = ExplorerAddPayload.Encode(paths);
        var buffer = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, buffer, payload.Length);
            var cds = new Win32Api.CopyDataStruct
            {
                DwData = IntPtr.Zero,
                CbData = payload.Length,
                LpData = buffer,
            };
            var sent = Win32Api.SendMessageTimeoutW(
                hostHwnd,
                Win32Api.WmCopydata,
                IntPtr.Zero, // 发送方无窗口；接收方只取 lParam 负载、不回包
                ref cds,
                SmtoAbortifhung,
                SendTimeoutMs,
                out _);
            return sent != IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
