using System.Text;
using TileWall.ApplicationModel;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Application;

public sealed class WallSessionTests
{
    private static (WallSession Session, InMemoryFileStore Files, string Root) Harness()
    {
        var files = new InMemoryFileStore();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "wall-session-" + Guid.NewGuid().ToString("N")));
        return (new WallSession(root, files, new FakeLinkFileService(files)), files, root);
    }

    private static WallLayoutResult Add(WallSession session) => session.Propose(
        new AddMemberAction(session.Current.Generation, new TileMember
        {
            Id = "tile1", LocalBounds = new GridRect(0, 0, 1, 1),
            Visual = new MemberVisual { Title = "First" },
        }, null, 0, 0, "group1"), 500, 500);

    [Fact]
    public void FreshCommitFailureKeepsCurrentThenUndoRestoresLayoutAndEntry()
    {
        var (session, files, _) = Harness();
        Assert.Equal(WallOpenStatus.Loaded, session.Open(1, 6).Status);
        Assert.Equal(0, session.Current.Generation);
        var added = Add(session);
        Assert.NotNull(added.Proposal);
        var committed = session.Commit(added.Proposal!,
            [new EntryChange.CreateUrl("tile1", "https://example.com")]);
        Assert.True(committed.Committed, string.Join("; ", committed.Diagnostics));
        Assert.Equal(1, session.Current.Generation);
        Assert.NotNull(session.Current.Lanes[0].Groups[0].Members[0].Entry);

        var tile = (TileMember)session.Current.Lanes[0].Groups[0].Members[0];
        var proposed = session.Propose(new ReplaceMemberAction(1, "tile1", tile with
        {
            Visual = tile.Visual with { Title = "Second" }
        }), 500, 500);
        Assert.NotNull(proposed.Proposal);
        files.EntryFault = EntryFaultPoint.BeforeEntrySwap;
        var failed = session.Commit(proposed.Proposal!,
            [new EntryChange.ChangeTarget("tile1", "https://second.example"),
             new EntryChange.Rename("tile1")]);
        Assert.False(failed.Committed);
        Assert.Equal(1, session.Current.Generation);
        Assert.Equal("First", session.Current.Lanes[0].Groups[0].Members[0].Visual.Title);

        var undone = session.Undo();
        Assert.True(undone.Committed, string.Join("; ", undone.Diagnostics));
        Assert.Equal(2, session.Current.Generation);
        Assert.Empty(session.Current.Lanes[0].Groups);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void FutureDataBlocksWithoutCreatingEmptyWall()
    {
        var (session, files, root) = Harness();
        files.WriteAllBytes(Path.Combine(root, "config.json"),
            Encoding.UTF8.GetBytes("{\"schemaVersion\":3}"));
        var result = session.Open(1, 6);
        Assert.Equal(WallOpenStatus.FutureVersionBlocked, result.Status);
        Assert.Throws<InvalidOperationException>(() => session.Current);
    }

    [Fact]
    public void CommittedDiskStateSurvivesViewFailureAndBlocksStaleViewCommandsUntilRebuilt()
    {
        var (session, files, root) = Harness();
        session.Open(1, 6);
        session.SetViewRefresh(_ => throw new InvalidOperationException("render failed"));
        var added = Add(session);
        var result = session.Commit(added.Proposal!);
        Assert.True(result.Committed);
        Assert.True(result.ViewFailed);
        Assert.True(session.ViewBlocked);
        Assert.Equal(1, session.Current.Generation);
        Assert.Equal(1, WallJson.Deserialize(files.ReadAllBytes(Path.Combine(root, "config.json"))).Generation);
        Assert.False(session.Commit(added.Proposal!).Committed);

        WallDocument? rendered = null;
        session.SetViewRefresh(document => rendered = document);
        Assert.True(session.RefreshViewFromCurrent());
        Assert.False(session.ViewBlocked);
        Assert.Same(session.Current, rendered);
        var removed = session.Propose(new RemoveMemberAction(1, "tile1"), 500, 500);
        Assert.True(session.Commit(removed.Proposal!).Committed);
        Assert.Equal(2, session.Current.Generation);
    }

    [Fact]
    public void FailedUndoRetainsSlotAndCanRetryToCurrentDiskGeneration()
    {
        var (session, files, root) = Harness();
        session.Open(1, 6);
        session.SetViewRefresh(_ => { });
        var added = Add(session);
        Assert.True(session.Commit(added.Proposal!,
            [new EntryChange.CreateUrl("tile1", "https://example.com")]).Committed);
        files.InjectedFault = FaultPoint.BeforeMove;
        var failed = session.Undo();
        Assert.False(failed.Committed);
        Assert.True(session.CanUndo);
        Assert.Equal(1, session.Current.Generation);

        files.InjectedFault = FaultPoint.None;
        var retried = session.Undo();
        Assert.True(retried.Committed, string.Join("; ", retried.Diagnostics));
        Assert.False(session.CanUndo);
        Assert.Equal(2, session.Current.Generation);
        var disk = WallJson.Deserialize(files.ReadAllBytes(Path.Combine(root, "config.json")));
        Assert.Equal(session.Current.Generation, disk.Generation);
        Assert.Empty(disk.Lanes[0].Groups);
    }
}
