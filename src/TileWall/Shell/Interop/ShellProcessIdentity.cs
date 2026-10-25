using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TileWall.Core.Shell;

namespace TileWall.Shell.Interop;

internal static class ShellProcessIdentity
{
    internal static ExplorerAddProcessIdentity? Read(uint processId)
    {
        using var process = new SafeProcessHandle(Win32Api.OpenProcess(
            Win32Api.ProcessQueryLimitedInformation, false, processId), ownsHandle: true);
        if (process.IsInvalid || !IsRunning(process)) return null;
        var path = new StringBuilder(32768);
        var size = (uint)path.Capacity;
        if (!Win32Api.QueryFullProcessImageNameW(process.DangerousGetHandle(), 0, path, ref size) ||
            !OpenProcessToken(process, 0x0008, out var token)) return null;
        using (token)
        {
            try
            {
                using var identity = new WindowsIdentity(token.DangerousGetHandle());
                var sid = identity.User?.Value;
                if (string.IsNullOrWhiteSpace(sid) ||
                    !GetTokenInformation(token, 12, out var sessionId, sizeof(int), out var returned) ||
                    returned != sizeof(int) || !IsRunning(process)) return null;
                var image = path.ToString();
                var recognized = ExplorerAddRoutingFilter.DeclaredWindowImageMatches(image, Environment.ProcessPath) ||
                    ExplorerContextMenuRegistration.IsRecognizedTileWall(image);
                return new(image, sid, sessionId, recognized);
            }
            catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException or Win32Exception)
            {
                return null;
            }
        }
    }

    private static bool IsRunning(SafeProcessHandle process) =>
        GetExitCodeProcess(process, out var exitCode) && exitCode == 259; // STILL_ACTIVE

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out int information, uint length, out uint needed);
}
