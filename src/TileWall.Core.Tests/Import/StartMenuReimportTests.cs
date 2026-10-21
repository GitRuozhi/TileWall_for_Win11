using TileWall.Core.Grid;
using TileWall.Core.Import;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Import;

public sealed class StartMenuReimportTests
{
    private static TileMember Tile(string id, string identity = "app") => new()
    {
        Id = id, TargetInput = identity, LocalBounds = new GridRect(0, 0, 2, 2),
        Visual = new MemberVisual { Title = "Imported " + id },
    };

    private static StartGroup Group(string id, string? title, params WallMember[] members) =>
        new() { Id = id, Title = title, Members = members };
    private static StartMenuMergeGroup Incoming(string id, string? title, params TileMember[] members) =>
        new(Group(id, title, members), members.Select(member => new StartMenuMergeMember(member, member.TargetInput!)).ToArray());
    private static WallDocument Document(params StartGroup[] groups) => WallDocument.Empty(1, 16) with
        { Generation = 9, Lanes = [new WallLane { Groups = groups }] };

    [Fact]
    public void ImportsAAndCIntoExistingAAndBWithoutDeletingMembersOrGroups()
    {
        var old = Tile("old", "same");
        var unselected = Tile("unselected", "unselected") with { LocalBounds = new(2, 0, 2, 2) };
        var a = Group("a", "A", old, unselected);
        var b = Group("b", "B", Tile("b-member", "same"));
        var current = Document(a, b);
        var before = WallJson.Serialize(current);
        var merged = StartMenuReimport.Merge(current,
            [Incoming("new-a", " a ", Tile("incoming-a", "same"), Tile("added", "new")),
             Incoming("new-c", "C", Tile("incoming-c", "same"))], member => member.TargetInput);
        var result = StartMenuImportPlanner.TryPlan(current, current.Generation, merged.Groups);
        Assert.True(result.Success, result.Detail);
        var proposed = result.Proposal!.Document;
        Assert.Equal(new[] { "A", "B", "C" }, proposed.Lanes.SelectMany(lane => lane.Groups).Select(group => group.Title));
        var updated = proposed.Lanes.SelectMany(lane => lane.Groups).Single(group => group.Id == "a");
        Assert.Equal(new[] { "old", "unselected", "added" }, updated.Members.Select(member => member.Id));
        Assert.Equal("old", merged.MemberIds["incoming-a"]);
        Assert.Same(unselected.Visual, updated.Members[1].Visual);
        Assert.Same(b, proposed.Lanes.SelectMany(lane => lane.Groups).Single(group => group.Id == "b"));
        Assert.Equal(before, WallJson.Serialize(current));
        Assert.Equal(10, proposed.Generation);
    }

    [Fact]
    public void PreserveKeepsPuzzleTypeAppearanceTitleAndDimensionsButUpdatesSource()
    {
        var puzzle = new PuzzleMember
        {
            Id = "puzzle", TargetInput = "identity", LocalBounds = new(0, 0, 4, 4),
            Partitions = [new(0, 0, 2, 4), new(2, 0, 2, 4)],
            Visual = new() { Title = "Manual", TitleMode = MemberTitleMode.Manual, BackgroundColor = "#112233" },
            Images = new() { Kind = MemberImageSourceKind.Folder, Path = @"C:\Pictures" }, UserClaimed = true,
        };
        var current = Document(Group("manual", "Tools", puzzle));
        var next = Tile("new", "new-source") with { ImportSource = new()
            { BatchId = "new", GroupBucketKey = "folder:TOOLS", OriginGroupId = "new-group" } };
        var merged = StartMenuReimport.Merge(current,
            [new(Group("new-group", "tools", next), [new(next, "identity")])], member => member.TargetInput);
        var updated = Assert.IsType<PuzzleMember>(Assert.Single(Assert.Single(merged.Groups).Members));
        Assert.Equal("puzzle", updated.Id);
        Assert.Equal("new-source", updated.TargetInput);
        Assert.Equal(puzzle.LocalBounds, updated.LocalBounds);
        Assert.Same(puzzle.Partitions, updated.Partitions);
        Assert.Same(puzzle.Visual, updated.Visual);
        Assert.Same(puzzle.Images, updated.Images);
        Assert.True(updated.UserClaimed);
        Assert.Equal("manual", updated.ImportSource!.OriginGroupId);
    }

    [Fact]
    public void MatchingGroupKeepsIdAndTitleButAdoptsTheCompleteIncomingSourceAndClearsGroupClaim()
    {
        var oldMember = Tile("old", "same") with
        {
            UserClaimed = true,
            ImportSource = new ImportSourceMetadata
            {
                BatchId = "old-batch", GroupBucketKey = "preset:old", OriginGroupId = "target",
                SourceRoot = "old-root", RelativePath = "old.lnk",
            },
        };
        var target = Group("target", "Tools", oldMember) with
        {
            ImportSource = new ImportSourceMetadata
            {
                BatchId = "old-batch", GroupBucketKey = "preset:old", OriginGroupId = "target",
            },
            UserClaimed = true,
        };
        var incomingMember = Tile("incoming", "same") with
        {
            ImportSource = new ImportSourceMetadata
            {
                BatchId = "new-batch", GroupBucketKey = "letter:T", OriginGroupId = "incoming-group",
                SourceRoot = "UserPrograms", RelativePath = "Tools\\app.lnk",
            },
        };
        var incomingSource = new ImportSourceMetadata
        {
            BatchId = "new-batch", GroupBucketKey = "letter:T", OriginGroupId = "incoming-group",
        };
        var incomingGroup = Group("incoming-group", " tools ", incomingMember) with
        {
            ImportSource = incomingSource,
            UserClaimed = true,
        };

        var merged = StartMenuReimport.Merge(Document(target),
            [new(incomingGroup, [new(incomingMember, "same")])], member => member.TargetInput);

        var group = Assert.Single(merged.Groups);
        Assert.Equal("target", group.Id);
        Assert.Equal("Tools", group.Title);
        Assert.False(group.UserClaimed);
        Assert.Equal(incomingSource with { OriginGroupId = "target" }, group.ImportSource);
        var member = Assert.IsType<TileMember>(Assert.Single(group.Members));
        Assert.Equal("old", member.Id);
        Assert.True(member.UserClaimed);
        Assert.Equal("new-batch", member.ImportSource!.BatchId);
        Assert.Equal("letter:T", member.ImportSource.GroupBucketKey);
        Assert.Equal("target", member.ImportSource.OriginGroupId);
        Assert.Equal("Tools\\app.lnk", member.ImportSource.RelativePath);
    }

    [Fact]
    public void RenamedBGroupRemainsCustomAndReimportingBCreatesANewGroup()
    {
        var renamed = Group("old-b", "Bb", Tile("old-b-member", "old")) with
        {
            ImportSource = new ImportSourceMetadata
            {
                BatchId = "old", GroupBucketKey = "letter:B", OriginGroupId = "old-b",
            },
            UserClaimed = true,
        };
        var current = Document(renamed);
        var newMember = Tile("new-b-member", "new") with
        {
            ImportSource = new ImportSourceMetadata
            {
                BatchId = "new", GroupBucketKey = "letter:B", OriginGroupId = "new-b",
                SourceRoot = "UserPrograms", RelativePath = "B.lnk",
            },
        };
        var newGroup = Group("new-b", "B", newMember) with
        {
            ImportSource = new ImportSourceMetadata
            {
                BatchId = "new", GroupBucketKey = "letter:B", OriginGroupId = "new-b",
            },
        };

        var merged = StartMenuReimport.Merge(current,
            [new(newGroup, [new(newMember, "new")])], member => member.TargetInput);
        var result = StartMenuImportPlanner.TryPlan(current, current.Generation, merged.Groups);

        Assert.True(result.Success, result.Detail);
        var ordered = result.Proposal!.Document.Lanes.SelectMany(lane => lane.Groups).ToArray();
        Assert.Equal(new[] { "new-b", "old-b" }, ordered.Select(group => group.Id));
        Assert.Equal(new[] { "B", "Bb" }, ordered.Select(group => group.Title));
        Assert.True(ordered[1].UserClaimed);
        Assert.Equal("old-b-member", ordered[1].Members[0].Id);
        Assert.Equal("new-b-member", ordered[0].Members[0].Id);
    }

    [Fact]
    public void ResetOnlyResetsSelectedMatchAndKeepsUnselectedMember()
    {
        var selected = new PuzzleMember { Id = "old", TargetInput = "same", LocalBounds = new(0, 0, 4, 4),
            Partitions = [new(0, 0, 4, 4)], Visual = new() { Title = "Manual", TitleMode = MemberTitleMode.Manual } };
        var unselected = Tile("unselected", "other");
        var merged = StartMenuReimport.Merge(Document(Group("a", "A", selected, unselected)),
            [Incoming("fresh", "A", Tile("new", "same"))], member => member.TargetInput, preserveChanges: false);
        var members = Assert.Single(merged.Groups).Members;
        var reset = Assert.IsType<TileMember>(members[0]);
        Assert.Equal("old", reset.Id);
        Assert.Equal(new GridRect(0, 0, 2, 2), reset.LocalBounds);
        Assert.Equal("Imported new", reset.Visual.Title);
        Assert.Same(unselected, members[1]);
    }

    [Fact]
    public void SourceRegroupingDoesNotMoveItsFormerMemberOrClaimInactiveRecords()
    {
        var a = Group("a", "A", Tile("former", "same"));
        var current = Document(a) with { InactiveMembers = [Tile("inactive", "same")] };
        var merged = StartMenuReimport.Merge(current, [Incoming("c", "C", Tile("new", "same"))], _ =>
            throw new InvalidOperationException("Unrelated groups and inactive records must not be resolved."));
        Assert.Equal("c", Assert.Single(merged.Groups).Id);
        Assert.Equal("new", merged.MemberIds["new"]);
        Assert.Same(a, current.Lanes[0].Groups[0]);
        Assert.Equal("inactive", Assert.Single(current.InactiveMembers).Id);
    }

    [Fact]
    public void SameDisplayedIncomingNamesMergeButUnnamedGroupsRemainIndependent()
    {
        var unnamedOne = Group("one", null, Tile("one-member", "same"));
        var unnamedTwo = Group("two", " ", Tile("two-member", "same"));
        var merged = StartMenuReimport.Merge(Document(unnamedOne, unnamedTwo),
            [Incoming("folder", " Tools ", Tile("folder-member", "first")),
             Incoming("preset", "tools", Tile("preset-member", "second")),
             Incoming("anonymous", null, Tile("anonymous-member", "same"))], member => member.TargetInput);
        Assert.Equal(2, merged.Groups.Count);
        Assert.Equal(new[] { "folder-member", "preset-member" }, merged.Groups[0].Members.Select(member => member.Id));
        Assert.Equal("anonymous", merged.Groups[1].Id);
        Assert.Equal(2, Document(unnamedOne, unnamedTwo).Lanes[0].Groups.Count);
    }
}
