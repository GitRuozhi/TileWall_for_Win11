using TileWall.Core.Application;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Application;

public sealed class RecycleBinOnDemandTests
{
    private const string TinyPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aHZkAAAAASUVORK5CYII=";

    private sealed class InlineExecutor : IWallStorageExecutor
    {
        public Task<T> ExecuteAsync<T>(Func<T> work)
        {
            try { return Task.FromResult(work()); }
            catch (Exception exception) { return Task.FromException<T>(exception); }
        }

        public Task ShutdownAsync() => Task.CompletedTask;
    }

    private static TileMember Tile(string id, int row = 0) => new()
    {
        Id = id,
        LocalBounds = new GridRect(0, row, 1, 1),
        Visual = new MemberVisual { Title = id },
    };

    private static TileMember TileAt(string id, int column, int row) => new()
    {
        Id = id,
        LocalBounds = new GridRect(column, row, 1, 1),
        Visual = new MemberVisual { Title = id },
    };

    private static WallDocument Wall(params WallMember[] activeMembers)
    {
        var groups = activeMembers.Length == 0
            ? Array.Empty<StartGroup>()
            : [new StartGroup { Id = "active-group", Members = activeMembers }];
        return WallDocument.Empty(1, 40) with
        {
            Lanes = [new WallLane { Groups = groups }],
        };
    }

    private static (WallStore Store, InMemoryFileStore Files, FakeLinkFileService Links, string Root,
        WallDocument Active, WallDocument Stored) SeedRecycleBin(
        IReadOnlyList<WallMember> retained,
        IReadOnlyList<EntryChange>? changes = null,
        IReadOnlyList<PreparedMediaAsset>? mediaAssets = null)
    {
        var files = new InMemoryFileStore();
        var links = new FakeLinkFileService(files);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "recycle-bin-on-demand-" + Guid.NewGuid().ToString("N")));
        var store = new WallStore(files, links, root);
        var initial = Wall(Tile("active"));
        var initialized = store.InitializeFresh(initial);
        if (initialized.Status != WallOpenStatus.Loaded)
            throw new InvalidOperationException(string.Join("; ", initialized.Diagnostics));

        var candidate = initial with { Generation = 1, InactiveMembers = retained };
        var committed = store.Commit(initial, new(0, candidate, changes ?? [], mediaAssets));
        if (committed.Status != WallCommitStatus.Committed || committed.CommittedDocument is null)
            throw new InvalidOperationException(string.Join("; ", committed.Diagnostics));
        var stored = committed.CommittedDocument;
        return (store, files, links, root, stored.ActiveView(), stored);
    }

    [Fact]
    public void OpenAndOrdinaryLayoutCommitExposeActiveViewAndPreserveRecycleFifoResourcesAcrossReopen()
    {
        var assetBytes = Convert.FromBase64String(TinyPng);
        var asset = PreparedMediaAsset.FromVerifiedImage(assetBytes, ".png");
        var retainedImage = Tile("retained-image") with
        {
            Images = new MemberImageSource { Kind = MemberImageSourceKind.Single, Media = asset.Reference },
        };
        var (store, files, links, root, _, seeded) = SeedRecycleBin(
            [Tile("oldest"), retainedImage],
            [new EntryChange.CreateUrl("retained-image", "https://retained.example/")],
            [asset]);

        var original = Assert.Single(seeded.InactiveMembers, member => member.Id == "retained-image");
        Assert.NotNull(original.Entry);
        var entryPath = Path.Combine(root, original.Entry!.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var originalEntryBytes = files.ReadAllBytes(entryPath);
        var mediaPath = store.ManagedMediaPath(original.Images.Media!);
        var originalMediaBytes = files.ReadAllBytes(mediaPath);

        var session = new WallSession(root, files, links);
        var refreshed = new List<WallDocument>();
        session.SetViewRefresh(refreshed.Add);
        var opened = session.Open(1, 40);

        Assert.Equal(WallOpenStatus.Loaded, opened.Status);
        Assert.NotNull(opened.Document);
        Assert.Empty(opened.Document!.InactiveMembers);
        Assert.Empty(session.Current.InactiveMembers);
        Assert.True(session.RefreshViewFromCurrent());

        var active = Assert.IsType<TileMember>(Assert.Single(session.Current.EnumerateAllMembers()));
        var layout = WallLayoutEngine.TryApply(session.Current,
            new ReplaceMemberAction(session.Current.Generation, active.Id,
                active with { Visual = active.Visual with { Title = "Updated" } }), 1000, 1000);
        Assert.True(layout.Success, layout.Detail);
        Assert.Empty(layout.Proposal!.Document.InactiveMembers);

        var saved = session.Commit(layout.Proposal);
        Assert.True(saved.Committed, string.Join("; ", saved.Diagnostics));
        Assert.Empty(session.Current.InactiveMembers);
        Assert.All(refreshed, view => Assert.Empty(view.InactiveMembers));
        Assert.Same(session.Current, refreshed[^1]);

        var reopened = new WallStore(files, links, root).Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Equal(new[] { "oldest", "retained-image" },
            reopened.Document!.InactiveMembers.Select(member => member.Id));
        var persistedImage = reopened.Document.InactiveMembers.Single(member => member.Id == "retained-image");
        Assert.Equal(original.Entry, persistedImage.Entry);
        Assert.Equal(asset.Reference, persistedImage.Images.Media);
        Assert.Equal(originalEntryBytes, files.ReadAllBytes(entryPath));
        Assert.Equal(originalMediaBytes, files.ReadAllBytes(new WallStore(files, links, root)
            .ManagedMediaPath(persistedImage.Images.Media!)));
    }

    [Fact]
    public async Task RecycleBinReadIsOnDemandAndRejectsStaleOrCancelledReadsWithoutConsuming()
    {
        var (store, files, links, root, _, _) = SeedRecycleBin([Tile("oldest"), Tile("newest")]);
        var session = new WallSession(root, files, links);
        var opened = session.Open(1, 40);
        Assert.Equal(WallOpenStatus.Loaded, opened.Status);
        Assert.Empty(session.Current.InactiveMembers);
        session.ConfigureAsyncExecution(new InlineExecutor(), _ => Task.CompletedTask);

        var generation = session.Current.Generation;
        var snapshot = await session.ReadRecycleBinAsync(generation);

        Assert.Equal(generation, snapshot.Generation);
        Assert.Equal(64, snapshot.ManifestSha256.Length);
        Assert.Equal(new[] { "oldest", "newest" }, snapshot.Members.Select(member => member.Id));
        Assert.Empty(session.Current.InactiveMembers);
        Assert.False(session.WritePending);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadRecycleBinAsync(generation + 1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadRecycleBinAsync(generation, cancellation.Token));
        Assert.Empty(session.Current.InactiveMembers);
        Assert.False(session.WritePending);

        var stillStored = store.Open();
        Assert.Equal(WallOpenStatus.Loaded, stillStored.Status);
        Assert.Equal(new[] { "oldest", "newest" }, stillStored.Document!.InactiveMembers.Select(member => member.Id));
    }

    [Fact]
    public void RecycleBinProposalsKeepUnpinFifoAndRejectOverflowCancelOrInvalidRestore()
    {
        var metrics = new WallMetrics();
        var width = metrics.WallWidth(4);
        var height = metrics.WallHeight(60);
        var current = Wall(Tile("active"));
        var oneItem = new RecycleBinSnapshot(current.Generation, new string('A', 64), [Tile("retained")]);

        var unpinned = RecycleBinRules.Propose(current, oneItem,
            new UnpinMemberAction(current.Generation, "active"), width, height, metrics);

        Assert.True(unpinned.Success, unpinned.Detail);
        Assert.Empty(unpinned.Proposal!.Document.InactiveMembers);
        Assert.Equal(new[] { "retained", "active" }, unpinned.Proposal.RecycleBin!.Members.Select(member => member.Id));
        Assert.Single(current.EnumerateAllMembers());

        var full = new RecycleBinSnapshot(current.Generation, new string('B', 64),
            Enumerable.Range(0, RecycleBinRules.Capacity).Select(index => (WallMember)Tile("retained-" + index)).ToArray());
        var cancelled = RecycleBinRules.Propose(current, full,
            new UnpinMembersAction(current.Generation, ["active"]), width, height, metrics);
        Assert.False(cancelled.Success);
        Assert.Equal("RECYCLE_OVERFLOW:1", cancelled.Detail);
        Assert.Equal(RecycleBinRules.Capacity, full.Members.Count);
        Assert.Equal("retained-0", full.Members[0].Id);
        Assert.Single(current.EnumerateAllMembers());

        var restoreSnapshot = new RecycleBinSnapshot(current.Generation, new string('C', 64), [Tile("restore-me")]);
        var restored = RecycleBinRules.Propose(current, restoreSnapshot,
            new RestoreRecycledMembersAction(current.Generation, ["restore-me"], "restored", "Restored",
                new Dictionary<string, string>()), width, height, metrics);
        Assert.True(restored.Success, restored.Detail);
        Assert.Empty(restored.Proposal!.Document.InactiveMembers);
        Assert.Empty(restored.Proposal.RecycleBin!.Members);
        Assert.Contains(restored.Proposal.Document.EnumerateAllMembers(), member => member.Id == "restore-me");

        var entryMember = Tile("entry-retained") with
        {
            Entry = new ManagedEntryReference("entry-retained", "Objects/entry-retained/entry.url", ManagedEntryKind.Url),
        };
        var invalidRestoreSnapshot = new RecycleBinSnapshot(current.Generation, new string('D', 64), [entryMember]);
        var rejectedRestore = RecycleBinRules.Propose(current, invalidRestoreSnapshot,
            new RestoreRecycledMembersAction(current.Generation, ["entry-retained"], "restored-entry", "Restored",
                new Dictionary<string, string>()), width, height, metrics);
        Assert.False(rejectedRestore.Success);
        Assert.Equal("RECYCLE_IDENTITY_UNAVAILABLE", rejectedRestore.Detail);
        Assert.Equal("entry-retained", Assert.Single(invalidRestoreSnapshot.Members).Id);
        Assert.Single(current.EnumerateAllMembers());
    }

    [Fact]
    public void ExplicitStoreClearRequiresCurrentGenerationAndCommitsAtomically()
    {
        var (store, files, links, root, _, _) = SeedRecycleBin([Tile("oldest"), Tile("newest")]);
        var active = store.Open().Document!.ActiveView();
        var proposed = active with
        {
            Generation = active.Generation + 1,
            Settings = active.Settings with { AlwaysOnTop = false },
        };

        var stale = store.Commit(active, new(0, proposed, [])
        {
            PreserveStoredRecycleBin = true,
            ClearRecycleBin = true,
        });

        Assert.Equal(WallCommitStatus.Rejected, stale.Status);
        Assert.Equal(new[] { "oldest", "newest" }, store.Open().Document!.InactiveMembers.Select(member => member.Id));

        var committed = store.Commit(active, new(active.Generation, proposed, [])
        {
            PreserveStoredRecycleBin = true,
            ClearRecycleBin = true,
        });

        Assert.Equal(WallCommitStatus.Committed, committed.Status);
        Assert.Empty(committed.CommittedDocument!.InactiveMembers);
        var reopened = new WallStore(files, links, root).Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Empty(reopened.Document!.InactiveMembers);
    }

    [Fact]
    public void SessionCommitAndClearRejectSameGenerationRecycleSnapshotSubstitution()
    {
        var (store, files, links, root, _, seeded) = SeedRecycleBin([Tile("original-retained")]);
        var session = new WallSession(root, files, links);
        var opened = session.Open(1, 40);
        Assert.Equal(WallOpenStatus.Loaded, opened.Status);
        var before = session.Current;
        var originalManifest = files.ReadAllBytes(Path.Combine(root, "state.json"));

        var alternate = seeded with { InactiveMembers = [Tile("substituted-retained")] };
        var alternateManifest = SnapshotTestData.Install(files, root, alternate);
        Assert.False(originalManifest.SequenceEqual(alternateManifest));
        var alternateDocument = SnapshotTestData.Read(files, root, alternateManifest);
        Assert.Equal(before.Generation, alternateDocument.Generation);
        Assert.Equal(WallJson.Serialize(before), WallJson.Serialize(alternateDocument.ActiveView()));
        Assert.Equal("substituted-retained", Assert.Single(alternateDocument.InactiveMembers).Id);

        var active = Assert.IsType<TileMember>(Assert.Single(before.EnumerateAllMembers()));
        var layout = WallLayoutEngine.TryApply(before,
            new ReplaceMemberAction(before.Generation, active.Id,
                active with { Visual = active.Visual with { Title = "Must not save" } }), 1000, 1000);
        Assert.True(layout.Success, layout.Detail);
        var ordinarySave = session.Commit(layout.Proposal!);
        Assert.False(ordinarySave.Committed);
        Assert.Same(before, session.Current);
        Assert.False(session.RequiresReopen);
        Assert.Equal(alternateManifest, files.ReadAllBytes(Path.Combine(root, "state.json")));

        var clearDocument = before with { Generation = before.Generation + 1 };
        var clear = new WallProposal(clearDocument, WallLayoutEngine.Measure(clearDocument))
        {
            ClearRecycleBin = true,
        };
        var clearResult = session.Commit(clear);
        Assert.False(clearResult.Committed);
        Assert.Same(before, session.Current);
        Assert.Equal(alternateManifest, files.ReadAllBytes(Path.Combine(root, "state.json")));
        Assert.Equal("substituted-retained", Assert.Single(SnapshotTestData.Read(files, root, alternateManifest).InactiveMembers).Id);
    }

    [Fact]
    public async Task Unpinning201ManagedActiveMembersRemovesOverflowEntryAndRetainsNext200()
    {
        var activeMembers = Enumerable.Range(0, RecycleBinRules.Capacity + 1)
            .Select(index => (WallMember)TileAt($"member-{index:D3}", index % 8, index / 8)).ToArray();
        var changes = activeMembers.Select(member => (EntryChange)new EntryChange.CreateUrl(member.Id,
            $"https://example.com/{member.Id}")).ToArray();
        var files = new InMemoryFileStore();
        var links = new FakeLinkFileService(files);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "recycle-bin-overflow-" + Guid.NewGuid().ToString("N")));
        var store = new WallStore(files, links, root);
        var initial = Wall(activeMembers);
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var seeded = store.Commit(initial, new(0, initial with { Generation = 1 }, changes));
        Assert.Equal(WallCommitStatus.Committed, seeded.Status);

        var session = new WallSession(root, files, links);
        Assert.Equal(WallOpenStatus.Loaded, session.Open(1, 40).Status);
        var activeIds = session.Current.EnumerateAllMembers().Select(member => member.Id).ToArray();
        Assert.Equal(activeMembers.Select(member => member.Id), activeIds);
        var evictedEntry = session.Current.EnumerateAllMembers().First().Entry!;
        var evictedPath = Path.Combine(root, evictedEntry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        session.ConfigureAsyncExecution(new InlineExecutor(), _ => Task.CompletedTask);
        var snapshot = await session.ReadRecycleBinAsync(session.Current.Generation);
        Assert.Empty(snapshot.Members);

        var metrics = new WallMetrics();
        var unpin = RecycleBinRules.Propose(session.Current, snapshot,
            new UnpinMembersAction(session.Current.Generation, activeIds, ConfirmOverflow: true),
            metrics.WallWidth(4), metrics.WallHeight(60), metrics);
        Assert.True(unpin.Success, unpin.Detail);
        Assert.Equal(activeIds.Skip(1), unpin.Proposal!.RecycleBin!.Members.Select(member => member.Id));

        var committed = session.Commit(unpin.Proposal);
        Assert.True(committed.Committed, string.Join("; ", committed.Diagnostics));
        Assert.False(files.Exists(evictedPath));
        Assert.Empty(session.Current.EnumerateAllMembers());
        Assert.Empty(session.Current.InactiveMembers);

        var reopened = new WallStore(files, links, root).Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Equal(activeIds.Skip(1), reopened.Document!.InactiveMembers.Select(member => member.Id));
        Assert.Empty(reopened.Document.Lanes.SelectMany(lane => lane.Groups).SelectMany(group => group.Members));
        Assert.All(reopened.Document.InactiveMembers, member =>
            Assert.True(files.Exists(Path.Combine(root, member.Entry!.RelativePath.Replace('/', Path.DirectorySeparatorChar)))));
    }
}
