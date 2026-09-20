using System.Runtime.InteropServices;

namespace TileWall.Shell.Interop;

/// <summary>owned window 关系设置（GWLP_HWNDPARENT；会话窗恒在墙前、不抢系统全局置顶，M4 §3.3 既有模式）。</summary>
internal static class WindowOwner
{
    private const int GwlHwndParent = -8;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>HWND 句柄值为 32 位安全句柄：x86/x64 分别走 SetWindowLong(W)/(Ptr)W。</summary>
    internal static void SetOwnerWindow(IntPtr hwnd, IntPtr ownerHwnd)
    {
        if (hwnd == IntPtr.Zero || ownerHwnd == IntPtr.Zero)
        {
            return;
        }

        if (Environment.Is64BitProcess)
        {
            _ = SetWindowLongPtr64(hwnd, GwlHwndParent, ownerHwnd);
        }
        else
        {
            _ = SetWindowLong32(hwnd, GwlHwndParent, ownerHwnd.ToInt32());
        }
    }
}
