using System.Security.Cryptography;
using System.Text;
using TileWall.Core.Import;
using TileWall.Core.Wall;

namespace TileWall.Core.Entries;

/// <summary>The actual shortcut-byte identity contract shared by storage and exchange.</summary>
public static class EntryObservation
{
    public const int MaximumBytes = StartMenuSourceCache.MaximumSourceBytes;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static LaunchIdentity Observe(LaunchIdentityService identities, ManagedEntryKind kind,
        string path, byte[] bytes, LinkFields? fields = null)
    {
        if (!Enum.IsDefined(kind) || bytes.Length is < 1 or > MaximumBytes)
            throw new InvalidDataException("Invalid or oversized shortcut.");
        var identity = identities.ResolveObservedInput(kind == ManagedEntryKind.Lnk ? EntryKind.Lnk : EntryKind.Url,
            path, bytes, fields);
        if (identity is not null) return identity;
        // A structurally valid InternetShortcut can contain opaque Shell metadata.
        // A malformed URL= value must never become an opaque identity.
        if (kind == ManagedEntryKind.Url && UrlShortcut.ReadUrlLine(bytes) is null &&
            DecodeUrlMetadata(bytes).Split(['\r', '\n'])
                .Any(line => line.Trim().Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase)))
            return new("opaque-url:" + Convert.ToHexString(SHA256.HashData(bytes)), path, "", "", true);
        throw new InvalidDataException("Managed entry has no valid launch identity.");
    }

    private static string DecodeUrlMetadata(ReadOnlySpan<byte> bytes)
    {
        // The BOM belongs to the encoding, not to the INI section name. Remove it
        // before the existing strict UTF-8 / Latin-1 fallback so opaque metadata
        // accepted by Schema4 remains readable even with non-UTF-8 icon fields.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }
}
