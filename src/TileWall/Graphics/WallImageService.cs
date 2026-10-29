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
/// Bounded read-only image resolution for a tile. Call from the XAML thread: file, shell,
/// PE and WIC work runs on a worker, then WriteableBitmap is created back on the caller thread.
/// Bounded memory decoding and scoped, disposable image/icon caches share verified source identities.
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
        if (widthPixels <= 0 || heightPixels <= 0 || widthPixels > 4096 || heightPixels > 4096)
            throw new ArgumentOutOfRangeException(nameof(widthPixels), "Physical dimensions must be 1..4096.");
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

    private async Task<Prepared> PrepareAutoAsync(string? entryOrTargetPath, int width, int height, CancellationToken ct,
        bool skipNative = false, bool cacheAllowed = true, MmcIconSource? mmcSource = null, bool followShellTarget = true,
        string? verifiedPackageTarget = null)
    {
        var policy = new AutomaticMemoryPolicy();
        var prepared = await PrepareAutoCoreAsync(entryOrTargetPath, width, height, ct,
            skipNative, cacheAllowed, mmcSource, followShellTarget, policy, verifiedPackageTarget).ConfigureAwait(false);
        return prepared with { MemoryCacheAllowed = prepared.MemoryCacheAllowed && policy.Allowed };
    }

    private sealed class AutomaticMemoryPolicy { public bool Allowed = true; }

    private async Task<Prepared> PrepareAutoCoreAsync(string? entryOrTargetPath, int width, int height, CancellationToken ct,
        bool skipNative, bool cacheAllowed, MmcIconSource? mmcSource, bool followShellTarget, AutomaticMemoryPolicy memoryPolicy,
        string? verifiedPackageTarget)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(entryOrTargetPath))
            return new(WallImageStatus.None, null, 0, 0, null);
        var entry = NormalizePath(entryOrTargetPath);
        var extension = entry is null ? "" : Path.GetExtension(entry);
        string? target = entry ?? entryOrTargetPath;
        string? iconPath = null;
        int? iconIndex = null;
        var shellLink = false;
        var shellIdentity = entry ?? entryOrTargetPath;
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var fields = _links.Read(entry!);
                shellLink = fields.HasIdList;
                target = !string.IsNullOrWhiteSpace(fields.ShellParsingName) ? fields.ShellParsingName : fields.TargetPath;
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
            var location = ReadUrlIconLocation(entry!);
            iconPath = location.Path; iconIndex = location.Index;
            if (new FileInfo(entry!).Length <= TileWall.Core.Entries.EntryObservation.MaximumBytes)
                target = TileWall.Core.Entries.UrlShortcut.ReadUrlLine(File.ReadAllBytes(entry!));
        }
        var hasPersistedIconLocation = !string.IsNullOrWhiteSpace(iconPath);
        var targetFile = NormalizePath(target);
        var protocol = targetFile is null && Uri.TryCreate(target, UriKind.Absolute, out var parsed) && !parsed.IsFile ? parsed : null;
        var web = protocol?.Scheme is "http" or "https";
        var registeredSearch = protocol?.Scheme.Equals("ms-search", StringComparison.OrdinalIgnoreCase) == true;
        var shellNamespace = shellLink || target?.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) == true ||
            target?.StartsWith("::{", StringComparison.Ordinal) == true;
        if (!skipNative && !hasPersistedIconLocation && IsMmcConsolePath(targetFile))
        {
            var requestCacheAllowed = cacheAllowed;
            var mmc = mmcSource is not null && string.Equals(mmcSource.ConsolePath, targetFile,
                StringComparison.OrdinalIgnoreCase) ? mmcSource : ReadMmcIconSource(targetFile!);
            cacheAllowed &= mmc.CacheAllowed;
            if (mmc.Declaration is { } declaration && IsSafeExternalFilePath(declaration.Path))
            {
                // MMC's XML declaration is authoritative only when the source
                // shortcut did not persist its own icon location. Parse its
                // native resource through the same bounded PE/ICO frame path,
                // but keep the native-package reader request-only; the outer
                // automatic stamp below includes the exact console and resource
                // content dependencies before it may persist these pixels.
                var declared = await PreparePeIconAsync(targetFile!, declaration.Path, declaration.Index,
                    entryOrTargetPath, width, height, ct, cacheAllowed: false);
                if (declared.Status == WallImageStatus.Cancelled) return declared;
                if (declared.Pixels is not null)
                    return declared with { CacheAllowed = requestCacheAllowed && mmc.CacheAllowed };
                if (TryPrepareMmcNativeIcon(declaration, width, height, ct) is { Pixels: not null } native)
                    return native;
                // Do not persist a Shell/placeholder result when an explicitly
                // declared native icon could not be read or decoded.
                cacheAllowed = false;
            }
            var knownSystem = await TryPrepareBundledSystemAsync(target, width, height, ct).ConfigureAwait(false);
            if (knownSystem?.Pixels is not null) return knownSystem;
            if (knownSystem is not null) cacheAllowed &= knownSystem.CacheAllowed;
        }
        // A shortcut's persisted IconFile/IconLocation is a separate authority
        // from an IExtractIcon handler's potentially opaque location string.
        var persistedIcon = NormalizePath(iconPath) is not null;
        // URI shortcuts must resolve the protocol's own icon, rather than the
        // InternetShortcut association, for system settings and other native URIs.
        if (protocol is not null && !web && !extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) shellIdentity = target!;
        if (protocol?.Scheme.Equals("ms-settings", StringComparison.OrdinalIgnoreCase) == true)
        {
            var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ImmersiveControlPanel", "SystemSettings.exe");
            if (File.Exists(settings)) { targetFile = settings; shellIdentity = settings; }
        }
        var locationFacts = !skipNative && (!web || entry is not null) ? ShellItemCatalog.IconLocation(shellIdentity, Math.Max(width, height)) : null;
        if (locationFacts is { CanCache: false })
        {
            cacheAllowed = false;
            memoryPolicy.Allowed = false; // Respect an explicit native GIL_DONTCACHE.
        }
        if (shellNamespace || locationFacts is { Flags: var instanceFlags } && (instanceFlags & 2) != 0 ||
            !web && locationFacts is null) cacheAllowed = false;
        var independentExplicitIcon = persistedIcon && !(locationFacts is { Flags: var persistedFlags } && (persistedFlags & 2) != 0);
        // AppsFolder's image factory may plate a tiny legacy logo onto an
        // opaque tile. Resolve this registered package's app-list resource
        // before accepting that composition, while respecting explicit icons.
        if (!persistedIcon && await TryPreparePackagedAppIconAsync(verifiedPackageTarget ?? target, width, height, ct) is { } packaged)
            return packaged;
        if (!independentExplicitIcon && (locationFacts is { CanCache: false } || locationFacts is { CanReadResource: false }))
        {
            skipNative = true;
            cacheAllowed = false;
        }
        if (iconPath is null && !web &&
            locationFacts is { CanReadResource: true, Path: not null })
        { iconPath = locationFacts.Path; iconIndex = locationFacts.Index; }
        if (iconPath is null && protocol is not null && !web && !shellNamespace && !registeredSearch)
        {
            var association = ShellItemCatalog.ProtocolIconLocation(protocol.Scheme);
            if (association is { CanReadResource: true, Path: not null })
            { iconPath = association.Path; iconIndex = association.Index; }
        }
        var explicitFile = NormalizePath(iconPath);
        if (!skipNative && (explicitFile is not null || targetFile is not null && Path.GetExtension(targetFile).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            var direct = await PreparePeIconAsync(targetFile ?? explicitFile!, iconPath, iconIndex, entryOrTargetPath, width, height, ct, cacheAllowed);
            if (direct.Pixels is not null || direct.Status == WallImageStatus.Cancelled)
                return direct with { CacheAllowed = cacheAllowed && direct.CacheAllowed };
            // A transient explicit read/decode failure must not become a stable
            // cached Shell/generic fallback while its metadata remains unchanged.
            cacheAllowed &= direct.CacheAllowed;
        }
        if (web && protocol is not null)
        {
            // A per-instance handler remains authoritative. A generic .url type
            // resource does not stand in for either a website or its browser.
            if (locationFacts is { Flags: var flags } && (flags & 2) != 0)
            {
                (byte[] Pixels, int Width, int Height)? instance = null;
                try
                {
                    instance = ShellItemCatalog.Icon(shellIdentity, Math.Max(width, height));
                }
                catch (Exception ex) when (ex is IOException or COMException or ArgumentException or InvalidOperationException) { }
                if (instance is null)
                {
                    try { instance = ShellItemCatalog.ExtractIcon(shellIdentity, Math.Max(width, height), ShellIconPixels.ReadIcon); }
                    catch (Exception ex) when (ex is IOException or COMException or ArgumentException or InvalidOperationException) { }
                }
                if (instance is { } instancePlane) return new(WallImageStatus.Loaded, instancePlane.Pixels, instancePlane.Width, instancePlane.Height,
                    "WebInstanceIcon") { CacheAllowed = cacheAllowed };
            }
            var bundled = await TryPrepareBundledWebsiteAsync(protocol, width, height, ct);
            if (bundled?.Pixels is not null) return bundled with { CacheAllowed = cacheAllowed && bundled.CacheAllowed };
            if (bundled is not null) cacheAllowed &= bundled.CacheAllowed;
            var browser = await PrepareDefaultBrowserIconAsync(protocol.Scheme, width, height, ct, cacheAllowed);
            if (browser.Pixels is not null) return browser with { CacheAllowed = cacheAllowed && browser.CacheAllowed };
            cacheAllowed &= browser.CacheAllowed;
            try
            {
                var association = ShellIconPixels.Read(".url", association: true, physicalPixels: Math.Max(width, height));
                if (association is { } fallback) return new(WallImageStatus.Fallback, fallback.Pixels, fallback.Width,
                    fallback.Height, "WebAssociation") { CacheAllowed = cacheAllowed };
            }
            catch (Exception ex) when (ex is IOException or COMException or ArgumentException or InvalidOperationException) { }
            return await TryPrepareBundledFallbackAsync(BundledFallbackIcon.Website, width, height, ct) is { } genericWeb
                ? genericWeb with { CacheAllowed = cacheAllowed }
                : new(WallImageStatus.Fallback, null, 0, 0, "WebIconUnavailable") { CacheAllowed = cacheAllowed };
        }
        // ms-search's URI Shell class can return a valid bitmap of the generic
        // blank-document icon. Its registered application is the icon authority.
        if (registeredSearch)
        {
            var search = await PrepareRegisteredProtocolAppIconAsync("ms-search", width, height, ct);
            if (search.Pixels is not null) return search with { CacheAllowed = cacheAllowed && search.CacheAllowed };
            return await PrepareSystemOrGenericFallbackAsync(target, targetFile, width, height, ct,
                cacheAllowed && search.CacheAllowed, "NativeProtocolIconUnavailable");
        }
        ct.ThrowIfCancellationRequested();
        (byte[] Pixels, int Width, int Height)? shell = null;
        if (!web)
        {
            try { shell = ShellItemCatalog.Icon(shellIdentity, Math.Max(width, height)); }
            catch (Exception ex) when (ex is COMException or IOException or ArgumentException or InvalidCastException) { }
        }
        // A verified location can recover a larger original frame of this same
        // item when its Shell factory only returned a small plane.
        if (!skipNative && locationFacts is { CanReadResource: true, Path: not null } resource &&
            (shell is null || shell.Value.Width < width || shell.Value.Height < height))
        {
            var original = await PreparePeIconAsync(resource.Path, resource.Path, resource.Index, shellIdentity, width, height, ct, cacheAllowed);
            if (original.Pixels is not null && (shell is null || (long)original.Width * original.Height > (long)shell.Value.Width * shell.Value.Height))
                return original with { CacheAllowed = cacheAllowed && original.CacheAllowed };
            cacheAllowed &= original.CacheAllowed;
        }
        if (shell is { } plane)
            return new(WallImageStatus.Loaded, plane.Pixels, plane.Width, plane.Height,
                $"ShellImageFactory:{plane.Width}x{plane.Height}:requested={width}x{height}:disk-cache={(cacheAllowed ? "allowed" : "disabled")}")
                { CacheAllowed = cacheAllowed };
        if (protocol is null || web)
        {
            var shellPath = entry is not null && (File.Exists(entry) || Directory.Exists(entry)) ? entry : targetFile;
            if (shellPath is not null && (File.Exists(shellPath) || Directory.Exists(shellPath)))
            {
                try
                {
                    var classic = ShellIconPixels.Read(shellPath, physicalPixels: Math.Max(width, height));
                    if (classic is { } icon) return new(WallImageStatus.Fallback, icon.Pixels, icon.Width, icon.Height,
                        $"ClassicShell:{icon.Width}x{icon.Height}:requested={width}x{height}") { CacheAllowed = cacheAllowed };
                }
                catch (Exception ex) when (ex is IOException or COMException or ArgumentException or InvalidOperationException) { }
            }
        }
        if (!web)
        {
            try
            {
                if (ShellItemCatalog.ExtractIcon(shellIdentity, Math.Max(width, height), ShellIconPixels.ReadIcon) is { } extracted)
                    return new(WallImageStatus.Loaded, extracted.Pixels, extracted.Width, extracted.Height,
                        $"ShellExtractIcon:{extracted.Width}x{extracted.Height}:requested={width}x{height}") { CacheAllowed = cacheAllowed };
            }
            catch (Exception ex) when (ex is IOException or COMException or ArgumentException or InvalidOperationException)
            { cacheAllowed = false; }
        }
        if (shellNamespace && followShellTarget && ShellItemCatalog.LinkedTargetPath(shellIdentity) is { } linked &&
            !string.Equals(linked, entry, StringComparison.OrdinalIgnoreCase) &&
            Path.GetExtension(linked).ToLowerInvariant() is ".url" or ".lnk" &&
            File.Exists(linked) && new FileInfo(linked).Length is > 0 and <= EntryObservation.MaximumBytes)
        {
            // AppsFolder may expose a link-to-shortcut whose own handler has no
            // image. Follow only its explicit metadata, never a title/path guess,
            // and retain the original Shell launch identity. One hop prevents cycles.
            var linkedIcon = await PrepareAutoAsync(linked, width, height, ct, followShellTarget: false);
            if (linkedIcon.Pixels is not null)
                return linkedIcon with { CacheAllowed = false, Diagnostic = "ShellLinkedTarget:" + linkedIcon.Diagnostic };
        }
        return await PrepareSystemOrGenericFallbackAsync(target, targetFile, width, height, ct, cacheAllowed,
            protocol is null ? "GenericPlaceholder" : "NativeProtocolIconUnavailable");
    }

    private async Task<Prepared> PrepareSystemOrGenericFallbackAsync(string? target, string? targetFile,
        int width, int height, CancellationToken ct, bool cacheAllowed, string diagnostic)
    {
        var system = await TryPrepareBundledSystemAsync(target, width, height, ct);
        if (system?.Pixels is not null) return system;
        if (system is not null) cacheAllowed &= system.CacheAllowed;
        var fallbackKind = targetFile is not null && Directory.Exists(targetFile) ? BundledFallbackIcon.Folder
            : targetFile is not null && File.Exists(targetFile) ? BundledFallbackIcon.File : BundledFallbackIcon.Unknown;
        return await TryPrepareBundledFallbackAsync(fallbackKind, width, height, ct) is { } generic
            ? generic with { CacheAllowed = cacheAllowed }
            : new(WallImageStatus.Fallback, null, 0, 0, diagnostic) { CacheAllowed = cacheAllowed };
    }

    private async Task<Prepared> PreparePeIconAsync(string exe, string? iconPath, int? iconIndex,
        string entry, int width, int height, CancellationToken ct, bool cacheAllowed = true)
    {
        var dependency = NativeIconDependency(exe, iconPath, iconIndex);
        var key = !cacheAllowed || dependency is null ? null : dependency + $":{width}x{height}";
        if (key is not null && TryGetCached(key, out var cached))
            return cached;
        ct.ThrowIfCancellationRequested();
        if (dependency is null) return new(WallImageStatus.Fallback, null, 0, 0, "PE:MissingFile") { CacheAllowed = false };
        var package = await NativePackageAsync(exe, iconPath, iconIndex, dependency, cacheAllowed).WaitAsync(ct).ConfigureAwait(false);
        if (package.Group is null || package.Frames.Count == 0)
            return new(WallImageStatus.Fallback, null, 0, 0, "PE:NoFrame") { CacheAllowed = cacheAllowed && package.Complete };
        var available = package.Group with { Frames = package.Frames.Select(frame => frame.Native).ToArray() };
        ct.ThrowIfCancellationRequested();
        if (NativeIconDependency(exe, iconPath, iconIndex) != dependency)
            return new(WallImageStatus.Fallback, null, 0, 0, "PE:ResourceChanged") { CacheAllowed = false };
        var selected = PeIconResourceReader.SelectFrame(available, width, height);
        var decoded = package.Frames.First(frame => frame.Native == selected).Decoded;
        var prepared = decoded with { Diagnostic = decoded.Diagnostic + $":resource={package.ResourcePath ?? exe}:requested={width}x{height}", CacheAllowed = cacheAllowed && package.Complete };
        if (key is not null) Cache(key, prepared);
        return prepared;
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

        private static (byte[] Pixels, int Width, int Height)? ReadCore(string path, bool association, int physicalPixels)
        {
            var info = new Win32Api.ShFileInfoW { szDisplayName = string.Empty, szTypeName = string.Empty };
            var size = (uint)Marshal.SizeOf<Win32Api.ShFileInfoW>();
            const uint useFileAttributes = 0x000000010;
            const uint normalFile = 0x000000080;
            if (physicalPixels > 32)
            {
                // The list index comes from this exact Shell item, never a guessed
                // stock icon number. Keep all returned pixels and transparent padding.
                const uint systemIconIndex = 0x000004000;
                var indexed = Win32Api.SHGetFileInfoW(path, association ? normalFile : 0, ref info, size,
                    systemIconIndex | (association ? useFileAttributes : 0));
                var iid = typeof(IImageList).GUID;
                IImageList? list = null;
                IntPtr icon = IntPtr.Zero;
                try
                {
                    if (indexed != IntPtr.Zero && SHGetImageList(physicalPixels > 48 ? 4 : 2, ref iid, out list) >= 0 &&
                        list is not null && list.GetIcon(info.iIcon, 1, out icon) >= 0 && icon != IntPtr.Zero && ReadIcon(icon) is { } native)
                        return native;
                }
                finally
                {
                    if (icon != IntPtr.Zero) Win32Api.DestroyIcon(icon);
                    if (list is not null) Marshal.ReleaseComObject(list);
                }
            }
            var result = Win32Api.SHGetFileInfoW(path, association ? normalFile : 0, ref info, size,
                Win32Api.ShgfiIcon | Win32Api.ShgfiLargeicon | (association ? useFileAttributes : 0));
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try { return ReadIcon(info.hIcon); }
            finally { _ = Win32Api.DestroyIcon(info.hIcon); }
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
    }
}
