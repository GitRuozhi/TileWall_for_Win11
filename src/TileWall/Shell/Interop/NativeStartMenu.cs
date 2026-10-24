namespace TileWall.Shell.Interop;

/// <summary>Dispatches the documented Start system command through our own window procedure.</summary>
internal static class NativeStartMenu
{
    // WM_SYSCOMMAND / SC_TASKLIST: DefWindowProc activates the native Start menu.
    // No synthetic modifier releases: the caller may still physically hold its hotkey.
    internal static bool TryOpen(nint ownerWindow) => ownerWindow != 0
        && Win32Api.PostMessageW(ownerWindow, 0x0112, 0xF130, 0);
}
