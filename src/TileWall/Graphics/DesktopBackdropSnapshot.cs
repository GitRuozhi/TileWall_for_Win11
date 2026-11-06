using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using TileWall.Core.Shell;
using TileWall.Shell.Interop;
using Windows.Graphics;

namespace TileWall.Graphics;

/// <summary>One physical-resolution desktop capture; pixels never leave process memory.</summary>
internal static class DesktopBackdropSnapshot
{
    internal sealed record CapturedFrame(DecodedImageFrame Pixels, int Left, int Top, int Width, int Height,
        int ContentWidth, int ContentHeight, double SigmaX, double SigmaY);

    internal static CapturedFrame? Capture(RectInt32 region, double rasterizationScale)
    {
        if (region.Width <= 0 || region.Height <= 0 || region.Width > 20000 || region.Height > 20000 ||
            !double.IsFinite(rasterizationScale) || rasterizationScale <= 0 || rasterizationScale > 8) return null;
        // Sample the blur's neighborhood before reducing/cropping. At the physical
        // desktop edge, the bounded Gaussian extends that edge rather than adding black.
        var sigma = AcrylicSnapshotRecipe.BlurSigmaDip * rasterizationScale;
        var padding = (int)Math.Ceiling(3 * sigma) + 2;
        var desktopX = Win32Api.GetSystemMetrics(76); // SM_XVIRTUALSCREEN
        var desktopY = Win32Api.GetSystemMetrics(77); // SM_YVIRTUALSCREEN
        var desktopRight = (long)desktopX + Win32Api.GetSystemMetrics(78);
        var desktopBottom = (long)desktopY + Win32Api.GetSystemMetrics(79);
        var left = (int)Math.Max(desktopX, (long)region.X - padding);
        var top = (int)Math.Max(desktopY, (long)region.Y - padding);
        var right = (int)Math.Min(desktopRight, (long)region.X + region.Width + padding);
        var bottom = (int)Math.Min(desktopBottom, (long)region.Y + region.Height + padding);
        if (left > region.X || top > region.Y || right < (long)region.X + region.Width ||
            bottom < (long)region.Y + region.Height) return null;
        var captureWidth = right - left;
        var captureHeight = bottom - top;
        if (captureWidth <= 0 || captureHeight <= 0) return null;
        // S must retain the physical desktop detail. Only the later Gaussian work
        // plane is reduced; both branches come from this single synchronous read.
        if ((long)captureWidth * captureHeight > 64L * 1024 * 1024) return null;
        var width = captureWidth;
        var height = captureHeight;
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
            if (!StretchBlt(memory, 0, 0, width, height, screen, left, top, captureWidth, captureHeight, 0x00CC0020)) return null;
            var bytes = new byte[width * height * 4];
            Marshal.Copy(pixels, bytes, 0, bytes.Length);
            for (var index = 3; index < bytes.Length; index += 4) bytes[index] = 255;
            var pixelsFrame = new DecodedImageFrame("memory:desktop", bytes.Length, DateTime.UtcNow,
                captureWidth, captureHeight, width, height, bytes);
            var cropLeft = region.X - left;
            var cropTop = region.Y - top;
            var cropWidth = region.Width;
            var cropHeight = region.Height;
            return new(pixelsFrame, cropLeft, cropTop, cropWidth, cropHeight, region.Width, region.Height,
                sigma, sigma);
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    internal static DecodedImageFrame BlurAndTint(CapturedFrame captured, AcrylicSnapshotRecipe recipe,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var scale = Math.Min(1, 512d / Math.Max(captured.Pixels.Width, captured.Pixels.Height));
        var workWidth = Math.Max(1, (int)Math.Round(captured.Pixels.Width * scale));
        var workHeight = Math.Max(1, (int)Math.Round(captured.Pixels.Height * scale));
        var work = ImageFrameDecoder.ResizePixels(captured.Pixels, workWidth, workHeight, token);
        var blurred = LowResolutionBlur.ApplySigma(work,
            captured.SigmaX * workWidth / captured.Pixels.Width,
            captured.SigmaY * workHeight / captured.Pixels.Height);
        token.ThrowIfCancellationRequested();
        var materialPixels = blurred.BgraPremultiplied.ToArray();
        var tintLuminosity = Luminosity(recipe.Tint.R, recipe.Tint.G, recipe.Tint.B);
        var luminosityOpacity = Math.Round(Math.Clamp(recipe.LuminosityOpacity, 0, 1) * 255) / 255;
        var tintOpacity = Math.Round(recipe.Tint.A * Math.Clamp(recipe.TintOpacity, 0, 1)) / 255;
        for (var index = 0; index < materialPixels.Length; index += 4)
        {
            double red = materialPixels[index + 2], green = materialPixels[index + 1], blue = materialPixels[index];
            var luminosityRed = red;
            var luminosityGreen = green;
            var luminosityBlue = blue;
            SetLuminosity(ref luminosityRed, ref luminosityGreen, ref luminosityBlue, tintLuminosity);
            red += (luminosityRed - red) * luminosityOpacity;
            green += (luminosityGreen - green) * luminosityOpacity;
            blue += (luminosityBlue - blue) * luminosityOpacity;

            double tintRed = recipe.Tint.R, tintGreen = recipe.Tint.G, tintBlue = recipe.Tint.B;
            SetLuminosity(ref tintRed, ref tintGreen, ref tintBlue, Luminosity(red, green, blue));
            red += (tintRed - red) * tintOpacity;
            green += (tintGreen - green) * tintOpacity;
            blue += (tintBlue - blue) * tintOpacity;
            materialPixels[index + 2] = ToByte(red);
            materialPixels[index + 1] = ToByte(green);
            materialPixels[index] = ToByte(blue);
            materialPixels[index + 3] = 255;
        }
        // Expand M to exactly S's padded coordinate space before cropping either.
        // Interpolation may smooth M; S is never resampled or reconstructed from M.
        var material = ImageFrameDecoder.ResizePixels(blurred with { BgraPremultiplied = materialPixels },
            captured.Pixels.Width, captured.Pixels.Height, token);
        var opacity = Math.Clamp(recipe.EffectiveOpacity, 0, 1);
        var pixels = new byte[checked(captured.Width * captured.Height * 4)];
        var sharpPixels = captured.Pixels.BgraPremultiplied.Span;
        var softenedPixels = material.BgraPremultiplied.Span;
        for (var row = 0; row < captured.Height; row++)
        {
            token.ThrowIfCancellationRequested();
            var sourceOffset = ((row + captured.Top) * captured.Pixels.Width + captured.Left) * 4;
            var destinationOffset = row * captured.Width * 4;
            for (var column = 0; column < captured.Width; column++)
            {
                var source = sourceOffset + column * 4;
                var destination = destinationOffset + column * 4;
                for (var channel = 0; channel < 3; channel++)
                    pixels[destination + channel] = ToByte(opacity * softenedPixels[source + channel] +
                        (1 - opacity) * sharpPixels[source + channel]);
                pixels[destination + 3] = 255;
            }
        }
        return captured.Pixels with { SourceWidth = captured.ContentWidth, SourceHeight = captured.ContentHeight,
            Width = captured.Width, Height = captured.Height, BgraPremultiplied = pixels };
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

/// <summary>Optional transition markers using the existing buffered application log.</summary>
internal static class BackdropTrace
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("TILEWALL_TRACE_BACKDROP") == "1";

    internal static void Write(string message)
    {
        if (Enabled)
            AppLog.Warn("BackdropTransition", $"qpc={System.Diagnostics.Stopwatch.GetTimestamp()} tid={Environment.CurrentManagedThreadId} {message}");
    }
}
