using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32.SafeHandles;
using TileWall.Core.Shell;
using TileWall.Shell.Interop;
using Windows.Graphics;

namespace TileWall.Graphics;

/// <summary>Physical desktop captures whose pixels stay in process memory.</summary>
internal static class DesktopBackdropSnapshot
{
    internal sealed record CapturedFrame(DecodedImageFrame Pixels, int Left, int Top, int Width, int Height,
        int ContentWidth, int ContentHeight, double SigmaX, double SigmaY);
    internal readonly record struct CaptureRequest(RectInt32 Region, double Scale);
    internal enum CaptureFailure { Geometry, Allocation, Read, Crop, Presentation }
    private readonly record struct CapturePlan(RectInt32 Bounds, RectInt32 Content, double Sigma);

    internal static CapturedFrame? Capture(RectInt32 region, double rasterizationScale)
    {
        var request = new CaptureRequest(region, rasterizationScale);
        if (VirtualBounds() is not { } desktop ||
            Plan(request, desktop) is not { } plan ||
            ReadDesktop(plan.Bounds, out var capture) is not null || capture is null) return null;
        using (capture)
            return capture.Crop(request, out var frame) is null ? frame : null;
    }

    internal static CaptureFailure? CaptureDesktop(out DesktopCapture? capture)
    {
        capture = null;
        if (VirtualBounds() is not { } desktop) return CaptureFailure.Geometry;
        var failure = ReadDesktop(desktop, out capture);
        if (failure is null && BackdropTrace.Enabled)
            BackdropTrace.Write($"VirtualDesktop captured width={desktop.Width} height={desktop.Height}");
        return failure;
    }

    internal sealed class DesktopCapture : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly RectInt32 _bounds;
        private readonly IntPtr _pixels;

        internal DesktopCapture(RectInt32 bounds, IntPtr bitmap, IntPtr pixels) : base(ownsHandle: true)
        {
            _bounds = bounds;
            _pixels = pixels;
            SetHandle(bitmap);
        }

        internal CaptureFailure? Crop(CaptureRequest request, out CapturedFrame? frame)
        {
            frame = null;
            if (Plan(request, _bounds) is not { } plan) return CaptureFailure.Geometry;
            var referenced = false;
            try
            {
                DangerousAddRef(ref referenced);
                frame = MakeFrame(plan, CopyPixels(plan.Bounds, _bounds, _pixels));
                return null;
            }
            catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or OverflowException or ObjectDisposedException)
            {
                return CaptureFailure.Crop;
            }
            finally { if (referenced) DangerousRelease(); }
        }

        protected override bool ReleaseHandle() => DeleteObject(handle);
    }

    private static RectInt32? VirtualBounds()
    {
        var x = Win32Api.GetSystemMetrics(76); // SM_XVIRTUALSCREEN
        var y = Win32Api.GetSystemMetrics(77); // SM_YVIRTUALSCREEN
        var width = Win32Api.GetSystemMetrics(78);
        var height = Win32Api.GetSystemMetrics(79);
        return width > 0 && height > 0 && (long)x + width <= int.MaxValue && (long)y + height <= int.MaxValue
            ? new RectInt32(x, y, width, height) : null;
    }

    private static CapturePlan? Plan(CaptureRequest request, RectInt32 desktop)
    {
        var (region, rasterizationScale) = request;
        if (region.Width <= 0 || region.Height <= 0 || region.Width > 20000 || region.Height > 20000 ||
            !double.IsFinite(rasterizationScale) || rasterizationScale <= 0 || rasterizationScale > 8) return null;
        // Sample the blur's neighborhood before reducing/cropping. At the physical
        // desktop edge, the bounded Gaussian extends that edge rather than adding black.
        var sigma = AcrylicSnapshotRecipe.BlurSigmaDip * rasterizationScale;
        var padding = (int)Math.Ceiling(3 * sigma) + 2;
        var left = (int)Math.Max(desktop.X, (long)region.X - padding);
        var top = (int)Math.Max(desktop.Y, (long)region.Y - padding);
        var right = (int)Math.Min((long)desktop.X + desktop.Width, (long)region.X + region.Width + padding);
        var bottom = (int)Math.Min((long)desktop.Y + desktop.Height, (long)region.Y + region.Height + padding);
        if (left > region.X || top > region.Y || right < (long)region.X + region.Width ||
            bottom < (long)region.Y + region.Height) return null;
        var captureWidth = right - left;
        var captureHeight = bottom - top;
        if (captureWidth <= 0 || captureHeight <= 0) return null;
        if ((long)captureWidth * captureHeight > 64L * 1024 * 1024) return null;
        return new(new RectInt32(left, top, captureWidth, captureHeight), region, sigma);
    }

    private static CaptureFailure? ReadDesktop(RectInt32 bounds, out DesktopCapture? capture)
    {
        capture = null;
        var width = bounds.Width;
        var height = bounds.Height;
        // An uncompressed DIB can leave SizeImage at zero. Check the address
        // span separately from the per-display managed allocation.
        if ((ulong)width * (ulong)height * 4 > (ulong)nint.MaxValue) return CaptureFailure.Geometry;
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return CaptureFailure.Read;
        IntPtr memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero) return CaptureFailure.Allocation;
            var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = width, Height = -height,
                Planes = 1, BitCount = 32, SizeImage = 0 };
            bitmap = CreateDIBSection(screen, ref info, 0, out var pixels, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || pixels == IntPtr.Zero) return CaptureFailure.Allocation;
            previous = SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) return CaptureFailure.Allocation;
            const uint sourceCopy = 0x00CC0020; // SRCCOPY
            var readStarted = Stopwatch.GetTimestamp();
            if (!BitBlt(memory, 0, 0, width, height, screen, bounds.X, bounds.Y, sourceCopy) ||
                !GdiFlush())
                return CaptureFailure.Read;
            var readElapsed = Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds;
            if (readElapsed >= 500)
                AppLog.Warn("BackdropSnapshot", $"Slow desktop read: width={width} height={height} elapsedMs={readElapsed:F0}");
            // Release both DCs on this thread while retaining the DIB for later crops.
            capture = new DesktopCapture(bounds, bitmap, pixels);
            bitmap = IntPtr.Zero;
            return null;
        }
        catch (OutOfMemoryException) { return CaptureFailure.Allocation; }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static byte[] CopyPixels(RectInt32 bounds, RectInt32 source, IntPtr pixels)
    {
        var stride = checked(bounds.Width * 4);
        var bytes = new byte[checked(stride * bounds.Height)];
        if (bounds == source) Marshal.Copy(pixels, bytes, 0, bytes.Length);
        else
        {
            var x = (long)bounds.X - source.X;
            var y = (long)bounds.Y - source.Y;
            var sourceStride = (long)source.Width * 4;
            for (var row = 0; row < bounds.Height; row++)
            {
                var offset = checked((y + row) * sourceStride + x * 4);
                Marshal.Copy(checked(pixels + (nint)offset), bytes, row * stride, stride);
            }
        }
        return bytes;
    }

    private static CapturedFrame MakeFrame(CapturePlan plan, byte[] bytes)
    {
        var bounds = plan.Bounds;
        for (var index = 3; index < bytes.Length; index += 4) bytes[index] = 255;
        var frame = new DecodedImageFrame("memory:desktop", bytes.Length, DateTime.UtcNow,
            bounds.Width, bounds.Height, bounds.Width, bounds.Height, bytes);
        var region = plan.Content;
        return new(frame, region.X - bounds.X, region.Y - bounds.Y, region.Width, region.Height,
            region.Width, region.Height, plan.Sigma, plan.Sigma);
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
        var opacity = Math.Clamp(recipe.EffectiveOpacity, 0, 1);
        var pixels = new byte[checked(captured.Width * captured.Height * 4)];
        // Scale M in S's padded coordinate space, writing only the crop directly into F.
        ImageFrameDecoder.CopyResizedPixelCrop(blurred with { BgraPremultiplied = materialPixels },
            captured.Pixels.Width, captured.Pixels.Height, captured.Left, captured.Top,
            captured.Width, captured.Height, pixels, token);
        var sharpPixels = captured.Pixels.BgraPremultiplied.Span;
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
                    pixels[destination + channel] = ToByte(opacity * pixels[destination + channel] +
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
    [DllImport("gdi32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    [DllImport("gdi32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
}

/// <summary>Optional transition markers using the existing buffered application log.</summary>
internal static class BackdropTrace
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("TILEWALL_TRACE_BACKDROP") == "1";

    internal static void Write(string message)
    {
        if (Enabled)
            AppLog.Warn("BackdropTransition", $"qpc={Stopwatch.GetTimestamp()} tid={Environment.CurrentManagedThreadId} {message}");
    }
}
