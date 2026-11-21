using System.Security.Cryptography;
using TileWall.Core.Import;
using TileWall.Core.Wall;

namespace TileWall.Core.Entries;

/// <summary>The actual shortcut-byte identity contract shared by storage and exchange.</summary>
public static class EntryObservation
{
    public const int MaximumBytes = StartMenuSourceCache.MaximumSourceBytes;

    public static LaunchIdentity Observe(LaunchIdentityService identities, ManagedEntryKind kind,
        string path, byte[] bytes, LinkFields? fields = null, ReadOnlyMemory<byte> idList = default)
    {
        if (!Enum.IsDefined(kind) || bytes.Length is < 1 or > MaximumBytes)
            throw new InvalidDataException("Invalid or oversized shortcut.");
        var identity = identities.ResolveObservedInput(kind == ManagedEntryKind.Lnk ? EntryKind.Lnk : EntryKind.Url,
            path, bytes, fields);
        if (identity is not null)
        {
            if (fields is { HasIdList: true, TargetPath: null, ShellIdListHash: not null } && !idList.IsEmpty &&
                Convert.ToHexString(SHA256.HashData(idList.Span)) == fields.ShellIdListHash &&
                NativeShellTargetSnapshot.ReadAppsFolderApplicationId(idList.Span) is { } id)
                return identity with { AppsFolderApplicationId = id };
            return identity;
        }
        // A structurally valid InternetShortcut can contain opaque Shell metadata.
        // A malformed URL= value must never become an opaque identity.
        if (kind == ManagedEntryKind.Url && UrlShortcut.ReadMetadata(bytes) is { HasInternetShortcut: true, Url: null })
            return new("opaque-url:" + Convert.ToHexString(SHA256.HashData(bytes)), path, "", "", true);
        throw new InvalidDataException("Managed entry has no valid launch identity.");
    }

}
