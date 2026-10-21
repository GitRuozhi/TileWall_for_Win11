using TileWall.Core.Grid;
using TileWall.Core.Import;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class StartMenuImportPlannerTests
{
    private static ImportSourceMetadata GroupSource(string id) => new()
    {
        BatchId = "batch",
        GroupBucketKey = id,
        OriginGroupId = id,
    };

    private static ImportSourceMetadata MemberSource(string groupId, string fileName) => new()
    {
        BatchId = "batch",
        GroupBucketKey = groupId,
        OriginGroupId = groupId,
        SourceRoot = "UserPrograms",
        RelativePath = fileName,
    };

    private static TileMember Tile(string id, int width = 1, int height = 1, string? groupId = null) => new()
    {
        Id = id,
        LocalBounds = new GridRect(0, 0, width, height),
        ImportSource = MemberSource(groupId ?? id, id + ".lnk"),
    };

    private static StartGroup Group(string id, params TileMember[] members) => new()
    {
        Id = id,
        Title = id,
        ImportSource = GroupSource(id),
        Members = members.Length == 0 ? [Tile(id + "-member", groupId: id)] : members,
    };

    private static StartGroup BucketGroup(string id, string title, string bucketKey, string batchId,
        params TileMember[] members) => new()
    {
        Id = id,
        Title = title,
        ImportSource = new ImportSourceMetadata
        {
            BatchId = batchId,
            GroupBucketKey = bucketKey,
            OriginGroupId = id,
        },
        Members = members.Length == 0 ? [Tile(id + "-member", groupId: id)] : members,
    };

    private static WallViewportBudget Budget(int columns, int rows, int? physicalRows = null)
    {
        var metrics = new WallMetrics();
        return new(metrics.WallWidth(columns), metrics.WallHeight(physicalRows ?? rows), columns, rows, false);
    }

    private static StartGroup TallGroup() => Group("tall",
        Tile("tall-a", 4, 3, "tall"), Tile("tall-b", 4, 3, "tall"), Tile("tall-c", 4, 3, "tall"));

    [Fact]
    public void RepackingKeepsCurrentHeightAndManualEmptyLaneCapacity()
    {
        var left = new WallLane { Groups = [] };
        var kept = Group("kept", Tile("kept-member", groupId: "kept") with { UserClaimed = true }) with { UserClaimed = true };
        var middle = new WallLane { Groups = [kept] };
        var right = new WallLane { Groups = [] };
        var current = WallDocument.Empty(3, 16) with { Generation = 5, Lanes = [left, middle, right] };
        var before = WallJson.Serialize(current);

        var result = StartMenuImportPlanner.TryPlan(current, 5,
            [Group("imported", Tile("imported-member", groupId: "imported"))], budget: Budget(4, 24));

        Assert.True(result.Success, result.Detail);
        var proposed = result.Proposal!.Document;
        Assert.Equal(16, proposed.Rows);
        Assert.Equal(3, proposed.Columns);
        Assert.Equal(new[] { "kept", "imported" }, proposed.Lanes.SelectMany(lane => lane.Groups).Select(group => group.Id));
        Assert.Same(kept, proposed.Lanes[0].Groups[0]);
        Assert.True(proposed.Lanes[0].Groups[0].UserClaimed);
        Assert.True(proposed.Lanes[0].Groups[0].Members[0].UserClaimed);
        Assert.All(proposed.Lanes.Skip(1), lane => Assert.Empty(lane.Groups));
        Assert.Equal(before, WallJson.Serialize(current));
    }

    [Fact]
    public void ImportCompactsIncomingMembersAndReflowsWithoutChangingUntouchedMemberGeometry()
    {
        var left = new WallLane { Groups = [] };
        var retainedMember = Tile("retained-member", groupId: "retained") with
            { LocalBounds = new GridRect(4, 1, 1, 1), UserClaimed = true };
        var retained = Group("retained", retainedMember) with { UserClaimed = true };
        var middle = new WallLane { Groups = [retained], ImportBatchId = "original-lane" };
        var right = new WallLane { Groups = [] };
        var current = WallDocument.Empty(3, 4) with { Generation = 8, Lanes = [left, middle, right] };
        var incomingA = Tile("incoming-a", groupId: "incoming") with { LocalBounds = new GridRect(1, 15, 1, 1) };
        var incomingB = Tile("incoming-b", groupId: "incoming") with { LocalBounds = new GridRect(7, 20, 1, 1) };
        var incoming = Group("incoming", incomingA, incomingB);
        var before = WallJson.Serialize(current);

        var result = StartMenuImportPlanner.TryPlan(current, 8, [incoming], budget: Budget(3, 24));

        Assert.True(result.Success, result.Detail);
        var proposed = result.Proposal!.Document;
        Assert.Equal(4, proposed.Rows);
        Assert.Equal(3, proposed.Columns);
        var proposedGroups = proposed.Lanes.SelectMany(lane => lane.Groups).ToArray();
        var retainedGroup = proposedGroups.Single(group => group.Id == "retained");
        Assert.Same(retained, retainedGroup);
        Assert.Same(retainedMember, retainedGroup.Members[0]);
        Assert.Equal(new GridRect(4, 1, 1, 1), retainedGroup.Members[0].LocalBounds);
        Assert.Equal("batch", proposed.Lanes.Single(lane => lane.Groups.Contains(retained)).ImportBatchId);
        var imported = proposedGroups.Single(group => group.Id == "incoming");
        Assert.Equal(new[] { "incoming-a", "incoming-b" }, imported.Members.Select(member => member.Id));
        Assert.All(imported.Members, member => Assert.Equal(0, member.LocalBounds.Row));
        Assert.Empty(proposed.Lanes[2].Groups);
        Assert.Same(incoming.ImportSource, imported.ImportSource);
        Assert.Same(incomingA.ImportSource, imported.Members[0].ImportSource);
        Assert.Equal(new GridRect(1, 15, 1, 1), incomingA.LocalBounds);
        Assert.Equal(new GridRect(7, 20, 1, 1), incomingB.LocalBounds);
        Assert.Equal(before, WallJson.Serialize(current));
    }

    [Fact]
    public void WholeTallGroupRaisesOnlyToSmallestHeightThatFits()
    {
        var current = WallDocument.Empty(1, 4);
        var before = WallJson.Serialize(current);
        var imported = TallGroup();

        var result = StartMenuImportPlanner.TryPlan(current, 0, [imported], budget: Budget(4, 24));

        Assert.True(result.Success, result.Detail);
        var proposed = result.Proposal!.Document;
        Assert.Equal(7, proposed.Rows); // Six occupied member rows plus the group's title row.
        var group = Assert.Single(proposed.Lanes[0].Groups);
        Assert.Equal(imported.Members.Select(member => member.Id), group.Members.Select(member => member.Id));
        Assert.Equal(6, group.Members.Max(member => member.LocalBounds.Bottom));
        Assert.Equal(before, WallJson.Serialize(current));
    }

    [Fact]
    public void ColumnPressureRaisesOnlyEnoughToStackCompleteGroupsWithoutMovingExistingContent()
    {
        var retained = BucketGroup("kept", "C", "letter:C", "old", Tile("kept-member", groupId: "kept"));
        var current = WallDocument.Empty(1, 2) with { Lanes = [new WallLane { Groups = [retained] }] };
        var groups = new[]
        {
            BucketGroup("one", "A", "letter:A", "old", Tile("one-member", groupId: "one")),
            BucketGroup("two", "B", "letter:B", "old", Tile("two-member", groupId: "two")),
        };
        var before = WallJson.Serialize(current);

        var result = StartMenuImportPlanner.TryPlan(current, 0, groups, budget: Budget(2, 24));

        Assert.True(result.Success, result.Detail);
        var proposed = result.Proposal!.Document;
        Assert.Equal(4, proposed.Rows);
        Assert.Equal(2, proposed.Columns);
        Assert.Equal(new[] { "one", "two" }, proposed.Lanes[0].Groups.Select(group => group.Id));
        Assert.Same(retained, proposed.Lanes[1].Groups[0]);
        Assert.Equal(before, WallJson.Serialize(current));
    }

    [Theory]
    [InlineData(6, 6)]
    [InlineData(24, 6)]
    public void MaximumRowOrPhysicalHeightLimitRejectsWholeGroupWithoutChangingInputs(int maxRows, int physicalRows)
    {
        var retained = Group("kept", Tile("kept-member", groupId: "kept") with { UserClaimed = true }) with { UserClaimed = true };
        var current = WallDocument.Empty(1, 4) with { Lanes = [new WallLane { Groups = [retained] }] };
        var imported = TallGroup();
        var before = WallJson.Serialize(current);
        var oldBounds = imported.Members.Select(member => member.LocalBounds).ToArray();

        var result = StartMenuImportPlanner.TryPlan(current, 0, [imported], budget: Budget(3, maxRows, physicalRows));

        Assert.Null(result.Proposal);
        Assert.Equal(WallRejection.NoSpace, result.Rejection);
        Assert.Equal("GROUP_TOO_TALL:tall", result.Detail);
        Assert.Equal(before, WallJson.Serialize(current));
        Assert.Equal(oldBounds, imported.Members.Select(member => member.LocalBounds));
        Assert.Same(retained, current.Lanes[0].Groups[0]);
    }

    [Fact]
    public void SmallerViewportCannotShrinkSavedHeightForImport()
    {
        var current = WallDocument.Empty(1, 16);
        var before = WallJson.Serialize(current);
        var result = StartMenuImportPlanner.TryPlan(current, 0,
            [Group("imported", Tile("item", groupId: "imported"))], budget: Budget(3, 12));
        Assert.Null(result.Proposal);
        Assert.Equal(WallRejection.WorkAreaTooSmall, result.Rejection);
        Assert.Equal(before, WallJson.Serialize(current));
    }

    [Fact]
    public void UnknownSourceGroupsStayCustomAndKeepTheirExistingRelativeOrder()
    {
        var oldFirst = Group("old-first", Tile("old-a"));
        var oldSecond = Group("old-second", Tile("old-b"));
        var current = new WallDocument
        {
            Columns = 2,
            Rows = 4,
            Generation = 17,
            Lanes =
            [
                new WallLane { Groups = [oldFirst] },
                new WallLane { Groups = [oldSecond] },
            ],
        };
        var imported = new[]
        {
            Group("menu-one", Tile("menu-a", 2, 2, "menu-one")),
            Group("menu-two", Tile("menu-b", 2, 2, "menu-two")),
        };

        var result = StartMenuImportPlanner.TryPlan(current, 17, imported);

        Assert.True(result.Success, result.Detail);
        var proposed = result.Proposal!.Document;
        Assert.Equal(3, proposed.Columns);
        Assert.Equal(4, proposed.Rows);
        Assert.Equal(18, proposed.Generation);
        Assert.Equal("batch", proposed.Lanes[0].ImportBatchId);
        Assert.Equal("batch", proposed.Lanes[1].ImportBatchId);
        Assert.Equal("batch", proposed.Lanes[2].ImportBatchId);
        Assert.Equal(new[] { "old-first", "old-second", "menu-one", "menu-two" },
            proposed.Lanes.SelectMany(lane => lane.Groups).Select(group => group.Id));
        Assert.Equal(new GridSize(2, 2), proposed.Lanes[1].Groups[0].Members[0].LocalBounds.Size);
        Assert.Same(oldFirst, proposed.Lanes[0].Groups[0]);
        Assert.Same(oldSecond, proposed.Lanes[0].Groups[1]);
        Assert.Same(oldFirst, current.Lanes[0].Groups[0]);
        Assert.Equal(17, current.Generation);
    }

    [Fact]
    public void FixedHeightPacksWholeGroupsWithoutAddingAnUnneededLane()
    {
        var oneLane = StartMenuImportPlanner.TryPlan(WallDocument.Empty(1, 2), 0,
            [Group("one", Tile("one-member"))]);
        Assert.True(oneLane.Success, oneLane.Detail);
        Assert.Equal(1, oneLane.Proposal!.Document.Columns);

        var groups = Enumerable.Range(1, 4)
            .Select(i => Group($"group-{i}", Tile($"member-{i}"))).ToArray();
        var tall = StartMenuImportPlanner.TryPlan(WallDocument.Empty(1, 2), 0, groups);

        Assert.True(tall.Success, tall.Detail);
        Assert.Equal(4, tall.Proposal!.Document.Columns);
        Assert.Equal(2, tall.Proposal.Document.Rows);
        Assert.Equal(new[] { "group-1", "group-2", "group-3", "group-4" },
            tall.Proposal.Document.Lanes.Take(4).SelectMany(lane => lane.Groups).Select(group => group.Id));
    }

    [Fact]
    public void ImportUsesTheCurrentSnapshotWithoutChangingExpectedGeneration()
    {
        var current = WallDocument.Empty(1, 2) with { Generation = 23 };
        var proposed = StartMenuImportPlanner.TryPlan(current, 23,
            [Group("imported", Tile("item"))]);
        Assert.True(proposed.Success, proposed.Detail);
        Assert.Equal(24, proposed.Proposal!.Document.Generation);
        Assert.Equal(23, current.Generation);

        var stale = StartMenuImportPlanner.TryPlan(current, 22,
            [Group("imported", Tile("item"))]);
        Assert.Equal(WallRejection.StaleGeneration, stale.Rejection);
        Assert.Null(stale.Proposal);
    }

    [Fact]
    public void EmptySelectionRejectsWithoutChangingTheWall()
    {
        var current = WallDocument.Empty(1, 8) with { Generation = 23,
            Lanes = [new WallLane { Groups = [Group("kept", Tile("kept-member"))] }] };
        var before = WallJson.Serialize(current);

        var result = StartMenuImportPlanner.TryPlan(current, current.Generation, []);

        Assert.Null(result.Proposal);
        Assert.Equal(WallRejection.InvalidAction, result.Rejection);
        Assert.Equal(before, WallJson.Serialize(current));
    }

    [Fact]
    public void ExistingDestinationIsRepackedAndUntouchedGroupIdentityRemains()
    {
        var old = Group("existing", Tile("old", groupId: "existing"));
        var untouched = Group("other", Tile("other-member", groupId: "other"));
        var current = WallDocument.Empty(2, 8) with { Generation = 3,
            Lanes = [new WallLane { Groups = [old] }, new WallLane { Groups = [untouched] }] };
        var incoming = old with { Members = [old.Members[0] with { LocalBounds = new(5, 4, 1, 1) },
            Tile("new", groupId: "existing")] };
        var result = StartMenuImportPlanner.TryPlan(current, 3, [incoming], budget: Budget(2, 8));
        Assert.True(result.Success, result.Detail);
        var proposed = result.Proposal!.Document;
        Assert.Equal(2, proposed.Columns); Assert.Equal(4, proposed.Generation);
        Assert.Equal(new[] { "existing", "other" }, proposed.Lanes.SelectMany(lane => lane.Groups).Select(group => group.Id));
        Assert.Equal("existing", proposed.Lanes[0].Groups[0].Id);
        Assert.Equal(new[] { "old", "new" }, proposed.Lanes[0].Groups[0].Members.Select(member => member.Id));
        Assert.All(proposed.Lanes[0].Groups[0].Members, member => Assert.Equal(0, member.LocalBounds.Row));
        Assert.Same(untouched, proposed.Lanes[0].Groups[1]);
    }

    [Fact]
    public void SortsTheWholeActiveWallByLetterFolderPresetThenStableCustomOrder()
    {
        var customFirst = BucketGroup("custom-first", "Custom One", "future:one", "old");
        var preset = BucketGroup("preset", "Microsoft", "preset:microsoft", "old");
        var letterD = BucketGroup("letter-d", "D", "letter:D", "old",
            Tile("d-lower", groupId: "letter:D") with { LocalBounds = new(5, 3, 1, 1) },
            Tile("d-upper", groupId: "letter:D") with { LocalBounds = new(1, 0, 2, 1) });
        var folderZulu = BucketGroup("folder-zulu", "Zulu", "folder:ZULU", "old");
        var letterA = BucketGroup("letter-a", "A", "letter:A", "old");
        var folderAlpha = BucketGroup("folder-alpha", "Alpha", "folder:ALPHA", "old");
        var customSecond = BucketGroup("custom-second", "Custom Two", "letter:Z", "old") with { UserClaimed = true };
        var claimedLetterB = BucketGroup("claimed-letter-b", "B", "letter:B", "old") with { UserClaimed = true };
        var letterHash = BucketGroup("letter-hash", "#", "letter:#", "old");
        var current = WallDocument.Empty(1, 24) with
        {
            Generation = 4,
            Lanes = [new WallLane
            {
                Groups = [customFirst, preset, letterD, folderZulu, letterA, folderAlpha,
                    customSecond, claimedLetterB, letterHash],
                ImportBatchId = "stale-lane-batch",
            }],
        };
        var incoming = BucketGroup("letter-c", "C", "letter:C", "new",
            Tile("c-member", groupId: "letter:C"));

        var result = StartMenuImportPlanner.TryPlan(current, 4, [incoming]);

        Assert.True(result.Success, result.Detail);
        var proposed = result.Proposal!.Document;
        var ordered = proposed.Lanes.SelectMany(lane => lane.Groups).ToArray();
        Assert.Equal(new[]
        {
            "letter-hash", "letter-a", "letter-c", "letter-d", "folder-alpha", "folder-zulu",
            "preset", "custom-first", "custom-second", "claimed-letter-b",
        }, ordered.Select(group => group.Id));
        Assert.Same(customFirst, ordered[7]);
        Assert.Same(customSecond, ordered[8]);
        Assert.Same(claimedLetterB, ordered[9]);
        Assert.Equal(new GridRect(5, 3, 1, 1), ordered[3].Members[0].LocalBounds);
        Assert.Equal(new GridRect(1, 0, 2, 1), ordered[3].Members[1].LocalBounds);
        Assert.Null(proposed.Lanes[0].ImportBatchId);
        Assert.Equal(5, proposed.Generation);
        Assert.Equal(10, ordered.Length);
    }

    [Fact]
    public void RenamedLetterGroupStaysCustomAndLaterImportCreatesTheOriginalLetterGroup()
    {
        var renamed = BucketGroup("old-b", "Bb", "letter:B", "old",
            Tile("old-b-member", groupId: "letter:B")) with { UserClaimed = true };
        var current = WallDocument.Empty(1, 8) with
        {
            Lanes = [new WallLane { Groups = [renamed] }],
        };
        var incoming = BucketGroup("new-b", "B", "letter:B", "new",
            Tile("new-b-member", groupId: "letter:B"));

        var result = StartMenuImportPlanner.TryPlan(current, current.Generation, [incoming]);

        Assert.True(result.Success, result.Detail);
        var ordered = result.Proposal!.Document.Lanes.SelectMany(lane => lane.Groups).ToArray();
        Assert.Equal(new[] { "new-b", "old-b" }, ordered.Select(group => group.Id));
        Assert.Equal(new[] { "B", "Bb" }, ordered.Select(group => group.Title));
        Assert.True(ordered[1].UserClaimed);
        Assert.Equal("old-b-member", ordered[1].Members[0].Id);
        Assert.Equal("new-b-member", ordered[0].Members[0].Id);
    }

    [Fact]
    public void TransientZeroLaneImportStillRejectsInvalidRemainingDocumentContracts()
    {
        var current = WallDocument.Empty(0, 0);
        var result = StartMenuImportPlanner.TryPlan(current, 0, [Group("imported", Tile("item"))]);
        Assert.Equal(WallRejection.InvalidDocument, result.Rejection);
        Assert.Null(result.Proposal);
    }

    [Fact]
    public void StructuralColumnLimitRejectsTheWholeImportWhenNoEmptyLaneCanBeReused()
    {
        var current = WallDocument.Empty(64, 2) with
        {
            Lanes = Enumerable.Range(0, 64).Select(index => new WallLane
            {
                Groups = [Group("old-" + index, Tile("old-member-" + index, groupId: "old-" + index))],
            }).ToArray(),
        };
        var result = StartMenuImportPlanner.TryPlan(current, current.Generation,
            [Group("imported", Tile("item"))]);

        Assert.Equal(WallRejection.NoSpace, result.Rejection);
        Assert.Null(result.Proposal);
        Assert.Equal(64, current.Columns);
        Assert.Equal(0, current.Generation);
    }
}

