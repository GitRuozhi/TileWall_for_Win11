using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class WallViewportPolicyTests
{
    private static StartGroup Group(string id, int height = 2) => new()
    {
        Id = id, Title = id, Members = [new TileMember { Id = id + "-tile", LocalBounds = new(0, 0, 2, height) }],
        ImportSource = new() { BatchId = "batch", GroupBucketKey = id, OriginGroupId = id },
    };

    [Fact]
    public void WindowedUsesBothNinetyPercentLimitsAndExpansionAllowsLogicalExtraColumns()
    {
        var windowed = WallViewportPolicy.Create(1920, 1080, false, expandColumns: false);
        var full = WallViewportPolicy.Create(1920, 1080, true);
        Assert.Equal(1680, windowed.MaxWidthDip);
        Assert.Equal(972, windowed.MaxHeightDip);
        Assert.Equal(3, windowed.MaxColumns);
        Assert.Equal(18, windowed.MaxRows);
        Assert.Equal(64, full.MaxColumns);
        Assert.Equal(18, full.MaxRows);
    }

    [Theory]
    [InlineData(false, false, false, 1680, 3)]
    [InlineData(false, false, true, 1424, 3)]
    [InlineData(true, false, false, 1776, 4)]
    [InlineData(true, false, true, 1520, 3)]
    [InlineData(false, true, false, 0, 64)]
    [InlineData(false, true, true, 0, 64)]
    [InlineData(true, true, false, 0, 64)]
    [InlineData(true, true, true, 0, 64)]
    public void BothModesBudgetExpansionIndependentlyOfTheFixedMenu(bool fullScreen, bool expand,
        bool menu, double width, int columns)
    {
        var metrics = new WallMetrics();
        var budget = WallViewportPolicy.Create(1920, 1080, fullScreen, metrics, expand, menu);
        Assert.Equal(expand ? metrics.WallWidth(64) : width, budget.MaxWidthDip);
        Assert.Equal(columns, budget.MaxColumns);
        Assert.Equal(18, budget.MaxRows);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(800, 600)]
    public void FullscreenVisibleViewportLeavesFivePercentAtItsOpenSideAndBothVerticalEnds(double width, double height)
    {
        var metrics = new WallMetrics();
        var visible = WallViewportPolicy.FullScreenVisibleSize(width, height, metrics);
        Assert.Equal(width * .95, visible.Width + WallViewportPolicy.FixedWidth(false), 8);
        Assert.Equal(height * .9, visible.Height, 8);
        var budget = WallViewportPolicy.Create(width, height, true, metrics);
        Assert.True(metrics.WallHeight(budget.MaxRows) <= visible.Height);
        Assert.True(metrics.WallHeight(budget.MaxRows + 1) > visible.Height);
        Assert.Equal(64, budget.MaxColumns);
        Assert.True(budget.MaxWidthDip > visible.Width);
    }

    [Theory]
    [InlineData(4, 4, true)]
    [InlineData(5, 4, false)]
    [InlineData(4, 5, false)]
    public void FinalLayoutBoundaryEnforcesOrdinaryTileMaximum(int width, int height, bool allowed)
    {
        var source = WallDocument.Empty(1, 10);
        var result = WallLayoutEngine.TryApply(source,
            new AddMemberAction(0, new TileMember { Id = "size-tile", LocalBounds = new(0, 0, width, height) }, null, 0, 0, "size-group"),
            1920, 1080);
        Assert.Equal(allowed, result.Success);
        Assert.Empty(source.Lanes[0].Groups);
    }

    [Fact]
    public void WindowedCapacityPressureCanReuseEmptyLanesWithoutChangingItsSource()
    {
        var source = WallDocument.Empty(4, 8);
        var before = WallJson.Serialize(source);
        var budget = WallViewportPolicy.Create(1920, 1080, false, expandColumns: false);
        var result = WallLayoutEngine.TryApply(source,
            new AddMemberAction(0, new TileMember { Id = "tile", LocalBounds = new(0, 0, 2, 2) }, null, 4, 0, "group"),
            budget.MaxWidthDip, budget.MaxHeightDip);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(3, result.Proposal!.Document.Columns);
        Assert.Equal("group", Assert.Single(result.Proposal.Document.Lanes.SelectMany(lane => lane.Groups)).Id);
        Assert.Equal(before, WallJson.Serialize(source));
    }

    [Fact]
    public void FixedHeightImportAddsMoreThanThreeLanesInOriginalWholeGroupOrder()
    {
        var source = WallDocument.Empty(1, 4);
        var metrics = new WallMetrics();
        var budget = new WallViewportBudget(metrics.WallWidth(64), metrics.WallHeight(4), 64, 4, true);
        var groups = Enumerable.Range(0, 5).Select(i => Group("g" + i)).ToArray();
        var result = StartMenuImportPlanner.TryPlan(source, 0, groups, metrics, budget);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(5, result.Proposal!.Document.Columns);
        Assert.Equal(4, result.Proposal.Document.Rows);
        Assert.Equal(groups.Select(g => g.Id), result.Proposal.Document.Lanes.Take(5).Select(l => Assert.Single(l.Groups).Id));
    }

    [Fact]
    public void FixedHeightImportRejectsOversizedGroupByNameAndDoesNotRaiseRows()
    {
        var source = WallDocument.Empty(1, 4);
        var before = WallJson.Serialize(source);
        var result = StartMenuImportPlanner.TryPlan(source, 0, [Group("too-tall", 4)]);
        Assert.False(result.Success);
        Assert.Equal("GROUP_TOO_TALL:too-tall", result.Detail);
        Assert.Equal(before, WallJson.Serialize(source));
    }

    [Fact]
    public void WindowedImportBeyondAvailableColumnsIsAtomic()
    {
        var metrics = new WallMetrics();
        var source = WallDocument.Empty(1, 4) with { Lanes = [new WallLane { Groups = [Group("retained")] }] };
        var budget = new WallViewportBudget(metrics.WallWidth(2), metrics.WallHeight(4), 2, 4, false);
        var result = StartMenuImportPlanner.TryPlan(source, 0, [Group("one"), Group("two")], metrics, budget);
        Assert.False(result.Success);
        Assert.Equal("CONTENT_TOO_LARGE", result.Detail);
        Assert.Single(source.Lanes);
    }

    [Fact]
    public void FailedFullscreenExitPreservesModeAndEveryLane()
    {
        var metrics = new WallMetrics();
        var source = new WallDocument
        {
            Columns = 3, Rows = 4, Settings = new() { FullScreen = true, WindowedColumns = 1, WindowedRows = 4 },
            Lanes = Enumerable.Range(0, 3).Select(i => new WallLane { Groups = [Group("g" + i)] }).ToArray(),
        };
        var before = WallJson.Serialize(source);
        var budget = new WallViewportBudget(metrics.WallWidth(1), metrics.WallHeight(4), 1, 4, false);
        var result = WallViewportPolicy.TryResize(source, 1, 4, source.Settings with { FullScreen = false }, budget);
        Assert.False(result.Success);
        Assert.Equal(before, WallJson.Serialize(source));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(65, 4)]
    [InlineData(1, 257)]
    public void PersistedRestoreFieldsAreValidated(int columns, int rows)
    {
        var source = WallDocument.Empty(1, 4) with { Settings = new()
            { WindowedColumns = columns, WindowedRows = rows } };
        Assert.NotEmpty(WallLayoutEngine.Validate(source));
    }

    [Fact]
    public void OptionalModeFieldsRoundTripWithoutSchemaChange()
    {
        var source = WallDocument.Empty(1, 4) with { Settings = new()
            { FullScreen = true, AnchorCorner = AnchorCorner.TopRight, WindowedColumns = 2, WindowedRows = 8,
                WindowedViewportWidthDip = 1280.5, ManualWindowedViewportWidthDip = 990.25 } };
        Assert.Equal(source.Settings, WallJson.Freeze(source).Settings);
        var next = WallJson.Deserialize(WallJson.Serialize(source));
        Assert.Equal(WallDocument.CurrentSchemaVersion, next.SchemaVersion);
        Assert.Equal(source.Settings, next.Settings);
        Assert.Equal(10, new WallSettings().BackgroundTransparency);
    }

    [Fact]
    public void MenuAndManualExpansionChoiceShareTheSameVisibleCapacity()
    {
        var metrics = new WallMetrics();
        var visible = WallViewportPolicy.FullScreenVisibleSize(1280, 720, metrics, menuStart: true);
        var limited = WallViewportPolicy.Create(1280, 720, true, metrics, expandColumns: false, menuStart: true);
        var expanded = WallViewportPolicy.Create(1280, 720, true, metrics, expandColumns: true, menuStart: true);
        Assert.Equal(912, visible.Width);
        Assert.Equal(1216, visible.Width + WallViewportPolicy.FixedWidth(true));
        Assert.Equal(visible.Width, limited.MaxWidthDip);
        Assert.Equal(2, limited.MaxColumns);
        Assert.Equal(64, expanded.MaxColumns);
        Assert.Equal(limited.MaxRows, expanded.MaxRows);
    }

    [Fact]
    public void ExpansionCanScrollAFullLaneThroughANarrowPositiveViewport()
    {
        var budget = WallViewportPolicy.Create(750, 600, true, expandColumns: true, menuStart: true);
        Assert.Equal(64, budget.MaxColumns);
        var current = WallDocument.Empty(1, 4);
        var result = WallViewportPolicy.TryResize(current, 1, 4,
            new() { FullScreen = true, ExpandColumns = true, MenuStart = true }, budget);
        Assert.True(result.Success, result.Detail);
        Assert.True(result.Proposal!.Document.Settings.ExpandColumns);

        var noVisibleSpace = WallViewportPolicy.Create(300, 600, true, expandColumns: true, menuStart: true);
        Assert.Equal(0, noVisibleSpace.MaxColumns);
        Assert.False(WallViewportPolicy.TryResize(current, 1, 4,
            new() { FullScreen = true, ExpandColumns = true, MenuStart = true }, noVisibleSpace).Success);
    }

    [Fact]
    public void ModeTransitionsPreserveTheRequestedExpansionAndAnchor()
    {
        var windowed = new WallSettings();
        var entered = WallViewportPolicy.ForModeTransition(windowed, windowed with { FullScreen = true });
        Assert.False(entered.ExpandColumns);
        var expanded = entered with { ExpandColumns = true, AnchorCorner = AnchorCorner.TopRight };
        var exited = WallViewportPolicy.ForModeTransition(expanded, expanded with { FullScreen = false });
        Assert.True(exited.ExpandColumns);
        Assert.Equal(AnchorCorner.TopRight, exited.AnchorCorner);
        Assert.Equal(expanded, WallViewportPolicy.ForModeTransition(windowed, expanded));
    }

    [Fact]
    public void DisplayFillerMapsToTheExistingTailOrOneAppendLaneWithoutCreatingHoles()
    {
        var source = WallDocument.Empty(3, 4) with
        {
            Settings = new() { FullScreen = true, ExpandColumns = true },
            Lanes = [new() { Groups = [Group("a")] }, new() { Groups = [Group("b")] }, new() { Groups = [] }],
        };
        Assert.Equal(2, WallViewportPolicy.MapCreationLane(source, 50));
        Assert.Equal(1, WallViewportPolicy.MapCreationLane(source, 1));
        Assert.Equal(3, WallViewportPolicy.MapCreationLane(source with { Settings = source.Settings with { ExpandColumns = false } }, 50));
        Assert.Equal(50, WallViewportPolicy.MapCreationLane(source with { Settings = new() }, 50));
        var windowed = source with { Settings = source.Settings with { FullScreen = false } };
        Assert.Equal(2, WallViewportPolicy.MapCreationLane(windowed, 50));
        Assert.Equal(1, WallViewportPolicy.MapCreationLane(windowed, 1));
    }

    [Fact]
    public void NarrowingKeepsExpansionWhileOneCompleteEmptyLaneFits()
    {
        var source = ExpandedWindow(Group("a"));
        var metrics = new WallMetrics();
        var wide = new WallViewportBudget(metrics.WallWidth(2), metrics.WallHeight(4), 2, 4, false);
        Assert.Null(WallViewportPolicy.TryCloseWindowedExpansion(source, wide));
        var narrow = wide with { MaxWidthDip = metrics.WallWidth(2) - .01, MaxColumns = 1 };
        var result = WallViewportPolicy.TryCloseWindowedExpansion(source, narrow);
        Assert.NotNull(result);
        Assert.True(result.Success, result.Detail);
        Assert.False(result.Proposal!.Document.Settings.ExpandColumns);
        Assert.Single(result.Proposal.Document.Lanes);
        Assert.True(source.Settings.ExpandColumns);
        Assert.Equal(0, source.Generation);
    }

    [Fact]
    public void NarrowingUsesTheCompleteOrdinaryFitAndKeepsExpansionWhenItCannotFit()
    {
        var metrics = new WallMetrics();
        var source = ExpandedWindow(Group("a"), Group("b"));
        var before = WallJson.Serialize(source);
        var oneColumn = new WallViewportBudget(metrics.WallWidth(1), metrics.WallHeight(4), 1, 4, false);
        Assert.Null(WallViewportPolicy.TryCloseWindowedExpansion(source, oneColumn));
        Assert.Equal(before, WallJson.Serialize(source));

        // A diagonal drag narrows the same four-row source while adding enough height
        // for its two whole groups. The candidate must use the requested eight rows.
        var result = WallViewportPolicy.TryCloseWindowedExpansion(source,
            oneColumn with { MaxHeightDip = metrics.WallHeight(8), MaxRows = 8 });
        Assert.NotNull(result);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(8, result.Proposal!.Document.Rows);
        Assert.Equal(new[] { "a", "b" }, result.Proposal!.Document.Lanes[0].Groups.Select(group => group.Id));
        Assert.Equal(1, result.Proposal.Document.Generation);
        Assert.Equal(before, WallJson.Serialize(source));
    }

    private static WallDocument ExpandedWindow(params StartGroup[] groups) => new()
    {
        Columns = groups.Length + 1, Rows = 4,
        Settings = new() { ExpandColumns = true },
        Lanes = [.. groups.Select(group => new WallLane { Groups = [group] }), new() { Groups = [] }],
    };

    [Fact]
    public void DisabledExpansionCanFillItsLastVisibleLaneWithoutReservingAnotherTail()
    {
        var metrics = new WallMetrics();
        var budget = new WallViewportBudget(metrics.WallWidth(2), metrics.WallHeight(4), 2, 4, true);
        var source = WallDocument.Empty(1, 4) with
        {
            Settings = new() { FullScreen = true, ExpandColumns = false },
            Lanes = [new() { Groups = [Group("a")] }],
        };
        var added = WallLayoutEngine.TryApply(source, new AddMemberAction(0,
            new TileMember { Id = "b-tile", LocalBounds = new(0, 0, 2, 2) }, null,
            WallViewportPolicy.MapCreationLane(source, 8), 0, "b"), budget.MaxWidthDip, budget.MaxHeightDip);

        Assert.True(added.Success, added.Detail);
        Assert.Equal(2, added.Proposal!.Document.Columns);
        Assert.All(added.Proposal.Document.Lanes, lane => Assert.NotEmpty(lane.Groups));
        Assert.False(added.Proposal.Document.Settings.ExpandColumns);
        var full = added.Proposal.Document;
        var before = WallJson.Serialize(full);
        var rejected = WallLayoutEngine.TryApply(full, new AddMemberAction(full.Generation,
            new TileMember { Id = "c-tile", LocalBounds = new(0, 0, 2, 2) }, null,
            WallViewportPolicy.MapCreationLane(full, 8), 0, "c"), budget.MaxWidthDip, budget.MaxHeightDip);
        Assert.False(rejected.Success);
        Assert.Equal(before, WallJson.Serialize(full));
    }
}

