using System.Threading.Channels;
using TileWall.ApplicationModel;
using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Application;

public sealed class WallSessionAsyncTests
{
    private sealed class Executor : IWallStorageExecutor
    {
        private readonly Queue<Action> _work = [];
        public int Count => _work.Count;
        public Task<T> ExecuteAsync<T>(Func<T> work)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Enqueue(() =>
            {
                try { completion.SetResult(work()); }
                catch (Exception ex) { completion.SetException(ex); }
            });
            return completion.Task;
        }
        public Task RunNextAsync() => Task.Run(_work.Dequeue());
        public Task ShutdownAsync() => Task.CompletedTask;
    }

    private sealed class AcceptanceQueue
    {
        private readonly Channel<(Action Apply, TaskCompletionSource Completion)> _queue =
            Channel.CreateUnbounded<(Action, TaskCompletionSource)>();
        public Task AcceptAsync(Action apply)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(_queue.Writer.TryWrite((apply, completion)));
            return completion.Task;
        }
        public async Task<Action> TakeAsync()
        {
            var item = await _queue.Reader.ReadAsync();
            return () =>
            {
                try { item.Apply(); item.Completion.SetResult(); }
                catch (Exception ex) { item.Completion.SetException(ex); }
            };
        }
    }

    private static (WallSession Session, InMemoryFileStore Files, string Root, Executor Executor,
        AcceptanceQueue Acceptance) Harness()
    {
        var files = new InMemoryFileStore();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wall-session-async-" + Guid.NewGuid().ToString("N")));
        var session = new WallSession(root, files, new FakeLinkFileService(files));
        session.Open(1, 6);
        var executor = new Executor();
        var acceptance = new AcceptanceQueue();
        session.ConfigureAsyncExecution(executor, acceptance.AcceptAsync);
        return (session, files, root, executor, acceptance);
    }

    private static WallProposal Add(WallSession session) => session.Propose(
        new AddMemberAction(session.Current.Generation, new TileMember
        {
            Id = "tile1", LocalBounds = new GridRect(0, 0, 1, 1),
            Visual = new MemberVisual { Title = "First" },
        }, null, 0, 0, "group1"), 500, 500).Proposal!;

    [Fact]
    public async Task DiskCommitDoesNotPublishBeforeUiAcceptanceAndKeepsSingleWriterReserved()
    {
        var (session, files, root, executor, acceptance) = Harness();
        var proposal = Add(session);
        var before = session.Current;
        WallDocument? refreshed = null;
        var refreshThread = 0;
        session.SetViewRefresh(document => { refreshed = document; refreshThread = Environment.CurrentManagedThreadId; });
        var operation = session.CommitAsync(proposal);
        Assert.True(session.WritePending);
        Assert.False(operation.IsCompleted);
        Assert.Same(before, session.Current);
        Assert.False(session.Commit(proposal).Committed);
        Assert.False((await session.CommitAsync(proposal)).Committed);
        Assert.False(session.Undo().Committed);
        Assert.Null(session.Propose(new RemoveMemberAction(0, "tile1"), 500, 500).Proposal);
        Assert.Equal(1, executor.Count);

        await executor.RunNextAsync();
        var apply = await acceptance.TakeAsync();
        Assert.Equal(1, WallJson.Deserialize(files.ReadAllBytes(Path.Combine(root, "config.json"))).Generation);
        Assert.Same(before, session.Current);
        Assert.Null(refreshed);
        Assert.True(session.WritePending);
        Assert.False(operation.IsCompleted); // Exit must drain through UI readback, not just disk replacement.
        var acceptanceThread = Environment.CurrentManagedThreadId;
        apply();
        var result = await operation;
        Assert.True(result.Committed);
        Assert.False(session.WritePending);
        Assert.Same(session.Current, refreshed);
        Assert.Equal(acceptanceThread, refreshThread);
        Assert.True(session.CanUndo);
        Assert.True(session.Undo().Committed);
        Assert.Empty(session.Current.Lanes[0].Groups);
    }

    [Fact]
    public async Task FailedTransactionKeepsCommittedSnapshotAndExistingUndo()
    {
        var (session, files, root, executor, acceptance) = Harness();
        Assert.True(session.Commit(Add(session)).Committed);
        var before = session.Current;
        var tile = (TileMember)before.Lanes[0].Groups[0].Members[0];
        var proposal = session.Propose(new ReplaceMemberAction(before.Generation, tile.Id,
            tile with { Visual = tile.Visual with { Title = "Second" } }), 500, 500).Proposal!;
        files.InjectedFault = FaultPoint.BeforeMove;
        var operation = session.CommitAsync(proposal);
        await executor.RunNextAsync();
        (await acceptance.TakeAsync())();
        var result = await operation;
        Assert.False(result.Committed);
        Assert.False(session.WritePending);
        Assert.Same(before, session.Current);
        Assert.True(session.CanUndo);
        Assert.Equal(before.Generation, WallJson.Deserialize(files.ReadAllBytes(Path.Combine(root, "config.json"))).Generation);
        files.InjectedFault = FaultPoint.None;
        Assert.True(session.Undo().Committed);
    }

    [Fact]
    public async Task ViewFailureRetainsSuccessfulDiskCommitAndCanRebuild()
    {
        var (session, files, root, executor, acceptance) = Harness();
        session.SetViewRefresh(_ => throw new InvalidOperationException("render failed"));
        var operation = session.CommitAsync(Add(session));
        await executor.RunNextAsync();
        (await acceptance.TakeAsync())();
        var result = await operation;
        Assert.True(result.Committed);
        Assert.True(result.ViewFailed);
        Assert.True(session.ViewBlocked);
        Assert.True(session.CanUndo);
        Assert.Equal(1, session.Current.Generation);
        Assert.Equal(1, WallJson.Deserialize(files.ReadAllBytes(Path.Combine(root, "config.json"))).Generation);
        Assert.False((await session.CommitAsync(Add(session))).Committed);
        session.SetViewRefresh(_ => { });
        Assert.True(session.RefreshViewFromCurrent());
        Assert.True(session.Undo().Committed);
    }

    [Fact]
    public async Task ReservationFreezesCallerOwnedDocumentAndEntryChangeCollection()
    {
        var (session, files, _, executor, acceptance) = Harness();
        var proposal = Add(session);
        var lanes = proposal.Document.Lanes.ToArray();
        proposal = proposal with { Document = proposal.Document with { Lanes = lanes } };
        EntryChange[] changes = [new EntryChange.CreateUrl("tile1", "https://example.com")];
        var operation = session.CommitAsync(proposal, changes);
        lanes[0] = new WallLane { Groups = [] };
        changes[0] = new EntryChange.CreateUrl("tile1", "https://changed.example");
        await executor.RunNextAsync();
        (await acceptance.TakeAsync())();
        Assert.True((await operation).Committed);
        var tile = Assert.IsType<TileMember>(Assert.Single(Assert.Single(session.Current.Lanes[0].Groups).Members));
        Assert.NotNull(tile.Entry);
        Assert.Contains("URL=https://example.com", System.Text.Encoding.UTF8.GetString(
            files.ReadAllBytes(session.ManagedEntryPath(tile)!)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LostDispatcherNeverPublishesCommittedStateFromWorkerOrReportsSuccess()
    {
        var (session, files, root, executor, _) = Harness();
        session.ConfigureAsyncExecution(executor, _ => Task.FromException(new InvalidOperationException("dispatcher closed")));
        var before = session.Current;
        var operation = session.CommitAsync(Add(session));
        await executor.RunNextAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await operation);
        Assert.Same(before, session.Current);
        Assert.True(session.ViewBlocked);
        Assert.False(session.WritePending);
        Assert.Equal(1, WallJson.Deserialize(files.ReadAllBytes(Path.Combine(root, "config.json"))).Generation);
    }
}
