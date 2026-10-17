using TileWall.Core.Grid;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class StartMenuImportPlannerTests
{
    private static ImportSourceMetadata GroupSource(string id) => new()
    {
        BatchId = "batch",
        GroupBucketKey = id,
        OriginGroupId = id,
    };

    private static ImportSourceMetadata MemberSource(string groupId, string fileName) => new()
    {
        BatchId = "batch",
        GroupBucketKey = groupId,
        OriginGroupId = groupId,
        SourceRoot = "UserPrograms",
        RelativePath = fileName,
    };

    private static TileMember Tile(string id, int width = 1, int height = 1, string? groupId = null) => new()
    {
        Id = id,
        LocalBounds = new GridRect(0, 0, width, height),
        ImportSource = MemberSource(groupId ?? id, id + ".lnk"),
    };

    private static StartGroup Group(string id, params TileMember[] members) => new()
    {
        Id = id,
        Title = id,
        ImportSource = GroupSource(id),
        Members = members,
    };

    [Fact]
    public void NewGroupsArePrependedInOrderAndExistingLanesKeepTheirContent()
    {
        var oldFirst = Group("old-first", Tile("old-a"));
        var oldSecond = Group("old-second", Tile("old-b"));
        var current = new WallDocument
        {
            Columns = 2,
            Rows = 4,
            Generation = 17,
            Lanes =
            [
                new WallLane { Groups = [oldFirst] },
                new WallLane { Groups = [oldSecond] },
            ],
        };
        var imported = new[]
        {
            Group("menu-one", Tile("menu-a", 2, 2, "menu-one")),
            Group("menu-two", Tile("menu-b", 2, 2, "menu-two")),
        };

        var result = StartMenuImportPlanner.TryPlan(current, 17, imported);

        Assert.True(result.Success, result.Detail);
        var proposed = result.Proposal!.Document;
        Assert.Equal(4, proposed.Columns);
        Assert.Equal(4, proposed.Rows);
        Assert.Equal(18, proposed.Generation);
        Assert.Equal("batch", proposed.Lanes[0].ImportBatchId);
        Assert.Equal("batch", proposed.Lanes[1].ImportBatchId);
        Assert.Null(proposed.Lanes[2].ImportBatchId);
        Assert.Equal(new[] { "menu-one", "menu-two", "old-first", "old-second" },
            proposed.Lanes.SelectMany(lane => lane.Groups).Select(group => group.Id));
        Assert.Equal(new GridSize(2, 2), proposed.Lanes[0].Groups[0].Members[0].LocalBounds.Size);
        Assert.Same(oldFirst, proposed.Lanes[2].Groups[0]);
        Assert.Same(oldSecond, proposed.Lanes[3].Groups[0]);
        Assert.Same(oldFirst, current.Lanes[0].Groups[0]);
        Assert.Equal(17, current.Generation);
    }

    [Fact]
    public void FixedHeightUsesAsManyWholeGroupLanesAsRequired()
    {
        var oneLane = StartMenuImportPlanner.TryPlan(WallDocument.Empty(1, 2), 0,
            [Group("one", Tile("one-member"))]);
        Assert.True(oneLane.Success, oneLane.Detail);
        Assert.Equal(2, oneLane.Proposal!.Document.Columns);

        var groups = Enumerable.Range(1, 4)
            .Select(i => Group($"group-{i}", Tile($"member-{i}"))).ToArray();
        var tall = StartMenuImportPlanner.TryPlan(WallDocument.Empty(1, 2), 0, groups);

        Assert.True(tall.Success, tall.Detail);
        Assert.Equal(5, tall.Proposal!.Document.Columns);
        Assert.Equal(2, tall.Proposal.Document.Rows);
        Assert.Equal(new[] { "group-1", "group-2", "group-3", "group-4" },
            tall.Proposal.Document.Lanes.Take(4).SelectMany(lane => lane.Groups).Select(group => group.Id));
    }

    [Fact]
    public void ImportUsesThePrunedSnapshotWithoutChangingExpectedGeneration()
    {
        var current = WallDocument.Empty(1, 2) with { Generation = 23 };
        var proposed = StartMenuImportPlanner.TryPlan(current, 23,
            [Group("imported", Tile("item"))]);
        Assert.True(proposed.Success, proposed.Detail);
        Assert.Equal(24, proposed.Proposal!.Document.Generation);
        Assert.Equal(23, current.Generation);

        var stale = StartMenuImportPlanner.TryPlan(current, 22,
            [Group("imported", Tile("item"))]);
        Assert.Equal(WallRejection.StaleGeneration, stale.Rejection);
        Assert.Null(stale.Proposal);
    }

    [Fact]
    public void TransientZeroLaneImportStillRejectsInvalidRemainingDocumentContracts()
    {
        var current = WallDocument.Empty(0, 0);
        var result = StartMenuImportPlanner.TryPlan(current, 0, [Group("imported", Tile("item"))]);
        Assert.Equal(WallRejection.InvalidDocument, result.Rejection);
        Assert.Null(result.Proposal);
    }

    [Fact]
    public void StructuralColumnLimitRejectsTheWholeImport()
    {
        var current = WallDocument.Empty(64, 1);
        var result = StartMenuImportPlanner.TryPlan(current, current.Generation,
            [Group("imported", Tile("item"))]);

        Assert.Equal(WallRejection.NoSpace, result.Rejection);
        Assert.Null(result.Proposal);
        Assert.Equal(64, current.Columns);
        Assert.Equal(0, current.Generation);
    }
}

