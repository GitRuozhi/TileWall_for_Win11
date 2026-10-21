using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace TileWall.Graphics;

/// <summary>One bounded capture while the wall is hidden; pixels never leave process memory.</summary>
internal static class DesktopBackdropSnapshot
{
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
        var tint = dark ? 32 : 243;
        for (var index = 0; index < pixels.Length; index += 4)
            for (var channel = 0; channel < 3; channel++)
                pixels[index + channel] = (byte)Math.Round(pixels[index + channel] * .55 + tint * .45);
        return blurred with { BgraPremultiplied = pixels };
    }

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
