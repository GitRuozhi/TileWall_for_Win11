using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class ExplorerAppendPlannerTests
{
    private static readonly WallMetrics Metrics = new();

    private static TileMember Tile(string id, int column = 0) => new()
    {
        Id = id, LocalBounds = new GridRect(column, 0, 2, 2),
    };

    private static StartGroup Group(string id, params TileMember[] members) => new() { Id = id, Members = members };
    private static WallLane Lane(params StartGroup[] groups) => new() { Groups = groups };
    private static WallDocument Document(params WallLane[] lanes) => new()
    {
        Columns = lanes.Length, Rows = 3, Generation = 7, Lanes = lanes,
        Settings = new WallSettings { FullScreen = false, ExpandColumns = false },
    };

    private static WallLayoutResult Plan(WallDocument current, TileMember[] incoming,
        IReadOnlyDictionary<string, string>? identities = null, int? availableColumns = null) =>
        WallBatchPlanner.TryPlan(current, incoming, current.Lanes.SelectMany(lane => lane.Groups).LastOrDefault()?.Id,
            null, "external", Metrics.WallWidth(availableColumns ?? current.Columns), Metrics.WallHeight(3), Metrics,
            appendOnly: true, launchIdentityKeys: identities);

    [Fact]
    public void AppendsAfterLastMemberOfLastGroupWithoutFillingEarlierHole()
    {
        var before = Document(Lane(Group("first", Tile("earlier"))),
            Lane(Group("last", Tile("left"), Tile("right", 4))), Lane());

        var result = Plan(before, [Tile("new")]);

        Assert.True(result.Success, result.Detail);
        Assert.Equal(["first", "last"], result.Proposal!.Document.Lanes.SelectMany(lane => lane.Groups).Select(group => group.Id));
        var last = result.Proposal.Document.Lanes[1].Groups[0];
        Assert.Equal(new GridRect(6, 0, 2, 2), last.Members.Single(member => member.Id == "new").LocalBounds);
        Assert.Empty(result.Proposal.Document.Lanes[2].Groups);
    }

    [Fact]
    public void EmptyWallCreatesOneUnnamedGroupAndLaterRequestReusesIt()
    {
        var before = Document(Lane());
        var first = Plan(before, [Tile("one"), Tile("two")]);
        Assert.True(first.Success, first.Detail);
        var group = Assert.Single(first.Proposal!.Document.Lanes[0].Groups);
        Assert.Null(group.Title);

        var second = Plan(first.Proposal.Document, [Tile("three")]);

        Assert.True(second.Success, second.Detail);
        var reused = Assert.Single(second.Proposal!.Document.Lanes[0].Groups);
        Assert.Equal(group.Id, reused.Id);
        Assert.Equal(new GridRect(4, 0, 2, 2), reused.Members.Single(member => member.Id == "three").LocalBounds);
        Assert.Equal(before.Generation + 2, second.Proposal.Document.Generation);
    }

    [Fact]
    public void FullTailSpillsForwardInsteadOfWrappingIntoEarlierEmptyLane()
    {
        var full = Enumerable.Range(0, 4).Select(index => Tile($"old-{index}", index * 2)).ToArray();
        var before = Document(Lane(Group("first", Tile("earlier"))), Lane(), Lane(Group("tail", full)));

        var result = Plan(before, [Tile("new"), Tile("next")], availableColumns: 4);

        Assert.True(result.Success, result.Detail);
        Assert.Empty(result.Proposal!.Document.Lanes[1].Groups);
        var spill = Assert.Single(result.Proposal.Document.Lanes[3].Groups);
        Assert.Null(spill.Title);
        Assert.Equal(["new", "next"], spill.Members.Select(member => member.Id));
        Assert.Equal(["first", "tail", spill.Id], result.Proposal.Document.Lanes.SelectMany(lane => lane.Groups).Select(group => group.Id));
        Assert.Equal(before.Generation + 1, result.Proposal.Document.Generation);
    }

    [Fact]
    public void DestinationDuplicatesAreSkippedBeforeTheyConsumeCapacity()
    {
        var before = Document(Lane(Group("tail", Tile("old-a"), Tile("old-b", 2), Tile("old-c", 4))));
        var keys = new Dictionary<string, string>
        { ["old-a"] = "same", ["duplicate"] = "same", ["new"] = "other" };

        var result = Plan(before, [Tile("duplicate"), Tile("new")], keys);

        Assert.True(result.Success, result.Detail);
        var group = Assert.Single(result.Proposal!.Document.Lanes[0].Groups);
        Assert.Equal(4, group.Members.Count);
        Assert.DoesNotContain(group.Members, member => member.Id == "duplicate");
        Assert.Equal(new GridRect(6, 0, 2, 2), group.Members.Single(member => member.Id == "new").LocalBounds);
    }

    [Fact]
    public void FullTailDuplicateDoesNotCreateAnotherGroupOrAdvanceGeneration()
    {
        var full = Enumerable.Range(0, 4).Select(index => Tile($"old-{index}", index * 2)).ToArray();
        var before = Document(Lane(Group("tail", full)));
        var keys = new Dictionary<string, string> { ["old-0"] = "same", ["duplicate"] = "same" };

        var result = Plan(before, [Tile("duplicate")], keys, availableColumns: 2);

        Assert.True(result.Success, result.Detail);
        Assert.Same(before, result.Proposal!.Document);
        Assert.Equal(before.Generation, result.Proposal.Document.Generation);
    }

    [Fact]
    public void ExistingIdentityInAnotherGroupDoesNotBlockAppend()
    {
        var before = Document(Lane(Group("first", Tile("old"))), Lane(Group("tail", Tile("other"))));
        var keys = new Dictionary<string, string> { ["old"] = "same", ["new"] = "same" };

        var result = Plan(before, [Tile("new")], keys);

        Assert.True(result.Success, result.Detail);
        Assert.Equal(["other", "new"], result.Proposal!.Document.Lanes[1].Groups[0].Members.Select(member => member.Id));
    }

    [Fact]
    public void BatchDuplicatesAreScopedToTheGroupReachedByEarlierItems()
    {
        var before = Document(Lane());
        var incoming = new[] { Tile("a"), Tile("b"), Tile("c"), Tile("d"), Tile("e"), Tile("a-again"), Tile("e-again") };
        var keys = incoming.ToDictionary(member => member.Id, member => member.Id.Split('-')[0], StringComparer.Ordinal);

        var result = Plan(before, incoming, keys, availableColumns: 2);

        Assert.True(result.Success, result.Detail);
        Assert.Equal(["a", "b", "c", "d"], result.Proposal!.Document.Lanes[0].Groups[0].Members.Select(member => member.Id));
        Assert.Equal(["e", "a-again"], result.Proposal.Document.Lanes[1].Groups[0].Members.Select(member => member.Id));
        Assert.Equal(before.Generation + 1, result.Proposal.Document.Generation);
    }

    [Fact]
    public void LastItemFailureRejectsWholeAppendAndLeavesSnapshotUntouched()
    {
        var before = Document(Lane(Group("tail", Tile("old-a"), Tile("old-b", 2), Tile("old-c", 4))));
        var serialized = WallJson.Serialize(before);

        var result = Plan(before, [Tile("first"), Tile("cannot-fit")]);

        Assert.False(result.Success);
        Assert.Equal(WallRejection.NoSpace, result.Rejection);
        Assert.Null(result.Proposal);
        Assert.Equal(serialized, WallJson.Serialize(before));
    }
}
