using TileWall.Core.Entries;
using TileWall.Core.Wall;

namespace TileWall.Core.Storage;

public sealed partial class WallStore
{
    /// <summary>Run on the existing STA storage executor; only bounded temporary bytes reach COM.</summary>
    public LaunchIdentity ObservePackageEntry(ManagedEntryKind kind, ReadOnlyMemory<byte> originalBytes,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(kind) || originalBytes.Length is < 1 or > EntryObservation.MaximumBytes)
            throw new InvalidDataException("Invalid or oversized package shortcut.");
        var bytes = originalBytes.ToArray();
        var directory = _paths.Under("Cache", "Exchange");
        var temporary = _paths.Under("Cache", "Exchange", Guid.NewGuid().ToString("N") +
            (kind == ManagedEntryKind.Lnk ? ".lnk" : ".url"));
        if (kind == ManagedEntryKind.Url)
            return EntryObservation.Observe(_identities, kind, temporary, bytes);
        try
        {
            _files.CreateDirectory(directory);
            _files.WriteAllBytes(temporary, bytes);
            token.ThrowIfCancellationRequested();
            var fields = _links.Read(temporary);
            token.ThrowIfCancellationRequested();
            // The parser saw a protected file containing exactly the observed bytes.
            if (Hash(_files.ReadAllBytes(temporary)) != Hash(bytes)) throw new IOException("Temporary shortcut changed.");
            return EntryObservation.Observe(_identities, kind, temporary, bytes, fields);
        }
        finally { _files.Delete(temporary); }
    }
}
