using System.Diagnostics;
using TileWall.Core.Shell;
using TileWall.Shell.Interop;

namespace TileWall.Views;

/// <summary>Preserves the native redirected surface's alpha beneath the XAML/Composition content.</summary>
internal sealed class NativeBackdropSurface : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Win32Api.SubclassProc _callback;
    private IntPtr _backgroundBrush;
    private bool _attached;
    private readonly bool _measureErase = Environment.GetEnvironmentVariable("TILEWALL_PERF") == "1";
    private readonly Action<long>? _eraseTiming;
    private readonly Action<uint, long>? _messageTiming;

    internal NativeBackdropSurface(IntPtr hwnd, Action<long>? eraseTiming = null, Action<uint, long>? messageTiming = null)
    {
        _hwnd = hwnd;
        _eraseTiming = eraseTiming;
        _messageTiming = messageTiming;
        _callback = WindowMessage;
        _backgroundBrush = Win32Api.CreateSolidBrush(0);
        _attached = Win32Api.SetWindowSubclass(hwnd, _callback, 1, 0);
        ConfigureDwm();
        var dc = Win32Api.GetDC(hwnd);
        if (dc == IntPtr.Zero) return;
        try { ClearBackground(dc); }
        finally { Win32Api.ReleaseDC(hwnd, dc); }
    }

    private void ConfigureDwm()
    {
        // This is alpha-surface preparation, not the active material's blur effect.
        // An empty off-window region keeps the unblurred CompositionColorBrush unblurred.
        var margins = new Win32Api.DwmMargins();
        var frameResult = Win32Api.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        var region = Win32Api.CreateRectRgn(-2, -2, -1, -1);
        try
        {
            var blur = new Win32Api.DwmBlurBehind { Flags = 3, Enable = true, Region = region };
            var blurResult = Win32Api.DwmEnableBlurBehindWindow(_hwnd, ref blur);
            if (frameResult < 0 || blurResult < 0)
                AppLog.Warn("WallBackdrop", $"Native alpha setup frame=0x{frameResult:X8} blur=0x{blurResult:X8}");
        }
        finally { if (region != IntPtr.Zero) Win32Api.DeleteObject(region); }
    }

    private bool ClearBackground(IntPtr dc)
    {
        if (_backgroundBrush == IntPtr.Zero || !Win32Api.GetClientRect(_hwnd, out var rect)) return false;
        return Win32Api.FillRect(dc, ref rect, _backgroundBrush) != 0;
    }

    private IntPtr WindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        // Black GDI clearing preserves zero alpha rather than letting an opaque class brush
        // erase the surface when the window is resized, activated, or exposed.
        if (message == 0x0014) // WM_ERASEBKGND
        {
            var started = _measureErase ? Stopwatch.GetTimestamp() : 0;
            try { if (ClearBackground(wParam)) return new IntPtr(1); }
            finally { if (started != 0) _eraseTiming?.Invoke(Stopwatch.GetTimestamp() - started); }
        }
        if (message == 0x031E) ConfigureDwm(); // WM_DWMCOMPOSITIONCHANGED
        if (message == 0x0082) Dispose(); // WM_NCDESTROY
        if (_measureErase && message is 0x0003 or 0x0005 or 0x0046 or 0x0047 or 0x0083)
        {
            // WM_MOVE / WM_SIZE / WM_WINDOWPOSCHANGING / WM_WINDOWPOSCHANGED / WM_NCCALCSIZE.
            var started = Stopwatch.GetTimestamp();
            try { return Win32Api.DefSubclassProc(hwnd, message, wParam, lParam); }
            finally { _messageTiming?.Invoke(message, Stopwatch.GetTimestamp() - started); }
        }
        return Win32Api.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_attached)
        {
            Win32Api.RemoveWindowSubclass(_hwnd, _callback, 1);
            _attached = false;
        }
        if (_backgroundBrush == IntPtr.Zero) return;
        Win32Api.DeleteObject(_backgroundBrush);
        _backgroundBrush = IntPtr.Zero;
    }
}
