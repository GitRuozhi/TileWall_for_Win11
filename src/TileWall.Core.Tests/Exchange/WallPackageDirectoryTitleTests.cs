using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using TileWall.Core.Exchange;
using Xunit;

namespace TileWall.Core.Tests.Exchange;

public sealed class WallPackageDirectoryTitleTests
{
    [Theory]
    [InlineData("photos.v2")]
    [InlineData("资料.2026")]
    public void ConfirmedDirectoryNameSurvivesWallAndPackageWithoutAnotherDirectoryObservation(string directoryName)
    {
        var target = Path.Combine(Path.GetTempPath(), "tilewall-title-only", directoryName);
        // Confirmation already admitted the directory. Store its observed title;
        // neither wall JSON nor package projection needs the path to exist here.
        var member = new TileMember
        {
            Id = "directory", LocalBounds = new GridRect(0, 0, 2, 2), TargetInput = target,
            Entry = new("directory", "Objects/directory/entry.lnk", ManagedEntryKind.Lnk),
            TargetDisplayName = MemberPresentationRules.AutomaticTitle(target, targetIsFolder: true),
            Images = new() { Kind = MemberImageSourceKind.None },
        };
        var wall = WallDocument.Empty(1, 4) with
        {
            Lanes = [new() { Groups = [new() { Id = "group", Members = [member] }] }],
        };
        var savedWall = WallJson.Deserialize(WallJson.Serialize(wall));
        var package = WallPackageContent.FromSelection(savedWall, [member.Id]);
        var restoredPackage = WallPackageJson.Deserialize<WallPackageContent>(WallPackageJson.Serialize(package)).ToDocument();

        Assert.Equal(directoryName, Assert.Single(package.Members).TargetDisplayName);
        foreach (var candidate in new[] { wall, savedWall, restoredPackage })
        {
            var restored = Assert.Single(candidate.EnumerateAllMembers());
            Assert.Equal(target, restored.TargetInput);
            Assert.Equal(directoryName, restored.TargetDisplayName);
            Assert.Equal(directoryName, MemberPresentationRules.ResolveTitle(restored));
        }
    }
}
