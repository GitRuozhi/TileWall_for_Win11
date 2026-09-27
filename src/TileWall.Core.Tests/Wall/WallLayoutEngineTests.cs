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
        var geometry = WallLayoutEngine.Measure(Document(
            Group("a", Tile("a1", new GridRect(0, 0, 1, 1))),
            Group("b", Tile("b1", new GridRect(0, 0, 1, 1)))) with { Rows = 4 }, metrics);
        Assert.Equal(412, geometry.Groups[0].TitleBounds.Width);
        Assert.Equal(geometry.Groups[0].Bounds.Y + geometry.Groups[0].Bounds.Height + metrics.GroupGap,
            geometry.Groups[1].Bounds.Y);
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
    public void CollisionUsesHorizontalSpaceInCurrentWallBeforeGrowingDownward()
    {
        var initial = Document(Group("g", Tile("old", new GridRect(0, 0, 1, 1)))) with { Rows = 2 };
        var result = WallLayoutEngine.TryApply(initial,
            new AddMemberAction(0, Tile("new", new GridRect(0, 0, 1, 1)), "g"), 500, 500);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(2, result.Proposal!.Document.Rows);
        var members = result.Proposal.Document.Lanes[0].Groups[0].Members;
        Assert.Equal(new GridRect(1, 0, 1, 1), members.Single(m => m.Id == "old").LocalBounds);
        Assert.Equal(new GridRect(0, 0, 1, 1), members.Single(m => m.Id == "new").LocalBounds);
    }

    [Fact]
    public void CurrentHeightBudgetSubtractsOtherGroupsAndTheirGap()
    {
        var initial = Document(Group("first", Tile("first-tile", new GridRect(0, 0, 1, 1))),
            Group("target", Tile("old", new GridRect(0, 0, 1, 1)))) with { Rows = 4 };
        var result = WallLayoutEngine.TryApply(initial,
            new AddMemberAction(0, Tile("new", new GridRect(0, 0, 1, 1)), "target"), 500, 500);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(4, result.Proposal!.Document.Rows);
        var groups = result.Proposal.Document.Lanes[0].Groups;
        Assert.Equal(new GridRect(0, 0, 1, 1), groups[0].Members[0].LocalBounds);
        Assert.Equal(new GridRect(1, 0, 1, 1), groups[1].Members.Single(m => m.Id == "old").LocalBounds);
    }

    [Fact]
    public void CollisionPreservesUnrelatedMemberInsteadOfCreatingAvoidableChain()
    {
        var initial = Document(Group("g", Tile("hit", new GridRect(0, 0, 1, 1)),
            Tile("unrelated", new GridRect(0, 1, 1, 1))));
        var result = WallLayoutEngine.TryApply(initial,
            new AddMemberAction(0, Tile("new", new GridRect(0, 0, 1, 1)), "g"), 500, 500);
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
            new AddMemberAction(0, Tile("new", new GridRect(0, 0, 1, 1)), "g"), 500, 500);
        Assert.True(displaced.Success, displaced.Detail);
        Assert.IsType<ClockMember>(displaced.Proposal!.Document.Lanes[0].Groups[0].Members.Single(m => m.Id == "clock"));
        Assert.Empty(WallLayoutEngine.Validate(displaced.Proposal.Document));
    }

    [Fact]
    public void WholeGroupMovePreservesLocalBoundsAndRejectsOutOfScreenLane()
    {
        var initial = Document(Group("a", Tile("a1", new GridRect(2, 1, 2, 2))),
            Group("b", Tile("b1", new GridRect(0, 0, 1, 1)))) with { Rows = 6 };
        var moved = WallLayoutEngine.TryApply(initial, new MoveGroupAction(0, "a", 0, 1), 500, 500);
        Assert.True(moved.Success, moved.Detail);
        Assert.Equal("a", moved.Proposal!.Document.Lanes[0].Groups[1].Id);
        Assert.Equal(new GridRect(2, 1, 2, 2), moved.Proposal.Document.Lanes[0].Groups[1].Members[0].LocalBounds);

        var rejected = WallLayoutEngine.TryApply(initial, new MoveGroupAction(0, "a", 1, 0), 500, 500);
        Assert.Equal(WallRejection.WorkAreaTooSmall, rejected.Rejection);
        Assert.Equal(0, initial.Generation);
    }

    [Fact]
    public void InsufficientHeightRejectsEntireProposalAndShrinkCannotClip()
    {
        var initial = Document(Group("a", Tile("a1", new GridRect(0, 0, 1, 1))));
        var cannotAdd = WallLayoutEngine.TryApply(initial,
            new AddMemberAction(0, Tile("b1", new GridRect(0, 0, 1, 1)), null, 0, 1, "b"), 500, 180);
        Assert.Equal(WallRejection.NoSpace, cannotAdd.Rejection);
        Assert.Single(initial.Lanes[0].Groups);

        var shrink = WallLayoutEngine.TryApply(initial, new ResizeWallAction(0, 1, 1), 500, 500);
        Assert.Equal(WallRejection.NoSpace, shrink.Rejection);
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
    public void SchemaTwoRoundTripKeepsMemberKindsAndImageState()
    {
        var puzzle = new PuzzleMember
        {
            Id = "p",
            LocalBounds = new GridRect(0, 0, 2, 2),
            Partitions = [new GridRect(0, 0, 2, 2)],
            Images = new PuzzleImageSource
            {
                Kind = PuzzleImageSourceKind.Single,
                ImagePaths = ["C:\\sample.png"],
                Transforms = [new PuzzleImageTransform("image-1", FitMode.CoverFill,
                    new ImageTransform(1.5, -3, 7))],
            },
            Carousel = new PuzzleCarouselState("image-1", DateTimeOffset.UnixEpoch),
        };
        var original = Document(Group("g", puzzle,
            new TileMember { Id = "t", LocalBounds = new GridRect(3, 0, 1, 1),
                Foreground = new NoForegroundIcon() },
            new ClockMember { Id = "c", LocalBounds = new GridRect(4, 0, 2, 1) }));
        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<WallDocument>(json)!;
        Assert.Empty(WallLayoutEngine.Validate(restored));
        var members = restored.Lanes[0].Groups[0].Members;
        Assert.IsType<PuzzleMember>(members[0]);
        Assert.IsType<NoForegroundIcon>(Assert.IsType<TileMember>(members[1]).Foreground);
        Assert.IsType<ClockMember>(members[2]);
        Assert.Equal("image-1", Assert.IsType<PuzzleMember>(members[0]).Carousel!.CurrentImageId);
    }

    [Fact]
    public void ReplaceMemberUsesCollisionRulesButKeepsIdentityKindAndOrder()
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
    public void MalformedNestedImageAndClockValuesRejectBeforeMeasurement()
    {
        var badImage = new PuzzleMember
        {
            Id = "p", LocalBounds = new GridRect(0, 0, 2, 2),
            Partitions = [new GridRect(0, 0, 2, 2)],
            Images = new PuzzleImageSource
            {
                Kind = PuzzleImageSourceKind.Single,
                ImagePaths = [null!],
                Transforms = [new PuzzleImageTransform("image", FitMode.CoverFill,
                    new ImageTransform(double.NaN, 0, 0))],
            },
        };
        var invalid = Document(Group("g", badImage,
            new ClockMember { Id = "clock", LocalBounds = new GridRect(3, 0, 1, 1),
                TimeFormat = null! }));
        var errors = WallLayoutEngine.Validate(invalid);
        Assert.Contains("PUZZLE_IMAGES", errors);
        Assert.Contains("CLOCK_FORMAT", errors);
        Assert.Throws<ArgumentException>(() => WallLayoutEngine.Measure(invalid));

        var invalidTransform = invalid with
        {
            Lanes = [new WallLane { Groups = [Group("g", badImage with
            {
                Images = badImage.Images with { ImagePaths = ["C:\\image.png"] },
            })] }],
        };
        Assert.Contains("PUZZLE_TRANSFORMS", WallLayoutEngine.Validate(invalidTransform));
    }

    [Theory]
    [InlineData(@"C:\Pictures\Photo.png")]
    [InlineData(@"c:\pictures\PHOTO.PNG")]
    public void DuplicateWindowsImageTransformsRejectValidationAndReplacement(string duplicateId)
    {
        var original = new PuzzleMember
        {
            Id = "p", LocalBounds = new GridRect(0, 0, 2, 2),
            Partitions = [new GridRect(0, 0, 2, 2)],
        };
        var replacement = original with
        {
            Images = new PuzzleImageSource
            {
                Kind = PuzzleImageSourceKind.Single,
                ImagePaths = [@"C:\Pictures\Photo.png"],
                Transforms =
                [
                    new PuzzleImageTransform(@"C:\Pictures\Photo.png", FitMode.CoverFill, new(1, 2, 3)),
                    new PuzzleImageTransform(duplicateId, FitMode.FitAll, new(2, 4, 6)),
                ],
            },
        };
        Assert.Contains("PUZZLE_TRANSFORMS", WallLayoutEngine.Validate(Document(Group("g", replacement))));
        var result = WallLayoutEngine.TryApply(Document(Group("g", original)),
            new ReplaceMemberAction(0, "p", replacement), 500, 500);
        Assert.Null(result.Proposal);
        Assert.Equal(WallRejection.InvalidAction, result.Rejection);
        Assert.Contains("PUZZLE_TRANSFORMS", result.Detail);
    }

    [Fact]
    public void DistinctImageTransformsRemainUnchangedInReplacement()
    {
        var original = new PuzzleMember
        {
            Id = "p", LocalBounds = new GridRect(0, 0, 2, 2),
            Partitions = [new GridRect(0, 0, 2, 2)],
        };
        var transforms = new[]
        {
            new PuzzleImageTransform(@"C:\Pictures\One.png", FitMode.CoverFill, new(1, 2, 3)),
            new PuzzleImageTransform(@"C:\Pictures\Two.png", FitMode.FitAll, new(2, 4, 6)),
        };
        var replacement = original with { Images = new PuzzleImageSource { Transforms = transforms } };
        var result = WallLayoutEngine.TryApply(Document(Group("g", original)),
            new ReplaceMemberAction(0, "p", replacement), 500, 500);
        Assert.True(result.Success, result.Detail);
        var saved = Assert.IsType<PuzzleMember>(Assert.Single(Assert.Single(result.Proposal!.Document.Lanes[0].Groups).Members));
        Assert.Equal(transforms, saved.Images.Transforms);
    }
}
