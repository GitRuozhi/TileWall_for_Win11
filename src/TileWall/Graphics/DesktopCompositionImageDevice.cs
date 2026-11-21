using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;

namespace TileWall.Graphics;

/// <summary>Uploads premultiplied BGRA frames for the desktop compositor.</summary>
internal sealed class DesktopCompositionImageDevice : IDisposable
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private const DirectXAlphaMode AlphaMode = DirectXAlphaMode.Premultiplied;
    private const float PixelDpi = 96f;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11SdkVersion = 7;
    private const uint NearestNeighbor = 0;

    private static readonly Guid DxgiDeviceId = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid CompositorInteropId = new("25297D5C-3AD4-4C9C-B5CF-E36A38512330");
    private static readonly Guid SurfaceInteropId = new("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE");
    private static readonly Guid D2DDeviceContextId = new("E8F7FE7A-191C-466D-AD95-975678BDA998");

    private CompositionGraphicsDevice? _device;

    internal DesktopCompositionImageDevice(Compositor compositor)
    {
        try { _device = OpenGraphicsDevice(compositor, D3DDriverType.Hardware); }
        catch (COMException) { _device = OpenGraphicsDevice(compositor, D3DDriverType.Warp); }
    }

    /// <summary>Creates a surface owned by the caller.</summary>
    internal CompositionDrawingSurface CreateSurface(DecodedImageFrame frame)
    {
        var device = _device ?? throw new ObjectDisposedException(nameof(DesktopCompositionImageDevice));
        var stride = (long)frame.Width * 4;
        if (frame.Width < 1 || frame.Height < 1 || stride > int.MaxValue ||
            stride * frame.Height != frame.BgraPremultiplied.Length)
            throw new ArgumentException("Expected a tightly packed premultiplied BGRA frame.", nameof(frame));

        var surface = device.CreateDrawingSurface(new Size(frame.Width, frame.Height), PixelFormat, AlphaMode);
        try
        {
            Upload(surface, frame, (uint)stride);
            return surface;
        }
        catch
        {
            surface.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        var device = _device;
        _device = null;
        device?.Dispose();
    }

    private static CompositionGraphicsDevice OpenGraphicsDevice(Compositor compositor, D3DDriverType driverType)
    {
        nint d3dDevice = 0, dxgiDevice = 0, d2dDevice = 0, compositorAbi = 0, interop = 0, graphicsDevice = 0;
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, driverType, 0, D3D11CreateDeviceBgraSupport,
                0, 0, D3D11SdkVersion, out d3dDevice, 0, 0));
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, in DxgiDeviceId, out dxgiDevice));
            Marshal.ThrowExceptionForHR(D2D1CreateDevice(dxgiDevice, 0, out d2dDevice));

            compositorAbi = WinRT.MarshalInspectable<Compositor>.FromManaged(compositor);
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(compositorAbi, in CompositorInteropId, out interop));
            Marshal.ThrowExceptionForHR(Method<CreateGraphicsDevice>(interop, 5)(interop, d2dDevice, out graphicsDevice));
            return CompositionGraphicsDevice.FromAbi(graphicsDevice);
        }
        finally
        {
            if (graphicsDevice != 0) Marshal.Release(graphicsDevice);
            if (interop != 0) Marshal.Release(interop);
            if (compositorAbi != 0) Marshal.Release(compositorAbi);
            if (d2dDevice != 0) Marshal.Release(d2dDevice);
            if (dxgiDevice != 0) Marshal.Release(dxgiDevice);
            if (d3dDevice != 0) Marshal.Release(d3dDevice);
        }
    }

    private static void Upload(CompositionDrawingSurface surface, DecodedImageFrame frame, uint stride)
    {
        var surfaceAbi = WinRT.MarshalInspectable<CompositionDrawingSurface>.FromManaged(surface);
        nint interop = 0, context = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfaceAbi, in SurfaceInteropId, out interop));
            var beginDraw = Method<BeginDraw>(interop, 3);
            var endDraw = Method<EndDraw>(interop, 4);
            // A null update rectangle requests a repaint of the whole surface.
            Marshal.ThrowExceptionForHR(beginDraw(interop, 0, in D2DDeviceContextId, out context, out var offset));
            var drawn = false;
            try
            {
                DrawFrame(context, frame, stride, offset);
                drawn = true;
            }
            finally
            {
                Marshal.Release(context);
                context = 0;
                var result = endDraw(interop);
                if (drawn) Marshal.ThrowExceptionForHR(result);
            }
        }
        finally
        {
            if (context != 0) Marshal.Release(context);
            if (interop != 0) Marshal.Release(interop);
            Marshal.Release(surfaceAbi);
        }
    }

    private static void DrawFrame(nint context, DecodedImageFrame frame, uint stride, Point offset)
    {
        if (!MemoryMarshal.TryGetArray(frame.BgraPremultiplied, out var pixels))
            pixels = new(frame.BgraPremultiplied.ToArray());
        var pin = GCHandle.Alloc(pixels.Array!, GCHandleType.Pinned);
        nint bitmap = 0;
        try
        {
            var size = new BitmapSize((uint)frame.Width, (uint)frame.Height);
            var properties = new BitmapProperties((uint)PixelFormat, (uint)AlphaMode, PixelDpi, PixelDpi);
            Marshal.ThrowExceptionForHR(Method<CreateBitmap>(context, 4)(context, size,
                pin.AddrOfPinnedObject() + pixels.Offset, stride, in properties, out bitmap));

            // BeginDraw can place the update inside a larger surface. Use its pixel offset.
            var identity = Matrix3x2.Identity;
            Method<SetDpi>(context, 51)(context, PixelDpi, PixelDpi);
            Method<SetTransform>(context, 30)(context, in identity);
            Method<Clear>(context, 47)(context, 0);
            var bounds = new BitmapRect(offset.X, offset.Y, offset.X + (float)frame.Width, offset.Y + (float)frame.Height);
            Method<DrawBitmap>(context, 26)(context, bitmap, in bounds, 1f, NearestNeighbor, 0);
        }
        finally
        {
            if (bitmap != 0) Marshal.Release(bitmap);
            pin.Free();
        }
    }

    // Slot order follows windows.ui.composition.interop.h and ID2D1RenderTarget in d2d1.h.
    private static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    private enum D3DDriverType : uint { Hardware = 1, Warp = 5 }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Point(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct BitmapSize(uint Width, uint Height);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct BitmapProperties(uint Format, uint Alpha, float DpiX, float DpiY);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct BitmapRect(float Left, float Top, float Right, float Bottom);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, D3DDriverType driverType, nint software,
        uint flags, nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device,
        nint featureLevel, nint immediateContext);

    [DllImport("d2d1.dll", ExactSpelling = true)]
    private static extern int D2D1CreateDevice(nint dxgiDevice, nint creationProperties, out nint d2dDevice);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateGraphicsDevice(nint instance, nint d2dDevice, out nint graphicsDevice);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int BeginDraw(nint instance, nint updateRect, in Guid iid, out nint context, out Point offset);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EndDraw(nint instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateBitmap(nint instance, BitmapSize size, nint pixels, uint stride,
        in BitmapProperties properties, out nint bitmap);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetDpi(nint instance, float dpiX, float dpiY);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetTransform(nint instance, in Matrix3x2 transform);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Clear(nint instance, nint color);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawBitmap(nint instance, nint bitmap, in BitmapRect bounds, float opacity,
        uint interpolation, nint sourceRect);
}
