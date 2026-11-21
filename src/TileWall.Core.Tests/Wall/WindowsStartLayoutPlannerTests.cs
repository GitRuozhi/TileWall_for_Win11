using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class WindowsStartLayoutPlannerTests
{
    private static readonly WallMetrics Metrics = new();

    [Theory]
    [InlineData(5, 5, 2)]
    [InlineData(3, 3, 1)]
    public void TriesLegalWallGrowthBeforeReducingAllNewTiles(int maximumRows, int expectedRows, int tileSize)
    {
        var current = WallDocument.Empty(2, 3) with { Lanes = [Lane("first"), Lane("second")] };
        var source = ImportGroup("Windows Start", 5);

        var result = WindowsStartLayoutPlanner.TryPlan(current, [source], Budget(3, maximumRows));

        Assert.True(result.Success, result.Detail);
        var proposal = result.Proposal!;
        var next = proposal.Document;
        Assert.Equal(current.Generation + 1, next.Generation);
        Assert.Equal(expectedRows, next.Rows);
        Assert.False(next.Settings.ExpandColumns);
        Assert.Same(current.Lanes[0], next.Lanes[0]);
        Assert.Same(current.Lanes[1], next.Lanes[1]);
        var members = Assert.Single(next.Lanes[2].Groups).Members;
        Assert.Equal(source.Members.Select(member => member.Id), members.Select(member => member.Id));
        Assert.All(members, member => Assert.Equal(new GridSize(tileSize, tileSize), member.LocalBounds.Size));
        Assert.Equal(tileSize == 2 ? new GridRect(0, 2, 2, 2) : new GridRect(4, 0, 1, 1), members[4].LocalBounds);
    }

    [Fact]
    public void AppendsAfterTheLastOccupiedLaneAndContinuesLongGroupsInSourceOrder()
    {
        var current = WallDocument.Empty(4, 5) with
        {
            Lanes = [Lane("first", "Tools"), new() { Groups = [] }, Lane("third"), new() { Groups = [] }],
        };
        var source = ImportGroup(" tools ", 11);
        var following = ImportGroup("Following", 1, "next");

        var result = WindowsStartLayoutPlanner.TryPlan(current, [source, following], Budget(6, 5));

        Assert.True(result.Success, result.Detail);
        var next = result.Proposal!.Document;
        Assert.Same(current.Lanes[0], next.Lanes[0]);
        Assert.Empty(next.Lanes[1].Groups);
        Assert.Same(current.Lanes[2], next.Lanes[2]);
        var added = next.Lanes.Skip(3).SelectMany(lane => lane.Groups).ToArray();
        Assert.Equal(["tools (2)", "tools (3)", "Following"], added.Select(group => group.Title));
        Assert.Equal([8, 3, 1], added.Select(group => group.Members.Count));
        Assert.Equal(source.Members.Concat(following.Members).Select(member => member.Id),
            added.SelectMany(group => group.Members).Select(member => member.Id));
        Assert.Equal(new GridRect(0, 0, 2, 2), added[1].Members[0].LocalBounds);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 2)]
    public void NarrowWallRetainsEmptySecondLaneThroughCommitAndReopen(bool locked, bool expanded, int tileSize)
    {
        var fixedWidth = WallViewportPolicy.FixedWidth(menuStart: true);
        var current = WallDocument.Empty(expanded ? 2 : 1, 3) with
        {
            Settings = new()
            {
                LayoutLocked = locked, ExpandColumns = expanded, AnchorCorner = AnchorCorner.TopRight,
                WindowedViewportWidthDip = fixedWidth + Metrics.WallWidth(1),
                ManualWindowedViewportWidthDip = fixedWidth + Metrics.WallWidth(1),
            },
            Lanes = expanded ? [Lane("welcome"), new() { Groups = [] }] : [Lane("welcome")],
        };
        var budget = Budget(1, 9);
        var result = WindowsStartLayoutPlanner.TryPlan(current, [ImportGroup("Windows Start", 1)], budget);

        Assert.True(result.Success, result.Detail);
        var proposal = result.Proposal!;
        Assert.Equal(current.Settings with { ExpandColumns = true }, proposal.Document.Settings);
        Assert.Equal(current.Rows, proposal.Document.Rows);
        Assert.Equal(WallLayoutEngine.Measure(current).Groups[0].Bounds, proposal.Geometry.Groups[0].Bounds);
        Assert.Equal(2, proposal.Geometry.Groups[1].LaneIndex);
        Assert.Equal(Metrics.WallWidth(4), proposal.Geometry.WallWidth);
        Assert.Equal(Metrics.WallWidth(1), WallViewportPolicy.ResolveVisibleWidth(proposal.Document.Settings,
            proposal.Geometry, Metrics.WallWidth(1), budget.MaxWidthDip));

        var files = new InMemoryFileStore();
        var links = new FakeLinkFileService(files);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "windows-start-layout-test"));
        var store = new WallStore(files, links, root);
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(current).Status);
        var request = store.PlanEntries(current, proposal.Document,
            [new EntryChange.CreateUrl("pin-0", "https://example.com/")]);
        Assert.NotNull(request.Request);
        Assert.Equal(WallCommitStatus.Committed, store.Commit(current, request.Request!).Status);

        var reopened = new WallStore(files, links, root).Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        var loaded = Assert.IsType<WallDocument>(reopened.Document);
        Assert.Equal(proposal.Document.Settings, loaded.Settings);
        Assert.Equal(4, loaded.Columns);
        Assert.Empty(loaded.Lanes[1].Groups);
        Assert.Empty(loaded.Lanes[3].Groups);
        var imported = Assert.Single(Assert.Single(loaded.Lanes[2].Groups).Members);
        Assert.Equal(new GridSize(tileSize, tileSize), imported.LocalBounds.Size);
        Assert.NotNull(imported.Entry);
        var expandedBudget = budget with { MaxColumns = 64, MaxWidthDip = Metrics.WallWidth(64) };
        Assert.Same(loaded, WallViewportPolicy.AdaptToEnvironment(loaded, expandedBudget).Proposal!.Document);
    }

    [Fact]
    public void ReservesTheExpandedTailAndRejectsAnOversizedBatchAtomically()
    {
        var current = WallDocument.Empty(63, 2) with
        {
            Settings = new() { ExpandColumns = true },
            Lanes = [.. Enumerable.Range(0, 62).Select(index => Lane("old-" + index)), new() { Groups = [] }],
        };
        var before = WallJson.Serialize(current);

        var result = WindowsStartLayoutPlanner.TryPlan(current, [ImportGroup("Windows Start", 9)], Budget(3, 2));

        Assert.Equal(WallRejection.NoSpace, result.Rejection);
        Assert.Equal("COLUMN_LIMIT", result.Detail);
        Assert.Null(result.Proposal);
        Assert.Equal(before, WallJson.Serialize(current));
    }

    private static WallViewportBudget Budget(int columns, int rows) =>
        new(Metrics.WallWidth(columns), Metrics.WallHeight(rows), columns, rows, false);

    private static WallLane Lane(string id, string? title = null) => new()
    {
        Groups = [new() { Id = id, Title = title, Members = [new TileMember { Id = id + "-member", LocalBounds = new(2, 0, 1, 1) }] }],
    };

    private static WindowsStartImportGroup ImportGroup(string title, int count, string id = "pin") =>
        new(title, Enumerable.Range(0, count).Select(index =>
            new TileMember { Id = id + "-" + index, LocalBounds = new(0, 0, 2, 2) }).ToArray());
}
