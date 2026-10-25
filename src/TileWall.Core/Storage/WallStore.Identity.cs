using TileWall.Core.Entries;
using TileWall.Core.Wall;

namespace TileWall.Core.Storage;

public sealed partial class WallStore
{
    private static HashSet<string> ActiveMemberIds(WallDocument document) =>
        document.Lanes.SelectMany(lane => lane.Groups).SelectMany(group => group.Members)
            .Select(member => member.Id).ToHashSet(StringComparer.Ordinal);

    private void ObserveIdentity(ManagedEntryKind kind, string path, byte[] bytes, string hash)
    {
        var key = IdentityFactKey(kind, hash);
        if (_identityFacts.ContainsKey(key)) return;
        var identity = EntryObservation.Observe(_identities, kind, path, bytes);
        _identityFacts.Add(key, identity);
    }

    private void ValidateGroupIdentities(WallDocument document, WallJournal journal)
    {
        var hashes = journal.Observed.ToDictionary(entry => entry.MemberId, entry => entry.Hash, StringComparer.Ordinal);
        foreach (var operation in journal.Ops)
            if (operation.NewPath is not null && operation.NewHash is { } hash) hashes[operation.MemberId] = hash;
        ValidateGroupIdentities(document, hashes);
    }

    private void ValidateGroupIdentities(WallDocument document, IReadOnlyDictionary<string, string> hashes)
    {
        foreach (var group in document.Lanes.SelectMany(lane => lane.Groups))
        {
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in group.Members)
            {
                if (member.Entry is not { } entry) continue;
                if (!hashes.TryGetValue(member.Id, out var hash) ||
                    !_identityFacts.TryGetValue(IdentityFactKey(entry.Kind, hash), out var identity) || identity is null)
                    throw new IOException($"Entry identity was not observed for this transaction: {member.Id}");
                if (!identities.Add(identity.Key))
                    throw new InvalidDataException($"DUPLICATE_GROUP_LAUNCH:{group.Id}");
            }
        }
    }

    private static string IdentityFactKey(ManagedEntryKind kind, string hash) => $"{kind}:{hash}";
}
