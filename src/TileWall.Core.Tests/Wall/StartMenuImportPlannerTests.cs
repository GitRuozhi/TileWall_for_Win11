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

    [Theory]
    [InlineData(ImportInsertionPosition.Head, 0)]
    [InlineData(ImportInsertionPosition.Tail, 3)]
    public void WallPackageReusesAnEdgeLaneWithoutMovingExistingGroups(ImportInsertionPosition position, int destination)
    {
        var first = Group("first");
        var second = Group("second");
        var current = WallDocument.Empty(4, 8) with
        {
            Lanes = [new() { Groups = [] }, new() { Groups = [first] }, new() { Groups = [second] }, new() { Groups = [] }],
        };
        var result = StartMenuImportPlanner.TryPlan(current, 0, [Group("incoming")], budget: Budget(4, 8),
            insertionPosition: position, preserveExistingGroups: true);
        Assert.True(result.Success, result.Detail);
        var lanes = result.Proposal!.Document.Lanes;
        Assert.Equal(4, lanes.Count);
        Assert.Equal("incoming", Assert.Single(lanes[destination].Groups).Id);
        Assert.Same(first, Assert.Single(lanes[1].Groups));
        Assert.Same(second, Assert.Single(lanes[2].Groups));
    }

    [Fact]
    public void WallPackageCanExpandHorizontallyWithoutRepackingTheOldWall()
    {
        var first = Group("first");
        var second = Group("second");
        var current = WallDocument.Empty(2, 8) with { Lanes = [new() { Groups = [first] }, new() { Groups = [second] }] };
        var result = StartMenuImportPlanner.TryPlan(current, 0, [Group("incoming")], budget: Budget(2, 8),
            insertionPosition: ImportInsertionPosition.Tail, preserveExistingGroups: true);
        Assert.True(result.Success, result.Detail);
        var next = result.Proposal!.Document;
        Assert.True(next.Settings.ExpandColumns);
        Assert.Equal(["first", "second", "incoming"], Groups(next).Select(group => group.Id));
        Assert.Empty(next.Lanes[^1].Groups);
    }

    [Theory]
    [InlineData(false, "PACKAGE_HEIGHT_LIMIT")]
    [InlineData(true, "LAYOUT_LOCKED_NO_SPACE")]
    public void WallPackageRejectsAnExistingLaneThatCannotGrow(bool locked, string detail)
    {
        var first = Group("first");
        var second = Group("second");
        var current = WallDocument.Empty(1, 4) with
        {
            Settings = new() { LayoutLocked = locked },
            Lanes = [new() { Groups = [first, second] }],
        };
        var updated = first with { Members = [first.Members[0], Tile("incoming", row: 1)] };
        var result = StartMenuImportPlanner.TryPlan(current, 0, [updated], budget: Budget(4, 4),
            preserveExistingGroups: true);
        Assert.Null(result.Proposal);
        Assert.Equal(detail, result.Detail);
        Assert.False(current.Settings.ExpandColumns);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TailReusesAdjacentEmptyLaneAndKeepsOldLanes(bool expanded)
    {
        var first = Group("first"); var second = Group("second");
        var current = WallDocument.Empty(3, 8) with
        {
            Settings = new() { ExpandColumns = expanded },
            Lanes = [new() { Groups = [first] }, new() { Groups = [second] }, new() { Groups = [] }],
        };
        var result = StartMenuImportPlanner.TryPlan(current, 0, [Group("incoming")],
            budget: Budget(4, 8), insertionPosition: ImportInsertionPosition.Tail);
        Assert.True(result.Success, result.Detail);
        var proposed = result.Proposal!.Document;
        Assert.Same(first, Assert.Single(proposed.Lanes[0].Groups));
        Assert.Same(second, Assert.Single(proposed.Lanes[1].Groups));
        Assert.Equal("incoming", Assert.Single(proposed.Lanes[2].Groups).Id);
        Assert.Equal(expanded ? 4 : 3, proposed.Columns);
        if (expanded) Assert.Empty(proposed.Lanes[^1].Groups);
    }

    [Fact]
    public void TailStartsAfterLastContentWithoutFillingItsRemainingHeight()
    {
        var first = Group("first"); var second = Group("second");
        var current = WallDocument.Empty(2, 16) with
            { Lanes = [new() { Groups = [first] }, new() { Groups = [second] }] };
        var result = StartMenuImportPlanner.TryPlan(current, 0, [Group("incoming")],
            budget: Budget(4, 16), insertionPosition: ImportInsertionPosition.Tail);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(3, result.Proposal!.Document.Columns);
        Assert.Equal(["first", "second", "incoming"], result.Proposal.Document.Lanes.Select(lane => Assert.Single(lane.Groups).Id));
    }

    [Fact]
    public void TailPreservesNonExpandedInternalAndSurplusEmptyLanes()
    {
        var current = WallDocument.Empty(5, 8) with
        {
            Lanes = [new() { Groups = [Group("first")] }, new() { Groups = [] },
                new() { Groups = [Group("last")] }, new() { Groups = [] }, new() { Groups = [] }],
        };
        var result = StartMenuImportPlanner.TryPlan(current, 0, [Group("incoming")],
            budget: Budget(5, 8), insertionPosition: ImportInsertionPosition.Tail);
        Assert.True(result.Success, result.Detail);
        var lanes = result.Proposal!.Document.Lanes;
        Assert.Empty(lanes[1].Groups);
        Assert.Equal("last", Assert.Single(lanes[2].Groups).Id);
        Assert.Equal("incoming", Assert.Single(lanes[3].Groups).Id);
        Assert.Empty(lanes[4].Groups);
    }

    [Fact]
    public void TailCompactsOnlyNecessaryBlankRowsBeforeRejectingTallGroup()
    {
        var old = Group("old", Tile("old-member", 3, 2));
        var incoming = Group("tall", Tile("upper"), Tile("lower", 4, 20));
        var current = WallDocument.Empty(2, 4) with
            { Lanes = [new() { Groups = [old] }, new() { Groups = [] }] };
        var before = WallJson.Serialize(current);
        var result = StartMenuImportPlanner.TryPlan(current, 0, [incoming],
            budget: Budget(2, 12), insertionPosition: ImportInsertionPosition.Tail);
        Assert.True(result.Success, result.Detail);
        var proposed = result.Proposal!.Document;
        Assert.Same(old, Assert.Single(proposed.Lanes[0].Groups));
        var lower = Assert.Single(proposed.Lanes[1].Groups).Members.Single(member => member.Id == "lower");
        Assert.Equal(10, lower.LocalBounds.Row);
        Assert.Equal(12, proposed.Rows);
        Assert.Equal(before, WallJson.Serialize(current));
    }

    [Fact]
    public void TailRemovesSavedBottomWhitespaceWhenEnvironmentRequiresIt()
    {
        var current = WallDocument.Empty(1, 16) with { Lanes = [new() { Groups = [Group("old")] }] };
        var result = StartMenuImportPlanner.TryPlan(current, 0, [Group("incoming")],
            budget: Budget(2, 12), insertionPosition: ImportInsertionPosition.Tail);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(12, result.Proposal!.Document.Rows);
        Assert.Equal("old", Assert.Single(result.Proposal.Document.Lanes[0].Groups).Id);
    }

    [Fact]
    public void TailReflowsWholeGroupsOnlyWhenAdjacentColumnCannotFit()
    {
        var current = WallDocument.Empty(2, 8) with
            { Lanes = [new() { Groups = [Group("first")] }, new() { Groups = [Group("second")] }] };
        var result = StartMenuImportPlanner.TryPlan(current, 0, [Group("incoming")],
            budget: Budget(2, 8), insertionPosition: ImportInsertionPosition.Tail);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(["first", "second", "incoming"], Groups(result.Proposal!.Document).Select(group => group.Id));
        Assert.Equal(2, result.Proposal.Document.Columns);
    }

    [Fact]
    public void PureSourceTailUsesRemainingHeightBeforeGrowing()
    {
        var first = Group("first");
        var last = Group("last");
        var current = WallDocument.Empty(2, 4) with
            { Lanes = [new() { Groups = [first] }, new() { Groups = [last] }] };
        var result = StartMenuImportPlanner.TryPlan(current, 0, [Group("incoming")],
            budget: Budget(2, 12), insertionPosition: ImportInsertionPosition.Tail, preferCurrentHeight: true);

        var next = Assert.IsType<WallProposal>(result.Proposal).Document;
        Assert.Equal(4, next.Rows);
        Assert.Same(first, Assert.Single(next.Lanes[0].Groups));
        Assert.Same(last, next.Lanes[1].Groups[0]);
        Assert.Equal("incoming", next.Lanes[1].Groups[1].Id);
    }

    [Fact]
    public void PureSourceTailReflowsOnlyTheNecessarySuffixAtCurrentHeight()
    {
        var first = Group("first", Tile("first-member", row: 2));
        var second = Group("second");
        var third = Group("third");
        var last = Group("last", Tile("last-member", column: 3, row: 2));
        var incoming = Group("incoming", Tile("new-member", row: 2));
        var current = WallDocument.Empty(4, 6) with
        {
            Lanes = [new() { Groups = [first] }, new() { Groups = [second] },
                new() { Groups = [third] }, new() { Groups = [last] }],
        };
        var result = StartMenuImportPlanner.TryPlan(current, 0, [incoming],
            budget: Budget(4, 12), insertionPosition: ImportInsertionPosition.Tail, preferCurrentHeight: true);

        var next = Assert.IsType<WallProposal>(result.Proposal).Document;
        Assert.Equal(6, next.Rows);
        Assert.Same(current.Lanes[0], next.Lanes[0]);
        Assert.Same(current.Lanes[1], next.Lanes[1]);
        Assert.Equal([third, last], next.Lanes[2].Groups);
        Assert.Same(incoming, Assert.Single(next.Lanes[3].Groups));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void PureSourceTailKeepsManualWidthAndDropsOnlyUnneededTailColumns(int manualColumns, int expectedColumns)
    {
        var current = WallDocument.Empty(4, 6) with
        {
            Settings = new() { ManualWindowedViewportWidthDip = new WallMetrics().WallWidth(manualColumns) +
                WallViewportPolicy.FixedWidth(menuStart: true) },
            Lanes = [new() { Groups = [Group("first")] }, new() { Groups = [] }, new() { Groups = [] }, new() { Groups = [] }],
        };
        var result = StartMenuImportPlanner.TryPlan(current, 0, [Group("incoming")],
            budget: Budget(4, 10), insertionPosition: ImportInsertionPosition.Tail, preferCurrentHeight: true);

        var next = Assert.IsType<WallProposal>(result.Proposal).Document;
        Assert.Equal(expectedColumns, next.Columns);
        Assert.Equal("incoming", Assert.Single(next.Lanes[1].Groups).Id);
    }

    [Theory]
    [InlineData(ImportInsertionPosition.Head, "incoming,old")]
    [InlineData(ImportInsertionPosition.Tail, "old,incoming")]
    public void PureSourceImportGrowsOnlyAsFarAsTheUnchangedGroupGeometryNeeds(
        ImportInsertionPosition position, string order)
    {
        var current = WallDocument.Empty(1, 4) with { Lanes = [new() { Groups = [Group("old")] }] };
        var incoming = Group("incoming", Tile("member", row: 3));
        var result = StartMenuImportPlanner.TryPlan(current, 0, [incoming],
            budget: Budget(1, 12), insertionPosition: position, preferCurrentHeight: true);

        var next = Assert.IsType<WallProposal>(result.Proposal).Document;
        Assert.Equal(7, next.Rows);
        Assert.Equal(order.Split(','), Groups(next).Select(group => group.Id));
        Assert.Same(incoming, next.Lanes[0].Groups.Single(group => group.Id == incoming.Id));

        var before = WallJson.Serialize(current);
        var rejected = StartMenuImportPlanner.TryPlan(current, 0, [incoming],
            budget: Budget(1, 4), insertionPosition: position, preferCurrentHeight: true);
        Assert.Null(rejected.Proposal);
        Assert.Equal("GROUP_TOO_TALL:incoming", rejected.Detail);
        Assert.Equal(before, WallJson.Serialize(current));
    }

    [Fact]
    public void PureSourceHeadUsesAdjacentLeadingSpaceWithoutShiftingTheWall()
    {
        var current = WallDocument.Empty(4, 6) with
        {
            Lanes = [new() { Groups = [] }, new() { Groups = [] },
                new() { Groups = [Group("first")] }, new() { Groups = [Group("second")] }],
        };
        var result = StartMenuImportPlanner.TryPlan(current, 0, [Group("incoming")],
            budget: Budget(4, 12), preferCurrentHeight: true);

        var next = Assert.IsType<WallProposal>(result.Proposal).Document;
        Assert.Equal(6, next.Rows);
        Assert.Same(current.Lanes[0], next.Lanes[0]);
        Assert.Equal("incoming", Assert.Single(next.Lanes[1].Groups).Id);
        Assert.Same(current.Lanes[2], next.Lanes[2]);
        Assert.Same(current.Lanes[3], next.Lanes[3]);
    }

    [Fact]
    public void PureSourceHeadPrependsInFirstLaneBeforeShiftingOtherLanes()
    {
        var first = Group("first");
        var incoming = Group("incoming");
        var current = WallDocument.Empty(2, 4) with
            { Lanes = [new() { Groups = [first] }, new() { Groups = [Group("second")] }] };
        var result = StartMenuImportPlanner.TryPlan(current, 0, [incoming],
            budget: Budget(3, 12), preferCurrentHeight: true);

        var next = Assert.IsType<WallProposal>(result.Proposal).Document;
        Assert.Equal(4, next.Rows);
        Assert.Equal(2, next.Columns);
        Assert.Equal([incoming, first], next.Lanes[0].Groups);
        Assert.Same(current.Lanes[1], next.Lanes[1]);
    }

    [Fact]
    public void PureSourceHeadReflowsOnlyTheNecessaryPrefix()
    {
        var first = Group("first", Tile("first-member", column: 3, row: 2));
        var second = Group("second");
        var incoming = Group("incoming", Tile("new-member", row: 2));
        var current = WallDocument.Empty(4, 6) with
        {
            Lanes = [new() { Groups = [first] }, new() { Groups = [second] },
                new() { Groups = [Group("third")] }, new() { Groups = [Group("last")] }],
        };
        var result = StartMenuImportPlanner.TryPlan(current, 0, [incoming],
            budget: Budget(4, 12), preferCurrentHeight: true);

        var next = Assert.IsType<WallProposal>(result.Proposal).Document;
        Assert.Equal(6, next.Rows);
        Assert.Same(incoming, Assert.Single(next.Lanes[0].Groups));
        Assert.Equal([first, second], next.Lanes[1].Groups);
        Assert.Same(current.Lanes[2], next.Lanes[2]);
        Assert.Same(current.Lanes[3], next.Lanes[3]);
    }

    [Fact]
    public void PureSourceHeadKeepsOldLaneBoundariesWhenItMustAddAColumn()
    {
        var incoming = Group("incoming", Tile("new-member", row: 4));
        var current = WallDocument.Empty(2, 6) with
        {
            Lanes = [new() { Groups = [Group("first"), Group("second")] },
                new() { Groups = [Group("last", Tile("last-member", row: 2))] }],
        };
        var result = StartMenuImportPlanner.TryPlan(current, 0, [incoming],
            budget: Budget(3, 12), preferCurrentHeight: true);

        var next = Assert.IsType<WallProposal>(result.Proposal).Document;
        Assert.Equal(6, next.Rows);
        Assert.Equal(3, next.Columns);
        Assert.Same(incoming, Assert.Single(next.Lanes[0].Groups));
        Assert.Same(current.Lanes[0], next.Lanes[1]);
        Assert.Same(current.Lanes[1], next.Lanes[2]);
    }

    [Fact]
    public void TailExpandedColumnLimitStillIncludesTheUniqueEmptyTail()
    {
        var current = WallDocument.Empty(64, 2) with
        {
            Settings = new() { ExpandColumns = true },
            Lanes = Enumerable.Range(0, 63).Select(index => new WallLane { Groups = [Group("old-" + index)] })
                .Append(new WallLane { Groups = [] }).ToArray(),
        };
        var before = WallJson.Serialize(current);
        var result = StartMenuImportPlanner.TryPlan(current, 0, [Group("incoming")], insertionPosition: ImportInsertionPosition.Tail);
        Assert.Null(result.Proposal);
        Assert.Equal(WallRejection.NoSpace, result.Rejection);
        Assert.Equal(before, WallJson.Serialize(current));
    }

    [Fact]
    public void TailRejectsTheCompleteImportWhenOccupiedRowsCannotFitEvenAfterCompaction()
    {
        var current = WallDocument.Empty(2, 4);
        var before = WallJson.Serialize(current);
        var incoming = Group("occupied", Tile("one", 0, 0, 4, 4),
            Tile("two", 0, 8, 4, 4), Tile("three", 0, 16, 4, 4));
        var result = StartMenuImportPlanner.TryPlan(current, 0, [incoming],
            budget: Budget(3, 8), insertionPosition: ImportInsertionPosition.Tail);
        Assert.Null(result.Proposal);
        Assert.Equal("GROUP_TOO_TALL:occupied", result.Detail);
        Assert.Equal(before, WallJson.Serialize(current));
    }
}
