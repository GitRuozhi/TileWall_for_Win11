using System.Text;
using System.Text.Json.Nodes;
using TileWall.Core.Exchange;
using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class MemberPresentationTests
{
    private static TileMember Tile() => new() { Id = "tile", LocalBounds = new GridRect(0, 0, 2, 2) };

    [Theory]
    [InlineData(null, ImageRotationInterval.Minute1)]
    [InlineData(15, ImageRotationInterval.Seconds15)]
    [InlineData(30, ImageRotationInterval.Seconds30)]
    [InlineData(300, ImageRotationInterval.Minute5)]
    public void SavedWallAndPackageIntervalsKeepExplicitValuesAndLegacyMissingFieldFallback(int? seconds, ImageRotationInterval expected)
    {
        var tile = Tile() with { RotationInterval = ImageRotationInterval.Seconds15 };
        var document = WallDocument.Empty(1, 4) with
        {
            Lanes = [new() { Groups = [new() { Id = "group", Members = [tile] }] }],
            RecycleBinEnabled = true, InactiveMembers = [tile with { Id = "retained" }],
        };
        var wall = JsonNode.Parse(WallJson.Serialize(document))!.AsObject();
        var package = JsonNode.Parse(WallPackageJson.Serialize(WallPackageContent.FromSelection(document, [tile.Id])))!.AsObject();
        foreach (var member in new[] { wall["lanes"]![0]!["groups"]![0]!["members"]![0]!.AsObject(),
            wall["inactiveMembers"]![0]!.AsObject(), package["members"]![0]!.AsObject() })
        {
            if (seconds is { } value) member["rotationInterval"] = value;
            else member.Remove("rotationInterval");
        }
        var restoredWall = WallJson.Deserialize(Encoding.UTF8.GetBytes(wall.ToJsonString()));
        var restoredPackage = WallPackageJson.Deserialize<WallPackageContent>(Encoding.UTF8.GetBytes(package.ToJsonString())).ToDocument();
        Assert.Equal(expected, restoredWall.EnumerateAllMembers().Single(member => member.Id == tile.Id).RotationInterval);
        Assert.Equal(expected, Assert.Single(restoredWall.InactiveMembers).RotationInterval);
        Assert.Equal(expected, Assert.Single(restoredPackage.EnumerateAllMembers()).RotationInterval);
    }

    [Theory]
    [InlineData(MemberImageSourceKind.Automatic, MemberBackgroundMode.Default, 50)]
    [InlineData(MemberImageSourceKind.Single, MemberBackgroundMode.Blur, 73)]
    [InlineData(MemberImageSourceKind.Folder, MemberBackgroundMode.Blur, 73)]
    public void SelectingSourceAppliesSharedDefaultsWithoutResettingDisplayOrTitle(
        MemberImageSourceKind kind, MemberBackgroundMode background, int transparency)
    {
        var original = Tile() with
        {
            Visual = new() { Title = "My title", TitleMode = MemberTitleMode.Manual,
                BackgroundMode = MemberBackgroundMode.Color, BackgroundColor = "#123456", BackgroundTransparency = 73 },
            ImageDisplay = new() { Mode = ImageDisplayMode.Custom, CustomScale = 1.75 },
            Carousel = ImageCarouselSequence.Next(["a", "b"], null, DateTimeOffset.UtcNow),
        };
        var source = new MemberImageSource { Kind = kind, Path = kind == MemberImageSourceKind.Automatic ? null : "source" };
        var result = MemberPresentationRules.ApplyImageSource(original, source);
        Assert.Same(source, result.Images);
        Assert.Null(result.Carousel);
        Assert.Equal(original.Visual with { BackgroundMode = background, BackgroundTransparency = transparency }, result.Visual);
        Assert.Same(original.ImageDisplay, result.ImageDisplay);
    }

    [Fact]
    public void SourceRecognitionAfterAppearanceEditPreservesAllVisualSettings()
    {
        var original = Tile() with
        { Visual = new() { BackgroundMode = MemberBackgroundMode.Blur, BackgroundTransparency = 73 } };
        var source = new MemberImageSource();
        var result = MemberPresentationRules.ApplyImageSource(original, source, applyDefaults: false);
        Assert.Same(source, result.Images);
        Assert.Same(original.Visual, result.Visual);
    }

    [Fact]
    public void AutomaticTitleRetainsTheObservedNativeDisplayName()
    {
        var member = Tile() with { TargetInput = "ms-settings:display", TargetDisplayName = "Display settings" };
        Assert.Equal(MemberTitleMode.Automatic, member.Visual.TitleMode);
        Assert.Equal("Display settings", MemberPresentationRules.ResolveTitle(member));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Desktop shortcut")]
    public void ShortcutTitleStaysDistinctFromResolvedExecutableAcrossImageRecognitionAndSerialization(string? observedName)
    {
        var member = Tile() with
        {
            TargetInput = @"D:\Programs\Game Launcher\launcher.exe",
            SourceShortcutPath = @"C:\Start Menu\Programs\游戏入口.lnk",
            TargetDisplayName = observedName,
            Visual = new() { Title = "launcher", TitleMode = MemberTitleMode.Automatic },
        };
        var recognized = MemberPresentationRules.ApplyImageSource(member, new() { Kind = MemberImageSourceKind.Folder, Path = "covers" });
        var document = WallDocument.Empty(1, 4) with
        { Lanes = [new() { Groups = [new() { Id = "group", Members = [recognized] }] }] };
        var restored = WallJson.Deserialize(WallJson.Serialize(document)).Lanes[0].Groups[0].Members[0];
        Assert.Equal(member.SourceShortcutPath, restored.SourceShortcutPath);
        Assert.Equal(member.TargetInput, restored.TargetInput);
        Assert.Equal(observedName ?? "游戏入口", MemberPresentationRules.ResolveTitle(restored));
        Assert.Equal("Mine", MemberPresentationRules.ResolveTitle(restored with
            { Visual = restored.Visual with { TitleMode = MemberTitleMode.Manual, Title = "Mine" } }));
        Assert.Equal("launcher", MemberPresentationRules.ResolveTitle(Tile() with { TargetInput = member.TargetInput }));
    }

    [Theory]
    [InlineData(ImageDisplayMode.Fit)]
    [InlineData(ImageDisplayMode.Fill)]
    [InlineData(ImageDisplayMode.Original)]
    [InlineData(ImageDisplayMode.Custom)]
    public void NewImagesDefaultToFillAndPersistedExplicitDisplayIsPreserved(ImageDisplayMode mode)
    {
        Assert.Equal(ImageDisplayMode.Fill, Tile().ImageDisplay.Mode);
        var tile = Tile() with { ImageDisplay = new() { Mode = mode, CustomScale = 1.75 } };
        var source = WallDocument.Empty(1, 4) with
        {
            Lanes = [new() { Groups = [new() { Id = "group", Members = [tile] }] }],
        };
        var restored = WallJson.Deserialize(WallJson.Serialize(source)).Lanes[0].Groups[0].Members[0];
        Assert.Equal(tile.ImageDisplay, restored.ImageDisplay);
    }

    [Theory]
    [InlineData(ImageDisplayMode.Fit, 0.5, 0, 25, 100, 50)]
    [InlineData(ImageDisplayMode.Fill, 1, -50, 0, 200, 100)]
    [InlineData(ImageDisplayMode.Original, 2.0 / 3, -50.0 / 3, 50.0 / 3, 400.0 / 3, 200.0 / 3)]
    [InlineData(ImageDisplayMode.Custom, 3, -250, -100, 600, 300)]
    public void OneCenteredCanvasSupportsEveryMode(ImageDisplayMode mode, double scale,
        double x, double y, double width, double height)
    {
        var result = MemberImageGeometry.Calculate(100, 100, 200, 100, 1.5,
            new MemberImageDisplay { Mode = mode, CustomScale = 3 });
        Assert.Equal(scale, result.Scale, 10);
        Assert.Equal(x, result.Bounds.X, 10);
        Assert.Equal(y, result.Bounds.Y, 10);
        Assert.Equal(width, result.Bounds.Width, 10);
        Assert.Equal(height, result.Bounds.Height, 10);
    }

    [Fact]
    public void CustomMultiplierAdaptsToEachAspectRatio()
    {
        var display = new MemberImageDisplay { Mode = ImageDisplayMode.Custom, CustomScale = 1.25 };
        var portrait = MemberImageGeometry.Calculate(100, 100, 100, 400, 1, display);
        var landscape = MemberImageGeometry.Calculate(100, 100, 400, 100, 1, display);
        Assert.Equal(500, portrait.Bounds.Height);
        Assert.Equal(500, landscape.Bounds.Width);
        Assert.Equal(1.25, MemberImageGeometry.CustomScaleFromImageScale(portrait.Scale, 100, 100, 100, 400));
    }

    [Theory]
    [InlineData(@"C:\Start\App shortcut.lnk", false, "App shortcut")]
    [InlineData(@"C:\Start\Website.url", false, "Website")]
    [InlineData(@"C:\folder.with.dot\", true, "folder.with.dot")]
    [InlineData("https://example.com/path?query=1", false, "example.com")]
    [InlineData("steam://rungameid/570", false, "rungameid")]
    [InlineData("steam:run/570", false, "steam")]
    [InlineData(@"C:\Steam\steam.exe", false, "steam")]
    [InlineData(null, false, "")]
    public void AutomaticTitleUsesSelectedTarget(string? target, bool folder, string expected) =>
        Assert.Equal(expected, MemberPresentationRules.AutomaticTitle(target, folder));

    [Fact]
    public void CarouselProgressDoesNotClaimButPropertyChangesDo()
    {
        var original = Tile();
        var advanced = original with { Carousel = ImageCarouselSequence.Next(["a", "b"], null, DateTimeOffset.UtcNow) };
        Assert.False(MemberPresentationRules.HasUserPropertyChanges(original, advanced));
        Assert.True(MemberPresentationRules.HasUserPropertyChanges(original,
            original with { Visual = original.Visual with { TitleMode = MemberTitleMode.Manual, Title = "Mine" } }));
        Assert.True(MemberPresentationRules.HasUserPropertyChanges(original,
            original with { ImageDisplay = new() { Mode = ImageDisplayMode.Custom, CustomScale = 2 } }));
    }

    [Fact]
    public void ShuffleUsesEveryCandidateOnceAndAvoidsBoundaryRepeat()
    {
        var random = new Random(123);
        ImageCarouselState? state = null;
        string? last = null;
        for (var round = 0; round < 20; round++)
        {
            var seen = new HashSet<string>();
            for (var index = 0; index < 4; index++)
            {
                state = ImageCarouselSequence.Next(["a", "b", "c", "d"], state, DateTimeOffset.UnixEpoch, random)!;
                Assert.NotEqual(last, state.CurrentImageId);
                Assert.True(seen.Add(state.CurrentImageId!));
                last = state.CurrentImageId;
            }
            Assert.Equal(4, seen.Count);
        }
    }

    [Fact]
    public void RestoredQueueResumesAndCandidateChangeRebuilds()
    {
        var state = ImageCarouselSequence.Next(["a", "b", "c"], null, DateTimeOffset.UnixEpoch, new Random(3))!;
        var tile = Tile() with { Images = new() { Kind = MemberImageSourceKind.Folder, Path = @"C:\Pictures" }, Carousel = state };
        var document = WallDocument.Empty(1, 4) with
        {
            Lanes = [new WallLane { Groups = [new StartGroup { Id = "group", Members = [tile] }] }]
        };
        var restored = WallJson.Deserialize(WallJson.Serialize(document)).Lanes[0].Groups[0].Members[0];
        var next = ImageCarouselSequence.Next(["c", "b", "a"], restored.Carousel, DateTimeOffset.UnixEpoch)!;
        Assert.Equal(state.Queue[state.NextIndex], next.CurrentImageId);
        var changed = ImageCarouselSequence.Next(["x", "y"], next, DateTimeOffset.UnixEpoch)!;
        Assert.Contains(changed.CurrentImageId, new[] { "x", "y" });
        Assert.Equal(1, changed.NextIndex);
    }

    [Fact]
    public void EmptyAndSingleCandidateAreBounded()
    {
        Assert.Null(ImageCarouselSequence.Next([], null, DateTimeOffset.UnixEpoch));
        var first = ImageCarouselSequence.Next(["a", "A"], null, DateTimeOffset.UnixEpoch)!;
        var second = ImageCarouselSequence.Next(["a"], first, DateTimeOffset.UnixEpoch)!;
        Assert.Single(second.Queue);
        Assert.Equal(first.CurrentImageId, second.CurrentImageId, ignoreCase: true);
    }

    [Theory]
    [InlineData(false, 217)]
    [InlineData(true, 51)]
    public void DefaultBackgroundIsNeutralAndOnlyBackgroundUsesTransparency(bool dark, byte channel)
    {
        var visual = new MemberVisual();
        var color = MemberBackgroundRules.GetColor(visual, dark);
        Assert.Equal(channel, color.R);
        Assert.Equal(channel, color.G);
        Assert.Equal(channel, color.B);
        Assert.Equal(0.5, color.Opacity);
        Assert.Equal(0, MemberBackgroundRules.GetColor(visual with { BackgroundMode = MemberBackgroundMode.None }, dark).Opacity);
        Assert.Equal(1, MemberBackgroundRules.GetColor(visual with { BackgroundTransparency = 0 }, dark).Opacity);
        Assert.Equal(0, MemberBackgroundRules.GetColor(visual with { BackgroundTransparency = 100 }, dark).Opacity);
    }

    [Fact]
    public void FrozenQueueCannotBeChangedThroughCallerLists()
    {
        var candidates = new List<string> { "a", "b" };
        var queue = new List<string> { "b", "a" };
        var tile = Tile() with { Carousel = new() { CandidateIds = candidates, Queue = queue } };
        var frozen = WallJson.Freeze(WallDocument.Empty(1, 4) with
        {
            Lanes = [new WallLane { Groups = [new StartGroup { Id = "group", Members = [tile] }] }]
        });
        candidates.Clear();
        queue.Clear();
        Assert.Equal(new[] { "b", "a" }, frozen.Lanes[0].Groups[0].Members[0].Carousel!.Queue);
        Assert.Equal(new[] { "a", "b" }, frozen.Lanes[0].Groups[0].Members[0].Carousel!.CandidateIds);
    }
}
