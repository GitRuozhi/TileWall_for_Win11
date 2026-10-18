using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TileWall.Core.Imaging;
using TileWall.Core.Wall;

namespace TileWall.Graphics;

public sealed record PuzzleCandidateResult(IReadOnlyList<string> Paths,
    WallImageStatus Status, string? Diagnostic = null);

public sealed record PuzzleFrameResult(PuzzleFrame? Frame,
    WallImageStatus Status, string? Diagnostic = null);

public sealed record PuzzleFrame(string CanonicalPath, long SourceBytes, DateTime LastWriteUtc,
    PixelSize SourcePixels, PixelSize SharpDecodedPixels, PixelSize BlurDecodedPixels,
    ImageSource Sharp, ImageSource BlurredUnderfill);

public enum MemberImagePurpose { Content, Thumbnail }

public sealed partial class WallImageService
{
    public async Task<PuzzleCandidateResult> EnumerateMemberCandidatesAsync(
        MemberImageSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ct.IsCancellationRequested)
            return new([], WallImageStatus.Cancelled);
        if (source.Kind == MemberImageSourceKind.Automatic)
            return new([], WallImageStatus.None);

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
                paths = source.Path is { } single ? [single] : [];
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

    public async Task<PuzzleFrameResult> LoadMemberFrameAsync(WallMember member, string? entryOrTarget,
        int widthPixels, int heightPixels, string? previewImagePath = null, CancellationToken ct = default,
        MemberImagePurpose purpose = MemberImagePurpose.Content)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (widthPixels is < 1 or > 4096 || heightPixels is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(widthPixels));
        if (purpose is not (MemberImagePurpose.Content or MemberImagePurpose.Thumbnail))
            throw new ArgumentOutOfRangeException(nameof(purpose));
        var thumbnail = purpose == MemberImagePurpose.Thumbnail;
        var reserve = !thumbnail && member.ImageDisplay.Mode == ImageDisplayMode.Custom ? Math.Max(1, member.ImageDisplay.CustomScale) : 1;
        widthPixels = (int)Math.Clamp(Math.Ceiling(widthPixels * reserve), 1, 4096);
        heightPixels = (int)Math.Clamp(Math.Ceiling(heightPixels * reserve), 1, 4096);
        var edge = thumbnail ? Math.Clamp(Math.Max(widthPixels, heightPixels), 64, 256) : 4096;
        if (thumbnail) widthPixels = heightPixels = edge;
        var budget = new PuzzleDecodeBudget(purpose, widthPixels, heightPixels,
            !thumbnail && member.ImageDisplay.Mode == ImageDisplayMode.Original, edge, thumbnail ? edge : 512);
        if (previewImagePath is not null)
            return await LoadImageFileFrameAsync(previewImagePath, budget, ct);
        if (member.Images.Kind == MemberImageSourceKind.Single)
            return await LoadImageFileFrameAsync(member.Images.Path, budget, ct);
        if (member.Images.Kind == MemberImageSourceKind.Folder)
        {
            var candidates = await EnumerateMemberCandidatesAsync(member.Images, ct);
            var selected = candidates.Paths.FirstOrDefault(path =>
                StringComparer.OrdinalIgnoreCase.Equals(path, member.Carousel?.CurrentImageId)) ?? candidates.Paths.FirstOrDefault();
            return await LoadImageFileFrameAsync(selected, budget, ct);
        }
        if (widthPixels is < 1 or > 4096 || heightPixels is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(widthPixels));
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            await _slots.WaitAsync(ct);
            Prepared prepared;
            try { prepared = await Task.Run(() => PrepareAutoAsync(entryOrTarget, widthPixels, heightPixels, ct), ct); }
            finally { _slots.Release(); }
            ct.ThrowIfCancellationRequested();
            if (prepared.Pixels is null) return new(null, prepared.Status, prepared.Diagnostic);
            var sharp = new DecodedImageFrame(entryOrTarget ?? $"automatic:{member.Id}", 0, DateTime.MinValue,
                prepared.Width, prepared.Height, prepared.Width, prepared.Height, prepared.Pixels);
            var blurred = LowResolutionBlur.Apply(sharp);
            return new(new PuzzleFrame(sharp.SourcePath, 0, DateTime.MinValue,
                new PixelSize(sharp.SourceWidth, sharp.SourceHeight), new PixelSize(sharp.Width, sharp.Height),
                new PixelSize(blurred.Width, blurred.Height), MakeBitmap(sharp), MakeBitmap(blurred)), prepared.Status);
        }
        catch (OperationCanceledException) { return new(null, WallImageStatus.Cancelled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or
            ArgumentException or InvalidOperationException or OverflowException)
        { return new(null, WallImageStatus.Invalid, ex.GetType().Name); }
    }

    private async Task<PuzzleFrameResult> LoadImageFileFrameAsync(
        string? path, PuzzleDecodeBudget budget, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ct.IsCancellationRequested)
            return new(null, WallImageStatus.Cancelled);
        if (string.IsNullOrWhiteSpace(path))
            return new(null, WallImageStatus.None);

        try
        {
            await _slots.WaitAsync(ct);
            PuzzlePrepared prepared;
            try
            {
                prepared = await Task.Run(() => PreparePuzzleAsync(path, budget, ct), ct);
            }
            finally { _slots.Release(); }
            if (ct.IsCancellationRequested)
                return new(null, WallImageStatus.Cancelled);
            if (prepared.Pair is null)
                return new(null, prepared.Status, prepared.Diagnostic);

            var sharp = prepared.Pair.Sharp;
            var blur = prepared.Pair.BlurSource;
            var sharpSource = MakeBitmap(sharp);
            var blurredSource = MakeBitmap(blur);
            if (ct.IsCancellationRequested)
                return new(null, WallImageStatus.Cancelled);
            return new(new PuzzleFrame(sharp.SourcePath, sharp.SourceBytes, sharp.LastWriteUtc,
                new PixelSize(sharp.SourceWidth, sharp.SourceHeight),
                new PixelSize(sharp.Width, sharp.Height), new PixelSize(blur.Width, blur.Height),
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

    private async Task<PuzzlePrepared> PreparePuzzleAsync(string path, PuzzleDecodeBudget budget, CancellationToken ct)
    {
        var canonicalPath = NormalizeImageFilePath(path);
        if (canonicalPath is null)
            return new(WallImageStatus.Invalid, null, "InvalidPath");
        var key = PuzzleFingerprint(canonicalPath, budget);
        if (key is not null && TryGetPuzzleCached(key, out var cached))
            return cached;

        var decoded = await _decoder.DecodePuzzlePairAsync(canonicalPath, budget.Width, budget.Height, ct,
            budget.PreserveSourceResolution, budget.SharpMaxEdge, budget.BlurMaxEdge);
        if (decoded.Pair is null)
            return new(decoded.Status switch
            {
                ImageDecodeStatus.Cancelled => WallImageStatus.Cancelled,
                ImageDecodeStatus.MissingFile => WallImageStatus.Missing,
                _ => WallImageStatus.Invalid,
            }, null, decoded.ErrorType ?? decoded.Status.ToString());

        var blurred = LowResolutionBlur.Apply(decoded.Pair.BlurSource);
        var result = new PuzzlePrepared(WallImageStatus.Loaded,
            new PuzzleDecodedPair(decoded.Pair.Sharp, blurred));
        var verifiedKey = PuzzleCacheKey(decoded.Pair.Sharp.SourcePath, decoded.Pair.Sharp.SourceBytes,
            decoded.Pair.Sharp.LastWriteUtc, budget);
        CachePuzzle(verifiedKey, result);
        return result;
    }

    private static string PuzzleCacheKey(string canonicalPath, long sourceBytes, DateTime lastWriteUtc,
        PuzzleDecodeBudget budget) =>
        $"puzzle:{budget.Purpose}:{budget.Width}x{budget.Height}:{budget.PreserveSourceResolution}:" +
        $"{budget.SharpMaxEdge}:{budget.BlurMaxEdge}|{canonicalPath}|{sourceBytes}|{lastWriteUtc.Ticks}";

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
        var bytes = pair.Sharp.BgraPremultiplied.Length + (long)pair.BlurSource.BgraPremultiplied.Length;
        if (bytes > CacheLimitBytes / 2) return;
        lock (_cacheLock)
        {
            if (_disposed || _cache.ContainsKey(key)) return;
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
        bool PreserveSourceResolution, int SharpMaxEdge, int BlurMaxEdge);
}
