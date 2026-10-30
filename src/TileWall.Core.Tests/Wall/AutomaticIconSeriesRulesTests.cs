using TileWall.Core.Wall;
using TileWall.Core.Storage;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class AutomaticIconSeriesRulesTests
{
    private static AutomaticIconSeries Series(params (int Width, int Height)[] sizes) => new("confirmed",
        sizes.Select((size, index) => new AutomaticIconFrame(new(new string((char)('A' + index), 64), ".png", 1),
            size.Width, size.Height)).ToArray(), 0, 1, 1);

    [Fact]
    public void SelectsSmallestFrameCoveringBothAxesAndFallsBackToLargestNativeFrame()
    {
        var series = Series((48, 24), (128, 64), (256, 128));
        Assert.Equal(1, AutomaticIconSeriesRules.SelectFrame(series, 100, 50));
        Assert.Equal(2, AutomaticIconSeriesRules.SelectFrame(series, 100, 65));
        Assert.Equal(2, AutomaticIconSeriesRules.SelectFrame(series, 1000, 500));
    }

    [Fact]
    public void SameLogicalSizeKeepsConfirmedSelectionRegardlessOfChangedPixelDemand()
    {
        var series = Series((48, 48), (128, 128), (256, 256)) with { SelectedFrame = 1, Columns = 2, Rows = 2 };
        Assert.Same(series, AutomaticIconSeriesRules.SelectForSize(series, 2, 2, 500, 500));
        var resized = AutomaticIconSeriesRules.SelectForSize(series, 4, 2, 200, 100);
        Assert.Equal(2, resized.SelectedFrame);
        Assert.Equal(4, resized.Columns);
        Assert.Equal(2, resized.Rows);
        Assert.Same(series.Frames, resized.Frames);
        Assert.Equal(series.Identity, resized.Identity);
    }

    [Fact]
    public void LargeNativeDimensionsUseWideAreaArithmeticWithoutAQualityCeiling()
    {
        var series = Series((65_536, 65_536), (80_000, 80_000));
        Assert.Equal(0, AutomaticIconSeriesRules.SelectFrame(series, 60_000, 60_000));
        Assert.Equal(1, AutomaticIconSeriesRules.SelectFrame(series, 70_000, 70_000));
    }

    [Fact]
    public void AcquiringOrSelectingAResolutionIsBookkeepingWhileChangingTheConfirmedImageIsAUserEdit()
    {
        var original = new TileMember { Id = "imported", LocalBounds = new(0, 0, 1, 1) };
        var acquired = original with { Images = new() { IconSeries = Series((48, 48), (128, 128)) } };
        Assert.False(MemberPresentationRules.HasUserPropertyChanges(original, acquired));
        var selected = acquired with { Images = acquired.Images with { IconSeries = acquired.Images.IconSeries! with { SelectedFrame = 1 } } };
        Assert.False(MemberPresentationRules.HasUserPropertyChanges(acquired, selected));
        var changed = acquired with { Images = acquired.Images with { IconSeries = acquired.Images.IconSeries! with { Identity = "different-image" } } };
        Assert.True(MemberPresentationRules.HasUserPropertyChanges(acquired, changed));
        Assert.True(MemberPresentationRules.HasUserPropertyChanges(acquired, original));
    }

    [Fact]
    public void FrozenSnapshotsKeepTheSameDisplayedFrameAndConfirmedSeriesByValue()
    {
        var series = Series((48, 48), (128, 128)) with { SelectedFrame = 1, Columns = 2, Rows = 2 };
        var member = new TileMember { Id = "bound", LocalBounds = new(0, 0, 2, 2), Images = new() { IconSeries = series } };
        var document = WallDocument.Empty(1, 2) with
        { Lanes = [new WallLane { Groups = [new StartGroup { Id = "group", Members = [member] }] }] };
        var frozen = WallJson.Freeze(WallJson.Freeze(document)).EnumerateAllMembers().Single().Images.IconSeries!;
        Assert.NotSame(series.Frames, frozen.Frames);
        Assert.True(AutomaticIconSeriesRules.SameSeries(series, frozen));
        Assert.True(AutomaticIconSeriesRules.SameSelectedFrame(series, frozen));
    }

    [Fact]
    public void DisplayEqualityIgnoresUnselectedFramesAndLogicalBoundsButTracksTheSelectedNativePlane()
    {
        var series = Series((48, 48), (128, 128)) with { SelectedFrame = 1 };
        var unselectedChange = series with
        { Frames = [series.Frames[0] with { Width = 32, Height = 32 }, series.Frames[1]], Columns = 4, Rows = 2 };
        Assert.True(AutomaticIconSeriesRules.SameSelectedFrame(series, unselectedChange));
        Assert.False(AutomaticIconSeriesRules.SameSeries(series, unselectedChange));
        Assert.False(AutomaticIconSeriesRules.SameSelectedFrame(series, series with { SelectedFrame = 0 }));
        Assert.False(AutomaticIconSeriesRules.SameSelectedFrame(series, series with { Identity = "another-source" }));
        Assert.False(AutomaticIconSeriesRules.SameSelectedFrame(series, series with
        { Frames = [series.Frames[0], series.Frames[1] with { Width = 256, Height = 256 }] }));
    }
}
