using TileWall.Core.Grid;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class WallMemberDropTargetTests
{
    private static readonly WallMetrics Metrics = new();

    [Theory]
    [InlineData(1, 1, 24)]
    [InlineData(1, 1, 4)]
    [InlineData(1, 1, 44)]
    [InlineData(2, 2, 50)]
    [InlineData(4, 4, 102)]
    public void BottomEdgeAppendsRegardlessOfTileSizeOrGrabOffset(int width, int height, double grabY)
    {
        var geometry = WallLayoutEngine.Measure(Document());
        var target = geometry.Groups[0];
        var pointer = new DipPoint(target.Bounds.X + Metrics.Pitch + 24,
            target.Bounds.Y + target.Bounds.Height - 2);

        Assert.Same(target, WallMemberDropResolver.FindGroup(geometry, 0, pointer));
        var bounds = WallMemberDropResolver.ResolveBounds(target, pointer, new DipPoint(24, grabY),
            new GridSize(width, height), Metrics);

        Assert.Equal(new GridRect(1, 1, width, height), bounds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void GrowingAnyStackedGroupPushesFollowingGroups(int targetIndex)
    {
        var document = Document();
        var before = WallLayoutEngine.Measure(document);
        var target = before.Groups[targetIndex];
        var pointer = new DipPoint(target.Bounds.X + Metrics.Pitch + 24,
            target.Bounds.Y + target.Bounds.Height - 2);
        var resolved = WallMemberDropResolver.FindGroup(before, 0, pointer)!;
        var bounds = WallMemberDropResolver.ResolveBounds(resolved, pointer, new DipPoint(24, 24),
            new GridSize(1, 1), Metrics);
        var move = WallLayoutEngine.TryApply(document,
            new MoveMemberAction(document.Generation, "moving", bounds, resolved.GroupId), 1000, 1000);

        Assert.True(move.Success, move.Detail);
        var after = move.Proposal!.Document;
        Assert.Equal(["first", "second", "third"], after.Lanes[0].Groups.Select(group => group.Id));
        Assert.Empty(after.Lanes[1].Groups);
        Assert.Equal(bounds, after.Lanes[0].Groups[targetIndex].Members.Single(member => member.Id == "moving").LocalBounds);
        var afterGeometry = WallLayoutEngine.Measure(after);
        for (var index = 0; index < 3; index++)
            Assert.Equal(before.Groups[index].Bounds.Y + (index > targetIndex ? Metrics.Pitch : 0),
                afterGeometry.Groups[index].Bounds.Y);
        Assert.Empty(WallLayoutEngine.Validate(after));
        Assert.Single(document.Lanes[1].Groups[0].Members);
    }

    [Fact]
    public void GroupBodyKeepsNormalGrabRelativeGridRounding()
    {
        var target = WallLayoutEngine.Measure(Document()).Groups[0];
        var pointer = new DipPoint(target.Bounds.X + Metrics.Pitch + 24,
            target.Bounds.Y + target.Bounds.Height - 13);

        var bounds = WallMemberDropResolver.ResolveBounds(target, pointer, new DipPoint(24, 24),
            new GridSize(1, 1), Metrics);

        Assert.Equal(new GridRect(1, 0, 1, 1), bounds);
    }

    [Fact]
    public void ExplicitInterGroupGapStillCreatesANewGroup()
    {
        var document = Document();
        var geometry = WallLayoutEngine.Measure(document);
        var first = geometry.Groups[0];
        var pointer = new DipPoint(first.Bounds.X + 24, first.Bounds.Y + first.Bounds.Height + Metrics.GroupGap / 2);

        Assert.Null(WallMemberDropResolver.FindGroup(geometry, 0, pointer));
        var move = WallLayoutEngine.TryApply(document,
            new MoveMemberAction(document.Generation, "moving", new GridRect(0, 0, 1, 1),
                null, 0, 1, "new-group"), 1000, 1000);

        Assert.True(move.Success, move.Detail);
        Assert.Equal(["first", "new-group", "second", "third"],
            move.Proposal!.Document.Lanes[0].Groups.Select(group => group.Id));
    }

    [Fact]
    public void GroupBottomIsExclusiveAndFollowingTitleStillTargetsFollowingGroup()
    {
        var geometry = WallLayoutEngine.Measure(Document());
        var first = geometry.Groups[0];
        Assert.Null(WallMemberDropResolver.FindGroup(geometry, 0,
            new DipPoint(first.Bounds.X, first.Bounds.Y + first.Bounds.Height)));
        var second = geometry.Groups[1];
        Assert.Same(second, WallMemberDropResolver.FindGroup(geometry, 0,
            new DipPoint(second.Bounds.X + 24, second.TitleBounds.Y + Metrics.TitleHeight / 2)));
        Assert.Null(WallMemberDropResolver.FindGroup(geometry, 2, new DipPoint(first.Bounds.X, 98)));
    }

    [Fact]
    public void GrowthRejectedByViewportKeepsOriginalDocument()
    {
        var document = Document() with
        {
            Rows = 10,
            Lanes =
            [
                new WallLane { Groups = [new StartGroup { Id = "target", Members =
                    [new TileMember { Id = "existing", LocalBounds = new GridRect(0, 0, 4, 4) }] }] },
                new WallLane { Groups = [new StartGroup { Id = "source", Members =
                    [new TileMember { Id = "moving", LocalBounds = new GridRect(0, 0, 4, 4) }] }] },
            ],
        };
        var geometry = WallLayoutEngine.Measure(document);
        var target = geometry.Groups[0];
        var pointer = new DipPoint(target.Bounds.X + 24, target.Bounds.Y + target.Bounds.Height - 2);
        var bounds = WallMemberDropResolver.ResolveBounds(target, pointer, new DipPoint(24, 24),
            new GridSize(4, 4), Metrics);
        var move = WallLayoutEngine.TryApply(document,
            new MoveMemberAction(document.Generation, "moving", bounds, target.GroupId),
            1000, target.Bounds.Y + target.Bounds.Height + Metrics.Margin);

        Assert.False(move.Success);
        Assert.Null(move.Proposal);
        Assert.Single(document.Lanes[0].Groups[0].Members);
        Assert.Single(document.Lanes[1].Groups[0].Members);
    }

    private static WallDocument Document() => new()
    {
        Columns = 2,
        Rows = 8,
        Lanes =
        [
            new WallLane { Groups = [Group("first", "one"), Group("second", "two"), Group("third", "three")] },
            new WallLane { Groups = [Group("source", "moving")] },
        ],
    };

    private static StartGroup Group(string id, string memberId) => new()
    {
        Id = id,
        Members = [new TileMember { Id = memberId, LocalBounds = new GridRect(0, 0, 1, 1) }],
    };
}
