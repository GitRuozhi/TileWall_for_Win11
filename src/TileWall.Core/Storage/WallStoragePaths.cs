using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Wall;

namespace TileWall.Core.Storage;

internal sealed class WallStoragePaths(IFileStore files, string dataRoot)
{
    private readonly IFileStore _files = files;
    public string Root { get; } = RequireRoot(dataRoot);
    public string Config => Under("state.json");
    public string Previous => Under("Recovery", "state.prev.json");
    public string Staging => Under("Staging");
    public string RecoveryEntries => Under("Recovery", "Entries");
    public string Objects => Under("Objects");
    public string MediaRoot => Under("Media");
    public string Snapshots => Under("Snapshots");
    public string Sources => Under("Sources");

    public string Gallery(ManagedGalleryReference gallery)
    {
        if (!PreparedManagedGallery.IsValidReference(gallery)) throw UnsafePath("Unsafe managed gallery reference.");
        return Under("Sources", gallery.ContentId);
    }

    private static string RequireRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetFullPath(path), path.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Data root must be a normalized absolute path.", nameof(path));
        return path.TrimEnd(Path.DirectorySeparatorChar);
    }

    public string Under(params string[] segments)
    {
        if (segments.Length == 0 || segments.Any(s => string.IsNullOrEmpty(s) || s is "." or ".." ||
            s.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || s.EndsWith('.') || s.EndsWith(' ')))
            throw UnsafePath("Invalid managed path segment.");
        var path = Path.GetFullPath(Path.Combine([Root, .. segments]));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw UnsafePath("Managed path escapes data root.");
        return path;
    }

    public string Entry(ManagedEntryReference entry)
    {
        if (!ValidId(entry.ObjectId) || !ValidEntry(entry)) throw UnsafePath("Unsafe managed entry reference.");
        var parts = entry.RelativePath.Split('/');
        return Under(parts);
    }

    public string Media(ManagedMediaReference media)
    {
        if (!PreparedMediaAsset.IsValidReference(media)) throw UnsafePath("Unsafe managed media reference.");
        return Under("Media", media.Sha256, "original" + media.Extension);
    }

    public static bool ValidId(string? id) =>
        id is { Length: > 0 and <= 200 } && id != "." && id != ".." &&
        EntryNames.Validate(id).Count == 0 && id.IndexOfAny(['/', '\\', ':']) < 0;

    public static bool ValidEntry(ManagedEntryReference? entry)
    {
        if (entry is null || !ValidId(entry.ObjectId) || !Enum.IsDefined(entry.Kind)) return false;
        var parts = entry.RelativePath?.Split('/');
        if (parts is not { Length: 3 } || parts[0] != "Objects" || parts[1] != entry.ObjectId) return false;
        var ext = entry.Kind == ManagedEntryKind.Lnk ? ".lnk" : ".url";
        return parts[2].EndsWith(ext, StringComparison.OrdinalIgnoreCase) &&
            EntryNames.Validate(parts[2][..^ext.Length]).Count == 0;
    }

    private static IOException UnsafePath(string message) => new(message)
    { Data = { [nameof(WallOpenBlockReason)] = WallOpenBlockReason.UnsafePath } };

    internal static bool IsUnsafePath(Exception error) =>
        error.Data[nameof(WallOpenBlockReason)] is WallOpenBlockReason.UnsafePath;

    public void Guard(string path, bool allowExternalRead = false)
        => GuardCore(path, allowExternalRead, null);

    /// <summary>One validation pass only; actual IO always uses the uncached Guard.</summary>
    public Action<string> CreateValidationGuard()
    {
        var inspected = new Dictionary<string, FileStorePathInfo>(StringComparer.OrdinalIgnoreCase);
        return path => GuardCore(path, false, inspected);
    }

    private void GuardCore(string path, bool allowExternalRead,
        Dictionary<string, FileStorePathInfo>? inspected)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path)
            throw UnsafePath($"Unnormalized path: {path}");
        var isManagedPath = path.Equals(Root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!allowExternalRead && !isManagedPath)
            throw UnsafePath($"Path escapes data root: {path}");

        var full = path;
        var cursor = Path.GetPathRoot(full)!;
        foreach (var part in full[cursor.Length..].Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, part);
            if (inspected is null || !inspected.TryGetValue(cursor, out var info))
            {
                info = _files.GetPathInfo(cursor);
                inspected?.Add(cursor, info);
            }
            if (info.IsReparsePoint && !(allowExternalRead && !isManagedPath))
                throw UnsafePath($"Reparse point in managed path: {cursor}");
            if (info.Exists && cursor != full && !info.IsDirectory)
                throw UnsafePath($"File in directory path: {cursor}");
        }
    }

    public void GuardTree(string path)
    {
        Guard(path);
        var info = _files.GetPathInfo(path);
        if (!info.Exists) return;
        if (!info.IsDirectory) throw UnsafePath($"Expected directory: {path}");
        foreach (var file in _files.ListFiles(path)) Guard(file);
        foreach (var child in _files.ListDirectories(path)) GuardTree(child);
    }

}
