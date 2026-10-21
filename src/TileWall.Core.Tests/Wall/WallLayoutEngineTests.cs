using System.Text.Json;
using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using TileWall.Core.Wall;
using TileWall.Core.Components;
using TileWall.Core.Storage;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class WallLayoutEngineTests
{
    [Fact]
    public void EveryOfferedClockFormatCanBeAddedAndReadBack()
    {
        foreach (var time in ClockFormatOptions.SupportedTimeFormats)
        foreach (var date in ClockFormatOptions.SupportedDateFormats)
        {
            var clock = Clock("clock", new GridRect(0, 0, 2, 1)) with { TimeFormat = time, DateFormat = date };
            var result = WallLayoutEngine.TryApply(WallDocument.Empty(1, 3),
                new AddMemberAction(0, clock, null, NewGroupId: "clock-group"), 1000, 1000);
            Assert.True(result.Success, result.Detail);
            var restored = WallJson.Deserialize(WallJson.Serialize(result.Proposal!.Document));
            Assert.Empty(WallLayoutEngine.Validate(restored));
            var savedClock = Assert.IsType<ClockMember>(Assert.Single(Assert.Single(restored.Lanes[0].Groups).Members));
            Assert.Equal(time, savedClock.TimeFormat);
            Assert.Equal(date, savedClock.DateFormat);
        }
    }

    [Theory]
    [InlineData("HH:mm:ss.fffffff", "yyyy/M/d")]
    [InlineData("HH:mm", "O")]
    [InlineData("", "yyyy/M/d")]
    [InlineData("HH:mm", "")]
    public void UnsupportedClockFormatIsRejectedForEditingAndPersistedDocuments(string time, string date)
    {
        var clock = Clock("clock", new GridRect(0, 0, 2, 1)) with { TimeFormat = time, DateFormat = date };
        var candidate = Document(Group("group", clock));
        Assert.Contains("CLOCK_FORMAT", WallLayoutEngine.Validate(candidate));
        Assert.Contains("CLOCK_FORMAT", WallLayoutEngine.Validate(WallJson.Deserialize(WallJson.Serialize(candidate))));
        Assert.False(WallLayoutEngine.TryApply(WallDocument.Empty(1, 3),
            new AddMemberAction(0, clock, null, NewGroupId: "clock-group"), 1000, 1000).Success);
    }

    private static TileMember Tile(string id, GridRect bounds) => new() { Id = id, LocalBounds = bounds };
    private static ClockMember Clock(string id, GridRect bounds) => new() { Id = id, LocalBounds = bounds };
    private static StartGroup Group(string id, params WallMember[] members) => new() { Id = id, Members = members };
    private static WallDocument Document(params StartGroup[] groups) => new()
    {
        Columns = 1,
        Rows = 3,
        Lanes = [new WallLane { Groups = groups }],
    };

    [Fact]
    public void EmptyAndDuplicateGroupsAreRejected()
    {
        Assert.Contains("EMPTY_GROUP", WallLayoutEngine.Validate(Document(Group("g"))));
        Assert.Contains("DUPLICATE_ID", WallLayoutEngine.Validate(Document(
            Group("g", Tile("same", new GridRect(0, 0, 1, 1))),
            Group("same", Tile("other", new GridRect(0, 0, 1, 1))))));
    }

    [Fact]
    public void MetricsAndGroupMeasureIncludeFullWidthTitleAndGroupGap()
    {
        var metrics = new WallMetrics();
        Assert.Equal(52, metrics.Pitch);
        Assert.Equal(412, metrics.LaneWidth);
        Assert.Equal(0, metrics.TopMargin);
        Assert.Equal(12, metrics.Margin);
        Assert.Equal(metrics.TopMargin + metrics.Margin + metrics.HeightOf(4), metrics.WallHeight(4));
        Assert.Equal(48, metrics.TitleHeight);
        Assert.Equal(4, metrics.TitleToMembersGap);
        var geometry = WallLayoutEngine.Measure(Document(
            Group("a", Tile("a1", new GridRect(0, 0, 1, 1))),
            Group("b", Tile("b1", new GridRect(0, 0, 1, 1)))) with { Rows = 4 }, metrics);
        Assert.Equal(412, geometry.Groups[0].TitleBounds.Width);
        Assert.Equal(48, geometry.Groups[0].TitleBounds.Height);
        Assert.Equal(geometry.Groups[0].TitleBounds.Y + 48 + 4,
            geometry.Groups[0].Members[0].Bounds.Y);
        Assert.Equal(geometry.Groups[0].Bounds.Y + geometry.Groups[0].Bounds.Height + metrics.GroupGap,
            geometry.Groups[1].Bounds.Y);
    }

    [Fact]
    public void MultipleLanesAndUnevenGroupsAlignWithinLanesRegardlessOfGridVisibility()
    {
        var metrics = new WallMetrics();
        var puzzle = new PuzzleMember
        {
            Id = "puzzle", LocalBounds = new GridRect(1, 1, 4, 4),
            Partitions = [new GridRect(0, 0, 2, 4), new GridRect(2, 0, 2, 4)],
        };
        var document = new WallDocument
        {
            Columns = 2, Rows = 9,
            Lanes =
            [
                new WallLane { Groups =
                [
                    Group("left-tall", Tile("small", new GridRect(0, 0, 1, 1)),
                        Tile("large", new GridRect(3, 2, 2, 2))),
                    Group("left-short", Tile("last", new GridRect(7, 0, 1, 1))),
                ] },
                new WallLane { Groups =
                [
                    Group("right-tall", puzzle),
                    Group("right-short", Clock("clock", new GridRect(0, 0, 2, 2))),
                ] },
            ],
        };

        var hidden = WallLayoutEngine.Measure(document);
        var shown = WallLayoutEngine.Measure(document with
        {
            Settings = document.Settings with { ShowGrid = true },
        });
        Assert.Equal(864, hidden.WallWidth);
        Assert.Equal(476, hidden.WallHeight);
        Assert.Equal(new DipRect(12, 0, 412, 48), hidden.Groups[0].TitleBounds);
        Assert.Equal(new DipRect(12, 260, 412, 48), hidden.Groups[1].TitleBounds);
        Assert.Equal(new DipRect(440, 0, 412, 48), hidden.Groups[2].TitleBounds);
        Assert.Equal(new DipRect(440, 312, 412, 48), hidden.Groups[3].TitleBounds);
        Assert.Equal(16, hidden.Groups[2].TitleBounds.X -
            (hidden.Groups[0].TitleBounds.X + hidden.Groups[0].TitleBounds.Width));
        Assert.Equal(hidden.WallWidth, shown.WallWidth);
        Assert.Equal(hidden.WallHeight, shown.WallHeight);

        var hiddenBounds = hidden.Groups.SelectMany(group =>
            new[] { group.TitleBounds, group.Bounds }.Concat(group.Members.Select(member => member.Bounds))).ToArray();
        var shownBounds = shown.Groups.SelectMany(group =>
            new[] { group.TitleBounds, group.Bounds }.Concat(group.Members.Select(member => member.Bounds))).ToArray();
        Assert.Equal(hiddenBounds, shownBounds);
        Assert.All(hidden.Groups, group =>
        {
            var laneOrigin = group.LaneIndex == 0 ? 12 : 440;
            var boundsInLane = new[] { group.TitleBounds, group.Bounds }
                .Concat(group.Members.Select(member => member.Bounds));
            Assert.All(boundsInLane, bounds =>
            {
                Assert.Equal(0, (bounds.X - laneOrigin) % 52);
                Assert.Equal(0, (bounds.Y - metrics.TopMargin) % 52);
                Assert.Equal(0, (bounds.X + bounds.Width + 4 - laneOrigin) % 52);
                Assert.Equal(0, (bounds.Y + bounds.Height + 4 - metrics.TopMargin) % 52);
            });
        });
    }

    [Fact]
    public void AsymmetricTopAndBottomMarginsShareMeasureAndCapacityCalculations()
    {
        var metrics = new WallMetrics { TopMargin = 7, Margin = 13 };
        var document = Document(Group("group", Tile("tile", new GridRect(0, 0, 1, 1)))) with
        {
            Rows = 2,
        };
        var geometry = WallLayoutEngine.Measure(document, metrics);
        Assert.Equal(7, geometry.Groups[0].TitleBounds.Y);
        Assert.Equal(7 + 13 + metrics.HeightOf(2), geometry.WallHeight);

        var compressible = Document(Group("group", Tile("tile", new GridRect(0, 1, 1, 1)))) with
        {
            Rows = 4,
        };
        var compacted = WallLayoutEngine.TryApply(compressible,
            new ResizeWallAction(0, 1, 2), metrics.WallWidth(1), 500, metrics);
        Assert.True(compacted.Success, compacted.Detail);
        Assert.Equal(2, compacted.Proposal!.Document.Rows);
        Assert.Equal(new GridRect(0, 0, 1, 1),
            compacted.Proposal.Document.Lanes[0].Groups[0].Members[0].LocalBounds);
        Assert.Equal(120, compacted.Proposal.Geometry.WallHeight);

        var minimumGroupHeight = metrics.TopMargin + metrics.Margin + metrics.TitleHeight +
            metrics.TitleToMembersGap + metrics.HeightOf(1);
        var fits = WallLayoutEngine.TryApply(WallDocument.Empty(1, 1),
            new AddMemberAction(0, Tile("new", new GridRect(0, 0, 1, 1)), null, NewGroupId: "new-group"),
            metrics.WallWidth(1), minimumGroupHeight, metrics);
        Assert.True(fits.Success, fits.Detail);
        Assert.Equal(2, fits.Proposal!.Document.Rows);

        var batchFits = WallBatchPlanner.TryPlan(WallDocument.Empty(1, 1), [Tile("batch", new GridRect(0, 0, 1, 1))], null, 0,
            "batch", metrics.WallWidth(1), minimumGroupHeight, metrics);
        Assert.True(batchFits.Success, batchFits.Detail);
        Assert.Equal(2, batchFits.Proposal!.Document.Rows);

        var empty = WallDocument.Empty(1, 1);
        var before = WallJson.Serialize(empty);
        var beyondScreen = WallLayoutEngine.TryApply(empty,
            new AddMemberAction(0, Tile("new", new GridRect(0, 0, 1, 1)), null, NewGroupId: "new-group"),
            metrics.WallWidth(1), minimumGroupHeight - 0.1, metrics);
        Assert.False(beyondScreen.Success);
        Assert.Equal(WallRejection.NoSpace, beyondScreen.Rejection);
        Assert.Null(beyondScreen.Proposal);
        var batchBeyondScreen = WallBatchPlanner.TryPlan(empty, [Tile("batch", new GridRect(0, 0, 1, 1))], null, 0,
            "batch", metrics.WallWidth(1), minimumGroupHeight - 0.1, metrics);
        Assert.False(batchBeyondScreen.Success);
        Assert.Equal(WallRejection.NoSpace, batchBeyondScreen.Rejection);
        Assert.Null(batchBeyondScreen.Proposal);
        Assert.Equal(before, WallJson.Serialize(empty));
    }

    [Fact]
    public void WallSettingsRejectNewInvalidEnumsTransparencyAndMissingCustomImage()
    {
        var baseline = Document(Group("group", Tile("tile", new GridRect(0, 0, 1, 1))));

        Assert.Contains("SETTINGS_ENUM", WallLayoutEngine.Validate(baseline with
        {
            Settings = baseline.Settings with { LanguageMode = (WallLanguageMode)99 },
        }));
        Assert.Contains("SETTINGS_ENUM", WallLayoutEngine.Validate(baseline with
        {
            Settings = baseline.Settings with { ThemeMode = (WallThemeMode)99 },
        }));
        Assert.Contains("SETTINGS_ENUM", WallLayoutEngine.Validate(baseline with
        {
            Settings = baseline.Settings with { BackgroundMode = (WallBackgroundMode)99 },
        }));
        Assert.Contains("SETTINGS_TRANSPARENCY", WallLayoutEngine.Validate(baseline with
        {
            Settings = baseline.Settings with { BackgroundTransparency = -1 },
        }));
        Assert.Contains("SETTINGS_TRANSPARENCY", WallLayoutEngine.Validate(baseline with
        {
            Settings = baseline.Settings with { BackgroundTransparency = 101 },
        }));
        Assert.Contains("SETTINGS_BACKGROUND_PATH", WallLayoutEngine.Validate(baseline with
        {
            Settings = baseline.Settings with { BackgroundMode = WallBackgroundMode.Custom },
        }));
        Assert.Contains("SETTINGS_BACKGROUND_PATH", WallLayoutEngine.Validate(baseline with
        {
            Settings = baseline.Settings with
            {
                BackgroundMode = WallBackgroundMode.Custom,
                BackgroundImagePath = "  ",
            },
        }));
        Assert.Empty(WallLayoutEngine.Validate(baseline with
        {
            Settings = baseline.Settings with
            {
                BackgroundMode = WallBackgroundMode.Custom,
                BackgroundImagePath = "C:\\wallpaper.png",
                BackgroundTransparency = 0,
            },
        }));
        Assert.Empty(WallLayoutEngine.Validate(baseline with
        {
            Settings = baseline.Settings with { BackgroundTransparency = 100 },
        }));
    }

    [Fact]
    public void AddAndMoveAcrossGroupsProducesOneValidProposalAndRemovesEmptySource()
    {
        var initial = Document(Group("first", Tile("a", new GridRect(0, 0, 1, 1))));
        var add = WallLayoutEngine.TryApply(initial,
            new AddMemberAction(0, Tile("b", new GridRect(0, 0, 1, 1)), null, 0, 1, "second"), 500, 500);
        Assert.True(add.Success, add.Detail);
        Assert.Single(initial.Lanes[0].Groups);
        var afterAdd = add.Proposal!.Document;
        Assert.Equal(1, afterAdd.Generation);
        Assert.Empty(WallLayoutEngine.Validate(afterAdd));

        var move = WallLayoutEngine.TryApply(afterAdd,
            new MoveMemberAction(1, "a", new GridRect(1, 0, 1, 1), "second"), 500, 500);
        Assert.True(move.Success, move.Detail);
        Assert.Single(move.Proposal!.Document.Lanes[0].Groups);
        Assert.Equal("second", move.Proposal.Document.Lanes[0].Groups[0].Id);
        Assert.Equal(2, move.Proposal.Document.Generation);
    }

    [Fact]
    public void OrdinaryReplaceAndMoveKeepImportMarkersOnTheirOriginalLanes()
    {
        var member = Tile("a", new GridRect(0, 0, 1, 1));
        var current = WallDocument.Empty(2, 3) with
        {
            Lanes =
            [
                new WallLane { ImportBatchId = "batch-a", Groups = [Group("first", member)] },
                new WallLane { ImportBatchId = "batch-b", Groups = [Group("second", Tile("b", new GridRect(0, 0, 1, 1)))] },
            ],
        };
        var replacement = member with { Visual = member.Visual with { ShowTitle = false } };
        var replace = WallLayoutEngine.TryApply(current,
            new ReplaceMemberAction(0, "a", replacement), 1000, 1000);
        Assert.True(replace.Success, replace.Detail);
        var replaced = replace.Proposal!.Document;
        Assert.Equal(new[] { "batch-a", "batch-b" }, replaced.Lanes.Select(lane => lane.ImportBatchId));

        var move = WallLayoutEngine.TryApply(replaced,
            new MoveMemberAction(replaced.Generation, "a", new GridRect(1, 0, 1, 1), "second"), 1000, 1000);
        Assert.True(move.Success, move.Detail);
        var moved = move.Proposal!.Document;
        Assert.Empty(moved.Lanes[0].Groups);
        Assert.Equal(new[] { "batch-a", "batch-b" }, moved.Lanes.Select(lane => lane.ImportBatchId));
        Assert.Equal(new[] { "a", "b" }, moved.Lanes[1].Groups[0].Members.Select(item => item.Id).Order());
    }

    [Fact]
    public void SameGroupSingleMemberCanMoveAndCollisionDisplacesExistingMember()
    {
        var initial = Document(Group("g", Tile("a", new GridRect(0, 0, 1, 1)),
            Tile("b", new GridRect(1, 0, 1, 1))));
        var move = WallLayoutEngine.TryApply(initial,
            new MoveMemberAction(0, "a", new GridRect(1, 0, 1, 1), "g"), 500, 500);
        Assert.True(move.Success, move.Detail);
        var members = move.Proposal!.Document.Lanes[0].Groups[0].Members;
        Assert.Equal(new GridRect(1, 0, 1, 1), members.Single(m => m.Id == "a").LocalBounds);
        Assert.False(members[0].LocalBounds.Intersects(members[1].LocalBounds));

        var solo = Document(Group("g", Tile("a", new GridRect(0, 0, 1, 1))));
        var soloMove = WallLayoutEngine.TryApply(solo,
            new MoveMemberAction(0, "a", new GridRect(3, 0, 1, 1), "g"), 500, 500);
        Assert.True(soloMove.Success, soloMove.Detail);
        Assert.Equal(new GridRect(3, 0, 1, 1), soloMove.Proposal!.Document.Lanes[0].Groups[0].Members[0].LocalBounds);
    }

    [Fact]
    public void NewGroupInsertionIndexIsAfterEmptySourceRemoval()
    {
        var initial = Document(Group("source", Tile("a", new GridRect(0, 0, 1, 1))),
            Group("existing", Tile("b", new GridRect(0, 0, 1, 1)))) with { Rows = 4 };
        var moved = WallLayoutEngine.TryApply(initial,
            new MoveMemberAction(0, "a", new GridRect(0, 0, 1, 1), null, 0, 1, "new"), 500, 500);
        Assert.True(moved.Success, moved.Detail);
        Assert.Equal(["existing", "new"], moved.Proposal!.Document.Lanes[0].Groups.Select(g => g.Id));
    }

    [Fact]
    public void AppendUsesHorizontalSpaceInCurrentWallBeforeGrowingDownward()
    {
        var initial = Document(Group("g", Tile("old", new GridRect(0, 0, 1, 1)))) with { Rows = 2 };
        var result = WallLayoutEngine.TryApply(initial,
            new AddMemberAction(0, Tile("new", new GridRect(0, 0, 1, 1)), "g"), 500, 500);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(2, result.Proposal!.Document.Rows);
        var members = result.Proposal.Document.Lanes[0].Groups[0].Members;
        Assert.Equal(new GridRect(0, 0, 1, 1), members.Single(m => m.Id == "old").LocalBounds);
        Assert.Equal(new GridRect(1, 0, 1, 1), members.Single(m => m.Id == "new").LocalBounds);
    }

    [Fact]
    public void AppendPreservesOtherGroupsAndExistingHeight()
    {
        var initial = Document(Group("first", Tile("first-tile", new GridRect(0, 0, 1, 1))),
            Group("target", Tile("old", new GridRect(0, 0, 1, 1)))) with { Rows = 4 };
        var result = WallLayoutEngine.TryApply(initial,
            new AddMemberAction(0, Tile("new", new GridRect(0, 0, 1, 1)), "target"), 500, 500);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(4, result.Proposal!.Document.Rows);
        var groups = result.Proposal.Document.Lanes[0].Groups;
        Assert.Equal(new GridRect(0, 0, 1, 1), groups[0].Members[0].LocalBounds);
        Assert.Equal(new GridRect(0, 0, 1, 1), groups[1].Members.Single(m => m.Id == "old").LocalBounds);
        Assert.Equal(new GridRect(1, 0, 1, 1), groups[1].Members.Single(m => m.Id == "new").LocalBounds);
    }

    [Fact]
    public void CollisionPreservesUnrelatedMemberInsteadOfCreatingAvoidableChain()
    {
        var initial = Document(Group("g", Tile("hit", new GridRect(0, 0, 1, 1)),
            Tile("unrelated", new GridRect(0, 1, 1, 1)), Tile("moving", new GridRect(3, 1, 1, 1))));
        var result = WallLayoutEngine.TryApply(initial,
            new MoveMemberAction(0, "moving", new GridRect(0, 0, 1, 1), "g"), 500, 500);
        Assert.True(result.Success, result.Detail);
        var members = result.Proposal!.Document.Lanes[0].Groups[0].Members;
        Assert.Equal(new GridRect(1, 0, 1, 1), members.Single(m => m.Id == "hit").LocalBounds);
        Assert.Equal(new GridRect(0, 1, 1, 1), members.Single(m => m.Id == "unrelated").LocalBounds);
        Assert.Equal(3, result.Proposal.Document.Rows);
    }

    [Fact]
    public void ClockCanBeDraggedAndDisplacedWithoutChangingMemberKind()
    {
        var initial = Document(Group("g", Clock("clock", new GridRect(0, 0, 1, 1)),
            Tile("tile", new GridRect(1, 0, 1, 1)))) with { Rows = 2 };
        var dragged = WallLayoutEngine.TryApply(initial,
            new MoveMemberAction(0, "clock", new GridRect(1, 0, 1, 1), "g"), 500, 500);
        Assert.True(dragged.Success, dragged.Detail);
        Assert.IsType<ClockMember>(dragged.Proposal!.Document.Lanes[0].Groups[0].Members.Single(m => m.Id == "clock"));
        Assert.Empty(WallLayoutEngine.Validate(dragged.Proposal.Document));

        var displaced = WallLayoutEngine.TryApply(initial,
            new MoveMemberAction(0, "tile", new GridRect(0, 0, 1, 1), "g"), 500, 500);
        Assert.True(displaced.Success, displaced.Detail);
        Assert.IsType<ClockMember>(displaced.Proposal!.Document.Lanes[0].Groups[0].Members.Single(m => m.Id == "clock"));
        Assert.Empty(WallLayoutEngine.Validate(displaced.Proposal.Document));
    }

    [Fact]
    public void WholeGroupMovePreservesLocalBoundsAndRejectsViewportOverflow()
    {
        var initial = Document(Group("a", Tile("a1", new GridRect(2, 1, 2, 2))),
            Group("b", Tile("b1", new GridRect(0, 0, 1, 1)))) with { Rows = 6 };
        var moved = WallLayoutEngine.TryApply(initial, new MoveGroupAction(0, "a", 0, 1), 500, 500);
        Assert.True(moved.Success, moved.Detail);
        Assert.Equal("a", moved.Proposal!.Document.Lanes[0].Groups[1].Id);
        Assert.Equal(new GridRect(2, 1, 2, 2), moved.Proposal.Document.Lanes[0].Groups[1].Members[0].LocalBounds);

        var before = WallJson.Serialize(initial);
        var rejected = WallLayoutEngine.TryApply(initial, new MoveGroupAction(0, "a", 1, 0), 1, 1);
        Assert.False(rejected.Success);
        Assert.Null(rejected.Proposal);
        Assert.Equal(before, WallJson.Serialize(initial));
        var extended = WallLayoutEngine.TryApply(initial, new MoveGroupAction(0, "a", 1, 0),
            new WallMetrics().WallWidth(2), new WallMetrics().WallHeight(6));
        Assert.True(extended.Success, extended.Detail);
        Assert.Equal(2, extended.Proposal!.Document.Columns);
        Assert.Equal("a", Assert.Single(extended.Proposal.Document.Lanes[1].Groups).Id);
        Assert.Equal(new GridRect(2, 1, 2, 2), Assert.Single(extended.Proposal.Document.Lanes[1].Groups[0].Members).LocalBounds);
        Assert.Equal(0, initial.Generation);
    }

    [Fact]
    public void AddRejectsViewportHeightOverflowAndExplicitShrinkCannotClip()
    {
        var initial = Document(Group("a", Tile("a1", new GridRect(0, 0, 1, 1))));
        var before = WallJson.Serialize(initial);
        var metrics = new WallMetrics();
        var action = new AddMemberAction(0, Tile("b1", new GridRect(0, 0, 1, 1)), null, 0, 1, "b");
        var rejected = WallLayoutEngine.TryApply(initial, action, metrics.WallWidth(1), metrics.WallHeight(3));
        Assert.False(rejected.Success);
        Assert.Equal(WallRejection.WorkAreaTooSmall, rejected.Rejection);
        Assert.Null(rejected.Proposal);
        Assert.Equal(before, WallJson.Serialize(initial));
        var add = WallLayoutEngine.TryApply(initial, action, metrics.WallWidth(1), metrics.WallHeight(4));
        Assert.True(add.Success, add.Detail);
        Assert.Equal(4, add.Proposal!.Document.Rows);
        Assert.Single(initial.Lanes[0].Groups);

        var shrink = WallLayoutEngine.TryApply(initial, new ResizeWallAction(0, 1, 1), 500, 500);
        Assert.Equal(WallRejection.NoSpace, shrink.Rejection);
        Assert.Null(shrink.Proposal);
        Assert.Equal(before, WallJson.Serialize(initial));
    }

    [Fact]
    public void OversizedResizeRejectsBeforeAllocatingLanes()
    {
        var initial = WallDocument.Empty(1, 6);
        var result = WallLayoutEngine.TryApply(initial,
            new ResizeWallAction(0, int.MaxValue, int.MaxValue), 500, 500);
        Assert.Equal(WallRejection.InvalidAction, result.Rejection);
        Assert.Single(initial.Lanes);
    }

    [Fact]
    public void StaleGenerationCannotOverwriteNewerWall()
    {
        var initial = Document(Group("g", Tile("a", new GridRect(0, 0, 1, 1)))) with { Generation = 4 };
        var result = WallLayoutEngine.TryApply(initial,
            new RemoveMemberAction(3, "a"), 500, 500);
        Assert.Equal(WallRejection.StaleGeneration, result.Rejection);
        Assert.NotNull(initial.Lanes[0].Groups[0].Members[0]);
    }

    [Fact]
    public void MalformedModelIsRejectedBeforeOverflowOrMeasurement()
    {
        var overflow = Document(Group("g", Tile("t", new GridRect(int.MaxValue, int.MaxValue, 2, 2))));
        Assert.Contains("MEMBER_BOUNDS", WallLayoutEngine.Validate(overflow));
        Assert.Throws<ArgumentException>(() => WallLayoutEngine.Measure(overflow));
        Assert.Equal(WallRejection.InvalidDocument,
            WallLayoutEngine.TryApply(overflow, new RemoveMemberAction(0, "t"), 500, 500).Rejection);

        var invalidSettings = Document(Group("g", Tile("t", new GridRect(0, 0, 1, 1)))) with
        {
            Settings = new WallSettings { AnchorCorner = (AnchorCorner)99 },
        };
        Assert.Contains("SETTINGS_ENUM", WallLayoutEngine.Validate(invalidSettings));

        var invalidVisual = Document(Group("g", Tile("t", new GridRect(0, 0, 1, 1)) with
        {
            Visual = null!,
        }));
        Assert.Contains("VISUAL_NULL", WallLayoutEngine.Validate(invalidVisual));

        var oversized = Document(Group("g", Tile("t", new GridRect(0, 0, 1, 1)))) with
        {
            Rows = int.MaxValue,
        };
        Assert.Contains("WALL_STRUCTURE", WallLayoutEngine.Validate(oversized));
    }

    [Fact]
    public void SchemaThreeRoundTripKeepsMemberKindsAndSharedImageState()
    {
        var puzzle = new PuzzleMember
        {
            Id = "p",
            LocalBounds = new GridRect(0, 0, 2, 2),
            Partitions = [new GridRect(0, 0, 2, 2)],
            Images = new MemberImageSource
            {
                Kind = MemberImageSourceKind.Single,
                Path = "C:\\sample.png",
            },
            ImageDisplay = new MemberImageDisplay { Mode = ImageDisplayMode.Custom, CustomScale = 1.5 },
            Carousel = new ImageCarouselState
            {
                CurrentImageId = "image-1",
                LastSwitchUtc = DateTimeOffset.UnixEpoch,
                CandidateIds = ["image-1"],
                Queue = ["image-1"],
            },
        };
        var original = Document(Group("g", puzzle,
            new TileMember { Id = "t", LocalBounds = new GridRect(3, 0, 1, 1) },
            new ClockMember { Id = "c", LocalBounds = new GridRect(4, 0, 2, 1) }));
        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<WallDocument>(json)!;
        Assert.Empty(WallLayoutEngine.Validate(restored));
        var members = restored.Lanes[0].Groups[0].Members;
        Assert.IsType<PuzzleMember>(members[0]);
        Assert.IsType<TileMember>(members[1]);
        Assert.IsType<ClockMember>(members[2]);
        Assert.Equal("image-1", Assert.IsType<PuzzleMember>(members[0]).Carousel!.CurrentImageId);
        Assert.Equal(ImageDisplayMode.Custom, members[0].ImageDisplay.Mode);
    }

    [Fact]
    public void ReplaceMemberUsesCollisionRulesAndKeepsIdentityKindAndSpatialOrder()
    {
        var initial = Document(Group("g", Tile("edited", new GridRect(0, 0, 1, 1)),
            Tile("blocker", new GridRect(1, 0, 1, 1)),
            Tile("unrelated", new GridRect(3, 0, 1, 1))));
        var replacement = Tile("edited", new GridRect(0, 0, 2, 2)) with
        {
            Visual = new MemberVisual { Title = "new title" },
        };
        var result = WallLayoutEngine.TryApply(initial,
            new ReplaceMemberAction(0, "edited", replacement), 500, 500);

        Assert.True(result.Success, result.Detail);
        Assert.Equal(1, result.Proposal!.Document.Generation);
        var members = result.Proposal.Document.Lanes[0].Groups[0].Members;
        Assert.Equal(["edited", "blocker", "unrelated"], members.Select(member => member.Id));
        Assert.Equal(new GridRect(0, 0, 2, 2), members[0].LocalBounds);
        Assert.Equal(new GridRect(3, 0, 1, 1), members[2].LocalBounds);
        Assert.NotEqual(new GridRect(1, 0, 1, 1), members[1].LocalBounds);
        Assert.Equal("new title", members[0].Visual.Title);
        Assert.Equal(new GridRect(0, 0, 1, 1), initial.Lanes[0].Groups[0].Members[0].LocalBounds);

        Assert.Equal(WallRejection.InvalidAction, WallLayoutEngine.TryApply(initial,
            new ReplaceMemberAction(0, "edited", Tile("different", new GridRect(0, 0, 1, 1))),
            500, 500).Rejection);
        Assert.Equal(WallRejection.InvalidAction, WallLayoutEngine.TryApply(initial,
            new ReplaceMemberAction(0, "edited", new ClockMember
            {
                Id = "edited", LocalBounds = new GridRect(0, 0, 1, 1),
            }), 500, 500).Rejection);
    }

    [Fact]
    public void BatchAddsOneGenerationAndRejectsWholeBatchWhenLastItemFails()
    {
        var initial = WallDocument.Empty(1, 3);
        var success = WallLayoutEngine.TryApply(initial, new AddBatchAction(0,
        [
            new AddBatchItem(Tile("one", new GridRect(0, 0, 1, 1)), null, NewGroupId: "group"),
            new AddBatchItem(Tile("two", new GridRect(2, 0, 1, 1)), "group"),
        ]), 500, 500);
        Assert.True(success.Success, success.Detail);
        Assert.Equal(1, success.Proposal!.Document.Generation);
        Assert.Equal(2, success.Proposal.Document.Lanes[0].Groups[0].Members.Count);
        Assert.Empty(initial.Lanes[0].Groups);

        var failure = WallLayoutEngine.TryApply(initial, new AddBatchAction(0,
        [
            new AddBatchItem(Tile("one", new GridRect(0, 0, 1, 1)), null, NewGroupId: "group"),
            new AddBatchItem(Tile("invalid", new GridRect(7, 0, 2, 1)), "group"),
        ]), 500, 500);
        Assert.Equal(WallRejection.InvalidAction, failure.Rejection);
        Assert.Null(failure.Proposal);
        Assert.Equal(0, initial.Generation);
        Assert.Empty(initial.Lanes[0].Groups);
    }

    [Fact]
    public void ManualActionsClaimImportedContentOnlyWhenItsMeaningfulPositionOrPropertiesChange()
    {
        var groupSource = new ImportSourceMetadata
        {
            BatchId = "batch",
            GroupBucketKey = "bucket",
            OriginGroupId = "origin-group",
        };
        var memberSource = groupSource with
        {
            SourceRoot = "UserPrograms",
            RelativePath = "App.lnk",
        };
        var member = Tile("imported", new GridRect(0, 0, 1, 1)) with { ImportSource = memberSource };
        var group = Group("managed", member) with { Title = "Managed", ImportSource = groupSource };
        var current = new WallDocument
        {
            Columns = 2,
            Rows = 3,
            Lanes = [new WallLane { Groups = [group] }, new WallLane { Groups = [] }],
        };

        var noOpReplace = WallLayoutEngine.TryApply(current,
            new ReplaceMemberAction(0, member.Id, member with
            {
                Carousel = new ImageCarouselState
                {
                    CandidateIds = ["one", "two"],
                    Queue = ["two", "one"],
                    NextIndex = 1,
                    Round = 1,
                },
            }), new WallMetrics().WallWidth(2), new WallMetrics().WallHeight(3));
        Assert.True(noOpReplace.Success, noOpReplace.Detail);
        Assert.False(noOpReplace.Proposal!.Document.Lanes[0].Groups[0].Members[0].UserClaimed);

        var edited = WallLayoutEngine.TryApply(current,
            new ReplaceMemberAction(0, member.Id, member with
            {
                Visual = member.Visual with { TitleMode = MemberTitleMode.Manual, Title = "Edited" },
            }), new WallMetrics().WallWidth(2), new WallMetrics().WallHeight(3));
        Assert.True(edited.Success, edited.Detail);
        Assert.True(edited.Proposal!.Document.Lanes[0].Groups[0].Members[0].UserClaimed);

        var noOpMove = WallLayoutEngine.TryApply(current,
            new MoveMemberAction(0, member.Id, member.LocalBounds, group.Id), new WallMetrics().WallWidth(2), new WallMetrics().WallHeight(3));
        Assert.True(noOpMove.Success, noOpMove.Detail);
        Assert.False(noOpMove.Proposal!.Document.Lanes[0].Groups[0].Members[0].UserClaimed);

        var moved = WallLayoutEngine.TryApply(current,
            new MoveMemberAction(0, member.Id, new GridRect(1, 0, 1, 1), group.Id), new WallMetrics().WallWidth(2), new WallMetrics().WallHeight(3));
        Assert.True(moved.Success, moved.Detail);
        Assert.True(moved.Proposal!.Document.Lanes[0].Groups[0].Members[0].UserClaimed);

        var noOpRename = WallLayoutEngine.TryApply(current,
            new RenameGroupAction(0, group.Id, group.Title), new WallMetrics().WallWidth(2), new WallMetrics().WallHeight(3));
        Assert.True(noOpRename.Success, noOpRename.Detail);
        Assert.False(noOpRename.Proposal!.Document.Lanes[0].Groups[0].UserClaimed);

        var renamed = WallLayoutEngine.TryApply(current,
            new RenameGroupAction(0, group.Id, "Renamed"), new WallMetrics().WallWidth(2), new WallMetrics().WallHeight(3));
        Assert.True(renamed.Success, renamed.Detail);
        Assert.True(renamed.Proposal!.Document.Lanes[0].Groups[0].UserClaimed);

        var noOpGroupMove = WallLayoutEngine.TryApply(current,
            new MoveGroupAction(0, group.Id, 0, 0), new WallMetrics().WallWidth(2), new WallMetrics().WallHeight(3));
        Assert.True(noOpGroupMove.Success, noOpGroupMove.Detail);
        Assert.False(noOpGroupMove.Proposal!.Document.Lanes[0].Groups[0].UserClaimed);

        var groupMoved = WallLayoutEngine.TryApply(current,
            new MoveGroupAction(0, group.Id, 1, 0), new WallMetrics().WallWidth(2), new WallMetrics().WallHeight(3));
        Assert.True(groupMoved.Success, groupMoved.Detail);
        Assert.True(groupMoved.Proposal!.Document.Lanes[1].Groups[0].UserClaimed);
    }

    [Fact]
    public void SavedRowsMustContainFullGroupGeometryIndependentlyOfMonitorSize()
    {
        var tooShort = Document(Group("one", Tile("a", new GridRect(0, 0, 1, 1))),
            Group("two", Tile("b", new GridRect(0, 0, 1, 1))));
        Assert.Empty(WallLayoutEngine.Validate(tooShort));
        Assert.Contains("WALL_GEOMETRY", WallLayoutEngine.ValidateGeometry(tooShort));
        Assert.Throws<ArgumentException>(() => WallLayoutEngine.Measure(tooShort));
        Assert.Equal(WallRejection.InvalidDocument, WallLayoutEngine.TryApply(tooShort,
            new RemoveMemberAction(0, "b"), 500, 500).Rejection);

        var savedLarger = tooShort with { Rows = 4 };
        Assert.Empty(WallLayoutEngine.ValidateGeometry(savedLarger));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("Lpt9.txt")]
    [InlineData("COM¹")]
    [InlineData("ends.")]
    [InlineData("ends ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void PathUnsafeIdsAreRejectedWithoutManagedEntries(string id)
    {
        Assert.Contains("INVALID_ID", WallLayoutEngine.Validate(
            Document(Group("g", Tile(id, new GridRect(0, 0, 1, 1))))));
    }

    [Fact]
    public void CaseOnlyIdsAndGenerationOverflowAreRejected()
    {
        var aliases = Document(Group("g", Tile("Alpha", new GridRect(0, 0, 1, 1)),
            Tile("alpha", new GridRect(1, 0, 1, 1))));
        Assert.Contains("DUPLICATE_ID", WallLayoutEngine.Validate(aliases));

        var overflow = Document(Group("g", Tile("a", new GridRect(0, 0, 1, 1)))) with
        {
            Generation = long.MaxValue,
        };
        Assert.Contains("GENERATION_OVERFLOW", WallLayoutEngine.Validate(overflow));
        Assert.Equal(WallRejection.InvalidDocument, WallLayoutEngine.TryApply(overflow,
            new RemoveMemberAction(long.MaxValue, "a"), 500, 500).Rejection);
    }

    [Fact]
    public void InvalidSharedImageSettingsPartitionsAndClockValuesRejectBeforeMeasurement()
    {
        var badImage = new PuzzleMember
        {
            Id = "p", LocalBounds = new GridRect(0, 0, 2, 2),
            Partitions = [new GridRect(0, 0, 2, 2)],
            Images = new MemberImageSource
            {
                Kind = MemberImageSourceKind.Single,
            },
        };
        var invalid = Document(Group("g", badImage,
            new ClockMember { Id = "clock", LocalBounds = new GridRect(3, 0, 1, 1),
                TimeFormat = null! }));
        var errors = WallLayoutEngine.Validate(invalid);
        Assert.Contains("IMAGE_SOURCE_PATH", errors);
        Assert.Contains("CLOCK_FORMAT", errors);
        Assert.Throws<ArgumentException>(() => WallLayoutEngine.Measure(invalid));

        var invalidDisplay = invalid with
        {
            Lanes = [new WallLane { Groups = [Group("g", badImage with
            {
                Images = badImage.Images with { Path = "C:\\image.png" },
                ImageDisplay = new MemberImageDisplay
                {
                    Mode = (ImageDisplayMode)99,
                },
            })] }],
        };
        Assert.Contains("IMAGE_DISPLAY", WallLayoutEngine.Validate(invalidDisplay));

        var invalidPartitions = Document(Group("g", badImage with
        {
            Images = badImage.Images with { Path = "C:\\image.png" },
            Partitions = [new GridRect(0, 0, 2, 2), new GridRect(1, 1, 1, 1)],
        }));
        Assert.Contains("PUZZLE_PARTITIONS", WallLayoutEngine.Validate(invalidPartitions));
    }
}

