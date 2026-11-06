using TileWall.Core.Grid;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class RecycleBinRulesTests
{
    private static readonly WallMetrics Metrics = new();
    private static TileMember Tile(string id, int column = 0, int row = 0) => new()
        { Id = id, LocalBounds = new GridRect(column, row, 1, 1) };
    private static WallDocument Wall(bool enabled = true, int retained = 0, params WallMember[] active) =>
        WallDocument.Empty(1, 40) with
        {
            RecycleBinEnabled = enabled,
            InactiveMembers = Enumerable.Range(0, retained).Select(index => (WallMember)Tile("retained-" + index)).ToArray(),
            Lanes = [new WallLane { Groups = active.Length == 0 ? [] : [new StartGroup { Id = "group", Members = active }] }],
        };
    private static WallLayoutResult Apply(WallDocument current, WallAction action) =>
        WallLayoutEngine.TryApply(current, action, Metrics.WallWidth(4), Metrics.WallHeight(60), Metrics);

    [Fact]
    public void LegacyDisabledUnpinRemovesTheMemberAndItsEmptyGroup()
    {
        var current = Wall(false, 0, Tile("member"));
        var result = Apply(current, new UnpinMemberAction(0, "member"));
        Assert.True(result.Success, result.Detail);
        Assert.Empty(result.Proposal!.Document.EnumerateAllMembers());
        Assert.All(result.Proposal.Document.Lanes, lane => Assert.Empty(lane.Groups));
        Assert.Single(current.EnumerateAllMembers());
    }

    [Fact]
    public void NewWallsRetainUnpinnedMembersByDefault()
    {
        var current = WallDocument.Empty(1, 40) with
        {
            Lanes = [new() { Groups = [new() { Id = "group", Members = [Tile("member")] }] }],
        };
        Assert.True(current.RecycleBinEnabled);
        var result = Apply(current, new UnpinMemberAction(current.Generation, "member"));
        Assert.True(result.Success, result.Detail);
        Assert.Equal("member", Assert.Single(result.Proposal!.Document.InactiveMembers).Id);
        Assert.All(result.Proposal.Document.Lanes, lane => Assert.Empty(lane.Groups));
    }

    [Theory]
    [InlineData(159, 160)]
    [InlineData(199, 200)]
    public void EnablingRetainsUpToCapacityWithoutConfirmation(int count, int expected)
    {
        var member = Tile("member", 6, 12);
        var current = Wall(true, count, member);
        var result = Apply(current, new UnpinMemberAction(0, member.Id));
        Assert.True(result.Success, result.Detail);
        Assert.Equal(expected, result.Proposal!.Document.InactiveMembers.Count);
        Assert.Equal(new GridRect(0, 0, 1, 1), result.Proposal.Document.InactiveMembers[^1].LocalBounds);
        Assert.Equal(member.Id, result.Proposal.Document.InactiveMembers[^1].Id);
    }

    [Fact]
    public void OverflowRequiresConfirmationAndCancellingLeavesTheOriginalUntouched()
    {
        var current = Wall(true, 200, Tile("new"));
        var rejected = Apply(current, new UnpinMembersAction(0, ["new"]));
        Assert.False(rejected.Success);
        Assert.Equal("RECYCLE_OVERFLOW:1", rejected.Detail);
        Assert.Equal("retained-0", current.InactiveMembers[0].Id);
        Assert.Single(current.Lanes[0].Groups);
        var accepted = Apply(current, new UnpinMembersAction(0, ["new"], ConfirmOverflow: true));
        Assert.True(accepted.Success, accepted.Detail);
        Assert.Equal(200, accepted.Proposal!.Document.InactiveMembers.Count);
        Assert.Equal("retained-1", accepted.Proposal.Document.InactiveMembers[0].Id);
        Assert.Equal("new", accepted.Proposal.Document.InactiveMembers[^1].Id);
    }

    [Fact]
    public void OversizedBatchUsesWallOrderIncludingNewlyUnpinnedOverflowItems()
    {
        var active = Enumerable.Range(0, 203).Select(index => (WallMember)Tile("item-" + index, index % 8, index / 8)).ToArray();
        var current = Wall(true, 0, active);
        var ids = active.Reverse().Select(member => member.Id).ToArray();
        Assert.Equal(new[] { "item-0", "item-1", "item-2" }, RecycleBinRules.Overflow(current, ids).Select(member => member.Id));
        var result = Apply(current, new UnpinMembersAction(0, ids, ConfirmOverflow: true));
        Assert.True(result.Success, result.Detail);
        Assert.Equal(active.Skip(3).Select(member => member.Id), result.Proposal!.Document.InactiveMembers.Select(member => member.Id));
    }

    [Fact]
    public void BatchUnpinOrdersAcrossGroupsAndDropsOriginalPlacementMetadata()
    {
        var imported = Tile("first", 6, 1) with { ImportSource = new()
            { BatchId = "batch", GroupBucketKey = "bucket", OriginGroupId = "origin", SourceRoot = "root", RelativePath = "item.lnk" } };
        var current = Wall(true, 0, imported, Tile("second", 2, 3)) with
        {
            Lanes = [new WallLane { Groups = [new StartGroup { Id = "group", Members = [Tile("second", 2, 3), imported] },
                new StartGroup { Id = "later", Members = [Tile("third")] }] }],
        };
        var result = Apply(current, new UnpinMembersAction(0, ["third", "second", "first"]));
        Assert.True(result.Success, result.Detail);
        Assert.Equal(new[] { "first", "second", "third" }, result.Proposal!.Document.InactiveMembers.Select(member => member.Id));
        Assert.Null(result.Proposal.Document.InactiveMembers[0].ImportSource);
        Assert.True(result.Proposal.Document.InactiveMembers[0].UserClaimed);
        Assert.Equal(new GridRect(0, 0, 1, 1), result.Proposal.Document.InactiveMembers[0].LocalBounds);
    }

    [Fact]
    public void RestoreUsesFifoNewNamedGroupAndPreservesIdentityAndAppearance()
    {
        var puzzle = new PuzzleMember { Id = "puzzle", LocalBounds = new(5, 18, 2, 2),
            Partitions = [new(0, 0, 2, 2)], Visual = new() { TitleMode = MemberTitleMode.Manual, Title = "Album" },
            Images = new() { Kind = MemberImageSourceKind.Folder, Path = Path.GetFullPath("gallery") }, RotationInterval = ImageRotationInterval.Seconds5 };
        var current = Wall(true, 0, Tile("active")) with { InactiveMembers = [puzzle, Tile("tile", 3, 8)] };
        current = current with { Lanes = [new WallLane { Groups = [current.Lanes[0].Groups[0] with { Title = "Restored" }] }] };
        var result = Apply(current, new RestoreRecycledMembersAction(0, ["tile", "puzzle"], "restored", "Restored", new Dictionary<string, string>()));
        Assert.True(result.Success, result.Detail);
        var group = result.Proposal!.Document.Lanes.SelectMany(lane => lane.Groups).Last();
        Assert.Equal("Restored (2)", group.Title);
        Assert.Equal(new[] { "puzzle", "tile" }, group.Members.Select(member => member.Id));
        Assert.Equal(new GridRect(0, 0, 2, 2), group.Members[0].LocalBounds);
        Assert.Equal(puzzle with { LocalBounds = new(0, 0, 2, 2) }, group.Members[0]);
        Assert.Empty(result.Proposal.Document.InactiveMembers);
        Assert.Equal(2, current.InactiveMembers.Count);
    }

    [Fact]
    public void DuplicateLaunchesRejectTheWholeRestoreButSeparateBatchesAreAllowed()
    {
        var a = Tile("a") with { Entry = new("a", "Objects/a/entry.url", ManagedEntryKind.Url) };
        var b = Tile("b") with { Entry = new("b", "Objects/b/entry.url", ManagedEntryKind.Url) };
        var current = Wall() with { InactiveMembers = [a, b] };
        var keys = new Dictionary<string, string> { ["a"] = "same", ["b"] = "same" };
        var rejected = Apply(current, new RestoreRecycledMembersAction(0, ["a", "b"], "restored", "Restored", keys));
        Assert.False(rejected.Success); Assert.Equal("RECYCLE_DUPLICATE_LAUNCH", rejected.Detail);
        Assert.Equal(2, current.InactiveMembers.Count);
        var first = Apply(current, new RestoreRecycledMembersAction(0, ["a"], "restored", "Restored", keys));
        Assert.True(first.Success, first.Detail);
        var second = Apply(first.Proposal!.Document, new RestoreRecycledMembersAction(1, ["b"], "restored2", "Restored", keys));
        Assert.True(second.Success, second.Detail);
        Assert.Equal(new[] { "Restored", "Restored (2)" }, second.Proposal!.Document.Lanes.SelectMany(lane => lane.Groups).Select(group => group.Title));
    }

    [Fact]
    public void CapacityStalenessAndMissingSelectionDoNotConsumeRetainedMembers()
    {
        var current = Wall(true, 2);
        var action = new RestoreRecycledMembersAction(0, ["retained-0"], "restored", "Restored", new Dictionary<string, string>());
        Assert.False(WallLayoutEngine.TryApply(current, action, 0, 0).Success);
        Assert.Equal(WallRejection.StaleGeneration, Apply(current, action with { ExpectedGeneration = 2 }).Rejection);
        Assert.Equal(WallRejection.ObjectNotFound, Apply(current, action with { MemberIds = ["missing"] }).Rejection);
        Assert.Equal(2, current.InactiveMembers.Count);
        Assert.Empty(current.Lanes[0].Groups);
    }

    [Fact]
    public void DeleteClearAndDisableOnlyRemoveRetainedContent()
    {
        var current = Wall(true, 3, Tile("active"));
        var deleted = Apply(current, new DeleteRecycledMembersAction(0, ["retained-1"])).Proposal!.Document;
        Assert.Equal(new[] { "retained-0", "retained-2" }, deleted.InactiveMembers.Select(member => member.Id));
        var cleared = Apply(deleted, new ClearRecycleBinAction(1)).Proposal!.Document;
        Assert.True(cleared.RecycleBinEnabled); Assert.Empty(cleared.InactiveMembers);
        Assert.Equal("active", Assert.Single(cleared.EnumerateAllMembers()).Id);
        var disabled = Apply(current, new SetRecycleBinEnabledAction(0, false)).Proposal!.Document;
        Assert.False(disabled.RecycleBinEnabled); Assert.Empty(disabled.InactiveMembers);
        Assert.Same(current.Settings, disabled.Settings);
        Assert.Equal("active", Assert.Single(disabled.EnumerateAllMembers()).Id);
    }

    [Fact]
    public void InvalidRetentionStatesAreRejected()
    {
        Assert.Contains("RECYCLE_DISABLED_CONTENT", WallLayoutEngine.Validate(Wall(false, 1)));
        Assert.Contains("RECYCLE_CAPACITY", WallLayoutEngine.Validate(Wall(true, 201)));
    }
}
