using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Storage;

public sealed partial class WallStoreTests
{
    private const string ImageA = @"C:\Pictures\A.png";
    private const string ImageB = @"C:\Pictures\B.png";
    private const string ImageC = @"C:\Pictures\C.png";
    private static readonly DateTimeOffset InitialSwitch = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);

    private static PuzzleMember UndoPuzzle() => new()
    {
        Id = "puzzle", LocalBounds = new GridRect(0, 0, 2, 2),
        Partitions = [new GridRect(0, 0, 2, 2)],
        Images = new PuzzleImageSource { Kind = PuzzleImageSourceKind.Multiple, ImagePaths = [ImageA, ImageB] },
        Carousel = new PuzzleCarouselState(ImageA, InitialSwitch)
    };

    [Fact]
    public void ExplicitPuzzleSelectionUndoRestoresOldImageAndTimeAfterAutomaticBaselineSave()
    {
        AssertPuzzleEditUndo(UndoPuzzle(), p => p with
        {
            Carousel = new PuzzleCarouselState(ImageB, null)
        }, preserveLiveCarousel: false);
    }

    [Theory]
    [InlineData("paths")]
    [InlineData("order")]
    [InlineData("kind")]
    [InlineData("folder")]
    [InlineData("clear")]
    public void PuzzleSourceUndoRestoresMatchingSourceAndCarouselAfterAutomaticSwitch(string change)
    {
        var before = UndoPuzzle();
        if (change == "folder") before = before with
        {
            Images = new PuzzleImageSource { Kind = PuzzleImageSourceKind.Folder, FolderPath = @"C:\Pictures" }
        };
        AssertPuzzleEditUndo(before, p => p with
        {
            Images = change switch
            {
                "paths" => p.Images with { ImagePaths = [ImageA, ImageC] },
                "order" => p.Images with { ImagePaths = [ImageB, ImageA] },
                "kind" => p.Images with { Kind = PuzzleImageSourceKind.Single, ImagePaths = [ImageA] },
                "folder" => p.Images with { FolderPath = @"C:\OtherPictures" },
                _ => new PuzzleImageSource()
            },
            Carousel = change == "clear" ? null : p.Carousel
        }, preserveLiveCarousel: false,
            liveCarousel: change == "clear" ? null : new PuzzleCarouselState(
                change == "paths" ? ImageC : ImageA, InitialSwitch.AddMinutes(1)));
    }

    [Theory]
    [InlineData("layout")]
    [InlineData("transform")]
    [InlineData("title")]
    [InlineData("path-case")]
    [InlineData("folder-case")]
    public void PuzzlePropertyUndoKeepsLaterAutomaticCarouselAndSettings(string change)
    {
        var before = UndoPuzzle();
        if (change == "folder-case") before = before with
        {
            Images = new PuzzleImageSource { Kind = PuzzleImageSourceKind.Folder, FolderPath = @"C:\Pictures" }
        };
        AssertPuzzleEditUndo(before, p => change switch
        {
            "layout" => p with { LocalBounds = new GridRect(1, 1, 2, 2) },
            "transform" => p with { Images = p.Images with
            {
                Transforms = [new PuzzleImageTransform(ImageA, FitMode.CoverFill, new ImageTransform(2, 3, 4))]
            } },
            "title" => p with { Visual = p.Visual with { Title = "Edited" } },
            "path-case" => p with
            {
                Images = p.Images with { ImagePaths = [ImageA.ToLowerInvariant(), ImageB.ToUpperInvariant()] },
                Carousel = p.Carousel! with { CurrentImageId = ImageA.ToLowerInvariant() }
            },
            _ => p with { Images = p.Images with { FolderPath = @"c:\PICTURES" } }
        }, preserveLiveCarousel: true);
    }

    private static void AssertPuzzleEditUndo(PuzzleMember original, Func<PuzzleMember, PuzzleMember> edit,
        bool preserveLiveCarousel, PuzzleCarouselState? liveCarousel = null)
    {
        var (store, files, _, root) = Harness();
        var empty = WallDocument.Empty(1, 6);
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(empty).Status);
        var populated = empty with
        {
            Generation = 1,
            Lanes = [new WallLane { Groups = [new StartGroup
            {
                Id = "group", Members = [original, Tile("untouched") with { LocalBounds = new GridRect(4, 0, 1, 1) }]
            }] }]
        };
        var seeded = store.Commit(empty, Plan(store, empty, populated,
            new EntryChange.CreateUrl(original.Id, "https://puzzle.example"),
            new EntryChange.CreateUrl("untouched", "https://untouched.example")), true);
        Assert.Equal(WallCommitStatus.Committed, seeded.Status);
        var before = seeded.CommittedDocument!;
        var puzzle = (PuzzleMember)before.Lanes[0].Groups[0].Members[0];
        var entries = before.Lanes[0].Groups[0].Members.ToDictionary(m => EntryPath(root, m.Entry!),
            m => files.ReadAllBytes(EntryPath(root, m.Entry!)).ToArray());
        var editedPuzzle = edit(puzzle);
        var candidate = WithPuzzle(before, editedPuzzle);
        EntryChange[] changes = editedPuzzle.Visual.Title != puzzle.Visual.Title
            ? [new EntryChange.Rename(puzzle.Id)] : [];
        var edited = store.Commit(before, Plan(store, before, candidate, changes), true);
        Assert.Equal(WallCommitStatus.Committed, edited.Status);
        Assert.NotNull(edited.Undo);
        var saved = edited.CommittedDocument!;
        var changedPuzzle = (PuzzleMember)saved.Lanes[0].Groups[0].Members[0];
        var automaticCarousel = changedPuzzle.Images.Kind == PuzzleImageSourceKind.None ? null :
            liveCarousel ?? new PuzzleCarouselState(ImageB, InitialSwitch.AddMinutes(1));
        var automatic = WithPuzzle(saved, changedPuzzle with { Carousel = automaticCarousel }) with
        {
            Settings = saved.Settings with { AlwaysOnTop = false, StayVisibleOnFocusLoss = true }
        };
        var advanced = store.Commit(saved, Plan(store, saved, automatic), false);
        Assert.Equal(WallCommitStatus.Committed, advanced.Status);
        Assert.Null(advanced.Undo);
        var current = advanced.CommittedDocument!;
        var undone = store.Undo(current, before, edited.Undo!, current.Generation);
        Assert.Equal(WallCommitStatus.Committed, undone.Status);
        var expected = WithPuzzle(before, puzzle with
        {
            Carousel = preserveLiveCarousel ? automaticCarousel : puzzle.Carousel
        }) with { Generation = current.Generation + 1, Settings = current.Settings };
        Assert.Equal(WallJson.Serialize(expected), WallJson.Serialize(undone.CommittedDocument!));
        Assert.Equal(WallJson.Serialize(expected), WallJson.Serialize(store.Open().Document!));
        foreach (var (path, bytes) in entries) Assert.Equal(bytes, files.ReadAllBytes(path));
        var objectsRoot = Path.Combine(root, "Objects") + Path.DirectorySeparatorChar;
        Assert.Equal(entries.Keys.Order(), files.SnapshotFiles().Keys
            .Where(path => path.StartsWith(objectsRoot, StringComparison.OrdinalIgnoreCase)).Order());
    }

    private static WallDocument WithPuzzle(WallDocument document, PuzzleMember puzzle) => document with
    {
        Generation = document.Generation + 1,
        Lanes = [document.Lanes[0] with
        {
            Groups = [document.Lanes[0].Groups[0] with
            {
                Members = document.Lanes[0].Groups[0].Members.Select(m => m.Id == puzzle.Id ? puzzle : m).ToArray()
            }]
        }]
    };
}
