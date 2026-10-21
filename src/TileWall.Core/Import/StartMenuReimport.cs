using TileWall.Core.Wall;

namespace TileWall.Core.Import;

public sealed record StartMenuMergeMember(WallMember Member, string IdentityKey);
public sealed record StartMenuMergeGroup(StartGroup Group, IReadOnlyList<StartMenuMergeMember> Members);
public sealed record StartMenuMergeResult(IReadOnlyList<StartGroup> Groups, IReadOnlyDictionary<string, string> MemberIds);

/// <summary>Incremental active-group merge. Import provenance never authorizes removing another member or group.</summary>
public static class StartMenuReimport
{
    public static StartMenuMergeResult Merge(WallDocument snapshot, IReadOnlyList<StartMenuMergeGroup> incoming,
        Func<WallMember, string?> existingIdentity, bool preserveChanges = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(existingIdentity);
        var existing = snapshot.Lanes.SelectMany(lane => lane.Groups).Where(group => !string.IsNullOrWhiteSpace(group.Title))
            .ToDictionary(group => StartMenuSourceCache.GroupNameKey(group.Title), StringComparer.Ordinal);
        var output = new List<StartGroup>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bucket in incoming.GroupBy(item => string.IsNullOrWhiteSpace(item.Group.Title)
                     ? "id:" + item.Group.Id : "name:" + StartMenuSourceCache.GroupNameKey(item.Group.Title), StringComparer.Ordinal))
        {
            var first = bucket.First().Group;
            var name = StartMenuSourceCache.GroupNameKey(first.Title);
            var matched = name.Length != 0 && existing.TryGetValue(name, out var group) ? group : null;
            var target = matched ?? first;
            var members = matched?.Members.ToList() ?? [];
            var byIdentity = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < members.Count; index++)
                if (existingIdentity(members[index]) is { } identity && !byIdentity.TryAdd(identity, index))
                    throw new InvalidOperationException("A destination group contains duplicate launch identities.");
            foreach (var item in bucket.SelectMany(item => item.Members))
            {
                if (string.IsNullOrWhiteSpace(item.IdentityKey)) throw new ArgumentException("An imported member has no launch identity.");
                var source = item.Member.ImportSource is { } origin ? origin with { OriginGroupId = target.Id } : null;
                if (byIdentity.TryGetValue(item.IdentityKey, out var index))
                {
                    var old = members[index];
                    members[index] = preserveChanges ? old with
                    {
                        TargetInput = item.Member.TargetInput, ImportSource = source,
                    } : item.Member with { Id = old.Id, Entry = old.Entry, ImportSource = source };
                    ids.Add(item.Member.Id, old.Id);
                }
                else
                {
                    byIdentity.Add(item.IdentityKey, members.Count);
                    members.Add(item.Member with { ImportSource = source });
                    ids.Add(item.Member.Id, item.Member.Id);
                }
            }
            var groupSource = first.ImportSource is { } incomingSource
                ? incomingSource with { OriginGroupId = target.Id }
                : null;
            output.Add(target with
            {
                Members = members.ToArray(),
                ImportSource = groupSource,
                UserClaimed = false,
            });
        }
        return new(output.AsReadOnly(), ids);
    }
}
