using TileWall.Core.Application;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Application;

public sealed class WallSessionViewportUndoTests
{
    [Fact]
    public void UndoChecksRestoredLayoutWithCurrentSettingsAndKeepsItsTokenWhenViewportRejectsIt()
    {
        var files = new InMemoryFileStore();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wall-viewport-undo-" + Guid.NewGuid().ToString("N")));
        var session = new WallSession(root, files, new FakeLinkFileService(files));
        Assert.Equal(WallOpenStatus.Loaded, session.Open(5, 8).Status);
        var fullscreen = session.Current with
        {
            Generation = session.Current.Generation + 1,
            Settings = session.Current.Settings with { FullScreen = true },
        };
        Assert.True(session.Commit(new(fullscreen, WallLayoutEngine.Measure(fullscreen)), recordUndo: false).Committed);
        var shrink = session.Propose(new ResizeWallAction(session.Current.Generation, 1, 8), 3000, 2000);
        Assert.True(shrink.Success, shrink.Detail);
        var windowed = shrink.Proposal!.Document with { Settings = fullscreen.Settings with { FullScreen = false } };
        Assert.True(session.Commit(new(windowed, WallLayoutEngine.Measure(windowed))).Committed);
        var before = WallJson.Serialize(session.Current);
        var saved = files.ReadAllBytes(Path.Combine(root, "config.json"));
        var refreshed = false;
        session.SetViewRefresh(_ => refreshed = true);
        var rejected = session.Undo(candidate =>
        {
            Assert.False(candidate.Settings.FullScreen);
            Assert.Equal(5, candidate.Columns);
            var fit = WallViewportPolicy.Validate(candidate, WallViewportPolicy.Create(1920, 1080, false, expandColumns: false));
            return fit.Success ? null : "viewport-rejected";
        });
        Assert.False(rejected.Committed);
        Assert.Equal("viewport-rejected", Assert.Single(rejected.Diagnostics));
        Assert.True(session.CanUndo);
        Assert.False(refreshed);
        Assert.Equal(before, WallJson.Serialize(session.Current));
        Assert.Equal(saved, files.ReadAllBytes(Path.Combine(root, "config.json")));

        var reopenFull = session.Current with
        {
            Generation = session.Current.Generation + 1,
            Settings = session.Current.Settings with { FullScreen = true, ExpandColumns = true },
        };
        Assert.True(session.Commit(new(reopenFull, WallLayoutEngine.Measure(reopenFull)), recordUndo: false).Committed);
        var retried = session.Undo(candidate =>
        {
            var fit = WallViewportPolicy.Validate(candidate, WallViewportPolicy.Create(1920, 1080,
                candidate.Settings.FullScreen, expandColumns: candidate.Settings.ExpandColumns));
            return fit.Success ? null : "viewport-rejected";
        });
        Assert.True(retried.Committed, string.Join("; ", retried.Diagnostics));
        Assert.False(session.CanUndo);
        Assert.True(session.Current.Settings.FullScreen);
        Assert.True(session.Current.Settings.ExpandColumns);
        Assert.Equal(1, session.Current.Columns);
        Assert.Empty(Assert.Single(session.Current.Lanes).Groups);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UndoPreflightAndStorageUseTheSameNormalizedExpandedCandidate(bool fullScreen)
    {
        var files = new InMemoryFileStore();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wall-expanded-undo-" + Guid.NewGuid().ToString("N")));
        var session = new WallSession(root, files, new FakeLinkFileService(files));
        Assert.Equal(WallOpenStatus.Loaded, session.Open(3, 8).Status);
        foreach (var (id, lane) in new[] { ("a", 0), ("b", 2) })
        {
            var addition = session.Propose(new AddMemberAction(session.Current.Generation,
                new TileMember { Id = id, LocalBounds = new(0, 0, 1, 1) }, null, lane, 0, id + "-group"), 3000, 2000);
            Assert.True(addition.Success, addition.Detail);
            Assert.True(session.Commit(addition.Proposal!).Committed);
        }
        var expanded = WallLayoutEngine.NormalizeExpanded(session.Current with
        {
            Generation = session.Current.Generation + 1,
            Settings = session.Current.Settings with { FullScreen = fullScreen, ExpandColumns = true },
        });
        Assert.True(expanded.Success, expanded.Detail);
        Assert.True(session.Commit(expanded.Proposal!).Committed);
        byte[]? checkedCandidate = null;

        var result = session.Undo(candidate =>
        {
            Assert.True(candidate.Settings.ExpandColumns);
            Assert.Equal(3, candidate.Columns);
            Assert.All(candidate.Lanes.Take(2), lane => Assert.Single(lane.Groups));
            Assert.Empty(candidate.Lanes[2].Groups);
            checkedCandidate = WallJson.Serialize(candidate);
            return null;
        });

        Assert.True(result.Committed, string.Join("; ", result.Diagnostics));
        Assert.NotNull(checkedCandidate);
        Assert.Equal(checkedCandidate, WallJson.Serialize(session.Current));
        Assert.Equal(checkedCandidate, files.ReadAllBytes(Path.Combine(root, "config.json")));
    }
}
