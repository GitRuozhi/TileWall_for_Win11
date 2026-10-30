using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using TileWall.Shell.Interop;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TileWall.Graphics;

public sealed partial class WallImageService
{
    private sealed record IconCandidateSeries(string Identity, int Priority, IReadOnlyList<Prepared> Frames,
        int Theme = 0, int Role = 0, bool CheckComposition = true);

    /// <summary>Freeze one selected image and every usable native resolution. No formal writes.</summary>
    public Task<PreparedAutomaticIconSeries?> PrepareAutomaticIconSeriesAsync(WallMember member, string? entryOrTarget,
        int width, int height, CancellationToken ct = default) => StartImageRequest<PreparedAutomaticIconSeries?>(token =>
        Task.Run(async () =>
        {
            if (member.ResourceOrigin == MemberResourceOrigin.Package || member.Images.Kind != MemberImageSourceKind.Automatic ||
                member.Images.IconSeries is not null) return null;
            var target = VerifiedPackageIconTarget(entryOrTarget, member);
            var before = AutomaticSourceStamp(entryOrTarget, target);
            var candidates = await CollectIconSeriesAsync(entryOrTarget, width, height, target, true, token).ConfigureAwait(false);
            var selected = SelectIconSeries(candidates, width, height);
            token.ThrowIfCancellationRequested();
            if (before != AutomaticSourceStamp(entryOrTarget, target) || target != VerifiedPackageIconTarget(entryOrTarget, member))
                throw new IOException("Icon source changed while preparing its series.");
            if (selected is null) return null;
            var assets = new List<PreparedMediaAsset>();
            var frames = new List<AutomaticIconFrame>();
            foreach (var frame in selected.Frames)
            {
                token.ThrowIfCancellationRequested();
                var asset = await EncodeIconFrameAsync(frame, token).ConfigureAwait(false);
                assets.Add(asset); frames.Add(new(asset.Reference, frame.Width, frame.Height));
            }
            var series = new AutomaticIconSeries(selected.Identity, frames.AsReadOnly(), 0,
                member.LocalBounds.Width, member.LocalBounds.Height);
            series = series with { SelectedFrame = AutomaticIconSeriesRules.SelectFrame(series, width, height) };
            return new PreparedAutomaticIconSeries(series, assets.AsReadOnly());
        }, token), ct, null);

    public Task<PuzzleFrameResult> LoadPreparedAutomaticIconFrameAsync(WallMember member,
        PreparedAutomaticIconSeries prepared, int width, int height, CancellationToken ct = default) =>
        StartImageRequest(async token =>
        {
            if (member.ResourceOrigin == MemberResourceOrigin.Package) return new(null, WallImageStatus.None);
            var series = member.Images.IconSeries ?? prepared.Series;
            var selected = series.Frames[series.SelectedFrame];
            var asset = prepared.Assets.FirstOrDefault(item => item.Reference == selected.Media);
            if (asset is null) return new(null, WallImageStatus.Invalid, "IconSeriesFrameUnavailable");
            // The selected native plane is kept whole. Display geometry never
            // changes which image/frame was confirmed by the editor.
            return await LoadPackageFrameCoreAsync(asset, selected.Width, selected.Height,
                member.Visual.BackgroundMode == MemberBackgroundMode.Blur, token);
        }, ct, new PuzzleFrameResult(null, WallImageStatus.Cancelled));

    private Task<PuzzleFrameResult> LoadSavedAutomaticIconFrameAsync(WallMember member, CancellationToken token)
    {
        var series = member.Images.IconSeries!;
        var frame = series.Frames[series.SelectedFrame];
        var budget = new PuzzleDecodeBudget(MemberImagePurpose.Content, frame.Width, frame.Height,
            true, int.MaxValue, 512, member.Visual.BackgroundMode == MemberBackgroundMode.Blur);
        return LoadImageFileFrameAsync(ManagedMediaPath(frame.Media), budget, token,
            "automatic-series:" + series.Identity, frame.Media.Sha256, frame.Media);
    }

    private static async Task<PreparedMediaAsset> EncodeIconFrameAsync(Prepared frame, CancellationToken token)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(token).ConfigureAwait(false);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            checked((uint)frame.Width), checked((uint)frame.Height), 96, 96, frame.Pixels!);
        await encoder.FlushAsync().AsTask(token).ConfigureAwait(false);
        stream.Seek(0);
        using var input = stream.AsStreamForRead();
        var bytes = new byte[checked((int)stream.Size)];
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return PreparedMediaAsset.FromVerifiedImage(bytes, ".png");
    }

    private static IconCandidateSeries? SelectIconSeries(IEnumerable<IconCandidateSeries> candidates, int width, int height)
    {
        // Every applicable source has already been collected. Admission is a
        // predicate, not a score; ordering remains explicit and deterministic.
        var admitted = new List<IconCandidateSeries>();
        foreach (var candidate in candidates)
        {
            var frames = candidate.Frames.Where(frame => frame.Pixels is not null &&
                    (!candidate.CheckComposition || IconCompositionRules.Accept(frame.Pixels, frame.Width, frame.Height)))
                .GroupBy(frame => (frame.Width, frame.Height)).Select(group => group.First())
                .OrderBy(frame => (long)frame.Width * frame.Height).ToArray();
            if (frames.Length == 0) continue;
            var anchor = frames[^1];
            // A one-pixel native rounding difference is allowed, not a change
            // of orientation or image aspect. Never mix unrelated PE groups.
            frames = frames.Where(frame => Math.Abs((long)frame.Width * anchor.Height - (long)frame.Height * anchor.Width)
                <= Math.Max(anchor.Width, anchor.Height)).ToArray();
            if (frames.Length != 0) admitted.Add(candidate with { Frames = frames });
        }
        return admitted.OrderBy(candidate => candidate.Priority).ThenBy(candidate => candidate.Theme)
            .ThenBy(candidate => candidate.Frames.Any(frame => frame.Width >= width && frame.Height >= height) ? 0 : 1)
            .ThenBy(candidate => candidate.Role).ThenBy(candidate => candidate.Identity, StringComparer.Ordinal).FirstOrDefault();
    }

    private async Task<List<IconCandidateSeries>> CollectIconSeriesAsync(string? source, int width, int height,
        string? verifiedPackage, bool followLinked, CancellationToken token)
    {
        var result = new List<IconCandidateSeries>();
        if (string.IsNullOrWhiteSpace(source)) return result;
        var entry = NormalizePath(source);
        var extension = entry is null ? string.Empty : Path.GetExtension(entry);
        string? target = entry ?? source, explicitIcon = null;
        int? explicitIndex = null;
        var nativeLink = false;
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var fields = _links.Read(entry!);
            target = string.IsNullOrWhiteSpace(fields.ShellParsingName) ? fields.TargetPath : fields.ShellParsingName;
            explicitIcon = fields.IconPath; explicitIndex = fields.IconIndex; nativeLink = fields.HasIdList;
        }
        else if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            (explicitIcon, explicitIndex) = ReadUrlIconLocation(entry!);
            target = TileWall.Core.Entries.UrlShortcut.ReadUrlLine(File.ReadAllBytes(entry!));
        }
        var targetFile = NormalizePath(target);
        var mmcTarget = targetFile is not null && IsMmcConsolePath(targetFile);
        var uri = targetFile is null && Uri.TryCreate(target, UriKind.Absolute, out var parsed) && !parsed.IsFile ? parsed : null;
        var web = uri?.Scheme is "http" or "https";
        var shellIdentity = uri is not null && !web && !extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? target! : source;
        var appsParsingName = AppsFolderIconParsingName(shellIdentity);
        // A URI registered as an AppsFolder instance can have its own factory/
        // IExtractIcon artwork while its generic system-list slot is a document.
        // Only the newly added virtual list path is ineligible for that input.
        var namespaceListAllowed = !IsAppsFolderIconSource(shellIdentity) ||
            appsParsingName is not null && !IsUriIconInstance(appsParsingName);
        var edge = Math.Max(width, height);
        token.ThrowIfCancellationRequested();
        if (NormalizePath(explicitIcon) is { } explicitFile)
            await AddPeAsync(explicitFile, explicitIndex ?? 0, 0);

        // A link's descriptive parsing name cannot replace verification of its
        // actual native target. Direct AppsFolder inputs keep their own identity.
        var packageTarget = verifiedPackage ?? (source.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)
            && PackagedAppIdentity(source) is not null ? source : null);
        if (packageTarget is not null)
            result.AddRange(await CollectPackagedIconSeriesAsync(packageTarget, 100, token).ConfigureAwait(false));
        if (targetFile is not null && Path.GetExtension(targetFile).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            await AddPeAsync(targetFile, null, 100);
        if (mmcTarget)
        {
            var mmc = ReadMmcIconSource(targetFile!);
            if (mmc.Declaration is { } declaration)
            {
                await AddPeAsync(declaration.Path, declaration.Index, 100);
                await Attempt(async () =>
                {
                    var native = TryPrepareMmcNativeIcon(declaration, width, height, token);
                    if (native is not null) AddSingle(native, "mmc:" + declaration.Path + ":" + declaration.Index, 110);
                    await Task.CompletedTask;
                });
            }
        }
        if (uri is not null && !web)
        {
            var facts = ProtocolAssociationReader(uri.Scheme);
            var location = ProtocolIconLocation(facts);
            if (location?.Path is { } protocolFile) await AddPeAsync(protocolFile, location.Index, 100);
            if (facts.AppUserModelId is { } appId)
                result.AddRange(await CollectPackagedIconSeriesAsync(appId, 110, token).ConfigureAwait(false));
            await Attempt(async () => AddSingle(await PrepareRegisteredProtocolAppIconAsync(uri.Scheme, width, height, token, facts),
                "protocol-logo:" + uri.Scheme + ":" + facts.AppUserModelId, 120));
            if (NormalizePath(facts.Executable) is { } exe) await AddPeAsync(exe, null, 130);
        }
        ShellItemCatalog.IconResourceLocation? locationFacts = null;
        await Attempt(async () => { locationFacts = ShellItemCatalog.IconLocation(shellIdentity, edge); await Task.CompletedTask; });
        // Only the handler's own opaque location is restricted by these flags.
        // A shortcut's separately persisted IconLocation remains an authority.
        if (!web && locationFacts is { CanReadResource: true, Path: not null } approved)
            await AddPeAsync(approved.Path, approved.Index, 140);

        var shellCacheAllowed = locationFacts?.CanCache != false;
        var shellPriority = web ? 50 : 300;
        if (!web || locationFacts is { Flags: var flags } && (flags & 2) != 0)
        {
            await Attempt(async () =>
            {
                if (ShellItemCatalog.Icon(shellIdentity, edge) is { } frame)
                    AddSingle(new(WallImageStatus.Loaded, frame.Pixels, frame.Width, frame.Height, "ShellImageFactory")
                        { CacheAllowed = shellCacheAllowed, MemoryCacheAllowed = shellCacheAllowed },
                        "shell-factory:" + shellIdentity, shellPriority);
                await Task.CompletedTask;
            });
            if (ShellIconPixels.IsNamespace(shellIdentity) && namespaceListAllowed || entry is not null && (File.Exists(entry) || Directory.Exists(entry)) ||
                targetFile is not null && (File.Exists(targetFile) || Directory.Exists(targetFile)))
                await Attempt(async () =>
                {
                    var path = ShellIconPixels.IsNamespace(shellIdentity) ? shellIdentity :
                        entry is not null && (File.Exists(entry) || Directory.Exists(entry)) ? entry : targetFile!;
                    var frames = ShellIconPixels.ReadSeries(path).Select(frame => new Prepared(WallImageStatus.Fallback,
                        frame.Pixels, frame.Width, frame.Height, "ClassicShell")
                        { CacheAllowed = shellCacheAllowed, MemoryCacheAllowed = shellCacheAllowed }).ToArray();
                    result.Add(new("shell-list:" + path, shellPriority + 10, frames));
                    await Task.CompletedTask;
                });
            await Attempt(async () =>
            {
                if (ShellItemCatalog.ExtractIcon(shellIdentity, edge, ShellIconPixels.ReadIcon) is { } frame)
                    AddSingle(new(WallImageStatus.Loaded, frame.Pixels, frame.Width, frame.Height, "ShellExtractIcon")
                        { CacheAllowed = shellCacheAllowed, MemoryCacheAllowed = shellCacheAllowed },
                        // An MSC handler resolves the console's own icon. The
                        // system image list can instead return a document plane
                        // for a private slot, without a reliable generic marker.
                        "shell-extract:" + shellIdentity, mmcTarget ? 130 : shellPriority + 20);
                await Task.CompletedTask;
            });
        }
        if (followLinked)
            await Attempt(async () =>
            {
                var linked = LinkedAutomaticIconTarget(source, nativeLink);
                // A direct target's native sources were already collected.
                if (linked is null || string.Equals(linked, targetFile, StringComparison.OrdinalIgnoreCase)) return;
                var related = await CollectIconSeriesAsync(linked, width, height,
                    VerifiedPackageIconTarget(linked, null), false, token).ConfigureAwait(false);
                result.AddRange(related.Select(item => item with { Priority = item.Priority + 400 }));
            });
        if (web && uri is not null)
        {
            await Attempt(async () => AddSingle(await TryPrepareBundledWebsiteAsync(uri, width, height, token), "website:" + uri.Host, 100));
            var browser = ProtocolAssociationReader(uri.Scheme);
            if (ProtocolIconLocation(browser)?.Path is { } icon) await AddPeAsync(icon, ProtocolIconLocation(browser)!.Index, 200);
            if (browser.AppUserModelId is { } appId)
                result.AddRange(await CollectPackagedIconSeriesAsync(appId, 210, token).ConfigureAwait(false));
            await Attempt(async () => AddSingle(await PrepareRegisteredProtocolAppIconAsync(uri.Scheme, width, height, token, browser),
                "browser-logo:" + browser.AppUserModelId, 220));
            if (NormalizePath(browser.Executable) is { } exe) await AddPeAsync(exe, null, 230);
            await Attempt(async () =>
            {
                var frames = ShellIconPixels.ReadSeries(".url", association: true).Select(frame =>
                    new Prepared(WallImageStatus.Fallback, frame.Pixels, frame.Width, frame.Height, "WebAssociation")).ToArray();
                result.Add(new("web-association", 400, frames)); await Task.CompletedTask;
            });
        }
        await Attempt(async () => AddSingle(await TryPrepareBundledSystemAsync(target, width, height, token), "system:" + target, 800));
        var generic = web ? BundledFallbackIcon.Website : targetFile is not null && Directory.Exists(targetFile)
            ? BundledFallbackIcon.Folder : targetFile is not null && File.Exists(targetFile) ? BundledFallbackIcon.File : BundledFallbackIcon.Unknown;
        await Attempt(async () => AddSingle(await TryPrepareBundledFallbackAsync(generic, width, height, token), "generic:" + generic, 900, false));
        return result;

        void AddSingle(Prepared? frame, string identity, int priority, bool composition = true)
        { if (frame?.Pixels is not null) result.Add(new(identity, priority, new[] { frame }, CheckComposition: composition)); }

        async Task AddPeAsync(string path, int? index, int priority)
        {
            await Attempt(async () =>
            {
                var before = PeIconResourceReader.ResourceDependencyStamp(path);
                var group = Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase)
                    ? ReadExplicitIco(path, index) : new PeIconResourceReader().Read(path, allowResourceModule: true).Catalog?
                        .SelectGroup(index is null ? null : path, index, fallbackToDefault: index is null);
                var frames = await DecodeIconGroupAsync(group, token).ConfigureAwait(false);
                if (group is not null && frames.Count != 0 && before == PeIconResourceReader.ResourceDependencyStamp(path))
                    result.Add(new("resource:" + path + ":" + group.ResourceId + ":" + group.ResourceName + ":" + group.LanguageId,
                        priority, frames.OrderByDescending(frame => frame.Native.BitsPerPixel).Select(frame => frame.Decoded).ToArray()));
            });
        }
        async Task Attempt(Func<Task> action)
        {
            token.ThrowIfCancellationRequested();
            try { await action().ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException or
                InvalidOperationException or OverflowException or NotSupportedException) { }
        }
    }
}
