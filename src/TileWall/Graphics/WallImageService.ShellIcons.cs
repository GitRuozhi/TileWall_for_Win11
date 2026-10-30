using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using TileWall.Core.Entries;
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
            if (stream.Size < 1)
                return new(WallImageStatus.Fallback, null, 0, 0, "NativeSearchIconEmpty") { CacheAllowed = false };
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token).ConfigureAwait(false);
            var actualWidth = checked((int)decoder.PixelWidth); var actualHeight = checked((int)decoder.PixelHeight);
            if (actualWidth < 1 || actualHeight < 1 || (long)actualWidth * actualHeight > Array.MaxLength / 4)
                return new(WallImageStatus.Fallback, null, 0, 0, "NativeSearchIconInvalidDimensions") { CacheAllowed = false };
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
    private readonly SemaphoreSlim _automaticAdmissions = new(256, 256);
    private static readonly TimeSpan VolatileIconMemoryLifetime = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, (string? Target, string? Icon, int? Index, bool ShellLink)> _automaticDescriptors = new(StringComparer.Ordinal);
    private sealed record AutomaticSourceSnapshot(string? Stamp, bool CacheAllowed, MmcIconSource? MmcSource,
        string? PackageTarget = null);
    private sealed record AutomaticDiskMapping(int Format, string Stamp, string PackageKey, string PixelsSha256,
        int Width, int Height, WallImageStatus Status, string? Diagnostic);

    private async Task<Prepared> PrepareAutomaticSharedAsync(string? source, int width, int height,
        AutomaticSourceSnapshot snapshot, CancellationToken token)
    {
        var key = ManagedImageCache.Key($"automatic-v9|{snapshot.Stamp}|{width}x{height}|BGRA8-premultiplied");
        if (snapshot.CacheAllowed && TryGetCached(key, out var cached)) return cached;
        if (!snapshot.CacheAllowed)
            return await StartUnsharedAutomaticAsync(source, width, height, snapshot, key, token).ConfigureAwait(false);
        Task<Prepared>? production;
        var ownsProduction = false;
        lock (_workGate)
        {
            if (_cacheStopping || _disposed) throw new OperationCanceledException(_cacheLifetime.Token);
            _automaticPrepared.TryGetValue(key, out production);
        }
        if (production is null)
        {
            await _automaticAdmissions.WaitAsync(token).ConfigureAwait(false);
            var releaseAdmission = true;
            try
            {
                lock (_workGate)
                {
                    if (_cacheStopping || _disposed) throw new OperationCanceledException(_cacheLifetime.Token);
                    token.ThrowIfCancellationRequested();
                    // Another caller may have published a producer or a ready
                    // frame while this request waited for admission.
                    if (!_automaticPrepared.TryGetValue(key, out production))
                    {
                        if (TryGetCached(key, out cached)) return cached;
                        production = ProduceAutomaticAsync(source, width, height, snapshot, key,
                            allowPixelCache: true, allowResultCache: true);
                        ownsProduction = true;
                        _automaticPrepared.Add(key, production);
                        TrackAutomaticProduction(production, key);
                        releaseAdmission = false;
                    }
                }
            }
            finally { if (releaseAdmission) _automaticAdmissions.Release(); }
        }
        // The service owns production. A caller only abandons its own wait.
        var result = await production!.WaitAsync(token).ConfigureAwait(false);
        if (ownsProduction || result.MemoryCacheAllowed) return result;
        token.ThrowIfCancellationRequested();
        // A native DONTCACHE result belongs only to its owning request.
        return await StartUnsharedAutomaticAsync(source, width, height, snapshot, key, token).ConfigureAwait(false);
    }

    private async Task<Prepared> StartUnsharedAutomaticAsync(string? source, int width, int height,
        AutomaticSourceSnapshot snapshot, string key, CancellationToken token)
    {
        // The request token already includes the service lifetime. Cancelling an
        // admission wait creates no producer; an admitted producer owns its work
        // independently until completion, even when this caller abandons its wait.
        await _automaticAdmissions.WaitAsync(token).ConfigureAwait(false);
        Task<Prepared> production;
        var releaseAdmission = true;
        try
        {
            lock (_workGate)
            {
                if (_cacheStopping || _disposed) throw new OperationCanceledException(_cacheLifetime.Token);
                token.ThrowIfCancellationRequested();
                production = ProduceAutomaticAsync(source, width, height, snapshot, key,
                    allowPixelCache: false, allowResultCache: false);
                TrackAutomaticProduction(production, null);
                releaseAdmission = false;
            }
        }
        finally { if (releaseAdmission) _automaticAdmissions.Release(); }
        return await production.WaitAsync(token).ConfigureAwait(false);
    }

    private void TrackAutomaticProduction(Task<Prepared> production, string? sharedKey)
    {
        TrackCacheWork(production);
        _ = production.ContinueWith(completed =>
        {
            if (sharedKey is not null)
                lock (_workGate)
                    if (_automaticPrepared.TryGetValue(sharedKey, out var current) && ReferenceEquals(current, completed))
                        _automaticPrepared.Remove(sharedKey);
            // Runs for success, failure and cancellation, including a Task.Run
            // cancelled before its body acquired an actual decode slot.
            _automaticAdmissions.Release();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private Task<Prepared> ProduceAutomaticAsync(string? source, int width, int height,
        AutomaticSourceSnapshot snapshot, string key, bool allowPixelCache, bool allowResultCache) => Task.Run(async () =>
    {
        var lifetime = _cacheLifetime.Token;
        await _automaticSlots.WaitAsync(lifetime).ConfigureAwait(false);
        try
        {
            var diskHit = allowPixelCache && snapshot.CacheAllowed ? ReadAutomaticPixels(key, snapshot.Stamp) : null;
            if (diskHit is not null && snapshot == AutomaticSourceStamp(source, snapshot.PackageTarget)) return diskHit;
            var prepared = await PrepareAutoCoreAsync(source, width, height, lifetime,
                snapshot.PackageTarget).ConfigureAwait(false);
            lifetime.ThrowIfCancellationRequested();
            if (snapshot != AutomaticSourceStamp(source, snapshot.PackageTarget))
                return new Prepared(WallImageStatus.Invalid, null, 0, 0, "IconSourceChanged") { CacheAllowed = false };
            prepared = prepared with
            {
                MemoryCacheAllowed = prepared.MemoryCacheAllowed && snapshot.CacheAllowed && allowResultCache,
                MemoryValidUntil = prepared.CacheAllowed ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow + VolatileIconMemoryLifetime,
            };
            if (allowPixelCache && prepared.Pixels is not null && prepared.MemoryCacheAllowed)
            {
                Cache(key, prepared, allowMemoryOnly: true);
                if (prepared.CacheAllowed)
                    SaveAutomaticPixels(key, snapshot.Stamp, width, height, prepared, lifetime);
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
                map.Width < 1 || map.Height < 1 || (long)map.Width * map.Height > Array.MaxLength / 4) return null;
            var directory = disk.Under("Cache", "Images", "Icons", "Packages", map.PackageKey);
            using var lease = disk.Lease(directory);
            var manifestBytes = disk.Read(Path.Combine(directory, "manifest.json"), 128 * 1024);
            var manifest = JsonSerializer.Deserialize<AutomaticDiskMapping>(manifestBytes, CacheJson);
            if (manifest is null || manifest.PackageKey != map.PackageKey || manifest.PixelsSha256 != map.PixelsSha256 ||
                manifest.Width != map.Width || manifest.Height != map.Height) return null;
            var pixels = disk.Read(Path.Combine(directory, map.PixelsSha256 + ".pixels"), checked(map.Width * map.Height * 4));
            if (pixels.Length != checked(map.Width * map.Height * 4) || Convert.ToHexString(SHA256.HashData(pixels)) != map.PixelsSha256) return null;
            return new(map.Status, pixels, map.Width, map.Height, map.Diagnostic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or OverflowException) { return null; }
    }

    private void SaveAutomaticPixels(string key, string? stamp, int requestedWidth, int requestedHeight,
        Prepared prepared, CancellationToken token)
    {
        if (_disk is not { } disk || prepared.Pixels is not { Length: > 0 } pixels) return;
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
        if (member?.ResourceOrigin == MemberResourceOrigin.Package) return new(null, WallImageStatus.None);
        if (member?.Images.IconSeries is not null) return await LoadSavedAutomaticIconFrameAsync(member, token);
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        try
        {
            var packageTarget = await Task.Run(() => VerifiedPackageIconTarget(source, member), token);
            var before = await Task.Run(() => AutomaticSourceStamp(source, packageTarget), token);
            if (packageTarget is not null && packageTarget != await Task.Run(() => VerifiedPackageIconTarget(source, member), token))
                return new(null, WallImageStatus.Invalid, "IconSourceChanged");
            var prepared = await PrepareAutomaticSharedAsync(source, width, height, before, token);
            token.ThrowIfCancellationRequested();
            if (before != await Task.Run(() => AutomaticSourceStamp(source, packageTarget), token)) return new(null, WallImageStatus.Invalid, "IconSourceChanged");
            if (packageTarget is not null && packageTarget != await Task.Run(() => VerifiedPackageIconTarget(source, member), token))
                return new(null, WallImageStatus.Invalid, "IconSourceChanged");
            if (prepared.Pixels is null) return new(null, prepared.Status, prepared.Diagnostic)
                { CacheAllowed = prepared.CacheAllowed, MemoryCacheAllowed = false };
            var sharp = new DecodedImageFrame(source ?? "automatic", 0, DateTime.MinValue, prepared.Width, prepared.Height,
                prepared.Width, prepared.Height, prepared.Pixels);
            var blurred = blur ? await Task.Run(() => LowResolutionBlur.Apply(BlurInput(sharp)), token) : null;
            token.ThrowIfCancellationRequested();
            return new(new PuzzleFrame(sharp.SourcePath, 0, DateTime.MinValue,
                new PixelSize(sharp.SourceWidth, sharp.SourceHeight), new PixelSize(sharp.Width, sharp.Height),
                blurred is null ? default : new PixelSize(blurred.Width, blurred.Height), MakeBitmap(sharp),
                blurred is null ? null : MakeBitmap(blurred))
                {
                    AutomaticStamp = before.Stamp, AutomaticPackageTarget = packageTarget, MemoryCacheAllowed = prepared.MemoryCacheAllowed,
                    MemoryValidUntil = prepared.MemoryValidUntil,
                }, prepared.Status, prepared.Diagnostic)
                { CacheAllowed = prepared.CacheAllowed, MemoryCacheAllowed = prepared.MemoryCacheAllowed };
        }
        catch (OperationCanceledException) { return new(null, WallImageStatus.Cancelled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException or InvalidOperationException or OverflowException)
        { return new(null, WallImageStatus.Invalid, ex.GetType().Name); }
    }

    public Task<bool> IsAutomaticFrameCurrentAsync(PuzzleFrame frame, string? entryOrTargetPath,
        CancellationToken ct = default, bool heldByCurrentWall = false) => StartImageRequest(token => Task.Run(() =>
    {
        try
        {
            if (!frame.MemoryCacheAllowed || frame.AutomaticStamp is null ||
                !heldByCurrentWall && frame.MemoryValidUntil <= DateTimeOffset.UtcNow) return false;
            var current = AutomaticSourceStamp(entryOrTargetPath, frame.AutomaticPackageTarget);
            return (heldByCurrentWall || current.CacheAllowed) && frame.AutomaticStamp == current.Stamp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException or InvalidOperationException or NotSupportedException)
        { return false; }
    }, token), ct, false);

    private AutomaticSourceSnapshot AutomaticSourceStamp(string? source, string? packageTarget = null)
    {
        var snapshot = AutomaticSourceStampCore(source, followLinkedTarget: true);
        return packageTarget is null ? snapshot : snapshot with
        {
            PackageTarget = packageTarget,
            Stamp = snapshot.Stamp + "|verified-package-app-list-v2|" + packageTarget + "|" + PackageRegistrationStamp(packageTarget),
        };
    }

    private AutomaticSourceSnapshot AutomaticSourceStampCore(string? source, bool followLinkedTarget)
    {
        var path = NormalizePath(source);
        string? target = source, explicitIcon = null; int? iconIndex = null;
        var entryStamp = source;
        var shellLink = false;
        if (path is not null && File.Exists(path))
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                if (new FileInfo(path).Length > EntryObservation.MaximumBytes) throw new IOException("Automatic shortcut is oversized.");
                entryStamp = FileContentHash(path);
                if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    (string? Target, string? Icon, int? Index, bool ShellLink) descriptor;
                    lock (_workGate)
                    {
                        if (!_automaticDescriptors.TryGetValue(entryStamp, out descriptor))
                        {
                            var fields = _links.Read(path);
                            descriptor = (!string.IsNullOrWhiteSpace(fields.ShellParsingName) ? fields.ShellParsingName : fields.TargetPath,
                                fields.IconPath, fields.IconIndex, fields.HasIdList);
                            if (_automaticDescriptors.Count >= 2048) _automaticDescriptors.Clear();
                            _automaticDescriptors[entryStamp] = descriptor;
                        }
                    }
                    (target, explicitIcon, iconIndex, shellLink) = descriptor;
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
        var appsParsingName = AppsFolderIconParsingName(source);
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
        var mmc = canonical is not null && IsMmcConsolePath(canonical)
            ? ReadMmcIconSource(canonical) : null;
        // Flags belong to the dependency snapshot before cache lookup as well as
        // to newly collected Shell pixels. This asks for location metadata only;
        // the minimum native argument does not extract or resize an image.
        ShellItemCatalog.IconResourceLocation? shellPolicy = null;
        try { if (source is not null) shellPolicy = ShellItemCatalog.IconLocation(source, 1); }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        var stamp = string.Join("|", "automatic-v10", source, entryStamp, target,
            canonical is null ? null : Fingerprint(canonical, "target"), Fingerprint(explicitIcon, "explicit") ?? explicitIcon,
            iconIndex, association, AssociationResourceStamp(associationIcon), Fingerprint(associationExecutable, "association-executable"),
            PeIconResourceReader.ResourceDependencyStamp(explicitIcon),
            canonical is not null && Path.GetExtension(canonical).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? PeIconResourceReader.ResourceDependencyStamp(canonical) : null,
            PeIconResourceReader.ResourceDependencyStamp(associationExecutable),
            shellPolicy is { CanReadResource: true, Path: not null } ? PeIconResourceReader.ResourceDependencyStamp(shellPolicy.Path) : null,
            canonical is not null && Directory.Exists(canonical) ? Fingerprint(Path.Combine(canonical, "desktop.ini"), "folder-customization") : null,
            System.Globalization.CultureInfo.CurrentUICulture.Name, Environment.OSVersion.VersionString,
            TileWall.Core.Imaging.WebsiteIconCatalog.VersionStamp, TileWall.Core.Imaging.SystemIconCatalog.VersionStamp,
            shellPolicy?.Flags, shellPolicy?.Path, shellPolicy?.Index);
        if (mmc is not null) stamp += "|mmc-icon-v2|" + mmc.Stamp;
        var packagedApp = PackagedAppIdentity(target) is not null;
        var shellIconContext = (path is not null && Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) ||
            source?.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) == true ||
            target?.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) == true ||
            source?.StartsWith("::{", StringComparison.Ordinal) == true ||
            target?.StartsWith("::{", StringComparison.Ordinal) == true || packagedApp;
        if (shellIconContext)
            stamp += "|shell-icon-context-v4|apps-list-parsing=" + appsParsingName;
        if (packagedApp) stamp += "|package-app-list-v2|" + PackageRegistrationStamp(target);
        if (followLinkedTarget && LinkedAutomaticIconTarget(source, shellLink) is { } linked &&
            !string.Equals(linked, canonical, StringComparison.OrdinalIgnoreCase))
            stamp += "|linked-icon-target-v2|" + AutomaticSourceStampCore(linked, followLinkedTarget: false).Stamp;
        return new(stamp, (mmc?.CacheAllowed ?? true) && (!IsAppsFolderIconSource(source) || appsParsingName is not null), mmc);
    }

    private static bool IsAppsFolderIconSource(string? source) =>
        source?.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase) == true;

    private static string? AppsFolderIconParsingName(string? source)
    {
        if (!IsAppsFolderIconSource(source)) return null;
        try { return ShellItemCatalog.Read(source!).ParsingName; }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { return null; }
    }

    private static bool IsUriIconInstance(string parsingName) => Uri.TryCreate(parsingName, UriKind.Absolute, out var uri) &&
        !uri.IsFile && !uri.Scheme.Equals("shell", StringComparison.OrdinalIgnoreCase);

    private static string? LinkedAutomaticIconTarget(string? source, bool nativeLink)
    {
        if (source is null || !nativeLink && !source.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return null;
        // This path is a fact supplied by the original Shell item, not a path
        // inferred from a display name or a command line. Follow it only once.
        var linked = NormalizePath(ShellItemCatalog.LinkedTargetPath(source));
        return linked is not null && !string.Equals(linked, NormalizePath(source), StringComparison.OrdinalIgnoreCase) &&
            Path.GetExtension(linked).ToLowerInvariant() is ".lnk" or ".url" or ".exe" && File.Exists(linked)
                ? linked : null;
    }

    private static string? AssociationResourceStamp(string? descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor)) return null;
        var value = Environment.ExpandEnvironmentVariables(descriptor.Trim());
        var comma = value.LastIndexOf(',');
        if (comma >= 0 && int.TryParse(value[(comma + 1)..].Trim(), out _)) value = value[..comma];
        var path = NormalizePath(value.Trim().Trim('"'));
        return path is null ? descriptor : PeIconResourceReader.ResourceDependencyStamp(path);
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
