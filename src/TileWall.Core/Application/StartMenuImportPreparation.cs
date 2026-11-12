using TileWall.Core.Entries;
using TileWall.Core.Import;
using TileWall.Core.Shell;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using System.Globalization;

namespace TileWall.Core.Application;

/// <summary>One observed shortcut is one short apartment-owned job; background hashing never occupies the storage queue.</summary>
internal static class StartMenuImportPreparation
{
    internal static async Task<StartMenuCachedSource> PrepareSourceAsync(IWallStorageExecutor executor,
        StartMenuCandidate candidate, byte[] observedBytes, string sha256, LaunchIdentityService identities,
        ILnkFileService links, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        candidate = candidate with { NotImportableReasons = Array.AsReadOnly(candidate.NotImportableReasons.ToArray()) };
        observedBytes = observedBytes.ToArray();
        var prepared = await executor.ExecuteAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            var fields = candidate.Kind == EntryKind.Lnk ? links.Read(candidate.FullPath) : null;
            token.ThrowIfCancellationRequested();
            var identity = identities.ResolveObservedInput(candidate.Kind, candidate.FullPath, observedBytes, fields);
            token.ThrowIfCancellationRequested();
            var urlIcon = candidate.Kind == EntryKind.Url ? ReadUrlIcon(observedBytes) : default;
            return new StartMenuCachedSource
            {
                Candidate = candidate, SourceSha256 = sha256, Identity = identity, LinkFields = fields,
                UrlIconPath = urlIcon.Path, UrlIconIndex = urlIcon.Index,
                ErrorCode = identity is null ? "InvalidTarget" : null,
            };
        }).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return prepared;
    }

    private static (string? Path, int? Index) ReadUrlIcon(byte[] bytes)
    {
        var metadata = UrlShortcut.ReadMetadata(bytes);
        int? index = int.TryParse(metadata.IconIndex, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : null;
        return (metadata.IconFile, index);
    }

    internal static LaunchIdentity? TryIdentity(WallMember member, string dataRoot,
        LaunchIdentityService identities, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var managed = member.Entry is { } entry && EntryPathGuard.IsEntryRelativePathAllowed(dataRoot, member.Id, entry.RelativePath)
                ? Path.GetFullPath(Path.Combine(dataRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar))) : null;
            var result = identities.ResolveMember(member, managed);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Warn("LaunchIdentity", $"member={member.Id} identity unreadable: {ex.Message}");
            return null;
        }
    }
}
