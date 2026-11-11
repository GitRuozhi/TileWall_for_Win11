using System.Text.Json;
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
    public void SharedNativeSelectionPreservesSourceTieOrderAndEmptyResult()
    {
        (int Width, int Height)[] frames = [(128, 64), (64, 128), (128, 64), (256, 128)];
        Assert.Equal(0, AutomaticIconSeriesRules.SelectNativeFrame(frames, 100, 50, static size => size));
        Assert.Equal(1, AutomaticIconSeriesRules.SelectNativeFrame(frames, 50, 100, static size => size));
        Assert.Equal(3, AutomaticIconSeriesRules.SelectNativeFrame(frames, 500, 500, static size => size));
        Assert.Equal(-1, AutomaticIconSeriesRules.SelectNativeFrame(Array.Empty<(int Width, int Height)>(),
            48, 48, static size => size));
    }

    [Theory]
    [InlineData(100, 50, 1, TileWall.Core.Imaging.ImageDisplayMode.Fill)]
    [InlineData(300, 150, 2, TileWall.Core.Imaging.ImageDisplayMode.Original)]
    [InlineData(100, 150, 2, TileWall.Core.Imaging.ImageDisplayMode.Original)]
    public void NewAutomaticDefaultFillsOrUsesTheLargestOriginalPlane(int width, int height, int selected,
        TileWall.Core.Imaging.ImageDisplayMode mode)
    {
        var series = Series((48, 24), (128, 64), (256, 128));
        var member = new TileMember { Id = "new", LocalBounds = new(0, 0, 2, 2) };
        var result = MemberPresentationRules.ApplyAutomaticImageDefaults(member, series, width, height);
        Assert.Equal(selected, result.Images.IconSeries!.SelectedFrame);
        Assert.Equal(mode, result.ImageDisplay.Mode);
        Assert.Equal(1, result.ImageDisplay.CustomScale);
        if (mode == TileWall.Core.Imaging.ImageDisplayMode.Original)
        {
            var frame = result.Images.IconSeries.Frames[selected];
            var placement = TileWall.Core.Imaging.MemberImageGeometry.Calculate(width / 1.5, height / 1.5,
                frame.Width, frame.Height, 1.5, result.ImageDisplay);
            Assert.Equal(frame.Width, placement.Bounds.Width * 1.5, 8);
            Assert.Equal(frame.Height, placement.Bounds.Height * 1.5, 8);
        }
    }

    [Fact]
    public void BindingWithoutDefaultsPreservesExplicitDisplayAndTheConfirmedReference()
    {
        var display = new TileWall.Core.Imaging.MemberImageDisplay
        { Mode = TileWall.Core.Imaging.ImageDisplayMode.Custom, CustomScale = 1.75 };
        var series = Series((48, 48), (256, 256));
        var member = new TileMember { Id = "existing", LocalBounds = new(0, 0, 2, 2), ImageDisplay = display };
        var result = MemberPresentationRules.ApplyAutomaticImageDefaults(member, series, 500, 500, applyDefaults: false);
        Assert.Same(display, result.ImageDisplay);
        Assert.Same(series, result.Images.IconSeries);
    }

    [Fact]
    public void GeometryReferenceStaysStableWhileDisplayResolutionFollowsDemand()
    {
        var series = Series((48, 48), (128, 128), (256, 256)) with { SelectedFrame = 1, Columns = 2, Rows = 2 };
        Assert.Same(series, AutomaticIconSeriesRules.SelectForSize(series, 2, 2, 500, 500));
        var resized = AutomaticIconSeriesRules.SelectForSize(series, 4, 2, 200, 100);
        Assert.Equal(1, resized.SelectedFrame);
        Assert.Equal(4, resized.Columns);
        Assert.Equal(2, resized.Rows);
        Assert.Same(series.Frames, resized.Frames);
        Assert.Equal(series.Identity, resized.Identity);
        Assert.Equal(0, AutomaticIconSeriesRules.SelectForDisplay(series, 48, 48, new()));
        Assert.Equal(1, AutomaticIconSeriesRules.SelectForDisplay(series, 100, 100, new()));
        Assert.Equal(2, AutomaticIconSeriesRules.SelectForDisplay(series, 200, 200, new()));
        Assert.Equal(2, AutomaticIconSeriesRules.SelectForDisplay(series, 500, 500, new()));
        Assert.Equal(1, AutomaticIconSeriesRules.SelectForDisplay(series, 500, 500,
            new() { Mode = TileWall.Core.Imaging.ImageDisplayMode.Original }));
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
    public void FrozenOptionalNativeMetadataDoesNotChangeTheSourceOrVisualScene()
    {
        static Dictionary<string, JsonElement> Fields() => new()
        { ["optionalNativeMetadata"] = JsonSerializer.SerializeToElement(new { Label = "retained", Value = 17 }) };
        var series = Series((48, 48), (128, 128)) with { SelectedFrame = 1, ExtensionData = Fields() };
        series = series with
        {
            Frames = series.Frames.Select(frame => frame with
            { ExtensionData = Fields(), Media = frame.Media with { ExtensionData = Fields() } }).ToArray(),
        };
        WallMember previous = new TileMember { Id = "bound", LocalBounds = new(0, 0, 2, 2), Images = new() { IconSeries = series } };
        var document = WallDocument.Empty(1, 4) with
        { Lanes = [new WallLane { Groups = [new StartGroup { Id = "group", Members = [previous] }] }] };
        for (var commit = 0; commit < 3; commit++)
        {
            document = WallJson.Freeze(document);
            var next = document.Lanes[0].Groups[0].Members[0];
            var frozen = next.Images.IconSeries!;
            Assert.NotSame(series.ExtensionData, frozen.ExtensionData);
            Assert.NotSame(series.Frames[1].ExtensionData, frozen.Frames[1].ExtensionData);
            Assert.NotSame(series.Frames[1].Media.ExtensionData, frozen.Frames[1].Media.ExtensionData);
            Assert.True(AutomaticIconSeriesRules.SameSeries(series, frozen));
            Assert.True(AutomaticIconSeriesRules.SameSelectedFrame(series, frozen));
            Assert.True(MemberPresentationRules.CanReuseVisualScene(previous, next));

            var selected = frozen.Frames[1];
            AutomaticIconFrame[] changedFrames =
            [
                selected with { Media = selected.Media with { Sha256 = new string('C', 64) } },
                selected with { Media = selected.Media with { Length = selected.Media.Length + 1 } },
                selected with { Media = selected.Media with { Extension = ".bmp" } },
                selected with { Width = selected.Width + 1 },
                selected with { Height = selected.Height + 1 },
            ];
            foreach (var changed in changedFrames)
            {
                var changedSeries = frozen with { Frames = [frozen.Frames[0], changed] };
                Assert.False(AutomaticIconSeriesRules.SameSeries(frozen, changedSeries));
                Assert.False(AutomaticIconSeriesRules.SameSelectedFrame(frozen, changedSeries));
                Assert.False(MemberPresentationRules.CanReuseVisualScene(previous, next with
                    { Images = next.Images with { IconSeries = changedSeries } }));
            }
            previous = next;
            series = frozen;
        }
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
