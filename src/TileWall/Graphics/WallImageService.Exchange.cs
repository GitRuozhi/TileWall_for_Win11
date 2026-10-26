using TileWall.Core.Imaging;
using TileWall.Core.Storage;

namespace TileWall.Graphics;

public sealed partial class WallImageService
{
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
