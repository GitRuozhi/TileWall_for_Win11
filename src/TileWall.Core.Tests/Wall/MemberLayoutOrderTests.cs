using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class MemberLayoutOrderTests
{
    private static TileMember Tile(string id, GridRect bounds) => new() { Id = id, LocalBounds = bounds };
    private static WallDocument Document(params WallMember[] members) => new()
    {
        Columns = 1, Rows = 8,
        Lanes = [new WallLane { Groups = [new StartGroup { Id = "group", Members = members }] }],
    };

    [Fact]
    public void ProjectionAndAcceptedDocumentUseTopLeftCellsInsteadOfInputOrderOrSize()
    {
        var document = Document(Tile("bottom-right", new GridRect(6, 4, 2, 1)),
            Tile("tall-left", new GridRect(0, 0, 2, 4)), Tile("top-right", new GridRect(4, 0, 2, 1)),
            Tile("middle", new GridRect(2, 1, 2, 2)));
        string[] expected = ["tall-left", "top-right", "middle", "bottom-right"];
        Assert.Equal(expected, MemberLayoutOrder.Order(document.Lanes[0].Groups[0].Members).Select(member => member.Id));
        Assert.Equal(expected, Assert.Single(WallLayoutEngine.Measure(document).Groups).Members.Select(member => member.MemberId));
        var normalized = WallLayoutEngine.NormalizeExpanded(document);
        Assert.True(normalized.Success, normalized.Detail);
        Assert.Equal(expected, normalized.Proposal!.Document.Lanes[0].Groups[0].Members.Select(member => member.Id));
        var renamed = WallLayoutEngine.TryApply(document, new RenameGroupAction(0, "group", "Renamed"), 1000, 1000);
        Assert.True(renamed.Success, renamed.Detail);
        Assert.Equal(expected, renamed.Proposal!.Document.Lanes[0].Groups[0].Members.Select(member => member.Id));
        Assert.Equal("bottom-right", document.Lanes[0].Groups[0].Members[0].Id);
    }

    [Fact]
    public void MoveAndReplaceFollowResultingSpaceRatherThanPreviousMemberOrder()
    {
        var document = Document(Tile("left", new GridRect(0, 0, 1, 1)), Tile("right", new GridRect(4, 0, 1, 1)));
        var moved = WallLayoutEngine.TryApply(document, new MoveMemberAction(0, "left", new GridRect(6, 1, 1, 1), "group"), 1000, 1000);
        Assert.True(moved.Success, moved.Detail);
        Assert.Equal(["right", "left"], moved.Proposal!.Document.Lanes[0].Groups[0].Members.Select(member => member.Id));
        var replaced = WallLayoutEngine.TryApply(document,
            new ReplaceMemberAction(0, "left", Tile("left", new GridRect(6, 1, 1, 1))), 1000, 1000);
        Assert.True(replaced.Success, replaced.Detail);
        Assert.Equal(["right", "left"], replaced.Proposal!.Document.Lanes[0].Groups[0].Members.Select(member => member.Id));
    }

    public static IEnumerable<object[]> NewMembers()
    {
        yield return [Tile("new", new GridRect(0, 0, 2, 2))];
        yield return [new ClockMember { Id = "new", LocalBounds = new GridRect(0, 0, 2, 1) }];
        yield return [new PuzzleMember { Id = "new", LocalBounds = new GridRect(0, 0, 4, 4), Partitions = [new GridRect(0, 0, 4, 4)] }];
    }

    [Theory]
    [MemberData(nameof(NewMembers))]
    public void CreatingAnyMemberKindSkipsEarlierHolesAndPreservesExistingGeometry(WallMember incoming)
    {
        var document = Document(Tile("last", new GridRect(2, 2, 2, 2)), Tile("first", new GridRect(0, 0, 2, 2)));
        var before = WallJson.Serialize(document);
        var result = WallLayoutEngine.TryApply(document, new AddMemberAction(0, incoming, "group"), 1000, 1000);
        Assert.True(result.Success, result.Detail);
        var members = result.Proposal!.Document.Lanes[0].Groups[0].Members;
        Assert.Equal(["first", "last", "new"], members.Select(member => member.Id));
        Assert.Equal(new GridRect(4, 2, incoming.LocalBounds.Width, incoming.LocalBounds.Height), members[^1].LocalBounds);
        foreach (var original in document.Lanes[0].Groups[0].Members)
            Assert.Equal(original.LocalBounds, members.Single(member => member.Id == original.Id).LocalBounds);
        Assert.Equal(incoming.GetType(), members[^1].GetType());
        Assert.Equal(before, WallJson.Serialize(document));
    }

    [Fact]
    public void AppendSearchSkipsSpaceOccupiedByTallerEarlierMember()
    {
        var document = Document(Tile("tall", new GridRect(4, 0, 4, 4)), Tile("last", new GridRect(0, 2, 2, 2)));
        var result = WallLayoutEngine.TryApply(document,
            new AddMemberAction(0, Tile("new", new GridRect(0, 0, 4, 1)), "group"), 1000, 1000);
        Assert.True(result.Success, result.Detail);
        var members = result.Proposal!.Document.Lanes[0].Groups[0].Members;
        Assert.Equal(new GridRect(0, 4, 4, 1), members.Single(member => member.Id == "new").LocalBounds);
        Assert.Equal(new GridRect(4, 0, 4, 4), members.Single(member => member.Id == "tall").LocalBounds);
    }

    [Fact]
    public void NoCapacityRejectsWholeAppendWithoutMovingExistingMembers()
    {
        var document = Document(Tile("left", new GridRect(0, 0, 4, 4)), Tile("right", new GridRect(4, 0, 4, 4))) with { Rows = 5 };
        var before = WallJson.Serialize(document);
        var metrics = new WallMetrics();
        var result = WallLayoutEngine.TryApply(document,
            new AddMemberAction(0, Tile("new", new GridRect(0, 0, 2, 2)), "group"),
            metrics.WallWidth(1), metrics.WallHeight(5));
        Assert.False(result.Success);
        Assert.Null(result.Proposal);
        Assert.Equal(before, WallJson.Serialize(document));
    }
}
