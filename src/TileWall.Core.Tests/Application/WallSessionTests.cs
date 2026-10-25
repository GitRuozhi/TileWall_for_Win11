using System.Text;
using TileWall.Core.Application;
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
    private sealed class FaultingFiles(IFileStore inner) : IFileStore
    {
        public List<Func<string, string?, string?, bool>> Rules { get; } = [];

        private void Trip(string kind, string? source, string? destination)
        {
            for (var i = 0; i < Rules.Count; i++)
            {
                if (!Rules[i](kind, source, destination)) continue;
                Rules.RemoveAt(i);
                throw new IOException($"Injected {kind}: {source} -> {destination}");
            }
        }

        public FileStorePathInfo GetPathInfo(string path) => inner.GetPathInfo(path);
        public bool Exists(string path) => inner.Exists(path);
        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);
        public void WriteAllBytes(string path, byte[] bytes) { Trip("write", null, path); inner.WriteAllBytes(path, bytes); }
        public void Move(string source, string destination, bool overwrite)
        { Trip("move", source, destination); inner.Move(source, destination, overwrite); }
        public void Copy(string source, string destination, bool overwrite)
        { Trip("copy", source, destination); inner.Copy(source, destination, overwrite); }
        public void Delete(string path) { Trip("delete", path, null); inner.Delete(path); }
        public void CreateDirectory(string path) { Trip("mkdir", null, path); inner.CreateDirectory(path); }
        public IReadOnlyList<string> ListDirectories(string path) => inner.ListDirectories(path);
        public IReadOnlyList<string> ListFiles(string path) => inner.ListFiles(path);
        public void DeleteDirectory(string path) { Trip("rmdir", path, null); inner.DeleteDirectory(path); }
    }

    private static (WallSession Session, InMemoryFileStore Files, string Root) Harness()
    {
        var files = new InMemoryFileStore();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "wall-session-" + Guid.NewGuid().ToString("N")));
        return (new WallSession(root, files, new FakeLinkFileService(files)), files, root);
    }

    private static (WallSession Session, FaultingFiles Files, string Root) RecoveryHarness()
    {
        var files = new FaultingFiles(new InMemoryFileStore());
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "wall-session-recovery-" + Guid.NewGuid().ToString("N")));
        return (new WallSession(root, files, new FakeLinkFileService(files)), files, root);
    }

    private static WallLayoutResult Add(WallSession session) => session.Propose(
        new AddMemberAction(session.Current.Generation, new TileMember
        {
            Id = "tile1", LocalBounds = new GridRect(0, 0, 1, 1),
            Visual = new MemberVisual { Title = "First" },
        }, null, 0, 0, "group1"), 500, 500);

    [Fact]
    public void FreshCommitFailureKeepsCurrentLayoutAndEntry()
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
    }

    [Fact]
    public void FutureDataBlocksWithoutCreatingEmptyWall()
    {
        var (session, files, root) = Harness();
        files.WriteAllBytes(Path.Combine(root, "config.json"),
            Encoding.UTF8.GetBytes($"{{\"schemaVersion\":{WallDocument.CurrentSchemaVersion + 1}}}"));
        var result = session.Open(1, 6);
        Assert.Equal(WallOpenStatus.UnsupportedFormatBlocked, result.Status);
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
        Assert.False(session.RequiresReopen);
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
    public void RecoveryBlockedCommitBlocksEditsAndRefreshUntilOpenRecovers()
    {
        var (session, files, root) = RecoveryHarness();
        Assert.Equal(WallOpenStatus.Loaded, session.Open(1, 6).Status);
        var added = Add(session);
        Assert.True(session.Commit(added.Proposal!,
            [new EntryChange.CreateUrl("tile1", "https://old.example")]).Committed);
        var before = session.Current;
        var tile = (TileMember)before.Lanes[0].Groups[0].Members[0];
        var changed = session.Propose(new ReplaceMemberAction(before.Generation, tile.Id,
            tile with { Visual = tile.Visual with { Title = "Second" } }), 500, 500);
        Assert.NotNull(changed.Proposal);

        files.Rules.Add((kind, source, destination) => kind == "move" &&
            source?.EndsWith("entry-tile1.tmp", StringComparison.Ordinal) == true &&
            destination?.Contains(Path.DirectorySeparatorChar + "Objects" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) == true);
        files.Rules.Add((kind, source, destination) => kind == "copy" &&
            source?.Contains(Path.DirectorySeparatorChar + "Recovery" + Path.DirectorySeparatorChar + "Entries",
                StringComparison.Ordinal) == true &&
            destination?.Contains(Path.DirectorySeparatorChar + "Objects" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) == true);

        var blocked = session.Commit(changed.Proposal!,
            [new EntryChange.ChangeTarget("tile1", "https://new.example")]);
        Assert.False(blocked.Committed);
        Assert.Empty(files.Rules);
        Assert.Same(before, session.Current);
        Assert.True(session.ViewBlocked);
        Assert.False(session.RequiresReopen);

        var refreshCount = 0;
        session.SetViewRefresh(_ => refreshCount++);
        Assert.False(session.RefreshViewFromCurrent());
        Assert.Equal(0, refreshCount);
        Assert.Null(session.Propose(new RemoveMemberAction(before.Generation, "tile1"), 500, 500).Proposal);
        Assert.False(session.Commit(changed.Proposal!).Committed);

        var reopened = session.Open(1, 6);
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Equal(before.Generation, session.Current.Generation);
        Assert.False(session.ViewBlocked);
        Assert.False(session.RequiresReopen);
        Assert.True(session.RefreshViewFromCurrent());
        Assert.Equal(1, refreshCount);
        Assert.NotNull(session.Propose(new RemoveMemberAction(session.Current.Generation, "tile1"), 500, 500).Proposal);
        Assert.Equal(before.Generation, WallJson.Deserialize(files.ReadAllBytes(Path.Combine(root, "config.json"))).Generation);
    }

    [Fact]
    public void OpenFailedDoesNotLatchRecoveryOrUnlockAnExistingBlock()
    {
        var (session, files, root) = Harness();
        files.InjectedFault = FaultPoint.BeforeMove;
        var failed = session.Open(1, 6);
        Assert.Equal(WallOpenStatus.OpenFailed, failed.Status);
        Assert.Contains("IOException", string.Join(" ", failed.Diagnostics), StringComparison.Ordinal);
        Assert.False(session.RecoveryBlocked);
        Assert.False(session.RequiresReopen);
        Assert.Throws<InvalidOperationException>(() => session.Current);

        files.InjectedFault = FaultPoint.None;
        Assert.Equal(WallOpenStatus.Loaded, session.Open(1, 6).Status);
        var current = session.Current;
        files.Delete(Path.Combine(root, "config.json"));
        files.InjectedFault = FaultPoint.BeforeMove;
        var again = session.Open(1, 6);
        Assert.Equal(WallOpenStatus.OpenFailed, again.Status);
        Assert.False(session.RecoveryBlocked);
        Assert.True(session.ViewBlocked);
        Assert.Same(current, session.Current);
        Assert.Null(session.Propose(new RemoveMemberAction(current.Generation, "missing"), 500, 500).Proposal);

        var blocked = new WallSession(root + "-blocked", files, new FakeLinkFileService(files));
        files.WriteAllBytes(Path.Combine(root + "-blocked", "config.init.tmp"), Encoding.UTF8.GetBytes("{}"));
        Assert.Equal(WallOpenStatus.RecoveryBlocked, blocked.Open(1, 6).Status);
        Assert.True(blocked.RecoveryBlocked);
        files.Delete(Path.Combine(root + "-blocked", "config.init.tmp"));
        files.InjectedFault = FaultPoint.BeforeMove;
        Assert.Equal(WallOpenStatus.OpenFailed, blocked.Open(1, 6).Status);
        Assert.True(blocked.RecoveryBlocked);
        Assert.True(blocked.ViewBlocked);
        Assert.Throws<InvalidOperationException>(() => blocked.Current);
    }

    [Fact]
    public void NonStorageFailureLatchesUnknownResultUntilOpen()
    {
        var inner = new InMemoryFileStore();
        var files = new ThrowingFiles(inner);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wall-session-" + Guid.NewGuid().ToString("N")));
        var session = new WallSession(root, files, new FakeLinkFileService(inner));
        Assert.Equal(WallOpenStatus.Loaded, session.Open(1, 6).Status);
        var added = Add(session);
        files.ThrowOnWrite = true;
        var error = Assert.Throws<InvalidOperationException>(() => session.Commit(added.Proposal!));
        Assert.Equal("boom", error.Message);
        Assert.True(session.ResultUnknown);
        Assert.False(session.RequiresReopen);
        Assert.True(session.ViewBlocked);
        var refreshes = 0;
        session.SetViewRefresh(_ => refreshes++);
        Assert.False(session.RefreshViewFromCurrent());
        Assert.Equal(0, refreshes);
        Assert.True(session.ViewBlocked);
        Assert.True(session.ResultUnknown);
        Assert.Null(session.Propose(new RemoveMemberAction(session.Current.Generation, "tile1"), 500, 500).Proposal);
        Assert.False(session.Commit(added.Proposal!).Committed);
        files.ThrowOnWrite = false;
        Assert.Equal(WallOpenStatus.Loaded, session.Open(1, 6).Status);
        Assert.False(session.ResultUnknown);
        Assert.False(session.ViewBlocked);
    }

    private sealed class ThrowingFiles(IFileStore inner) : IFileStore
    {
        public bool ThrowOnWrite;
        public FileStorePathInfo GetPathInfo(string path) => inner.GetPathInfo(path);
        public bool Exists(string path) => inner.Exists(path);
        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);
        public void WriteAllBytes(string path, byte[] bytes)
        {
            if (ThrowOnWrite) throw new InvalidOperationException("boom");
            inner.WriteAllBytes(path, bytes);
        }
        public void Move(string source, string destination, bool overwrite) => inner.Move(source, destination, overwrite);
        public void Copy(string source, string destination, bool overwrite) => inner.Copy(source, destination, overwrite);
        public void Delete(string path) => inner.Delete(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public IReadOnlyList<string> ListDirectories(string path) => inner.ListDirectories(path);
        public IReadOnlyList<string> ListFiles(string path) => inner.ListFiles(path);
        public void DeleteDirectory(string path) => inner.DeleteDirectory(path);
    }
}
