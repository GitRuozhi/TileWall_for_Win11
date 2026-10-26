using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using TileWall.Core.Shell;
using TileWall.Shell.Interop;
using Windows.Graphics;

namespace TileWall.Graphics;

/// <summary>One bounded desktop capture; pixels never leave process memory.</summary>
internal static class DesktopBackdropSnapshot
{
    internal static DecodedImageFrame? CaptureExcludingWindow(RectInt32 region, IntPtr window, ref uint? affinityToRestore)
    {
        // A failed earlier restore owns the original value until read-back confirms
        // it. Never reinterpret our remaining exclusion flag as the original policy.
        if (!TryRestoreWindowAffinity(window, ref affinityToRestore) || !IsOwnedCaptureWindow(window)) return null;
        if (!Win32Api.GetWindowDisplayAffinity(window, out var original))
        {
            AppLog.Warn("BackdropSnapshot", $"Read display affinity failed: {Marshal.GetLastWin32Error()}");
            return null;
        }
        // Register before changing policy. Even a failed Set must pass through
        // restoration, because accepting another baseline would lose ownership.
        affinityToRestore = original;
        DecodedImageFrame? captured = null;
        bool restored;
        try
        {
            if (!Win32Api.SetWindowDisplayAffinity(window, Win32Api.WdaExcludeFromCapture))
            {
                AppLog.Warn("BackdropSnapshot", $"Exclude window from capture failed: {Marshal.GetLastWin32Error()}");
                return null;
            }
            captured = Capture(region);
            if (captured is null) AppLog.Warn("BackdropSnapshot", "Desktop capture returned no frame.");
        }
        finally
        {
            // Keep this scope synchronous and short. Blur/bitmap preparation happens
            // only after the original screenshot/recording policy has been restored.
            restored = TryRestoreWindowAffinity(window, ref affinityToRestore);
        }
        return restored ? captured : null;
    }

    internal static bool TryRestoreWindowAffinity(IntPtr window, ref uint? affinityToRestore)
    {
        if (affinityToRestore is not { } original) return true;
        if (!IsOwnedCaptureWindow(window))
        {
            AppLog.Warn("BackdropSnapshot", "Restore display affinity deferred: owned top-level window is unavailable.");
            return false;
        }
        if (!Win32Api.SetWindowDisplayAffinity(window, original))
        {
            AppLog.Warn("BackdropSnapshot", $"Restore display affinity Set failed: {Marshal.GetLastWin32Error()}, expected={original}.");
            return false;
        }
        if (!Win32Api.GetWindowDisplayAffinity(window, out var actual))
        {
            AppLog.Warn("BackdropSnapshot", $"Restore display affinity read-back failed: {Marshal.GetLastWin32Error()}, expected={original}.");
            return false;
        }
        if (actual != original)
        {
            AppLog.Warn("BackdropSnapshot", $"Restore display affinity mismatch: expected={original}, actual={actual}.");
            return false;
        }
        affinityToRestore = null;
        return true;
    }

    private static bool IsOwnedCaptureWindow(IntPtr window) =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && window != IntPtr.Zero &&
        Win32Api.GetWindowThreadProcessId(window, out var owner) != 0 && owner == (uint)Environment.ProcessId &&
        (Win32Api.GetWindowLongPtrW(window, -16).ToInt64() & 0x40000000) == 0; // GWL_STYLE / WS_CHILD

    internal static DecodedImageFrame? Capture(RectInt32 region)
    {
        if (region.Width <= 0 || region.Height <= 0 || region.Width > 20000 || region.Height > 20000) return null;
        var scale = Math.Min(1, 512d / Math.Max(region.Width, region.Height));
        var width = Math.Max(1, (int)Math.Round(region.Width * scale));
        var height = Math.Max(1, (int)Math.Round(region.Height * scale));
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero) return null;
            var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = width, Height = -height,
                Planes = 1, BitCount = 32, SizeImage = (uint)(width * height * 4) };
            bitmap = CreateDIBSection(screen, ref info, 0, out var pixels, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || pixels == IntPtr.Zero) return null;
            previous = SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) return null;
            SetStretchBltMode(memory, 4);
            if (!StretchBlt(memory, 0, 0, width, height, screen, region.X, region.Y, region.Width, region.Height, 0x00CC0020)) return null;
            var bytes = new byte[width * height * 4];
            Marshal.Copy(pixels, bytes, 0, bytes.Length);
            for (var index = 3; index < bytes.Length; index += 4) bytes[index] = 255;
            return new("memory:desktop", bytes.Length, DateTime.UtcNow, region.Width, region.Height, width, height, bytes);
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    internal static DecodedImageFrame BlurAndTint(DecodedImageFrame captured, bool dark)
    {
        var blurred = LowResolutionBlur.Apply(captured, 20);
        var pixels = blurred.BgraPremultiplied.ToArray();
        // Default DesktopAcrylicController values verified against the current runtime.
        // They are a local reference recipe, not a guarantee across Windows versions.
        var tint = dark ? 44d : 252d;
        var luminosityOpacity = dark ? .96 : .85;
        var tintOpacity = dark ? .15 : 0;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            double red = pixels[index + 2], green = pixels[index + 1], blue = pixels[index];
            var luminosityRed = red;
            var luminosityGreen = green;
            var luminosityBlue = blue;
            SetLuminosity(ref luminosityRed, ref luminosityGreen, ref luminosityBlue, tint);
            red += (luminosityRed - red) * luminosityOpacity;
            green += (luminosityGreen - green) * luminosityOpacity;
            blue += (luminosityBlue - blue) * luminosityOpacity;

            // Both verified tints are neutral. Color blending therefore moves each
            // channel towards the adjusted luminance without changing that luminance.
            var luminosity = Luminosity(red, green, blue);
            red += (luminosity - red) * tintOpacity;
            green += (luminosity - green) * tintOpacity;
            blue += (luminosity - blue) * tintOpacity;
            pixels[index + 2] = ToByte(red);
            pixels[index + 1] = ToByte(green);
            pixels[index] = ToByte(blue);
        }
        return blurred with { BgraPremultiplied = pixels };
    }

    // Nonseparable luminosity blending keeps the source's hue/saturation; clipping
    // into gamut must preserve luminance rather than clamp each channel separately.
    private static void SetLuminosity(ref double red, ref double green, ref double blue, double luminosity)
    {
        var delta = luminosity - Luminosity(red, green, blue);
        red += delta;
        green += delta;
        blue += delta;
        var minimum = Math.Min(red, Math.Min(green, blue));
        var maximum = Math.Max(red, Math.Max(green, blue));
        if (minimum < 0)
        {
            var scale = luminosity / (luminosity - minimum);
            red = luminosity + (red - luminosity) * scale;
            green = luminosity + (green - luminosity) * scale;
            blue = luminosity + (blue - luminosity) * scale;
        }
        if (maximum > 255)
        {
            var scale = (255 - luminosity) / (maximum - luminosity);
            red = luminosity + (red - luminosity) * scale;
            green = luminosity + (green - luminosity) * scale;
            blue = luminosity + (blue - luminosity) * scale;
        }
    }

    private static double Luminosity(double red, double green, double blue) => .3 * red + .59 * green + .11 * blue;
    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    internal static WriteableBitmap ToBitmap(DecodedImageFrame frame)
    {
        var bitmap = new WriteableBitmap(frame.Width, frame.Height);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(frame.BgraPremultiplied.Span);
        bitmap.Invalidate();
        return bitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitCount;
        internal uint Compression, SizeImage;
        internal int XPelsPerMeter, YPelsPerMeter;
        internal uint ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr pixels, IntPtr section, uint offset);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StretchBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
}
