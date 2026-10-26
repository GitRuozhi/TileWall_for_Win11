using System.Security.Cryptography;
using TileWall.Core.Imaging;
using TileWall.Shell.Interop;

namespace TileWall.Graphics;

internal enum BundledFallbackIcon { File, Folder, Website, Unknown }

public sealed partial class WallImageService
{
    private static readonly IReadOnlyDictionary<BundledFallbackIcon, (string File, string Sha256)> GenericFallbackAssets =
        new Dictionary<BundledFallbackIcon, (string File, string Sha256)>
        {
            [BundledFallbackIcon.File] = ("file.png", "0B366279FA3DBCEA09E6444E33110BB8F2AE5037CDF293ABD533145418412EAB"),
            [BundledFallbackIcon.Folder] = ("folder.png", "21139101E15ED2A87DAD01E25F4186E64B9CBD471F842131BAF4180FC998BCDE"),
            [BundledFallbackIcon.Website] = ("website.png", "2192C1E7251597CC7308B26A03430380A0C6EE8B239FD6D28433FE566FEBE1BF"),
            [BundledFallbackIcon.Unknown] = ("unknown.png", "D87C8050F7F1A1B9CF9B706577BB198EC67369241767D6BF0EDD7C8B6BC5B109"),
        };

    /// <summary>Loads a generic first-party placeholder through the shared decoder and derived cache.</summary>
    private Task<Prepared?> TryPrepareBundledFallbackAsync(BundledFallbackIcon icon, int width, int height,
        CancellationToken ct)
    {
        if (!GenericFallbackAssets.TryGetValue(icon, out var asset)) return Task.FromResult<Prepared?>(null);
        return TryPrepareBundledFileAsync("Generic", asset.File, "placeholder-" + icon.ToString().ToLowerInvariant(),
            asset.Sha256, width, height, ct);
    }

    /// <summary>Prepares a verified offline website logo using the shared puzzle decoder and cache.</summary>
    private async Task<Prepared?> TryPrepareBundledWebsiteAsync(Uri uri, int width, int height,
        CancellationToken ct)
    {
        if (!WebsiteIconCatalog.TryGet(uri, out var match)) return null;
        var asset = match.Default;
        if (asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit) ||
            asset.Width is < 1 or > 4096 || asset.Height is < 1 or > 4096)
            return UnavailableWebsiteIcon(match.Id);

        ct.ThrowIfCancellationRequested();
        return await TryPrepareBundledFileAsync("Websites", asset.RelativePath, "website:" + match.Id,
            asset.Sha256, width, height, ct, asset.Width, asset.Height).ConfigureAwait(false)
            ?? UnavailableWebsiteIcon(match.Id);
    }

    private static Prepared UnavailableWebsiteIcon(string id) =>
        new(WallImageStatus.Fallback, null, 0, 0, "BundledWebsiteUnavailable:" + id) { CacheAllowed = false };

    private async Task<Prepared?> TryPrepareBundledSystemAsync(string? target, int width, int height,
        CancellationToken ct)
    {
        if (!SystemIconCatalog.TryGet(target, null, out var asset))
        {
            var path = NormalizePath(target);
            if (path is null || !SystemIconCatalog.TryGet(path, SystemIconHostPaths(), out asset)) return null;
        }
        if (asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit) ||
            asset.Width is < 1 or > 4096 || asset.Height is < 1 or > 4096)
            return new Prepared(WallImageStatus.Fallback, null, 0, 0, "BundledSystemUnavailable:" + asset.Id) { CacheAllowed = false };
        var prepared = await TryPrepareBundledFileAsync("System", asset.RelativePath, "system:" + asset.Id,
            asset.Sha256, width, height, ct, asset.Width, asset.Height).ConfigureAwait(false);
        // The native failure may be temporary even while this bundled image is
        // valid. Keep decoded asset reuse, but retry native on the next request.
        return (prepared ?? new Prepared(WallImageStatus.Fallback, null, 0, 0,
            "BundledSystemUnavailable:" + asset.Id)) with { CacheAllowed = false };
    }

    private static SystemIconPaths SystemIconHostPaths() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        new Dictionary<string, string?>
        {
            ["desktop"] = KnownFolderPaths.Desktop, ["documents"] = KnownFolderPaths.Documents,
            ["downloads"] = KnownFolderPaths.Downloads,
            ["profile"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ["pictures"] = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            ["music"] = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            ["videos"] = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            ["fonts"] = Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            ["startup"] = Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            ["tools"] = Environment.GetFolderPath(Environment.SpecialFolder.AdminTools),
        });

    private async Task<Prepared?> TryPrepareBundledFileAsync(string area, string file, string identity,
        string expectedSha256, int width, int height, CancellationToken ct, int? sourceWidth = null, int? sourceHeight = null)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var assetRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", "FallbackIcons"));
            var path = Path.GetFullPath(Path.Combine(assetRoot, area, file));
            if (!path.StartsWith(assetRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path))
                return null;
            // The manifest's digest is authoritative. A length/mtime memo must
            // not retain a bad digest after a repaired bundled file, and this
            // read lease keeps verification and decoding on the same payload.
            using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (original.Length is < 1 || original.Length > TileWall.Core.Storage.PreparedMediaAsset.MaximumBytes ||
                !StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(SHA256.HashData(original)), expectedSha256))
                return null;

            var budget = CachedIconBudget(width, false, MemberImagePurpose.Thumbnail, height);
            var cacheIdentity = identity.StartsWith("website:", StringComparison.Ordinal)
                ? "website-icon:" + identity["website:".Length..] + ":" + WebsiteIconCatalog.VersionStamp
                : identity.StartsWith("system:", StringComparison.Ordinal)
                    ? "system-icon:" + identity["system:".Length..] + ":" + SystemIconCatalog.VersionStamp
                    : "fallback-icon:" + identity;
            var decoded = await PreparePuzzleAsync(path, budget, ct,
                identityPath: cacheIdentity, contentHash: expectedSha256).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (decoded.Pair?.Sharp is not { } sharp) return null;
            if (sourceWidth is not null && sharp.SourceWidth != sourceWidth ||
                sourceHeight is not null && sharp.SourceHeight != sourceHeight ||
                !BundledPixelsVisible(sharp.BgraPremultiplied.Span)) return null;

            var dimensions = sourceWidth is null || sourceHeight is null
                ? $"{sharp.SourceWidth}x{sharp.SourceHeight}"
                : $"{sourceWidth.Value}x{sourceHeight.Value}";
            return new(WallImageStatus.Fallback, sharp.BgraPremultiplied.ToArray(), sharp.Width, sharp.Height,
                $"BundledIcon:{identity}:{dimensions}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return null;
        }
    }

    private static bool BundledPixelsVisible(ReadOnlySpan<byte> pixels)
    {
        for (var index = 3; index < pixels.Length; index += 4)
            if (pixels[index] != 0) return true;
        return false;
    }
}
