using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Exchange;
using TileWall.Core.Imaging;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Shell.Interop;
using Windows.Graphics.Imaging;

namespace TileWall.Graphics;

public sealed partial class WallImageService
{
    internal sealed record ProtocolAssociationFacts(string? ProgId, string? AppUserModelId,
        string? Executable, string? DefaultIcon);

    // A single metadata boundary keeps scheme identity and the resource used by
    // preparation consistent. Tests can replace this reader without changing a
    // user's protocol registrations or the image preparation pipeline.
    internal Func<string, ProtocolAssociationFacts> ProtocolAssociationReader { get; set; } =
        scheme => new(ShellItemCatalog.AssociationValue(scheme, true, 20),
            ShellItemCatalog.ProtocolAppUserModelId(scheme), ShellItemCatalog.AssociationValue(scheme, true, 2),
            ShellItemCatalog.AssociationValue(scheme, true, 15));

    private static string ProtocolAssociationIdentity(string scheme, ProtocolAssociationFacts facts) =>
        string.Join("|", scheme.ToLowerInvariant(), facts.ProgId, facts.AppUserModelId,
            facts.Executable, facts.DefaultIcon);

    private static string ProtocolAssociationStamp(string scheme, ProtocolAssociationFacts facts) =>
        string.Join("|", ProtocolAssociationIdentity(scheme, facts), AssociationResourceStamp(facts.DefaultIcon),
            Fingerprint(facts.Executable, "association-executable"));

    private static ShellItemCatalog.IconResourceLocation? ProtocolIconLocation(ProtocolAssociationFacts facts)
    {
        if (string.IsNullOrWhiteSpace(facts.DefaultIcon)) return null;
        var text = Environment.ExpandEnvironmentVariables(facts.DefaultIcon.Trim());
        var comma = text.LastIndexOf(',');
        var index = 0;
        if (comma >= 0 && !int.TryParse(text[(comma + 1)..].Trim(), out index)) return null;
        var path = NormalizePath((comma >= 0 ? text[..comma] : text).Trim().Trim('"'));
        return path is null ? null : new(path, index, 0, true);
    }

    private async Task<Prepared> PrepareRegisteredProtocolAppIconAsync(string scheme, int width, int height,
        CancellationToken token, ProtocolAssociationFacts? association = null)
    {
        try
        {
            // A list containing one packaged handler does not prove it is the
            // effective default; a Win32 default may not be in that list.
            if (association is not null && string.IsNullOrWhiteSpace(association.AppUserModelId))
                return new(WallImageStatus.Fallback, null, 0, 0, "ProtocolDefaultAppIdUnavailable");
            // This enumerates metadata only. It neither launches the handler nor
            // guesses an executable, package folder, AUMID or resource index.
            var handlers = await Windows.System.Launcher.FindUriSchemeHandlersAsync(scheme).AsTask(token).ConfigureAwait(false);
            var defaultId = association is null ? ShellItemCatalog.ProtocolAppUserModelId(scheme) : association.AppUserModelId;
            var app = defaultId is not null
                ? handlers.FirstOrDefault(handler => string.Equals(handler.AppUserModelId, defaultId, StringComparison.OrdinalIgnoreCase))
                : handlers.Count == 1 ? handlers[0] : null;
            if (app is null) return new(WallImageStatus.Fallback, null, 0, 0, "NativeSearchIconHandlerUnavailable") { CacheAllowed = false };
            var logo = app.DisplayInfo.GetLogo(new Windows.Foundation.Size(width, height));
            using var stream = await logo.OpenReadAsync().AsTask(token).ConfigureAwait(false);
            if (stream.Size is < 1 or > 32 * 1024 * 1024)
                return new(WallImageStatus.Fallback, null, 0, 0, "NativeSearchIconBudget") { CacheAllowed = false };
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token).ConfigureAwait(false);
            var actualWidth = checked((int)decoder.PixelWidth); var actualHeight = checked((int)decoder.PixelHeight);
            if (actualWidth is < 1 or > 4096 || actualHeight is < 1 or > 4096 || (long)actualWidth * actualHeight > 16_777_216)
                return new(WallImageStatus.Fallback, null, 0, 0, "NativeSearchIconBudget") { CacheAllowed = false };
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(token).ConfigureAwait(false);
            var pixels = data.DetachPixelData();
            token.ThrowIfCancellationRequested();
            if (pixels.Length != checked(actualWidth * actualHeight * 4) ||
                (association is null ? defaultId != ShellItemCatalog.ProtocolAppUserModelId(scheme) :
                    ProtocolAssociationStamp(scheme, association) != ProtocolAssociationStamp(scheme, ProtocolAssociationReader(scheme))))
                return new(WallImageStatus.Fallback, null, 0, 0, "NativeSearchIconChanged") { CacheAllowed = false };
            var visible = false;
            for (var index = 3; index < pixels.Length; index += 4) visible |= pixels[index] != 0;
            if (!visible) return new(WallImageStatus.Fallback, null, 0, 0, "ProtocolAppLogoTransparent") { CacheAllowed = false };
            return new(WallImageStatus.Loaded, pixels, actualWidth, actualHeight,
                $"ProtocolAppLogo:{scheme}:{app.AppUserModelId}:{actualWidth}x{actualHeight}:requested={width}x{height}") { CacheAllowed = false };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException)
        { return new(WallImageStatus.Fallback, null, 0, 0, "NativeSearchIcon:" + ex.GetType().Name) { CacheAllowed = false }; }
    }

    public Task<PuzzleFrameResult> LoadAutomaticFrameAsync(string? entryOrTargetPath, int widthPixels, int heightPixels,
        bool blur, CancellationToken ct = default) => StartImageRequest(token => LoadAutomaticFrameCoreAsync(
            entryOrTargetPath, widthPixels, heightPixels, blur, token), ct, new PuzzleFrameResult(null, WallImageStatus.Cancelled));

    private readonly Dictionary<string, Task<Prepared>> _automaticPrepared = new(StringComparer.Ordinal);
    private int _automaticUnsharedWork;
    private readonly Dictionary<string, (string? Target, string? Icon, int? Index)> _automaticDescriptors = new(StringComparer.Ordinal);
    private sealed record AutomaticDiskMapping(int Format, string Stamp, string PackageKey, string PixelsSha256,
        int Width, int Height, WallImageStatus Status, string? Diagnostic);

    private async Task<Prepared> PrepareAutomaticSharedAsync(string? source, int width, int height, string? stamp,
        CancellationToken token)
    {
        var key = ManagedImageCache.Key($"automatic-v6|{stamp}|{width}x{height}|BGRA8-premultiplied");
        if (TryGetCached(key, out var cached)) return cached;
        Task<Prepared> production;
        var ownsProduction = false;
        lock (_workGate)
        {
            if (_cacheStopping || _disposed) throw new OperationCanceledException(_cacheLifetime.Token);
            if (!_automaticPrepared.TryGetValue(key, out production!))
            {
                if (_automaticPrepared.Count >= 256) return new(WallImageStatus.Fallback, null, 0, 0, "IconWorkBudget");
                production = ProduceAutomaticAsync(source, width, height, stamp, key, allowPixelCache: true);
                ownsProduction = true;
                _automaticPrepared.Add(key, production);
                TrackCacheWork(production);
                _ = production.ContinueWith(_ => { lock (_workGate) _automaticPrepared.Remove(key); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        // The service owns production. A caller only abandons its own wait.
        var result = await production.WaitAsync(token).ConfigureAwait(false);
        if (ownsProduction || result.CacheAllowed) return result;
        token.ThrowIfCancellationRequested();
        // The first observation establishes handler policy. An uncacheable
        // result belongs to that request, including during concurrent loading.
        return await StartUnsharedAutomaticAsync(source, width, height, stamp, key).WaitAsync(token).ConfigureAwait(false);
    }

    private Task<Prepared> StartUnsharedAutomaticAsync(string? source, int width, int height, string? stamp, string key)
    {
        lock (_workGate)
        {
            if (_cacheStopping || _disposed) return Task.FromCanceled<Prepared>(new CancellationToken(true));
            if (_automaticUnsharedWork >= 256)
                return Task.FromResult(new Prepared(WallImageStatus.Fallback, null, 0, 0, "IconWorkBudget") { CacheAllowed = false });
            _automaticUnsharedWork++;
            var task = ProduceAutomaticAsync(source, width, height, stamp, key, allowPixelCache: false);
            TrackCacheWork(task);
            _ = task.ContinueWith(_ => { lock (_workGate) _automaticUnsharedWork--; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private Task<Prepared> ProduceAutomaticAsync(string? source, int width, int height, string? stamp,
        string key, bool allowPixelCache) => Task.Run(async () =>
    {
        var lifetime = _cacheLifetime.Token;
        await _automaticSlots.WaitAsync(lifetime).ConfigureAwait(false);
        try
        {
            var diskHit = allowPixelCache ? ReadAutomaticPixels(key, stamp) : null;
            if (diskHit is not null && stamp == AutomaticSourceStamp(source)) return diskHit;
            var prepared = await PrepareAutoAsync(source, width, height, lifetime).ConfigureAwait(false);
            lifetime.ThrowIfCancellationRequested();
            if (stamp != AutomaticSourceStamp(source))
                return new Prepared(WallImageStatus.Invalid, null, 0, 0, "IconSourceChanged") { CacheAllowed = false };
            if (allowPixelCache && prepared.Pixels is not null && prepared.CacheAllowed)
            {
                Cache(key, prepared);
                SaveAutomaticPixels(key, stamp, width, height, prepared, lifetime);
            }
            return prepared;
        }
        finally { _automaticSlots.Release(); }
    }, _cacheLifetime.Token);

    private Prepared? ReadAutomaticPixels(string key, string? stamp)
    {
        if (_disk is not { } disk) return null;
        try
        {
            var map = JsonSerializer.Deserialize<AutomaticDiskMapping>(disk.Read(disk.Under("Cache", "Images", "Icons", "Mappings", key + ".json"), 128 * 1024), CacheJson);
            if (map is null || map.Format != 6 || map.Stamp != stamp || map.PackageKey is not { Length: 64 } ||
                !map.PackageKey.All(Uri.IsHexDigit) || map.PixelsSha256 is not { Length: 64 } || !map.PixelsSha256.All(Uri.IsHexDigit) ||
                map.Width is < 1 or > 4096 || map.Height is < 1 or > 4096 || (long)map.Width * map.Height > 8_000_000) return null;
            var directory = disk.Under("Cache", "Images", "Icons", "Packages", map.PackageKey);
            using var lease = disk.Lease(directory);
            var manifestBytes = disk.Read(Path.Combine(directory, "manifest.json"), 128 * 1024);
            var manifest = JsonSerializer.Deserialize<AutomaticDiskMapping>(manifestBytes, CacheJson);
            if (manifest is null || manifest.PackageKey != map.PackageKey || manifest.PixelsSha256 != map.PixelsSha256 ||
                manifest.Width != map.Width || manifest.Height != map.Height) return null;
            var pixels = disk.Read(Path.Combine(directory, map.PixelsSha256 + ".pixels"), 32 * 1024 * 1024);
            if (pixels.Length != checked(map.Width * map.Height * 4) || Convert.ToHexString(SHA256.HashData(pixels)) != map.PixelsSha256) return null;
            return new(map.Status, pixels, map.Width, map.Height, map.Diagnostic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or OverflowException) { return null; }
    }

    private void SaveAutomaticPixels(string key, string? stamp, int requestedWidth, int requestedHeight,
        Prepared prepared, CancellationToken token)
    {
        if (_disk is not { } disk || prepared.Pixels is not { Length: > 0 and <= 32 * 1024 * 1024 } pixels) return;
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(pixels));
            // Actual dimensions and pixel format are part of the shared payload identity.
            // The mapping key separately includes the requested dimensions and dependency facts.
            var packageKey = ManagedImageCache.Key($"automatic-plane-v6|requested={requestedWidth}x{requestedHeight}|" +
                $"actual={prepared.Width}x{prepared.Height}|BGRA8-premultiplied|{hash}");
            var manifest = new AutomaticDiskMapping(6, null!, packageKey, hash, prepared.Width, prepared.Height, prepared.Status, null);
            var mapping = manifest with { Stamp = stamp!, Diagnostic = prepared.Diagnostic };
            disk.PublishIconPackage(packageKey, new Dictionary<string, byte[]>
            {
                [hash + ".pixels"] = pixels,
                ["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, CacheJson),
            }, key, JsonSerializer.SerializeToUtf8Bytes(mapping, CacheJson), NativeIconLimit, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    private async Task<PuzzleFrameResult> LoadAutomaticFrameCoreAsync(string? source, int width, int height, bool blur,
        CancellationToken token, WallMember? member = null)
    {
        if (width is < 1 or > 4096 || height is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(width));
        try
        {
            var before = await Task.Run(() => AutomaticSourceStamp(source), token);
            var prepared = await PrepareAutomaticSharedAsync(source, width, height, before, token);
            token.ThrowIfCancellationRequested();
            if (before != await Task.Run(() => AutomaticSourceStamp(source), token)) return new(null, WallImageStatus.Invalid, "IconSourceChanged");
            if (prepared.Pixels is null) return new(null, prepared.Status, prepared.Diagnostic) { CacheAllowed = prepared.CacheAllowed };
            var sharp = new DecodedImageFrame(source ?? "automatic", 0, DateTime.MinValue, prepared.Width, prepared.Height,
                prepared.Width, prepared.Height, prepared.Pixels);
            var blurred = blur ? await Task.Run(() => LowResolutionBlur.Apply(BlurInput(sharp)), token) : null;
            token.ThrowIfCancellationRequested();
            return new(new PuzzleFrame(sharp.SourcePath, 0, DateTime.MinValue,
                new PixelSize(sharp.SourceWidth, sharp.SourceHeight), new PixelSize(sharp.Width, sharp.Height),
                blurred is null ? default : new PixelSize(blurred.Width, blurred.Height), MakeBitmap(sharp),
                blurred is null ? null : MakeBitmap(blurred)), prepared.Status, prepared.Diagnostic) { CacheAllowed = prepared.CacheAllowed };
        }
        catch (OperationCanceledException) { return new(null, WallImageStatus.Cancelled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException or InvalidOperationException or OverflowException)
        { return new(null, WallImageStatus.Invalid, ex.GetType().Name); }
    }

    private string? AutomaticSourceStamp(string? source)
    {
        var path = NormalizePath(source);
        string? target = source, explicitIcon = null; int? iconIndex = null;
        var entryStamp = source;
        if (path is not null && File.Exists(path))
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                if (new FileInfo(path).Length > EntryObservation.MaximumBytes) throw new IOException("Automatic shortcut is oversized.");
                entryStamp = FileContentHash(path);
                if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    (string? Target, string? Icon, int? Index) descriptor;
                    lock (_workGate)
                    {
                        if (!_automaticDescriptors.TryGetValue(entryStamp, out descriptor))
                        {
                            var fields = _links.Read(path);
                            descriptor = (!string.IsNullOrWhiteSpace(fields.ShellParsingName) ? fields.ShellParsingName : fields.TargetPath,
                                fields.IconPath, fields.IconIndex);
                            if (_automaticDescriptors.Count >= 2048) _automaticDescriptors.Clear();
                            _automaticDescriptors[entryStamp] = descriptor;
                        }
                    }
                    (target, explicitIcon, iconIndex) = descriptor;
                }
                else
                {
                    target = UrlShortcut.ReadUrlLine(File.ReadAllBytes(path));
                    (explicitIcon, iconIndex) = ReadUrlIconLocation(path);
                }
            }
            else entryStamp = Fingerprint(path, "automatic-source");
        }
        var canonical = NormalizePath(target);
        var uri = canonical is null && Uri.TryCreate(target, UriKind.Absolute, out var parsed) && !parsed.IsFile ? parsed : null;
        string? associationIcon = null, associationExecutable = null;
        string association;
        if (uri is not null)
        {
            var facts = ProtocolAssociationReader(uri.Scheme);
            associationIcon = facts.DefaultIcon;
            associationExecutable = facts.Executable;
            association = ProtocolAssociationIdentity(uri.Scheme, facts);
        }
        else if (canonical is not null && !Directory.Exists(canonical))
        {
            var extension = Path.GetExtension(canonical);
            associationIcon = ShellItemCatalog.AssociationValue(extension, false, 15);
            association = ShellItemCatalog.AssociationValue(extension, false, 20) + "|" + associationIcon;
        }
        else association = "folder";
        return string.Join("|", "automatic-v6", source, entryStamp, target,
            canonical is null ? null : Fingerprint(canonical, "target"), Fingerprint(explicitIcon, "explicit") ?? explicitIcon,
            iconIndex, association, AssociationResourceStamp(associationIcon), Fingerprint(associationExecutable, "association-executable"),
            canonical is not null && Directory.Exists(canonical) ? Fingerprint(Path.Combine(canonical, "desktop.ini"), "folder-customization") : null,
            System.Globalization.CultureInfo.CurrentUICulture.Name, Environment.OSVersion.VersionString,
            TileWall.Core.Imaging.WebsiteIconCatalog.VersionStamp, TileWall.Core.Imaging.SystemIconCatalog.VersionStamp);
    }

    private static string? AssociationResourceStamp(string? descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor)) return null;
        var value = Environment.ExpandEnvironmentVariables(descriptor.Trim());
        var comma = value.LastIndexOf(',');
        if (comma >= 0 && int.TryParse(value[(comma + 1)..].Trim(), out _)) value = value[..comma];
        var path = NormalizePath(value.Trim().Trim('"'));
        return path is null ? descriptor : Fingerprint(path, "association-resource") ?? "missing:" + path;
    }

    private async Task<Prepared> PrepareDefaultBrowserIconAsync(string scheme, int width, int height, CancellationToken token,
        bool cacheAllowed = true)
    {
        var facts = ProtocolAssociationReader(scheme);
        var identity = ProtocolAssociationIdentity(scheme, facts);
        var stamp = ProtocolAssociationStamp(scheme, facts);
        var location = ProtocolIconLocation(facts);
        if (location is { CanReadResource: true, Path: not null })
        {
            var prepared = await PreparePeIconAsync(location.Path, location.Path, location.Index, scheme, width, height, token, cacheAllowed).ConfigureAwait(false);
            if (stamp != ProtocolAssociationStamp(scheme, ProtocolAssociationReader(scheme)))
                return new(WallImageStatus.Fallback, null, 0, 0, "ProtocolAssociationChanged") { CacheAllowed = false };
            if (prepared.Pixels is not null) return prepared with { Diagnostic = "DefaultBrowser:" + scheme + ":" + identity + ":" + prepared.Diagnostic };
            cacheAllowed &= prepared.CacheAllowed;
        }
        var app = !string.IsNullOrWhiteSpace(facts.AppUserModelId)
            ? await PrepareRegisteredProtocolAppIconAsync(scheme, width, height, token, facts).ConfigureAwait(false)
            : null;
        if (stamp != ProtocolAssociationStamp(scheme, ProtocolAssociationReader(scheme)))
            return new(WallImageStatus.Fallback, null, 0, 0, "ProtocolAssociationChanged") { CacheAllowed = false };
        if (app?.Pixels is not null) return app;
        if (app is not null) cacheAllowed &= app.CacheAllowed;
        var executable = NormalizePath(facts.Executable);
        if (executable is not null)
        {
            var prepared = await PreparePeIconAsync(executable, null, null, scheme, width, height, token, cacheAllowed).ConfigureAwait(false);
            if (stamp == ProtocolAssociationStamp(scheme, ProtocolAssociationReader(scheme))) return prepared;
        }
        return new(WallImageStatus.Fallback, null, 0, 0, "DefaultBrowserUnavailable") { CacheAllowed = cacheAllowed };
    }
    internal Task<PuzzleFrameResult> LoadPackageIconFrameAsync(VerifiedPackageEntry entry, int width, int height,
        bool blur, CancellationToken ct = default) => StartImageRequest(token => LoadPackageIconFrameCoreAsync(entry,
            width, height, blur, token), ct, new PuzzleFrameResult(null, WallImageStatus.Cancelled));

    private async Task<PuzzleFrameResult> LoadPackageIconFrameCoreAsync(VerifiedPackageEntry entry, int width, int height,
        bool blur, CancellationToken token)
    {
        if (_disk is not { } disk) return new(null, WallImageStatus.Invalid, "ImageStorageUnavailable");
        string? path = null;
        FileStream? hold = null;
        var created = false;
        try
        {
            token.ThrowIfCancellationRequested();
            path = disk.Under("Cache", "Images", "Temp", Guid.NewGuid().ToString("N") + (entry.Kind == ManagedEntryKind.Lnk ? ".lnk" : ".url"));
            var capturedPath = path;
            await Task.Run(async () =>
            {
                disk.CreateDirectory(Path.GetDirectoryName(capturedPath)!); disk.Guard(capturedPath);
                using (var output = new LocalFileStore().CreateNewStream(capturedPath))
                {
                    created = true;
                    await output.WriteAsync(entry.CopyBytes(), token).ConfigureAwait(false);
                    await output.FlushAsync(token).ConfigureAwait(false);
                }
                disk.Guard(capturedPath);
                hold = new FileStream(capturedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (hold.Length != entry.Length || Convert.ToHexString(SHA256.HashData(hold)) != entry.Sha256)
                    throw new IOException("Verified package icon entry changed during preparation.");
                hold.Position = 0;
            }, token);
            var result = await LoadAutomaticFrameCoreAsync(path, width, height, blur, token);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) { return new(null, WallImageStatus.Cancelled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException or InvalidOperationException)
        { return new(null, WallImageStatus.Invalid, ex.GetType().Name); }
        finally
        {
            hold?.Dispose();
            if (created && path is not null)
            {
                try { disk.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("ImageIcon", $"Package icon temporary cleanup failed: {ex.GetType().Name}"); }
            }
        }
    }

    // Downsample only the blur underfill; the visible sharp plane retains its
    // actual Shell/PE dimensions, alpha, design padding and aspect ratio.
    private static DecodedImageFrame BlurInput(DecodedImageFrame source)
    {
        if (source.Width <= 512 && source.Height <= 512) return source;
        var scale = Math.Min(512d / source.Width, 512d / source.Height);
        var width = Math.Max(1, (int)Math.Floor(source.Width * scale));
        var height = Math.Max(1, (int)Math.Floor(source.Height * scale));
        var pixels = new byte[width * height * 4];
        var input = source.BgraPremultiplied.Span;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var left = x * source.Width / width; var right = Math.Max(left + 1, (x + 1) * source.Width / width);
            var top = y * source.Height / height; var bottom = Math.Max(top + 1, (y + 1) * source.Height / height);
            for (var channel = 0; channel < 4; channel++)
            {
                long sum = 0;
                for (var row = top; row < bottom; row++)
                for (var column = left; column < right; column++) sum += input[(row * source.Width + column) * 4 + channel];
                pixels[(y * width + x) * 4 + channel] = (byte)(sum / ((right - left) * (bottom - top)));
            }
        }
        return source with { Width = width, Height = height, BgraPremultiplied = pixels };
    }
}
