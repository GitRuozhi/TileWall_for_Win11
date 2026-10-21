using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using TileWall.Core.Wall;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TileWall.Graphics;

public sealed partial class WallImageService
{
    private readonly Dictionary<string, Task<NativeIconPackage>> _nativePackages = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _nativeSlots = new(2, 2);
    private readonly Dictionary<string, Task<ManagedIconDescriptor>> _iconDescriptors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<AdoptedIconPackage?>> _memberIconCopies = new(StringComparer.Ordinal);
    private sealed record ManagedIconDescriptor(string Sha, string? Target, string? Icon, int? Index);
    private sealed record AdoptedIconPackage(string EntryPath, string EntrySha256, string Directory, string Key,
        string TargetPath, string? IconPath, int? IconIndex, string Language, string Dependency);
    private sealed record NativeIconFrame(PeIconFrame Native, byte[] Ico, Prepared Decoded);
    private sealed record NativeIconPackage(PeIconGroup? Group, IReadOnlyList<NativeIconFrame> Frames, bool CacheEmpty);
    private sealed record DiskIconFrame(uint ResourceId, uint LanguageId, int Width, int Height, int BitsPerPixel,
        int ColorCount, int Planes, string NativeSha256, string File, string FileSha256);
    private sealed record DiskIconManifest(int Format, string Key, uint GroupId, string? GroupName, uint LanguageId,
        bool Empty, IReadOnlyList<DiskIconFrame> Frames);

    public Task<string?> PrewarmPeIconsAsync(string sourcePath, string? targetPath, string? iconPath,
        int? iconIndex, string sourceSha256, string iconsDirectory, CancellationToken ct = default)
        => StartImageRequest(token => PrewarmPeIconsCoreAsync(sourcePath, targetPath, iconPath,
            iconIndex, sourceSha256, iconsDirectory, token), ct, (string?)null);

    private async Task<string?> PrewarmPeIconsCoreAsync(string sourcePath, string? targetPath, string? iconPath,
        int? iconIndex, string sourceSha256, string iconsDirectory, CancellationToken ct)
    {
        if (_disk is not { } disk) return null;
        if (sourceSha256.Length != 64 || !sourceSha256.All(Uri.IsHexDigit)) return null;
        var exe = NormalizePath(targetPath);
        if (exe is null || !Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            disk.Guard(iconsDirectory);
            return await Task.Run(async () =>
            {
                var token = ct; token.ThrowIfCancellationRequested();
                var language = CultureInfo.CurrentUICulture.Name;
                var dependency = Fingerprint(exe, "native-icon-v1:" + language + ":" + NormalizePath(iconPath) + ":" + iconIndex);
                if (dependency is null) return null;
                var key = ManagedImageCache.Key(sourceSha256 + "|" + dependency);
                if (ReadIconManifest(iconsDirectory, key, verifyFrames: true) is not null) return key;
                // Native extraction has its own bounded budget. A slow/cold EXE
                // must not occupy the slots used to read already verified frames.
                var package = await NativePackageAsync(exe, iconPath, iconIndex, dependency).WaitAsync(token).ConfigureAwait(false);
                if (package.Frames.Count == 0 && !package.CacheEmpty) return null;
                var files = new List<(string Staged, string Destination)>();
                var frames = new List<DiskIconFrame>();
                try
                {
                    var directory = Path.Combine(iconsDirectory, key); disk.Guard(directory);
                    foreach (var frame in package.Frames)
                    {
                        token.ThrowIfCancellationRequested();
                        var file = frame.Native.Sha256 + ".ico";
                        files.Add((await disk.StageAsync(frame.Ico, token).ConfigureAwait(false), Path.Combine(directory, file)));
                        frames.Add(new(frame.Native.ResourceId, frame.Native.LanguageId, frame.Native.Width, frame.Native.Height,
                            frame.Native.BitsPerPixel, frame.Native.ColorCount, frame.Native.Planes, frame.Native.Sha256, file,
                            Convert.ToHexString(SHA256.HashData(frame.Ico))));
                    }
                    var manifest = new DiskIconManifest(1, key, package.Group?.ResourceId ?? 0, package.Group?.ResourceName,
                        package.Group?.LanguageId ?? 0, frames.Count == 0, frames);
                    var manifestStage = await disk.StageAsync(JsonSerializer.SerializeToUtf8Bytes(manifest, CacheJson), token).ConfigureAwait(false);
                    files.Add((manifestStage, Path.Combine(directory, "manifest.json")));
                    var accepted = false;
                    await PublishCacheAsync(() =>
                    {
                        if (Fingerprint(exe, "native-icon-v1:" + language + ":" + NormalizePath(iconPath) + ":" + iconIndex) != dependency ||
                            !IconSourceStillCurrent(sourcePath, sourceSha256, iconsDirectory)) return;
                        foreach (var file in files) disk.Publish(file.Staged, file.Destination);
                        accepted = true;
                    }, token).ConfigureAwait(false);
                    return accepted ? key : null;
                }
                finally { foreach (var file in files) disk.Delete(file.Staged); }
            }, ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or COMException)
        { return null; }
    }

    /// <summary>After commit, adopt a verified source package without parsing its EXE again.</summary>
    public Task<bool> CopyCachedIconsToMemberAsync(string memberId, string managedEntryPath, string sourceSha256,
        string? targetPath, string? iconPath, int? iconIndex, string sourceIconsDirectory, string iconKey,
        CancellationToken ct = default) =>
        StartImageRequest(token => CopyCachedIconsRequestAsync(memberId, managedEntryPath, sourceSha256,
            targetPath, iconPath, iconIndex, sourceIconsDirectory, iconKey, token), ct, false);

    private Task<bool> CopyCachedIconsRequestAsync(string memberId, string managedEntryPath, string sourceSha256,
        string? targetPath, string? iconPath, int? iconIndex, string sourceIconsDirectory, string iconKey,
        CancellationToken ct)
    {
        Task<AdoptedIconPackage?> copy;
        lock (_workGate)
        {
            if (_disk is null || _cacheStopping || _disposed) return Task.FromResult(false);
            // Register synchronously before the first await. The view refresh
            // queued by the successful commit then observes this exact transfer.
            copy = TrackCacheWork(Task.Run(() => CopyCachedIconsCoreAsync(memberId, managedEntryPath, sourceSha256,
                targetPath, iconPath, iconIndex, sourceIconsDirectory, iconKey, _cacheLifetime.Token), _cacheLifetime.Token));
            _memberIconCopies[memberId] = copy;
        }
        return CopyAcceptedAsync(copy, ct);
    }

    private static async Task<bool> CopyAcceptedAsync(Task<AdoptedIconPackage?> copy, CancellationToken ct)
    {
        try { return await copy.WaitAsync(ct).ConfigureAwait(false) is not null; }
        catch (OperationCanceledException) { return false; }
    }

    private async Task<AdoptedIconPackage?> CopyCachedIconsCoreAsync(string memberId, string entryPath, string expectedSha,
        string? targetPath, string? iconPath, int? iconIndex, string sourceDirectory, string iconKey, CancellationToken ct)
    {
        if (_disk is not { } disk || expectedSha.Length != 64 || !expectedSha.All(Uri.IsHexDigit)) return null;
        var staged = new List<(string Source, string Destination)>();
        var token = ct;
        try
        {
            token.ThrowIfCancellationRequested();
            var exe = NormalizePath(targetPath);
            if (exe is null || !Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            var language = CultureInfo.CurrentUICulture.Name;
            var dependency = Fingerprint(exe, "native-icon-v1:" + language + ":" + NormalizePath(iconPath) + ":" + iconIndex);
            if (dependency is null || ManagedImageCache.Key(expectedSha + "|" + dependency) != iconKey) return null;
            var manifest = ReadIconManifest(sourceDirectory, iconKey, verifyFrames: true);
            if (manifest is null) return null;
            var destinationDirectory = disk.Under("Objects", memberId, "IconCache");
            disk.Guard(entryPath);
            foreach (var frame in manifest.Frames)
            {
                token.ThrowIfCancellationRequested();
                var source = Path.Combine(sourceDirectory, iconKey, frame.File);
                using var lease = disk.Lease(source);
                var bytes = disk.Read(source, 4 * 1024 * 1024 + 22);
                if (Convert.ToHexString(SHA256.HashData(bytes)) != frame.FileSha256) return null;
                staged.Add((await disk.StageAsync(bytes, token).ConfigureAwait(false),
                    Path.Combine(destinationDirectory, iconKey, frame.File)));
            }
            staged.Add((await disk.StageAsync(JsonSerializer.SerializeToUtf8Bytes(manifest, CacheJson), token).ConfigureAwait(false),
                Path.Combine(destinationDirectory, iconKey, "manifest.json")));
            var accepted = false;
            await PublishCacheAsync(() =>
            {
                if (Fingerprint(exe, "native-icon-v1:" + language + ":" + NormalizePath(iconPath) + ":" + iconIndex) != dependency ||
                    !IconSourceStillCurrent(entryPath, expectedSha, destinationDirectory)) return;
                foreach (var file in staged) disk.Publish(file.Source, file.Destination);
                accepted = true;
            }, token).ConfigureAwait(false);
            return accepted ? new(entryPath, expectedSha, destinationDirectory, iconKey, exe, iconPath, iconIndex, language, dependency) : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or COMException)
        { return null; }
        finally { foreach (var file in staged) disk.Delete(file.Source); }
    }

    private bool IconSourceStillCurrent(string sourcePath, string expectedSha, string iconsDirectory)
    {
        if (_disk is not { } disk || _cacheStopping) return false;
        disk.Guard(iconsDirectory);
        var objects = disk.Under("Objects") + Path.DirectorySeparatorChar;
        if (iconsDirectory.StartsWith(objects, StringComparison.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(objects, iconsDirectory).Split(Path.DirectorySeparatorChar);
            if (relative.Length != 2 || relative[1] != "IconCache" || _formalMember?.Invoke(relative[0]) is not { Entry: { } entry } member || member.Id != relative[0]) return false;
            var currentEntry = Path.GetFullPath(Path.Combine(disk.Root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            disk.Guard(currentEntry);
            if (!StringComparer.OrdinalIgnoreCase.Equals(currentEntry, sourcePath)) return false;
        }
        return FileContentHash(sourcePath) == expectedSha;
    }

    public Task<PuzzleFrameResult> LoadCachedIconFrameAsync(string iconsDirectory, string iconKey, int edge, CancellationToken ct = default)
        => StartImageRequest(token => LoadCachedIconFrameCoreAsync(iconsDirectory, iconKey, edge, false, token), ct,
            new PuzzleFrameResult(null, WallImageStatus.Cancelled));

    private async Task<PuzzleFrameResult> LoadCachedIconFrameCoreAsync(string iconsDirectory, string iconKey, int edge, bool needsBlur, CancellationToken ct)
    {
        DiskIconManifest? manifest;
        try { manifest = await Task.Run(() => ReadIconManifest(iconsDirectory, iconKey, verifyFrames: false), ct); }
        catch (OperationCanceledException) { return new(null, WallImageStatus.Cancelled); }
        if (manifest is null) return new(null, WallImageStatus.Fallback, "IconCacheMissing");
        if (manifest.Empty || manifest.Frames.Count == 0) return new(null, WallImageStatus.Fallback, "NoNativeIcon");
        var group = new PeIconGroup(manifest.GroupId, manifest.GroupName, manifest.LanguageId,
            manifest.Frames.Select(frame => new PeIconFrame(frame.ResourceId, frame.LanguageId, frame.Width, frame.Height, frame.BitsPerPixel,
                frame.ColorCount, frame.Planes, PeIconFrameFormat.Unknown, ReadOnlyMemory<byte>.Empty, frame.NativeSha256)).ToArray());
        var chosen = PeIconResourceReader.SelectFrame(group, Math.Max(1, edge));
        var descriptor = manifest.Frames.First(frame => frame.ResourceId == chosen!.ResourceId && frame.Width == chosen.Width && frame.Height == chosen.Height && frame.BitsPerPixel == chosen.BitsPerPixel);
        var path = Path.Combine(iconsDirectory, iconKey, descriptor.File);
        try
        {
            if (await Task.Run(() => FileContentHash(path), ct) != descriptor.FileSha256)
            { _disk!.Delete(Path.Combine(iconsDirectory, iconKey, "manifest.json")); return new(null, WallImageStatus.Invalid, "IconCacheHashMismatch"); }
            var result = await LoadImageFileFrameAsync(path, new(MemberImagePurpose.Content, Math.Clamp(edge, 1, 4096), Math.Clamp(edge, 1, 4096), true, 4096, 512, needsBlur), ct,
                "automatic:cached-icon:" + iconKey, descriptor.FileSha256);
            if (result.Frame is { } frame && (frame.SourcePixels.Width != descriptor.Width || frame.SourcePixels.Height != descriptor.Height))
            { _disk!.Delete(Path.Combine(iconsDirectory, iconKey, "manifest.json")); return new(null, WallImageStatus.Invalid, "IconCacheDimensionsMismatch"); }
            return result;
        }
        catch (OperationCanceledException) { return new(null, WallImageStatus.Cancelled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return new(null, WallImageStatus.Invalid, ex.GetType().Name); }
    }

    private DiskIconManifest? ReadIconManifest(string iconsDirectory, string key, bool verifyFrames)
    {
        if (_disk is not { } disk || key.Length != 64 || !key.All(Uri.IsHexDigit)) return null;
        try
        {
            disk.Guard(iconsDirectory); var directory = Path.Combine(iconsDirectory, key);
            var manifest = JsonSerializer.Deserialize<DiskIconManifest>(disk.Read(Path.Combine(directory, "manifest.json"), 128 * 1024), CacheJson);
            if (manifest is null || manifest.Format != 1 || manifest.Key != key || manifest.Frames is null || manifest.Frames.Count > 64 ||
                manifest.Empty != (manifest.Frames.Count == 0)) return null;
            foreach (var frame in manifest.Frames)
            {
                if (frame is null || frame.Width is < 1 or > 256 || frame.Height is < 1 or > 256 ||
                    frame.NativeSha256 is not { Length: 64 } || !frame.NativeSha256.All(Uri.IsHexDigit) || frame.FileSha256 is not { Length: 64 } || !frame.FileSha256.All(Uri.IsHexDigit) ||
                    frame.File != frame.NativeSha256 + ".ico") return null;
                var path = Path.Combine(directory, frame.File); disk.Guard(path);
                if (!File.Exists(path) || verifyFrames && FileContentHash(path) != frame.FileSha256) return null;
            }
            return manifest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException) { return null; }
    }

    private async Task<PuzzleFrameResult?> TryLoadMemberIconAsync(WallMember member, string? entryPath, int edge, CancellationToken ct)
    {
        if (_disk is not { } disk || member.Entry is null || entryPath is null || _cacheStopping) return null;
        try
        {
            disk.Guard(entryPath);
            var sha = await Task.Run(() => FileContentHash(entryPath), ct);
            Task<AdoptedIconPackage?>? copy;
            lock (_workGate) _memberIconCopies.TryGetValue(member.Id, out copy);
            if (copy is not null && await copy.WaitAsync(ct) is { } adopted &&
                adopted.EntrySha256 == sha && StringComparer.OrdinalIgnoreCase.Equals(adopted.EntryPath, entryPath) &&
                adopted.Language == CultureInfo.CurrentUICulture.Name &&
                Fingerprint(adopted.TargetPath, "native-icon-v1:" + adopted.Language + ":" + NormalizePath(adopted.IconPath) + ":" + adopted.IconIndex) == adopted.Dependency)
                return await LoadCachedIconFrameCoreAsync(adopted.Directory, adopted.Key, edge,
                    member.Visual.BackgroundMode == MemberBackgroundMode.Blur, ct);
            Task<ManagedIconDescriptor> descriptorTask;
            lock (_workGate)
            {
                if (_cacheStopping || _disposed) return null;
                if (!_iconDescriptors.TryGetValue(sha, out descriptorTask!))
                {
                    descriptorTask = TrackCacheWork(Task.Run(() =>
                    {
                        var fields = Path.GetExtension(entryPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? _links.Read(entryPath) : null;
                        return new ManagedIconDescriptor(sha, fields?.TargetPath, fields?.IconPath, fields?.IconIndex);
                    }, _cacheLifetime.Token));
                    if (_iconDescriptors.Count >= 2048) _iconDescriptors.Clear();
                    _iconDescriptors.Add(sha, descriptorTask);
                }
            }
            var descriptor = await descriptorTask.WaitAsync(ct);
            var directory = disk.Under("Objects", member.Id, "IconCache");
            var key = await PrewarmPeIconsCoreAsync(entryPath, descriptor.Target, descriptor.Icon, descriptor.Index, descriptor.Sha, directory, ct);
            if (key is null) return null;
            return await LoadCachedIconFrameCoreAsync(directory, key, edge, member.Visual.BackgroundMode == MemberBackgroundMode.Blur, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or COMException) { return null; }
    }

    private Task<NativeIconPackage> NativePackageAsync(string exe, string? iconPath, int? iconIndex, string dependency)
    {
        lock (_workGate)
        {
            if (_cacheStopping || _disposed)
                return Task.FromCanceled<NativeIconPackage>(new CancellationToken(true));
            if (_nativePackages.TryGetValue(dependency, out var task)) return task;
            task = Task.Run(async () =>
            {
                await _nativeSlots.WaitAsync(_cacheLifetime.Token).ConfigureAwait(false);
                try
                {
                var read = _peReader.Read(exe); var group = read.Catalog?.SelectGroup(iconPath, iconIndex);
                var frames = new List<NativeIconFrame>();
                if (group is not null)
                    foreach (var frame in group.Frames)
                    {
                        _cacheLifetime.Token.ThrowIfCancellationRequested();
                        var prepared = await DecodeNativeFrameAsync(frame, group, _cacheLifetime.Token).ConfigureAwait(false);
                        if (prepared.Pixels is not null) frames.Add(new(frame, PeIconResourceReader.WrapSingleFrameIco(frame), prepared));
                    }
                var result = new NativeIconPackage(group, frames, group is not null || read.Status is PeIconReadStatus.NoIconGroups or PeIconReadStatus.NotExe or PeIconReadStatus.FileTooLarge);
                lock (_workGate)
                {
                    var completed = _nativePackages.Where(pair => pair.Key != dependency && pair.Value.IsCompletedSuccessfully).ToArray();
                    var bytes = completed.Sum(pair => pair.Value.Result.Frames.Sum(frame => (long)frame.Ico.Length + (frame.Decoded.Pixels?.LongLength ?? 0))) +
                        frames.Sum(frame => (long)frame.Ico.Length + (frame.Decoded.Pixels?.LongLength ?? 0));
                    foreach (var pair in completed)
                    { if (_nativePackages.Count <= 32 && bytes <= 64L * 1024 * 1024) break; _nativePackages.Remove(pair.Key); bytes -= pair.Value.Result.Frames.Sum(frame => (long)frame.Ico.Length + (frame.Decoded.Pixels?.LongLength ?? 0)); }
                }
                return result;
                }
                finally { _nativeSlots.Release(); }
            }, _cacheLifetime.Token);
            _nativePackages.Add(dependency, task); return TrackCacheWork(task);
        }
    }

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
            return new(WallImageStatus.Loaded, pixels, frame.Width, frame.Height, $"PE:{group.ResourceId}:{frame.ResourceId}:{frame.Width}x{frame.Height}");
        }
        catch (Exception ex) when (ex is COMException or IOException or ArgumentException or InvalidOperationException)
        { return new(WallImageStatus.Fallback, null, 0, 0, "PE:" + ex.GetType().Name); }
    }
}
