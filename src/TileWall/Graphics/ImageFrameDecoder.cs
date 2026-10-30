using System.Runtime.InteropServices;
using TileWall.Core.Imaging;
using Windows.Graphics.Imaging;

namespace TileWall.Graphics;

public enum ImageDecodeStatus { Success, MissingFile, UnsupportedFormat, SourceTooLarge, InvalidImage, Cancelled, SourceChanged }

public sealed record DecodedImageFrame(
    string SourcePath,
    long SourceBytes,
    DateTime LastWriteUtc,
    int SourceWidth,
    int SourceHeight,
    int Width,
    int Height,
    ReadOnlyMemory<byte> BgraPremultiplied);

public sealed record ImageDecodeResult(ImageDecodeStatus Status, DecodedImageFrame? Frame, string? ErrorType = null);
public sealed record PuzzleDecodedPair(DecodedImageFrame Sharp, DecodedImageFrame? BlurSource);
public sealed record PuzzleDecodeResult(ImageDecodeStatus Status, PuzzleDecodedPair? Pair, string? ErrorType = null);

/// <summary>Orientation-correct WIC decode. Safe to call away from the XAML UI thread.</summary>
public sealed class ImageFrameDecoder
{
    private const int MaxDecodedEdge = int.MaxValue;
    /// <summary>Discover format support through WIC itself, including installed codecs and extensionless files.</summary>
    public async Task<bool> ProbeAsync(string path, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 1) return false;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var stream = file.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
            var frame = await SelectSourceFrameAsync(decoder, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (frame.PixelWidth == 0 || frame.PixelHeight == 0) return false;
            // A valid header alone is not a usable gallery candidate. Decode a sample
            // before counting this file toward the shared image list.
            return await DecodePlaneAsync(frame, path, info.Length, info.LastWriteTimeUtc,
                checked((int)frame.PixelWidth), checked((int)frame.PixelHeight),
                checked((int)frame.OrientedPixelWidth), checked((int)frame.OrientedPixelHeight),
                1, 1, ct, maxEdge: 1) is not null;
        }
        catch (OperationCanceledException) { throw; }
        // Read failures are different from a readable non-image. The candidate
        // service reports them as Invalid so target inference cannot accept them
        // as an authoritative empty source/Automatic tile.
        catch (Exception ex) when (ex is COMException or
            ArgumentException or OverflowException or InvalidOperationException) { return false; }
    }

    /// <summary>Decode both complete image planes from one open file and one decoder.</summary>
    public async Task<PuzzleDecodeResult> DecodePuzzlePairAsync(
        string? sourcePath, int requiredWidthPx, int requiredHeightPx,
        CancellationToken cancellationToken = default, bool preserveSourceResolution = false,
        int sharpMaxEdge = MaxDecodedEdge, int blurMaxEdge = MaxDecodedEdge, bool needsBlur = true)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return new(ImageDecodeStatus.MissingFile, null);
        if (requiredWidthPx <= 0 || requiredHeightPx <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredWidthPx));
        if (sharpMaxEdge < 1 || blurMaxEdge < 1)
            throw new ArgumentOutOfRangeException(nameof(sharpMaxEdge));

        string path;
        try { path = Path.GetFullPath(sourcePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return new(ImageDecodeStatus.MissingFile, null, ex.GetType().Name); }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!info.Exists)
                return new(ImageDecodeStatus.MissingFile, null);
            if (info.Length < 1)
                return new(ImageDecodeStatus.SourceTooLarge, null);
            var fileBytes = info.Length;
            var lastWriteUtc = info.LastWriteTimeUtc;

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = file.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
            var source = await SelectSourceFrameAsync(decoder, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var rawWidth = checked((int)source.PixelWidth);
            var rawHeight = checked((int)source.PixelHeight);
            var orientedWidth = checked((int)source.OrientedPixelWidth);
            var orientedHeight = checked((int)source.OrientedPixelHeight);
            if (rawWidth <= 0 || rawHeight <= 0 ||
                (long)orientedWidth * orientedHeight != (long)rawWidth * rawHeight)
                return new(ImageDecodeStatus.SourceTooLarge, null);

            var sharp = await DecodePlaneAsync(source, path, fileBytes, lastWriteUtc,
                rawWidth, rawHeight, orientedWidth, orientedHeight,
                preserveSourceResolution ? orientedWidth : requiredWidthPx,
                preserveSourceResolution ? orientedHeight : requiredHeightPx, cancellationToken, maxEdge: sharpMaxEdge);
            DecodedImageFrame? blur = null;
            if (needsBlur)
            {
                // A second WIC read uses the same open stream and decoder, never a second file version.
                blur = await DecodePlaneAsync(source, path, fileBytes, lastWriteUtc,
                    rawWidth, rawHeight, orientedWidth, orientedHeight,
                    blurMaxEdge, blurMaxEdge, cancellationToken, maxEdge: blurMaxEdge);
            }
            if (sharp is null || needsBlur && blur is null)
                return new(ImageDecodeStatus.InvalidImage, null, "UnexpectedPixelCount");

            info.Refresh();
            if (!info.Exists || info.Length != fileBytes || info.LastWriteTimeUtc != lastWriteUtc)
                return new(ImageDecodeStatus.SourceChanged, null);
            return new(ImageDecodeStatus.Success, new PuzzleDecodedPair(sharp, blur));
        }
        catch (OperationCanceledException)
        {
            return new(ImageDecodeStatus.Cancelled, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or
            ArgumentException or OverflowException or InvalidOperationException)
        {
            return new(ImageDecodeStatus.InvalidImage, null, ex.GetType().Name);
        }
    }

    private static async Task<BitmapFrame> SelectSourceFrameAsync(BitmapDecoder decoder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // WIC's default ICO frame can be 16px even when a native 256px image
        // exists. Size alternatives are specific to ICO; GIF/TIFF retain their
        // first-frame behavior. Content and thumbnail use one stable source plane.
        if (decoder.DecoderInformation.CodecId != BitmapDecoder.IcoDecoderId)
            return await decoder.GetFrameAsync(0).AsTask(ct).ConfigureAwait(false);
        // ICO stores its frame count in a 16-bit directory field. This is a
        // format validation, not a TileWall frame-count quota.
        if (decoder.FrameCount is < 1 or > ushort.MaxValue)
            throw new InvalidOperationException("ICO frame count is outside its native representation.");
        BitmapFrame? best = null;
        long bestArea = 0;
        for (uint index = 0; index < decoder.FrameCount; index++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var candidate = await decoder.GetFrameAsync(index).AsTask(ct).ConfigureAwait(false);
                // ICO's byte-sized dimensions encode 256 as zero.
                if (candidate.PixelWidth is < 1 or > 256 || candidate.PixelHeight is < 1 or > 256) continue;
                var area = (long)candidate.PixelWidth * candidate.PixelHeight;
                if (area <= bestArea) continue;
                best = candidate;
                bestArea = area;
            }
            catch (COMException) { /* One unreadable size alternative does not hide the others. */ }
        }
        return best ?? throw new InvalidOperationException("ICO has no usable image frame.");
    }

    private static async Task<DecodedImageFrame?> DecodePlaneAsync(
        BitmapFrame decoder, string path, long fileBytes, DateTime lastWriteUtc,
        int rawWidth, int rawHeight, int orientedWidth, int orientedHeight,
        int requiredWidthPx, int requiredHeightPx, CancellationToken ct, int maxEdge = MaxDecodedEdge)
    {
        ct.ThrowIfCancellationRequested();
        var dimensions = ImageDecodeSizing.Calculate(rawWidth, rawHeight, orientedWidth, orientedHeight,
            requiredWidthPx, requiredHeightPx, maxEdge);
        var transform = dimensions.ScaledRawWidth != rawWidth || dimensions.ScaledRawHeight != rawHeight
            ? new BitmapTransform
            {
                ScaledWidth = (uint)dimensions.ScaledRawWidth,
                ScaledHeight = (uint)dimensions.ScaledRawHeight,
                InterpolationMode = BitmapInterpolationMode.Fant,
            }
            : new BitmapTransform();
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var pixels = pixelData.DetachPixelData();
        long expectedByteCount;
        try { expectedByteCount = checked((long)dimensions.Width * dimensions.Height * 4); }
        catch (OverflowException) { return null; }
        return pixels.LongLength == expectedByteCount
            ? new(path, fileBytes, lastWriteUtc, orientedWidth, orientedHeight,
                dimensions.Width, dimensions.Height, pixels)
            : null;
    }

    /// <summary>Decode a verified package thumbnail without writing a temporary file.</summary>
    internal Task<ImageDecodeResult> DecodePackageThumbnailAsync(byte[] bytes, string identity,
        int edge, CancellationToken token)
    {
        if (edge < 1) throw new ArgumentOutOfRangeException(nameof(edge));
        return DecodePackageFrameCoreAsync(bytes, identity, edge, edge, edge, token);
    }

    internal Task<ImageDecodeResult> DecodePackageFrameAsync(byte[] bytes, string identity,
        int width, int height, CancellationToken token, int maximumEdge = MaxDecodedEdge) => DecodePackageFrameCoreAsync(bytes, identity,
            width, height, maximumEdge, token);

    private static async Task<ImageDecodeResult> DecodePackageFrameCoreAsync(byte[] bytes, string identity,
        int requiredWidth, int requiredHeight, int maximumEdge, CancellationToken token)
    {
        if (bytes.LongLength < 1) return new(ImageDecodeStatus.SourceTooLarge, null);
        if (requiredWidth < 1 || requiredHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(requiredWidth));
        if (maximumEdge < 1) throw new ArgumentOutOfRangeException(nameof(maximumEdge));
        token.ThrowIfCancellationRequested();
        using var file = new MemoryStream(bytes, writable: false);
        using var stream = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token).ConfigureAwait(false);
        var source = await SelectSourceFrameAsync(decoder, token).ConfigureAwait(false);
        var rawWidth = checked((int)source.PixelWidth); var rawHeight = checked((int)source.PixelHeight);
        var width = checked((int)source.OrientedPixelWidth); var height = checked((int)source.OrientedPixelHeight);
        if (rawWidth <= 0 || rawHeight <= 0 ||
            (long)width * height != (long)rawWidth * rawHeight) return new(ImageDecodeStatus.SourceTooLarge, null);
        var frame = await DecodePlaneAsync(source, identity, bytes.Length, DateTime.MinValue,
            rawWidth, rawHeight, width, height, requiredWidth, requiredHeight, token, maximumEdge).ConfigureAwait(false);
        return new(frame is null ? ImageDecodeStatus.InvalidImage : ImageDecodeStatus.Success, frame);
    }

    /// <param name="requiredWidthPx">Target physical width, including current zoom reserve.</param>
    /// <param name="requiredHeightPx">Target physical height, including current zoom reserve.</param>
    public async Task<ImageDecodeResult> DecodeAsync(
        string? sourcePath, int requiredWidthPx, int requiredHeightPx, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return new(ImageDecodeStatus.MissingFile, null);
        if (requiredWidthPx <= 0 || requiredHeightPx <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredWidthPx), "Required physical dimensions must be positive.");

        string path;
        try { path = Path.GetFullPath(sourcePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return new(ImageDecodeStatus.MissingFile, null, ex.GetType().Name); }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!info.Exists)
                return new(ImageDecodeStatus.MissingFile, null);
            if (info.Length < 1)
                return new(ImageDecodeStatus.SourceTooLarge, null);
            var fileBytes = info.Length;
            var lastWriteUtc = info.LastWriteTimeUtc;

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = file.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
            var source = await SelectSourceFrameAsync(decoder, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var rawWidth = checked((int)source.PixelWidth);
            var rawHeight = checked((int)source.PixelHeight);
            var orientedWidth = checked((int)source.OrientedPixelWidth);
            var orientedHeight = checked((int)source.OrientedPixelHeight);
            if (rawWidth <= 0 || rawHeight <= 0 ||
                (long)orientedWidth * orientedHeight != (long)rawWidth * rawHeight)
                return new(ImageDecodeStatus.SourceTooLarge, null);

            var frame = await DecodePlaneAsync(source, path, fileBytes, lastWriteUtc,
                rawWidth, rawHeight, orientedWidth, orientedHeight,
                requiredWidthPx, requiredHeightPx, cancellationToken);
            if (frame is null)
                return new(ImageDecodeStatus.InvalidImage, null, "UnexpectedPixelCount");

            info.Refresh();
            if (!info.Exists || info.Length != fileBytes || info.LastWriteTimeUtc != lastWriteUtc)
                return new(ImageDecodeStatus.SourceChanged, null);
            return new(ImageDecodeStatus.Success, frame);
        }
        catch (OperationCanceledException)
        {
            return new(ImageDecodeStatus.Cancelled, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or
            ArgumentException or OverflowException or InvalidOperationException)
        {
            return new(ImageDecodeStatus.InvalidImage, null, ex.GetType().Name);
        }
    }
}
