using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TileWall.Core.Entries;
using TileWall.Core.Wall;
using TileWall.Shell.Interop;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TileWall.Graphics;

public enum WallImageStatus { None, Loaded, Fallback, Missing, Invalid, Cancelled }

public sealed record WallImageResult(ImageSource? Source, WallImageStatus Status, string? Diagnostic = null);

/// <summary>
/// Read-only image resolution for a tile. Call from the XAML thread: file, shell,
/// PE and WIC work runs on a worker, then WriteableBitmap is created back on the caller thread.
/// Decoding and capacity-managed disposable image/icon caches share verified source identities.
/// </summary>
public sealed partial class WallImageService : IDisposable
{
    private const long CacheLimitBytes = 16L * 1024 * 1024;
    private readonly ILnkFileService _links;
    private readonly ImageFrameDecoder _decoder = new();
    private readonly PeIconResourceReader _peReader = new();
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly SemaphoreSlim _automaticSlots = new(2, 2);
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
        LoadAsync(token => PrepareFileAsync(imagePath, "background", widthPixels, heightPixels, token),
            widthPixels, heightPixels, ct);

    private Task<WallImageResult> LoadAsync(
        Func<CancellationToken, Task<Prepared>> prepare, int widthPixels, int heightPixels, CancellationToken ct) =>
        StartImageRequest(token => LoadCoreAsync(prepare, widthPixels, heightPixels, token), ct,
            new WallImageResult(null, WallImageStatus.Cancelled));

    private async Task<WallImageResult> LoadCoreAsync(
        Func<CancellationToken, Task<Prepared>> prepare, int widthPixels, int heightPixels,
        CancellationToken ct)
    {
        if (widthPixels <= 0 || heightPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(widthPixels), "Physical dimensions must be positive.");
        if (ct.IsCancellationRequested)
            return new(null, WallImageStatus.Cancelled);

        try
        {
            await _slots.WaitAsync(ct);
            Prepared prepared;
            try
            {
                prepared = await Task.Run(() => prepare(ct), ct);
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

    private async Task<Prepared> PrepareAutoCoreAsync(string? entryOrTargetPath, int width, int height, CancellationToken ct,
        string? verifiedPackageTarget = null)
    {
        var candidates = await CollectIconSeriesAsync(entryOrTargetPath, width, height, verifiedPackageTarget,
            true, ct).ConfigureAwait(false);
        var chosen = SelectIconSeries(candidates, width, height);
        if (chosen is null) return new(WallImageStatus.None, null, 0, 0, "IconSeriesUnavailable");
        return chosen.Frames.OrderBy(frame => frame.Width >= width && frame.Height >= height ? 0 : 1)
            .ThenBy(frame => frame.Width >= width && frame.Height >= height ? (long)frame.Width * frame.Height : -(long)frame.Width * frame.Height)
            .First();
    }
    private async Task<Prepared> PrepareFileAsync(string? path, string semantic, int width, int height,
        CancellationToken ct, ManagedMediaReference? media = null)
    {
        ct.ThrowIfCancellationRequested();
        var canonicalPath = NormalizeImageFilePath(path);
        if (canonicalPath is null)
            return media is null ? new(WallImageStatus.Missing, null, 0, 0, "MissingFile")
                : new(WallImageStatus.Invalid, null, 0, 0, "InvalidManagedMediaReference");
        using var ownership = media is null ? null : _formalResourceLease?.Invoke(null, media);
        var verified = media is null ? default : OpenVerifiedMedia(canonicalPath, media);
        using var mediaRead = verified.Lease;
        if (verified.Diagnostic is { } diagnostic)
            return new(WallImageStatus.Invalid, null, 0, 0, diagnostic);
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

    private static (string? Path, int? Index) ReadUrlIconLocation(string path)
    {
        try
        {
            using var source = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (source.Length > TileWall.Core.Entries.EntryObservation.MaximumBytes) return (null, null);
            var bytes = new byte[checked((int)source.Length)];
            source.ReadExactly(bytes);
            if (source.ReadByte() != -1) return (null, null);
            var metadata = TileWall.Core.Entries.UrlShortcut.ReadMetadata(bytes);
            int? iconIndex = int.TryParse(metadata.IconIndex, out var parsedIndex) ? parsedIndex : null;
            string? icon = null;
            if (metadata.IconFile is { } value)
            {
                var comma = value.LastIndexOf(',');
                if (comma >= 0 && int.TryParse(value[(comma + 1)..], out var index)) { iconIndex ??= index; value = value[..comma]; }
                icon = NormalizePath(value.Trim('"'));
            }
            return (icon, iconIndex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return (null, null);
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
                    if (cached.MemoryValidUntil <= DateTimeOffset.UtcNow)
                    {
                        _lru.Remove(node); _cache.Remove(key); _cacheBytes -= node.Value.Bytes;
                        prepared = null!; return false;
                    }
                    prepared = cached;
                    return true;
                }
            }
        }
        prepared = null!;
        return false;
    }

    private void Cache(string key, Prepared frame, bool allowMemoryOnly = false)
    {
        if (!frame.MemoryCacheAllowed || !frame.CacheAllowed && !allowMemoryOnly ||
            frame.Pixels is null || frame.Pixels.LongLength > CacheLimitBytes / 2) return;
        lock (_cacheLock)
        {
            if (_disposed || _cacheLifetime.IsCancellationRequested || _cache.ContainsKey(key)) return;
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
        lock (_workGate) _cacheStopping = true;
        _cacheLifetime.Cancel();
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
        string? Diagnostic = null)
    {
        public bool MemoryCacheAllowed { get; init; } = true;
        public DateTimeOffset MemoryValidUntil { get; init; } = DateTimeOffset.MaxValue;
        public bool CacheAllowed { get; init; } = true;
    }

    private static class ShellIconPixels
    {
        public static (byte[] Pixels, int Width, int Height)? Read(string path, bool association = false, int physicalPixels = 32) =>
            ShellItemCatalog.InApartment(() => ReadCore(path, association, physicalPixels));

        internal static IReadOnlyList<(byte[] Pixels, int Width, int Height)> ReadSeries(string path, bool association = false) =>
            ShellItemCatalog.InApartment(() =>
            {
                var frames = new List<(byte[] Pixels, int Width, int Height)>();
                var info = new Win32Api.ShFileInfoW { szDisplayName = string.Empty, szTypeName = string.Empty };
                var size = (uint)Marshal.SizeOf<Win32Api.ShFileInfoW>();
                const uint useFileAttributes = 0x10, normalFile = 0x80, systemIconIndex = 0x4000;
                IntPtr pidl = IntPtr.Zero;
                try
                {
                    if (!association && IsNamespace(path))
                    {
                        // This is the same Shell object, not its linked filesystem target.
                        Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _));
                        if (pidl == IntPtr.Zero) throw new IOException("The Shell icon item is unavailable.");
                    }
                    if (ReadInfo(systemIconIndex) != IntPtr.Zero)
                    {
                        var extra = ReadImageListIcon(info.iIcon, 2);
                        // Enumerate actual list kinds. A padded Jumbo slot still
                        // falls back only on the existing exact-pixel proof.
                        foreach (var kind in new[] { 4, 2, 0, 1, 3 })
                        {
                            if (ReadImageListIcon(info.iIcon, kind) is not { } native) continue;
                            if (extra is { } small && ShellImageListFrameRules.IsPaddedDuplicate(native.Pixels, native.Width,
                                native.Height, small.Pixels, small.Width, small.Height)) native = small;
                            if (!frames.Any(frame => frame.Width == native.Width && frame.Height == native.Height)) frames.Add(native);
                        }
                    }
                    var result = ReadInfo(Win32Api.ShgfiIcon | Win32Api.ShgfiLargeicon);
                    if (result != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                        try
                        {
                            if (ReadIcon(info.hIcon) is { } native && !frames.Any(frame => frame.Width == native.Width && frame.Height == native.Height))
                                frames.Add(native);
                        }
                        finally { Win32Api.DestroyIcon(info.hIcon); }
                    return (IReadOnlyList<(byte[] Pixels, int Width, int Height)>)frames;

                    IntPtr ReadInfo(uint flags) => pidl != IntPtr.Zero
                        ? SHGetFileInfoPidlW(pidl, 0, ref info, size, flags | 0x8) // SHGFI_PIDL
                        : Win32Api.SHGetFileInfoW(path, association ? normalFile : 0, ref info, size,
                            flags | (association ? useFileAttributes : 0));
                }
                finally { if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
            });

        internal static bool IsNamespace(string path) => path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("::{", StringComparison.Ordinal);

        private static (byte[] Pixels, int Width, int Height)? ReadCore(string path, bool association, int physicalPixels)
        {
            var frames = ReadSeries(path, association);
            return frames.Count == 0 ? null : frames.OrderBy(frame => frame.Width >= physicalPixels && frame.Height >= physicalPixels ? 0 : 1)
                .ThenBy(frame => frame.Width >= physicalPixels && frame.Height >= physicalPixels
                    ? (long)frame.Width * frame.Height : -(long)frame.Width * frame.Height).First();
        }
        private static (byte[] Pixels, int Width, int Height)? ReadImageListIcon(int index, int imageList)
        {
            var iid = typeof(IImageList).GUID;
            IImageList? list = null;
            IntPtr icon = IntPtr.Zero;
            try
            {
                return SHGetImageList(imageList, ref iid, out list) >= 0 && list is not null &&
                    list.GetIcon(index, 1, out icon) >= 0 && icon != IntPtr.Zero ? ReadIcon(icon) : null;
            }
            finally
            {
                if (icon != IntPtr.Zero) Win32Api.DestroyIcon(icon);
                if (list is not null) Marshal.ReleaseComObject(list);
            }
        }

        internal static (byte[] Pixels, int Width, int Height)? ReadIcon(IntPtr icon)
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
                if (width < 1 || height < 1 || (long)width * height * 4 > Array.MaxLength) return null;
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

        // Windows SDK CommonControls.h: slots preceding IImageList.GetIcon.
        [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IImageList
        {
            [PreserveSig] int Add(IntPtr image, IntPtr mask, out int index);
            [PreserveSig] int ReplaceIcon(int index, IntPtr icon, out int replaced);
            [PreserveSig] int SetOverlayImage(int image, int overlay);
            [PreserveSig] int Replace(int index, IntPtr image, IntPtr mask);
            [PreserveSig] int AddMasked(IntPtr image, uint mask, out int index);
            [PreserveSig] int Draw(IntPtr parameters);
            [PreserveSig] int Remove(int index);
            [PreserveSig] int GetIcon(int index, uint flags, out IntPtr icon);
        }
        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHGetImageList(int imageList, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IImageList? list);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributes, out uint returnedAttributes);
        [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr SHGetFileInfoPidlW(IntPtr pidl, uint attributes, ref Win32Api.ShFileInfoW info, uint size, uint flags);
    }
}
