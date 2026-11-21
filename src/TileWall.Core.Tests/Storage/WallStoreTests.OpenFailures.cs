using System.Text;
using TileWall.Core.Storage;
using Xunit;

namespace TileWall.Core.Tests.Storage;

public sealed partial class WallStoreTests
{
    [Fact]
    public void UnattributedFilesReportReasonAndSurviveRepeatedOpen()
    {
        var (store, files, _, root) = Harness();
        files.WriteAllBytes(Path.Combine(root, "unrecognized.bin"), [1, 2, 3]);
        var before = files.SnapshotFiles();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = store.Open();
            Assert.Equal(WallOpenBlockReason.UnattributedFiles, result.BlockReason);
            Assert.Null(result.Document);
            AssertSameFileBytes(before, files.SnapshotFiles());
        }
    }

    [Fact]
    public void EmptyTaskbarRecoveryLocksAllowFreshWallInitialization()
    {
        var (store, files, _, root) = Harness();
        var runtime = Path.Combine(root, "Runtime");
        var firstLock = Path.Combine(runtime, "Taskbar-2", "taskbar-recovery.lock");
        var secondLock = Path.Combine(runtime, "Taskbar-12", "taskbar-recovery.lock");
        files.CreateDirectory(runtime);
        files.CreateDirectory(Path.GetDirectoryName(firstLock)!);
        files.CreateDirectory(Path.GetDirectoryName(secondLock)!);
        files.CreateDirectory(Path.Combine(runtime, "Taskbar-3"));
        files.WriteAllBytes(firstLock, []);
        files.WriteAllBytes(secondLock, []);

        Assert.Equal(WallOpenStatus.Fresh, store.Open().Status);
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(Wall()).Status);
        Assert.Equal(WallOpenStatus.Loaded, store.Open().Status);
        Assert.Empty(files.ReadAllBytes(firstLock));
        Assert.Empty(files.ReadAllBytes(secondLock));
    }

    [Theory]
    [InlineData("Taskbar-2/taskbar-recovery.json")]
    [InlineData("Taskbar-2/taskbar-recovery.lock", true)]
    [InlineData("Taskbar-2/unknown/taskbar-recovery.lock")]
    [InlineData("Taskbar-02/taskbar-recovery.lock")]
    [InlineData("unrecognized.bin")]
    public void OtherTaskbarRuntimeResidueBlocksInitialization(string relativePath, bool nonempty = false)
    {
        var (store, files, _, root) = Harness();
        var runtime = Path.Combine(root, "Runtime");
        var lockPath = Path.Combine(runtime, "Taskbar-2", "taskbar-recovery.lock");
        files.CreateDirectory(runtime);
        files.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        files.WriteAllBytes(lockPath, []);
        var residue = Path.Combine(runtime, relativePath.Replace('/', Path.DirectorySeparatorChar));
        files.CreateDirectory(Path.GetDirectoryName(residue)!);
        files.WriteAllBytes(residue, nonempty ? [1] : []);
        var before = files.SnapshotFiles();

        Assert.Equal(WallOpenBlockReason.UnattributedFiles, store.Open().BlockReason);
        Assert.Equal(WallOpenStatus.RecoveryBlocked, store.InitializeFresh(Wall()).Status);
        AssertSameFileBytes(before, files.SnapshotFiles());
    }

    [Fact]
    public void ReparseTaskbarRecoveryLockBlocksInitialization()
    {
        var (store, files, _, root) = Harness();
        var runtime = Path.Combine(root, "Runtime");
        var lockPath = Path.Combine(runtime, "Taskbar-2", "taskbar-recovery.lock");
        files.CreateDirectory(runtime);
        files.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        files.WriteAllBytes(lockPath, []);
        files.ReparsePaths.Add(lockPath);

        Assert.Equal(WallOpenBlockReason.UnsafePath, store.Open().BlockReason);
        Assert.Equal(WallOpenStatus.RecoveryBlocked, store.InitializeFresh(Wall()).Status);
        Assert.False(files.Exists(Path.Combine(root, "state.json")));
    }

    [Fact]
    public void InvalidSnapshotReportsStructuredReasonAndPreservesFiles()
    {
        var (store, files, _, root) = Harness();
        Assert.Equal(WallOpenStatus.Loaded, store.InitializeFresh(Wall()).Status);
        var manifest = UserDataSnapshotJson.ReadManifest(files.ReadAllBytes(Path.Combine(root, "state.json")));
        files.WriteAllBytes(Path.Combine(root, "Snapshots", manifest.SnapshotId, "wall.json"), Encoding.UTF8.GetBytes("{}"));
        var before = files.SnapshotFiles();

        var result = store.Open();

        Assert.Equal(WallOpenStatus.CorruptBlocked, result.Status);
        Assert.Equal(WallOpenBlockReason.InvalidSnapshot, result.BlockReason);
        Assert.Null(result.Document);
        AssertSameFileBytes(before, files.SnapshotFiles());
    }

    [Fact]
    public void MissingEntryReportsReferencedAssetsWithoutLoadingPartialWall()
    {
        var (store, files, _, root) = Harness();
        var initial = Wall();
        store.InitializeFresh(initial);
        var committed = store.Commit(initial, Plan(store, initial, Wall(Tile("a", "A")) with { Generation = 1 },
            new EntryChange.CreateUrl("a", "https://example.com")));
        files.Delete(EntryPath(root, committed.CommittedDocument!.EnumerateAllMembers().Single().Entry!));
        var before = files.SnapshotFiles();

        var result = store.Open();

        Assert.Equal(WallOpenBlockReason.ReferencedAssets, result.BlockReason);
        Assert.Null(result.Document);
        AssertSameFileBytes(before, files.SnapshotFiles());
    }

    [Fact]
    public void DataFolderEntryValidatesRootAgainAndNeverReturnsAReparseTarget()
    {
        var (store, files, _, root) = Harness();
        Assert.Throws<DirectoryNotFoundException>(() => store.ValidatedDataRoot());
        files.CreateDirectory(root);
        Assert.Equal(root, store.ValidatedDataRoot());
        files.ReparsePaths.Add(root);

        Assert.Throws<IOException>(() => store.ValidatedDataRoot());
        var result = store.Open();
        Assert.Equal(WallOpenBlockReason.UnsafePath, result.BlockReason);
        Assert.Null(result.Document);
    }
}
