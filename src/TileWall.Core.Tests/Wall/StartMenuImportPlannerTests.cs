using TileWall.Core.Grid;
using TileWall.Core.Import;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class StartMenuImportPlannerTests
{
    private static TileMember Tile(string id, int column = 0, int row = 0, int width = 1, int height = 1) =>
        new() { Id = id, LocalBounds = new(column, row, width, height) };
    private static StartGroup Group(string id, params WallMember[] members) =>
        new() { Id = id, Title = id, Members = members.Length == 0 ? [Tile(id + "-member")] : members };
    private static WallViewportBudget Budget(int columns, int rows)
    {
        var metrics = new WallMetrics();
        return new(metrics.WallWidth(columns), metrics.WallHeight(rows), columns, rows, false);
    }
    private static IEnumerable<StartGroup> Groups(WallDocument document) => document.Lanes.SelectMany(lane => lane.Groups);

    [Theory]
    [InlineData(ImportInsertionPosition.Head, "incoming,first,second")]
    [InlineData(ImportInsertionPosition.Tail, "first,second,incoming")]
    public void InvolvedGroupBlockGoesToChosenEndAndUntouchedOrderIsStable(ImportInsertionPosition position, string expected)
    {
        var first = Group("first"); var second = Group("second");
        var current = WallDocument.Empty(3, 8) with { Lanes = [new() { Groups = [first] }, new() { Groups = [second] }, new() { Groups = [] }] };
        var before = WallJson.Serialize(current);
        var incoming = Group("incoming", Tile("sparse", 5, 3, 2, 2));
        var result = StartMenuImportPlanner.TryPlan(current, 0, [incoming], budget: Budget(4, 16), insertionPosition: position);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(expected.Split(','), Groups(result.Proposal!.Document).Select(group => group.Id));
        Assert.Equal(new GridRect(5, 3, 2, 2), Groups(result.Proposal.Document).Single(group => group.Id == "incoming").Members[0].LocalBounds);
        Assert.Same(first, Groups(result.Proposal.Document).Single(group => group.Id == "first"));
        Assert.Same(second, Groups(result.Proposal.Document).Single(group => group.Id == "second"));
        Assert.Equal(3, result.Proposal.Document.Columns);
        Assert.Equal(8, result.Proposal.Document.Rows);
        Assert.Equal(before, WallJson.Serialize(current));
    }

    [Fact]
    public void HeadIsDefaultAndExistingDestinationMovesAsCompleteGroup()
    {
        var first = Group("first"); var destination = Group("destination", Tile("old", 3, 2)); var last = Group("last");
        var current = WallDocument.Empty(1, 16) with { Lanes = [new() { Groups = [first, destination, last] }] };
        var merged = destination with { Members = [destination.Members[0], Tile("new", 1, 5)] };
        var result = StartMenuImportPlanner.TryPlan(current, 0, [merged], budget: Budget(3, 16));
        Assert.True(result.Success, result.Detail);
        Assert.Equal(["destination", "first", "last"], Groups(result.Proposal!.Document).Select(group => group.Id));
        Assert.Equal([new GridRect(3, 2, 1, 1), new GridRect(1, 5, 1, 1)], Groups(result.Proposal.Document).First().Members.Select(member => member.LocalBounds));
    }

    [Fact]
    public void StoredImportMetadataDoesNotReclassifyUntouchedWall()
    {
        StartGroup Bucket(string id, string bucket) => Group(id) with { ImportSource = new() { BatchId = "old", OriginGroupId = id, GroupBucketKey = bucket } };
        var preset = Bucket("preset", "preset:microsoft"); var letter = Bucket("letter", "letter:A"); var folder = Bucket("folder", "folder:TOOLS");
        var current = WallDocument.Empty(1, 16) with { Lanes = [new() { Groups = [preset, letter, folder] }] };
        var result = StartMenuImportPlanner.TryPlan(current, 0, [Group("new")], insertionPosition: ImportInsertionPosition.Tail);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(["preset", "letter", "folder", "new"], Groups(result.Proposal!.Document).Select(group => group.Id));
    }

    [Fact]
    public void WholeGroupGrowsWallOnlyEnoughWithoutCompressingRecordedGap()
    {
        var incoming = Group("tall", Tile("upper", 0, 0, 4, 3), Tile("lower", 4, 8, 4, 3));
        var current = WallDocument.Empty(1, 4);
        var result = StartMenuImportPlanner.TryPlan(current, 0, [incoming], budget: Budget(4, 24));
        Assert.True(result.Success, result.Detail);
        Assert.Equal(12, result.Proposal!.Document.Rows);
        Assert.Equal(11, Groups(result.Proposal.Document).Single().Members.Max(member => member.LocalBounds.Bottom));
        Assert.Equal(new GridRect(4, 8, 4, 3), Groups(result.Proposal.Document).Single().Members[1].LocalBounds);
    }

    [Fact]
    public void RecordedGapThatExceedsPhysicalBudgetRejectsTheCompleteImport()
    {
        var current = WallDocument.Empty(1, 4); var before = WallJson.Serialize(current);
        var incoming = Group("tall", Tile("lower", 0, 20, 1, 1));
        var result = StartMenuImportPlanner.TryPlan(current, 0, [incoming], budget: Budget(4, 12));
        Assert.Null(result.Proposal); Assert.Equal(WallRejection.NoSpace, result.Rejection);
        Assert.Equal("GROUP_TOO_TALL:tall", result.Detail); Assert.Equal(before, WallJson.Serialize(current));
    }

    [Fact]
    public void InvalidRecordedOverlapIsRejectedRatherThanRepacked()
    {
        var incoming = Group("overlap", Tile("one", 0, 0, 2, 2), Tile("two", 1, 1, 2, 2));
        var result = StartMenuImportPlanner.TryPlan(WallDocument.Empty(1, 8), 0, [incoming]);
        Assert.Null(result.Proposal); Assert.Equal("INVALID_SOURCE_GEOMETRY", result.Detail);
    }

    [Fact]
    public void SmallerViewportCannotShrinkSavedHeightForImport()
    {
        var current = WallDocument.Empty(1, 16);
        var result = StartMenuImportPlanner.TryPlan(current, 0, [Group("new")], budget: Budget(3, 12));
        Assert.Null(result.Proposal); Assert.Equal(WallRejection.WorkAreaTooSmall, result.Rejection);
    }

    [Fact]
    public void StaleGenerationAndInvalidCurrentDocumentRejectBeforePlanning()
    {
        var stale = StartMenuImportPlanner.TryPlan(WallDocument.Empty(1, 8) with { Generation = 3 }, 2, [Group("new")]);
        Assert.Equal(WallRejection.StaleGeneration, stale.Rejection);
        var invalid = StartMenuImportPlanner.TryPlan(WallDocument.Empty(0, 0), 0, [Group("new")]);
        Assert.Equal(WallRejection.InvalidDocument, invalid.Rejection);
    }

    [Fact]
    public void StructuralColumnLimitRejectsWithoutDroppingGroups()
    {
        var current = WallDocument.Empty(64, 2) with { Lanes = Enumerable.Range(0, 64)
            .Select(index => new WallLane { Groups = [Group("old-" + index)] }).ToArray() };
        var result = StartMenuImportPlanner.TryPlan(current, 0, [Group("new")]);
        Assert.Null(result.Proposal); Assert.Equal(WallRejection.NoSpace, result.Rejection);
        Assert.Equal(64, Groups(current).Count());
    }
}
