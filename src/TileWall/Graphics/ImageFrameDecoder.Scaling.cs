using System.Runtime.InteropServices;

namespace TileWall.Graphics;

public sealed partial class ImageFrameDecoder
{
    /// <summary>Scale held premultiplied pixels directly through WIC, without a compressed-image round trip.</summary>
    internal static DecodedImageFrame ResizePixels(DecodedImageFrame source, int width, int height, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (width < 1 || height < 1 || source.Width < 1 || source.Height < 1 ||
            source.BgraPremultiplied.Length != checked(source.Width * source.Height * 4))
            throw new ArgumentOutOfRangeException(nameof(width));
        if (source.Width == width && source.Height == height) return source;
        var output = new byte[checked(width * height * 4)];
        if (!MemoryMarshal.TryGetArray(source.BgraPremultiplied, out var input))
            input = new(source.BgraPremultiplied.ToArray());
        var inputPin = GCHandle.Alloc(input.Array!, GCHandleType.Pinned);
        var outputPin = GCHandle.Alloc(output, GCHandleType.Pinned);
        nint factory = 0, bitmap = 0, scaler = 0;
        var initialized = WicPixels.CoInitializeEx(0, 0);
        try
        {
            // An existing STA is also valid for WIC. Balance only our successful COM initialization.
            if (initialized < 0 && initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(initialized);
            var classId = new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a");
            var interfaceId = new Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
            Marshal.ThrowExceptionForHR(WicPixels.CoCreateInstance(in classId, 0, 1, in interfaceId, out factory));
            var format = new Guid("6fddc324-4e03-4bfe-b185-3d77768dc910"); // GUID_WICPixelFormat32bppPBGRA
            Marshal.ThrowExceptionForHR(WicPixels.Method<WicPixels.CreateBitmapFromMemory>(factory, 20)(factory,
                checked((uint)source.Width), checked((uint)source.Height), in format,
                checked((uint)source.Width * 4), checked((uint)input.Count), inputPin.AddrOfPinnedObject() + input.Offset, out bitmap));
            Marshal.ThrowExceptionForHR(WicPixels.Method<WicPixels.CreateBitmapScaler>(factory, 11)(factory, out scaler));
            Marshal.ThrowExceptionForHR(WicPixels.Method<WicPixels.InitializeScaler>(scaler, 8)(scaler, bitmap,
                checked((uint)width), checked((uint)height), 3)); // WICBitmapInterpolationModeFant
            token.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(WicPixels.Method<WicPixels.CopyPixels>(scaler, 7)(scaler, 0,
                checked((uint)width * 4), checked((uint)output.Length), outputPin.AddrOfPinnedObject()));
            token.ThrowIfCancellationRequested();
            return source with { Width = width, Height = height, BgraPremultiplied = output };
        }
        finally
        {
            if (scaler != 0) Marshal.Release(scaler);
            if (bitmap != 0) Marshal.Release(bitmap);
            if (factory != 0) Marshal.Release(factory);
            if (initialized >= 0) WicPixels.CoUninitialize();
            outputPin.Free(); inputPin.Free();
        }
    }

    // Native WIC is not indexed by this winapp find-api SDK scope. GUIDs, slot
    // order and signatures below were checked against the installed SDK's um/wincodec.h.
    private static class WicPixels
    {
        [DllImport("ole32.dll")] internal static extern int CoInitializeEx(nint reserved, uint flags);
        [DllImport("ole32.dll")] internal static extern void CoUninitialize();
        [DllImport("ole32.dll")] internal static extern int CoCreateInstance(in Guid classId, nint outer,
            uint context, in Guid interfaceId, out nint instance);
        internal static T Method<T>(nint instance, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int CreateBitmapFromMemory(nint factory, uint width, uint height, in Guid format,
            uint stride, uint bytes, nint pixels, out nint bitmap);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int CreateBitmapScaler(nint factory, out nint scaler);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int InitializeScaler(nint scaler, nint source, uint width, uint height, int interpolation);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int CopyPixels(nint source, nint rectangle, uint stride, uint bytes, nint pixels);
    }
}
