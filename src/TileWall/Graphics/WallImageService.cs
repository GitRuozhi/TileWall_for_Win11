using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TileWall.Core.Entries;
using TileWall.Shell.Interop;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TileWall.Graphics;

public enum WallImageStatus { None, Loaded, Fallback, Missing, Invalid, Cancelled }

public sealed record WallImageResult(ImageSource? Source, WallImageStatus Status, string? Diagnostic = null);

/// <summary>
/// Bounded read-only image resolution for a tile. Call from the XAML thread: file, shell,
/// PE and WIC work runs on a worker, then WriteableBitmap is created back on the caller thread.
/// A small decoded-pixel cache lives only in memory; cacheRoot is never written.
/// </summary>
public sealed partial class WallImageService : IDisposable
{
    private const long CacheLimitBytes = 16L * 1024 * 1024;
    private readonly ILnkFileService _links;
    private readonly ImageFrameDecoder _decoder = new();
    private readonly PeIconResourceReader _peReader = new();
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, LinkedListNode<CacheItem>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheItem> _lru = new();
    private long _cacheBytes;
    private bool _disposed;

    public WallImageService(ILnkFileService links)
    {
        _links = links ?? throw new ArgumentNullException(nameof(links));
    }

    public Task<WallImageResult> LoadBackgroundAsync(
        string? imagePath, int widthPixels, int heightPixels, CancellationToken ct = default) =>
        LoadAsync(() => PrepareFileAsync(imagePath, "background", widthPixels, heightPixels, ct),
            widthPixels, heightPixels, ct);

    private async Task<WallImageResult> LoadAsync(
        Func<Task<Prepared>> prepare, int widthPixels, int heightPixels, CancellationToken ct)
    {
        if (widthPixels <= 0 || heightPixels <= 0 || widthPixels > 4096 || heightPixels > 4096)
            throw new ArgumentOutOfRangeException(nameof(widthPixels), "Physical dimensions must be 1..4096.");
        if (_disposed)
            throw new ObjectDisposedException(nameof(WallImageService));
        if (ct.IsCancellationRequested)
            return new(null, WallImageStatus.Cancelled);

        try
        {
            await _slots.WaitAsync(ct);
            Prepared prepared;
            try
            {
                prepared = await Task.Run(prepare, ct);
            }
            finally
            {
                _slots.Release();
            }
            if (ct.IsCancellationRequested)
                return new(null, WallImageStatus.Cancelled);
            if (prepared.Pixels is null)
                return new(null, prepared.Status, prepared.Diagnostic);

            // This continuation is intentionally on the caller's XAML thread.
            var bitmap = new WriteableBitmap(prepared.Width, prepared.Height);
            using (var stream = bitmap.PixelBuffer.AsStream())
                stream.Write(prepared.Pixels, 0, prepared.Pixels.Length);
            bitmap.Invalidate();
            return new(bitmap, prepared.Status, prepared.Diagnostic);
        }
        catch (OperationCanceledException)
        {
            return new(null, WallImageStatus.Cancelled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or
            ArgumentException or InvalidOperationException or OverflowException)
        {
            return new(null, WallImageStatus.Invalid, ex.GetType().Name);
        }
    }

    private async Task<Prepared> PrepareAutoAsync(string? entryOrTargetPath, int width, int height, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(entryOrTargetPath))
            return new(WallImageStatus.None, null, 0, 0, null);
        if (Uri.TryCreate(entryOrTargetPath, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var web = ShellIconPixels.Read(".url", association: true);
            return web is { } icon
                ? new(WallImageStatus.Fallback, icon.Pixels, icon.Width, icon.Height, "WebAssociation")
                : new(WallImageStatus.Fallback, null, 0, 0, "WebEntry");
        }

        var entry = NormalizePath(entryOrTargetPath);
        if (entry is null)
            return new(WallImageStatus.Invalid, null, 0, 0, "InvalidEntryPath");
        var extension = Path.GetExtension(entry);
        string? target = entry;
        string? iconPath = null;
        int? iconIndex = null;
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var fields = _links.Read(entry);
                target = NormalizePath(fields.TargetPath);
                iconPath = fields.IconPath;
                iconIndex = fields.IconIndex;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or
                ArgumentException or InvalidOperationException)
            {
                target = null;
            }
        }
        else if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            target = null;
            iconPath = ReadUrlIconPath(entry);
        }

        if (target is not null && Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var direct = await PreparePeIconAsync(target, iconPath, iconIndex, entry, width, height, ct);
            if (direct.Pixels is not null || direct.Status == WallImageStatus.Cancelled)
                return direct;
        }

        // Shell resolves shortcut IconLocation, file association and folder icons without
        // executing their targets. An absent/unusable icon leaves a generic UI placeholder.
        var shellPath = File.Exists(entry) || Directory.Exists(entry) ? entry :
            iconPath is not null && File.Exists(iconPath) ? iconPath : target;
        if (shellPath is not null && (File.Exists(shellPath) || Directory.Exists(shellPath)))
        {
            var shell = ShellIconPixels.Read(shellPath);
            if (shell is not null)
                return new(WallImageStatus.Fallback, shell.Value.Pixels, shell.Value.Width, shell.Value.Height, "ShellIcon");
        }
        return new(WallImageStatus.Fallback, null, 0, 0, "GenericPlaceholder");
    }

    private async Task<Prepared> PreparePeIconAsync(string exe, string? iconPath, int? iconIndex,
        string entry, int width, int height, CancellationToken ct)
    {
        var key = Fingerprint(exe, $"pe:{entry}:{Fingerprint(entry, "entry")}:" +
            $"{iconPath}:{iconIndex}:{width}x{height}");
        if (key is not null && TryGetCached(key, out var cached))
            return cached;
        ct.ThrowIfCancellationRequested();
        var read = _peReader.Read(exe);
        if (read.Catalog is null)
            return new(WallImageStatus.Fallback, null, 0, 0, $"PE:{read.Status}");
        var group = read.Catalog.SelectGroup(iconPath, iconIndex);
        var frame = group is null ? null : PeIconResourceReader.SelectFrame(group, Math.Max(width, height));
        if (frame is null)
            return new(WallImageStatus.Fallback, null, 0, 0, "PE:NoFrame");
        try
        {
            var ico = PeIconResourceReader.WrapSingleFrameIco(frame);
            using var stream = new InMemoryRandomAccessStream();
            using var writer = new DataWriter(stream);
            writer.WriteBytes(ico);
            await writer.StoreAsync();
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
            ct.ThrowIfCancellationRequested();
            var pixels = data.DetachPixelData();
            var decodedWidth = checked((int)decoder.PixelWidth);
            var decodedHeight = checked((int)decoder.PixelHeight);
            if (decodedWidth != frame.Width || decodedHeight != frame.Height ||
                pixels.Length != decodedWidth * decodedHeight * 4)
                return new(WallImageStatus.Fallback, null, 0, 0, "PE:InvalidFramePixels");
            var result = new Prepared(WallImageStatus.Loaded, pixels, decodedWidth, decodedHeight,
                $"PE:{group!.ResourceId}:{frame.ResourceId}:{frame.Width}x{frame.Height}");
            if (key is not null) Cache(key, result);
            return result;
        }
        catch (Exception ex) when (ex is COMException or IOException or ArgumentException or InvalidOperationException)
        {
            return new(WallImageStatus.Fallback, null, 0, 0, $"PE:{ex.GetType().Name}");
        }
    }

    private async Task<Prepared> PrepareFileAsync(string? path, string semantic, int width, int height,
        CancellationToken ct)
    {
        var canonicalPath = NormalizeImageFilePath(path);
        if (canonicalPath is null)
            return new(WallImageStatus.Missing, null, 0, 0, "MissingFile");
        var key = Fingerprint(canonicalPath, $"{semantic}:{width}x{height}");
        if (key is not null && TryGetCached(key, out var cached))
            return cached;
        var decoded = await _decoder.DecodeAsync(canonicalPath, width, height, ct);
        if (decoded.Frame is null)
            return new(decoded.Status switch
            {
                ImageDecodeStatus.Cancelled => WallImageStatus.Cancelled,
                ImageDecodeStatus.MissingFile => WallImageStatus.Missing,
                _ => WallImageStatus.Invalid,
            }, null, 0, 0, decoded.ErrorType ?? decoded.Status.ToString());
        var result = new Prepared(WallImageStatus.Loaded, decoded.Frame.BgraPremultiplied.ToArray(),
            decoded.Frame.Width, decoded.Frame.Height, null);
        if (key is not null) Cache(key, result);
        return result;
    }

    private static string? Fingerprint(string? path, string semantic)
    {
        var full = NormalizePath(path);
        if (full is null) return null;
        try
        {
            var info = new FileInfo(full);
            return info.Exists ? $"{semantic}|{full}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            return Path.IsPathFullyQualified(expanded) ? Path.GetFullPath(expanded) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? NormalizeImageFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }

    private static string? ReadUrlIconPath(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 64 * 1024) return null;
            foreach (var line in File.ReadLines(path))
            {
                if (!line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line["IconFile=".Length..].Trim();
                var comma = value.LastIndexOf(',');
                return NormalizePath(comma >= 0 ? value[..comma] : value);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    private bool TryGetCached(string key, out Prepared prepared)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                if (node.Value.Value is Prepared cached)
                {
                    prepared = cached;
                    return true;
                }
            }
        }
        prepared = null!;
        return false;
    }

    private void Cache(string key, Prepared frame)
    {
        if (frame.Pixels is null || frame.Pixels.LongLength > CacheLimitBytes / 2) return;
        lock (_cacheLock)
        {
            if (_disposed || _cache.ContainsKey(key)) return;
            var item = new CacheItem(key, frame, frame.Pixels.LongLength);
            var node = _lru.AddFirst(item);
            _cache.Add(key, node);
            _cacheBytes += frame.Pixels.LongLength;
            while (_cacheBytes > CacheLimitBytes && _lru.Last is { } last)
            {
                _cacheBytes -= last.Value.Bytes;
                _cache.Remove(last.Value.Key);
                _lru.RemoveLast();
            }
        }
    }

    public void Dispose()
    {
        lock (_cacheLock)
        {
            _disposed = true;
            _cache.Clear();
            _lru.Clear();
            _cacheBytes = 0;
        }
    }

    private sealed record CacheItem(string Key, object Value, long Bytes);
    private sealed record Prepared(WallImageStatus Status, byte[]? Pixels, int Width, int Height,
        string? Diagnostic = null);

    private static class ShellIconPixels
    {
        public static (byte[] Pixels, int Width, int Height)? Read(string path, bool association = false)
        {
            var info = new Win32Api.ShFileInfoW { szDisplayName = string.Empty, szTypeName = string.Empty };
            var size = (uint)Marshal.SizeOf<Win32Api.ShFileInfoW>();
            const uint useFileAttributes = 0x000000010;
            const uint normalFile = 0x000000080;
            var result = Win32Api.SHGetFileInfoW(path, association ? normalFile : 0, ref info, size,
                Win32Api.ShgfiIcon | Win32Api.ShgfiLargeicon | (association ? useFileAttributes : 0));
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try { return ReadIcon(info.hIcon); }
            finally { _ = Win32Api.DestroyIcon(info.hIcon); }
        }

        private static (byte[] Pixels, int Width, int Height)? ReadIcon(IntPtr icon)
        {
            var info = new Win32Api.IconInfoW();
            if (!Win32Api.GetIconInfo(icon, ref info)) return null;
            try
            {
                var sizeBitmap = info.hbmColor != IntPtr.Zero ? info.hbmColor : info.hbmMask;
                if (sizeBitmap == IntPtr.Zero ||
                    Win32Api.GetObjectW(sizeBitmap, Marshal.SizeOf<Win32Api.BitmapW>(), out var bitmap) == 0)
                    return null;
                var width = bitmap.bmWidth;
                // Monochrome ICONINFO has no color bitmap; its AND and XOR masks
                // are stacked vertically in a bitmap twice the icon's height.
                var height = Math.Abs(bitmap.bmHeight) / (info.hbmColor == IntPtr.Zero ? 2 : 1);
                if (width is < 1 or > 256 || height is < 1 or > 256) return null;
                var onBlack = DrawOnColor(icon, width, height, 0);
                var onWhite = DrawOnColor(icon, width, height, 255);
                if (onBlack is null || onWhite is null) return null;
                var premultiplied = new byte[onBlack.Length];
                for (var i = 0; i < premultiplied.Length; i += 4)
                {
                    // Two composites recover coverage for both AC_SRC_OVER alpha
                    // icons and classic AND-mask icons (DrawIconEx DI_NORMAL).
                    // The black composite is already premultiplied color.
                    var delta = ((int)onWhite[i] - onBlack[i] +
                        onWhite[i + 1] - onBlack[i + 1] +
                        onWhite[i + 2] - onBlack[i + 2]) / 3;
                    var alpha = (byte)Math.Clamp(255 - delta, 0, 255);
                    premultiplied[i] = Math.Min(onBlack[i], alpha);
                    premultiplied[i + 1] = Math.Min(onBlack[i + 1], alpha);
                    premultiplied[i + 2] = Math.Min(onBlack[i + 2], alpha);
                    premultiplied[i + 3] = alpha;
                }
                return (premultiplied, width, height);
            }
            finally
            {
                if (info.hbmColor != IntPtr.Zero) _ = Win32Api.DeleteObject(info.hbmColor);
                if (info.hbmMask != IntPtr.Zero) _ = Win32Api.DeleteObject(info.hbmMask);
            }
        }

        private static byte[]? DrawOnColor(IntPtr icon, int width, int height, byte background)
        {
            var bmi = new Win32Api.BitmapInfoW
            { bmiHeader = Win32Api.BitmapInfoHeaderW.BgraTopDown(width, height) };
            var dc = Win32Api.CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return null;
            try
            {
                var dib = CreateDIBSection(dc, ref bmi, Win32Api.DibRgbColors, out var bits,
                    IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
                try
                {
                    var previous = SelectObject(dc, dib);
                    if (previous == IntPtr.Zero) return null;
                    try
                    {
                        var pixels = new byte[width * height * 4];
                        for (var i = 0; i < pixels.Length; i += 4)
                        {
                            pixels[i] = background;
                            pixels[i + 1] = background;
                            pixels[i + 2] = background;
                            pixels[i + 3] = 255;
                        }
                        Marshal.Copy(pixels, 0, bits, pixels.Length);
                        if (!DrawIconEx(dc, 0, 0, icon, width, height, 0, IntPtr.Zero, 0x0003))
                            return null;
                        Marshal.Copy(bits, pixels, 0, pixels.Length);
                        return pixels;
                    }
                    finally { _ = SelectObject(dc, previous); }
                }
                finally { _ = Win32Api.DeleteObject(dib); }
            }
            finally { _ = Win32Api.DeleteDC(dc); }
        }

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(IntPtr dc, ref Win32Api.BitmapInfoW info,
            uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon,
            int width, int height, uint step, IntPtr brush, uint flags);
    }
}
