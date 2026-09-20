using TileWall.Core.Configuration;
using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Configuration;

/// <summary>
/// T-STORE：原子保存（三处失败注入）、恢复链、双损坏文件字节不变、Fresh→CreateInitial→Save→Load（设计 §16.3、§17.2、C13/C26）。
/// </summary>
public class ConfigStoreTests : IDisposable
{
    private const string Root = "mem-root";
    private static readonly string ConfigPath = Path.Combine(Root, "config.json");
    private static readonly string TempPath = Path.Combine(Root, "config.json.tmp");
    private static readonly string BackupPath = Path.Combine(Root, "Recovery", "config.prev.json");

    private readonly InMemoryFileStore _files = new();
    private readonly ConfigStore _store;

    public ConfigStoreTests()
    {
        _store = new ConfigStore(_files, new FakeDataDirectoryProvider(Root));
    }

    public void Dispose()
    {
        _files.InjectedFault = FaultPoint.None;
    }

    private static TileWallConfig ValidConfig(int rows = 5) =>
        TileWallConfig.CreateInitial(2, rows);

    // —— Fresh → CreateInitial → Save → Load 回读（首启链路，§8.3）——

    [Fact]
    public void Load_WithoutFile_ReturnsFresh()
    {
        var result = _store.Load();
        Assert.Equal(ConfigLoadStatus.Fresh, result.Status);
        Assert.Null(result.Config);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void FirstSave_CreatesConfigWithoutBackup_TmpIsGone_AndRoundTrips()
    {
        var config = ValidConfig();
        var report = _store.Save(config);

        Assert.False(report.BackupCreated);
        Assert.Equal(ConfigPath, report.ConfigPath);
        Assert.Null(report.BackupPath);
        Assert.False(_files.Exists(TempPath), "tmp 写完即改名，正常不存在。");
        Assert.True(_files.Exists(ConfigPath));

        var loaded = _store.Load();
        Assert.Equal(ConfigLoadStatus.Loaded, loaded.Status);
        Assert.NotNull(loaded.Config);
        Assert.Equal(config.Wall, loaded.Config!.Wall);
        Assert.Empty(loaded.Config.Objects);
    }

    [Fact]
    public void SecondSave_CreatesBackupOfPreviousVersion()
    {
        _store.Save(ValidConfig(rows: 5));
        var secondBytes = ConfigJson.Serialize(ValidConfig(rows: 7));

        var report = _store.Save(ValidConfig(rows: 7));

        Assert.True(report.BackupCreated);
        Assert.Equal(BackupPath, report.BackupPath);
        Assert.True(_files.Exists(BackupPath));
        // 备份 = 上一版内容（不是新内容）
        var expected = ConfigJson.Serialize(ValidConfig(rows: 5));
        Assert.Equal(expected, _files.ReadAllBytes(BackupPath));
        Assert.Equal(secondBytes, _files.ReadAllBytes(ConfigPath));
    }

    // —— 校验失败不落盘 ——

    [Fact]
    public void Save_InvalidConfig_ThrowsValidationException_DiskUntouched()
    {
        var invalid = Layouts.Config(Layouts.Wall2x9, [
            Layouts.Tile("dup", new GridRect(0, 0, 1, 1)),
            Layouts.Tile("dup", new GridRect(1, 0, 1, 1)),
        ]);

        var exception = Assert.Throws<ConfigValidationException>(() => _store.Save(invalid));

        Assert.Contains(exception.Violations, v => v.Code == ConfigValidator.DuplicateId);
        Assert.False(_files.Exists(ConfigPath)); // 磁盘零改动
        Assert.False(_files.Exists(TempPath));
    }

    // —— 原子保存三处失败注入：任一步失败 → 异常 + config.json 旧内容不变 + tmp 清理 ——

    [Theory]
    [InlineData(FaultPoint.BeforeWriteTemp)]
    [InlineData(FaultPoint.BeforeBackup)]
    [InlineData(FaultPoint.BeforeMove)]
    public void Save_InjectedIoFailure_PreservesOldConfig(FaultPoint point)
    {
        var firstBytes = ConfigJson.Serialize(ValidConfig(rows: 5));
        _store.Save(ValidConfig(rows: 5));
        _files.InjectedFault = point;

        var exception = Record.Exception(() => _store.Save(ValidConfig(rows: 7)));

        Assert.NotNull(exception);
        Assert.IsType<IOException>(exception);
        Assert.Equal(firstBytes, _files.ReadAllBytes(ConfigPath)); // config.json 保持旧内容（§16.3）
        Assert.False(_files.Exists(TempPath));                     // 尽力删除 .tmp
        _files.InjectedFault = FaultPoint.None;

        // 失败后再保存恢复正常
        _store.Save(ValidConfig(rows: 7));
        Assert.Equal(ConfigLoadStatus.Loaded, _store.Load().Status);
    }

    [Fact]
    public void Save_BeforeWriteTemp_WithNoPriorConfig_LeavesDiskEmpty()
    {
        _files.InjectedFault = FaultPoint.BeforeWriteTemp;

        Assert.Throws<IOException>(() => _store.Save(ValidConfig()));

        Assert.False(_files.Exists(ConfigPath));
        Assert.False(_files.Exists(TempPath));
        Assert.False(_files.Exists(BackupPath));
    }

    // —— 恢复链（§5.3）——

    [Fact]
    public void Load_CorruptMain_FallsBackToBackup()
    {
        _store.Save(ValidConfig(rows: 5));
        _store.Save(ValidConfig(rows: 5)); // 第二次保存才生成 Recovery/config.prev.json
        _files.WriteAllBytes(ConfigPath, [0xDE, 0xAD, 0xBE, 0xEF]); // 主文件损坏（非 JSON）

        var loaded = _store.Load();

        Assert.Equal(ConfigLoadStatus.RecoveredFromBackup, loaded.Status);
        Assert.NotNull(loaded.Config);
        Assert.Equal(new WallState(2, 5), loaded.Config!.Wall);
        Assert.NotEmpty(loaded.Diagnostics); // UI 须提示
    }

    [Fact]
    public void Load_MainViolatesInvariants_FallsBackToBackup()
    {
        _store.Save(ValidConfig(rows: 5));
        _store.Save(ValidConfig(rows: 5)); // 确保 Recovery/config.prev.json 存在
        // 越墙对象：解析成功但校验失败
        var bad = new TileWallConfig
        {
            Wall = new WallState(2, 9),
            Objects = [Layouts.Tile("t", new GridRect(15, 8, 1, 2))],
        };
        _files.WriteAllBytes(ConfigPath, ConfigJson.Serialize(bad));

        var loaded = _store.Load();

        Assert.Equal(ConfigLoadStatus.RecoveredFromBackup, loaded.Status);
        Assert.Equal(new WallState(2, 5), loaded.Config!.Wall);
    }

    [Fact]
    public void Load_BothCorrupt_ReturnsCorrupt_KeepsBothFilesByteIdentical()
    {
        // §17.2：不能安全加载时保留文件并提示，不覆盖成空墙
        var corruptMain = new byte[] { 0x01 };
        var corruptBackup = new byte[] { 0x02 };
        _files.WriteAllBytes(ConfigPath, corruptMain);
        _files.CreateDirectory(_store.BackupPath);
        _files.WriteAllBytes(BackupPath, corruptBackup);

        var loaded = _store.Load();

        Assert.Equal(ConfigLoadStatus.Corrupt, loaded.Status);
        Assert.Null(loaded.Config);
        Assert.NotEmpty(loaded.Diagnostics);
        Assert.Equal(corruptMain, _files.ReadAllBytes(ConfigPath));   // 两文件字节未变
        Assert.Equal(corruptBackup, _files.ReadAllBytes(BackupPath));
    }

    [Fact]
    public void Load_FutureVersion_KeepsFileAndDoesNotMigrate()
    {
        const string futureJson = """
            {
              "schemaVersion": 2,
              "wall": { "columns": 2, "rows": 9 },
              "objects": []
            }
            """;
        _files.WriteAllBytes(ConfigPath, System.Text.Encoding.UTF8.GetBytes(futureJson));

        var loaded = _store.Load();

        Assert.Equal(ConfigLoadStatus.FutureVersion, loaded.Status);
        Assert.Null(loaded.Config); // 不自动迁移（设计 §16.6）
        Assert.Contains(loaded.Diagnostics, d => d.Contains("schemaVersion=2"));
        Assert.Equal(
            System.Text.Encoding.UTF8.GetBytes(futureJson),
            _files.ReadAllBytes(ConfigPath)); // 原文件保留、绝不以空墙覆盖（§17.2）
    }

    // —— .tmp 清理 ——

    [Fact]
    public void CleanupTempFiles_RemovesLeftoverTmp_KeepsConfig()
    {
        _store.Save(ValidConfig());
        _files.WriteAllBytes(TempPath, [0x99]); // 模拟崩溃残留

        _store.CleanupTempFiles();

        Assert.False(_files.Exists(TempPath));
        Assert.True(_files.Exists(ConfigPath));
    }

    // —— LocalFileStore / LocalAppDataDirectoryProvider（真实文件系统路径冒烟）——

    [Fact]
    public void LocalFileStore_WriteFlushMoveCopy_RoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "tilewall-m2-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalFileStore();
            var main = Path.Combine(root, "config.json");
            var tmp = main + ".tmp";
            var backup = Path.Combine(root, "Recovery", "config.prev.json");

            store.CreateDirectory(root);
            store.CreateDirectory(Path.Combine(root, "Recovery")); // File.Copy 不建目录：显式准备
            store.WriteAllBytes(tmp, [1, 2, 3]);
            Assert.True(store.Exists(tmp));
            Assert.False(store.Exists(main));

            store.Move(tmp, main, overwrite: true);
            Assert.False(store.Exists(tmp));
            Assert.Equal(new byte[] { 1, 2, 3 }, store.ReadAllBytes(main));

            store.Copy(main, backup, overwrite: true);
            Assert.Equal(new byte[] { 1, 2, 3 }, store.ReadAllBytes(backup));

            store.WriteAllBytes(main, [9]);
            store.Copy(main, backup, overwrite: true);
            Assert.Equal([9], store.ReadAllBytes(backup));

            store.Delete(main);
            Assert.False(store.Exists(main));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void LocalAppDataDirectoryProvider_ReturnsTileWallUnderLocalAppData()
    {
        var directory = new LocalAppDataDirectoryProvider().GetDefault();

        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TileWall");
        Assert.Equal(expectedRoot, directory.RootPath);
        Assert.Equal(Path.Combine(directory.RootPath, "Objects"), directory.ObjectsPath);
        Assert.Equal(Path.Combine(directory.RootPath, "Recovery"), directory.RecoveryPath);
    }

    [Fact]
    public void LocalFileStore_PowersRealConfigStore_RoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "tilewall-m2-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(new LocalFileStore(), new FakeDataDirectoryProvider(root));

            Assert.Equal(ConfigLoadStatus.Fresh, store.Load().Status);
            store.Save(ValidConfig(rows: 6));
            var loaded = store.Load();
            Assert.Equal(ConfigLoadStatus.Loaded, loaded.Status);
            Assert.Equal(new WallState(2, 6), loaded.Config!.Wall);

            store.Save(ValidConfig(rows: 8));
            Assert.True(File.Exists(Path.Combine(root, "Recovery", "config.prev.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
