using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Storage;

public sealed partial class WallStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpandedCommitRejectsMissingTailWithoutWritingConfiguration(bool fullScreen)
    {
        var (store, files, _, root) = Harness();
        var initial = WallDocument.Empty(1, 4) with
        {
            Settings = new() { FullScreen = fullScreen, ExpandColumns = true },
        };
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var saved = files.ReadAllBytes(Path.Combine(root, "config.json"));
        var invalid = initial with
        {
            Generation = 1,
            Lanes = [new() { Groups = [new() { Id = "group", Members = [Tile("one")] }] }],
        };
        var planned = store.PlanEntries(initial, invalid, []);
        Assert.Null(planned.Request);
        Assert.Contains("EXPANDED_TAIL_LANE", planned.Diagnostics);
        Assert.Equal(saved, files.ReadAllBytes(Path.Combine(root, "config.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpandedModeAndVisibleWidthsSurviveCommitAndReopen(bool fullScreen)
    {
        var (store, files, links, root) = Harness();
        var initial = WallDocument.Empty(1, 4) with
        {
            Settings = new()
            {
                FullScreen = fullScreen, ExpandColumns = true, AnchorCorner = AnchorCorner.TopRight,
                WindowedViewportWidthDip = 1728, ManualWindowedViewportWidthDip = 1024.5,
            },
        };
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var candidate = WallLayoutEngine.TryApply(initial, new AddMemberAction(0,
            Tile("one"), null, 0, 0, "group"), 30000, 2000);
        Assert.True(candidate.Success, candidate.Detail);
        var result = store.Commit(initial, Plan(store, initial, candidate.Proposal!.Document), true);
        Assert.Equal(WallCommitStatus.Committed, result.Status);
        var reopened = new WallStore(files, links, root).Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Equal(initial.Settings, reopened.Document!.Settings);
        Assert.Equal(2, reopened.Document.Columns);
        Assert.Single(reopened.Document.Lanes[0].Groups);
        Assert.Empty(reopened.Document.Lanes[1].Groups);
    }
}
