using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Entries;

/// <summary>
/// T-COMMIT/T-JOURNAL 共享夹具：内存文件系统 + 伪链服务 + 确定性小墙（2 栏 × 4 行 = 16 列 × 4 行）。
/// 根目录用真实感字符串（含盘符形状），键内路径一律经 Path.Combine —— 与 EntryPaths 的故障模式标记一致。
/// </summary>
internal sealed class EntryCommitHarness : IDisposable
{
    public const string ObjectId = "obj-0001";
    public const string EntryBaseName = "工作浏览器";

    public InMemoryFileStore Files { get; } = new();
    public FakeLinkFileService Links { get; }
    public string Root { get; } = Path.Combine("C:", "mem", "tilewall-" + Guid.NewGuid().ToString("N")[..8]);
    public ConfigStore Store { get; }
    public EntryCommitService Service { get; }
    public EntryRecovery Recovery { get; }
    public WallGrid Wall { get; } = new(2, 4);

    public EntryCommitHarness()
    {
        Links = new FakeLinkFileService(Files);
        Store = new ConfigStore(Files, new FakeDataDirectoryProvider(Root));
        Recovery = new EntryRecovery(Files, Links, new FakeDataDirectoryProvider(Root).GetDefault());
        Service = new EntryCommitService(Files, Store, Links, new FakeDataDirectoryProvider(Root));
    }

    public string ConfigPath => Store.ConfigPath;

    public string EntryPath(string objectId, string fileName) => EntryPaths.Full(Root, EntryPaths.EntryRelativePath(objectId, fileName));

    /// <summary>装配一个带 .lnk 入口的既有磁贴（1×1，位于 (0,0)）并保存 config.json。</summary>
    public TileWallConfig SeedLinkedTile(string objectId = ObjectId, string baseName = EntryBaseName, string target = "C:\\tools\\app.exe", string arguments = "--flag x", string workingDirectory = "C:\\tools")
    {
        var entryPath = EntryPath(objectId, EntryNames.CombineName(baseName, EntryKind.Lnk));
        Links.Register(entryPath, target, arguments, workingDirectory);
        var config = Layouts.Config(Wall, [Layouts.Tile(objectId, new GridRect(0, 0, 1, 1), new EntryReference { RelativePath = EntryPaths.EntryRelativePath(objectId, EntryNames.CombineName(baseName, EntryKind.Lnk)) })]);
        Store.Save(config);
        return config;
    }

    /// <summary>装配一个空目标磁贴（无 Objects/&lt;id&gt;/ 目录）并保存 config.json。</summary>
    public TileWallConfig SeedEmptyTile(string objectId = ObjectId, GridRect? bounds = null)
    {
        var config = Layouts.Config(Wall, [Layouts.Tile(objectId, bounds ?? new GridRect(0, 0, 1, 1))]);
        Store.Save(config);
        return config;
    }

    public EntryCommitRequest Request(TileDraft draft, string objectId = ObjectId, string actionName = "编辑磁贴") =>
        new(objectId, actionName, draft);

    public TileWallConfig LoadConfig() => ConfigJson.Deserialize(Files.ReadAllBytes(ConfigPath));

    /// <summary>
    /// INV-E6：提交失败后正式区与快照逐字节一致。快照中没有的键只允许
    /// Recovery/config.prev.json（M2 既有 ConfigStore 语义：备份先于 Move 提交点）。
    /// </summary>
    public void AssertFormalAreaUnchanged(Dictionary<string, byte[]> before)
    {
        var after = Files.SnapshotFiles();
        foreach (var key in before.Keys)
        {
            Assert.True(after.TryGetValue(key, out var actual), $"文件丢失：{key}");
            Assert.True(before[key].AsSpan().SequenceEqual(actual), $"文件字节不一致：{key}");
        }

        var prevBackup = Path.Combine(Root, "Recovery" + Path.DirectorySeparatorChar + ConfigStore.BackupFileName);
        Assert.DoesNotContain(after.Keys, k => !before.ContainsKey(k) && !string.Equals(k, prevBackup, StringComparison.Ordinal));

        AssertNoStagingResidue();
    }

    /// <summary>暂存区（Staging/）不得残留任何键——失败与成功路径都必须成立。</summary>
    public void AssertNoStagingResidue()
    {
        var stagingRoot = EntryPaths.StagingRoot(Root) + Path.DirectorySeparatorChar;
        Assert.DoesNotContain(Files.FilePaths, p => p.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Staging 与 Recovery/Entries 内不得残留任何键（半提交检测——仅失败路径断言）。</summary>
    public void AssertNoTransientResidue()
    {
        AssertNoStagingResidue();
        var recoveryEntriesRoot = EntryPaths.RecoveryEntriesRoot(Root) + Path.DirectorySeparatorChar;
        Assert.DoesNotContain(Files.FilePaths, p => p.StartsWith(recoveryEntriesRoot, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
    }
}
