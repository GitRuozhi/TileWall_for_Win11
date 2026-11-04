using System.Security.Cryptography;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using TileWall.Core.Imaging;
using TileWall.Core.Wall;

namespace TileWall.Graphics;

public sealed partial class WallImageService
{
    /// <summary>A consumer-specific display plane from a held frame; never changes its native source or selection.</summary>
    public Task<PuzzleFrame> PrepareDisplayFrameAsync(PuzzleFrame frame, int width, int height,
        MemberImageDisplay display, CancellationToken ct = default) => StartImageRequest(async token =>
    {
        if (frame.Sharp is not WriteableBitmap bitmap) return frame;
        var size = ImageDecodeSizing.ForDisplay(checked((int)frame.SourcePixels.Width), checked((int)frame.SourcePixels.Height),
            width, height, display);
        if (size.Width >= bitmap.PixelWidth && size.Height >= bitmap.PixelHeight) return frame;
        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        using (var input = bitmap.PixelBuffer.AsStream()) input.ReadExactly(pixels);
        var source = new DecodedImageFrame(frame.CanonicalPath, frame.SourceBytes, frame.LastWriteUtc,
            checked((int)frame.SourcePixels.Width), checked((int)frame.SourcePixels.Height),
            bitmap.PixelWidth, bitmap.PixelHeight, pixels);
        var scaled = await PrepareAutomaticDisplayPlaneAsync(source, width, height, display, frame.MemoryCacheAllowed, token);
        token.ThrowIfCancellationRequested();
        return frame with { Sharp = MakeBitmap(scaled), SharpDecodedPixels = new(scaled.Width, scaled.Height) };
    }, ct, frame);

    private readonly Dictionary<string, Task<DecodedImageFrame>> _displayPrepared = new(StringComparer.Ordinal);

    private async Task<DecodedImageFrame> PrepareAutomaticDisplayPlaneAsync(DecodedImageFrame source,
        int width, int height, MemberImageDisplay display, bool cacheAllowed, CancellationToken token)
    {
        var size = ImageDecodeSizing.ForDisplay(source.SourceWidth, source.SourceHeight, width, height, display);
        if (size.Width >= source.Width && size.Height >= source.Height) return source;
        var key = await Task.Run(() => "automatic-display-v1:" +
            Convert.ToHexString(SHA256.HashData(source.BgraPremultiplied.Span)) + $":{size.Width}x{size.Height}", token);
        DecodedImageFrame WithSource(DecodedImageFrame plane) => plane with
        {
            SourcePath = source.SourcePath, SourceBytes = source.SourceBytes, LastWriteUtc = source.LastWriteUtc,
            SourceWidth = source.SourceWidth, SourceHeight = source.SourceHeight,
        };
        Task<DecodedImageFrame> production;
        if (!cacheAllowed)
        {
            lock (_workGate)
            {
                if (_cacheStopping || _disposed) throw new OperationCanceledException(_cacheLifetime.Token);
                production = TrackCacheWork(ProduceDisplayPlaneAsync(source, size.Width, size.Height, key, false, token));
            }
        }
        else
        {
            lock (_workGate)
            {
                if (_cacheStopping || _disposed) throw new OperationCanceledException(_cacheLifetime.Token);
                if (TryGetPuzzleCached(key, out var cached) && cached.Pair is { } pair) return WithSource(pair.Sharp);
                if (!_displayPrepared.TryGetValue(key, out production!))
                {
                    production = ProduceDisplayPlaneAsync(source, size.Width, size.Height, key, true, _cacheLifetime.Token);
                    _displayPrepared.Add(key, production);
                    TrackCacheWork(production);
                    _ = production.ContinueWith(completed =>
                    {
                        lock (_workGate)
                            if (_displayPrepared.TryGetValue(key, out var current) && ReferenceEquals(current, completed))
                                _displayPrepared.Remove(key);
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        return WithSource(await production.WaitAsync(token));
    }

    private Task<DecodedImageFrame> ProduceDisplayPlaneAsync(DecodedImageFrame source,
        int width, int height, string key, bool cacheAllowed, CancellationToken token) => Task.Run(async () =>
    {
        await _slots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // The native buffer is immutable; the service owns a shared display
            // producer independently of a caller abandoning its wait.
            var asset = await EncodeIconFrameAsync(new Prepared(WallImageStatus.Loaded,
                source.BgraPremultiplied.ToArray(), source.Width, source.Height, null), token).ConfigureAwait(false);
            var decoded = await _decoder.DecodePackageFrameAsync(asset.CopyBytes(), source.SourcePath,
                width, height, token).ConfigureAwait(false);
            if (decoded.Frame is not { } result) throw new IOException(decoded.ErrorType ?? "Automatic display scaling failed.");
            token.ThrowIfCancellationRequested();
            if (cacheAllowed) CachePuzzle(key, new PuzzlePrepared(WallImageStatus.Loaded, new(result, null)));
            return result;
        }
        finally { _slots.Release(); }
    }, token);

    /// <summary>Derive an editor underfill from an already held wall frame; never reread its source.</summary>
    public Task<PuzzleFrame> EnsureFrameBlurAsync(PuzzleFrame frame, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return StartImageRequest(token => EnsureFrameBlurCoreAsync(frame, token), ct, frame);
    }

    private static async Task<PuzzleFrame> EnsureFrameBlurCoreAsync(PuzzleFrame frame, CancellationToken ct)
    {
        if (frame.BlurredUnderfill is not null) return frame;
        ct.ThrowIfCancellationRequested();
        if (frame.Sharp is not WriteableBitmap bitmap) return frame;
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var pixels = new byte[checked(width * height * 4)];
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.ReadExactly(pixels);
        var source = new DecodedImageFrame(frame.CanonicalPath, frame.SourceBytes, frame.LastWriteUtc,
            checked((int)frame.SourcePixels.Width), checked((int)frame.SourcePixels.Height), width, height, pixels);
        var blur = await Task.Run(() => LowResolutionBlur.Apply(BlurInput(source)), ct);
        ct.ThrowIfCancellationRequested();
        return frame with { BlurredUnderfill = MakeBitmap(blur), BlurDecodedPixels = new PixelSize(blur.Width, blur.Height) };
    }

    private const long DerivedLimit = 128L * 1024 * 1024;
    private const int MaximumDerivedBytes = 2 * 1024 * 1024;
    private string DerivedPath(string hash, PuzzleDecodeBudget budget) => _disk!.Under("Cache", "Images", "Derived",
        ManagedImageCache.Key($"derived-v5|{hash}|{budget.Purpose}|{budget.Width}x{budget.Height}|" +
            $"{budget.PreserveSourceResolution}|{budget.SharpMaxEdge}|{budget.BlurMaxEdge}|{budget.NeedsBlur}|{budget.CacheSharp}|BGRA8-premultiplied") + ".pixels");

    private PuzzleDecodedPair? ReadDerived(string hash, PuzzleDecodeBudget budget, string identity)
    {
        if (_disk is not { } disk || budget.Purpose != MemberImagePurpose.Thumbnail && !budget.NeedsBlur && !budget.CacheSharp) return null;
        var path = DerivedPath(hash, budget);
        try
        {
            using var lease = disk.Lease(path);
            using var stream = new MemoryStream(disk.Read(path, MaximumDerivedBytes), writable: false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != 0x54574431) return null;
            var sourceWidth = reader.ReadInt32(); var sourceHeight = reader.ReadInt32();
            if (sourceWidth < 1 || sourceHeight < 1) return null;
            var sharp = ReadPlane(reader, identity, sourceWidth, sourceHeight,
                budget.Purpose == MemberImagePurpose.Thumbnail || budget.CacheSharp ? budget.SharpMaxEdge : 0);
            var blur = ReadPlane(reader, identity, sourceWidth, sourceHeight, 512);
            if (stream.Position != stream.Length || (budget.Purpose == MemberImagePurpose.Thumbnail || budget.CacheSharp) && sharp is null || budget.NeedsBlur && blur is null) return null;
            Interlocked.Increment(ref _displayDiskHits);
            return new(sharp ?? new DecodedImageFrame(identity, 0, DateTime.MinValue, sourceWidth, sourceHeight, 0, 0, ReadOnlyMemory<byte>.Empty), blur);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { return null; }
    }

    private static DecodedImageFrame? ReadPlane(BinaryReader reader, string identity, int sourceWidth, int sourceHeight, int maximumEdge)
    {
        var width = reader.ReadInt32(); var height = reader.ReadInt32(); var length = reader.ReadInt32();
        if (width == 0 && height == 0 && length == 0) return null;
        if (width < 1 || height < 1 || width > maximumEdge || height > maximumEdge || length != checked(width * height * 4))
            throw new InvalidDataException("Invalid derived image dimensions.");
        var expectedHash = reader.ReadBytes(32); var pixels = reader.ReadBytes(length);
        if (pixels.Length != length || !SHA256.HashData(pixels).AsSpan().SequenceEqual(expectedHash)) throw new InvalidDataException("Invalid derived image hash.");
        return new(identity, 0, DateTime.MinValue, sourceWidth, sourceHeight, width, height, pixels);
    }

    private async Task SaveDerivedAsync(string hash, PuzzleDecodeBudget budget, PuzzleDecodedPair pair, CancellationToken ct)
    {
        if (_disk is not { } disk || budget.Purpose != MemberImagePurpose.Thumbnail && !budget.NeedsBlur && !budget.CacheSharp) return;
        try
        {
            ct.ThrowIfCancellationRequested();
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(0x54574431); writer.Write(pair.Sharp.SourceWidth); writer.Write(pair.Sharp.SourceHeight);
                WritePlane(writer, budget.Purpose == MemberImagePurpose.Thumbnail || budget.CacheSharp ? pair.Sharp : null);
                WritePlane(writer, pair.BlurSource);
            }
            if (stream.Length > MaximumDerivedBytes) return;
            await PublishBytesAsync(DerivedPath(hash, budget), stream.ToArray(), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            disk.Evict(disk.Under("Cache", "Images", "Derived"), DerivedLimit, ".pixels");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { /* Derived files are disposable; a decoded image remains usable. */ }
    }
    private static void WritePlane(BinaryWriter writer, DecodedImageFrame? plane)
    {
        writer.Write(plane?.Width ?? 0); writer.Write(plane?.Height ?? 0); writer.Write(plane?.BgraPremultiplied.Length ?? 0);
        if (plane is null) return;
        writer.Write(SHA256.HashData(plane.BgraPremultiplied.Span)); writer.Write(plane.BgraPremultiplied.Span);
    }
}
