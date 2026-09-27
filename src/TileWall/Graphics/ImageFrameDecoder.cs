using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage;

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
public sealed record PuzzleDecodedPair(DecodedImageFrame Sharp, DecodedImageFrame BlurSource);
public sealed record PuzzleDecodeResult(ImageDecodeStatus Status, PuzzleDecodedPair? Pair, string? ErrorType = null);

/// <summary>Bounded, orientation-correct WIC decode. Safe to call away from the XAML UI thread.</summary>
public sealed class ImageFrameDecoder
{
    private const long MaxSourceBytes = 32L * 1024 * 1024;
    private const long MaxSourcePixels = 80_000_000;
    private const long MaxDecodedPixels = 16_000_000;
    private const int MaxDecodedEdge = 4096;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".bmp" };

    /// <summary>Decode both complete image planes from one open file and one decoder.</summary>
    public async Task<PuzzleDecodeResult> DecodePuzzlePairAsync(
        string? sourcePath, int requiredWidthPx, int requiredHeightPx,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return new(ImageDecodeStatus.MissingFile, null);
        if (requiredWidthPx <= 0 || requiredHeightPx <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredWidthPx));

        string path;
        try { path = Path.GetFullPath(sourcePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return new(ImageDecodeStatus.MissingFile, null, ex.GetType().Name); }
        if (!AllowedExtensions.Contains(Path.GetExtension(path)))
            return new(ImageDecodeStatus.UnsupportedFormat, null);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!info.Exists)
                return new(ImageDecodeStatus.MissingFile, null);
            if (info.Length is < 1 or > MaxSourceBytes)
                return new(ImageDecodeStatus.SourceTooLarge, null);
            var fileBytes = info.Length;
            var lastWriteUtc = info.LastWriteTimeUtc;

            var file = await StorageFile.GetFileFromPathAsync(path);
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            cancellationToken.ThrowIfCancellationRequested();
            var rawWidth = checked((int)decoder.PixelWidth);
            var rawHeight = checked((int)decoder.PixelHeight);
            var orientedWidth = checked((int)decoder.OrientedPixelWidth);
            var orientedHeight = checked((int)decoder.OrientedPixelHeight);
            if (rawWidth <= 0 || rawHeight <= 0 ||
                (long)rawWidth * rawHeight > MaxSourcePixels ||
                (long)orientedWidth * orientedHeight != (long)rawWidth * rawHeight)
                return new(ImageDecodeStatus.SourceTooLarge, null);

            var sharp = await DecodePlaneAsync(decoder, path, fileBytes, lastWriteUtc,
                rawWidth, rawHeight, orientedWidth, orientedHeight,
                requiredWidthPx, requiredHeightPx, cancellationToken);
            // A second WIC read uses the same open stream and decoder, never a second file version.
            var blur = await DecodePlaneAsync(decoder, path, fileBytes, lastWriteUtc,
                rawWidth, rawHeight, orientedWidth, orientedHeight,
                512, 512, cancellationToken, maxEdge: 512);
            if (sharp is null || blur is null)
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

    private static async Task<DecodedImageFrame?> DecodePlaneAsync(
        BitmapDecoder decoder, string path, long fileBytes, DateTime lastWriteUtc,
        int rawWidth, int rawHeight, int orientedWidth, int orientedHeight,
        int requiredWidthPx, int requiredHeightPx, CancellationToken ct, int maxEdge = MaxDecodedEdge)
    {
        var scale = Math.Min(1.0,
            Math.Max((double)requiredWidthPx / orientedWidth, (double)requiredHeightPx / orientedHeight));
        scale = Math.Min(scale, (double)maxEdge / Math.Max(rawWidth, rawHeight));
        scale = Math.Min(scale, Math.Sqrt((double)MaxDecodedPixels / ((long)rawWidth * rawHeight)));
        var scaledRawWidth = Math.Max(1, (int)Math.Round(rawWidth * scale));
        var scaledRawHeight = Math.Max(1, (int)Math.Round(rawHeight * scale));
        var swapsAxes = rawWidth != rawHeight && orientedWidth == rawHeight && orientedHeight == rawWidth;
        var resultWidth = swapsAxes ? scaledRawHeight : scaledRawWidth;
        var resultHeight = swapsAxes ? scaledRawWidth : scaledRawHeight;
        if ((long)resultWidth * resultHeight > MaxDecodedPixels ||
            Math.Max(resultWidth, resultHeight) > maxEdge)
            return null;
        var transform = scale < 1.0
            ? new BitmapTransform
            {
                ScaledWidth = (uint)scaledRawWidth,
                ScaledHeight = (uint)scaledRawHeight,
                InterpolationMode = BitmapInterpolationMode.Fant,
            }
            : new BitmapTransform();
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        ct.ThrowIfCancellationRequested();
        var pixels = pixelData.DetachPixelData();
        return pixels.LongLength == (long)resultWidth * resultHeight * 4
            ? new(path, fileBytes, lastWriteUtc, orientedWidth, orientedHeight,
                resultWidth, resultHeight, pixels)
            : null;
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
        if (!AllowedExtensions.Contains(Path.GetExtension(path)))
            return new(ImageDecodeStatus.UnsupportedFormat, null);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!info.Exists)
                return new(ImageDecodeStatus.MissingFile, null);
            if (info.Length is < 1 or > MaxSourceBytes)
                return new(ImageDecodeStatus.SourceTooLarge, null);
            var fileBytes = info.Length;
            var lastWriteUtc = info.LastWriteTimeUtc;

            var file = await StorageFile.GetFileFromPathAsync(path);
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            cancellationToken.ThrowIfCancellationRequested();

            var rawWidth = checked((int)decoder.PixelWidth);
            var rawHeight = checked((int)decoder.PixelHeight);
            var orientedWidth = checked((int)decoder.OrientedPixelWidth);
            var orientedHeight = checked((int)decoder.OrientedPixelHeight);
            if (rawWidth <= 0 || rawHeight <= 0 ||
                (long)rawWidth * rawHeight > MaxSourcePixels ||
                (long)orientedWidth * orientedHeight != (long)rawWidth * rawHeight)
                return new(ImageDecodeStatus.SourceTooLarge, null);

            var frame = await DecodePlaneAsync(decoder, path, fileBytes, lastWriteUtc,
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
