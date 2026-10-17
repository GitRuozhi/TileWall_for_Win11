using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Storage;

public sealed partial class WallStoreTests
{
    private static readonly DateTimeOffset InitialSwitch = DateTimeOffset.UnixEpoch;
    private static ImageCarouselState CarouselAt(string image, int index, DateTimeOffset time) => new()
    {
        CandidateIds = ["a", "b", "c"], Queue = ["a", "b", "c"],
        CurrentImageId = image, NextIndex = index, Round = 1, LastSwitchUtc = time,
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SharedMemberUndoRestoresSourceOrKeepsLaterAutomaticState(bool puzzle, bool sourceChange)
    {
        var (store, files, _, root) = Harness();
        var empty = WallDocument.Empty(1, 6);
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(empty).Status);
        WallMember original = puzzle ? new PuzzleMember
        {
            Id = "member", LocalBounds = new GridRect(0, 0, 2, 2), Partitions = [new GridRect(0, 0, 2, 2)]
        } : new TileMember { Id = "member", LocalBounds = new GridRect(0, 0, 2, 2) };
        original = original with
        {
            Images = new() { Kind = MemberImageSourceKind.Folder, Path = @"C:\Pictures" },
            Carousel = CarouselAt("a", 1, InitialSwitch), TargetInput = "https://example.com",
            ImportSource = new()
            {
                BatchId = "batch", GroupBucketKey = "letter:A", OriginGroupId = "group",
                SourceRoot = @"C:\Programs", RelativePath = "App.lnk",
            },
        };
        var populated = empty with
        {
            Generation = 1,
            Lanes = [new WallLane { Groups = [new StartGroup { Id = "group", Members = [original] }] }]
        };
        var seeded = store.Commit(empty, Plan(store, empty, populated,
            new EntryChange.CreateUrl("member", "https://example.com")), true);
        Assert.Equal(WallCommitStatus.Committed, seeded.Status);
        var before = seeded.CommittedDocument!;
        var member = before.Lanes[0].Groups[0].Members[0];
        var entryPath = EntryPath(root, member.Entry!);
        var entryBytes = files.ReadAllBytes(entryPath).ToArray();
        var editedMember = member with
        {
            UserClaimed = true,
            Images = sourceChange ? member.Images with { Path = @"C:\OtherPictures" } : member.Images,
            ImageDisplay = sourceChange ? member.ImageDisplay : new() { Mode = ImageDisplayMode.Fill },
        };
        var edited = store.Commit(before, Plan(store, before, WithSharedMember(before, editedMember)), true);
        Assert.Equal(WallCommitStatus.Committed, edited.Status);
        var saved = edited.CommittedDocument!;
        var automaticState = CarouselAt("b", 2, InitialSwitch.AddMinutes(1));
        var automatic = WithSharedMember(saved, saved.Lanes[0].Groups[0].Members[0] with { Carousel = automaticState }) with
        {
            Settings = saved.Settings with { AlwaysOnTop = false }
        };
        var advanced = store.Commit(saved, Plan(store, saved, automatic), false);
        Assert.Equal(WallCommitStatus.Committed, advanced.Status);
        Assert.Null(advanced.Undo);
        var current = advanced.CommittedDocument!;
        var undone = store.Undo(current, before, edited.Undo!, current.Generation);
        Assert.Equal(WallCommitStatus.Committed, undone.Status);
        var restored = undone.CommittedDocument!.Lanes[0].Groups[0].Members[0];
        Assert.False(restored.UserClaimed);
        Assert.Equal(member.ImportSource, restored.ImportSource);
        Assert.Equal(member.Images, restored.Images);
        Assert.Equal(member.ImageDisplay, restored.ImageDisplay);
        Assert.Equal(sourceChange ? "a" : "b", restored.Carousel!.CurrentImageId);
        Assert.Equal(sourceChange ? 1 : 2, restored.Carousel.NextIndex);
        Assert.False(undone.CommittedDocument.Settings.AlwaysOnTop);
        Assert.Equal(entryBytes, files.ReadAllBytes(entryPath));
        Assert.Equal(WallJson.Serialize(undone.CommittedDocument), WallJson.Serialize(store.Open().Document!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonUndoCannotChangeUserClaimed(bool puzzle)
    {
        var (store, _, _, _) = Harness();
        WallMember member = puzzle ? new PuzzleMember
        {
            Id = "member", LocalBounds = new GridRect(0, 0, 2, 2), Partitions = [new GridRect(0, 0, 2, 2)]
        } : new TileMember { Id = "member", LocalBounds = new GridRect(0, 0, 2, 2) };
        var initial = WallDocument.Empty(1, 6) with
        {
            Lanes = [new WallLane { Groups = [new StartGroup { Id = "group", Members = [member] }] }]
        };
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var candidate = WithSharedMember(initial, member with { UserClaimed = true });
        Assert.Equal(WallCommitStatus.Rejected, store.Commit(initial, Plan(store, initial, candidate), false).Status);
    }

    private static WallDocument WithSharedMember(WallDocument document, WallMember member) => document with
    {
        Generation = document.Generation + 1,
        Lanes = [document.Lanes[0] with
        {
            Groups = [document.Lanes[0].Groups[0] with
            {
                Members = document.Lanes[0].Groups[0].Members.Select(m => m.Id == member.Id ? member : m).ToArray()
            }]
        }]
    };
}
