using System.IO.Compression;
using System.Text;
using TileWall.Core.Application;
using TileWall.Core.Configuration;
using TileWall.Core.Exchange;
using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Exchange;

public sealed class WallPackageExportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wall-export-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZ1sAAAAASUVORK5CYII=");
    private static readonly byte[] Shortcut = Encoding.UTF8.GetBytes("[InternetShortcut]\r\nURL=https://example.com/export\r\n");
    public WallPackageExportTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private (WallStore Store, WallDocument Document, string Root, WallPackageReader Reader) Seed(
        Func<ReadOnlyMemory<byte>, string, CancellationToken, Task<bool>>? verifyImage = null)
    {
        var files = new LocalFileStore();
        var root = Path.Combine(_directory, "data"); Directory.CreateDirectory(root);
        var media = PreparedMediaAsset.FromVerifiedImage(Png, ".png").Reference;
        var document = WallJson.Freeze(new WallDocument
        {
            Generation = 5, Columns = 2, Rows = 30,
            Settings = new() { ShowGrid = true, BackgroundMode = WallBackgroundMode.Custom,
                BackgroundMedia = new(new string('B', 64), ".png", 1), BackgroundImagePath = Path.Combine(_directory, "background.zip") },
            RecycleBinEnabled = true,
            InactiveMembers = [new TileMember { Id = "inactive", LocalBounds = new(0, 0, 2, 2) }],
            Lanes =
            [
                new() { Groups = [new() { Id = "omitted", Title = "Omitted", Members =
                    [new TileMember { Id = "unselected", LocalBounds = new(0, 0, 2, 2),
                        Entry = new("unselected", "Objects/unselected/entry.url", ManagedEntryKind.Url),
                        TargetInput = Path.Combine(_directory, "unselected-source.zip") }] }] },
                new() { Groups = [new() { Id = "mixed", Title = "Mixed", UserClaimed = true,
                    ImportSource = new() { BatchId = "old", GroupBucketKey = "old", OriginGroupId = "old" }, Members =
                    [
                        new TileMember { Id = "tile", LocalBounds = new(0, 0, 2, 2),
                            Entry = new("tile", "Objects/tile/entry.url", ManagedEntryKind.Url), TargetInput = "https://example.com/export",
                            Visual = new() { TitleMode = MemberTitleMode.Manual, Title = "Selected", BackgroundMode = MemberBackgroundMode.Color, BackgroundColor = "#FF123456" },
                            Images = new() { Kind = MemberImageSourceKind.Single, Media = media, Path = Path.Combine(_directory, "source.zip") },
                            Carousel = new() { CurrentImageId = "must-not-leak", CandidateIds = ["must-not-leak"], Queue = ["must-not-leak"] } },
                        new PuzzleMember { Id = "puzzle", LocalBounds = new(3, 1, 3, 4),
                            Partitions = [new(0, 0, 1, 4), new(1, 0, 2, 4)], Images = new() { Kind = MemberImageSourceKind.Single, Media = media } },
                        new ClockMember { Id = "clock", LocalBounds = new(0, 7, 4, 2), TimeFormat = "HH:mm:ss", DateFormat = "yyyy/M/d" },
                    ] }] },
            ],
        });
        var entry = Path.Combine(root, "Objects", "tile", "entry.url"); Directory.CreateDirectory(Path.GetDirectoryName(entry)!); File.WriteAllBytes(entry, Shortcut);
        var original = Path.Combine(root, "Media", media.Sha256, "original.png"); Directory.CreateDirectory(Path.GetDirectoryName(original)!); File.WriteAllBytes(original, Png);
        SnapshotTestData.Install(files, root, document);
        // Missing unselected entry/background media prove that selection capture
        // does not read resources outside its selected payload closure.
        var store = new WallStore(files, new FakeLinkFileService(files), root);
        var reader = new WallPackageReader((kind, bytes, token) => Task.FromResult(store.ObservePackageEntry(kind, bytes, token)),
            verifyImage ?? ((bytes, extension, _) => Task.FromResult(extension == ".png" && bytes.Span.SequenceEqual(Png))));
        return (store, document, root, reader);
    }

    [Theory]
    [InlineData(".zip")]
    [InlineData(".ZIP")]
    public async Task SelectedMixedContentKeepsOriginalLaneGroupGeometryAndOnlyItsFormalClosure(string extension)
    {
        var (store, document, _, reader) = Seed();
        using var snapshot = store.CapturePackageSnapshot(document, 5, ["tile", "puzzle", "clock"]);
        var path = Path.Combine(_directory, "selected" + extension);
        var saved = await new WallPackageWriter(reader).WriteAsync(snapshot, path, "1.3.8");
        Assert.Equal(WallPackageWriteStatus.Saved, saved.Status);
        var read = await reader.ReadPreviewAsync(path);
        Assert.Equal(WallPackageReadStatus.Valid, read.Status);
        var content = read.Preview!.Content;
        Assert.Equal(2, content.Columns); Assert.Empty(content.Lanes[0].GroupIds);
        Assert.Equal(new[] { "mixed" }, content.Lanes[1].GroupIds);
        Assert.Equal(new[] { "tile", "puzzle", "clock" }, Assert.Single(content.Groups).MemberIds);
        Assert.Equal("Mixed", content.Groups[0].Title);
        Assert.Equal(new WallPackageBounds(3, 1, 3, 4), content.Members.Single(member => member.Id == "puzzle").LocalBounds);
        Assert.Equal(2, content.Members.Single(member => member.Id == "puzzle").Partitions!.Count);
        Assert.Equal("HH:mm:ss", content.Members.Single(member => member.Id == "clock").TimeFormat);
        Assert.Equal(document.Lanes[1].Groups[0].Members[0].Visual, content.Members[0].Visual);
        Assert.Single(read.Preview.Manifest.Files, file => file.Purpose == "media");
        Assert.Equal(3, read.Preview.Manifest.Files.Count); // wall + entry + one shared original.
        using var zip = ZipFile.OpenRead(path);
        using var contentStream = zip.GetEntry("wall.json")!.Open();
        using var text = new StreamReader(contentStream);
        var json = await text.ReadToEndAsync();
        Assert.DoesNotContain(Path.Combine(_directory, "source.zip").Replace("\\", "\\\\", StringComparison.Ordinal), json, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-leak", json);
        Assert.DoesNotContain("importSource", json);
        Assert.DoesNotContain("inactive", json);
        Assert.False(content.ToDocument().Settings.ShowGrid);
    }

    [Fact]
    public async Task FrozenEntriesAndOriginalHandlesSurviveLaterCommittedWallChanges()
    {
        var (store, document, root, reader) = Seed();
        using var snapshot = store.CapturePackageSnapshot(document, 5, ["tile"]);
        File.WriteAllBytes(Path.Combine(root, "Objects", "tile", "entry.url"), Encoding.UTF8.GetBytes("[InternetShortcut]\r\nURL=https://example.com/new\r\n"));
        SnapshotTestData.Install(new LocalFileStore(), root, document with { Generation = 6 });
        var original = store.ManagedMediaPath(document.Lanes[1].Groups[0].Members[0].Images.Media!);
        if (OperatingSystem.IsWindows()) Assert.Throws<IOException>(() => File.WriteAllBytes(original, [0]));
        var path = Path.Combine(_directory, "frozen.zip");
        Assert.Equal(WallPackageWriteStatus.Saved, (await new WallPackageWriter(reader).WriteAsync(snapshot, path, "1.3.8")).Status);
        var preview = (await reader.ReadPreviewAsync(path)).Preview!;
        using var batch = await reader.OpenImportAsync(preview);
        Assert.Equal("https://example.com/export", (await batch.ReadEntryAsync("tile")).Identity.Target.TrimEnd('/'));
        Assert.Equal(5, snapshot.Generation);
    }

    [Fact]
    public async Task AllActiveSelectionIncludesBothGroupsAndNeverSettingsOrInactiveMembers()
    {
        var (store, document, root, reader) = Seed();
        var entry = Path.Combine(root, "Objects", "unselected", "entry.url");
        Directory.CreateDirectory(Path.GetDirectoryName(entry)!); File.WriteAllBytes(entry, Shortcut);
        var ids = document.Lanes.SelectMany(lane => lane.Groups).SelectMany(group => group.Members).Select(member => member.Id);
        using var snapshot = store.CapturePackageSnapshot(document, 5, ids);
        var path = Path.Combine(_directory, "all.zip");
        Assert.Equal(WallPackageWriteStatus.Saved, (await new WallPackageWriter(reader).WriteAsync(snapshot, path, "1.3.8")).Status);
        var preview = (await reader.ReadPreviewAsync(path)).Preview!;
        Assert.Equal(new[] { "omitted", "mixed" }, preview.Content.Groups.Select(group => group.Id));
        Assert.Equal(4, preview.Content.Members.Count);
        Assert.DoesNotContain(preview.Content.Members, member => member.Id == "inactive");
        Assert.Null(preview.Content.ToDocument().Settings.BackgroundMedia);
        Assert.DoesNotContain(preview.Manifest.Files, file => file.Sha256 == document.Settings.BackgroundMedia!.Sha256);
    }

    [Fact]
    public void SelectedOriginalReparsePathIsRejectedAtItsReadBoundary()
    {
        var (_, document, root, _) = Seed();
        var files = new InMemoryFileStore();
        SnapshotTestData.Install(files, root, document);
        files.WriteAllBytes(Path.Combine(root, "Objects", "tile", "entry.url"), Shortcut);
        var reference = document.Lanes[1].Groups[0].Members[0].Images.Media!;
        var directory = Path.Combine(root, "Media", reference.Sha256);
        files.WriteAllBytes(Path.Combine(directory, "original.png"), Png);
        files.ReparsePaths.Add(directory);
        var store = new WallStore(files, new FakeLinkFileService(files), root);
        Assert.Throws<IOException>(() => store.CapturePackageSnapshot(document, 5, ["tile"]));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("inactive")]
    [InlineData("removed")]
    [InlineData("stale")]
    [InlineData("changed-config")]
    [InlineData("missing-entry")]
    [InlineData("changed-original")]
    public void InvalidOrChangedSelectionCannotProduceASnapshot(string failure)
    {
        var (store, document, root, _) = Seed();
        string[] selected = failure == "empty" ? [] : [failure is "inactive" or "removed" ? failure : "tile"];
        if (failure == "changed-config") File.WriteAllBytes(Path.Combine(root, "state.json"), UserDataSnapshotJson.Prepare(document with { Generation = 6 }).ManifestBytes);
        if (failure == "missing-entry") File.Delete(Path.Combine(root, "Objects", "tile", "entry.url"));
        if (failure == "changed-original") File.WriteAllBytes(store.ManagedMediaPath(document.Lanes[1].Groups[0].Members[0].Images.Media!), [0]);
        if (failure is "empty" or "inactive" or "removed")
            Assert.Throws<InvalidDataException>(() => store.CapturePackageSnapshot(document, 5, selected));
        else Assert.ThrowsAny<IOException>(() => store.CapturePackageSnapshot(document, failure == "stale" ? 4 : 5, selected));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("validation")]
    [InlineData("changed-target")]
    public async Task CancellationValidationAndDestinationCompetitionPreserveExistingTarget(string failure)
    {
        var path = Path.Combine(_directory, "protected.zip"); File.WriteAllText(path, "original");
        using var cancel = new CancellationTokenSource();
        var (store, document, _, reader) = Seed((_, _, _) =>
        {
            if (failure == "cancel") cancel.Cancel();
            if (failure == "changed-target") File.WriteAllText(path, "external change");
            return Task.FromResult(failure != "validation");
        });
        using var snapshot = store.CapturePackageSnapshot(document, 5, ["tile"]);
        var result = await new WallPackageWriter(reader).WriteAsync(snapshot, path, "1.3.8", token: cancel.Token);
        Assert.Equal(failure switch { "cancel" => WallPackageWriteStatus.Cancelled, "validation" => WallPackageWriteStatus.InvalidPackage, _ => WallPackageWriteStatus.IoFailure }, result.Status);
        Assert.Equal(failure == "changed-target" ? "external change" : "original", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_directory, ".tilewall-*.tmp"));
    }

    [Fact]
    public async Task ContainerBudgetRejectionDoesNotCreateOutputOrLoseExistingTarget()
    {
        var (store, document, _, reader) = Seed();
        using var snapshot = store.CapturePackageSnapshot(document, 5, ["tile"]);
        var path = Path.Combine(_directory, "budget.zip"); File.WriteAllText(path, "keep");
        var result = await new WallPackageWriter(reader).WriteAsync(snapshot, path, "1.3.8", limits: new() { MaximumEntries = 2 });
        Assert.Equal(WallPackageWriteStatus.InvalidPackage, result.Status);
        Assert.Equal("keep", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_directory, ".tilewall-*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetitionAtThePublicationPointNeverLosesTheOtherWritersFile(bool initiallyExists)
    {
        var (store, document, _, reader) = Seed();
        using var snapshot = store.CapturePackageSnapshot(document, 5, ["tile"]);
        var path = Path.Combine(_directory, "last-instant.zip");
        if (initiallyExists) File.WriteAllText(path, "original");
        var writer = new WallPackageWriter(reader, WallPackageWriter.Publish, () => File.WriteAllText(path, "last instant external write"));
        var result = await writer.WriteAsync(snapshot, path, "1.4.0");
        if (initiallyExists)
        {
            Assert.Equal(WallPackageWriteStatus.DestinationChanged, result.Status);
            Assert.NotNull(result.PreservedPath);
            Assert.Equal("last instant external write", File.ReadAllText(result.PreservedPath));
            Assert.Equal(WallPackageReadStatus.Valid, (await reader.ReadPreviewAsync(path)).Status);
        }
        else
        {
            Assert.Equal(WallPackageWriteStatus.ResultUnknown, result.Status);
            Assert.Equal("last instant external write", File.ReadAllText(path));
        }
    }

    [Fact]
    public async Task DisappearingDestinationDoesNotReportAnUncreatedRecoveryFile()
    {
        var (store, document, _, reader) = Seed();
        using var snapshot = store.CapturePackageSnapshot(document, 5, ["tile"]);
        var path = Path.Combine(_directory, "disappeared.zip"); File.WriteAllText(path, "original");
        var writer = new WallPackageWriter(reader, WallPackageWriter.Publish, () => File.Delete(path));
        var result = await writer.WriteAsync(snapshot, path, "1.4.0");
        Assert.Equal(WallPackageWriteStatus.ResultUnknown, result.Status);
        Assert.Null(result.PreservedPath);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_directory, ".tilewall-preserved-*.bak"));
    }

    [Theory]
    [InlineData("before", WallPackageWriteStatus.IoFailure)]
    [InlineData("after", WallPackageWriteStatus.Saved)]
    [InlineData("cancel-after", WallPackageWriteStatus.Saved)]
    [InlineData("unknown", WallPackageWriteStatus.ResultUnknown)]
    public async Task PublicationExceptionsAreClassifiedByReadBackWithoutDeletingDestination(string failure, WallPackageWriteStatus expected)
    {
        var (store, document, _, reader) = Seed();
        using var snapshot = store.CapturePackageSnapshot(document, 5, ["tile"]);
        var path = Path.Combine(_directory, "publish.zip"); File.WriteAllText(path, "original");
        using var cancel = new CancellationTokenSource();
        var writer = new WallPackageWriter(reader, (temporary, destination, backup) =>
        {
            if (failure is "after" or "cancel-after") WallPackageWriter.Publish(temporary, destination, backup);
            if (failure == "unknown") File.WriteAllText(destination, "another writer");
            if (failure == "cancel-after") { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); }
            throw new IOException("Injected publication failure");
        });
        var result = await writer.WriteAsync(snapshot, path, "1.3.8", token: cancel.Token);
        Assert.Equal(expected, result.Status);
        if (expected == WallPackageWriteStatus.Saved) Assert.Equal(WallPackageReadStatus.Valid, (await reader.ReadPreviewAsync(path)).Status);
        else Assert.Equal(failure == "unknown" ? "another writer" : "original", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_directory, ".tilewall-*.tmp"));
    }

    [Theory]
    [InlineData("data")]
    [InlineData("selected-source")]
    [InlineData("unselected-source")]
    [InlineData("background-source")]
    [InlineData("extra-source")]
    [InlineData("directory")]
    [InlineData("extension")]
    [InlineData("legacy-extension")]
    public async Task OutputCannotOverwriteKnownSourcesOrManagedData(string failure)
    {
        var (store, document, root, reader) = Seed();
        using var snapshot = store.CapturePackageSnapshot(document, 5, ["tile"]);
        var path = failure switch
        {
            "data" => Path.Combine(root, "inside.zip"),
            "selected-source" => Path.Combine(_directory, "source.zip"),
            "unselected-source" => Path.Combine(_directory, "unselected-source.zip"),
            "background-source" => Path.Combine(_directory, "background.zip"),
            "extra-source" => Path.Combine(_directory, "input.zip"),
            "directory" => Path.Combine(_directory, "folder.zip"),
            "legacy-extension" => Path.Combine(_directory, "legacy.twpack"),
            _ => Path.Combine(_directory, "wrong.exe"),
        };
        if (failure == "directory") Directory.CreateDirectory(path); else File.WriteAllText(path, "keep");
        var result = await new WallPackageWriter(reader).WriteAsync(snapshot, path, "1.3.8", additionalProtectedPaths: failure == "extra-source" ? [path] : null);
        Assert.NotEqual(WallPackageWriteStatus.Saved, result.Status);
        if (failure != "directory") Assert.Equal("keep", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_directory, ".tilewall-*.tmp"));
    }

    [Fact]
    public async Task SnapshotReservationFreezesIdsAndEndsBeforeWriterRuns()
    {
        var files = new InMemoryFileStore(); var root = Path.Combine(_directory, "session");
        var session = new WallSession(root, files, new FakeLinkFileService(files));
        Assert.Equal(WallOpenStatus.Loaded, session.Open(1, 10).Status);
        var member = new ClockMember { Id = "clock", LocalBounds = new GridRect(0, 0, 2, 2) };
        var current = session.Current with { Lanes = [new WallLane { Groups = [new StartGroup { Id = "g", Members = [member] }] }] };
        SnapshotTestData.Install(files, root, current);
        Assert.Equal(WallOpenStatus.Loaded, session.Open(1, 10).Status);
        var executor = new DeferredExecutor(); session.ConfigureAsyncExecution(executor, _ => Task.CompletedTask);
        var selected = new List<string> { "clock" };
        var capture = session.CapturePackageSnapshotAsync(0, selected);
        selected.Clear();
        Assert.True(session.WritePending);
        Assert.Throws<InvalidOperationException>(() => { _ = session.CapturePackageSnapshotAsync(0, ["clock"]); });
        executor.Run();
        using var snapshot = await capture;
        Assert.False(session.WritePending);
        Assert.Equal("clock", Assert.Single(snapshot.Content.Members).Id);
        Assert.Throws<IOException>(() => { _ = session.CapturePackageSnapshotAsync(1, ["clock"]); });
    }

    [Fact]
    public async Task FailedSnapshotCaptureReleasesSessionReservation()
    {
        var files = new InMemoryFileStore(); var root = Path.Combine(_directory, "cancelled-session");
        var session = new WallSession(root, files, new FakeLinkFileService(files));
        Assert.Equal(WallOpenStatus.Loaded, session.Open(1, 10).Status);
        var executor = new DeferredExecutor(); session.ConfigureAsyncExecution(executor, _ => Task.CompletedTask);
        using var cancel = new CancellationTokenSource();
        var capture = session.CapturePackageSnapshotAsync(0, ["no-member"], cancel.Token);
        Assert.True(session.WritePending); cancel.Cancel(); executor.Run();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var snapshot = await capture; });
        Assert.False(session.WritePending);
    }

    private sealed class DeferredExecutor : IWallStorageExecutor
    {
        private Action? _work;
        public Task<T> ExecuteAsync<T>(Func<T> work)
        {
            var completed = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _work = () => { try { completed.SetResult(work()); } catch (Exception ex) { completed.SetException(ex); } };
            return completed.Task;
        }
        internal void Run() => (_work ?? throw new InvalidOperationException())();
        public Task ShutdownAsync() => Task.CompletedTask;
    }
}
