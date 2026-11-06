using TileWall.Core.Grid;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class WallMemberDropTargetTests
{
    private static readonly WallMetrics Metrics = new();

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(2, 2, false)]
    [InlineData(3, 3, true)]
    [InlineData(4, 2, false)]
    [InlineData(6, 4, true)]
    [InlineData(8, 12, true)]
    public void SameGridFootprintAppendsWithoutAGapAndCreatesOnlyAfterATitleRow(int width, int height, bool puzzle)
    {
        var document = Document(Moving(width, height, puzzle));
        foreach (var (globalRow, target, localRow) in new[] { (2, "target", 1), (3, (string?)null, 0) })
        {
            var dragged = Rect(0, 0, globalRow, width, height);
            foreach (var grab in Grabs(dragged))
            {
                var session = Session(document);
                var actual = WallMemberDropResolver.DragBounds(
                    new DipPoint(dragged.X + grab.X, dragged.Y + grab.Y), grab,
                    new GridSize(width, height), Metrics);
                Assert.Equal(dragged, actual);
                var slot = session.Snap(actual);
                Assert.Equal(new WallMemberDropSlot(0, new GridRect(0, globalRow, width, height)), slot);
                var action = session.Resolve(slot);
                Assert.Equal(target, action.TargetGroupId);
                Assert.Equal(new GridRect(0, localRow, width, height), action.TargetBounds);
                var preview = Apply(document, action);
                var member = preview.Geometry.Groups.SelectMany(group => group.Members)
                    .Single(member => member.MemberId == "moving");
                Assert.Equal(globalRow * Metrics.Pitch, member.Bounds.Y);
                Assert.Equal(target is null ? 2 : 1, preview.Document.Lanes[0].Groups.Count);
            }
        }
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(3, 3, true)]
    [InlineData(6, 4, true)]
    public void DirectAndViaInsideOrTemporaryNewGroupChooseTheSameTarget(int width, int height, bool puzzle)
    {
        var document = Document(Moving(width, height, puzzle));
        var drop = Rect(0, 0, 2, width, height);
        var direct = Session(document);
        var expected = direct.Resolve(direct.Snap(drop));
        foreach (var previousRow in new[] { 1, 4 })
        {
            var session = Session(document);
            var previous = session.Snap(Rect(0, 0, previousRow, width, height));
            Apply(document, session.Resolve(previous));
            session.SetDisplayed(previous);
            var slot = session.Snap(drop);
            Assert.Equal(expected, session.Resolve(slot));
            session.SetDisplayed(slot);
            Assert.Equal(expected, session.Resolve(session.Snap(drop with { Y = drop.Y + 1 })));
        }
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(3, 3, true)]
    [InlineData(6, 4, true)]
    public void HorizontalSnapChoosesTheClosestLegalWholeMemberSlot(int width, int height, bool puzzle)
    {
        var session = Session(Document(Moving(width, height, puzzle)));
        var lastColumn = 8 - width;
        var lastX = Metrics.Margin + lastColumn * Metrics.Pitch;
        var nextX = Metrics.Margin + Metrics.LaneWidth + Metrics.ColumnGap;
        var middle = (lastX + nextX) / 2;
        var left = session.Snap(Rect(0, 0, 1, width, height) with { X = middle - 1 });
        var right = session.Snap(Rect(0, 0, 1, width, height) with { X = middle + 1 });
        Assert.Equal(new WallMemberDropSlot(0, new GridRect(lastColumn, 1, width, height)), left);
        Assert.Equal(new WallMemberDropSlot(1, new GridRect(0, 1, width, height)), right);
        Assert.Equal(-1, session.Snap(Rect(-1, 0, 1, width, height)).LaneIndex);
        Assert.Equal(64, session.Snap(Rect(64, 0, 1, width, height)).LaneIndex);
    }

    [Fact]
    public void NewGroupUpdatesGridPositionAndKeepsTheSnappedHeightAfterItsTitle()
    {
        var document = Document(Moving(3, 3, true));
        var session = Session(document);
        foreach (var (column, globalRow, localRow) in new[] { (4, 3, 0), (2, 5, 2), (5, 4, 1) })
        {
            var slot = session.Snap(Rect(0, column, globalRow, 3, 3));
            var action = session.Resolve(slot);
            Assert.Null(action.TargetGroupId);
            Assert.Equal("new", action.NewGroupId);
            Assert.Equal(0, action.LaneIndex);
            Assert.Equal(1, action.GroupInsertIndex);
            Assert.Equal(new GridRect(column, localRow, 3, 3), action.TargetBounds);
            var preview = Apply(document, action);
            Assert.Equal(globalRow * Metrics.Pitch, preview.Geometry.Groups.Single(group => group.GroupId == "new")
                .Members.Single().Bounds.Y);
            session.SetDisplayed(slot);
        }
    }

    [Fact]
    public void EmptyLaneRequiresTitleSpaceAndDoesNotShiftTheSnappedMember()
    {
        var document = Document(Moving(2, 2, false));
        var session = Session(document);
        var invalid = session.Resolve(session.Snap(Rect(2, 3, 0, 2, 2)));
        Assert.True(invalid.TargetBounds.Row < 0);
        Assert.False(WallLayoutEngine.TryApply(document, invalid, 2000, 2000).Success);
        foreach (var globalRow in new[] { 1, 3 })
        {
            var action = session.Resolve(session.Snap(Rect(2, 3, globalRow, 2, 2)));
            Assert.Equal(globalRow - 1, action.TargetBounds.Row);
            var preview = Apply(document, action);
            Assert.Equal(globalRow * Metrics.Pitch, preview.Geometry.Groups.Single(group => group.GroupId == "new")
                .Members.Single().Bounds.Y);
        }
    }

    [Fact]
    public void AppendingOwnsTheFollowingTitleRowAndPushesTheWholeFollowingGroup()
    {
        var document = Document(Moving(3, 3, true)) with
        {
            Lanes = [new WallLane { Groups = [Group("target", "one"), Group("following", "two")] },
                new WallLane { Groups = [new StartGroup { Id = "source", Members = [Moving(3, 3, true)] }] }],
        };
        var original = WallLayoutEngine.Measure(document);
        var session = Session(document);
        var action = session.Resolve(session.Snap(Rect(0, 4, 2, 3, 3)));
        Assert.Equal("target", action.TargetGroupId);
        Assert.Equal(1, action.TargetBounds.Row);
        var preview = Apply(document, action);
        Assert.Equal(["target", "following"], preview.Document.Lanes[0].Groups.Select(group => group.Id));
        Assert.Equal(original.Groups.Single(group => group.GroupId == "following").Bounds.Y + 3 * Metrics.Pitch,
            preview.Geometry.Groups.Single(group => group.GroupId == "following").Bounds.Y);
    }

    [Fact]
    public void FullWidthGroupOwnershipProtectsInternalEmptyRowsAndLateralMembers()
    {
        var document = Document(Moving(2, 2, false)) with
        {
            Lanes = [new WallLane { Groups = [new StartGroup { Id = "target", Members =
                [new TileMember { Id = "top", LocalBounds = new GridRect(6, 0, 1, 1) },
                 new TileMember { Id = "bottom", LocalBounds = new GridRect(6, 4, 1, 1) }] }] },
                new WallLane { Groups = [new StartGroup { Id = "source", Members = [Moving(2, 2, false)] }] }],
        };
        var session = Session(document);
        foreach (var row in new[] { 2, 3, 5, 6 })
        {
            var action = session.Resolve(session.Snap(Rect(0, 0, row, 2, 2)));
            Assert.Equal("target", action.TargetGroupId);
            Assert.Equal(row - 1, action.TargetBounds.Row);
        }
        Assert.Null(session.Resolve(session.Snap(Rect(0, 0, 7, 2, 2))).TargetGroupId);
    }

    [Fact]
    public void NewGroupFootprintCannotOverlapLaterMembersEvenWhenItsTopIsInAFormerSourceGap()
    {
        var moving = Moving(3, 3, true);
        var document = Document(moving) with
        {
            Lanes = [new WallLane { Groups = [new StartGroup { Id = "source", Members = [moving] },
                new StartGroup { Id = "target", Members =
                    [new TileMember { Id = "stays", LocalBounds = new GridRect(7, 0, 1, 1) }] }] },
                new WallLane { Groups = [] }],
        };
        var session = Session(document);
        var action = session.Resolve(session.Snap(Rect(0, 0, 3, 3, 3)));
        Assert.Equal("target", action.TargetGroupId);
        Assert.Equal(0, action.TargetBounds.Row);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SourceOccupancyExcludesTheMovingMember(bool onlyMember)
    {
        var moving = new TileMember { Id = "moving", LocalBounds = new GridRect(0, onlyMember ? 0 : 4, 1, 1) };
        var document = Document(moving) with
        {
            Lanes = [new WallLane { Groups = [new StartGroup { Id = "source", Members = onlyMember
                ? [moving] : [new TileMember { Id = "stays", LocalBounds = new GridRect(0, 0, 1, 1) }, moving] }] },
                new WallLane { Groups = [] }],
        };
        var session = Session(document);
        var firstFree = onlyMember ? 1 : 2;
        var append = session.Resolve(session.Snap(Rect(0, 0, firstFree, 1, 1)));
        Assert.Equal("source", append.TargetGroupId);
        Assert.Equal(onlyMember ? 0 : 1, append.TargetBounds.Row);
        var created = session.Resolve(session.Snap(Rect(0, 0, firstFree + 1, 1, 1)));
        Assert.Null(created.TargetGroupId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReturningToTheOriginalGridSlotKeepsSourceIdentityAndLocalBounds(bool onlyMember)
    {
        var moving = new TileMember { Id = "moving", LocalBounds = new GridRect(2, 4, 1, 1) };
        var document = Document(moving) with
        {
            Lanes = [new WallLane { Groups = [new StartGroup { Id = "source", Members = onlyMember
                ? [moving] : [new TileMember { Id = "stays", LocalBounds = new GridRect(5, 0, 1, 1) }, moving] }] },
                new WallLane { Groups = [] }],
        };
        var session = Session(document);
        var original = session.Snap(Rect(0, 2, 5, 1, 1));
        Assert.Equal("source", session.Resolve(original).TargetGroupId);
        var away = session.Snap(Rect(0, 2, 8, 1, 1));
        Assert.Null(session.Resolve(away).TargetGroupId);
        session.SetDisplayed(away);
        var returned = session.Resolve(session.Snap(Rect(0, 2, 5, 1, 1)));
        Assert.Equal("source", returned.TargetGroupId);
        Assert.Equal(moving.LocalBounds, returned.TargetBounds);
        var preview = Apply(document, returned);
        Assert.Equal("source", Assert.Single(preview.Document.Lanes[0].Groups).Id);
        Assert.Equal(moving.LocalBounds, preview.Document.Lanes[0].Groups[0].Members
            .Single(member => member.Id == "moving").LocalBounds);
    }

    [Fact]
    public void RemovingSourceAboveTargetKeepsOriginalGridReferenceWithoutArtificialEmptyRows()
    {
        var document = Document(Moving(1, 1, false)) with
        {
            Lanes = [new WallLane { Groups = [Group("source", "moving"), Group("target", "stays")] },
                new WallLane { Groups = [] }],
        };
        var session = Session(document);
        var slot = session.Snap(Rect(0, 1, 3, 1, 1));
        var action = session.Resolve(slot);
        Assert.Equal("target", action.TargetGroupId);
        Assert.Equal(0, action.TargetBounds.Row);
        var preview = Apply(document, action);
        Assert.Single(preview.Document.Lanes[0].Groups);
        Assert.All(preview.Document.Lanes[0].Groups[0].Members, member => Assert.Equal(0, member.LocalBounds.Row));
        session.SetDisplayed(slot);
        Assert.Equal(action, session.Resolve(session.Snap(Rect(0, 1, 3, 1, 1) with { Y = 3 * Metrics.Pitch + 1 })));
    }

    [Fact]
    public void ExpandedColumnsKeepOriginalLogicalLaneUntilTheFinalProposalNormalizesIt()
    {
        var document = Document(Moving(1, 1, false)) with
        {
            Columns = 3,
            Settings = new WallSettings { ExpandColumns = true },
            Lanes = [new WallLane { Groups = [Group("source", "moving")] },
                new WallLane { Groups = [Group("target", "one")] }, new WallLane { Groups = [] }],
        };
        var session = Session(document);
        var slot = session.Snap(Rect(1, 1, 1, 1, 1));
        var action = session.Resolve(slot);
        Assert.Equal("target", action.TargetGroupId);
        var preview = Apply(document, action);
        Assert.Equal(0, preview.Geometry.Groups.Single(group => group.GroupId == "target").LaneIndex);
        session.SetDisplayed(slot);
        Assert.Equal(action, session.Resolve(session.Snap(Rect(1, 1, 1, 1, 1) with
            { X = Rect(1, 1, 1, 1, 1).X + 1 })));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void PuzzleGridBoundaryKeepsSmallMotionStableAndStillGrowsAndShrinks(int rows)
    {
        var document = PuzzleDocument(rows);
        var session = Session(document);
        var boundary = 4.5 * Metrics.Pitch;
        foreach (var (top, expectedRow) in new[]
        {
            (4 * Metrics.Pitch, 3), (boundary - 1, 3), (boundary + 1, 3), (boundary - 1, 3),
            (boundary + 2, 3), (boundary + Metrics.Gap + 1, 4),
            (boundary - 1, 4), (boundary + 1, 4), (boundary - Metrics.Gap - 1, 3),
        })
        {
            var slot = session.Snap(Rect(3, 5, 0, 3, 3) with { Y = top });
            Assert.Equal(expectedRow + 1, slot.Bounds.Row);
            var action = session.Resolve(slot);
            Assert.Equal("puzzles", action.TargetGroupId);
            Assert.Equal(new GridRect(5, expectedRow, 3, 3), action.TargetBounds);
            var preview = Apply(document, action);
            Assert.Equal(expectedRow == 3 ? 11 : rows, preview.Document.Rows);
            Assert.Equal(expectedRow == 3 ? 6 : 0, preview.Document.Lanes[3].Groups[0].Members
                .Single(member => member.Id == "large").LocalBounds.Row);
            session.SetDisplayed(slot);
        }
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void TallPuzzleMovesThroughEachGridRowAndCreatesOnlyWithATitleRow(int rows)
    {
        var document = PuzzleDocument(rows);
        var session = Session(document);
        foreach (var globalRow in new[] { 6, 7, 8, 9, 8, 7 })
        {
            var slot = session.Snap(Rect(3, 5, globalRow, 3, 3));
            var action = session.Resolve(slot);
            Assert.Equal(globalRow == 9 ? null : "puzzles", action.TargetGroupId);
            Assert.Equal(globalRow == 9 ? 0 : globalRow - 1, action.TargetBounds.Row);
            Apply(document, action);
            session.SetDisplayed(slot);
        }
    }

    [Fact]
    public void RejectedOrDifferentLaneSamplesDoNotInheritRowHysteresis()
    {
        var session = Session(PuzzleDocument(10));
        var acquired = session.Snap(Rect(3, 5, 4, 3, 3));
        session.SetDisplayed(acquired);
        var nearBoundary = Rect(3, 5, 0, 3, 3) with { Y = 4.5 * Metrics.Pitch + 1 };
        Assert.Equal(4, session.Snap(nearBoundary).Bounds.Row);
        Assert.Equal(5, session.Snap(nearBoundary with { X = Rect(2, 5, 0, 3, 3).X }).Bounds.Row);
        session.SetDisplayed(null);
        Assert.Equal(5, session.Snap(nearBoundary).Bounds.Row);
    }

    private static WallMemberDropSession Session(WallDocument document) => new(document, "moving", "new", Metrics);

    private static DipRect Rect(int lane, int column, int row, int width, int height) => new(
        Metrics.Margin + lane * (Metrics.LaneWidth + Metrics.ColumnGap) + column * Metrics.Pitch,
        Metrics.TopMargin + row * Metrics.Pitch, Metrics.WidthOf(width), Metrics.HeightOf(height));

    private static IEnumerable<DipPoint> Grabs(DipRect rect) =>
    [new(1, 1), new(rect.Width - 1, 1), new(1, rect.Height - 1),
        new(rect.Width - 1, rect.Height - 1), new(rect.Width / 2, rect.Height / 2)];

    private static WallMember Moving(int width, int height, bool puzzle) => puzzle
        ? new PuzzleMember { Id = "moving", LocalBounds = new GridRect(0, 0, width, height),
            Partitions = PartitionLayout.FullGrid(new GridSize(width, height)).Partitions }
        : new TileMember { Id = "moving", LocalBounds = new GridRect(0, 0, width, height) };

    private static WallProposal Apply(WallDocument document, MoveMemberAction action)
    {
        var result = WallLayoutEngine.TryApply(document, action, 4000, 4000);
        Assert.True(result.Success, result.Detail);
        return result.Proposal!;
    }

    private static WallDocument Document(WallMember moving) => new()
    {
        Columns = 2,
        Rows = 24,
        Lanes = [new WallLane { Groups = [Group("target", "one")] },
            new WallLane { Groups = [new StartGroup { Id = "source", Members = [moving] }] }],
    };

    private static StartGroup Group(string id, string memberId) => new()
    {
        Id = id,
        Members = [new TileMember { Id = memberId, LocalBounds = new GridRect(0, 0, 1, 1) }],
    };

    private static WallDocument PuzzleDocument(int rows) => new()
    {
        Columns = 4,
        Rows = rows,
        Settings = new WallSettings { AnchorCorner = AnchorCorner.BottomLeft },
        Lanes = [new WallLane { Groups = [] }, new WallLane { Groups = [] }, new WallLane { Groups = [] },
            new WallLane { Groups = [new StartGroup { Id = "puzzles", Members =
                [new PuzzleMember { Id = "moving", LocalBounds = new GridRect(5, 0, 3, 3),
                    Partitions = [new GridRect(0, 0, 2, 1), new GridRect(2, 0, 1, 2),
                        new GridRect(0, 1, 1, 2), new GridRect(1, 1, 1, 1), new GridRect(1, 2, 2, 1)] },
                 new PuzzleMember { Id = "large", LocalBounds = new GridRect(2, 3, 6, 4),
                    Partitions = [new GridRect(0, 0, 2, 2), new GridRect(2, 0, 2, 2), new GridRect(4, 0, 2, 2),
                        new GridRect(0, 2, 2, 2), new GridRect(2, 2, 2, 2), new GridRect(4, 2, 2, 2)] }] }] }],
    };
}
