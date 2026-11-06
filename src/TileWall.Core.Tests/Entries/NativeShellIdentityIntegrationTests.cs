using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Exchange;
using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Entries;

public sealed class NativeShellIdentityIntegrationTests : IDisposable
{
    private const string Computer = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    private const string Recycle = "::{645FF040-5081-101B-9F08-00AA002F954E}";
    private readonly string _fixture = Path.Combine(AppContext.BaseDirectory, "native-shell-identity-fixtures", Guid.NewGuid().ToString("N"));
    private readonly ShellLinkFileService _links = new();

    [Theory]
    [InlineData(Computer)]
    [InlineData(Recycle)]
    public void ValidShellPidlIdentitySurvivesCreateShellAndReadWithoutParsingProperty(string parsingName)
    {
        Directory.CreateDirectory(_fixture);
        var source = Path.Combine(_fixture, "Native.lnk");
        var sourceHash = ParseHash(parsingName);
        var expected = LaunchIdentityService.ForShellIdList(sourceHash, parsingName);

        _links.CreateShell(source, parsingName);

        var bytes = File.ReadAllBytes(source);
        var fields = _links.Read(source);
        Assert.True(fields.HasIdList);
        Assert.True(string.IsNullOrEmpty(fields.TargetPath));
        Assert.Equal(sourceHash, fields.ShellIdListHash);
        var observed = new LaunchIdentityService(new LocalFileStore(), _links).ResolveInput(source)!;
        Assert.Equal(expected.Key, observed.Key);
        // A missing PKEY_Link_TargetParsingPath is descriptive loss only.
        var withoutProperty = new LaunchIdentityService(new LocalFileStore(), _links)
            .ResolveObservedInput(EntryKind.Lnk, source, bytes, fields with { ShellParsingName = null })!;
        Assert.Equal(expected.Key, withoutProperty.Key);
        Assert.Equal("pidl:" + sourceHash, withoutProperty.Target);
        Assert.Equal(bytes, File.ReadAllBytes(source));
    }

    [Fact]
    public void DifferentNativeTargetsArgumentsAndWorkingDirectoriesRemainDistinctAfterRead()
    {
        Directory.CreateDirectory(_fixture);
        var computer = Path.Combine(_fixture, "Computer.lnk");
        var recycle = Path.Combine(_fixture, "Recycle.lnk");
        _links.CreateShell(computer, Computer);
        _links.CreateShell(recycle, Recycle);
        var identities = new LaunchIdentityService(new LocalFileStore(), _links);
        var baseline = identities.ResolveInput(computer)!;
        Assert.NotEqual(baseline.Key, identities.ResolveInput(recycle)!.Key);
        var hash = ParseHash(Computer);
        var workA = Directory.CreateDirectory(Path.Combine(_fixture, "work-a")).FullName;
        var workB = Directory.CreateDirectory(Path.Combine(_fixture, "work-b")).FullName;

        SetLaunchFields(computer, "--profile A", workA);
        var withArguments = identities.ResolveInput(computer)!;
        Assert.Equal(LaunchIdentityService.ForShellIdList(hash, null, "--profile A", workA).Key, withArguments.Key);
        Assert.NotEqual(baseline.Key, withArguments.Key);
        SetLaunchFields(computer, "--profile B", workA);
        var otherArguments = identities.ResolveInput(computer)!;
        Assert.Equal(LaunchIdentityService.ForShellIdList(hash, null, "--profile B", workA).Key, otherArguments.Key);
        Assert.NotEqual(withArguments.Key, otherArguments.Key);
        SetLaunchFields(computer, "--profile B", workB);
        var otherWorkingDirectory = identities.ResolveInput(computer)!;
        Assert.Equal(LaunchIdentityService.ForShellIdList(hash, null, "--profile B", workB).Key, otherWorkingDirectory.Key);
        Assert.NotEqual(otherArguments.Key, otherWorkingDirectory.Key);
    }

    [Fact]
    public void RealLocalWallStoreCommitsNativeShellLinksAndReopensBothIdentities()
    {
        var root = Path.Combine(_fixture, "wall");
        var files = new LocalFileStore();
        var store = new WallStore(files, _links, root);
        var current = Wall();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(current).Status);
        var expected = new[] { (Id: "computer", ParsingName: Computer), (Id: "recycle", ParsingName: Recycle) }
            .ToDictionary(target => target.Id, target => LaunchIdentityService.ForShellIdList(ParseHash(target.ParsingName), target.ParsingName).Key);
        var candidate = Wall(Tile("computer"), Tile("recycle")) with { Generation = 1 };
        var plan = store.PlanEntries(current, candidate,
        [
            new EntryChange.CreateShellLink("computer", Computer, expected["computer"]),
            new EntryChange.CreateShellLink("recycle", Recycle, expected["recycle"])
        ]);
        Assert.NotNull(plan.Request);

        var committed = store.Commit(current, plan.Request);

        Assert.Equal(WallCommitStatus.Committed, committed.Status);
        var reopened = new WallStore(new LocalFileStore(), new ShellLinkFileService(), root).Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Equal(WallJson.Serialize(committed.CommittedDocument!), WallJson.Serialize(reopened.Document!));
        var identities = new LaunchIdentityService(files, _links);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal),
            reopened.Document!.EnumerateAllMembers().Select(member => member.Id).Order(StringComparer.Ordinal));
        foreach (var member in reopened.Document.EnumerateAllMembers())
        {
            Assert.Equal(ManagedEntryKind.Lnk, member.Entry!.Kind);
            var path = Path.Combine(root, member.Entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(_links.Read(path).HasIdList);
            Assert.Equal(expected[member.Id], identities.ResolveInput(path)!.Key);
        }
    }

    [Fact]
    public void WrongExpectedNativePidlRejectsBeforeAnyFormalLocalFileMutation()
    {
        var root = Path.Combine(_fixture, "wall");
        var files = new RecordingLocalFiles(root);
        var store = new WallStore(files, _links, root);
        var initial = Wall();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(initial).Status);
        var seedPlan = store.PlanEntries(initial, Wall(Tile("existing")) with { Generation = 1 },
            [new EntryChange.CreateUrl("existing", "https://existing.example/")]);
        Assert.NotNull(seedPlan.Request);
        var seeded = store.Commit(initial, seedPlan.Request);
        Assert.Equal(WallCommitStatus.Committed, seeded.Status);
        var current = seeded.CommittedDocument!;
        var existing = Assert.IsType<TileMember>(Assert.Single(current.EnumerateAllMembers()));
        var before = FormalBytes(root);
        files.FormalMutations.Clear();
        var plan = store.PlanEntries(current, Wall(existing, Tile("native")) with { Generation = 2 },
            [new EntryChange.CreateShellLink("native", Computer, LaunchIdentityService.ForShellIdList(ParseHash(Recycle), "Same descriptive label").Key)]);
        Assert.NotNull(plan.Request);

        var rejected = store.Commit(current, plan.Request);

        Assert.Equal(WallCommitStatus.Rejected, rejected.Status);
        Assert.Null(rejected.CommittedDocument);
        Assert.Contains(rejected.Diagnostics, message => message.Contains("native Shell identity changed", StringComparison.Ordinal));
        Assert.Empty(files.FormalMutations);
        var after = FormalBytes(root);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
        var staging = Path.Combine(root, "Staging");
        if (Directory.Exists(staging)) Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
        var reopened = new WallStore(new LocalFileStore(), new ShellLinkFileService(), root).Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        Assert.Equal(WallJson.Serialize(current), WallJson.Serialize(reopened.Document!));
    }

    [Fact]
    public void TrustedFrozenPidlIsConfirmedAtStorePreparationAndReopensWithExactIdentity()
    {
        var confirmations = 0;
        var target = CaptureTarget(Computer, () => confirmations++);
        var root = Path.Combine(_fixture, "frozen-native-wall");
        var store = new WallStore(new LocalFileStore(), _links, root);
        var empty = Wall();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(empty).Status);
        var plan = store.PlanEntries(empty, Wall(Tile("native")) with { Generation = 1 },
            [new EntryChange.CreateShellLink("native", Computer, target.Identity.Key) { NativeTarget = target }]);

        var committed = store.Commit(empty, plan.Request!);

        Assert.Equal(WallCommitStatus.Committed, committed.Status);
        Assert.Equal(1, confirmations);
        var reopened = new WallStore(new LocalFileStore(), new ShellLinkFileService(), root).Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        var entry = Assert.Single(reopened.Document!.EnumerateAllMembers()).Entry!;
        var path = Path.Combine(root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var guardedLinks = new GuardedWallLinks(_links, new WallStoragePaths(new LocalFileStore(), root));
        var fields = guardedLinks.ReadWithIdList(path, out var actualIdList);
        Assert.Equal(target.IdListSha256, fields.ShellIdListHash);
        Assert.Equal(fields.ShellIdListHash, Convert.ToHexString(SHA256.HashData(actualIdList.Span)));
        Assert.Equal(target.Identity.Key, new LaunchIdentityService(new LocalFileStore(), _links).ResolveInput(path)!.Key);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<NativeShellTargetSnapshot>(JsonSerializer.Serialize(target)));
    }

    [Fact]
    public void ChangedFrozenNativeSourceRejectsWholeBatchBeforeFormalMutation()
    {
        var first = CaptureTarget(Computer);
        var changed = CaptureTarget(Recycle, () => throw new IOException("The native package registration changed."));
        var root = Path.Combine(_fixture, "changed-frozen-wall");
        var files = new RecordingLocalFiles(root);
        var store = new WallStore(files, _links, root);
        var empty = Wall();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(empty).Status);
        var before = FormalBytes(root);
        files.FormalMutations.Clear();
        var plan = store.PlanEntries(empty, Wall(Tile("first"), Tile("changed")) with { Generation = 1 },
        [
            new EntryChange.CreateShellLink("first", Computer, first.Identity.Key) { NativeTarget = first },
            new EntryChange.CreateShellLink("changed", Recycle, changed.Identity.Key) { NativeTarget = changed },
        ]);

        var result = store.Commit(empty, plan.Request!);

        Assert.Equal(WallCommitStatus.Rejected, result.Status);
        Assert.Contains(result.Diagnostics, message => message.Contains("native package registration changed", StringComparison.Ordinal));
        Assert.Empty(files.FormalMutations);
        var after = FormalBytes(root);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "Staging")));
        Assert.Empty(store.Open().Document!.EnumerateAllMembers());
    }

    [Theory]
    [InlineData("--profile different", "")]
    [InlineData("", @"C:\different-working-directory")]
    public void FrozenPidlCannotAuthorizeDifferentArgumentsOrWorkingDirectory(string arguments, string workingDirectory)
    {
        var target = CaptureTarget(Computer, arguments: arguments, workingDirectory: workingDirectory);
        var root = Path.Combine(_fixture, "different-frozen-fields-" + Guid.NewGuid().ToString("N"));
        var files = new RecordingLocalFiles(root);
        var store = new WallStore(files, _links, root);
        var empty = Wall();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(empty).Status);
        files.FormalMutations.Clear();
        var plan = store.PlanEntries(empty, Wall(Tile("native")) with { Generation = 1 },
            [new EntryChange.CreateShellLink("native", Computer, target.Identity.Key) { NativeTarget = target }]);

        var result = store.Commit(empty, plan.Request!);

        Assert.Equal(WallCommitStatus.Rejected, result.Status);
        Assert.Empty(files.FormalMutations);
        Assert.Empty(store.Open().Document!.EnumerateAllMembers());
    }

    private static NativeShellTargetSnapshot CaptureTarget(string parsingName, Action? confirmCurrent = null,
        string arguments = "", string workingDirectory = "")
    {
        var result = SHParseDisplayName(parsingName, IntPtr.Zero, out var pidl, 0, out _);
        try
        {
            Marshal.ThrowExceptionForHR(result);
            var identity = LaunchIdentityService.ForShellIdList(ShellLinkFileService.IdListHash(pidl), parsingName, arguments, workingDirectory);
            return NativeShellTargetSnapshot.Capture(pidl, identity, parsingName, confirmCurrent);
        }
        finally { if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
    }

    [Fact]
    public async Task NativePidlWithoutParsingDescriptionSurvivesPackagePreviewImportAndReexport()
    {
        var sourceRoot = Path.Combine(_fixture, "source-wall");
        var sourceStore = new WallStore(new LocalFileStore(), _links, sourceRoot);
        var empty = Wall();
        Assert.Equal(WallOpenStatus.Loaded, sourceStore.InitializeFresh(empty).Status);
        var hash = ParseHash(Computer);
        var expected = LaunchIdentityService.ForShellIdList(hash, Computer);
        var seedPlan = sourceStore.PlanEntries(empty, Wall(Tile("computer")) with { Generation = 1 },
            [new EntryChange.CreateShellLink("computer", Computer, expected.Key)]);
        Assert.NotNull(seedPlan.Request);
        var seed = sourceStore.Commit(empty, seedPlan.Request);
        Assert.Equal(WallCommitStatus.Committed, seed.Status);
        var sourceDocument = seed.CommittedDocument!;
        var sourceEntry = Assert.Single(sourceDocument.EnumerateAllMembers()).Entry!;
        var sourcePath = Path.Combine(sourceRoot, sourceEntry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        // SetIDList/CreateShell does not set PKEY_Link_TargetParsingPath. Keep
        // this real serialized shortcut unmodified throughout the exchange.
        Assert.True(string.IsNullOrEmpty(_links.Read(sourcePath).ShellParsingName));
        var originalBytes = File.ReadAllBytes(sourcePath);
        var sourceReader = new WallPackageReader(
            (kind, bytes, token) => Task.FromResult(sourceStore.ObservePackageEntry(kind, bytes, token)),
            (_, _, _) => Task.FromResult(true));
        var firstPackage = Path.Combine(_fixture, "native-source.zip");
        using (var snapshot = sourceStore.CapturePackageSnapshot(sourceDocument, sourceDocument.Generation, ["computer"]))
        {
            snapshot.CompleteAutomaticImages();
            Assert.Equal(WallPackageWriteStatus.Saved, (await new WallPackageWriter(sourceReader).WriteAsync(snapshot, firstPackage, "1.4.0")).Status);
        }
        var firstRead = await sourceReader.ReadPreviewAsync(firstPackage);
        Assert.Equal(WallPackageReadStatus.Valid, firstRead.Status);
        var preview = firstRead.Preview!;
        Assert.Equal(WallPackageManifest.CurrentFormatId, preview.Manifest.FormatId);
        Assert.Equal(WallPackageManifest.CurrentFormatVersion, preview.Manifest.FormatVersion);
        Assert.Equal(WallDocument.CurrentSchemaVersion, preview.Manifest.WallSchemaVersion);
        Assert.Equal(expected.Key, preview.Identities["computer"].Key);
        Assert.Equal("pidl:" + hash, preview.Identities["computer"].Target);
        using var import = await sourceReader.OpenImportAsync(preview);
        var verified = await import.ReadEntryAsync("computer");
        Assert.Equal(expected.Key, verified.Identity.Key);
        Assert.Equal(originalBytes, verified.CopyBytes());

        var importedRoot = Path.Combine(_fixture, "imported-wall");
        var importedStore = new WallStore(new LocalFileStore(), new ShellLinkFileService(), importedRoot);
        Assert.Equal(WallOpenStatus.Loaded, importedStore.InitializeFresh(empty).Status);
        var importPlan = importedStore.PlanEntries(empty, Wall(Tile("computer")) with { Generation = 1 },
            [new EntryChange.FromPackage("computer", verified)]);
        Assert.NotNull(importPlan.Request);
        var imported = importedStore.Commit(empty, importPlan.Request);
        Assert.Equal(WallCommitStatus.Committed, imported.Status);
        var reopened = new WallStore(new LocalFileStore(), new ShellLinkFileService(), importedRoot).Open();
        Assert.Equal(WallOpenStatus.Loaded, reopened.Status);
        var importedDocument = reopened.Document!;
        var importedEntry = Assert.Single(importedDocument.EnumerateAllMembers()).Entry!;
        var importedPath = Path.Combine(importedRoot, importedEntry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(originalBytes, File.ReadAllBytes(importedPath));
        Assert.Equal(expected.Key, new LaunchIdentityService(new LocalFileStore(), _links).ResolveInput(importedPath)!.Key);
        Assert.True(string.IsNullOrEmpty(_links.Read(importedPath).ShellParsingName));

        var importedReader = new WallPackageReader(
            (kind, bytes, token) => Task.FromResult(importedStore.ObservePackageEntry(kind, bytes, token)),
            (_, _, _) => Task.FromResult(true));
        var secondPackage = Path.Combine(_fixture, "native-reexport.zip");
        using (var snapshot = importedStore.CapturePackageSnapshot(importedDocument, importedDocument.Generation, ["computer"]))
        {
            snapshot.CompleteAutomaticImages();
            Assert.Equal(WallPackageWriteStatus.Saved, (await new WallPackageWriter(importedReader).WriteAsync(snapshot, secondPackage, "1.4.0")).Status);
        }
        var secondRead = await importedReader.ReadPreviewAsync(secondPackage);
        Assert.Equal(WallPackageReadStatus.Valid, secondRead.Status);
        Assert.Equal(preview.Manifest.FormatVersion, secondRead.Preview!.Manifest.FormatVersion);
        Assert.Equal(preview.Manifest.WallSchemaVersion, secondRead.Preview.Manifest.WallSchemaVersion);
        Assert.Equal(preview.Manifest.ContentFingerprint, secondRead.Preview.Manifest.ContentFingerprint);
        Assert.Equal(expected.Key, secondRead.Preview.Identities["computer"].Key);
        Assert.Equal("pidl:" + hash, secondRead.Preview.Identities["computer"].Target);
        using var reimport = await importedReader.OpenImportAsync(secondRead.Preview);
        Assert.Equal(originalBytes, (await reimport.ReadEntryAsync("computer")).CopyBytes());
        Assert.Equal(originalBytes, File.ReadAllBytes(sourcePath));
    }

    private static string ParseHash(string parsingName)
    {
        // Only valid absolute PIDLs supplied by Shell are passed to IdListHash.
        var result = SHParseDisplayName(parsingName, IntPtr.Zero, out var pidl, 0, out _);
        try
        {
            Marshal.ThrowExceptionForHR(result);
            Assert.NotEqual(IntPtr.Zero, pidl);
            return ShellLinkFileService.IdListHash(pidl);
        }
        finally { if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
    }

    private static void SetLaunchFields(string path, string arguments, string workingDirectory)
    {
        object shell = new TestShellLink();
        try
        {
            var link = (ITestShellLinkW)shell;
            var file = (IPersistFile)link;
            file.Load(path, 0);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(workingDirectory);
            file.Save(path, true);
        }
        finally
        {
            // Both interfaces share this locally owned RCW. Release it once,
            // including when a field update or Save throws.
            if (OperatingSystem.IsWindows()) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static TileMember Tile(string id) => new()
    {
        Id = id, LocalBounds = new GridRect(0, 0, 1, 1),
        Visual = new MemberVisual { TitleMode = MemberTitleMode.Manual, Title = id }
    };

    private static WallDocument Wall(params TileMember[] tiles) => new()
    {
        Columns = 1, Rows = 12,
        Lanes = [new WallLane { Groups = tiles.Select((tile, index) => new StartGroup { Id = "group" + index, Members = [tile] }).ToArray() }]
    };

    private static bool IsFormalPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative is "state.json" or "Recovery/state.prev.json" or "Objects" or "Media" ||
            relative.StartsWith("Objects/", StringComparison.Ordinal) || relative.StartsWith("Media/", StringComparison.Ordinal);
    }

    private static Dictionary<string, byte[]> FormalBytes(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => IsFormalPath(root, path)).ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes, StringComparer.Ordinal);

    // A local-store observer for the rejection assertion, not a replacement IO
    // implementation: every operation still reaches the real LocalFileStore.
    private sealed class RecordingLocalFiles(string root) : IFileStore
    {
        private readonly LocalFileStore _inner = new();
        public List<string> FormalMutations { get; } = [];
        private void Record(string operation, string path) { if (IsFormalPath(root, path)) FormalMutations.Add(operation + ":" + path); }
        public FileStorePathInfo GetPathInfo(string path) => _inner.GetPathInfo(path);
        public bool Exists(string path) => _inner.Exists(path);
        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
        public Stream OpenReadProtected(string path) => _inner.OpenReadProtected(path);
        public void WriteAllBytes(string path, byte[] bytes) { Record("write", path); _inner.WriteAllBytes(path, bytes); }
        public void Move(string src, string dst, bool overwrite) { Record("move-from", src); Record("move-to", dst); _inner.Move(src, dst, overwrite); }
        public void MoveDirectory(string src, string dst) { Record("move-directory-from", src); Record("move-directory-to", dst); _inner.MoveDirectory(src, dst); }
        public void Copy(string src, string dst, bool overwrite) { Record("copy", dst); _inner.Copy(src, dst, overwrite); }
        public void Delete(string path) { Record("delete", path); _inner.Delete(path); }
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public IReadOnlyList<string> ListDirectories(string path) => _inner.ListDirectories(path);
        public IReadOnlyList<string> ListFiles(string path) => _inner.ListFiles(path);
        public void DeleteDirectory(string path) { Record("delete-directory", path); _inner.DeleteDirectory(path); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_fixture)) Directory.Delete(_fixture, true);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributes, out uint returnedAttributes);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class TestShellLink { }

    // The SDK IShellLinkW vtable prefix through SetArguments, matching the
    // existing link integration fixture; no target is resolved or launched.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface ITestShellLinkW
    {
        void GetPath([Out] StringBuilder path, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out] StringBuilder text, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out] StringBuilder path, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments([Out] StringBuilder text, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
    }
}
