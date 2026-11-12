// Adapted from ReStart (MIT), commits 297c8f5 / b9c2718 and their base.
// Copyright (c) 2026 ReStart contributors. See Licenses/ReStartLicense.txt.
using TileWall.ApplicationModel;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TileWall.Shell.Interop;

/// <summary>Reads bounded native clipping geometry without collecting taskbar text or pixels.</summary>
internal static class TaskbarNativeRegion
{
    /// <summary>Recognizes a complete rectangular drawable surface, excluding the native auto-hide clipping strip.</summary>
    /// <param name="clip">Current bounded clipping snapshot; null means unknown legacy state.</param>
    /// <param name="bounds">Window dimensions in physical pixels; desktop origin is ignored.</param>
    /// <returns>True for an absent region or one rectangle covering the entire window; false for unknown or restrictive clipping.</returns>
    internal static bool FullSurface(TaskbarClipState? clip, TaskbarBounds bounds) => clip != null && (!clip.HasRegion
        || clip.Rectangles is [{ } rect] && rect.X <= 0 && rect.Y <= 0 && (long)rect.X + rect.Width >= bounds.Width && (long)rect.Y + rect.Height >= bounds.Height);

    /// <summary>Recognizes only the one- or two-pixel clipping strip at a hidden tray's exposed edge.</summary>
    /// <param name="clip">Captured window-local region; null or unrestricted regions never qualify.</param>
    /// <param name="bounds">Tray dimensions in physical pixels; desktop origin is ignored.</param>
    /// <param name="edge">Docking edge: left 0, top 1, right 2 or bottom 3.</param>
    /// <returns>True only for one exact full-length auto-hide strip; false for custom, empty or partial regions.</returns>
    internal static bool AutoHideStrip(TaskbarClipState? clip, TaskbarBounds bounds, int edge)
    {
        if (clip is not { HasRegion: true, Rectangles: [{ } rect] }) return false;
        return edge switch
        {
            0 => rect is { Y: 0, Width: >= 1 and <= 2 } && rect.X == bounds.Width - rect.Width && rect.Height == bounds.Height,
            1 => rect is { X: 0, Height: >= 1 and <= 2 } && rect.Y == bounds.Height - rect.Height && rect.Width == bounds.Width,
            2 => rect is { X: 0, Y: 0, Width: >= 1 and <= 2 } && rect.Height == bounds.Height,
            3 => rect is { X: 0, Y: 0, Height: >= 1 and <= 2 } && rect.Width == bounds.Width,
            _ => false
        };
    }

    /// <summary>Compares detached region geometry, preserving the distinction between unknown, empty and unrestricted regions.</summary>
    /// <param name="first">First snapshot, or null for legacy unknown clipping.</param>
    /// <param name="second">Second snapshot, or null for legacy unknown clipping.</param>
    /// <returns>True only for matching presence and ordered native rectangles, or two null snapshots.</returns>
    internal static bool Same(TaskbarClipState? first, TaskbarClipState? second) => first == null ? second == null
        : second != null && first.HasRegion == second.HasRegion && first.Rectangles.SequenceEqual(second.Rectangles);

    /// <summary>Sets an already validated, owned clipping effect and transfers the allocated region to Windows on success.</summary>
    /// <param name="window">Revalidated Explorer tray covered by a persisted ready-watchdog lease, or an owned verification window.</param>
    /// <param name="clip">Detached bounded snapshot; HasRegion=false removes explicit clipping.</param>
    /// <exception cref="InvalidDataException">The snapshot is outside the bounded recovery contract.</exception>
    /// <exception cref="Win32Exception">A native allocation, region combination or assignment fails.</exception>
    internal static void Write(nint window, TaskbarClipState clip)
    {
        if (!clip.IsValid()) throw new InvalidDataException("Invalid taskbar clipping state.");
        nint region = 0;
        try
        {
            if (clip.HasRegion)
            {
                region = CreateRectRgn(0, 0, 0, 0);
                if (region == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                foreach (var rect in clip.Rectangles)
                {
                    var part = CreateRectRgn(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);
                    if (part == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                    try { if (CombineRgn(region, region, part, 2) == 0) throw new Win32Exception(Marshal.GetLastWin32Error()); }
                    finally { DeleteObject(part); }
                }
            }
            if (SetWindowRgn(window, region, true) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            region = 0; // SetWindowRgn takes ownership; it must not be deleted here.
        }
        finally { if (region != 0) DeleteObject(region); }
    }

    /// <summary>Copies a window-local clipping region into managed recovery data.</summary>
    /// <param name="window">Revalidated Explorer taskbar, or a verification window owned by this process.</param>
    /// <returns>A detached region snapshot; HasRegion is false for the default rectangular surface.</returns>
    /// <exception cref="IOException">The native region cannot be represented by at most 256 bounded rectangles.</exception>
    /// <exception cref="Win32Exception">A native region resource cannot be allocated or read.</exception>
    internal static TaskbarClipState Read(nint window)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var kind = GetWindowRgn(window, region);
            if (kind == 0) return new(false, []); // A window without an explicit region uses its normal rectangle.
            var size = GetRegionData(region, 0, null);
            if (size is < 32 or > 4128) throw new IOException("Taskbar clipping region exceeds the recovery bound.");
            var data = new byte[size];
            if (GetRegionData(region, size, data) != size) throw new Win32Exception(Marshal.GetLastWin32Error());
            var count = BitConverter.ToInt32(data, 8);
            if (BitConverter.ToInt32(data, 0) != 32 || BitConverter.ToInt32(data, 4) != 1
                || count is < 0 or > 256 || 32 + count * 16 != data.Length)
                throw new IOException("Invalid taskbar clipping region.");
            var rectangles = new TaskbarBounds[count];
            for (var i = 0; i < count; i++)
            {
                var offset = 32 + i * 16;
                var x = BitConverter.ToInt32(data, offset); var y = BitConverter.ToInt32(data, offset + 4);
                rectangles[i] = new(x, y, checked(BitConverter.ToInt32(data, offset + 8) - x), checked(BitConverter.ToInt32(data, offset + 12) - y));
            }
            var result = new TaskbarClipState(true, rectangles);
            if (!result.IsValid()) throw new IOException("Invalid taskbar clipping geometry.");
            return result;
        }
        finally { DeleteObject(region); }
    }

    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern uint GetRegionData(nint region, uint size, [Out] byte[]? data);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int CombineRgn(nint destination, nint first, nint second, int mode);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint resource);
}
