using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TileWall.Core.Imaging;
using TileWall.Core.Wall;

namespace TileWall.Graphics;

public sealed record PuzzleCandidateResult(IReadOnlyList<string> Paths,
    WallImageStatus Status, string? Diagnostic = null)
{
    public string? SourceVersion { get; init; }
    public IReadOnlySet<string> ChangedPaths { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool IsComplete { get; init; } = true;
    public string? DiagnosticDetail { get; init; }
}

public sealed record PuzzleFrameResult(PuzzleFrame? Frame,
    WallImageStatus Status, string? Diagnostic = null)
{
    /// <summary>Whether this result may be reused in the current display session.</summary>
    public bool MemoryCacheAllowed { get; init; } = true;
    /// <summary>Whether dependency facts permit reuse from persistent icon caches.</summary>
    public bool CacheAllowed { get; init; } = true;
}

public sealed record PuzzleFrame(string CanonicalPath, long SourceBytes, DateTime LastWriteUtc,
    PixelSize SourcePixels, PixelSize SharpDecodedPixels, PixelSize BlurDecodedPixels,
    ImageSource Sharp, ImageSource? BlurredUnderfill)
{
    public bool MemoryCacheAllowed { get; init; } = true;
    internal string? AutomaticStamp { get; init; }
    internal string? AutomaticPackageTarget { get; init; }
    internal DateTimeOffset MemoryValidUntil { get; init; } = DateTimeOffset.MaxValue;
}

public enum MemberImagePurpose { Content, Thumbnail }

public sealed record TargetImageSourceResult(MemberImageSource Source, PuzzleCandidateResult? Candidates)
{
    internal string? TargetPath { get; init; }
    internal string? SourceFingerprint { get; init; }
    internal bool? TargetIsDirectory { get; init; }

    public WallMember ApplyTo(WallMember member) => member with
    {
        Images = Source, Carousel = null,
        Visual = member.Visual with
        {
            BackgroundMode = Source.Kind == MemberImageSourceKind.Automatic ? MemberBackgroundMode.Default : MemberBackgroundMode.Blur,
            BackgroundTransparency = Source.Kind == MemberImageSourceKind.Automatic ? 50 : member.Visual.BackgroundTransparency,
        },
    };
}

public sealed partial class WallImageService
{
    /// <summary>Infer from readable target contents, using the same candidates as the editor and gallery.</summary>
    public Task<TargetImageSourceResult?> ResolveTargetSourceAsync(string? target, CancellationToken ct = default) =>
        StartImageRequest<TargetImageSourceResult?>(token => ResolveTargetSourceCoreAsync(target, token), ct, null);

    private async Task<TargetImageSourceResult?> ResolveTargetSourceCoreAsync(string? target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target)) return new(new(), null);
        var before = await Task.Run(() => ReadTargetFingerprint(target, ct), ct).ConfigureAwait(false);
        var source = new MemberImageSource();
        PuzzleCandidateResult? candidates = null;
        if (before.Directory || !Path.GetExtension(target).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var proposed = new MemberImageSource
            { Kind = before.Directory ? MemberImageSourceKind.Folder : MemberImageSourceKind.Single, Path = target };
            if (before.Directory)
            {
                // A confirmation checks this source now. Share any in-flight producer,
                // but do not accept an old fallback or wait for the periodic deadline.
                var folder = NormalizeImageFilePath(target);
                lock (_workGate)
                    if (folder is not null && _gallerySources.TryGetValue(folder, out var state)) state.Due = DateTimeOffset.MinValue;
            }
            candidates = await EnumerateCandidatesCoreAsync(proposed, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (candidates.Status == WallImageStatus.Cancelled) throw new OperationCanceledException(ct);
            if (!candidates.IsComplete || candidates.Status is WallImageStatus.Fallback or WallImageStatus.Invalid)
                throw new IOException(DescribeCandidateDiagnostic(candidates) ?? "Image source is unavailable.");
            if (candidates.Paths.Count > 0) source = proposed;
        }
        var after = await Task.Run(() => ReadTargetFingerprint(target, ct), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (before != after) throw new IOException("Target changed while its image source was being read.");
        return new(source, candidates) { TargetPath = target, SourceFingerprint = before.Fingerprint, TargetIsDirectory = before.Directory };
    }

    public Task<bool> IsTargetSourceCurrentAsync(TargetImageSourceResult result, CancellationToken ct = default) =>
        StartImageRequest(token => IsTargetSourceCurrentCoreAsync(result, token), ct, false);

    private async Task<bool> IsTargetSourceCurrentCoreAsync(TargetImageSourceResult result, CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => result.TargetPath is null ||
                ReadTargetFingerprint(result.TargetPath, ct).Fingerprint == result.SourceFingerprint, ct).ConfigureAwait(false);
        }
        finally { _slots.Release(); }
    }

    private static (bool Directory, string Fingerprint) ReadTargetFingerprint(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        var directory = (attributes & FileAttributes.Directory) != 0;
        Add(directory ? "directory" : "file");
        if (directory)
        {
            // Materialize the whole listing: an unreadable/incomplete directory is
            // never an authoritative empty gallery. Only top-level files participate.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ThenBy(value => value, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                if (!info.Exists) throw new IOException("Target directory changed during enumeration.");
                Add(info.Name); Add(info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Add(info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        else
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var info = new FileInfo(path);
            Add(file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // Hash possible originals as well as shortcut bytes. Large ordinary
            // files remain valid launch targets without reading their whole contents.
            if (file.Length <= TileWall.Core.Storage.PreparedMediaAsset.MaximumBytes)
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = file.Read(buffer)) != 0) { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer.AsSpan(0, read)); }
            }
        }
        ct.ThrowIfCancellationRequested();
        return (directory, Convert.ToHexString(hash.GetHashAndReset()));
    }

    public Task<PuzzleCandidateResult> EnumerateMemberCandidatesAsync(
        MemberImageSource source, CancellationToken ct = default) =>
        StartImageRequest(token => EnumerateCandidatesCoreAsync(source, token), ct,
            new PuzzleCandidateResult([], WallImageStatus.Cancelled));

    private async Task<PuzzleCandidateResult> EnumerateCandidatesCoreAsync(
        MemberImageSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ct.IsCancellationRequested)
            return new([], WallImageStatus.Cancelled);
        if (source.Kind is MemberImageSourceKind.Automatic or MemberImageSourceKind.None)
            return new([], WallImageStatus.None);
        if (source.Kind == MemberImageSourceKind.Folder) return await RefreshGalleryRequestAsync(source, ct);

        try
        {
            await _slots.WaitAsync(ct);
            try
            {
                return await Task.Run(() => EnumerateMemberCandidatesAsyncCore(source, ct), ct);
            }
            finally { _slots.Release(); }
        }
        catch (OperationCanceledException)
        {
            return new([], WallImageStatus.Cancelled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new([], WallImageStatus.Invalid, ex.GetType().Name);
        }
    }

    private async Task<PuzzleCandidateResult> EnumerateMemberCandidatesAsyncCore(MemberImageSource source, CancellationToken ct)
    {
        IEnumerable<string> paths;
        switch (source.Kind)
        {
            case MemberImageSourceKind.Single:
                var single = source.Media is not null ? ManagedMediaPath(source.Media) : source.Path;
                if (source.Media is not null)
                {
                    if (single is null) return new([], WallImageStatus.Invalid, "InvalidManagedMediaReference");
                    _disk!.Guard(single);
                    return File.Exists(single) ? new([source.Path ?? single], WallImageStatus.Loaded) : new([], WallImageStatus.Missing);
                }
                paths = single is not null ? [single] : [];
                break;
            case MemberImageSourceKind.Folder:
                var folder = NormalizeImageFilePath(source.Path);
                if (folder is null || !Directory.Exists(folder))
                    return new([], WallImageStatus.Missing);
                paths = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                    .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static path => path, StringComparer.Ordinal);
                break;
            default:
                return new([], WallImageStatus.Invalid, "UnknownSourceKind");
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in paths)
        {
            ct.ThrowIfCancellationRequested();
            var path = NormalizeImageFilePath(candidate);
            if (path is null || !File.Exists(path) || !await _decoder.ProbeAsync(path, ct))
                continue;
            if (seen.Add(path)) result.Add(path);
            if (result.Count >= 99) break;
        }
        return result.Count == 0
            ? new([], WallImageStatus.Missing)
            : new(result, WallImageStatus.Loaded);
    }

    public Task<PuzzleFrameResult> LoadMemberFrameAsync(WallMember member, string? entryOrTarget,
        int widthPixels, int heightPixels, string? previewImagePath = null, CancellationToken ct = default,
        MemberImagePurpose purpose = MemberImagePurpose.Content) =>
        StartImageRequest(token => LoadMemberFrameCoreAsync(member, entryOrTarget, widthPixels, heightPixels,
            previewImagePath, token, purpose), ct, new PuzzleFrameResult(null, WallImageStatus.Cancelled));

    private async Task<PuzzleFrameResult> LoadMemberFrameCoreAsync(WallMember member, string? entryOrTarget,
        int widthPixels, int heightPixels, string? previewImagePath, CancellationToken ct,
        MemberImagePurpose purpose = MemberImagePurpose.Content)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (member.Images.Kind == MemberImageSourceKind.None) return new(null, WallImageStatus.None);
        if (member.ResourceOrigin == MemberResourceOrigin.Package &&
            !(member.Images.Kind == MemberImageSourceKind.Single && member.Images.Media is not null ||
              member.Images.Kind == MemberImageSourceKind.Folder && member.Images.ManagedGallery is not null))
            return new(null, WallImageStatus.None, "PackageImageSourceUnavailable");
        if (widthPixels < 1 || heightPixels < 1)
            throw new ArgumentOutOfRangeException(nameof(widthPixels));
        if (purpose is not (MemberImagePurpose.Content or MemberImagePurpose.Thumbnail))
            throw new ArgumentOutOfRangeException(nameof(purpose));
        if (member.Images.Kind == MemberImageSourceKind.Automatic && previewImagePath is null)
            return await LoadAutomaticFrameCoreAsync(entryOrTarget, widthPixels, heightPixels,
                member.Visual.BackgroundMode == MemberBackgroundMode.Blur, ct, member);
        var thumbnail = purpose == MemberImagePurpose.Thumbnail;
        var reserve = !thumbnail && member.ImageDisplay.Mode == ImageDisplayMode.Custom ? Math.Max(1, member.ImageDisplay.CustomScale) : 1;
        widthPixels = checked((int)Math.Max(1, Math.Ceiling(widthPixels * reserve)));
        heightPixels = checked((int)Math.Max(1, Math.Ceiling(heightPixels * reserve)));
        var edge = thumbnail ? Math.Max(1, Math.Max(widthPixels, heightPixels)) : int.MaxValue;
        if (thumbnail) widthPixels = heightPixels = edge;
        var needsBlur = member.Visual.BackgroundMode == MemberBackgroundMode.Blur;
        var budget = new PuzzleDecodeBudget(purpose, widthPixels, heightPixels,
            !thumbnail && member.ImageDisplay.Mode == ImageDisplayMode.Original, edge, thumbnail ? edge : 512, needsBlur);
        if (member.Images.Kind == MemberImageSourceKind.Single)
            return await LoadImageFileFrameAsync(member.Images.Media is { } media ? ManagedMediaPath(media) : previewImagePath ?? member.Images.Path,
                budget, ct, member.Images.Path, member.Images.Media?.Sha256, member.Images.Media);
        if (member.Images.Kind == MemberImageSourceKind.Folder)
        {
            IDisposable? sourceRead = null;
            try
            {
                var selected = previewImagePath;
                if (selected is null)
                {
                    var candidates = await EnumerateCandidatesCoreAsync(member.Images, ct);
                    selected = candidates.Paths.FirstOrDefault(path =>
                        StringComparer.OrdinalIgnoreCase.Equals(path, member.Carousel?.CurrentImageId)) ?? candidates.Paths.FirstOrDefault();
                }
                // A committed current frame can be prepared without enumerating its
                // gallery (and without selecting or saving a different image).
                if (selected is not null && !StringComparer.OrdinalIgnoreCase.Equals(
                    NormalizeImageFilePath(Path.GetDirectoryName(selected)), NormalizeImageFilePath(member.Images.Path)))
                    return new(null, WallImageStatus.Invalid, "ImageOutsideGallery");
                var selectedPath = selected;
                sourceRead = await Task.Run(() => OpenManagedGalleryRead(member.Images, selectedPath), ct);
                var cached = GalleryReadPath(member.Images, selected);
                if (cached.Path is null && selected is not null)
                    return await LoadImageFileFrameAsync(selected, budget, ct, selected);
                if (cached.Path is not null && !File.Exists(cached.Path))
                    await EnsureGalleryOriginalAsync(member.Images, selected, ct);
                return await LoadImageFileFrameAsync(cached.Path, budget, ct, selected, cached.Hash);
            }
            finally
            {
                if (sourceRead is not null) await Task.Run(sourceRead.Dispose);
            }
        }
        if (previewImagePath is not null) return await LoadImageFileFrameAsync(previewImagePath, budget, ct);
        if (widthPixels < 1 || heightPixels < 1)
            throw new ArgumentOutOfRangeException(nameof(widthPixels));
        return await LoadAutomaticFrameCoreAsync(entryOrTarget, widthPixels, heightPixels, needsBlur, ct, member);
    }

    private async Task<PuzzleFrameResult> LoadImageFileFrameAsync(
        string? path, PuzzleDecodeBudget budget, CancellationToken ct = default, string? identityPath = null, string? contentHash = null,
        ManagedMediaReference? media = null)
    {
        if (ct.IsCancellationRequested)
            return new(null, WallImageStatus.Cancelled);
        if (string.IsNullOrWhiteSpace(path))
            return media is null ? new(null, WallImageStatus.None)
                : new(null, WallImageStatus.Invalid, "InvalidManagedMediaReference");

        try
        {
            await _slots.WaitAsync(ct);
            PuzzlePrepared prepared;
            try
            {
                prepared = await Task.Run(() => PreparePuzzleAsync(path, budget, ct, identityPath, contentHash, media), ct);
            }
            finally { _slots.Release(); }
            if (ct.IsCancellationRequested)
                return new(null, WallImageStatus.Cancelled);
            if (prepared.Pair is null)
                return new(null, prepared.Status, prepared.Diagnostic);

            var sharp = prepared.Pair.Sharp;
            var blur = prepared.Pair.BlurSource;
            var sharpSource = MakeBitmap(sharp);
            var blurredSource = blur is null ? null : MakeBitmap(blur);
            if (ct.IsCancellationRequested)
                return new(null, WallImageStatus.Cancelled);
            return new(new PuzzleFrame(sharp.SourcePath, sharp.SourceBytes, sharp.LastWriteUtc,
                new PixelSize(sharp.SourceWidth, sharp.SourceHeight),
                new PixelSize(sharp.Width, sharp.Height), blur is null ? default : new PixelSize(blur.Width, blur.Height),
                sharpSource, blurredSource), WallImageStatus.Loaded);
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

    private async Task<PuzzlePrepared> PreparePuzzleAsync(string path, PuzzleDecodeBudget budget, CancellationToken ct,
        string? identityPath = null, string? contentHash = null, ManagedMediaReference? media = null)
    {
        ct.ThrowIfCancellationRequested();
        var canonicalPath = NormalizeImageFilePath(path);
        if (canonicalPath is null)
            return new(WallImageStatus.Invalid, null, "InvalidPath");
        using var ownership = media is null ? null : _formalResourceLease?.Invoke(null, media);
        var verified = media is null ? default : OpenVerifiedMedia(canonicalPath, media);
        using var mediaRead = verified.Lease;
        if (verified.Diagnostic is { } diagnostic)
            return new(WallImageStatus.Invalid, null, diagnostic);
        var key = PuzzleFingerprint(canonicalPath, budget);
        if (key is not null && TryGetPuzzleCached(key, out var cached))
            return identityPath is not null && cached.Pair is { } cachedPair
                ? cached with { Pair = WithIdentity(cachedPair, identityPath) } : cached;

        using var lease = _disk is { } disk && canonicalPath.StartsWith(disk.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? disk.Lease(canonicalPath) : null;
        var actualHash = contentHash ?? FileContentHash(canonicalPath);
        if (contentHash is not null && canonicalPath.Contains(Path.DirectorySeparatorChar + "Galleries" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && FileContentHash(canonicalPath) != contentHash)
        {
            if (_disk is not null && canonicalPath.Contains(Path.DirectorySeparatorChar + "Galleries" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                _disk.Delete(canonicalPath);
            return new(WallImageStatus.Invalid, null, "ImageCacheHashMismatch");
        }
        var derived = ReadDerived(actualHash, budget, identityPath ?? canonicalPath);
        if (budget.Purpose == MemberImagePurpose.Thumbnail && derived?.Sharp is { } thumbnail && (!budget.NeedsBlur || derived.BlurSource is not null))
        { var hit = new PuzzlePrepared(WallImageStatus.Loaded, derived); if (key is not null) CachePuzzle(key, hit); return hit; }

        var decoded = await _decoder.DecodePuzzlePairAsync(canonicalPath, budget.Width, budget.Height, ct,
            budget.PreserveSourceResolution, budget.SharpMaxEdge, budget.BlurMaxEdge, budget.NeedsBlur && derived?.BlurSource is null);
        if (decoded.Pair is null)
            return new(decoded.Status switch
            {
                ImageDecodeStatus.Cancelled => WallImageStatus.Cancelled,
                ImageDecodeStatus.MissingFile => WallImageStatus.Missing,
                _ => WallImageStatus.Invalid,
            }, null, decoded.ErrorType ?? decoded.Status.ToString());

        ct.ThrowIfCancellationRequested();
        PuzzleDecodedPair pair = decoded.Pair;
        if (budget.NeedsBlur)
        {
            if (decoded.Pair.BlurSource is null && derived?.BlurSource is null)
                return new(WallImageStatus.Invalid, null, "MissingBlurPlane");
            pair = new PuzzleDecodedPair(decoded.Pair.Sharp, derived?.BlurSource ?? LowResolutionBlur.Apply(decoded.Pair.BlurSource!));
        }
        if (identityPath is not null) pair = WithIdentity(pair, identityPath);
        if (derived is null) await SaveDerivedAsync(actualHash, budget, pair, ct).ConfigureAwait(false);
        var result = new PuzzlePrepared(WallImageStatus.Loaded, pair);
        var verifiedKey = PuzzleCacheKey(canonicalPath, decoded.Pair.Sharp.SourceBytes, decoded.Pair.Sharp.LastWriteUtc, budget);
        CachePuzzle(verifiedKey, result);
        return result;
    }

    private static PuzzleDecodedPair WithIdentity(PuzzleDecodedPair pair, string identity) =>
        new(pair.Sharp with { SourcePath = identity }, pair.BlurSource is { } blur ? blur with { SourcePath = identity } : null);

    private static string PuzzleCacheKey(string canonicalPath, long sourceBytes, DateTime lastWriteUtc,
        PuzzleDecodeBudget budget) =>
        $"puzzle:{budget.Purpose}:{budget.Width}x{budget.Height}:{budget.PreserveSourceResolution}:" +
        $"{budget.SharpMaxEdge}:{budget.BlurMaxEdge}:{budget.NeedsBlur}|{canonicalPath}|{sourceBytes}|{lastWriteUtc.Ticks}";

    private static string? PuzzleFingerprint(string canonicalPath, PuzzleDecodeBudget budget)
    {
        try
        {
            var info = new FileInfo(canonicalPath);
            return info.Exists
                ? PuzzleCacheKey(canonicalPath, info.Length, info.LastWriteTimeUtc, budget)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private bool TryGetPuzzleCached(string key, out PuzzlePrepared prepared)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var node) && node.Value.Value is PuzzlePrepared cached)
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                prepared = cached;
                return true;
            }
        }
        prepared = null!;
        return false;
    }

    private void CachePuzzle(string key, PuzzlePrepared prepared)
    {
        var pair = prepared.Pair!;
        var bytes = pair.Sharp.BgraPremultiplied.Length + (pair.BlurSource?.BgraPremultiplied.Length ?? 0);
        if (bytes > CacheLimitBytes / 2) return;
        lock (_cacheLock)
        {
            if (_disposed || _cacheLifetime.IsCancellationRequested || _cache.ContainsKey(key)) return;
            var node = _lru.AddFirst(new CacheItem(key, prepared, bytes));
            _cache.Add(key, node);
            _cacheBytes += bytes;
            while (_cacheBytes > CacheLimitBytes && _lru.Last is { } last)
            {
                _cacheBytes -= last.Value.Bytes;
                _cache.Remove(last.Value.Key);
                _lru.RemoveLast();
            }
        }
    }

    private static ImageSource MakeBitmap(DecodedImageFrame frame)
    {
        var bitmap = new WriteableBitmap(frame.Width, frame.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            var pixels = frame.BgraPremultiplied.Span;
            stream.Write(pixels);
        }
        bitmap.Invalidate();
        return bitmap;
    }

    private sealed record PuzzlePrepared(WallImageStatus Status, PuzzleDecodedPair? Pair,
        string? Diagnostic = null);

    private readonly record struct PuzzleDecodeBudget(MemberImagePurpose Purpose, int Width, int Height,
        bool PreserveSourceResolution, int SharpMaxEdge, int BlurMaxEdge, bool NeedsBlur);
}
