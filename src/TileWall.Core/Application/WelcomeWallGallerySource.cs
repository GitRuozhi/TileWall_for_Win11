using TileWall.Core.Configuration;
using TileWall.Core.Storage;

namespace TileWall.Core.Application;

/// <summary>Freeze bundled seasons for joint publication by WallStore; no formal IO happens here.</summary>
internal static class WelcomeWallGallerySource
{
    private static readonly string[] Names = ["01-spring", "02-summer", "03-autumn", "04-winter"];
    public static PreparedManagedGallery Prepare(IFileStore files, string dataRoot,
        IReadOnlyList<PreparedMediaAsset> originals, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(originals);
        token.ThrowIfCancellationRequested();
        if (originals.Count != Names.Length) throw new ArgumentException("Exactly four seasons are required.", nameof(originals));
        var gallery = PreparedManagedGallery.FromVerifiedFiles(originals.Select((asset, index) =>
            new PreparedManagedGalleryFile(Names[index] + asset.Reference.Extension, asset)));
        token.ThrowIfCancellationRequested();
        return gallery;
    }
}
