namespace TileWall.Shell.Interop;

/// <summary>Dispatches the documented Start system command through our own window procedure.</summary>
internal static class NativeStartMenu
{
    // WM_SYSCOMMAND / SC_TASKLIST: DefWindowProc activates the native Start menu.
    // No synthetic modifier releases: the caller may still physically hold its hotkey.
    internal static bool TryOpen(nint ownerWindow) => TryOpen(ownerWindow, Win32Api.PostMessageW);

    internal static bool TryOpen(nint ownerWindow, Func<nint, uint, nint, nint, bool> postMessage) => ownerWindow != 0
        && postMessage(ownerWindow, 0x0112, 0xF130, 0);
}
