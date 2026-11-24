using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class WallDropPlanTests
{
    private static readonly WallMetrics Metrics = new();

    [Fact]
    public void StationaryPlacementDoesNotSave()
    {
        var basis = InitialWall();
        Assert.False(Drop(basis, 0).HasChange);
        Assert.True(Drop(basis, 1).HasChange);
    }

    [Fact]
    public void SecondDropCanBePlannedBeforeFirstIsAcceptedAndReusesAcceptedMemberData()
    {
        var basis = InitialWall();
        var first = Drop(basis, 1);
        var second = Drop(first.Proposal.Document, 2);
        Assert.Null(second.Revalidate(basis, 1000, 1000, Metrics));

        var group = first.Proposal.Document.Lanes[0].Groups[0];
        var member = group.Members[0];
        var latest = member with { TargetDisplayName = "accepted metadata" };
        var accepted = WallJson.Freeze(first.Proposal.Document with
        {
            Lanes = [new WallLane { Groups = [group with { Members = [latest] }] }]
        });
        var prepared = second.Revalidate(accepted, 1000, 1000, Metrics);
        Assert.NotNull(prepared);
        Assert.True(second.Matches(prepared.Document));
        Assert.Equal("accepted metadata", prepared.Document.EnumerateAllMembers().Single().TargetDisplayName);
        Assert.Null(second.Revalidate(accepted with { Settings = accepted.Settings with { LayoutLocked = true } }, 1000, 1000, Metrics));
    }

    private static WallDropPlan Drop(WallDocument basis, int column)
    {
        var action = new MoveMemberAction(basis.Generation, "tile", new GridRect(column, 0, 1, 1), "group", 0, 0, null);
        var displayed = WallDropPlan.Calculate(basis, action, false, 1000, 1000, Metrics);
        Assert.NotNull(displayed.Proposal);
        return new(basis, displayed, action, false);
    }

    private static WallDocument InitialWall() => WallDocument.Empty(1, 6) with
    {
        Lanes = [new WallLane { Groups = [new StartGroup
        {
            Id = "group", Members = [new TileMember { Id = "tile", LocalBounds = new(0, 0, 1, 1) }]
        }] }]
    };
}
