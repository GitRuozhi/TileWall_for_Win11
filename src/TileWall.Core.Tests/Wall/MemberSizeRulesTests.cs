using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Wall;

public sealed class MemberSizeRulesTests
{
    [Theory]
    [InlineData(4, 4, true)]
    [InlineData(5, 4, false)]
    [InlineData(4, 5, false)]
    [InlineData(1, 1, true)]
    public void TileBoundaries(int width, int height, bool valid) => Assert.Equal(valid, MemberSizeRules.IsTileSize(width, height));

    [Fact]
    public void PuzzleTwelveRowsAndOneCellArePermitted()
    {
        Assert.True(MemberSizeRules.IsPuzzleSize(8, 12));
        Assert.True(MemberSizeRules.IsPuzzleSize(1, 1));
        Assert.False(MemberSizeRules.IsPuzzleSize(8, 13));
        var initial = PartitionLayout.FullGrid(new GridSize(7, 11));
        var session = new PartitionEditSession(initial.Size, initial.Partitions);
        Assert.True(session.AddRow()); Assert.True(session.AddColumn());
        Assert.False(session.AddRow()); Assert.False(session.AddColumn());
        Assert.True(session.Current.Check().IsValid);
        Assert.All(session.Current.Partitions, p => Assert.True(MemberSizeRules.IsPartitionSize(p.Width, p.Height)));
    }

    [Fact]
    public void OversizeEraseKeepsClosureFeedbackAndDoesNotCreateUndo()
    {
        var session = new PartitionEditSession(new GridSize(5, 2), [new(0, 0, 3, 2), new(3, 0, 2, 1), new(3, 1, 2, 1)]);
        var before = session.Current;
        var edge = new WallEdge(true, 3, 0);
        var preview = session.Preview(edge, EditTool.Erase);
        Assert.False(preview.Applicable); Assert.Equal(StrokeOutcome.SizeLimit, preview.Outcome);
        Assert.Equal(new GridRect(0, 0, 5, 2), preview.MergeExtent);
        Assert.Equal(4, preview.AffectedEdges.Count); Assert.Null(preview.Layout);
        session.BeginStroke(); Assert.Equal(StrokeOutcome.SizeLimit, session.Stroke(edge, EditTool.Erase));
        Assert.False(session.EndStroke()); Assert.Equal(0, session.UndoDepth); Assert.Equal(before, session.Current);
        Assert.Equal(PartitionOpStatus.SizeLimit, PartitionOps.EraseWall(before, edge).Status);
    }

    [Fact]
    public void FillRelativeCustomScaleIsSharedAcrossDifferentAspectRatios()
    {
        foreach (var pixels in new[] { (Width: 400d, Height: 100d), (Width: 100d, Height: 400d) })
        {
            var fill = MemberImageGeometry.Calculate(100, 200, pixels.Width, pixels.Height, 1, new() { Mode = ImageDisplayMode.Fill });
            var custom = MemberImageGeometry.Calculate(100, 200, pixels.Width, pixels.Height, 1, new() { Mode = ImageDisplayMode.Custom, CustomScale = 1 });
            Assert.Equal(fill, custom);
            Assert.Equal(1, MemberImageGeometry.CustomScaleFromImageScale(fill.Scale, 100, 200, pixels.Width, pixels.Height));
            Assert.Equal((100 - custom.Bounds.Width) / 2, custom.Bounds.X);
            Assert.Equal((200 - custom.Bounds.Height) / 2, custom.Bounds.Y);
        }
    }
}
