using TileWall.Core.Application;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Import;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Application;

public sealed class StartMenuImportPreparationTests
{
    // A controlled delegate queue uses an STA on Windows, with no real COM or source files.
    private sealed class Executor : IWallStorageExecutor
    {
        private readonly Queue<Action> _jobs = [];
        public int Count => _jobs.Count;
        public Task<T> ExecuteAsync<T>(Func<T> work)
        {
            var completed = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _jobs.Enqueue(() =>
            {
                try { completed.TrySetResult(work()); }
                catch (Exception ex) { completed.TrySetException(ex); }
            });
            return completed.Task;
        }
        public Task RunNextAsync()
        {
            var job = _jobs.Dequeue();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { job(); completed.TrySetResult(); }
                catch (Exception ex) { completed.TrySetException(ex); }
            }) { IsBackground = true };
            if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completed.Task;
        }
        public Task ShutdownAsync() => Task.CompletedTask;
    }

    private sealed class Links(Func<string, LinkFields> read) : ILnkFileService
    {
        public int Reads;
        public LinkFields Read(string path) { Interlocked.Increment(ref Reads); return read(path); }
        public void Create(string path, string target, string arguments, string workingDirectory) => throw new NotSupportedException();
        public void EditTargetOnly(string path, string newTarget) => throw new NotSupportedException();
    }

    private static string Root() => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "import-preparation-" + Guid.NewGuid().ToString("N")));
    private static LinkFields Target(string path) => new(path, string.Empty, string.Empty, null, 0, 1, false);
    private static StartMenuCandidate Candidate(string path) => new(path, Path.GetFileNameWithoutExtension(path), EntryKind.Lnk, SourceKind.User, string.Empty);
    private static TileMember Member(string input) => new() { Id = "existing", TargetInput = input, LocalBounds = new GridRect(0, 0, 1, 1) };
    private static WallDocument Document(IReadOnlyList<WallMember> members) => WallDocument.Empty(1, 4) with
    {
        Lanes = [new WallLane { Groups = [new StartGroup { Id = "group", Members = members }] }],
    };
    private static StartMenuPreparationInput Input(string root, Links links, IReadOnlyList<StartMenuCandidate> candidates,
        WallDocument? document = null) => StartMenuImportPreparation.Capture(document ?? WallDocument.Empty(1, 4),
            new(candidates, 0), root, new(new InMemoryFileStore(), links), new("invalid-target", "unsupported-protocol"), static _ => true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationBeforeQueueOrBeforeWorkerStartsDoesNotRead(bool beforeQueue)
    {
        var root = Root();
        var links = new Links(_ => Target(Path.Combine(root, "app.exe")));
        var executor = new Executor();
        using var cancellation = new CancellationTokenSource();
        if (beforeQueue) cancellation.Cancel();
        var operation = StartMenuImportPreparation.PrepareAsync(executor,
            Input(root, links, [Candidate(Path.Combine(root, "app.lnk"))]), cancellation.Token);
        Assert.Equal(beforeQueue ? 0 : 1, executor.Count);
        if (!beforeQueue)
        {
            cancellation.Cancel();
            await executor.RunNextAsync();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(0, links.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterSynchronousReadStopsRemainingWorkAndNextRequestStillWorks(bool existingMember)
    {
        var root = Root();
        var first = Path.Combine(root, "a.lnk");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var block = true;
        var links = new Links(path =>
        {
            if (OperatingSystem.IsWindows()) Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            if (block)
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The controlled read was not released.");
            }
            return Target(Path.Combine(root, Path.GetFileNameWithoutExtension(path) + ".exe"));
        });
        var executor = new Executor();
        var input = Input(root, links, [Candidate(first), Candidate(Path.Combine(root, "b.lnk"))],
            existingMember ? Document([Member(first)]) : null);
        using var cancellation = new CancellationTokenSource();
        var operation = StartMenuImportPreparation.PrepareAsync(executor, input, cancellation.Token);
        var running = executor.RunNextAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
        }
        finally { release.Set(); }
        await running;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(1, links.Reads); // No later member/candidate accepts the cancelled IO tail.

        block = false;
        var next = StartMenuImportPreparation.PrepareAsync(executor, input, CancellationToken.None);
        await executor.RunNextAsync();
        var candidates = await next;
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.False(candidate.ReadFailed));
        Assert.Equal(existingMember, candidates[0].AlreadyPresent);
    }

    [Fact]
    public async Task QueuedPreparationUsesFrozenDocumentCandidatesAndReasonLists()
    {
        var root = Root();
        var path = Path.Combine(root, "app.lnk");
        var members = new List<WallMember> { Member(path) };
        var reasons = new List<string> { "retained-reason" };
        var candidates = new List<StartMenuCandidate> { Candidate(path) with { NotImportableReasons = reasons } };
        var links = new Links(_ => Target(Path.Combine(root, "app.exe")));
        var executor = new Executor();
        var operation = StartMenuImportPreparation.PrepareAsync(executor,
            Input(root, links, candidates, Document(members)), CancellationToken.None);
        members.Clear(); reasons.Clear(); candidates.Clear();
        await executor.RunNextAsync();
        var prepared = Assert.Single(await operation);
        Assert.True(prepared.AlreadyPresent);
        Assert.False(prepared.Candidate.IsImportable);
        Assert.Equal("retained-reason", Assert.Single(prepared.Candidate.NotImportableReasons));
    }

    [Fact]
    public async Task ReadFailureRemainsIncompleteWithoutDiscardingOtherCandidates()
    {
        var root = Root();
        var links = new Links(path => Path.GetFileName(path) == "a.lnk"
            ? throw new IOException("controlled-read-failure") : Target(Path.Combine(root, "app.exe")));
        var executor = new Executor();
        var operation = StartMenuImportPreparation.PrepareAsync(executor,
            Input(root, links, [Candidate(Path.Combine(root, "a.lnk")), Candidate(Path.Combine(root, "b.lnk"))]),
            CancellationToken.None);
        await executor.RunNextAsync();
        var candidates = await operation;
        Assert.Equal(2, candidates.Count);
        Assert.True(candidates[0].ReadFailed);
        Assert.False(candidates[0].CanAdd);
        Assert.Equal("controlled-read-failure", candidates[0].Error);
        Assert.False(candidates[1].ReadFailed);
        Assert.True(candidates[1].CanAdd);
    }
}
