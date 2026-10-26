using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using TileWall.Core.Wall;
using TileWall.Shell.Interop;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TileWall.Graphics;

public sealed partial class WallImageService
{
    private const long NativeIconLimit = 128L * 1024 * 1024;
    private readonly Dictionary<string, Task<NativeIconPackage>> _nativePackages = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _nativeSlots = new(2, 2);
    private sealed record NativeIconFrame(PeIconFrame Native, byte[] Ico, Prepared Decoded);
    private sealed record NativeIconPackage(PeIconGroup? Group, IReadOnlyList<NativeIconFrame> Frames,
        bool CacheEmpty, string? ResourcePath = null, string? Key = null, bool Complete = true);
    private sealed record DiskIconFrame(uint ResourceId, uint LanguageId, int Width, int Height, int BitsPerPixel,
        int ColorCount, int Planes, string NativeSha256, string File, string FileSha256);
    private sealed record DiskIconManifest(int Format, string Key, uint GroupId, string? GroupName, uint LanguageId,
        bool Empty, IReadOnlyList<DiskIconFrame> Frames);
    private sealed record DiskIconMapping(int Format, string Dependency, string PackageKey);
    public string? SharedIconDirectory => _disk?.Under("Cache", "Images", "Icons", "Packages");

    private static PuzzleDecodeBudget CachedIconBudget(int width, bool needsBlur, MemberImagePurpose purpose, int? height = null)
    {
        var thumbnail = purpose == MemberImagePurpose.Thumbnail;
        var physicalWidth = Math.Clamp(width, 1, thumbnail ? 256 : 4096);
        var physicalHeight = Math.Clamp(height ?? width, 1, thumbnail ? 256 : 4096);
        var edge = Math.Max(physicalWidth, physicalHeight);
        return new(purpose, physicalWidth, physicalHeight, !thumbnail, thumbnail ? edge : 4096, thumbnail ? edge : 512, needsBlur);
    }

    public Task<string?> PrewarmPeIconsAsync(string sourcePath, string? targetPath, string? iconPath,
        int? iconIndex, string sourceSha256, string iconsDirectory, CancellationToken ct = default) =>
        StartImageRequest(token => PrewarmImportIconAsync(sourcePath, targetPath, iconPath, iconIndex,
            sourceSha256, token), ct, (string?)null);

    private async Task<string?> PrewarmImportIconAsync(string sourcePath, string? targetPath, string? iconPath,
        int? iconIndex, string sourceSha256, CancellationToken ct)
    {
        try
        {
            if (_disk is null || FileContentHash(sourcePath) != sourceSha256) return null;
            var policy = await Task.Run(() => ShellItemCatalog.IconLocation(sourcePath, 96), ct).ConfigureAwait(false);
            if (policy is { CanCache: false } or { CanReadResource: false }) return null;
            if (string.IsNullOrWhiteSpace(iconPath) && policy is { CanReadResource: true, Path: not null })
            { iconPath = policy.Path; iconIndex = policy.Index; }
            var target = NormalizePath(targetPath) ?? NormalizePath(iconPath);
            if (target is null) return null;
            var dependency = NativeIconDependency(target, iconPath, iconIndex);
            if (dependency is null) return null;
            var package = await NativePackageAsync(target, iconPath, iconIndex, dependency).WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return FileContentHash(sourcePath) == sourceSha256 &&
                dependency == NativeIconDependency(target, iconPath, iconIndex) ? package.Key : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or COMException)
        { return null; }
    }

    public Task<PuzzleFrameResult> LoadCachedIconFrameAsync(string iconsDirectory, string iconKey, int edge,
        CancellationToken ct = default) => LoadCachedIconFrameAsync(iconsDirectory, iconKey, edge, edge, ct);

    public Task<PuzzleFrameResult> LoadCachedIconFrameAsync(string iconsDirectory, string iconKey, int width, int height,
        CancellationToken ct = default) => StartImageRequest(token => LoadCachedIconFrameCoreAsync(iconsDirectory,
            iconKey, width, height, token), ct, new PuzzleFrameResult(null, WallImageStatus.Cancelled));

    private async Task<PuzzleFrameResult> LoadCachedIconFrameCoreAsync(string iconsDirectory, string key,
        int width, int height, CancellationToken ct)
    {
        if (_disk is not { } disk || SharedIconDirectory is not { } root ||
            !string.Equals(Path.GetFullPath(iconsDirectory), root, StringComparison.OrdinalIgnoreCase))
            return new(null, WallImageStatus.Fallback, "IconCacheMissing");
        try
        {
            using var lease = disk.Lease(Path.Combine(root, key));
            var manifest = ReadIconManifest(key);
            if (manifest is null) return new(null, WallImageStatus.Fallback, "IconCacheMissing");
            var group = new PeIconGroup(manifest.GroupId, manifest.GroupName, manifest.LanguageId,
                manifest.Frames.Select(frame => new PeIconFrame(frame.ResourceId, frame.LanguageId, frame.Width,
                    frame.Height, frame.BitsPerPixel, frame.ColorCount, frame.Planes, PeIconFrameFormat.Unknown,
                    ReadOnlyMemory<byte>.Empty, frame.NativeSha256)).ToArray());
            var chosen = PeIconResourceReader.SelectFrame(group, width, height);
            var frame = manifest.Frames.First(item => item.ResourceId == chosen!.ResourceId && item.NativeSha256 == chosen.Sha256);
            var path = Path.Combine(root, key, frame.File);
            var budget = new PuzzleDecodeBudget(MemberImagePurpose.Content, width, height, true, 4096, 512, false);
            var result = await LoadImageFileFrameAsync(path, budget, ct, "automatic:shared-icon:" + key, frame.FileSha256);
            return result.Frame is { } loaded && (loaded.SourcePixels.Width != frame.Width || loaded.SourcePixels.Height != frame.Height)
                ? new(null, WallImageStatus.Invalid, "IconCacheDimensionsMismatch") : result;
        }
        catch (OperationCanceledException) { return new(null, WallImageStatus.Cancelled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { return new(null, WallImageStatus.Invalid, ex.GetType().Name); }
    }

    private DiskIconManifest? ReadIconManifest(string key)
    {
        if (_disk is not { } disk || key.Length != 64 || !key.All(Uri.IsHexDigit)) return null;
        try
        {
            var directory = disk.Under("Cache", "Images", "Icons", "Packages", key);
            var manifest = JsonSerializer.Deserialize<DiskIconManifest>(disk.Read(Path.Combine(directory, "manifest.json"), 128 * 1024), CacheJson);
            // Old Empty manifests have no proof of a stable absence. Never import them.
            if (manifest is null || manifest.Format != 2 || manifest.Key != key || manifest.Empty ||
                manifest.Frames is not { Count: > 0 and <= 64 }) return null;
            foreach (var frame in manifest.Frames)
            {
                if (frame is null || frame.Width is < 1 or > 256 || frame.Height is < 1 or > 256 ||
                    frame.NativeSha256 is not { Length: 64 } || !frame.NativeSha256.All(Uri.IsHexDigit) ||
                    frame.FileSha256 is not { Length: 64 } || !frame.FileSha256.All(Uri.IsHexDigit) ||
                    frame.File != frame.NativeSha256 + ".ico") return null;
                var path = Path.Combine(directory, frame.File);
                var bytes = disk.Read(path, 4 * 1024 * 1024 + 22);
                if (Convert.ToHexString(SHA256.HashData(bytes)) != frame.FileSha256) return null;
            }
            return manifest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException) { return null; }
    }

    private async Task<NativeIconPackage?> ReadSharedNativePackageAsync(string dependency, CancellationToken token)
    {
        if (_disk is not { } disk) return null;
        try
        {
            var mapping = JsonSerializer.Deserialize<DiskIconMapping>(disk.Read(disk.Under("Cache", "Images", "Icons", "Mappings",
                ManagedImageCache.Key(dependency) + ".json"), 128 * 1024), CacheJson);
            if (mapping is null || mapping.Format != 2 || mapping.Dependency != dependency ||
                mapping.PackageKey is not { Length: 64 } || !mapping.PackageKey.All(Uri.IsHexDigit)) return null;
            var directory = disk.Under("Cache", "Images", "Icons", "Packages", mapping.PackageKey);
            using var lease = disk.Lease(directory);
            var manifest = ReadIconManifest(mapping.PackageKey);
            if (manifest is null) { disk.DiscardIconPackage(mapping.PackageKey); return null; }
            var native = new List<PeIconFrame>();
            foreach (var item in manifest.Frames)
            {
                token.ThrowIfCancellationRequested();
                var single = ReadExplicitIco(Path.Combine(directory, item.File), 0)?.Frames.SingleOrDefault();
                if (single is null || single.Width != item.Width || single.Height != item.Height || single.Sha256 != item.NativeSha256) return null;
                native.Add(single with { ResourceId = item.ResourceId, LanguageId = item.LanguageId });
            }
            var group = new PeIconGroup(manifest.GroupId, manifest.GroupName, manifest.LanguageId, native);
            var frames = await DecodeIconGroupAsync(group, token).ConfigureAwait(false);
            return frames.Count == native.Count ? new(group, frames, false, Key: mapping.PackageKey) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException) { return null; }
    }

    private string? PublishSharedNativePackage(NativeIconPackage package, string dependency, CancellationToken token)
    {
        if (_disk is not { } disk || package.Group is null || package.Frames.Count == 0 || !package.Complete) return null;
        var group = package.Group;
        var key = ManagedImageCache.Key("native-resource-v4|" + Fingerprint(package.ResourcePath, "resource") + "|" +
            group.ResourceId + "|" + group.ResourceName + "|" + group.LanguageId + "|" +
            string.Join("|", package.Frames.Select(frame => frame.Native.Sha256)));
        var frames = package.Frames.Select(frame => new DiskIconFrame(frame.Native.ResourceId, frame.Native.LanguageId,
            frame.Native.Width, frame.Native.Height, frame.Native.BitsPerPixel, frame.Native.ColorCount, frame.Native.Planes,
            frame.Native.Sha256, frame.Native.Sha256 + ".ico", Convert.ToHexString(SHA256.HashData(frame.Ico)))).ToArray();
        var manifest = new DiskIconManifest(2, key, group.ResourceId, group.ResourceName, group.LanguageId, false, frames);
        var payload = package.Frames.GroupBy(frame => frame.Native.Sha256, StringComparer.Ordinal)
            .ToDictionary(items => items.Key + ".ico", items => items.First().Ico, StringComparer.Ordinal);
        payload.Add("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, CacheJson));
        var mapping = JsonSerializer.SerializeToUtf8Bytes(new DiskIconMapping(2, dependency, key), CacheJson);
        try
        {
            if (ReadIconManifest(key) is null) disk.DiscardIconPackage(key);
            return disk.PublishIconPackage(key, payload, ManagedImageCache.Key(dependency), mapping, NativeIconLimit, token) ? key : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return null; }
    }

    private static string? NativeIconDependency(string exe, string? iconPath, int? iconIndex, string? language = null)
    {
        var icon = NormalizePath(iconPath);
        var iconDependency = icon is not null ? Fingerprint(icon, "explicit-icon") ?? "missing:" + icon
            : string.IsNullOrWhiteSpace(iconPath) ? "none" : "invalid:" + iconPath;
        var target = Fingerprint(exe, "target") ?? "non-file:" + exe;
        if (icon is null && !File.Exists(exe)) return null;
        return "native-icon-v4:" + (language ?? CultureInfo.CurrentUICulture.Name) + ":" + iconIndex + ":" + iconDependency + "|" + target;
    }
    private static PeIconGroup? ReadExplicitIco(string path, int? index)
    {
        // An ICO is one icon group with multiple size/depth frames, not multiple indexed groups.
        if (index is not (null or 0)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length is < 22 or > 32 * 1024 * 1024) return null;
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1) return null;
            var count = reader.ReadUInt16();
            if (count is < 1 or > 64 || stream.Length < 6 + count * 16) return null;
            var entries = new List<(int Width, int Height, int Colors, int Planes, int Bits, uint Bytes, uint Offset)>();
            long total = 0;
            for (var n = 0; n < count; n++)
            {
                var width = reader.ReadByte(); var height = reader.ReadByte(); var colors = reader.ReadByte();
                _ = reader.ReadByte(); var planes = reader.ReadUInt16(); var bits = reader.ReadUInt16();
                var bytes = reader.ReadUInt32(); var offset = reader.ReadUInt32();
                total += bytes;
                if (bytes is < 1 or > 4 * 1024 * 1024 || total > 32 * 1024 * 1024 ||
                    offset < 6 + count * 16 || (long)offset + bytes > stream.Length) return null;
                entries.Add((width == 0 ? 256 : width, height == 0 ? 256 : height, colors, planes, bits, bytes, offset));
            }
            var frames = new List<PeIconFrame>();
            foreach (var entry in entries)
            {
                stream.Position = entry.Offset;
                var bytes = reader.ReadBytes(checked((int)entry.Bytes));
                if (bytes.Length != entry.Bytes) return null;
                var format = PeIconResourceReader.DetectFormat(bytes, entry.Width, entry.Height);
                if (format == PeIconFrameFormat.Unknown) continue;
                frames.Add(new((uint)frames.Count + 1, 0, entry.Width, entry.Height, entry.Bits, entry.Colors,
                    entry.Planes, format, bytes, Convert.ToHexString(SHA256.HashData(bytes))));
            }
            return frames.Count == 0 ? null : new(0, null, 0, frames);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { return null; }
    }

    private static async Task<IReadOnlyList<NativeIconFrame>> DecodeIconGroupAsync(PeIconGroup? group, CancellationToken ct)
    {
        var frames = new List<NativeIconFrame>();
        if (group is not null)
            foreach (var frame in group.Frames)
            {
                ct.ThrowIfCancellationRequested();
                var prepared = await DecodeNativeFrameAsync(frame, group, ct).ConfigureAwait(false);
                if (prepared.Pixels is not null) frames.Add(new(frame, PeIconResourceReader.WrapSingleFrameIco(frame), prepared));
            }
        return frames;
    }

    private Task<NativeIconPackage> NativePackageAsync(string exe, string? iconPath, int? iconIndex, string dependency,
        bool cacheAllowed = true)
    {
        lock (_workGate)
        {
            if (_cacheStopping || _disposed)
                return Task.FromCanceled<NativeIconPackage>(new CancellationToken(true));
            // DONTCACHE may still name an independently persisted explicit icon.
            // Parse/decode it through the same bounded producer without touching
            // any reusable native result, mapping, or package.
            if (!cacheAllowed) return TrackCacheWork(ProduceNativePackageAsync(exe, iconPath, iconIndex, dependency, false));
            if (_nativePackages.TryGetValue(dependency, out var task))
            {
                // Share the current producer and retain verified native results.
                // A transient read/decode failure is not a durable "no icon" fact;
                // only a later real request retries it, without background polling.
                if (!task.IsCompleted || task.IsCompletedSuccessfully &&
                    task.Result.Complete && (task.Result.Frames.Count != 0 || task.Result.CacheEmpty)) return task;
                _nativePackages.Remove(dependency);
            }
            task = ProduceNativePackageAsync(exe, iconPath, iconIndex, dependency, true);
            _nativePackages.Add(dependency, task); return TrackCacheWork(task);
        }
    }

    private Task<NativeIconPackage> ProduceNativePackageAsync(string exe, string? iconPath, int? iconIndex,
        string dependency, bool cacheAllowed) => Task.Run(async () =>
    {
        await _nativeSlots.WaitAsync(_cacheLifetime.Token).ConfigureAwait(false);
        try
        {
            var language = CultureInfo.CurrentUICulture.Name;
            var shared = cacheAllowed ? await ReadSharedNativePackageAsync(dependency, _cacheLifetime.Token).ConfigureAwait(false) : null;
            if (shared is not null && NativeIconDependency(exe, iconPath, iconIndex, language) == dependency) return shared;
            var reader = cacheAllowed ? _peReader : new PeIconResourceReader();
            var explicitPath = NormalizePath(iconPath);
            var resourcePath = explicitPath;
            PeIconGroup? group = null;
            if (explicitPath is not null)
                group = Path.GetExtension(explicitPath).Equals(".ico", StringComparison.OrdinalIgnoreCase)
                    ? ReadExplicitIco(explicitPath, iconIndex)
                    : reader.Read(explicitPath, allowResourceModule: true).Catalog?
                        .SelectGroup(explicitPath, iconIndex ?? 0, fallbackToDefault: false);
            var frames = await DecodeIconGroupAsync(group, _cacheLifetime.Token).ConfigureAwait(false);
            var explicitFailure = explicitPath is not null && frames.Count == 0;
            PeIconReadResult? fallback = null;
            if (frames.Count == 0)
            {
                resourcePath = exe;
                fallback = reader.Read(exe);
                group = fallback.Catalog?.SelectGroup();
                frames = await DecodeIconGroupAsync(group, _cacheLifetime.Token).ConfigureAwait(false);
            }
            _cacheLifetime.Token.ThrowIfCancellationRequested();
            // The parsed resource and its cache identity must describe the same file generation.
            if (NativeIconDependency(exe, iconPath, iconIndex, language) != dependency)
                return new NativeIconPackage(null, [], false, Complete: false);
            var cacheEmpty = group is null && fallback?.Status is PeIconReadStatus.NoIconGroups or PeIconReadStatus.NotExe or PeIconReadStatus.FileTooLarge;
            var complete = !explicitFailure && (group is null ? cacheEmpty : frames.Count == group.Frames.Count);
            var result = new NativeIconPackage(group, frames, cacheEmpty, resourcePath, Complete: complete);
            if (cacheAllowed && NativeIconDependency(exe, iconPath, iconIndex, language) == dependency)
                result = result with { Key = PublishSharedNativePackage(result, dependency, _cacheLifetime.Token) };
            if (cacheAllowed)
            {
                lock (_workGate)
                {
                    var completed = _nativePackages.Where(pair => pair.Key != dependency && pair.Value.IsCompletedSuccessfully).ToArray();
                    var bytes = completed.Sum(pair => pair.Value.Result.Frames.Sum(frame => (long)frame.Ico.Length + (frame.Decoded.Pixels?.LongLength ?? 0))) +
                        frames.Sum(frame => (long)frame.Ico.Length + (frame.Decoded.Pixels?.LongLength ?? 0));
                    foreach (var pair in completed)
                    { if (_nativePackages.Count <= 32 && bytes <= 64L * 1024 * 1024) break; _nativePackages.Remove(pair.Key); bytes -= pair.Value.Result.Frames.Sum(frame => (long)frame.Ico.Length + (frame.Decoded.Pixels?.LongLength ?? 0)); }
                }
            }
            return result;
        }
        finally { _nativeSlots.Release(); }
    }, _cacheLifetime.Token);

    private static async Task<Prepared> DecodeNativeFrameAsync(PeIconFrame frame, PeIconGroup group, CancellationToken ct)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream(); using var writer = new DataWriter(stream);
            // Cancellation must reach the WinRT bridge: checking between frames
            // cannot end a shutdown wait when a codec never signals Completed.
            writer.WriteBytes(PeIconResourceReader.WrapSingleFrameIco(frame));
            await writer.StoreAsync().AsTask(ct).ConfigureAwait(false); stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(ct).ConfigureAwait(false);
            var pixels = data.DetachPixelData();
            if (decoder.PixelWidth != frame.Width || decoder.PixelHeight != frame.Height || pixels.Length != frame.Width * frame.Height * 4)
                return new(WallImageStatus.Fallback, null, 0, 0, "PE:InvalidFramePixels");
            var visible = false;
            for (var index = 3; index < pixels.Length; index += 4) visible |= pixels[index] != 0;
            if (!visible) return new(WallImageStatus.Fallback, null, 0, 0, "PE:TransparentFrame");
            return new(WallImageStatus.Loaded, pixels, frame.Width, frame.Height, $"PE:{group.ResourceId}:{frame.ResourceId}:{frame.Width}x{frame.Height}");
        }
        catch (Exception ex) when (ex is COMException or IOException or ArgumentException or InvalidOperationException)
        { return new(WallImageStatus.Fallback, null, 0, 0, "PE:" + ex.GetType().Name); }
    }
}
