using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class WallEnvironmentAdaptationTests
{
    private static StartGroup Group(string id, int height = 2) => new()
    {
        Id = id, Title = id, Members = [new TileMember { Id = id + "-tile", LocalBounds = new(0, 0, 2, height) }],
    };

    [Fact]
    public void SmallerScreenRemovesEmptyCapacityAndPreservesAllMembers()
    {
        var source = WallDocument.Empty(4, 20) with
        { Lanes = [new() { Groups = [Group("a")] }, new() { Groups = [] }, new() { Groups = [Group("b")] }, new() { Groups = [] }] };
        var before = WallJson.Serialize(source);
        var metrics = new WallMetrics();
        var budget = new WallViewportBudget(metrics.WallWidth(2), metrics.WallHeight(8), 2, 8, false);
        var result = WallViewportPolicy.AdaptToEnvironment(source, budget);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(new[] { "a-tile", "b-tile" }, result.Proposal!.Document.EnumerateAllMembers().Select(m => m.Id));
        Assert.Equal(1, result.Proposal.Document.Generation);
        Assert.Equal(before, WallJson.Serialize(source));
    }

    [Fact]
    public void ShorterScreenCanRedistributeGroupsIntoAnotherVisibleLane()
    {
        var source = WallDocument.Empty(1, 10) with { Lanes = [new() { Groups = [Group("a"), Group("b")] }] };
        var metrics = new WallMetrics();
        var budget = new WallViewportBudget(metrics.WallWidth(2), metrics.WallHeight(4), 2, 4, false);
        var result = WallViewportPolicy.AdaptToEnvironment(source, budget);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(2, result.Proposal!.Document.Columns);
        Assert.Equal(new[] { "a", "b" }, result.Proposal.Document.Lanes.SelectMany(l => l.Groups).Select(g => g.Id));
    }

    [Fact]
    public void NarrowerScreenCanUseAvailableHeightToRetainAllGroups()
    {
        var source = WallDocument.Empty(2, 4) with
        { Lanes = [new() { Groups = [Group("a")] }, new() { Groups = [Group("b")] }] };
        var metrics = new WallMetrics();
        var budget = new WallViewportBudget(metrics.WallWidth(1), metrics.WallHeight(8), 1, 8, false);
        var result = WallViewportPolicy.AdaptToEnvironment(source, budget);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(new[] { "a", "b" }, result.Proposal!.Document.Lanes.Single().Groups.Select(g => g.Id));
    }

    [Fact]
    public void UnfittableOrZeroCapacityLeavesExactSourceForTemporaryScrolling()
    {
        var source = WallDocument.Empty(1, 14) with { Lanes = [new() { Groups = [Group("tall", 12)] }] };
        var before = WallJson.Serialize(source);
        var result = WallViewportPolicy.AdaptToEnvironment(source, WallViewportPolicy.Create(800, 600, false, expandColumns: false));
        Assert.False(result.Success);
        Assert.Equal(before, WallJson.Serialize(source));
        Assert.False(WallViewportPolicy.AdaptToEnvironment(source, new(0, 0, 0, 0, false)).Success);
    }

    [Fact]
    public void AlreadyFittingEnvironmentDoesNotCreateAnotherGeneration()
    {
        var source = WallDocument.Empty(1, 4) with { Generation = 12, Lanes = [new() { Groups = [Group("a")] }] };
        var result = WallViewportPolicy.AdaptToEnvironment(source, WallViewportPolicy.Create(1920, 1080, false));
        Assert.True(result.Success);
        Assert.Same(source, result.Proposal!.Document);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NarrowPositiveViewportProposesFormalExpansionWithoutMutatingTheSource(bool empty)
    {
        var source = WallDocument.Empty(1, 4) with
        {
            Lanes = [new() { Groups = empty ? [] : [Group("a")] }],
            Settings = new() { WindowedViewportWidthDip = 1024, ManualWindowedViewportWidthDip = 990.25 },
        };
        var before = WallJson.Serialize(source);
        var budget = WallViewportPolicy.Create(500, 1080, false, expandColumns: false);
        Assert.Equal(0, budget.MaxColumns);

        var result = WallViewportPolicy.AdaptToEnvironment(source, budget);
        Assert.True(result.Success, result.Detail);
        var candidate = result.Proposal!.Document;
        Assert.True(candidate.Settings.ExpandColumns);
        Assert.Equal(source.Settings with { ExpandColumns = true }, candidate.Settings);
        Assert.Equal(source.Generation + 1, candidate.Generation);
        Assert.Equal(empty ? 1 : 2, candidate.Columns);
        Assert.Empty(candidate.Lanes[^1].Groups);
        Assert.Equal(source.EnumerateAllMembers().Select(member => member.Id),
            candidate.EnumerateAllMembers().Select(member => member.Id));
        Assert.Equal(before, WallJson.Serialize(source));
        Assert.True(WallViewportPolicy.Validate(candidate,
            WallViewportPolicy.Create(500, 1080, false, expandColumns: true)).Success);
        Assert.Equal(budget.MaxWidthDip, WallViewportPolicy.ResolveVisibleWidth(candidate.Settings,
            result.Proposal.Geometry, budget.MaxWidthDip, budget.MaxWidthDip));
    }

    [Fact]
    public void NarrowAndShorterScreenCanOpenExpansionAndRedistributeWholeGroupsTogether()
    {
        var source = WallDocument.Empty(1, 8) with { Lanes = [new() { Groups = [Group("a"), Group("b")] }] };
        var budget = WallViewportPolicy.Create(500, 300, false, expandColumns: false);
        var result = WallViewportPolicy.AdaptToEnvironment(source, budget);
        Assert.True(result.Success, result.Detail);
        var candidate = result.Proposal!.Document;
        Assert.True(candidate.Settings.ExpandColumns);
        Assert.Equal(3, candidate.Columns);
        Assert.Equal(new[] { "a", "b" }, candidate.Lanes.SelectMany(lane => lane.Groups).Select(group => group.Id));
        Assert.Empty(candidate.Lanes[^1].Groups);
        Assert.Equal(budget.MaxRows, candidate.Rows);
    }
}
