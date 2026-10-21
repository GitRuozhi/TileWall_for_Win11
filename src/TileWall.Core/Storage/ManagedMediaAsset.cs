using System.Security.Cryptography;
using TileWall.Core.Wall;

namespace TileWall.Core.Storage;

/// <summary>
/// Original image bytes already probed by the image decoder. This boundary freezes and hashes
/// those exact bytes; it does not decode them a second time or retain an external write path.
/// </summary>
public sealed class PreparedMediaAsset
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    private readonly byte[] _bytes;

    private PreparedMediaAsset(byte[] bytes, string extension)
    {
        _bytes = bytes;
        Reference = new(Convert.ToHexString(SHA256.HashData(bytes)), extension, bytes.LongLength);
    }

    public ManagedMediaReference Reference { get; }

    public static PreparedMediaAsset FromVerifiedImage(ReadOnlyMemory<byte> originalBytes, string extension)
    {
        if (originalBytes.Length is < 1 or > MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(originalBytes));
        extension = NormalizeExtension(extension);
        return new(originalBytes.ToArray(), extension);
    }

    internal byte[] CopyBytes() => _bytes.ToArray();

    public static string NormalizeExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        var value = extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
        value = value switch { ".jpeg" or ".jpe" => ".jpg", ".tif" => ".tiff", _ => value };
        if (value.Length is < 2 or > 10 || value.AsSpan(1).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789") >= 0)
            throw new ArgumentException("Unsafe media extension.", nameof(extension));
        return value;
    }

    public static bool IsValidReference(ManagedMediaReference? media) => media is not null &&
        media.Sha256 is { Length: 64 } hash && hash.All(Uri.IsHexDigit) &&
        hash == hash.ToUpperInvariant() && media.Length is > 0 and <= MaximumBytes &&
        ValidExtension(media.Extension);

    private static bool ValidExtension(string? extension)
    {
        try { return extension is not null && NormalizeExtension(extension) == extension; }
        catch (ArgumentException) { return false; }
    }
}
