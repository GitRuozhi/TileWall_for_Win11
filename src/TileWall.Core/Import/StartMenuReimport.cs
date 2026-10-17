using TileWall.Core.Wall;

namespace TileWall.Core.Import;

public sealed record StartMenuPrunedSnapshot(WallDocument Document, IReadOnlyList<WallMember> Removed);

public static class StartMenuReimport
{
    public static StartMenuPrunedSnapshot Prune(WallDocument snapshot)
    {
        var removed = new List<WallMember>();
        var lanes = snapshot.Lanes.Select(lane => lane with
        {
            Groups = lane.Groups.Select(group =>
            {
                if (group.UserClaimed || group.ImportSource?.Kind != ImportSourceKind.StartMenu) return group;
                var retained = group.Members.Where(member =>
                {
                    var replace = !member.UserClaimed && member.ImportSource is { Kind: ImportSourceKind.StartMenu } origin &&
                        origin.OriginGroupId == group.Id && origin.BatchId == group.ImportSource.BatchId;
                    if (replace) removed.Add(member);
                    return !replace;
                }).ToArray();
                // Remaining manual members become an independent, protected group with the same identity and cells.
                return group with { Members = retained, UserClaimed = retained.Length != 0 };
            }).Where(group => group.Members.Count != 0 || group.ImportSource is null || group.UserClaimed).ToArray(),
        }).Where(lane => lane.Groups.Count != 0 || lane.ImportBatchId is null).ToArray();
        return new(snapshot with { Lanes = lanes, Columns = lanes.Length }, removed);
    }
}
