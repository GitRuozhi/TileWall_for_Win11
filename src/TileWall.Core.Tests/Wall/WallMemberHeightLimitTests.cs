using TileWall.Core.Application;
using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class WallMemberHeightLimitTests
{
    private static WallViewportBudget RuntimeBudget => WallViewportPolicy.Create(3440, 1440, true);
    private static WallMember Member(string kind, int height) => kind == "tile"
        ? new TileMember { Id = "member", LocalBounds = new(0, 0, 2, height) }
        : new PuzzleMember { Id = "member", LocalBounds = new(0, 0, 2, height), Partitions = PartitionLayout.FullGrid(new GridSize(2, height)).Partitions };
    private static WallDocument Document(WallMember member) => WallDocument.Empty(1, 20) with
    {
        Settings = new() { FullScreen = true },
        Lanes = [new WallLane { Groups = [new StartGroup { Id = "group", Members = [member] }] }],
    };

    [Theory]
    [InlineData("tile", "add", 4, true)]
    [InlineData("tile", "add", 5, false)]
    [InlineData("tile", "replace", 4, true)]
    [InlineData("tile", "replace", 5, false)]
    [InlineData("tile", "move", 4, true)]
    [InlineData("tile", "move", 5, false)]
    [InlineData("puzzle", "add", 12, true)]
    [InlineData("puzzle", "add", 13, false)]
    [InlineData("puzzle", "replace", 12, true)]
    [InlineData("puzzle", "replace", 13, false)]
    [InlineData("puzzle", "move", 12, true)]
    [InlineData("puzzle", "move", 13, false)]
    public void AllContentActionsRespectMemberSizeLimits(string kind, string actionKind, int height, bool expected)
    {
        // Shared limits now reject invalid creation, replacement and movement in the engine.
        // Puzzle fixtures use complete one-cell partitions so partition size cannot mask the limit.
        var source = actionKind == "add" ? WallDocument.Empty(1, 20) with { Settings = new() { FullScreen = true } }
            : Document(Member(kind, kind == "tile" ? 4 : 12));
        var before = WallJson.Serialize(source);
        WallAction action = actionKind switch
        {
            "add" => new AddMemberAction(0, Member(kind, height), null, NewGroupId: "group"),
            "replace" => new ReplaceMemberAction(0, "member", Member(kind, height)),
            _ => new MoveMemberAction(0, "member", new GridRect(1, 0, 2, height), "group"),
        };
        var geometryProposal = WallLayoutEngine.TryApply(source, action, RuntimeBudget.MaxWidthDip, RuntimeBudget.MaxHeightDip);
        Assert.Equal(expected, geometryProposal.Success);
        if (expected)
        {
            Assert.True(WallViewportPolicy.Validate(geometryProposal.Proposal!.Document, RuntimeBudget).Success);
        }
        else
        {
            Assert.Equal(WallRejection.InvalidAction, geometryProposal.Rejection);
            Assert.Contains("MEMBER_SIZE_LIMIT", geometryProposal.Detail);
            Assert.Null(geometryProposal.Proposal);
        }
        Assert.Equal(before, WallJson.Serialize(source));
    }

    [Fact]
    public void ProposalCheckRejectsAThirteenRowClockAndAShrunkBudget()
    {
        var clock = new ClockMember { Id = "clock", LocalBounds = new(0, 0, 2, 13) };
        var document = Document(clock);
        var proposal = new WallProposal(document, WallLayoutEngine.Measure(document));
        var tooTall = WallViewportPolicy.Validate(proposal, RuntimeBudget);
        Assert.False(tooTall.Success);
        Assert.Contains("MEMBER_TOO_TALL", tooTall.Detail);

        var tile = new TileMember { Id = "member", LocalBounds = new(0, 0, 2, 2) };
        var small = Document(tile);
        var fitted = new WallProposal(small, WallLayoutEngine.Measure(small));
        var tiny = WallViewportPolicy.Create(200, 200, false);
        var rejected = WallViewportPolicy.Validate(fitted, tiny);
        Assert.False(rejected.Success);
        Assert.Equal(WallJson.Serialize(small), WallJson.Serialize(fitted.Document));
    }

    [Theory]
    [InlineData("tile", 5)]
    [InlineData("puzzle", 13)]
    public void ImportRejectsTheEntireBatchContainingAnOversizedMember(string kind, int height)
    {
        var source = WallDocument.Empty(1, 20) with { Settings = new() { FullScreen = true } };
        var before = WallJson.Serialize(source);
        var group = new StartGroup
        {
            Id = "import", Members = [Member(kind, height)],
            ImportSource = new() { BatchId = "batch", GroupBucketKey = "bucket", OriginGroupId = "import" },
        };
        var result = StartMenuImportPlanner.TryPlan(source, 0, [group], budget: RuntimeBudget);
        Assert.False(result.Success);
        Assert.Contains("MEMBER_SIZE_LIMIT", result.Detail);
        Assert.Null(result.Proposal);
        Assert.Equal(before, WallJson.Serialize(source));
    }

    [Fact]
    public void ResizeCannotMakeAnOversizedMemberLegalByRemovingWhitespace()
    {
        var source = Document(Member("puzzle", 13));
        var before = WallJson.Serialize(source);
        var result = WallViewportPolicy.TryResize(source, 1, 20, source.Settings, RuntimeBudget);
        Assert.False(result.Success);
        Assert.Equal("MEMBER_TOO_TALL:13:12", result.Detail);
        Assert.Null(result.Proposal);
        Assert.Equal(before, WallJson.Serialize(source));
    }

    [Fact]
    public void UndoCannotRestoreAnOversizedMemberAndRetainsItsToken()
    {
        var files = new InMemoryFileStore();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wall-member-height-" + Guid.NewGuid().ToString("N")));
        var session = new WallSession(root, files, new FakeLinkFileService(files));
        Assert.Equal(WallOpenStatus.Loaded, session.Open(1, 20).Status);
        // Clock dimensions retain their structural rule. This fixture reaches the generic
        // viewport guard that tiles/puzzles now reject earlier in the shared size contract.
        var clock = new ClockMember { Id = "member", LocalBounds = new(0, 0, 2, 13) };
        var structural = session.Propose(new AddMemberAction(0, clock, null, NewGroupId: "group"), 3000, 2000);
        Assert.True(structural.Success, structural.Detail);
        Assert.True(session.Commit(structural.Proposal!).Committed);
        var removed = session.Propose(new RemoveMemberAction(session.Current.Generation, "member"), 3000, 2000);
        Assert.True(session.Commit(removed.Proposal!).Committed);
        var before = WallJson.Serialize(session.Current);
        var saved = files.ReadAllBytes(Path.Combine(root, "config.json"));
        var result = session.Undo(candidate =>
        {
            var fit = WallViewportPolicy.Validate(candidate, RuntimeBudget);
            return fit.Success ? null : fit.Detail;
        });
        Assert.False(result.Committed);
        Assert.Equal("MEMBER_TOO_TALL:13:12", Assert.Single(result.Diagnostics));
        Assert.True(session.CanUndo);
        Assert.Equal(before, WallJson.Serialize(session.Current));
        Assert.Equal(saved, files.ReadAllBytes(Path.Combine(root, "config.json")));
    }
}
