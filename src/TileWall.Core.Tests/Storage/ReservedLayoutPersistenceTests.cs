using TileWall.Core.Exchange;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Storage;

public sealed class ReservedLayoutPersistenceTests
{
    private static WallDocument ReservedWall() => WallDocument.Empty(3, 12) with
    {
        Settings = new() { LayoutLocked = true, ExpandColumns = true },
        Lanes =
        [
            new() { Groups = [new() { Id = "reserved", ReservedRows = 3, Members = [] },
                new() { Id = "content", ReservedRows = 4, Members =
                    [new TileMember { Id = "tile", LocalBounds = new(2, 1, 2, 2) }] }] },
            new() { Groups = [] }, new() { Groups = [] },
        ],
    };

    [Fact]
    public void CurrentSnapshotRetainsEmptyReservedGroupsAndInternalEmptyLanes()
    {
        var original = ReservedWall();
        var snapshot = UserDataSnapshotJson.Prepare(original);
        var restored = UserDataSnapshotJson.Read(snapshot.Manifest, name => snapshot.Files[name], _ => throw new InvalidOperationException());
        Assert.Equal(5, snapshot.Manifest.SchemaVersion);
        Assert.True(restored.Settings.LayoutLocked);
        Assert.Empty(restored.Lanes[0].Groups[0].Members);
        Assert.Equal(3, restored.Lanes[0].Groups[0].ReservedRows);
        Assert.Empty(restored.Lanes[1].Groups);
        Assert.Equal(WallJson.Serialize(original), WallJson.Serialize(restored));
    }

    [Fact]
    public void CompleteSelectionCarriesReservedGeometryAndLegacyFingerprintShapeStaysUnchanged()
    {
        var content = WallPackageContent.FromSelection(ReservedWall(), ["tile"]);
        WallPackageJson.ValidateContent(content);
        Assert.Equal(2, content.Groups.Count);
        Assert.Empty(content.Groups[0].MemberIds);
        Assert.Equal(3, content.ToDocument().Lanes[0].Groups[0].ReservedRows);
        var oldShape = content with { Groups = content.Groups.Select(group => group with { ReservedRows = null }).ToArray() };
        Assert.Equal(WallPackageJson.Fingerprint(oldShape, [], 5), WallPackageJson.Fingerprint(content, [], 5));
        Assert.NotEqual(WallPackageJson.Fingerprint(oldShape, []), WallPackageJson.Fingerprint(content, []));
    }
}
