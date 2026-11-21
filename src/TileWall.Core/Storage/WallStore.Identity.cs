using System.Collections.Frozen;
using TileWall.Core.Entries;
using TileWall.Core.Wall;

namespace TileWall.Core.Storage;

public sealed partial class WallStore
{
    internal (LinkFields? Link, string? Url, string Sha256) ObserveManagedEntry(ManagedEntryReference entry)
    {
        var path = _paths.Entry(entry);
        // Keep the actual file protected against writes and replacement throughout
        // the COM read. GuardedWallLinks independently rechecks the path before IO.
        using var source = _files.OpenReadProtected(path);
        if (source.Length is < 1 or > EntryObservation.MaximumBytes)
            throw new InvalidDataException("Invalid or oversized shortcut.");
        var bytes = new byte[(int)source.Length];
        source.ReadExactly(bytes);
        var hash = Hash(bytes);
        return entry.Kind == ManagedEntryKind.Lnk
            ? (_links.Read(path), null, hash)
            : (null, UrlShortcut.ReadUrlLine(bytes), hash);
    }

    private static HashSet<string> ActiveMemberIds(WallDocument document) =>
        document.Lanes.SelectMany(lane => lane.Groups).SelectMany(group => group.Members)
            .Select(member => member.Id).ToHashSet(StringComparer.Ordinal);

    private sealed record EntryFact(LaunchIdentity Identity, string? Command);

    private void ObserveIdentity(ManagedEntryKind kind, string path, byte[] bytes, string hash)
    {
        var key = IdentityFactKey(kind, hash);
        if (_identityFacts.ContainsKey(key)) return;
        var fields = kind == ManagedEntryKind.Lnk && bytes.Length is > 0 and <= EntryObservation.MaximumBytes
            ? _links.Read(path) : null;
        var identity = EntryObservation.Observe(_identities, kind, path, bytes, fields);
        var command = identity.Arguments.Length > 0 && fields?.TargetPath is { } rawPath
            ? LaunchCommandText.ForFile(identity with { Target = rawPath }) ?? LaunchCommandText.ForFile(identity)
            : null;
        _identityFacts.Add(key, new(identity, command));
    }

    private FrozenDictionary<string, string> GroupLaunchCommands(WallDocument document, WallJournal journal,
        bool validateIdentities = true)
    {
        var hashes = journal.Observed.ToDictionary(entry => entry.MemberId, entry => entry.Hash, StringComparer.Ordinal);
        foreach (var operation in journal.Ops)
            if (operation.NewPath is not null && operation.NewHash is { } hash) hashes[operation.MemberId] = hash;
        return GroupLaunchCommands(document, hashes, validateIdentities);
    }

    private FrozenDictionary<string, string> GroupLaunchCommands(WallDocument document,
        IReadOnlyDictionary<string, string> hashes, bool validateIdentities = true)
    {
        var commands = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in document.Lanes.SelectMany(lane => lane.Groups))
        {
            var identities = validateIdentities ? new HashSet<string>(StringComparer.Ordinal) : null;
            foreach (var member in group.Members)
            {
                if (member.Entry is not { } entry) continue;
                if (!hashes.TryGetValue(member.Id, out var hash) ||
                    !_identityFacts.TryGetValue(IdentityFactKey(entry.Kind, hash), out var fact))
                {
                    if (validateIdentities) throw new IOException($"Entry identity was not observed for this transaction: {member.Id}");
                    continue;
                }
                if (identities?.Add(fact.Identity.Key) == false)
                    throw new InvalidDataException($"DUPLICATE_GROUP_LAUNCH:{group.Id}");
                if (fact.Command is { } command) commands[member.Id] = command;
            }
        }
        return commands.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static string IdentityFactKey(ManagedEntryKind kind, string hash) => $"{kind}:{hash}";
}
