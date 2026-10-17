using TileWall.Core.Grid;
using TileWall.Core.Import;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Import;

public sealed class StartMenuReimportTests
{
    [Fact]
    public void PruningIsInMemoryAndProtectsClaimedMovedAndManualMembersWithTheirOriginalCells()
    {
        var source = new ImportSourceMetadata { BatchId = "batch", GroupBucketKey = "letter:A", OriginGroupId = "managed" };
        var automatic = Tile("automatic") with { ImportSource = source };
        var claimed = Tile("claimed") with { ImportSource = source, UserClaimed = true, LocalBounds = new GridRect(4, 3, 2, 2) };
        var manual = Tile("manual") with { LocalBounds = new GridRect(2, 5, 2, 2) };
        var moved = Tile("moved") with { ImportSource = source with { OriginGroupId = "another-group" } };
        var group = new StartGroup { Id = "managed", Title = "A", ImportSource = source, Members = [automatic, claimed, manual, moved] };
        var document = WallDocument.Empty(1, 10) with { Generation = 7, Lanes = [new WallLane { Groups = [group] }] };
        var result = StartMenuReimport.Prune(document);
        Assert.Equal(4, document.Lanes[0].Groups[0].Members.Count);
        Assert.Equal(7, result.Document.Generation);
        Assert.Equal("automatic", Assert.Single(result.Removed).Id);
        var retained = Assert.Single(result.Document.Lanes[0].Groups);
        Assert.Equal(group.Id, retained.Id);
        Assert.True(retained.UserClaimed);
        Assert.Equal(new[] { claimed, manual, moved }, retained.Members);
    }

    [Fact]
    public void OnlyEmptyMarkedImportLanesAreRemoved()
    {
        var source = new ImportSourceMetadata { BatchId = "batch", GroupBucketKey = "letter:A", OriginGroupId = "managed" };
        var automatic = new StartGroup { Id = "managed", ImportSource = source,
            Members = [Tile("automatic") with { ImportSource = source }] };
        var protectedGroup = new StartGroup { Id = "protected", Members = [Tile("manual")] };
        var ordinaryEmpty = new WallLane { Groups = [] };
        var retainedImport = new WallLane { ImportBatchId = "batch", Groups = [protectedGroup] };
        var document = WallDocument.Empty(3, 10) with { Lanes =
            [new WallLane { ImportBatchId = "batch", Groups = [automatic] }, ordinaryEmpty, retainedImport] };
        var result = StartMenuReimport.Prune(document);
        Assert.Equal(2, result.Document.Columns);
        Assert.Equal(3, document.Columns);
        Assert.Null(result.Document.Lanes[0].ImportBatchId);
        Assert.Equal("batch", result.Document.Lanes[1].ImportBatchId);
        Assert.Same(protectedGroup, Assert.Single(result.Document.Lanes[1].Groups));
    }

    [Fact]
    public void RemovingEveryOldAutomaticLaneProducesOnlyAnInternalZeroLaneSnapshot()
    {
        var source = new ImportSourceMetadata { BatchId = "old", GroupBucketKey = "letter:A", OriginGroupId = "old-group",
            SourceRoot = @"C:\Programs", RelativePath = "app.lnk" };
        var original = WallDocument.Empty(1, 4) with { Generation = 9, Lanes =
        [new WallLane { ImportBatchId = "old", Groups = [new StartGroup { Id = "old-group", ImportSource = source,
            Members = [Tile("old-member") with { ImportSource = source }] }] }] };
        var pruned = StartMenuReimport.Prune(original);
        Assert.Equal(0, pruned.Document.Columns);
        Assert.Empty(pruned.Document.Lanes);
        Assert.NotEmpty(WallLayoutEngine.Validate(pruned.Document));
        var newSource = source with { BatchId = "new", OriginGroupId = "new-group" };
        var result = StartMenuImportPlanner.TryPlan(pruned.Document, original.Generation,
        [new StartGroup { Id = "new-group", ImportSource = newSource,
            Members = [Tile("new-member") with { ImportSource = newSource }] }]);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(1, result.Proposal!.Document.Columns);
        Assert.Equal("new", Assert.Single(result.Proposal.Document.Lanes).ImportBatchId);
        Assert.Equal(10, result.Proposal.Document.Generation);
        Assert.Empty(WallLayoutEngine.Validate(result.Proposal.Document));
    }

    [Fact]
    public void ClaimedWholeGroupsAndManualSameNamedGroupsAreNotDeletionCandidates()
    {
        var source = new ImportSourceMetadata { BatchId = "batch", GroupBucketKey = "letter:A", OriginGroupId = "managed" };
        var group = new StartGroup { Id = "managed", Title = "A", ImportSource = source,
            UserClaimed = true, Members = [Tile("claimed-group-child") with { ImportSource = source }] };
        var manual = new StartGroup { Id = "manual", Title = "A", Members = [Tile("manual-child")] };
        var document = WallDocument.Empty(1, 10) with { Lanes = [new WallLane { Groups = [group, manual] }] };
        var result = StartMenuReimport.Prune(document);
        Assert.Empty(result.Removed);
        Assert.Equal(new[] { group, manual }, result.Document.Lanes[0].Groups);
    }

    private static TileMember Tile(string id) => new() { Id = id, LocalBounds = new GridRect(0, 0, 2, 2) };
}
