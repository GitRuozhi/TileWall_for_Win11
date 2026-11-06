using System.Text.Json;
using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class MemberVisualSceneTests
{
    private static AutomaticIconSeries Series() => new("confirmed",
        [new(new(new string('A', 64), ".png", 1), 48, 48),
         new(new(new string('B', 64), ".png", 2), 128, 128)], 1, 2, 2);

    private static TileMember Tile() => new()
    {
        Id = "bound", LocalBounds = new(0, 0, 2, 2), TargetInput = "app.exe",
        Entry = new("entry", "Entries/app.lnk", ManagedEntryKind.Lnk),
        SourceShortcutPath = "app.lnk", TargetDisplayName = "Application",
        Images = new() { IconSeries = Series() },
    };

    private static WallDocument Document(params WallMember[] members) => WallDocument.Empty(1, 4) with
    { Lanes = [new WallLane { Groups = [new StartGroup { Id = "group", Members = members }] }] };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedSnapshotsRetainBoundTileAndPuzzleScenes(bool puzzle)
    {
        WallMember original = puzzle ? new PuzzleMember
        {
            Id = "bound", LocalBounds = new(0, 0, 2, 2),
            Partitions = [new(0, 0, 1, 2), new(1, 0, 1, 2)],
            Images = new() { IconSeries = Series() },
        } : Tile();
        var document = Document(original);
        for (var commit = 0; commit < 3; commit++)
        {
            document = WallJson.Freeze(document);
            var next = document.Lanes[0].Groups[0].Members[0];
            Assert.NotSame(original.Images.IconSeries!.Frames, next.Images.IconSeries!.Frames);
            Assert.True(MemberPresentationRules.CanReuseVisualScene(original, next));
            original = next;
        }
    }

    [Fact]
    public void RepeatedSnapshotsWithUnknownOptionalFieldsRetainOnlyUnchangedKnownPresentation()
    {
        static Dictionary<string, JsonElement> Fields() => new()
        { ["optionalPresentation"] = JsonSerializer.SerializeToElement(new { Label = "retained", Enabled = true }) };
        var tile = Tile();
        WallMember original = tile with
        {
            ExtensionData = Fields(), Visual = tile.Visual with { ExtensionData = Fields() },
            Images = tile.Images with { ExtensionData = Fields() },
            ImageDisplay = tile.ImageDisplay with { ExtensionData = Fields() },
        };
        var document = Document(original);
        for (var commit = 0; commit < 3; commit++)
        {
            document = WallJson.Freeze(document);
            var next = document.Lanes[0].Groups[0].Members[0];
            Assert.NotSame(original.ExtensionData, next.ExtensionData);
            Assert.NotSame(original.Visual.ExtensionData, next.Visual.ExtensionData);
            Assert.NotSame(original.Images.ExtensionData, next.Images.ExtensionData);
            Assert.NotSame(original.ImageDisplay.ExtensionData, next.ImageDisplay.ExtensionData);
            Assert.True(MemberPresentationRules.CanReuseVisualScene(original, next));
            Assert.False(MemberPresentationRules.CanReuseVisualScene(original, next with { TargetInput = "other.exe" }));
            Assert.False(MemberPresentationRules.CanReuseVisualScene(original, next with
                { Visual = next.Visual with { ShowTitle = false } }));
            Assert.False(MemberPresentationRules.CanReuseVisualScene(original, next with
                { Images = next.Images with { Path = "other" } }));
            Assert.False(MemberPresentationRules.CanReuseVisualScene(original, next with
                { ImageDisplay = next.ImageDisplay with { Mode = ImageDisplayMode.Original } }));
            original = next;
        }
    }

    [Fact]
    public void BindingAnotherMemberDoesNotReplaceAnAlreadyBoundScene()
    {
        var document = WallJson.Freeze(Document(Tile(), Tile() with
        { Id = "legacy", LocalBounds = new(2, 0, 2, 2), Images = new() }));
        var group = document.Lanes[0].Groups[0];
        var bound = group.Members[0];
        var legacy = group.Members[1];
        var committed = WallJson.Freeze(document with
        {
            Lanes = [document.Lanes[0] with
            {
                Groups = [group with { Members = [bound, legacy with { Images = bound.Images }] }],
            }],
        });
        Assert.True(MemberPresentationRules.CanReuseVisualScene(bound, committed.Lanes[0].Groups[0].Members[0]));
        Assert.False(MemberPresentationRules.CanReuseVisualScene(legacy, committed.Lanes[0].Groups[0].Members[1]));
    }

    [Fact]
    public void PlacementAndCommitBookkeepingRetainTheScene()
    {
        var original = Tile();
        var next = original with
        {
            LocalBounds = new(3, 1, 2, 2), UserClaimed = true,
            ImportSource = new()
            {
                BatchId = "batch", GroupBucketKey = "bucket", OriginGroupId = "origin",
                SourceRoot = "start", RelativePath = "app.lnk",
            },
            Carousel = new() { CandidateIds = ["a"], Queue = ["a"], CurrentImageId = "a", NextIndex = 1 },
        };
        var frozen = WallJson.Freeze(Document(next)).Lanes[0].Groups[0].Members[0];
        Assert.True(MemberPresentationRules.CanReuseVisualScene(original, frozen));
    }

    [Fact]
    public void ActualSourceTargetAppearanceAndSizeChangesStillReplaceTheScene()
    {
        var original = Tile();
        var series = original.Images.IconSeries!;
        (string Name, WallMember Member)[] changes =
        [
            ("title", original with { Visual = original.Visual with { TitleMode = MemberTitleMode.Manual, Title = "New" } }),
            ("title visibility", original with { Visual = original.Visual with { ShowTitle = false } }),
            ("background", original with { Visual = original.Visual with { BackgroundMode = MemberBackgroundMode.Blur } }),
            ("target", original with { TargetInput = "other.exe" }),
            ("entry", original with { Entry = new("other", "Entries/other.lnk", ManagedEntryKind.Lnk) }),
            ("shortcut", original with { SourceShortcutPath = "other.lnk" }),
            ("display name", original with { TargetDisplayName = "Other" }),
            ("origin", original with { ResourceOrigin = MemberResourceOrigin.Package }),
            ("source kind", original with { Images = new() { Kind = MemberImageSourceKind.None } }),
            ("source path", original with { Images = original.Images with { Path = "other" } }),
            ("display mode", original with { ImageDisplay = new() { Mode = ImageDisplayMode.Original } }),
            ("size", original with { LocalBounds = new(0, 0, 4, 2) }),
            ("series identity", original with { Images = original.Images with { IconSeries = series with { Identity = "other" } } }),
            ("selected frame", original with { Images = original.Images with { IconSeries = series with { SelectedFrame = 0 } } }),
            ("native frame", original with { Images = original.Images with { IconSeries = series with
                { Frames = [series.Frames[0] with { Width = 32 }, series.Frames[1]] } } }),
        ];
        foreach (var (name, replacement) in changes)
            Assert.False(MemberPresentationRules.CanReuseVisualScene(original, replacement), name);
    }

    [Fact]
    public void OnlyProvenLocalAutomaticBindingCanRetainAnUnboundScene()
    {
        var bound = Tile();
        var legacy = bound with { Images = bound.Images with { IconSeries = null } };
        Assert.False(MemberPresentationRules.CanReuseVisualScene(legacy, bound));
        Assert.True(MemberPresentationRules.CanReuseVisualScene(legacy, bound, retainsPreparedAutomaticFrame: true));
        Assert.False(MemberPresentationRules.CanReuseVisualScene(bound, legacy, retainsPreparedAutomaticFrame: true));
        Assert.False(MemberPresentationRules.CanReuseVisualScene(legacy, bound with { TargetInput = "other.exe" }, true));
        Assert.False(MemberPresentationRules.CanReuseVisualScene(legacy, bound with
            { Images = bound.Images with { Path = "other" } }, true));
        Assert.False(MemberPresentationRules.CanReuseVisualScene(legacy, bound with
            { Visual = bound.Visual with { BackgroundTransparency = 75 } }, true));
        Assert.False(MemberPresentationRules.CanReuseVisualScene(legacy, bound with
            { LocalBounds = new(0, 0, 4, 2) }, true));
        Assert.False(MemberPresentationRules.CanReuseVisualScene(legacy with { ResourceOrigin = MemberResourceOrigin.Package },
            bound with { ResourceOrigin = MemberResourceOrigin.Package }, true));
        Assert.False(MemberPresentationRules.CanReuseVisualScene(legacy with
            { Images = legacy.Images with { Kind = MemberImageSourceKind.Single } }, bound, true));
        Assert.False(MemberPresentationRules.CanReuseVisualScene(bound, bound with
            { Images = bound.Images with { IconSeries = Series() with { Identity = "other" } } }, true));
    }

    [Fact]
    public void PuzzlePartitionsAndClockFormatsRemainPartOfTheScene()
    {
        var puzzle = new PuzzleMember
        {
            Id = "puzzle", LocalBounds = new(0, 0, 2, 2),
            Partitions = [new(0, 0, 1, 2), new(1, 0, 1, 2)],
        };
        Assert.False(MemberPresentationRules.CanReuseVisualScene(puzzle, puzzle with
            { Partitions = [new(0, 0, 2, 1), new(0, 1, 2, 1)] }));
        Assert.False(MemberPresentationRules.CanReuseVisualScene(puzzle, Tile() with { Id = puzzle.Id }));
        var clock = new ClockMember { Id = "clock", LocalBounds = new(0, 0, 2, 2) };
        Assert.True(MemberPresentationRules.CanReuseVisualScene(clock, WallJson.Freeze(Document(clock)).Lanes[0].Groups[0].Members[0]));
        Assert.False(MemberPresentationRules.CanReuseVisualScene(clock, clock with { TimeFormat = "HH:mm:ss" }));
        Assert.False(MemberPresentationRules.CanReuseVisualScene(clock, clock with { DateFormat = "yyyy/M/d" }));
    }
}
