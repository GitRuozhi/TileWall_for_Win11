using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TileWall.Shell.Interop;

/// <summary>One bounded sample after desktop return, never called from a low-level input hook.</summary>
internal static class InputDesktopState
{
    internal const uint DesktopSwitchEvent = 0x20;

    internal static bool TryReadHeldKeys(uint threadId, out HashSet<int> held)
    {
        held = [];
        var ownDesktop = GetThreadDesktop(threadId); // Borrowed; never close the thread's desktop.
        if (!IsInputDesktop(ownDesktop)) return false;
        // READOBJECTS | HOOKCONTROL. A zero asynchronous state on an inaccessible desktop is not a release.
        var inputDesktop = OpenInputDesktop(0, false, 0x0009);
        if (inputDesktop == 0) return false;
        try
        {
            if (!IsInputDesktop(ownDesktop) || !IsInputDesktop(inputDesktop)) return false;
            var foreground = GetForegroundWindow();
            if (foreground == 0) return false;
            Win32Api.GetWindowThreadProcessId(foreground, out var foregroundPid);
            // UIPI can also turn an inaccessible key state into zero. Do not reconcile against a higher-IL foreground.
            if (!TryReadIntegrity((uint)Environment.ProcessId, out var ownIntegrity)
                || !TryReadIntegrity(foregroundPid, out var foregroundIntegrity) || foregroundIntegrity > ownIntegrity)
                return false;
            foreach (var key in Enumerable.Range(8, 247))
                if (key is not (0x10 or 0x11 or 0x12) && (Win32Api.GetAsyncKeyState(key) & 0x8000) != 0) held.Add(key);
            Win32Api.GetWindowThreadProcessId(foreground, out var currentPid);
            return GetForegroundWindow() == foreground && currentPid == foregroundPid
                && IsInputDesktop(ownDesktop) && IsInputDesktop(inputDesktop);
        }
        finally { _ = CloseDesktop(inputDesktop); }
    }

    private static bool IsInputDesktop(nint desktop) => desktop != 0
        && GetUserObjectInformationW(desktop, 6, out var receivesInput, sizeof(int), out _) && receivesInput != 0; // UOI_IO

    private static bool TryReadIntegrity(uint processId, out int integrity)
    {
        integrity = 0;
        using var process = new SafeProcessHandle(Win32Api.OpenProcess(0x1000, false, processId), ownsHandle: true);
        if (process.IsInvalid || !OpenProcessToken(process, 0x0008, out var token)) return false;
        using (token)
        {
            _ = GetTokenInformation(token, 25, 0, 0, out var length); // TokenIntegrityLevel / TOKEN_MANDATORY_LABEL
            if (length == 0 || length > 4096) return false;
            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetTokenInformation(token, 25, buffer, length, out _)) return false;
                var sid = Marshal.ReadIntPtr(buffer);
                var countPointer = GetSidSubAuthorityCount(sid);
                if (countPointer == 0) return false;
                var count = Marshal.ReadByte(countPointer);
                if (count == 0) return false;
                var levelPointer = GetSidSubAuthority(sid, (uint)(count - 1));
                if (levelPointer == 0) return false;
                integrity = Marshal.ReadInt32(levelPointer);
                return true;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint eventThread, uint eventTime);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(nint handle, int index, out int information, int length, out uint needed);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, nint information, uint length, out uint needed);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint index);
}
