using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Import;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Import;

public sealed class ImportSourcePlannerTests
{
    private static TileMember Tile(string id, GridRect? bounds = null) => new() { Id = id, LocalBounds = bounds ?? new(0, 0, 1, 1) };
    private static StartGroup Group(string id, string? title, params WallMember[] members) => new() { Id = id, Title = title, Members = members };
    private static ImportSourceCandidate Item(string key, string name, ImportSourceGroup? group = null, string? identity = null,
        GridRect? bounds = null, bool position = false, bool size = false, int order = 0) => new(key, name, Tile("source-" + key, bounds))
    {
        SourceGroup = group, SourceOrder = order, HasRecordedPosition = position, HasRecordedSize = size,
        Identity = identity is null ? null : LaunchIdentityService.ForPath(@"C:\Apps\" + identity + ".exe"),
    };
    private static WallViewportBudget Budget(int columns = 4, int rows = 24)
    {
        var metrics = new WallMetrics();
        return new(metrics.WallWidth(columns), metrics.WallHeight(rows), columns, rows, false);
    }
    private static ImportSourcePlan Plan(WallDocument wall, ImportSourceProjection projection, IEnumerable<string> selected,
        GridSize? size = null, ImportInsertionPosition position = ImportInsertionPosition.Head) =>
        ImportSourcePlanner.TryPlan(wall, wall.Generation, projection, selected, size ?? new(1, 1), position, budget: Budget());
    private static StartGroup Destination(ImportSourcePlan plan, string? title = null) => plan.Proposal!.Document.Lanes
        .SelectMany(lane => lane.Groups).Single(group => title is null || group.Title == title);

    [Fact]
    public void RecycledSourceKeepsMembersSeparateAndDoesNotMatchAnActiveGroup()
    {
        var wall = WallDocument.Empty(1, 8) with
        {
            Lanes = [new() { Groups = [Group("active", "Recycle bin", Tile("active-member"))] }],
        };
        var first = Item("first", "First", new("recycle", "Recycle bin", 0), bounds: new(0, 0, 2, 2), size: true) with
        {
            SourceKind = ImportPageSourceKind.RecycleBin, Policy = ImportSourcePolicy.Attributed,
        };
        var second = first with { SourceKey = "second", Member = Tile("second"), SourceOrder = 1 };
        var projection = ImportSourcePlanner.Project(wall, [first, second]);
        var group = Assert.Single(projection.Groups);
        Assert.Null(group.DestinationGroupId);
        Assert.Equal(2, group.Candidates.Count);
        Assert.Same(first.Member, group.Candidates[0].Source.Member);
        Assert.Same(second.Member, group.Candidates[1].Source.Member);
        Assert.All(group.Candidates, candidate => Assert.True(candidate.CanAdd));
    }

    [Fact]
    public void FamilyRecognitionPrecedesExistingMultiMemberGroups()
    {
        var original = new ImportSourceGroup("creative", "My tools", 0);
        var candidates = new[]
        {
            Item("one", "Adobe Photoshop", original, "photoshop"), Item("two", "Adobe Illustrator", original, "illustrator"),
            Item("three", "Adobe Acrobat", new("single-one", "Old one", 1), "acrobat"),
            Item("four", "Adobe Premiere Pro", new("single-two", "Old two", 2), "premiere"),
        };
        var projected = ImportSourcePlanner.Project(WallDocument.Empty(1, 8), candidates);
        Assert.Equal("Adobe", Assert.Single(projected.Groups).Title);
        Assert.Equal(4, projected.Groups[0].Candidates.Count);
    }

    [Fact]
    public void DuplicateSourceRepresentativesDoNotInflateOriginalGroupCount()
    {
        var source = new ImportSourceGroup("one", "Original", 0);
        var projected = ImportSourcePlanner.Project(WallDocument.Empty(1, 8),
            [Item("user", "Alpha", source, "same", order: 0), Item("common", "Alpha", source, "same", order: 1)]);
        var candidate = Assert.Single(projected.Candidates, candidate => candidate.Visible);
        Assert.Equal("A", Assert.Single(projected.Groups).Title);
        Assert.Equal("user", candidate.SelectionKey);
        Assert.Equal(["user", "common"], candidate.SourceKeys);
    }

    [Fact]
    public void StablePreferredSourceWinsWithinFinalGroupWhileSameIdentityInAnotherGroupRemains()
    {
        var shared = new ImportSourceGroup("shared", "Tools", 0);
        var user = Item("user", "User name", shared, "same", order: 0);
        var common = Item("common", "Common name", shared, "same", order: 1);
        var elsewhere = Item("elsewhere", "Another", new("other", "Other", 1), "same");
        var projected = ImportSourcePlanner.Project(WallDocument.Empty(1, 8), [common, elsewhere, user], recognizeGroups: false, autoFilter: false);
        Assert.Equal(2, projected.Candidates.Count);
        var tools = Assert.Single(projected.Groups, group => group.Title == "Tools");
        Assert.Equal("user", Assert.Single(tools.Candidates).SelectionKey);
        Assert.Equal(["user", "common"], tools.Candidates[0].SourceKeys);
        Assert.Equal("elsewhere", Assert.Single(projected.Groups, group => group.Title == "Other").Candidates[0].SelectionKey);
    }

    [Fact]
    public void SharedNormalizedSourceGroupAndLetterTitleMergeOnlyAfterFinalAssignment()
    {
        var wall = WallDocument.Empty(1, 8);
        var folder = Item("folder", "Tool", new("folder:A", " A ", 0), "tool");
        var letter = Item("letter", "Alpha", identity: "alpha");
        var projected = ImportSourcePlanner.Project(wall, [letter, folder], recognizeGroups: false);
        var group = Assert.Single(projected.Groups);
        Assert.Equal("A", group.Title);
        Assert.Equal(["letter", "folder"], group.Candidates.Select(candidate => candidate.SelectionKey));
        var plan = Plan(wall, projected, ["folder", "letter"]);
        Assert.Equal(ImportPlanStatus.Ready, plan.Status);
        Assert.Equal(2, Destination(plan).Members.Count);
    }

    [Fact]
    public void SharedOriginalGroupRetainsSourceOrderAndSingletonUsesLocalInitialFallback()
    {
        var shared = new ImportSourceGroup("folder:TOOLS", "Tools", 100);
        var user = Item("user", "Alpha", shared, "alpha") with { RelativeDirectory = @"Tools\Deep" };
        var common = Item("common", "Beta", shared with { Title = "TOOLS" }, "beta", order: 1);
        var chinese = Item("chinese", "中文", new("singleton", "Single", 101), "chinese");
        var root = Item("root", "7-Zip", identity: "root");
        var projected = ImportSourcePlanner.Project(WallDocument.Empty(1, 8), [root, chinese, common, user]);
        Assert.Equal(["#", "Z", "Tools"], projected.Groups.Select(group => group.Title));
        Assert.Equal(["user", "common"], projected.Groups[2].Candidates.Select(candidate => candidate.SelectionKey));
        Assert.Equal("chinese", projected.Groups[1].Candidates[0].SelectionKey);
    }

    [Fact]
    public void UnnamedGroupsAreRealAndIndependentAndNonEntryMembersCount()
    {
        var one = new ImportSourceGroup("one", "", 0); var two = new ImportSourceGroup("two", null, 1);
        var clock = Item("clock", "Clock", one) with { Member = new ClockMember { Id = "clock", LocalBounds = new(0, 0, 2, 2) } };
        var items = new[] { clock, Item("decor", "Decoration", one), Item("another", "Another", two), Item("last", "Last", two) };
        var projected = ImportSourcePlanner.Project(WallDocument.Empty(1, 8), items);
        Assert.Equal(2, projected.Groups.Count);
        Assert.All(projected.Groups, group => Assert.True(string.IsNullOrEmpty(group.Title)));
        Assert.All(projected.Groups, group => Assert.Equal(2, group.Candidates.Count));
        var plan = Plan(WallDocument.Empty(1, 8), projected, items.Select(item => item.SourceKey));
        Assert.Equal(ImportPlanStatus.Ready, plan.Status);
        Assert.Equal(2, plan.Proposal!.Document.Lanes.SelectMany(lane => lane.Groups).Count());
    }

    [Fact]
    public void RecognitionOffPreservesSingleAndUnnamedSourceGroups()
    {
        var projected = ImportSourcePlanner.Project(WallDocument.Empty(1, 8),
            [Item("named", "Adobe Photoshop", new("named", "Creative", 0), "photoshop"),
             Item("unnamed", "Only", new("unnamed", "", 1)), Item("root", "Beta")], recognizeGroups: false);
        Assert.Equal(["B", "Creative", ""], projected.Groups.Select(group => group.Title));
    }

    [Fact]
    public void FilteringPrecedesGroupCountsAndMissingHasPriorityOverPurpose()
    {
        var old = Tile("old"); var current = WallDocument.Empty(1, 8) with { Lanes = [new() { Groups = [Group("tools", "Tools", old)] }] };
        var source = new ImportSourceGroup("source", "Tools", 0);
        var duplicate = Item("duplicate", "Alpha", source, "same") with { TargetAvailability = ImportTargetAvailability.Missing, Exclusion = StartMenuExclusion.Uninstall };
        var missing = Item("missing", "Beta", source, "missing") with { TargetAvailability = ImportTargetAvailability.Missing };
        var unknown = Item("unknown", "Gamma", source, "unknown") with { TargetAvailability = ImportTargetAvailability.Unknown };
        var identities = new Dictionary<string, string> { [old.Id] = duplicate.Identity!.Key };
        var filtered = ImportSourcePlanner.Project(current, [duplicate, missing, unknown], existingIdentityKeys: identities);
        Assert.Equal("G", Assert.Single(filtered.Groups).Title);
        Assert.Equal(2, filtered.FilteredCount);
        Assert.Equal(0, filtered.FilterCounts[ImportFilterReason.NonPrimaryEntry]);
        Assert.Equal(2, filtered.FilterCounts[ImportFilterReason.MissingTarget]);
        Assert.Equal("unknown", Assert.Single(filtered.Candidates, candidate => candidate.CanAdd).SelectionKey);
        var shown = ImportSourcePlanner.Project(current, [duplicate, missing, unknown], autoFilter: false, existingIdentityKeys: identities);
        Assert.Equal("Tools", Assert.Single(shown.Groups).Title);
        Assert.Equal(0, shown.FilteredCount); Assert.All(shown.FilterCounts.Values, count => Assert.Equal(0, count));
        Assert.All(shown.Candidates, candidate => Assert.True(candidate.CanAdd));
    }

    [Fact]
    public void DestinationObservationMarksOverwriteWithoutFilteringOrChangingSelection()
    {
        var existing = Tile("existing");
        var wall = WallDocument.Empty(1, 8) with
        {
            Lanes = [new() { Groups = [Group("tools", "Tools", existing), Group("other", "Other", Tile("unrelated"))] }],
        };
        var tools = new ImportSourceGroup("incoming-tools", "Tools", 0);
        var duplicate = Item("duplicate", "Alpha", tools, "same");
        var fresh = Item("fresh", "Beta", tools, "new", order: 1);
        var elsewhere = Item("elsewhere", "Gamma", new("incoming-other", "Other", 1), "same");
        var candidates = new[] { duplicate, fresh, elsewhere };
        var initial = ImportSourcePlanner.Project(wall, candidates, recognizeGroups: false, autoFilter: false);
        var selected = new Dictionary<string, string>
        {
            [duplicate.SourceKey] = duplicate.SelectionIdentity,
            [fresh.SourceKey] = fresh.SelectionIdentity,
        };

        var observed = ImportSourcePlanner.Project(wall, candidates, recognizeGroups: false, autoFilter: false,
            existingIdentityKeys: new Dictionary<string, string> { [existing.Id] = duplicate.Identity!.Key });
        Assert.Equal(initial.Groups.Select(group => group.Key), observed.Groups.Select(group => group.Key));
        Assert.Equal(["tools", "other"], observed.Groups.Select(group => group.DestinationGroupId));
        Assert.Equal(ImportFilterReason.None,
            observed.Candidates.Single(candidate => candidate.SelectionKey == duplicate.SourceKey).FilterReason);
        Assert.Equal(existing.Id, observed.Candidates.Single(candidate => candidate.SelectionKey == duplicate.SourceKey).ReplacedMemberId);
        Assert.True(observed.Candidates.Single(candidate => candidate.SelectionKey == elsewhere.SourceKey).CanAdd);
        var migrated = ImportSourcePlanner.MigrateSelection(selected, observed);
        Assert.Equal(2, migrated.Selected.Count);
        Assert.Equal(0, migrated.RemovedCount);
        var plan = Plan(wall, observed, migrated.Selected.Keys);
        Assert.Equal(2, plan.Accepted.Count);
        Assert.All(plan.Accepted, candidate => Assert.Equal("tools", candidate.GroupId));
    }

    [Fact]
    public void SameIdentityInDifferentDestinationGroupsIsLegalAndDecorationIsNeverIdentityGuessed()
    {
        var first = new ImportSourceGroup("first", "First", 0); var second = new ImportSourceGroup("second", "Second", 1);
        var projected = ImportSourcePlanner.Project(WallDocument.Empty(1, 8),
            [Item("a", "App", first, "same"), Item("b", "App", second, "same"), Item("decor-one", "Same", first), Item("decor-two", "Same", first)],
            recognizeGroups: false, autoFilter: false);
        Assert.Equal(4, projected.Candidates.Count);
        var plan = Plan(WallDocument.Empty(1, 8), projected, projected.Candidates.Select(candidate => candidate.SelectionKey));
        Assert.Equal(4, plan.AddedCount); Assert.Equal(0, plan.OverwrittenCount);
    }

    [Fact]
    public void RefreshKeepsOnlyCurrentVisibleIdentityAndNeverSelectsNewOrResurrectedRows()
    {
        var a = Item("a", "Alpha", identity: "alpha"); var b = Item("b", "Beta", identity: "beta");
        var previous = new Dictionary<string, string> { [a.SourceKey] = a.SelectionIdentity, [b.SourceKey] = b.SelectionIdentity };
        var projection = ImportSourcePlanner.Project(WallDocument.Empty(1, 8),
            [a, b with { TargetAvailability = ImportTargetAvailability.Missing }, Item("new", "New", identity: "new")]);
        var migrated = ImportSourcePlanner.MigrateSelection(previous, projection);
        Assert.Equal("a", Assert.Single(migrated.Selected).Key); Assert.Equal(1, migrated.RemovedCount);
        var unfiltered = ImportSourcePlanner.Project(WallDocument.Empty(1, 8), [a, b], autoFilter: false);
        Assert.Equal("a", Assert.Single(ImportSourcePlanner.MigrateSelection(migrated.Selected, unfiltered).Selected).Key);
        var changed = ImportSourcePlanner.Project(WallDocument.Empty(1, 8), [a with { Identity = b.Identity }]);
        Assert.Empty(ImportSourcePlanner.MigrateSelection(migrated.Selected, changed).Selected);
        Assert.Empty(ImportSourcePlanner.MigrateSelection(new Dictionary<string, string>(), unfiltered).Selected);
    }

    [Fact]
    public void MergeAndSplitKeepSelectedSourceProvenanceInsteadOfTransferringItToRepresentative()
    {
        var wall = WallDocument.Empty(1, 8);
        var one = Item("one", "Alpha", new("source-one", "One", 0), "same");
        var two = Item("two", "Alpha", new("source-two", "Two", 1), "same");
        var separate = ImportSourcePlanner.Project(wall, [one, two], recognizeGroups: false, autoFilter: false);
        var merged = ImportSourcePlanner.Project(wall, [one, two], recognizeGroups: true, autoFilter: false);
        Assert.Single(merged.Candidates);
        Assert.Equal("one", merged.Candidates[0].SelectionKey);
        var selected = new Dictionary<string, string> { ["two"] = two.SelectionIdentity };
        var joined = ImportSourcePlanner.MigrateSelection(selected, merged);
        Assert.Equal("two", Assert.Single(joined.Selected).Key);
        var split = ImportSourcePlanner.MigrateSelection(joined.Selected, separate);
        Assert.Equal("two", Assert.Single(split.Selected).Key);
        var plan = Plan(wall, separate, split.Selected.Keys);
        Assert.Equal("two", Assert.Single(plan.Accepted).SourceKey);
        Assert.Equal("Two", Destination(plan).Title);

        var both = ImportSourcePlanner.MigrateSelection(new Dictionary<string, string>
            { ["one"] = one.SelectionIdentity, ["two"] = two.SelectionIdentity }, merged);
        Assert.Equal(2, both.Selected.Count);
        Assert.Equal(0, both.RemovedCount);
        Assert.Equal(2, ImportSourcePlanner.MigrateSelection(both.Selected, separate).Selected.Count);
        Assert.Single(Plan(wall, merged, both.Selected.Keys).Accepted);
    }

    [Fact]
    public void SelectionUsesOrdinalSourceKeysAndIdentityInsteadOfSharedDisplayName()
    {
        var wall = WallDocument.Empty(1, 8);
        var one = Item("path-a", "Same name", identity: "one");
        var two = Item("path-b", "Same name", identity: "two");
        var projected = ImportSourcePlanner.Project(wall, [one, two]);
        var selected = new Dictionary<string, string> { ["path-b"] = two.SelectionIdentity };
        var migration = ImportSourcePlanner.MigrateSelection(selected, projected);
        Assert.Equal("path-b", Assert.Single(migration.Selected).Key);
        var wrongCase = new Dictionary<string, string> { ["PATH-B"] = two.SelectionIdentity };
        var removed = ImportSourcePlanner.MigrateSelection(wrongCase, projected);
        Assert.Empty(removed.Selected);
        Assert.Equal(1, removed.RemovedCount);
    }

    [Fact]
    public void FilteringOffDoesNotUnlockInvalidSourcesAndInvalidRecordedGeometryNeverUsesDefaults()
    {
        var invalid = Item("invalid", "Invalid", bounds: new(0, 0, 9, 1), size: true);
        var unreadable = Item("unreadable", "Unreadable") with { ErrorCode = "ReadFailed", Exclusion = StartMenuExclusion.Uninstall };
        var unresolved = Item("unresolved", "Unresolved") with { RequiresIdentity = true };
        var wall = WallDocument.Empty(1, 8);
        var projection = ImportSourcePlanner.Project(wall, [invalid, unreadable, unresolved], autoFilter: false);
        Assert.Equal(3, projection.InvalidCount); Assert.All(projection.Candidates, candidate => Assert.False(candidate.CanAdd));
        Assert.Equal(ImportPlanStatus.Rejected, Plan(wall, projection, [invalid.SourceKey]).Status);
        Assert.Equal("MEMBER_SIZE_LIMIT", Plan(wall, projection, [invalid.SourceKey]).Detail);
        var filtered = ImportSourcePlanner.Project(wall, [invalid, unreadable, unresolved]);
        Assert.All(filtered.Candidates, candidate => Assert.False(candidate.Visible));
        Assert.Equal(3, filtered.FilteredCount);
        Assert.Equal(3, filtered.FilterCounts[ImportFilterReason.NonPrimaryEntry]);
        var attributed = ImportSourcePlanner.Project(wall, [unreadable with { Policy = ImportSourcePolicy.Attributed }]);
        Assert.False(Assert.Single(attributed.Candidates).CanAdd);
    }

    [Fact]
    public void UnavailablePreviewRetainsAutomaticCaptionWithoutAcceptingTheSource()
    {
        var source = Item("stale", "Everything") with
        {
            Member = new TileMember { Id = "stale", LocalBounds = new(0, 0, 1, 1), TargetInput = @"C:\Tools\Everything.lnk" },
            ErrorCode = "SourceChanged",
        };
        var projection = ImportSourcePlanner.Project(WallDocument.Empty(1, 8), [source], autoFilter: false);
        var preview = ImportSourcePlanner.BuildSourcePreview(projection, new(1, 1), singleColumn: true);
        var member = Assert.Single(Assert.Single(Assert.Single(preview.Lanes).Groups).Members);
        Assert.Equal(source.Member.TargetInput, member.TargetInput);
        Assert.Equal("Everything", MemberPresentationRules.ResolveTitle(member));
        Assert.False(Assert.Single(projection.Candidates).CanAdd);
    }

    [Fact]
    public void RecordedSizeAndSparsePositionSurviveDefaultsWhileMissingRecordsUseDefaults()
    {
        var group = new ImportSourceGroup("source", "Tools", 0);
        var recorded = Item("recorded", "Recorded", group, bounds: new(4, 5, 2, 3), position: true, size: true, order: 0);
        var missing = Item("missing", "Missing", group, bounds: new(99, 99, 99, 99), order: 1);
        var wall = WallDocument.Empty(1, 4);
        var projection = ImportSourcePlanner.Project(wall, [recorded, missing], recognizeGroups: false);
        var small = Plan(wall, projection, ["recorded", "missing"]);
        var large = Plan(wall, projection, ["recorded", "missing"], new(2, 2));
        Assert.Equal(ImportPlanStatus.Ready, small.Status); Assert.Equal(ImportPlanStatus.Ready, large.Status);
        var recordedId = large.Accepted.Single(item => item.SourceKey == "recorded").MemberId;
        var missingId = large.Accepted.Single(item => item.SourceKey == "missing").MemberId;
        Assert.Equal(new GridRect(4, 5, 2, 3), Destination(large).Members.Single(member => member.Id == recordedId).LocalBounds);
        Assert.Equal(new GridRect(0, 0, 2, 2), Destination(large).Members.Single(member => member.Id == missingId).LocalBounds);
        Assert.Equal(new GridSize(1, 1), Destination(small).Members.Single(member => member.Id == missingId).LocalBounds.Size);
        Assert.Equal(small.Accepted.Select(item => item.MemberId), large.Accepted.Select(item => item.MemberId));
    }

    [Fact]
    public void ExistingGroupUsesPreImportMaximumBottomIncludingOverwrittenSlot()
    {
        var overwritten = Tile("old", new(3, 6, 2, 2)) with { Visual = new() { TitleMode = MemberTitleMode.Manual, Title = "Old" } };
        var retained = Tile("retained", new(0, 0, 1, 1));
        var other = Group("other", "Other", Tile("other-member"));
        var existing = Group("tools", "Tools", overwritten, retained);
        var wall = WallDocument.Empty(1, 16) with { Generation = 4, Lanes = [new() { Groups = [other, existing] }] };
        var source = new ImportSourceGroup("source", "Tools", 0);
        var replacement = Item("replacement", "Replacement", source, "same", new(2, 2, 3, 1), true, true) with
            { Member = Tile("untrusted", new(2, 2, 3, 1)) with { Visual = new() { TitleMode = MemberTitleMode.Manual, Title = "Imported" } } };
        var added = Item("added", "Added", source, "new", new(6, 0, 1, 2), true, true, 1);
        var projection = ImportSourcePlanner.Project(wall, [replacement, added], autoFilter: false,
            existingIdentityKeys: new Dictionary<string, string> { [overwritten.Id] = replacement.Identity!.Key });
        var before = WallJson.Serialize(wall);
        var plan = Plan(wall, projection, ["replacement", "added"]);
        Assert.Equal(ImportPlanStatus.Ready, plan.Status); Assert.Equal(1, plan.AddedCount); Assert.Equal(1, plan.OverwrittenCount);
        var destination = Destination(plan, "Tools");
        Assert.Equal("tools", destination.Id);
        Assert.Same(retained, destination.Members.Single(member => member.Id == "retained"));
        var replaced = destination.Members.Single(member => member.Id == "old");
        Assert.Equal(new GridRect(2, 10, 3, 1), replaced.LocalBounds); Assert.Equal("Imported", replaced.Visual.Title);
        Assert.Equal(new GridRect(6, 8, 1, 2), destination.Members.Single(member => member.Id != "old" && member.Id != "retained").LocalBounds);
        Assert.Equal(["tools", "other"], plan.Proposal!.Document.Lanes.SelectMany(lane => lane.Groups).Select(group => group.Id));
        Assert.Equal(before, WallJson.Serialize(wall));
    }

    [Fact]
    public void SelectedOverwriteAdoptsImportedTypePartitionsAppearanceAndSourceWhileRetainingLocalGroupIdentity()
    {
        var old = Tile("old") with { Entry = new("old", "Objects/old/entry.lnk", ManagedEntryKind.Lnk) };
        var current = WallDocument.Empty(1, 8) with { Lanes = [new() { Groups = [Group("local-group", "Tools", old)] }] };
        var puzzle = new PuzzleMember
        {
            Id = "source-puzzle", LocalBounds = new(2, 1, 4, 4), Partitions = [new(0, 0, 4, 4)],
            Entry = new("source-puzzle", "Objects/source-puzzle/entry.lnk", ManagedEntryKind.Lnk),
            Visual = new() { TitleMode = MemberTitleMode.Manual, Title = "Imported puzzle", BackgroundMode = MemberBackgroundMode.Color,
                BackgroundColor = "#112233", ShowTitle = false },
            Images = new() { Kind = MemberImageSourceKind.Folder, Path = @"C:\Images" },
            RotationInterval = ImageRotationInterval.Seconds15,
            ImportSource = new() { BatchId = "incoming", GroupBucketKey = "source", OriginGroupId = "source-group",
                SourceRoot = @"C:\Programs", RelativePath = @"Tools\app.lnk" },
        };
        var candidate = Item("source", "Imported puzzle", new("source-group", " tools ", 0), "same", position: true, size: true)
            with { Member = puzzle };
        var projection = ImportSourcePlanner.Project(current, [candidate], recognizeGroups: false, autoFilter: false,
            existingIdentityKeys: new Dictionary<string, string> { [old.Id] = candidate.Identity!.Key });
        var plan = Plan(current, projection, ["source"]);
        Assert.Equal(ImportPlanStatus.Ready, plan.Status);
        var group = Destination(plan);
        var adopted = Assert.IsType<PuzzleMember>(Assert.Single(group.Members));
        Assert.Equal("local-group", group.Id);
        Assert.Equal("Tools", group.Title);
        Assert.Equal("old", adopted.Id);
        Assert.Equal(new GridRect(2, 2, 4, 4), adopted.LocalBounds);
        Assert.Equal(puzzle.Partitions, adopted.Partitions);
        Assert.Equal(puzzle.Visual, adopted.Visual);
        Assert.Equal(puzzle.Images, adopted.Images);
        Assert.Equal(puzzle.RotationInterval, adopted.RotationInterval);
        Assert.Equal("old", adopted.Entry!.ObjectId);
        Assert.Equal("incoming", adopted.ImportSource!.BatchId);
        Assert.Equal("local-group", adopted.ImportSource.OriginGroupId);
        Assert.Equal(@"Tools\app.lnk", adopted.ImportSource.RelativePath);
    }

    [Fact]
    public void RenamedGroupAndInactiveMembersAreNotClaimedByOldImportProvenance()
    {
        var old = Tile("old-member") with { ImportSource = new() { BatchId = "old", GroupBucketKey = "letter:B",
            OriginGroupId = "old-b", SourceRoot = @"C:\Programs", RelativePath = "B.lnk" } };
        var renamed = Group("old-b", "Bb", old) with { UserClaimed = true };
        var inactive = Tile("inactive");
        var wall = WallDocument.Empty(1, 8) with { Lanes = [new() { Groups = [renamed] }], RecycleBinEnabled = true, InactiveMembers = [inactive] };
        var candidate = Item("new", "Beta", identity: "same");
        var projected = ImportSourcePlanner.Project(wall, [candidate], existingIdentityKeys: new Dictionary<string, string>
            { [old.Id] = candidate.Identity!.Key, [inactive.Id] = candidate.Identity!.Key });
        Assert.Null(Assert.Single(projected.Groups).DestinationGroupId);
        var plan = Plan(wall, projected, ["new"]);
        Assert.Equal(1, plan.AddedCount);
        Assert.Equal(0, plan.OverwrittenCount);
        var groups = plan.Proposal!.Document.Lanes.SelectMany(lane => lane.Groups).ToArray();
        Assert.Equal(["B", "Bb"], groups.Select(group => group.Title));
        Assert.Same(renamed, groups[1]);
        Assert.Same(inactive, Assert.Single(plan.Proposal.Document.InactiveMembers));
    }

    [Fact]
    public void UnselectedDuplicatePreservesOldContentAndNoSelectionDoesNotMoveGroups()
    {
        var old = Tile("old", new(4, 3, 1, 1)); var existing = Group("tools", "Tools", old);
        var wall = WallDocument.Empty(1, 8) with { Lanes = [new() { Groups = [Group("first", "First", Tile("first-member")), existing] }] };
        var source = new ImportSourceGroup("source", "Tools", 0);
        var duplicate = Item("duplicate", "Duplicate", source, "same"); var added = Item("new", "New", source, "new");
        var projection = ImportSourcePlanner.Project(wall, [duplicate, added], autoFilter: false,
            existingIdentityKeys: new Dictionary<string, string> { [old.Id] = duplicate.Identity!.Key });
        var empty = Plan(wall, projection, []);
        Assert.Equal(ImportPlanStatus.NoChange, empty.Status); Assert.Null(empty.Proposal);
        var plan = Plan(wall, projection, ["new"]);
        Assert.Equal(0, plan.OverwrittenCount); Assert.Same(old, Destination(plan, "Tools").Members.Single(member => member.Id == "old"));
        var newMemberId = Assert.Single(plan.Accepted).MemberId;
        Assert.Equal(4, Destination(plan, "Tools").Members.Single(member => member.Id == newMemberId).LocalBounds.Row);
    }

    [Fact]
    public void MultipleSourceBlocksWithSameNameContinueBelowPreviousBlockAndPreserveEachInternalGap()
    {
        var wall = WallDocument.Empty(1, 4);
        var one = Item("one", "One", new("source-one", "Same", 0), bounds: new(5, 2, 1, 1), position: true, size: true);
        var two = Item("two", "Two", new("source-two", " same ", 1), bounds: new(1, 4, 2, 2), position: true, size: true);
        var projection = ImportSourcePlanner.Project(wall, [two, one], recognizeGroups: false);
        var plan = Plan(wall, projection, ["one", "two"]);
        Assert.Equal(ImportPlanStatus.Ready, plan.Status); Assert.Single(projection.Groups);
        var accepted = plan.Accepted.ToDictionary(item => item.SourceKey, item => item.MemberId);
        Assert.Equal(new GridRect(5, 2, 1, 1), Destination(plan).Members.Single(member => member.Id == accepted["one"]).LocalBounds);
        Assert.Equal(new GridRect(1, 7, 2, 2), Destination(plan).Members.Single(member => member.Id == accepted["two"]).LocalBounds);
    }

    [Fact]
    public void UnrecordedItemsUseDisplayNameOrderAndSequentialLayoutFromExistingBottom()
    {
        var wall = WallDocument.Empty(1, 8) with { Lanes = [new() { Groups = [Group("tools", "Tools", Tile("old", new(7, 2, 1, 1)))] }] };
        var group = new ImportSourceGroup("source", "Tools", 0);
        var items = new[] { Item("three", "Three", group, order: 2), Item("one", "One", group, order: 0), Item("two", "Two", group, order: 1) };
        var projection = ImportSourcePlanner.Project(wall, items, recognizeGroups: false);
        Assert.Equal(["One", "Three", "Two"], Assert.Single(projection.Groups).Candidates.Select(candidate => candidate.Source.DisplayName));
        var plan = Plan(wall, projection, ["one", "two", "three"], new(4, 2));
        var ids = plan.Accepted.ToDictionary(item => item.SourceKey, item => item.MemberId);
        var members = Destination(plan).Members.ToDictionary(member => member.Id);
        Assert.Equal(new GridRect(0, 3, 4, 2), members[ids["one"]].LocalBounds);
        Assert.Equal(new GridRect(4, 3, 4, 2), members[ids["three"]].LocalBounds);
        Assert.Equal(new GridRect(0, 5, 4, 2), members[ids["two"]].LocalBounds);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void UnrecordedItemsFromDifferentFoldersFillFinalLetterGroupContinuouslyInPreviewAndPlan(int size)
    {
        var wall = WallDocument.Empty(1, 8) with
            { Lanes = [new() { Groups = [Group("existing-b", "B", Tile("old", new(7, 2, 1, 1)))] }] };
        var items = Enumerable.Range(0, 10).Reverse().Select(index => Item("item" + index, $"B entry {index:D2}",
            new("folder" + index, "Original folder " + index, index), "app" + index, order: 9 - index)).ToArray();
        var projection = ImportSourcePlanner.Project(wall, items);
        var group = Assert.Single(projection.Groups);
        Assert.Equal("B", group.Title);
        Assert.Equal(Enumerable.Range(0, 10).Select(index => "item" + index), group.Candidates.Select(candidate => candidate.Source.SourceKey));

        var preview = ImportSourcePlanner.BuildSourcePreview(projection, new(size, size));
        Assert.Null(preview.ErrorCode);
        var previewGroup = Assert.Single(preview.Lanes.SelectMany(lane => lane.Groups));
        var plan = Plan(wall, projection, items.Select(item => item.SourceKey), new(size, size));
        Assert.Equal(ImportPlanStatus.Ready, plan.Status);
        Assert.Equal(10, plan.AddedCount);
        var destination = Destination(plan, "B");
        Assert.Equal(new GridRect(7, 2, 1, 1), destination.Members.Single(member => member.Id == "old").LocalBounds);
        var acceptedIds = plan.Accepted.ToDictionary(item => item.SourceKey, item => item.MemberId);
        var perRow = 8 / size;
        for (var index = 0; index < 10; index++)
        {
            var expected = new GridRect(index % perRow * size, index / perRow * size, size, size);
            var key = "item" + index;
            var previewMember = Assert.Single(previewGroup.Members,
                member => preview.CandidatesByMemberId[member.Id].Source.SourceKey == key);
            Assert.Equal(expected, previewMember.LocalBounds);
            Assert.Equal(expected.Translate(0, 3), destination.Members.Single(member => member.Id == acceptedIds[key]).LocalBounds);
        }
    }

    [Fact]
    public void IdAllocationAvoidsInactiveAndGroupIdsAndMapsEntriesToAcceptedId()
    {
        var item = Item("source", "Source", new("source-group", "Source", 0), "app") with
            { Member = Tile("source-id") with { Entry = new("source-id", "Objects/source-id/entry.lnk", ManagedEntryKind.Lnk) } };
        var empty = WallDocument.Empty(1, 8);
        var initial = Plan(empty, ImportSourcePlanner.Project(empty, [item], recognizeGroups: false), ["source"]);
        var collision = Assert.Single(initial.Accepted).MemberId;
        var wall = empty with { RecycleBinEnabled = true, InactiveMembers = [Tile(collision)] };
        var plan = Plan(wall, ImportSourcePlanner.Project(wall, [item], recognizeGroups: false), ["source"]);
        var member = Assert.Single(Destination(plan).Members);
        Assert.NotEqual(collision, member.Id); Assert.NotEqual("source-id", member.Id);
        Assert.Equal(member.Id, member.Entry!.ObjectId); Assert.Equal($"Objects/{member.Id}/entry.lnk", member.Entry.RelativePath);
    }

    [Fact]
    public void EmptyFilteredAndStaleRequestsHaveExplicitResultsWithoutPartialProposal()
    {
        var wall = WallDocument.Empty(1, 8);
        var item = Item("missing", "Missing", identity: "missing") with { TargetAvailability = ImportTargetAvailability.Missing };
        var projection = ImportSourcePlanner.Project(wall, [item]);
        Assert.Equal(ImportPlanStatus.NoChange, Plan(wall, projection, ["missing"]).Status);
        Assert.Equal(ImportPlanStatus.NoChange, Plan(wall, ImportSourcePlanner.Project(wall, []), []).Status);
        var stale = ImportSourcePlanner.TryPlan(wall with { Generation = 1 }, 1, projection, ["missing"], new(1, 1));
        Assert.Equal(ImportPlanStatus.Rejected, stale.Status); Assert.Equal(WallRejection.StaleGeneration, stale.Rejection); Assert.Null(stale.Proposal);
        Assert.Equal(ImportPlanStatus.Rejected, Plan(wall, projection, ["unknown-source"]).Status);
    }

    [Fact]
    public void RecordedOverlapsAndExcessiveSparseHeightRejectWholeSelection()
    {
        var wall = WallDocument.Empty(1, 8); var source = new ImportSourceGroup("source", "Source", 0);
        var one = Item("one", "One", source, bounds: new(0, 0, 2, 2), position: true, size: true);
        var two = Item("two", "Two", source, bounds: new(1, 1, 2, 2), position: true, size: true);
        var overlap = Plan(wall, ImportSourcePlanner.Project(wall, [one, two]), ["one", "two"]);
        Assert.Equal(ImportPlanStatus.Rejected, overlap.Status); Assert.Null(overlap.Proposal); Assert.Empty(overlap.Accepted);
        var tall = one with { Member = one.Member with { LocalBounds = new(0, 30, 2, 2) } };
        var capacity = Plan(wall, ImportSourcePlanner.Project(wall, [tall], recognizeGroups: false), ["one"]);
        Assert.Equal(ImportPlanStatus.Rejected, capacity.Status); Assert.StartsWith("GROUP_TOO_TALL:", capacity.Detail); Assert.Null(capacity.Proposal);
    }

    [Fact]
    public void PurposeFilteringPrecedesFolderAndFamilyCounts()
    {
        var folder = new ImportSourceGroup("tools", "Tools", 0);
        var alpha = Item("alpha", "Alpha", folder, "alpha");
        var uninstall = Item("uninstall", "Uninstall", folder, "remove") with { Exclusion = StartMenuExclusion.Uninstall };
        var result = ImportSourcePlanner.Project(WallDocument.Empty(1, 8), [alpha, uninstall]);
        Assert.Equal("A", Assert.Single(result.Groups).Title);
        Assert.Equal(1, result.FilterCounts[ImportFilterReason.NonPrimaryEntry]);
        var adobe = Item("photoshop", "Adobe Photoshop", new("creative", "Creative", 0), "photoshop");
        var updater = Item("update", "Adobe Creative Cloud", new("creative", "Creative", 0), "updater") with
            { Exclusion = StartMenuExclusion.Maintenance };
        Assert.Equal("A", Assert.Single(ImportSourcePlanner.Project(WallDocument.Empty(1, 8), [adobe, updater]).Groups).Title);
    }

    [Fact]
    public void SourceDuplicateFilteringIsGlobalButExistingWallOnlyMarksAnOverwrite()
    {
        var user = Item("user", "Alpha", new("one", "One", 0), "same") with { SourcePriority = 0 };
        var common = Item("common", "Beta", new("two", "Two", 1), "same") with { SourcePriority = 1 };
        var old = Tile("old");
        var wall = WallDocument.Empty(1, 8) with { Lanes = [new() { Groups = [Group("one", "One", old)] }] };
        var result = ImportSourcePlanner.Project(wall, [common, user], recognizeGroups: false,
            existingIdentityKeys: new Dictionary<string, string> { [old.Id] = user.Identity!.Key });
        var row = Assert.Single(result.Candidates, candidate => candidate.CanAdd);
        Assert.Equal("user", row.SelectionKey); Assert.Equal("old", row.ReplacedMemberId);
        Assert.Equal(["user", "common"], row.SourceKeys);
        Assert.Equal(1, result.FilterCounts[ImportFilterReason.DuplicateTarget]);
        var migrated = ImportSourcePlanner.MigrateSelection(new Dictionary<string, string> { ["common"] = common.SelectionIdentity }, result);
        Assert.Equal("common", Assert.Single(migrated.Selected).Key);
        Assert.Equal(1, Plan(wall, result, migrated.Selected.Keys).OverwrittenCount);
    }

    [Fact]
    public void DirectFilesAndSystemEntriesSkipPurposeButNotMissingOrDuplicateChecks()
    {
        var direct = Item("file", "Readme", identity: "readme") with
            { Policy = ImportSourcePolicy.DirectTarget, Exclusion = StartMenuExclusion.DocumentOrWeb };
        var system = Item("system", "Settings", identity: "settings") with
            { Policy = ImportSourcePolicy.SystemFeature, Exclusion = StartMenuExclusion.WindowsBuiltIn };
        var missing = direct with { SourceKey = "missing", Identity = LaunchIdentityService.ForPath(@"C:\missing.txt"), TargetAvailability = ImportTargetAvailability.Missing };
        var duplicate = system with { SourceKey = "duplicate", SourceOrder = 1 };
        var app = Item("app", "System app", identity: "app") with
            { Policy = ImportSourcePolicy.Application, Exclusion = StartMenuExclusion.WindowsBuiltIn };
        var invalid = direct with { SourceKey = "invalid", ErrorCode = "ReadFailed" };
        var result = ImportSourcePlanner.Project(WallDocument.Empty(1, 8), [direct, system, missing, duplicate, app, invalid]);
        Assert.Equal(2, result.Candidates.Count(candidate => candidate.CanAdd));
        Assert.False(result.Candidates.Single(candidate => candidate.SelectionKey == "invalid").Visible);
        Assert.Equal(1, result.InvalidCount);
        Assert.Equal(1, result.FilterCounts[ImportFilterReason.DuplicateTarget]);
        Assert.Equal(1, result.FilterCounts[ImportFilterReason.MissingTarget]);
        Assert.Equal(2, result.FilterCounts[ImportFilterReason.NonPrimaryEntry]);
    }

    [Fact]
    public void SystemFeatureCatalogGroupsKeepStableKeysAndSingletonsWhenRecognitionIsEnabled()
    {
        var appsEnglish = new ImportSourceGroup("system:settings:apps-media-games", "Settings: Apps, media, and gaming", 6);
        var appsChinese = appsEnglish with { Title = "设置：应用、媒体与游戏" };
        var oneEnglish = Item("adobe-one", "Adobe Photoshop", appsEnglish, "photoshop") with
            { Policy = ImportSourcePolicy.SystemFeature, SourceKind = ImportPageSourceKind.SystemFeatures };
        var twoEnglish = Item("adobe-two", "Adobe Illustrator", appsEnglish, "illustrator", order: 1) with
            { Policy = ImportSourcePolicy.SystemFeature, SourceKind = ImportPageSourceKind.SystemFeatures };
        var singletonEnglish = Item("single", "A single system item", new("system:control-panel:apps", "Control Panel: Apps", 7), "system-item") with
            { Policy = ImportSourcePolicy.SystemFeature, SourceKind = ImportPageSourceKind.SystemFeatures };

        var english = ImportSourcePlanner.Project(WallDocument.Empty(1, 8), [oneEnglish, twoEnglish, singletonEnglish],
            recognizeGroups: true);
        var chinese = ImportSourcePlanner.Project(WallDocument.Empty(1, 8),
            [oneEnglish with { SourceGroup = appsChinese }, twoEnglish with { SourceGroup = appsChinese }, singletonEnglish],
            recognizeGroups: true);

        Assert.Equal([appsEnglish.Key, "system:control-panel:apps"], english.Groups.Select(group => group.Key));
        Assert.Equal([appsEnglish.Key, "system:control-panel:apps"], chinese.Groups.Select(group => group.Key));
        Assert.Equal(appsEnglish.Title, english.Groups[0].Title);
        Assert.Equal(appsChinese.Title, chinese.Groups[0].Title);
        Assert.Equal(2, english.Groups[0].Candidates.Count);
        Assert.Single(english.Groups[1].Candidates);
    }

    [Fact]
    public void AttributedMembersIgnoreFiltersAndRecognitionAndRetainSourceLaneAndGeometry()
    {
        var first = Item("one", "Adobe Photoshop", new("original", "Original", 0) { LaneIndex = 1 }, "same",
            new(3, 5, 2, 2), position: true, size: true) with
            { Policy = ImportSourcePolicy.Attributed, TargetAvailability = ImportTargetAvailability.Missing, Exclusion = StartMenuExclusion.WindowsBuiltIn };
        var second = Item("two", "Adobe Illustrator", new("other", "Other", 1) { LaneIndex = 2 }, "same",
            new(6, 2, 1, 3), position: true, size: true) with { Policy = ImportSourcePolicy.Attributed };
        var projection = ImportSourcePlanner.Project(WallDocument.Empty(1, 8), [first, second]);
        Assert.Equal(0, projection.FilteredCount);
        Assert.Equal(["Original", "Other"], projection.Groups.Select(group => group.Title));
        var preview = ImportSourcePlanner.BuildSourcePreview(projection, new(1, 1));
        Assert.Null(preview.ErrorCode); Assert.Equal(3, preview.Lanes.Count); Assert.Empty(preview.Lanes[0].Groups);
        Assert.Equal("original", Assert.Single(preview.Lanes[1].Groups).Id);
        Assert.Equal(first.Member.LocalBounds, preview.Lanes[1].Groups[0].Members[0].LocalBounds);
        Assert.Equal(second.Member.LocalBounds, preview.Lanes[2].Groups[0].Members[0].LocalBounds);
    }

    [Fact]
    public void SingleColumnSourcePreviewStacksAttributedLanesWithoutChangingGroupGeometry()
    {
        var laneOne = new ImportSourceGroup("lane-one", "Lane One", 0) { LaneIndex = 1 };
        var laneZero = new ImportSourceGroup("lane-zero", "Lane Zero", 1) { LaneIndex = 0 };
        var items = new[]
        {
            Item("one-wide", "One wide", laneOne, bounds: new(3, 5, 2, 2), position: true, size: true, order: 0)
                with { Policy = ImportSourcePolicy.Attributed },
            Item("one-tall", "One tall", laneOne, bounds: new(0, 0, 1, 3), position: true, size: true, order: 1)
                with { Policy = ImportSourcePolicy.Attributed },
            Item("zero-wide", "Zero wide", laneZero, bounds: new(1, 2, 2, 1), position: true, size: true, order: 0)
                with { Policy = ImportSourcePolicy.Attributed },
            Item("zero-tall", "Zero tall", laneZero, bounds: new(5, 1, 1, 2), position: true, size: true, order: 1)
                with { Policy = ImportSourcePolicy.Attributed },
        };
        var projection = ImportSourcePlanner.Project(WallDocument.Empty(1, 8), items);
        Assert.Equal(["lane-one", "lane-zero"], projection.Groups.Select(group => group.SourceGroupId));

        var metrics = new WallMetrics();
        var normal = ImportSourcePlanner.BuildSourcePreview(projection, new(1, 1), metrics);
        var singleColumn = ImportSourcePlanner.BuildSourcePreview(projection, new(1, 1), metrics, singleColumn: true);

        Assert.Equal(2, normal.Lanes.Count);
        Assert.Single(singleColumn.Lanes);
        Assert.Equal(metrics.WallWidth(2), normal.Geometry.WallWidth);
        Assert.Equal(metrics.WallWidth(1), singleColumn.Geometry.WallWidth);
        Assert.Equal(["lane-zero", "lane-one"], singleColumn.Lanes[0].Groups.Select(group => group.Id));
        Assert.Equal(["lane-zero", "lane-one"], singleColumn.Geometry.Groups.Select(group => group.GroupId));
        Assert.All(singleColumn.Geometry.Groups, group => Assert.Equal(0, group.LaneIndex));
        Assert.Equal(singleColumn.Geometry.Groups[0].Bounds.Y + singleColumn.Geometry.Groups[0].Bounds.Height + metrics.GroupGap,
            singleColumn.Geometry.Groups[1].TitleBounds.Y, 6);

        var normalGeometry = normal.Geometry.Groups.ToDictionary(group => group.GroupId);
        var singleGeometry = singleColumn.Geometry.Groups.ToDictionary(group => group.GroupId);
        foreach (var groupId in new[] { "lane-zero", "lane-one" })
        {
            var original = normalGeometry[groupId];
            var stacked = singleGeometry[groupId];
            Assert.Equal(original.TitleBounds.Width, stacked.TitleBounds.Width);
            Assert.Equal(original.TitleBounds.Height, stacked.TitleBounds.Height);
            Assert.Equal(original.Bounds.Width, stacked.Bounds.Width);
            Assert.Equal(original.Bounds.Height, stacked.Bounds.Height);
            var originalMembers = original.Members.ToDictionary(member => member.MemberId);
            foreach (var member in stacked.Members)
            {
                var originalMember = originalMembers[member.MemberId];
                Assert.Equal(new DipRect(originalMember.Bounds.X - original.TitleBounds.X,
                        originalMember.Bounds.Y - original.TitleBounds.Y, originalMember.Bounds.Width, originalMember.Bounds.Height),
                    new DipRect(member.Bounds.X - stacked.TitleBounds.X, member.Bounds.Y - stacked.TitleBounds.Y,
                        member.Bounds.Width, member.Bounds.Height));
            }

            var originalGroup = normal.Lanes.SelectMany(lane => lane.Groups).Single(group => group.Id == groupId);
            var stackedGroup = singleColumn.Lanes[0].Groups.Single(group => group.Id == groupId);
            var originalBounds = originalGroup.Members.ToDictionary(member => member.Id, member => member.LocalBounds);
            Assert.Equal(originalBounds.Count, stackedGroup.Members.Count);
            foreach (var member in stackedGroup.Members)
                Assert.Equal(originalBounds[member.Id], member.LocalBounds);
        }
    }

    [Fact]
    public void StatisticsCountRawObjectsAndRequireExplicitNoTargetForComponents()
    {
        var file = Item("file", "Text") with { Policy = ImportSourcePolicy.DirectTarget, TargetKind = ImportTargetKind.File, HasLaunchTarget = true };
        var clock = Item("clock", "Clock") with { HasLaunchTarget = false,
            Member = new ClockMember { Id = "clock", LocalBounds = new(0, 0, 2, 2) } };
        var puzzle = Item("puzzle", "Puzzle") with { HasLaunchTarget = false,
            Member = new PuzzleMember { Id = "puzzle", LocalBounds = new(0, 0, 2, 2), Partitions = [new(0, 0, 1, 2), new(1, 0, 1, 2)] } };
        var invalid = Item("unknown", "Unknown") with { ErrorCode = "ReadFailed", TargetKind = ImportTargetKind.Component };
        var result = ImportSourcePlanner.Project(WallDocument.Empty(1, 8), [file, clock, puzzle, invalid]);
        Assert.Equal(4, result.Statistics.AcquiredCount);
        Assert.Equal(1, result.Statistics.ContentCounts[ImportTargetKind.File]);
        Assert.Equal(2, result.Statistics.ContentCounts[ImportTargetKind.Component]);
        Assert.Equal(3, result.Statistics.ContentCounts.Values.Sum());
        Assert.Equal(1, result.FilteredCount);
    }

    [Fact]
    public void PreviewAndFormalPlanSharePlacementButOnlyFormalPlanAppliesCapacity()
    {
        var source = new ImportSourceGroup("tools", "Tools", 0);
        var items = Enumerable.Range(0, 1100).Select(index => Item("key" + index, "Tool " + index, source, order: index)).ToArray();
        var wall = WallDocument.Empty(1, 8);
        var projection = ImportSourcePlanner.Project(wall, items, recognizeGroups: false);
        var preview = ImportSourcePlanner.BuildSourcePreview(projection, new(2, 2));
        Assert.Null(preview.ErrorCode); Assert.Equal(items.Length, preview.CandidatesByMemberId.Count);
        Assert.True(preview.Lanes[0].Groups[0].Members.Max(member => member.LocalBounds.Bottom) > 256);
        Assert.Equal(ImportPlanStatus.Rejected, Plan(wall, projection, items.Select(item => item.SourceKey), new(2, 2)).Status);

        var small = ImportSourcePlanner.Project(wall, items.Take(3).ToArray(), recognizeGroups: false);
        var smallPreview = ImportSourcePlanner.BuildSourcePreview(small, new(2, 2));
        var plan = Plan(wall, small, items.Take(3).Select(item => item.SourceKey), new(2, 2));
        Assert.Equal(smallPreview.Lanes[0].Groups[0].Members.Select(member => member.LocalBounds), Destination(plan).Members.Select(member => member.LocalBounds));
    }
}
