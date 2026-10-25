using TileWall.Core.Imaging;
using TileWall.Core.Storage;

namespace TileWall.Graphics;

public sealed partial class WallImageService
{
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
