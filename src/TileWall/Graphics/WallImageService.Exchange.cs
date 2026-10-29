using TileWall.Core.Imaging;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TileWall.Graphics;

public sealed partial class WallImageService
{
    /// <summary>Encode the automatic source plane, before any ImageDisplay, partition, background or blur transform.</summary>
    internal Task<PreparedMediaAsset?> PrepareAutomaticExportImageAsync(WallMember member, string? entryOrTarget,
        int width, int height, CancellationToken token) => StartImageRequest<PreparedMediaAsset?>(async requestToken =>
    {
        if (member.ResourceOrigin == MemberResourceOrigin.Package || member.Images.Kind != MemberImageSourceKind.Automatic)
            return null;
        var result = await LoadAutomaticFrameCoreAsync(entryOrTarget, width, height, false, requestToken, member);
        requestToken.ThrowIfCancellationRequested();
        if (result.Frame?.Sharp is not WriteableBitmap bitmap) return null;
        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        using (var input = bitmap.PixelBuffer.AsStream()) input.ReadExactly(pixels);
        var pixelWidth = bitmap.PixelWidth; var pixelHeight = bitmap.PixelHeight;
        return await Task.Run(async () =>
        {
            using var encoded = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded).AsTask(requestToken).ConfigureAwait(false);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)pixelWidth, (uint)pixelHeight, 96, 96, pixels);
            await encoder.FlushAsync().AsTask(requestToken).ConfigureAwait(false);
            requestToken.ThrowIfCancellationRequested();
            if (encoded.Size is < 1 or > PreparedMediaAsset.MaximumBytes) throw new IOException("Automatic foreground exceeds its PNG byte budget.");
            encoded.Seek(0);
            using var input = encoded.AsStreamForRead();
            var bytes = new byte[(int)encoded.Size];
            await input.ReadExactlyAsync(bytes, requestToken).ConfigureAwait(false);
            return PreparedMediaAsset.FromVerifiedImage(bytes, ".png");
        }, requestToken);
    }, token, null);

    internal Task<PuzzleFrameResult> LoadPackageFrameAsync(PreparedMediaAsset asset, int width, int height, bool blur,
        CancellationToken token) => StartImageRequest(requestToken => LoadPackageFrameCoreAsync(asset, width, height, blur, requestToken),
            token, new PuzzleFrameResult(null, WallImageStatus.Cancelled));

    private async Task<PuzzleFrameResult> LoadPackageFrameCoreAsync(PreparedMediaAsset asset, int width, int height, bool blur,
        CancellationToken token)
    {
        try
        {
            await _slots.WaitAsync(token);
            DecodedImageFrame? sharp;
            DecodedImageFrame? blurred;
            try
            {
                var bytes = asset.CopyBytes();
                var identity = "package:" + asset.Reference.Sha256;
                var result = await Task.Run(() => _decoder.DecodePackageFrameAsync(bytes, identity, width, height, token), token);
                sharp = result.Frame;
                var blurSource = sharp is not null && blur
                    ? (await Task.Run(() => _decoder.DecodePackageFrameAsync(bytes, identity, 512, 512, token, maximumEdge: 512), token)).Frame : null;
                blurred = blurSource is not null ? await Task.Run(() => LowResolutionBlur.Apply(blurSource), token) : null;
            }
            finally { _slots.Release(); }
            token.ThrowIfCancellationRequested();
            if (sharp is null || blur && blurred is null) return new(null, WallImageStatus.Invalid);
            return new(new PuzzleFrame(sharp.SourcePath, asset.Reference.Length, DateTime.MinValue,
                new PixelSize(sharp.SourceWidth, sharp.SourceHeight), new PixelSize(sharp.Width, sharp.Height),
                blurred is null ? default : new PixelSize(blurred.Width, blurred.Height), MakeBitmap(sharp),
                blurred is null ? null : MakeBitmap(blurred)), WallImageStatus.Loaded);
        }
        catch (OperationCanceledException) { return new(null, WallImageStatus.Cancelled); }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or OverflowException)
        { return new(null, WallImageStatus.Invalid, ex.GetType().Name); }
    }

    internal Task<PuzzleFrameResult> LoadPackageThumbnailAsync(PreparedMediaAsset asset, int edge, bool blur,
        CancellationToken token) => StartImageRequest(async requestToken =>
    {
        try
        {
            await _slots.WaitAsync(requestToken);
            DecodedImageFrame? sharp;
            DecodedImageFrame? blurred;
            try
            {
                var decoded = await Task.Run(() => _decoder.DecodePackageThumbnailAsync(asset.CopyBytes(),
                    "package:" + asset.Reference.Sha256, edge, requestToken), requestToken);
                sharp = decoded.Frame;
                blurred = sharp is not null && blur ? await Task.Run(() => LowResolutionBlur.Apply(sharp), requestToken) : null;
            }
            finally { _slots.Release(); }
            requestToken.ThrowIfCancellationRequested();
            if (sharp is null) return new PuzzleFrameResult(null, WallImageStatus.Invalid);
            // XAML objects are created only after returning to the caller's UI context.
            return new PuzzleFrameResult(new PuzzleFrame(sharp.SourcePath, asset.Reference.Length, DateTime.MinValue,
                new PixelSize(sharp.SourceWidth, sharp.SourceHeight), new PixelSize(sharp.Width, sharp.Height),
                blurred is null ? default : new PixelSize(blurred.Width, blurred.Height),
                MakeBitmap(sharp), blurred is null ? null : MakeBitmap(blurred)), WallImageStatus.Loaded);
        }
        catch (OperationCanceledException) { return new PuzzleFrameResult(null, WallImageStatus.Cancelled); }
    }, token, new PuzzleFrameResult(null, WallImageStatus.Cancelled));
}
