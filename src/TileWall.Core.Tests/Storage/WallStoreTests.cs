using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Storage;

public sealed partial class WallStoreTests
{
    private sealed class FaultingFiles(IFileStore inner) : IFileStore
    {
        public List<Func<string, string?, string?, bool>> Rules { get; } = [];
        public List<Func<string, string?, string?, bool>> Sticky { get; } = [];
        private void Trip(string kind, string? src, string? dst)
        {
            foreach (var rule in Sticky)
                if (rule(kind, src, dst))
                    throw new IOException($"Injected {kind}: {src} -> {dst}");
            for (var i = 0; i < Rules.Count; i++)
                if (Rules[i](kind, src, dst))
                {
                    Rules.RemoveAt(i);
                    throw new IOException($"Injected {kind}: {src} -> {dst}");
                }
        }
        public FileStorePathInfo GetPathInfo(string path) => inner.GetPathInfo(path);
        public bool Exists(string path) => inner.Exists(path);
        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);
        public void WriteAllBytes(string path, byte[] bytes) { Trip("write", null, path); inner.WriteAllBytes(path, bytes); }
        public void Move(string src, string dst, bool overwrite) { Trip("move", src, dst); inner.Move(src, dst, overwrite); }
        public void Copy(string src, string dst, bool overwrite) { Trip("copy", src, dst); inner.Copy(src, dst, overwrite); }
        public void Delete(string path) { Trip("delete", path, null); inner.Delete(path); }
        public void CreateDirectory(string path) { Trip("mkdir", null, path); inner.CreateDirectory(path); }
        public IReadOnlyList<string> ListDirectories(string path) => inner.ListDirectories(path);
        public IReadOnlyList<string> ListFiles(string path) => inner.ListFiles(path);
        public void DeleteDirectory(string path) { Trip("rmdir", path, null); inner.DeleteDirectory(path); }
    }

    private static TileMember Tile(string id, string? title = null, ManagedEntryReference? entry = null) => new()
    {
        Id = id, LocalBounds = new GridRect(0, 0, 1, 1), Visual = new MemberVisual { TitleMode = MemberTitleMode.Manual, Title = title }, Entry = entry
    };

    private static WallDocument Wall(params TileMember[] tiles) => new()
    {
        Columns = 1, Rows = 4,
        Lanes = [new WallLane { Groups = tiles.Select((t, i) => new StartGroup
        { Id = $"group{i}", Members = [t] }).ToArray() }]
    };

    private static (WallStore Store, InMemoryFileStore Files, FakeLinkFileService Links, string Root) Harness()
    {
        var files = new InMemoryFileStore();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wall-storage-" + Guid.NewGuid().ToString("N")));
        var links = new FakeLinkFileService(files);
        return (new WallStore(files, links, root), files, links, root);
    }

    private static string EntryPath(string root, ManagedEntryReference entry) =>
        Path.Combine(root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static WallCommitRequest Plan(WallStore store, WallDocument before, WallDocument candidate,
        params EntryChange[] changes)
    {
        var result = store.PlanEntries(before, candidate, changes);
        Assert.NotNull(result.Request);
        return result.Request;
    }

    private static (WallStore Store, InMemoryFileStore Files, string Root, WallDocument Document,
        string JournalPath, string CommitId) CreateObservedJournalScenario(bool leaveStagingJournal)
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var added = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://example.com")), true);
        Assert.Equal(WallCommitStatus.Committed, added.Status);

        var current = added.CommittedDocument!;
        var candidate = current with
        {
            Generation = current.Generation + 1,
            Settings = current.Settings with { AlwaysOnTop = !current.Settings.AlwaysOnTop }
        };
        var request = Plan(store, current, candidate);
        if (leaveStagingJournal) files.EntryFault = EntryFaultPoint.BeforeStagingCleanup;
        var result = store.Commit(current, request, true);
        string commitId;
        string journalPath;
        if (leaveStagingJournal)
        {
            Assert.Equal(WallCommitStatus.CommittedNeedsCleanup, result.Status);
            var stage = Assert.Single(files.ListDirectories(Path.Combine(root, "Staging")));
            commitId = Path.GetFileName(stage);
            journalPath = Path.Combine(stage, "commit.json");
        }
        else
        {
            Assert.Equal(WallCommitStatus.Committed, result.Status);
            commitId = result.Undo!.CommitId;
            journalPath = Path.Combine(root, "Recovery", "Entries", commitId, "journal.json");
        }

        var journal = JsonNode.Parse(files.ReadAllBytes(journalPath))!;
        Assert.NotEmpty(journal["observed"]!.AsArray());
        return (store, files, root, result.CommittedDocument!, journalPath, commitId);
    }

    private static void AssertSameFileBytes(
        IReadOnlyDictionary<string, byte[]> expected, IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in expected)
            Assert.Equal(bytes, actual[path]);
    }

    [Fact]
    public void FreshInitializeCommitRestartAndStaleReject()
    {
        var (store, files, _, root) = Harness();
        Assert.Equal(WallOpenStatus.Fresh, store.Open().Status);
        var initial = Wall();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        Assert.Equal(WallOpenStatus.Loaded, store.Open().Status);
        var next = Wall(Tile("a", "Alpha")) with { Generation = 1 };
        var request = Plan(store, initial, next, new EntryChange.CreateUrl("a", "https://example.com"));
        var commit = store.Commit(initial, request, true);
        Assert.Equal(WallCommitStatus.Committed, commit.Status);
        Assert.Equal(1, commit.CommittedDocument!.Generation);
        var entry = ((TileMember)commit.CommittedDocument.Lanes[0].Groups[0].Members[0]).Entry!;
        Assert.Equal("Objects/a/Alpha.url", entry.RelativePath);
        Assert.True(files.Exists(EntryPath(root, entry)));
        Assert.Equal(WallOpenStatus.Loaded, store.Open().Status);
        var snapshot = files.SnapshotFiles();
        Assert.Equal(WallCommitStatus.Rejected, store.Commit(initial, request, true).Status);
        Assert.Equal(snapshot.Keys.Order(), files.SnapshotFiles().Keys.Order());
    }

    [Fact]
    public void EmptyManagedContainersAndLogsStillClassifyFresh()
    {
        var (store, files, _, root) = Harness();
        foreach (var directory in new[] { "Objects", "Staging", "Recovery", "Cache", "logs" })
            files.CreateDirectory(Path.Combine(root, directory));
        files.CreateDirectory(Path.Combine(root, "Recovery", "Entries"));
        files.WriteAllBytes(Path.Combine(root, "logs", "tilewall.log"), [1, 2]);
        files.WriteAllBytes(Path.Combine(root, "Cache", "icon.bin"), [3]);
        Assert.Equal(WallOpenStatus.Fresh, store.Open().Status);
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(Wall()).Status);
    }

    [Fact]
    public void SameNameSeparateMembersAndUrlCopyPreservesBytes()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var source = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "source-" + Guid.NewGuid().ToString("N") + ".url"));
        var bytes = Encoding.Latin1.GetBytes("[InternetShortcut]\r\nURL=https://example.com\r\nIconFile=C:\\icon.ico\r\n");
        files.WriteAllBytes(source, bytes);
        var candidate = Wall(Tile("a", "Same"), Tile("b", "Same")) with { Generation = 1 };
        var request = Plan(store, initial, candidate,
            new EntryChange.CopyShortcut("a", source), new EntryChange.CopyShortcut("b", source));
        var result = store.Commit(initial, request, true);
        Assert.Equal(WallCommitStatus.Committed, result.Status);
        foreach (var member in result.CommittedDocument!.Lanes[0].Groups.SelectMany(g => g.Members))
            Assert.Equal(bytes, files.ReadAllBytes(EntryPath(root, member.Entry!)));
        Assert.Equal(bytes, files.ReadAllBytes(source));
    }

    [Fact]
    public void RenameAndTargetEditUndoRestoresExactBytes()
    {
        var (store, files, links, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var first = Plan(store, initial, Wall(Tile("a", "One")) with { Generation = 1 },
            new EntryChange.CreateLink("a", @"C:\first.exe"));
        var added = store.Commit(initial, first, true);
        Assert.Equal(WallCommitStatus.Committed, added.Status);
        var current = added.CommittedDocument!;
        var oldEntry = current.Lanes[0].Groups[0].Members[0].Entry!;
        var oldPath = EntryPath(root, oldEntry);
        links.Register(oldPath, @"C:\first.exe", "--arg", @"C:\work");
        var oldBytes = files.ReadAllBytes(oldPath).ToArray();
        var update = Plan(store, current, Wall(Tile("a", "Two")) with { Generation = 2 },
            new EntryChange.ChangeTarget("a", @"C:\second.exe"), new EntryChange.Rename("a"));
        var changed = store.Commit(current, update, true);
        Assert.Equal(WallCommitStatus.Committed, changed.Status);
        var newEntry = changed.CommittedDocument!.Lanes[0].Groups[0].Members[0].Entry!;
        Assert.Equal("--arg", links.StateOf(EntryPath(root, newEntry)).Arguments);
        Assert.Equal(@"C:\work", links.StateOf(EntryPath(root, newEntry)).WorkingDirectory);
        Assert.False(files.Exists(oldPath));
        var undone = store.Undo(changed.CommittedDocument, current, changed.Undo!, 2);
        Assert.Equal(WallCommitStatus.Committed, undone.Status);
        Assert.Equal(oldBytes, files.ReadAllBytes(oldPath));
        Assert.False(files.Exists(EntryPath(root, newEntry)));
        Assert.Equal(3, store.Open().Document!.Generation);
    }

    [Fact]
    public void EntrySwapFailureRollsBackAndLeavesOldDocument()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var create = Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://old.example"));
        var added = store.Commit(initial, create, true);
        Assert.Equal(WallCommitStatus.Committed, added.Status);
        var before = added.CommittedDocument!;
        var entry = before.Lanes[0].Groups[0].Members[0].Entry!;
        var bytes = files.ReadAllBytes(EntryPath(root, entry)).ToArray();
        var edit = Plan(store, before, before with { Generation = 2 },
            new EntryChange.ChangeTarget("a", "https://new.example"));
        files.EntryFault = EntryFaultPoint.BeforeEntrySwap;
        var failed = store.Commit(before, edit, true);
        Assert.Equal(WallCommitStatus.Rejected, failed.Status);
        Assert.Equal(bytes, files.ReadAllBytes(EntryPath(root, entry)));
        Assert.Equal(1, store.Open().Document!.Generation);
    }

    [Fact]
    public void FutureAndCorruptSchemaNeverReset()
    {
        var (store, files, _, root) = Harness();
        files.WriteAllBytes(Path.Combine(root, "config.json"), Encoding.UTF8.GetBytes("{\"schemaVersion\":4}"));
        Assert.Equal(WallOpenStatus.FutureVersionBlocked, store.Open().Status);
        Assert.Equal(WallOpenStatus.FutureVersionBlocked, store.ResetLegacyState().Status);
        files.WriteAllBytes(Path.Combine(root, "config.json"), Encoding.UTF8.GetBytes("{\"generation\":0}"));
        Assert.Equal(WallOpenStatus.CorruptBlocked, store.Open().Status);
    }

    [Fact]
    public void NoncanonicalSchemaThreeCanCommitUsingActualDiskFingerprint()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var formatted = Encoding.UTF8.GetBytes("{ \"columns\": 1, \"rows\": 4, \"schemaVersion\": 3, \"generation\": 0, \"lanes\": [{\"groups\":[]}], \"settings\":{} }");
        files.WriteAllBytes(Path.Combine(root, "config.json"), formatted);
        var loaded = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, loaded.Status);
        var candidate = loaded.Document! with
        {
            Generation = 1,
            Settings = loaded.Document.Settings with { AlwaysOnTop = false }
        };
        var request = Plan(store, loaded.Document, candidate);
        Assert.Equal(WallCommitStatus.Committed, store.Commit(loaded.Document, request, false).Status);
        Assert.Equal(formatted, files.ReadAllBytes(Path.Combine(root, "Recovery", "config.prev.json")));
    }

    [Fact]
    public void SameGenerationExternalConfigChangeRejectsCommitWithoutWriting()
    {
        var (store, files, _, root) = Harness();
        var current = Wall();
        store.InitializeFresh(current);
        var candidate = current with
        {
            Generation = 1,
            Settings = current.Settings with { AlwaysOnTop = false }
        };
        var request = Plan(store, current, candidate);

        var external = current with { Settings = current.Settings with { ThemeMode = WallThemeMode.Dark } };
        var configPath = Path.Combine(root, "config.json");
        files.WriteAllBytes(configPath, WallJson.Serialize(external));
        var baseline = files.SnapshotFiles();

        Assert.Equal(WallCommitStatus.Rejected, store.Commit(current, request, true).Status);
        AssertSameFileBytes(baseline, files.SnapshotFiles());
    }

    [Fact]
    public void CorruptPrimaryAfterSamePathTargetEditRestoresPreviousBytes()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var first = Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://old.example"));
        var added = store.Commit(initial, first, true);
        Assert.Equal(WallCommitStatus.Committed, added.Status);
        var before = added.CommittedDocument!;
        var entry = before.Lanes[0].Groups[0].Members[0].Entry!;
        var entryPath = EntryPath(root, entry);
        var oldBytes = files.ReadAllBytes(entryPath).ToArray();
        var edit = Plan(store, before, before with { Generation = 2 },
            new EntryChange.ChangeTarget("a", "https://new.example"));
        var changed = store.Commit(before, edit, true);
        Assert.Equal(WallCommitStatus.Committed, changed.Status);
        Assert.NotEqual(oldBytes, files.ReadAllBytes(entryPath));
        files.WriteAllBytes(Path.Combine(root, "config.json"), Encoding.UTF8.GetBytes("{\"schemaVersion\":3,\"lanes\":null}"));
        var recovered = store.Open();
        Assert.Equal(WallOpenStatus.RecoveredPrevious, recovered.Status);
        Assert.Equal(oldBytes, files.ReadAllBytes(entryPath));
        Assert.Equal(before.Lanes[0].Groups[0].Members[0].Entry, recovered.Document!.Lanes[0].Groups[0].Members[0].Entry);
        Assert.Equal(WallOpenStatus.Loaded, store.Open().Status);
    }

    [Fact]
    public void MissingRecoveryMaterialBlocksCorruptPrimaryWithoutChangingEntry()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var add = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://old.example")), true);
        var before = add.CommittedDocument!;
        var edit = store.Commit(before, Plan(store, before, before with { Generation = 2 },
            new EntryChange.ChangeTarget("a", "https://new.example")), true);
        var entry = before.Lanes[0].Groups[0].Members[0].Entry!;
        var entryPath = EntryPath(root, entry);
        var active = files.ReadAllBytes(entryPath).ToArray();
        files.Delete(Path.Combine(root, "Recovery", "Entries", edit.Undo!.CommitId, "a", "old.entry"));
        files.WriteAllBytes(Path.Combine(root, "config.json"), Encoding.UTF8.GetBytes("{\"schemaVersion\":3,\"lanes\":null}"));
        Assert.Equal(WallOpenStatus.RecoveryBlocked, store.Open().Status);
        Assert.Equal(active, files.ReadAllBytes(entryPath));
    }

    [Fact]
    public void SettingsSaveDoesNotDisplaceLayoutUndo()
    {
        var (store, _, _, _) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var added = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://example.com")), true);
        Assert.Equal(WallCommitStatus.Committed, added.Status);
        var settings = added.CommittedDocument! with
        {
            Generation = 2,
            Settings = added.CommittedDocument.Settings with { AlwaysOnTop = false }
        };
        var saved = store.Commit(added.CommittedDocument, Plan(store, added.CommittedDocument, settings), false);
        Assert.Equal(WallCommitStatus.Committed, saved.Status);
        Assert.Null(saved.Undo);
        var undone = store.Undo(saved.CommittedDocument!, initial, added.Undo!, 2);
        Assert.Equal(WallCommitStatus.Committed, undone.Status);
        Assert.False(undone.CommittedDocument!.Settings.AlwaysOnTop);
        Assert.Empty(undone.CommittedDocument.Lanes[0].Groups);
        Assert.Equal(3, store.Open().Document!.Generation);
    }

    [Fact]
    public void InvalidDocumentAndMissingSchemaHaveTypedResults()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var candidate = Wall(Tile("a")) with { Generation = 1 };
        var malformed = candidate with { Lanes = null! };
        Assert.Null(store.PlanEntries(initial, malformed, []).Request);
        files.WriteAllBytes(Path.Combine(root, "config.json"), Encoding.UTF8.GetBytes("{\"schemaVersion\":3,\"lanes\":null}"));
        Assert.Equal(WallOpenStatus.CorruptBlocked, store.Open().Status);
        files.WriteAllBytes(Path.Combine(root, "config.json"), Encoding.UTF8.GetBytes("{\"rows\":4}"));
        Assert.Equal(WallOpenStatus.CorruptBlocked, store.Open().Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OlderSchemaResetOnlyRemovesAppOwnedPaths(int schema)
    {
        var (store, files, _, root) = Harness();
        var external = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "external-" + Guid.NewGuid().ToString("N") + ".url"));
        files.WriteAllBytes(external, [1, 2, 3]);
        files.WriteAllBytes(Path.Combine(root, "config.json"), Encoding.UTF8.GetBytes($"{{\"schemaVersion\":{schema}}}"));
        files.CreateDirectory(Path.Combine(root, "Objects"));
        files.CreateDirectory(Path.Combine(root, "logs"));
        files.WriteAllBytes(Path.Combine(root, "logs", "tilewall.log"), [4]);
        files.WriteAllBytes(Path.Combine(root, "Objects", "legacy.url"), [5]);
        Assert.Equal(WallOpenStatus.LegacyResetRequired, store.Open().Status);
        Assert.Equal(WallOpenStatus.Fresh, store.ResetLegacyState().Status);
        Assert.True(files.Exists(external));
        Assert.True(files.Exists(Path.Combine(root, "logs", "tilewall.log")));
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(Wall()).Status);
    }

    [Fact]
    public void PreIntentBackupFailureLeavesNoChangedEntryOrRecoveryBlock()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var added = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://old.example")), true);
        var before = added.CommittedDocument!;
        var entry = before.Lanes[0].Groups[0].Members[0].Entry!;
        var path = EntryPath(root, entry);
        var oldBytes = files.ReadAllBytes(path).ToArray();
        files.EntryFault = EntryFaultPoint.BeforeEntryBackup;
        var request = Plan(store, before, before with { Generation = 2 },
            new EntryChange.ChangeTarget("a", "https://new.example"));
        Assert.Equal(WallCommitStatus.Rejected, store.Commit(before, request, true).Status);
        Assert.Equal(oldBytes, files.ReadAllBytes(path));
        Assert.Equal(WallOpenStatus.Loaded, store.Open().Status);
    }

    [Fact]
    public void CleanupFailureIsCommittedAndRestartCompletesCleanup()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        files.EntryFault = EntryFaultPoint.BeforeStagingCleanup;
        var result = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://example.com")), true);
        Assert.Equal(WallCommitStatus.CommittedNeedsCleanup, result.Status);
        Assert.Null(result.Undo);
        Assert.Equal(1, store.Open().Document!.Generation);
        Assert.Empty(files.ListDirectories(Path.Combine(root, "Staging")));
    }

    [Fact]
    public void MaliciousJournalBlocksRecoveryAndPreservesMaterial()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        files.EntryFault = EntryFaultPoint.BeforeStagingCleanup;
        var result = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://example.com")), true);
        Assert.Equal(WallCommitStatus.CommittedNeedsCleanup, result.Status);
        var stage = Assert.Single(files.ListDirectories(Path.Combine(root, "Staging")));
        var journalPath = Path.Combine(stage, "commit.json");
        var journal = JsonNode.Parse(files.ReadAllBytes(journalPath))!;
        journal["ops"]![0]!["newPath"] = "Objects/a/../../outside.url";
        files.WriteAllBytes(journalPath, Encoding.UTF8.GetBytes(journal.ToJsonString()));
        Assert.Equal(WallOpenStatus.RecoveryBlocked, store.Open().Status);
        Assert.True(files.Exists(journalPath));
        Assert.Equal(1, WallJson.Deserialize(files.ReadAllBytes(Path.Combine(root, "config.json"))).Generation);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, " \t ")]
    [InlineData(false, null)]
    [InlineData(false, "")]
    [InlineData(false, " \t ")]
    public void ObservedJournalPathMissingBlocksRecoveryAndKeepsMaterial(
        bool leaveStagingJournal, string? invalidRelativePath)
    {
        // An equivalent untouched journal must remain readable before the malformed copy is checked.
        var valid = CreateObservedJournalScenario(leaveStagingJournal);
        var validJournal = JsonNode.Parse(valid.Files.ReadAllBytes(valid.JournalPath))!;
        var validObserved = Assert.Single(validJournal["observed"]!.AsArray());
        Assert.Equal("Objects/a/A.url", validObserved!["relativePath"]!.GetValue<string>());
        Assert.Equal(WallOpenStatus.Loaded, valid.Store.Open().Status);

        var (store, files, root, current, journalPath, commitId) =
            CreateObservedJournalScenario(leaveStagingJournal);
        var journal = JsonNode.Parse(files.ReadAllBytes(journalPath))!;
        var observed = Assert.Single(journal["observed"]!.AsArray());
        if (invalidRelativePath is null)
            observed!["relativePath"] = null;
        else
            observed!["relativePath"] = JsonValue.Create(invalidRelativePath);
        files.WriteAllBytes(journalPath, Encoding.UTF8.GetBytes(journal.ToJsonString()));

        var configPath = Path.Combine(root, "config.json");
        var entryPath = EntryPath(root, current.Lanes[0].Groups[0].Members[0].Entry!);
        var baseline = files.SnapshotFiles();
        Assert.Contains(journalPath, baseline.Keys);
        Assert.Contains(configPath, baseline.Keys);
        Assert.Contains(entryPath, baseline.Keys);

        var opened = store.Open();
        Assert.Equal(WallOpenStatus.RecoveryBlocked, opened.Status);
        Assert.Contains(commitId, string.Join(" ", opened.Diagnostics), StringComparison.Ordinal);
        Assert.True(files.Exists(journalPath));
        AssertSameFileBytes(baseline, files.SnapshotFiles());

        var candidate = current with
        {
            Generation = current.Generation + 1,
            Settings = current.Settings with { AlwaysOnTop = !current.Settings.AlwaysOnTop }
        };
        var request = Plan(store, current, candidate);
        var blockedCommit = store.Commit(current, request, true);
        Assert.Equal(WallCommitStatus.RecoveryBlocked, blockedCommit.Status);
        Assert.True(files.Exists(journalPath));
        AssertSameFileBytes(baseline, files.SnapshotFiles());
    }

    [Fact]
    public void FreshInitializationIsOneTimeAndFailedPublicationLeavesFreshRoot()
    {
        var (store, files, _, root) = Harness();
        files.InjectedFault = FaultPoint.BeforeMove;
        Assert.Equal(WallOpenStatus.OpenFailed, store.InitializeFresh(Wall()).Status);
        Assert.False(files.Exists(Path.Combine(root, "config.init.tmp")));
        Assert.Equal(WallOpenStatus.Fresh, store.Open().Status);
        files.InjectedFault = FaultPoint.None;
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(Wall()).Status);
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(Wall()).Status);
    }

    [Fact]
    public void ConfigReplaceFailureRollsBackEntryAndUndoCanRetry()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var added = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://old.example")), true);
        var before = added.CommittedDocument!;
        var entry = before.Lanes[0].Groups[0].Members[0].Entry!;
        var entryPath = EntryPath(root, entry);
        var oldBytes = files.ReadAllBytes(entryPath).ToArray();
        var request = Plan(store, before, before with { Generation = 2 },
            new EntryChange.ChangeTarget("a", "https://new.example"));
        files.InjectedFault = FaultPoint.BeforeMove;
        Assert.Equal(WallCommitStatus.Rejected, store.Commit(before, request, true).Status);
        Assert.Equal(oldBytes, files.ReadAllBytes(entryPath));
        Assert.Equal(1, store.Open().Document!.Generation);
        files.InjectedFault = FaultPoint.None;
        var changed = store.Commit(before, request, true);
        Assert.Equal(WallCommitStatus.Committed, changed.Status);
        files.InjectedFault = FaultPoint.BeforeMove;
        Assert.Equal(WallCommitStatus.Rejected,
            store.Undo(changed.CommittedDocument!, before, changed.Undo!, 2).Status);
        Assert.Equal(2, store.Open().Document!.Generation);
        files.InjectedFault = FaultPoint.None;
        Assert.Equal(WallCommitStatus.Committed,
            store.Undo(changed.CommittedDocument!, before, changed.Undo!, 2).Status);
        Assert.Equal(oldBytes, files.ReadAllBytes(entryPath));
    }

    [Fact]
    public void UnsafeMemberIdsAndIdListEditAreRejectedBeforeWrite()
    {
        var (store, files, links, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        foreach (var bad in new[] { "CON", "COM¹", "LPT²", "name.", "name ", "a/b", "a\\b" })
            Assert.Null(store.PlanEntries(initial, Wall(Tile(bad)) with { Generation = 1 }, []).Request);
        Assert.Null(store.PlanEntries(initial, Wall(Tile("a", "COM¹")) with { Generation = 1 },
            [new EntryChange.CreateUrl("a", "https://example.com")]).Request);
        var add = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateLink("a", @"C:\target.exe")), true);
        var current = add.CommittedDocument!;
        var path = EntryPath(root, current.Lanes[0].Groups[0].Members[0].Entry!);
        links.Register(path, null, hasIdList: true);
        var baseline = files.SnapshotFiles();
        var plan = store.PlanEntries(current, current with { Generation = 2 },
            [new EntryChange.ChangeTarget("a", @"C:\other.exe")]);
        Assert.Null(plan.Request);
        Assert.Equal(baseline.Keys.Order(), files.SnapshotFiles().Keys.Order());
        Assert.Equal(baseline[path], files.ReadAllBytes(path));
    }

    [Fact]
    public void ReparsePointAndRelativeRootAreBlocked()
    {
        var files = new InMemoryFileStore();
        var links = new FakeLinkFileService(files);
        Assert.Throws<ArgumentException>(() => new WallStore(files, links, "relative-root"));
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wall-reparse-" + Guid.NewGuid().ToString("N")));
        var store = new WallStore(files, links, root);
        files.ReparsePaths.Add(root);
        Assert.Equal(WallOpenStatus.RecoveryBlocked, store.Open().Status);
        Assert.Empty(files.FilePaths);
        files.ReparsePaths.Clear();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(Wall()).Status);
        files.ReparsePaths.Add(Path.Combine(root, "Objects"));
        var candidate = Wall(Tile("a", "A")) with { Generation = 1 };
        var planned = Plan(store, Wall(), candidate, new EntryChange.CreateUrl("a", "https://example.com"));
        Assert.Equal(WallCommitStatus.RecoveryBlocked, store.Commit(Wall(), planned, true).Status);
        Assert.Equal(0, store.Open().Document!.Generation);
    }

    [Fact]
    public void AppearanceOnlyCommitNeverTouchesManagedEntry()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var added = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://example.com")), true);
        var before = added.CommittedDocument!;
        var member = (TileMember)before.Lanes[0].Groups[0].Members[0];
        var entryPath = EntryPath(root, member.Entry!);
        var oldBytes = files.ReadAllBytes(entryPath).ToArray();
        var altered = before with
        {
            Generation = 2,
            Lanes = [new WallLane { Groups = [before.Lanes[0].Groups[0] with
            { Members = [member with { Visual = member.Visual with { BackgroundColor = "#FF0000" } }] }] }]
        };
        Assert.Equal(WallCommitStatus.Committed, store.Commit(before, Plan(store, before, altered), true).Status);
        Assert.Equal(oldBytes, files.ReadAllBytes(entryPath));
    }

    [Fact]
    public void MissingPrimaryAfterSamePathEditRestoresPreviousEntryBytes()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var add = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://old.example")), true);
        var before = add.CommittedDocument!;
        var entry = before.Lanes[0].Groups[0].Members[0].Entry!;
        var path = EntryPath(root, entry);
        var oldBytes = files.ReadAllBytes(path).ToArray();
        var changed = store.Commit(before, Plan(store, before, before with { Generation = 2 },
            new EntryChange.ChangeTarget("a", "https://new.example")), true);
        Assert.Equal(WallCommitStatus.Committed, changed.Status);
        files.Delete(Path.Combine(root, "config.json"));
        var recovered = store.Open();
        Assert.Equal(WallOpenStatus.RecoveredPrevious, recovered.Status);
        Assert.Equal(oldBytes, files.ReadAllBytes(path));
        Assert.Equal(WallOpenStatus.Loaded, store.Open().Status);
    }

    [Fact]
    public void UndoRetainsRecoveryMaterialAfterItChangesEntries()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var added = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://example.com")), true);
        var member = added.CommittedDocument!.Lanes[0].Groups[0].Members[0];
        var path = EntryPath(root, member.Entry!);
        var undo = store.Undo(added.CommittedDocument, initial, added.Undo!, 1);
        Assert.Equal(WallCommitStatus.Committed, undo.Status);
        Assert.False(files.Exists(path));
        files.WriteAllBytes(Path.Combine(root, "config.json"), Encoding.UTF8.GetBytes("{\"schemaVersion\":3,\"lanes\":null}"));
        var recovered = store.Open();
        Assert.Equal(WallOpenStatus.RecoveredPrevious, recovered.Status);
        Assert.True(files.Exists(path));
        Assert.Single(Assert.Single(recovered.Document!.Lanes[0].Groups).Members);
    }

    [Fact]
    public void KeepCannotRemoveAndRenamingRequiresValidTitleAndExplicitChange()
    {
        var (store, _, _, _) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var added = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://example.com")), true);
        var before = added.CommittedDocument!;
        Assert.Null(store.PlanEntries(before, Wall() with { Generation = 2 },
            [new EntryChange.Keep("a")]).Request);
        var renamed = Wall(Tile("a", "B")) with { Generation = 2 };
        Assert.Null(store.PlanEntries(before, renamed, []).Request);
        var keptWithNewTitle = before with
        {
            Generation = 2,
            Lanes = [new WallLane { Groups = [before.Lanes[0].Groups[0] with
            { Members = [((TileMember)before.Lanes[0].Groups[0].Members[0]) with
            { Visual = new MemberVisual { TitleMode = MemberTitleMode.Manual, Title = "B" } }] }] }]
        };
        Assert.Null(store.PlanEntries(before, keptWithNewTitle, [new EntryChange.Keep("a")]).Request);
        Assert.Null(store.PlanEntries(before, Wall(Tile("a", "COM¹")) with { Generation = 2 },
            [new EntryChange.Rename("a")]).Request);
        Assert.NotNull(store.PlanEntries(before, renamed, [new EntryChange.Rename("a")]).Request);
    }

    [Fact]
    public void RemoveAllowsDecorationTitleAndRestoringSameFilenameNeedsNoFileMove()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var added = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "Alpha")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://example.com")), true);
        var before = added.CommittedDocument!;
        var member = (TileMember)before.Lanes[0].Groups[0].Members[0];
        var entryPath = EntryPath(root, member.Entry!);
        var content = files.ReadAllBytes(entryPath).ToArray();
        var emptyTitle = before with
        {
            Generation = 2,
            Lanes = [new WallLane { Groups = [before.Lanes[0].Groups[0] with
            { Members = [member with { Visual = member.Visual with { Title = "" } }] }] }]
        };
        var cleared = store.Commit(before, Plan(store, before, emptyTitle), true);
        Assert.Equal(WallCommitStatus.Committed, cleared.Status);
        var clearedMember = (TileMember)cleared.CommittedDocument!.Lanes[0].Groups[0].Members[0];
        var restoredTitle = cleared.CommittedDocument with
        {
            Generation = 3,
            Lanes = [new WallLane { Groups = [cleared.CommittedDocument.Lanes[0].Groups[0] with
            { Members = [clearedMember with { Visual = clearedMember.Visual with { Title = "Alpha" } }] }] }]
        };
        var restored = store.Commit(cleared.CommittedDocument,
            Plan(store, cleared.CommittedDocument, restoredTitle, new EntryChange.Rename("a")), true);
        Assert.Equal(WallCommitStatus.Committed, restored.Status);
        Assert.Equal(member.Entry, restored.CommittedDocument!.Lanes[0].Groups[0].Members[0].Entry);
        Assert.Equal(content, files.ReadAllBytes(entryPath));
        var decoration = restored.CommittedDocument with
        {
            Generation = 4,
            Lanes = [new WallLane { Groups = [restored.CommittedDocument.Lanes[0].Groups[0] with
            { Members = [((TileMember)restored.CommittedDocument.Lanes[0].Groups[0].Members[0]) with
            { Entry = null, Visual = new MemberVisual { TitleMode = MemberTitleMode.Manual, Title = "a/b" } }] }] }]
        };
        var removed = store.Commit(restored.CommittedDocument,
            Plan(store, restored.CommittedDocument, decoration, new EntryChange.Remove("a")), true);
        Assert.Equal(WallCommitStatus.Committed, removed.Status);
        Assert.Null(removed.CommittedDocument!.Lanes[0].Groups[0].Members[0].Entry);
        Assert.False(files.Exists(entryPath));
    }

    [Fact]
    public void RecoveryMaterialKeepsLatestCommitAndLayoutUndoOnly()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var layout = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://example.com")), true);
        var current = layout.CommittedDocument!;
        for (var generation = 2; generation <= 5; generation++)
        {
            var candidate = current with
            {
                Generation = generation,
                Settings = current.Settings with { AlwaysOnTop = generation % 2 != 0 }
            };
            var saved = store.Commit(current, Plan(store, current, candidate), false);
            Assert.Equal(WallCommitStatus.Committed, saved.Status);
            current = saved.CommittedDocument!;
        }
        Assert.Equal(2, files.ListDirectories(Path.Combine(root, "Recovery", "Entries")).Count);
        Assert.Equal(WallCommitStatus.Committed, store.Undo(current, initial, layout.Undo!, 5).Status);
        Assert.Single(files.ListDirectories(Path.Combine(root, "Recovery", "Entries")));
    }

    [Fact]
    public void OpaqueCopiedShortcutsPreserveBytesWithoutInventingTargets()
    {
        var (store, files, links, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var lnkSource = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"opaque-{Guid.NewGuid():N}.lnk"));
        var urlSource = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"opaque-{Guid.NewGuid():N}.url"));
        links.Register(lnkSource, null, hasIdList: false);
        files.WriteAllBytes(urlSource, Encoding.UTF8.GetBytes("[InternetShortcut]\r\nIconFile=C:\\icon.ico\r\n"));
        var lnkBytes = files.ReadAllBytes(lnkSource).ToArray();
        var urlBytes = files.ReadAllBytes(urlSource).ToArray();
        var candidate = Wall(Tile("a", "Opaque"), Tile("b", "Opaque")) with { Generation = 1 };
        var result = store.Commit(initial, Plan(store, initial, candidate,
            new EntryChange.CopyShortcut("a", lnkSource), new EntryChange.CopyShortcut("b", urlSource)), true);
        Assert.Equal(WallCommitStatus.Committed, result.Status);
        var members = result.CommittedDocument!.Lanes[0].Groups.SelectMany(g => g.Members).ToArray();
        Assert.Equal(lnkBytes, files.ReadAllBytes(EntryPath(root, members[0].Entry!)));
        Assert.Equal(urlBytes, files.ReadAllBytes(EntryPath(root, members[1].Entry!)));
        Assert.Equal(lnkBytes, files.ReadAllBytes(lnkSource));
        Assert.Equal(urlBytes, files.ReadAllBytes(urlSource));
    }

    [Fact]
    public void Latin1UrlRejectsUnrepresentableTargetWithoutChangingEntryOrStoreFiles()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var source = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "source-" + Guid.NewGuid().ToString("N") + ".url"));
        var oldBytes = Encoding.Latin1.GetBytes(
            "[InternetShortcut]\r\nURL=https://old.example/\r\nIconFile=C:\\icons\\caf\u00E9.ico\r\nIconIndex=3\r\n");
        files.WriteAllBytes(source, oldBytes);

        var added = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CopyShortcut("a", source)), true);
        Assert.Equal(WallCommitStatus.Committed, added.Status);
        var current = added.CommittedDocument!;
        var entry = current.Lanes[0].Groups[0].Members[0].Entry!;
        var entryPath = EntryPath(root, entry);
        Assert.Equal(oldBytes, files.ReadAllBytes(entryPath));

        const string target = "https://new.example/\u20AC";
        Assert.Empty(UrlShortcut.ValidateUrl(target));
        var request = Plan(store, current, current with { Generation = 2 },
            new EntryChange.ChangeTarget("a", target));
        var baseline = files.SnapshotFiles();

        Assert.Equal(WallCommitStatus.Rejected, store.Commit(current, request, true).Status);
        Assert.Equal(oldBytes, files.ReadAllBytes(entryPath));
        AssertSameFileBytes(baseline, files.SnapshotFiles());
    }

    [Fact]
    public void DamagedStoredRecoveryJournalBlocksLaterWritesAndKeepsMaterial()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var added = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://example.com")), true);
        Assert.Equal(WallCommitStatus.Committed, added.Status);
        var journalPath = Path.Combine(root, "Recovery", "Entries", added.Undo!.CommitId, "journal.json");
        var journal = JsonNode.Parse(files.ReadAllBytes(journalPath))!;
        journal["unexpected"] = "unsafe";
        files.WriteAllBytes(journalPath, Encoding.UTF8.GetBytes(journal.ToJsonString()));
        var baseline = files.SnapshotFiles();
        Assert.Equal(WallOpenStatus.RecoveryBlocked, store.Open().Status);
        var candidate = added.CommittedDocument! with { Generation = 2 };
        var result = store.Commit(added.CommittedDocument, new WallCommitRequest(1, candidate, []), false);
        Assert.Equal(WallCommitStatus.RecoveryBlocked, result.Status);
        Assert.Equal(baseline.Keys.Order(), files.SnapshotFiles().Keys.Order());
        Assert.True(files.Exists(journalPath));
    }

    [Fact]
    public void LegacyResetRefusesUndecidableInstallIntent()
    {
        var (store, files, _, root) = Harness();
        var config = Path.Combine(root, "config.json");
        files.WriteAllBytes(config, Encoding.UTF8.GetBytes("{\"schemaVersion\":1}"));
        var stage = Path.Combine(root, "Staging", Guid.NewGuid().ToString("N"));
        files.CreateDirectory(Path.Combine(root, "Staging"));
        files.CreateDirectory(stage);
        files.WriteAllBytes(Path.Combine(stage, "install.intent"), []);
        Assert.Equal(WallOpenStatus.LegacyResetRequired, store.Open().Status);
        Assert.Equal(WallOpenStatus.RecoveryBlocked, store.ResetLegacyState().Status);
        Assert.True(files.Exists(config));
        Assert.True(files.Exists(Path.Combine(stage, "install.intent")));
    }

    [Fact]
    public void LocalFileStorePersistsUrlAndRestartsFromIsolatedRoot()
    {
        var testBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tilewall-storage-tests"));
        var root = Path.GetFullPath(Path.Combine(testBase, Guid.NewGuid().ToString("N")));
        Assert.StartsWith(testBase + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase);
        try
        {
            var files = new LocalFileStore();
            var directory = root;
            var initial = Wall();
            var store = new WallStore(files, new FakeLinkFileService(files), directory);
            Assert.Equal(WallOpenStatus.Fresh, store.Open().Status);
            Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
            var added = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
                new EntryChange.CreateUrl("a", "https://example.com")), true);
            Assert.Equal(WallCommitStatus.Committed, added.Status);
            var entry = added.CommittedDocument!.Lanes[0].Groups[0].Members[0].Entry!;
            Assert.True(File.Exists(EntryPath(root, entry)));
            var restarted = new WallStore(files, new FakeLinkFileService(files), directory);
            Assert.Equal(WallOpenStatus.Loaded, restarted.Open().Status);
            Assert.Equal(1, restarted.Open().Document!.Generation);
            Assert.Contains("URL=https://example.com", File.ReadAllText(EntryPath(root, entry)), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("intent")]
    [InlineData("install")]
    [InlineData("backup-config")]
    public void ExactPreCommitFaultsRejectAndRestoreOldEntry(string point)
    {
        var (baseStore, files, _, root) = Harness();
        var initial = Wall();
        baseStore.InitializeFresh(initial);
        var add = baseStore.Commit(initial, Plan(baseStore, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://old.example")), true);
        var before = add.CommittedDocument!;
        var entry = before.Lanes[0].Groups[0].Members[0].Entry!;
        var path = EntryPath(root, entry);
        var bytes = files.ReadAllBytes(path).ToArray();
        var fault = new FaultingFiles(files);
        var store = new WallStore(fault, new FakeLinkFileService(fault), root);
        var request = Plan(store, before, before with { Generation = 2 },
            new EntryChange.ChangeTarget("a", "https://new.example"));
        fault.Rules.Add((kind, src, dst) => point switch
        {
            "stage" => kind == "write" && dst?.EndsWith("entry-a.tmp", StringComparison.Ordinal) == true,
            "intent" => kind == "move" && dst?.EndsWith("install.intent", StringComparison.Ordinal) == true,
            "install" => kind == "move" && src?.EndsWith("entry-a.tmp", StringComparison.Ordinal) == true &&
                dst?.Contains(Path.DirectorySeparatorChar + "Objects" + Path.DirectorySeparatorChar, StringComparison.Ordinal) == true,
            "backup-config" => kind == "move" && dst?.EndsWith("config.prev.json", StringComparison.Ordinal) == true,
            _ => false
        });
        var result = store.Commit(before, request, true);
        Assert.Equal(WallCommitStatus.Rejected, result.Status);
        Assert.Empty(fault.Rules);
        Assert.Equal(bytes, files.ReadAllBytes(path));
        Assert.Equal(1, store.Open().Document!.Generation);
        Assert.Empty(files.ListDirectories(Path.Combine(root, "Staging")));
    }

    [Fact]
    public void InstallAndRollbackDoubleFailureBlocksUntilRecoveryCanReplay()
    {
        var (baseStore, files, _, root) = Harness();
        var initial = Wall();
        baseStore.InitializeFresh(initial);
        var add = baseStore.Commit(initial, Plan(baseStore, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://old.example")), true);
        var before = add.CommittedDocument!;
        var entry = before.Lanes[0].Groups[0].Members[0].Entry!;
        var oldBytes = files.ReadAllBytes(EntryPath(root, entry)).ToArray();
        var fault = new FaultingFiles(files);
        var store = new WallStore(fault, new FakeLinkFileService(fault), root);
        var request = Plan(store, before, before with { Generation = 2 },
            new EntryChange.ChangeTarget("a", "https://new.example"));
        fault.Rules.Add((kind, src, dst) => kind == "move" && src?.EndsWith("entry-a.tmp", StringComparison.Ordinal) == true &&
            dst?.Contains(Path.DirectorySeparatorChar + "Objects" + Path.DirectorySeparatorChar, StringComparison.Ordinal) == true);
        fault.Rules.Add((kind, src, dst) => kind == "copy" && src?.Contains(Path.DirectorySeparatorChar + "Recovery" + Path.DirectorySeparatorChar + "Entries", StringComparison.Ordinal) == true &&
            dst?.Contains(Path.DirectorySeparatorChar + "Objects" + Path.DirectorySeparatorChar, StringComparison.Ordinal) == true);
        var failed = store.Commit(before, request, true);
        Assert.Equal(WallCommitStatus.RecoveryBlocked, failed.Status);
        Assert.Empty(fault.Rules);
        Assert.Single(files.ListDirectories(Path.Combine(root, "Staging")));
        Assert.Equal(WallOpenStatus.Loaded, store.Open().Status);
        Assert.Equal(oldBytes, files.ReadAllBytes(EntryPath(root, entry)));
        Assert.Empty(files.ListDirectories(Path.Combine(root, "Staging")));
    }

    [Fact]
    public void MissingBackupAfterIntentBlocksRecoveryAndPreservesJournal()
    {
        var (baseStore, files, _, root) = Harness();
        var initial = Wall();
        baseStore.InitializeFresh(initial);
        var add = baseStore.Commit(initial, Plan(baseStore, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://old.example")), true);
        var before = add.CommittedDocument!;
        var fault = new FaultingFiles(files);
        var store = new WallStore(fault, new FakeLinkFileService(fault), root);
        var request = Plan(store, before, before with { Generation = 2 },
            new EntryChange.ChangeTarget("a", "https://new.example"));
        fault.Rules.Add((kind, src, dst) => kind == "move" && src?.EndsWith("entry-a.tmp", StringComparison.Ordinal) == true &&
            dst?.Contains(Path.DirectorySeparatorChar + "Objects" + Path.DirectorySeparatorChar, StringComparison.Ordinal) == true);
        fault.Rules.Add((kind, src, dst) => kind == "copy" && src?.Contains(Path.DirectorySeparatorChar + "Recovery" + Path.DirectorySeparatorChar + "Entries", StringComparison.Ordinal) == true &&
            dst?.Contains(Path.DirectorySeparatorChar + "Objects" + Path.DirectorySeparatorChar, StringComparison.Ordinal) == true);
        Assert.Equal(WallCommitStatus.RecoveryBlocked, store.Commit(before, request, true).Status);
        var stage = Assert.Single(files.ListDirectories(Path.Combine(root, "Staging")));
        var id = Path.GetFileName(stage);
        var journal = Path.Combine(stage, "commit.json");
        files.Delete(Path.Combine(root, "Recovery", "Entries", id, "a", "old.entry"));
        Assert.Equal(WallOpenStatus.RecoveryBlocked, store.Open().Status);
        Assert.True(files.Exists(journal));
        Assert.True(files.Exists(Path.Combine(stage, "install.intent")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedCommitPreviousBackupCanRecoverFromPriorSuccessfulNewState(bool deletePrimary)
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var add = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://old.example")), true);
        var current = add.CommittedDocument!;
        var entry = current.Lanes[0].Groups[0].Members[0].Entry!;
        var path = EntryPath(root, entry);
        var bytes = files.ReadAllBytes(path).ToArray();
        files.InjectedFault = FaultPoint.BeforeMove;
        var request = Plan(store, current, current with { Generation = 2 },
            new EntryChange.ChangeTarget("a", "https://new.example"));
        Assert.Equal(WallCommitStatus.Rejected, store.Commit(current, request, true).Status);
        files.InjectedFault = FaultPoint.None;
        Assert.Equal(current.Generation,
            WallJson.Deserialize(files.ReadAllBytes(Path.Combine(root, "Recovery", "config.prev.json"))).Generation);
        var primary = Path.Combine(root, "config.json");
        if (deletePrimary) files.Delete(primary);
        else files.WriteAllBytes(primary, Encoding.UTF8.GetBytes("{\"schemaVersion\":3,\"lanes\":null}"));
        var recovered = store.Open();
        Assert.Equal(WallOpenStatus.RecoveredPrevious, recovered.Status);
        Assert.Equal(bytes, files.ReadAllBytes(path));
        Assert.Equal(WallOpenStatus.Loaded, store.Open().Status);
    }

    [Fact]
    public void InvalidCommitDoesNotSweepPreexistingPreIntentResidue()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var stage = Path.Combine(root, "Staging", Guid.NewGuid().ToString("N"));
        files.CreateDirectory(Path.Combine(root, "Staging"));
        files.CreateDirectory(stage);
        files.WriteAllBytes(Path.Combine(stage, "config.tmp"), [1]);
        var malformed = Wall(Tile("a")) with { Generation = 1, Rows = 1 };
        Assert.Contains("WALL_GEOMETRY", WallLayoutEngine.ValidateGeometry(malformed));
        var result = store.Commit(initial, new WallCommitRequest(0, malformed, []), true);
        Assert.Equal(WallCommitStatus.Rejected, result.Status);
        Assert.True(files.Exists(Path.Combine(stage, "config.tmp")));
        Assert.Equal(0, WallJson.Deserialize(files.ReadAllBytes(Path.Combine(root, "config.json"))).Generation);
    }

    [Fact]
    public void SecondCorruptionAfterRecoveryStillHasTrustedPreviousMaterial()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var added = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://old.example")), true);
        var withEntry = added.CommittedDocument!;

        var settingsOnly = withEntry with
        {
            Generation = withEntry.Generation + 1,
            Settings = withEntry.Settings with { ShowGrid = true },
        };
        var settings = store.Commit(withEntry, Plan(store, withEntry, settingsOnly), true);
        Assert.Equal(WallCommitStatus.Committed, settings.Status);
        var previous = withEntry;
        var previousBytes = files.ReadAllBytes(Path.Combine(root, "Recovery", "config.prev.json"));
        Assert.Equal(previous.Generation, WallJson.Deserialize(previousBytes).Generation);

        var configPath = Path.Combine(root, "config.json");
        files.WriteAllBytes(configPath, Encoding.UTF8.GetBytes("{\"schemaVersion\":3,\"lanes\":null}"));
        var recovered = store.Open();
        Assert.Equal(WallOpenStatus.RecoveredPrevious, recovered.Status);
        Assert.Equal(previous.Generation + 1, recovered.Document!.Generation);
        Assert.Equal(WallJson.Serialize(previous),
            WallJson.Serialize(recovered.Document with { Generation = previous.Generation }));
        Assert.Equal(previousBytes, files.ReadAllBytes(Path.Combine(root, "Recovery", "config.prev.json")));

        // The recovery itself must not delete the journal that proves config.prev.
        files.WriteAllBytes(configPath, Encoding.UTF8.GetBytes("{\"schemaVersion\":3,\"lanes\":null}"));
        var recoveredAgain = store.Open();
        Assert.Equal(WallOpenStatus.RecoveredPrevious, recoveredAgain.Status);
        Assert.Equal(previous.Generation + 1, recoveredAgain.Document!.Generation);
        Assert.Equal(WallJson.Serialize(previous),
            WallJson.Serialize(recoveredAgain.Document with { Generation = previous.Generation }));
    }

    [Fact]
    public void HardInterruptedInitializationTempIsPreservedAndBlocksFresh()
    {
        var (store, files, _, root) = Harness();
        var path = Path.Combine(root, "config.init.tmp");
        files.WriteAllBytes(path, WallJson.Serialize(Wall()));
        var opened = store.Open();
        Assert.Equal(WallOpenStatus.RecoveryBlocked, opened.Status);
        Assert.Contains("config.init.tmp", Assert.Single(opened.Diagnostics), StringComparison.Ordinal);
        Assert.True(files.Exists(path));
        Assert.Equal(WallOpenStatus.RecoveryBlocked, store.InitializeFresh(Wall()).Status);
        Assert.False(files.Exists(Path.Combine(root, "config.json")));
    }

    [Fact]
    public void FreshCreateOrTempWriteFailureIsOpenFailedWithoutConfig()
    {
        var inner = new InMemoryFileStore();
        var files = new FaultingFiles(inner);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wall-storage-" + Guid.NewGuid().ToString("N")));
        var store = new WallStore(files, new FakeLinkFileService(inner), root);
        files.Rules.Add((_, _, _) => true);
        var created = store.InitializeFresh(Wall());
        Assert.Equal(WallOpenStatus.OpenFailed, created.Status);
        Assert.Contains("IOException", string.Join(" ", created.Diagnostics), StringComparison.Ordinal);
        Assert.False(files.Exists(Path.Combine(root, "config.json")));

        files.Rules.Clear();
        files.Rules.Add((kind, _, destination) => kind == "write" &&
            destination?.EndsWith("config.init.tmp", StringComparison.Ordinal) == true);
        var written = store.InitializeFresh(Wall());
        Assert.Equal(WallOpenStatus.OpenFailed, written.Status);
        Assert.False(files.Exists(Path.Combine(root, "config.init.tmp")));
        Assert.False(files.Exists(Path.Combine(root, "config.json")));
    }

    [Fact]
    public void TempCleanupFailureStaysRecoveryBlockedAndKeepsTheTempFile()
    {
        var inner = new InMemoryFileStore();
        var files = new FaultingFiles(inner);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wall-storage-" + Guid.NewGuid().ToString("N")));
        var store = new WallStore(files, new FakeLinkFileService(inner), root);
        inner.InjectedFault = FaultPoint.BeforeMove;
        files.Rules.Add((kind, source, _) => kind == "delete" &&
            source?.EndsWith("config.init.tmp", StringComparison.Ordinal) == true);
        var failed = store.InitializeFresh(Wall());
        Assert.Equal(WallOpenStatus.RecoveryBlocked, failed.Status);
        Assert.True(files.Exists(Path.Combine(root, "config.init.tmp")));
        Assert.False(files.Exists(Path.Combine(root, "config.json")));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void WholePuzzleBelowMinimumIsRejectedAndConfigStays(int width, int height)
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var configPath = Path.Combine(root, "config.json");
        var config = files.ReadAllBytes(configPath).ToArray();
        var puzzle = new PuzzleMember
        {
            Id = "puzzle", LocalBounds = new GridRect(0, 0, width, height),
            Partitions = [new GridRect(0, 0, width, height)]
        };
        var candidate = initial with
        {
            Generation = 1,
            Lanes = [new WallLane { Groups = [new StartGroup { Id = "group0", Members = [puzzle] }] }]
        };
        var rejected = store.Commit(initial, new WallCommitRequest(initial.Generation, candidate, []), true);
        Assert.Equal(WallCommitStatus.Rejected, rejected.Status);
        Assert.Contains("PUZZLE_SIZE", string.Join(" ", rejected.Diagnostics), StringComparison.Ordinal);
        Assert.Equal(config, files.ReadAllBytes(configPath));
    }

    [Fact]
    public void RecoveryCleanupFailureKeepsSourceUntilSweepRetries()
    {
        var inner = new InMemoryFileStore();
        var files = new FaultingFiles(inner);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wall-storage-" + Guid.NewGuid().ToString("N")));
        var store = new WallStore(files, new FakeLinkFileService(inner), root);
        var initial = Wall();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var added = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://old.example")), true);
        var withEntry = added.CommittedDocument!;
        var entryPath = EntryPath(root, withEntry.Lanes[0].Groups[0].Members[0].Entry!);
        var entryBytes = files.ReadAllBytes(entryPath).ToArray();
        var settingsOnly = withEntry with
        {
            Generation = withEntry.Generation + 1,
            Settings = withEntry.Settings with { ShowGrid = true },
        };
        Assert.Equal(WallCommitStatus.Committed, store.Commit(withEntry, Plan(store, withEntry, settingsOnly), true).Status);

        files.Rules.Add((kind, source, _) => kind == "rmdir" &&
            source?.Contains(Path.DirectorySeparatorChar + "Staging" + Path.DirectorySeparatorChar, StringComparison.Ordinal) == true);
        var configPath = Path.Combine(root, "config.json");
        files.WriteAllBytes(configPath, Encoding.UTF8.GetBytes("{\"schemaVersion\":3,\"lanes\":null}"));
        var recovered = store.Open();
        Assert.Equal(WallOpenStatus.RecoveredPrevious, recovered.Status);
        Assert.Contains("cleanup", string.Join(" ", recovered.Diagnostics), StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(files.ListDirectories(Path.Combine(root, "Staging")));
        Assert.Equal(entryBytes, files.ReadAllBytes(entryPath));

        files.Rules.Clear();
        Assert.Equal(WallOpenStatus.Loaded, store.Open().Status);
        Assert.Empty(files.ListDirectories(Path.Combine(root, "Staging")));
        files.WriteAllBytes(Path.Combine(root, "Recovery", "corrupt-not-a-commit.json"), [1]);
        files.WriteAllBytes(configPath, Encoding.UTF8.GetBytes("{\"schemaVersion\":3,\"lanes\":null}"));
        var again = store.Open();
        Assert.Equal(WallOpenStatus.RecoveredPrevious, again.Status);
        Assert.Equal(entryBytes, files.ReadAllBytes(entryPath));
        var diagnostics = files.ListFiles(Path.Combine(root, "Recovery"))
            .Select(Path.GetFileName)
            .Where(name => name!.StartsWith("corrupt-", StringComparison.Ordinal))
            .ToArray();
        Assert.Contains("corrupt-not-a-commit.json", diagnostics);
        Assert.Single(diagnostics, name => name!.StartsWith("corrupt-", StringComparison.Ordinal) &&
            name.EndsWith(".json", StringComparison.Ordinal) && name.Length == "corrupt-".Length + 32 + ".json".Length);
    }

    [Fact]
    public void OrdinaryCommitDoesNotReparseConfigAndStillHashesUnchangedEntries()
    {
        var inner = new InMemoryFileStore();
        var files = new CountingFiles(inner);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wall-storage-" + Guid.NewGuid().ToString("N")));
        var store = new WallStore(files, new FakeLinkFileService(inner), root);
        var initial = Wall(Tile("a", "A"));
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var seeded = store.Commit(initial, Plan(store, initial, initial with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://example.com")), true);
        var current = seeded.CommittedDocument!;
        var entryPath = EntryPath(root, current.Lanes[0].Groups[0].Members[0].Entry!);
        files.ConfigReads = 0;
        files.EntryReads = 0;
        var saved = store.Commit(current, Plan(store, current, current with
        {
            Generation = current.Generation + 1,
            Settings = current.Settings with { ShowGrid = true },
        }), false);
        Assert.Equal(WallCommitStatus.Committed, saved.Status);
        // Match, preflight hash, transaction bytes, and the post-install fingerprint. No second document parse.
        Assert.Equal(4, files.ConfigReads);
        Assert.Equal(1, files.EntryReads);
        Assert.True(files.Exists(entryPath));
    }

    [Fact]
    public void FuturePreviousBlocksCommitWithoutInstallingEntries()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var config = files.ReadAllBytes(Path.Combine(root, "config.json")).ToArray();
        files.CreateDirectory(Path.Combine(root, "Recovery"));
        files.WriteAllBytes(Path.Combine(root, "Recovery", "config.prev.json"),
            Encoding.UTF8.GetBytes($"{{\"schemaVersion\":{WallDocument.CurrentSchemaVersion + 1}}}"));
        var candidate = initial with { Generation = 1, Settings = initial.Settings with { ShowGrid = true } };
        var blocked = store.Commit(initial, new WallCommitRequest(0, candidate, []), false);
        Assert.Equal(WallCommitStatus.RecoveryBlocked, blocked.Status);
        Assert.Equal(config, files.ReadAllBytes(Path.Combine(root, "config.json")));
        Assert.Empty(files.ListDirectories(Path.Combine(root, "Staging")));
    }

    [Fact]
    public void PersistentCleanupFailureBlocksOpenAndCommitUntilRetry()
    {
        var (store, files, root, recovered) = RecoverWithStickyStagingCleanup();
        var configPath = Path.Combine(root, "config.json");
        var config = files.ReadAllBytes(configPath).ToArray();
        var stages = files.ListDirectories(Path.Combine(root, "Staging")).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(stages);
        Assert.Equal(recovered.Generation, WallJson.Deserialize(config).Generation);

        var blockedOpen = store.Open();
        Assert.Equal(WallOpenStatus.RecoveryBlocked, blockedOpen.Status);
        Assert.Contains("Committed cleanup remains", string.Join(" ", blockedOpen.Diagnostics), StringComparison.Ordinal);
        Assert.Equal(config, files.ReadAllBytes(configPath));
        Assert.Equal(stages, files.ListDirectories(Path.Combine(root, "Staging")).Order(StringComparer.Ordinal).ToArray());

        var candidate = recovered with
        {
            Generation = recovered.Generation + 1,
            Settings = recovered.Settings with { ShowGrid = !recovered.Settings.ShowGrid },
        };
        var blockedCommit = store.Commit(recovered, Plan(store, recovered, candidate), true);
        Assert.Equal(WallCommitStatus.RecoveryBlocked, blockedCommit.Status);
        Assert.Contains("Committed cleanup remains", string.Join(" ", blockedCommit.Diagnostics), StringComparison.Ordinal);
        Assert.Equal(config, files.ReadAllBytes(configPath));
        Assert.Equal(stages, files.ListDirectories(Path.Combine(root, "Staging")).Order(StringComparer.Ordinal).ToArray());

        files.Sticky.Clear();
        var loaded = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, loaded.Status);
        Assert.Equal(recovered.Generation, loaded.Document!.Generation);
        Assert.Empty(files.ListDirectories(Path.Combine(root, "Staging")));
        Assert.Equal(config, files.ReadAllBytes(configPath));

        var saved = store.Commit(loaded.Document, Plan(store, loaded.Document, candidate), true);
        Assert.Equal(WallCommitStatus.Committed, saved.Status);
        Assert.Equal(recovered.Generation + 1, saved.CommittedDocument!.Generation);
        Assert.Empty(files.ListDirectories(Path.Combine(root, "Staging")));
    }

    [Fact]
    public void OrdinaryUndoKeepsTheLatestCorruptDiagnostic()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var added = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://old.example")), true);
        var withEntry = added.CommittedDocument!;
        var edited = store.Commit(withEntry, Plan(store, withEntry, withEntry with { Generation = 2 },
            new EntryChange.ChangeTarget("a", "https://new.example")), true);
        Assert.Equal(WallCommitStatus.Committed, edited.Status);
        files.WriteAllBytes(Path.Combine(root, "config.json"), Encoding.UTF8.GetBytes("{\"schemaVersion\":3,\"lanes\":null}"));

        var recovered = store.Open();
        Assert.Equal(WallOpenStatus.RecoveredPrevious, recovered.Status);
        var loaded = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, loaded.Status);
        var current = loaded.Document!;
        var latest = Assert.Single(StrictCorruptNames(files, root));
        var decoy = "corrupt-" + new string('a', 32) + ".json";
        files.WriteAllBytes(Path.Combine(root, "Recovery", decoy), [1]);

        var saved = store.Commit(current, Plan(store, current, current with
        {
            Generation = current.Generation + 1,
            Settings = current.Settings with { ShowGrid = !current.Settings.ShowGrid },
        }), true);
        Assert.Equal(WallCommitStatus.Committed, saved.Status);
        Assert.NotNull(saved.Undo);
        Assert.Contains(latest, StrictCorruptNames(files, root));
        Assert.True(files.Exists(Path.Combine(root, "Recovery", decoy)));

        var undone = store.Undo(saved.CommittedDocument!, current, saved.Undo!, saved.CommittedDocument!.Generation);
        Assert.Equal(WallCommitStatus.Committed, undone.Status);
        Assert.Contains(latest, StrictCorruptNames(files, root));
        Assert.True(files.Exists(Path.Combine(root, "Recovery", decoy)));
        Assert.DoesNotContain(StrictCorruptNames(files, root), name => name.Contains(saved.Undo!.CommitId, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CleanupRetryWithoutReadablePreviousDoesNotPruneSources(bool missing)
    {
        var (store, files, root, recovered) = OpenRecoveryStoppedBeforePrune();
        var configPath = Path.Combine(root, "config.json");
        var config = files.ReadAllBytes(configPath).ToArray();
        var previous = Path.Combine(root, "Recovery", "config.prev.json");
        var materials = MaterialIds(files, root);
        var sourceId = OldMatchSourceId(files, root);
        Assert.Contains(sourceId, materials);
        Assert.True(materials.Length >= 2);
        if (missing) files.Delete(previous);
        else files.WriteAllBytes(previous, Encoding.UTF8.GetBytes("{\"schemaVersion\":3}"));
        var retained = RecoveryEntryFiles(files, root);
        var stages = files.ListDirectories(Path.Combine(root, "Staging")).Order(StringComparer.Ordinal).ToArray();

        var blocked = store.Open();
        Assert.Equal(WallOpenStatus.RecoveryBlocked, blocked.Status);
        Assert.Equal(config, files.ReadAllBytes(configPath));
        Assert.Equal(missing, !files.Exists(previous));
        if (!missing) Assert.Equal(Encoding.UTF8.GetBytes("{\"schemaVersion\":3}"), files.ReadAllBytes(previous));
        Assert.Equal(materials, MaterialIds(files, root));
        Assert.True(files.Exists(Path.Combine(root, "Recovery", "Entries", sourceId, "journal.json")));
        Assert.Equal(stages, files.ListDirectories(Path.Combine(root, "Staging")).Order(StringComparer.Ordinal).ToArray());
        foreach (var path in retained) Assert.True(files.Exists(path));

        var candidate = recovered with
        {
            Generation = recovered.Generation + 1,
            Settings = recovered.Settings with { ShowGrid = !recovered.Settings.ShowGrid },
        };
        var commit = store.Commit(recovered, Plan(store, recovered, candidate), false);
        Assert.Equal(WallCommitStatus.RecoveryBlocked, commit.Status);
        Assert.Equal(config, files.ReadAllBytes(configPath));
        Assert.Equal(materials, MaterialIds(files, root));
        foreach (var path in retained) Assert.True(files.Exists(path));
    }

    [Fact]
    public void DamagedOldMatchBackupRefusesCleanupAndKeepsEverySource()
    {
        var (store, files, root) = FaultedHarness();
        var initial = Wall();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var added = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://old.example")), true);
        var alternateId = Assert.Single(MaterialIds(files, root));
        var alternateJournal = files.ReadAllBytes(Path.Combine(root, "Recovery", "Entries", alternateId, "journal.json")).ToArray();
        var withEntry = added.CommittedDocument!;
        var edited = store.Commit(withEntry, Plan(store, withEntry, withEntry with { Generation = 2 },
            new EntryChange.ChangeTarget("a", "https://new.example")), true);
        Assert.Equal(WallCommitStatus.Committed, edited.Status);
        Assert.DoesNotContain(alternateId, MaterialIds(files, root));

        files.Rules.Add((kind, _, destination) => kind == "copy" &&
            destination?.EndsWith(Path.DirectorySeparatorChar + "new.snapshot.json", StringComparison.Ordinal) == true);
        var configPath = Path.Combine(root, "config.json");
        files.WriteAllBytes(configPath, Encoding.UTF8.GetBytes("{\"schemaVersion\":3,\"lanes\":null}"));
        var recovered = store.Open();
        Assert.Equal(WallOpenStatus.RecoveredPrevious, recovered.Status);
        Assert.NotEmpty(files.ListDirectories(Path.Combine(root, "Staging")));

        var alternateDir = Path.Combine(root, "Recovery", "Entries", alternateId);
        files.CreateDirectory(alternateDir);
        files.WriteAllBytes(Path.Combine(alternateDir, "journal.json"), alternateJournal);
        var sourceId = OldMatchSourceId(files, root);
        Assert.NotEqual(alternateId, sourceId);
        var backup = Path.Combine(root, "Recovery", "Entries", sourceId, "a", "old.entry");
        Assert.True(files.Exists(backup));
        var damaged = new byte[] { 9, 9, 9 };
        files.WriteAllBytes(backup, damaged);
        var config = files.ReadAllBytes(configPath).ToArray();
        var materials = MaterialIds(files, root);
        Assert.Contains(alternateId, materials);
        Assert.Contains(sourceId, materials);
        Assert.True(materials.Length >= 3);
        var retained = RecoveryEntryFiles(files, root);

        var blocked = store.Open();
        Assert.Equal(WallOpenStatus.RecoveryBlocked, blocked.Status);
        Assert.Equal(config, files.ReadAllBytes(configPath));
        Assert.Equal(materials, MaterialIds(files, root));
        Assert.Equal(damaged, files.ReadAllBytes(backup));
        Assert.NotEmpty(files.ListDirectories(Path.Combine(root, "Staging")));
        foreach (var path in retained) Assert.True(files.Exists(path));
        Assert.True(files.Exists(Path.Combine(alternateDir, "journal.json")));
        Assert.True(files.Exists(Path.Combine(root, "Recovery", "Entries", sourceId, "journal.json")));
    }

    private static (WallStore Store, FaultingFiles Files, string Root, WallDocument Recovered) RecoverWithStickyStagingCleanup()
    {
        var (store, files, root) = FaultedHarness();
        SeedEntryThenChangeTarget(store);
        files.Sticky.Add((kind, source, _) => kind == "rmdir" &&
            source?.Contains(Path.DirectorySeparatorChar + "Staging" + Path.DirectorySeparatorChar, StringComparison.Ordinal) == true);
        files.WriteAllBytes(Path.Combine(root, "config.json"), Encoding.UTF8.GetBytes("{\"schemaVersion\":3,\"lanes\":null}"));
        var recovered = store.Open();
        Assert.Equal(WallOpenStatus.RecoveredPrevious, recovered.Status);
        Assert.Contains("cleanup", string.Join(" ", recovered.Diagnostics), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(recovered.Document);
        return (store, files, root, recovered.Document);
    }

    private static (WallStore Store, FaultingFiles Files, string Root, WallDocument Recovered) OpenRecoveryStoppedBeforePrune()
    {
        var (store, files, root) = FaultedHarness();
        var edited = SeedEntryThenChangeTarget(store);
        files.Rules.Add((kind, _, destination) => kind == "copy" &&
            destination?.EndsWith(Path.DirectorySeparatorChar + "new.snapshot.json", StringComparison.Ordinal) == true);
        files.WriteAllBytes(Path.Combine(root, "config.json"), Encoding.UTF8.GetBytes("{\"schemaVersion\":3,\"lanes\":null}"));
        var recovered = store.Open();
        Assert.Equal(WallOpenStatus.RecoveredPrevious, recovered.Status);
        Assert.Contains("cleanup", string.Join(" ", recovered.Diagnostics), StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(files.ListDirectories(Path.Combine(root, "Staging")));
        Assert.NotNull(recovered.Document);
        Assert.Equal(edited.Generation, WallJson.Deserialize(files.ReadAllBytes(Path.Combine(root, "Recovery", "config.prev.json"))).Generation + 1);
        return (store, files, root, recovered.Document);
    }

    private static (WallStore Store, FaultingFiles Files, string Root) FaultedHarness()
    {
        var inner = new InMemoryFileStore();
        var files = new FaultingFiles(inner);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wall-storage-" + Guid.NewGuid().ToString("N")));
        return (new WallStore(files, new FakeLinkFileService(inner), root), files, root);
    }

    private static WallDocument SeedEntryThenChangeTarget(WallStore store)
    {
        var initial = Wall();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var added = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://old.example")), true);
        var withEntry = added.CommittedDocument!;
        var edited = store.Commit(withEntry, Plan(store, withEntry, withEntry with { Generation = 2 },
            new EntryChange.ChangeTarget("a", "https://new.example")), true);
        Assert.Equal(WallCommitStatus.Committed, edited.Status);
        return edited.CommittedDocument!;
    }

    private static string[] MaterialIds(IFileStore files, string root) =>
        [.. files.ListDirectories(Path.Combine(root, "Recovery", "Entries")).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    private static string[] RecoveryEntryFiles(IFileStore files, string root)
    {
        var found = new List<string>();
        void Walk(string directory)
        {
            found.AddRange(files.ListFiles(directory));
            foreach (var child in files.ListDirectories(directory)) Walk(child);
        }
        Walk(Path.Combine(root, "Recovery", "Entries"));
        return [.. found.Order(StringComparer.Ordinal)];
    }

    private static string[] StrictCorruptNames(IFileStore files, string root) =>
        [.. files.ListFiles(Path.Combine(root, "Recovery")).Select(Path.GetFileName)
            .Where(name => name!.Length == "corrupt-".Length + 32 + ".json".Length &&
                name.StartsWith("corrupt-", StringComparison.Ordinal) &&
                name.EndsWith(".json", StringComparison.Ordinal) &&
                name.Skip("corrupt-".Length).Take(32).All(Uri.IsHexDigit))!];

    private static string OldMatchSourceId(IFileStore files, string root)
    {
        var previousHash = Convert.ToHexString(SHA256.HashData(
            files.ReadAllBytes(Path.Combine(root, "Recovery", "config.prev.json"))));
        string? match = null;
        foreach (var id in MaterialIds(files, root))
        {
            var journal = JsonNode.Parse(files.ReadAllBytes(
                Path.Combine(root, "Recovery", "Entries", id, "journal.json")))!;
            if (!string.Equals((string?)journal["oldHash"], previousHash, StringComparison.Ordinal)) continue;
            Assert.Null(match);
            match = id;
        }
        Assert.NotNull(match);
        return match;
    }

    private sealed class CountingFiles(IFileStore inner) : IFileStore
    {
        public int ConfigReads;
        public int EntryReads;
        public FileStorePathInfo GetPathInfo(string path) => inner.GetPathInfo(path);
        public bool Exists(string path) => inner.Exists(path);
        public byte[] ReadAllBytes(string path)
        {
            if (path.EndsWith(Path.DirectorySeparatorChar + "config.json", StringComparison.OrdinalIgnoreCase))
                ConfigReads++;
            else if (path.Contains(Path.DirectorySeparatorChar + "Objects" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                EntryReads++;
            return inner.ReadAllBytes(path);
        }
        public void WriteAllBytes(string path, byte[] bytes) => inner.WriteAllBytes(path, bytes);
        public void Move(string src, string dst, bool overwrite) => inner.Move(src, dst, overwrite);
        public void Copy(string src, string dst, bool overwrite) => inner.Copy(src, dst, overwrite);
        public void Delete(string path) => inner.Delete(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public IReadOnlyList<string> ListDirectories(string path) => inner.ListDirectories(path);
        public IReadOnlyList<string> ListFiles(string path) => inner.ListFiles(path);
        public void DeleteDirectory(string path) => inner.DeleteDirectory(path);
    }
}
