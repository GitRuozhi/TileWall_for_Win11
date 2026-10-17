using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Windows.System;

namespace TileWall.Shell;

internal enum WindowsShortcut
{
    Documents, Downloads, Music, Pictures, Videos, Profile, Network, Explorer, Settings,
    AccountSettings, Lock, SignOut, Sleep, ShutDown, Restart,
}

/// <summary>Windows destinations only; no managed entry or wall state is changed.</summary>
internal static class WindowsShortcutLauncher
{
    private static Guid FolderId(WindowsShortcut shortcut) => shortcut switch
    {
        WindowsShortcut.Documents => new("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
        WindowsShortcut.Downloads => new("374DE290-123F-4565-9164-39C4925E467B"),
        WindowsShortcut.Music => new("4BD8D571-6D19-48D3-BE97-422220080E43"),
        WindowsShortcut.Pictures => new("33E28130-4E1E-4676-835A-98395C3BC3BB"),
        WindowsShortcut.Videos => new("18989B1D-99B5-455B-841C-AB7C74E4DDFC"),
        WindowsShortcut.Profile => new("5E6C858F-0E22-4760-9AFE-EA3317B67173"),
        _ => Guid.Empty,
    };

    internal static bool IsAvailable(WindowsShortcut shortcut)
    {
        try
        {
            return shortcut switch
            {
                WindowsShortcut.Sleep => !PolicyEnabled("Explorer", "NoClose") && IsPwrSuspendAllowed() && HasShutdownPrivilege(),
                WindowsShortcut.ShutDown or WindowsShortcut.Restart => !PolicyEnabled("Explorer", "NoClose") && IsPwrShutdownAllowed() && HasShutdownPrivilege(),
                WindowsShortcut.Lock => !PolicyEnabled("System", "DisableLockWorkstation"),
                WindowsShortcut.SignOut => !PolicyEnabled("Explorer", "NoLogoff"),
                _ => true,
            };
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        { return false; }
    }

    // Called only by a destination button or a specific account/power menu item.
    internal static async Task<string?> OpenAsync(WindowsShortcut shortcut)
    {
        try
        {
            if (!IsAvailable(shortcut)) throw new Win32Exception(5);
            switch (shortcut)
            {
                case WindowsShortcut.Settings:
                case WindowsShortcut.AccountSettings:
                    if (!await Launcher.LaunchUriAsync(new Uri(shortcut == WindowsShortcut.Settings
                            ? "ms-settings:" : "ms-settings:yourinfo")))
                        throw new Win32Exception(1155);
                    break;
                case WindowsShortcut.Lock:
                    if (!LockWorkStation()) throw new Win32Exception(Marshal.GetLastWin32Error());
                    break;
                case WindowsShortcut.SignOut:
                    // No FORCE flags: Windows may ask applications to save their work.
                    if (!ExitWindowsEx(0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    break;
                case WindowsShortcut.Sleep:
                case WindowsShortcut.ShutDown:
                case WindowsShortcut.Restart:
                    ExecutePower(shortcut);
                    break;
                default:
                    var folder = FolderId(shortcut);
                    if (folder == Guid.Empty && shortcut is not (WindowsShortcut.Network or WindowsShortcut.Explorer))
                        throw new ArgumentOutOfRangeException(nameof(shortcut));
                    var target = folder != Guid.Empty ? QueryFolder(folder) : shortcut == WindowsShortcut.Network
                        ? "shell:::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}" : null;
                    var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                        { UseShellExecute = true };
                    if (target is not null) start.Arguments = "\"" + target + "\"";
                    using (Process.Start(start)) { }
                    break;
            }
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or
            COMException or ArgumentException or System.Security.SecurityException)
        { return ex.Message; }
    }

    private static string QueryFolder(Guid id)
    {
        var result = SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var path);
        try
        {
            Marshal.ThrowExceptionForHR(result);
            return Marshal.PtrToStringUni(path) ?? throw new IOException("Windows Known Folder is unavailable.");
        }
        finally { if (path != IntPtr.Zero) Marshal.FreeCoTaskMem(path); }
    }

    private static bool PolicyEnabled(string area, string name)
    {
        var path = @"Software\Microsoft\Windows\CurrentVersion\Policies\" + area;
        using var user = Registry.CurrentUser.OpenSubKey(path);
        using var machine = Registry.LocalMachine.OpenSubKey(path);
        return user?.GetValue(name) is int userValue && userValue != 0 ||
            machine?.GetValue(name) is int machineValue && machineValue != 0;
    }

    private static bool HasShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out var token)) return false;
        using (token)
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var privilege)) return false;
            GetTokenInformation(token, 3, IntPtr.Zero, 0, out var length);
            if (length == 0) return false;
            var buffer = Marshal.AllocHGlobal(checked((int)length));
            try
            {
                if (!GetTokenInformation(token, 3, buffer, length, out _)) return false;
                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count; i++)
                {
                    var value = Marshal.PtrToStructure<LuidAndAttributes>(IntPtr.Add(buffer, 4 + i * 12));
                    if (value.Luid.Low == privilege.Low && value.Luid.High == privilege.High && (value.Attributes & 4) == 0)
                        return true; // Present but disabled privileges can be enabled at the explicit click.
                }
                return false;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private static void ExecutePower(WindowsShortcut shortcut)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0028, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var desired = new TokenPrivileges { Count = 1, Privilege = new LuidAndAttributes { Luid = luid, Attributes = 2 } };
            if (!AdjustTokenPrivileges(token, false, ref desired, (uint)Marshal.SizeOf<TokenPrivileges>(), out var previous, out _) ||
                Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var accepted = shortcut == WindowsShortcut.Sleep ? SetSuspendState(false, false, false)
                    : ExitWindowsEx(shortcut == WindowsShortcut.Restart ? 2u : 1u, 0x80040000);
                if (!accepted) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { AdjustTokenPrivileges(token, false, ref previous, 0, out _, out _); }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Luid { internal uint Low; internal int High; }
    [StructLayout(LayoutKind.Sequential)] private struct LuidAndAttributes { internal Luid Luid; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { internal uint Count; internal LuidAndAttributes Privilege; }

    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr data, uint size, out uint required);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges state, uint size, out TokenPrivileges previous, out uint required);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LockWorkStation();
    [DllImport("powrprof.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool IsPwrSuspendAllowed();
    [DllImport("powrprof.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool IsPwrShutdownAllowed();
    [DllImport("powrprof.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWake);
}
