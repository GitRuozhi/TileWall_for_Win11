using System.Text;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Entries;

/// <summary>
/// T-JOURNAL（M4 设计 §10.2）：F-5/F-6 崩溃窗口重演 + 幂等回滚重入 + 启动清扫过期材料清理。
/// F-5/F-6 按设计「手工留下 staging+日志后重演启动」：用 CommitJournalFile 写真实日志构造中断态。
/// </summary>
public class EntryRecoveryTests : IDisposable
{
    private readonly EntryCommitHarness _h = new();

    [Fact]
    public void F5_步骤5后步骤6前崩溃_Sweep幂等回滚_配置与入口一致()
    {
        // 旧态：o1 → Objects/o1/工作浏览器.lnk（旧 .lnk）
        var config = _h.SeedLinkedTile();
        var oldRel = EntryPaths.EntryRelativePath(EntryCommitHarness.ObjectId, $"{EntryCommitHarness.EntryBaseName}.lnk");
        var oldPath = EntryPaths.Full(_h.Root, oldRel);
        var newRel = EntryPaths.EntryRelativePath(EntryCommitHarness.ObjectId, "portal.url");
        var newPath = EntryPaths.Full(_h.Root, newRel);
        var oldBytes = _h.Files.ReadAllBytes(oldPath);

        // 手工构造「步骤 5 完成、步骤 6 未执行」的磁盘形态（类型切换 replace）：
        var commitId = "crash5" + Guid.NewGuid().ToString("N")[..8];
        var newUrlBytes = UrlShortcut.CreateContent("https://portal.example/");
        _h.Files.WriteAllBytes(newPath, newUrlBytes); // 新入口已生效
        _h.Files.Delete(oldPath); // 旧入口已被移入 Recovery
        var materialDir = EntryPaths.RecoveryEntriesDir(_h.Root, commitId);
        _h.Files.CreateDirectory(materialDir);
        _h.Files.WriteAllBytes(Path.Combine(materialDir, $"{EntryCommitHarness.EntryBaseName}.lnk"), oldBytes);
        var journal = new CommitJournal(commitId, [new JournalOp("replace", EntryCommitHarness.ObjectId, oldRel, newRel, null, null)]);
        var stagingDir = EntryPaths.StagingDir(_h.Root, commitId);
        _h.Files.CreateDirectory(stagingDir);
        CommitJournalFile.Write(_h.Files, Path.Combine(stagingDir, CommitJournalFile.StagingFileName), journal);
        // config.json 未动（旧配置）——上文 SeedLinkedTile 已保存

        // 重演启动：Sweep → 检测不一致 → 幂等回滚
        var report = _h.Recovery.Sweep();
        Assert.Contains(report.Actions, a => a.Contains(commitId, StringComparison.Ordinal) && a.Contains("回滚", StringComparison.Ordinal));

        Assert.Equal(oldBytes, _h.Files.ReadAllBytes(oldPath)); // 旧入口逐字节还原
        Assert.False(_h.Files.Exists(newPath)); // 新入口移除
        _h.AssertNoTransientResidue(); // 暂存 + 材料已清
        Assert.Equal(ConfigLoadStatus.Loaded, _h.Store.Load().Status); // 配置有效且全部入口存在（INV-E2）

        // 幂等重入：连跑两次结果一致
        var second = _h.Recovery.Sweep();
        Assert.DoesNotContain(second.Actions, a => a.Contains(commitId, StringComparison.Ordinal));
        Assert.Equal(oldBytes, _h.Files.ReadAllBytes(oldPath));
    }

    [Fact]
    public void F6_步骤6后步骤7前崩溃_Sweep判定已完成_保留新版仅清材料()
    {
        var config = _h.SeedLinkedTile();
        var oldRel = EntryPaths.EntryRelativePath(EntryCommitHarness.ObjectId, $"{EntryCommitHarness.EntryBaseName}.lnk");
        var oldPath = EntryPaths.Full(_h.Root, oldRel);
        var newRel = EntryPaths.EntryRelativePath(EntryCommitHarness.ObjectId, "portal.url");
        var newPath = EntryPaths.Full(_h.Root, newRel);
        var newUrlBytes = UrlShortcut.CreateContent("https://portal.example/");

        // 提交完成版配置（指向新 .url）+ 步骤 7 未做的残留
        var committed = config with
        {
            Objects = [.. config.Objects.Select(o => o.Id == EntryCommitHarness.ObjectId
                ? o with { Entry = new EntryReference { RelativePath = newRel } }
                : o)],
        };
        _h.Store.Save(committed); // 步骤 6 已生效
        _h.Files.WriteAllBytes(newPath, newUrlBytes);
        _h.Files.Delete(oldPath);
        var commitId = "crash6" + Guid.NewGuid().ToString("N")[..8];
        var materialDir = EntryPaths.RecoveryEntriesDir(_h.Root, commitId);
        _h.Files.CreateDirectory(materialDir);
        var journal = new CommitJournal(commitId, [new JournalOp("replace", EntryCommitHarness.ObjectId, oldRel, newRel, null, null)]);
        var stagingDir = EntryPaths.StagingDir(_h.Root, commitId);
        _h.Files.CreateDirectory(stagingDir);
        CommitJournalFile.Write(_h.Files, Path.Combine(stagingDir, CommitJournalFile.StagingFileName), journal);

        var report = _h.Recovery.Sweep();
        Assert.Contains(report.Actions, a => a.Contains(commitId, StringComparison.Ordinal) && a.Contains("已完成", StringComparison.Ordinal));

        Assert.Equal(newUrlBytes, _h.Files.ReadAllBytes(newPath)); // 新版入口保留
        Assert.False(_h.Files.Exists(oldPath));
        Assert.Equal(newRel, Assert.IsType<TileObject>(_h.LoadConfig().Objects.Single(o => o.Id == EntryCommitHarness.ObjectId)).Entry!.RelativePath);
        _h.AssertNoTransientResidue();
    }

    [Fact]
    public void Sweep_无日志暂存区_仅删残留_正式区零改动()
    {
        var config = _h.SeedLinkedTile();
        var before = _h.Files.SnapshotFiles();
        var stagingDir = EntryPaths.StagingDir(_h.Root, "halfstage");
        _h.Files.CreateDirectory(stagingDir);
        _h.Files.WriteAllBytes(Path.Combine(stagingDir, "half-written.url"), "垃圾残留"u8.ToArray());

        var report = _h.Recovery.Sweep();
        Assert.Contains(report.Actions, a => a.Contains("无日志", StringComparison.Ordinal));
        _h.AssertFormalAreaUnchanged(before);
    }

    [Fact]
    public void Sweep_日志不可读_仅清暂存_不动正式区()
    {
        var config = _h.SeedLinkedTile();
        var before = _h.Files.SnapshotFiles();
        var stagingDir = EntryPaths.StagingDir(_h.Root, "badjournal");
        _h.Files.CreateDirectory(stagingDir);
        _h.Files.WriteAllBytes(Path.Combine(stagingDir, CommitJournalFile.StagingFileName), "{ this is not json"u8.ToArray());

        var report = _h.Recovery.Sweep();
        Assert.Contains(report.Actions, a => a.Contains("不可读", StringComparison.Ordinal));
        _h.AssertFormalAreaUnchanged(before);
    }

    [Fact]
    public void Sweep_配置损坏加未完成日志_按回滚处理()
    {
        var config = _h.SeedLinkedTile();
        var oldRel = EntryPaths.EntryRelativePath(EntryCommitHarness.ObjectId, $"{EntryCommitHarness.EntryBaseName}.lnk");
        var oldPath = EntryPaths.Full(_h.Root, oldRel);
        var oldBytes = _h.Files.ReadAllBytes(oldPath);
        var commitId = "corrupt" + Guid.NewGuid().ToString("N")[..8];

        _h.Files.WriteAllBytes(_h.ConfigPath, "broken json"u8.ToArray()); // 配置不可解析
        var journal = new CommitJournal(commitId, [new JournalOp("remove", EntryCommitHarness.ObjectId, oldRel, null, null, null)]);
        var stagingDir = EntryPaths.StagingDir(_h.Root, commitId);
        _h.Files.CreateDirectory(stagingDir);
        CommitJournalFile.Write(_h.Files, Path.Combine(stagingDir, CommitJournalFile.StagingFileName), journal);
        var materialDir = EntryPaths.RecoveryEntriesDir(_h.Root, commitId);
        _h.Files.CreateDirectory(Path.Combine(materialDir, "removed"));
        _h.Files.WriteAllBytes(Path.Combine(materialDir, "removed", $"{EntryCommitHarness.EntryBaseName}.lnk"), oldBytes);
        _h.Files.Delete(oldPath); // remove 已生效

        _h.Recovery.Sweep(); // 配置不一致 → 回滚 → 旧入口还原

        Assert.Equal(oldBytes, _h.Files.ReadAllBytes(oldPath));
        _h.AssertNoTransientResidue();
    }

    [Fact]
    public void Sweep_清理过期撤销材料_保留不动正式区()
    {
        var config = _h.SeedLinkedTile();
        var before = _h.Files.SnapshotFiles();
        var staleDir = EntryPaths.RecoveryEntriesDir(_h.Root, "stale-material");
        _h.Files.CreateDirectory(staleDir);
        _h.Files.WriteAllBytes(Path.Combine(staleDir, "old.lnk"), "x"u8.ToArray());

        var report = _h.Recovery.Sweep();
        Assert.Contains(report.Actions, a => a.Contains("过期撤销材料", StringComparison.Ordinal));
        _h.AssertFormalAreaUnchanged(before); // 正式区（Objects/Objects config）零改动
    }

    [Fact]
    public void F5_add类提交步骤5后崩溃_Sweep按指纹回滚删除新入口孤儿()
    {
        // 旧配置：空墙（不引用新对象）——「配置引用存在性」判定不了 add 类，必须靠指纹
        var oldConfig = Layouts.Config(_h.Wall, []);
        _h.Store.Save(oldConfig);
        var newRel = EntryPaths.EntryRelativePath("obj-add", "新入口.lnk");
        var newPath = EntryPaths.Full(_h.Root, newRel);
        _h.Links.Register(newPath, "C:\\x\\new.exe"); // 步骤 5 已生效：新入口已落盘（孤儿）

        var commitId = "add5" + Guid.NewGuid().ToString("N")[..8];
        // 生产语义：日志记录「拟写入的新配置」指纹；步骤 5 后崩溃 → 盘上仍是旧配置 → 指纹不匹配 → 回滚
        var newConfig = oldConfig with
        {
            Objects = [.. oldConfig.Objects, Layouts.Tile("obj-add", new GridRect(1, 0, 1, 1), new EntryReference { RelativePath = newRel })],
        };
        var journal = new CommitJournal(commitId, [new JournalOp("add", "obj-add", null, newRel, null, null)], CommitJournalFile.Fingerprint(newConfig));
        var stagingDir = EntryPaths.StagingDir(_h.Root, commitId);
        _h.Files.CreateDirectory(stagingDir);
        CommitJournalFile.Write(_h.Files, Path.Combine(stagingDir, CommitJournalFile.StagingFileName), journal);

        var report = _h.Recovery.Sweep();
        Assert.Contains(report.Actions, a => a.Contains(commitId, StringComparison.Ordinal) && a.Contains("未完成", StringComparison.Ordinal));

        Assert.False(_h.Files.Exists(newPath)); // 孤儿新入口被幂等回滚删除
        _h.AssertNoTransientResidue();
        Assert.True(ConfigJson.Serialize(oldConfig).AsSpan().SequenceEqual(_h.Files.ReadAllBytes(_h.ConfigPath))); // 配置本来就未动
    }

    [Fact]
    public void F6_add类提交步骤6后崩溃_Sweep按指纹判已完成_保留新入口()
    {
        var newRel = EntryPaths.EntryRelativePath("obj-add", "新入口.lnk");
        var newPath = EntryPaths.Full(_h.Root, newRel);
        _h.Links.Register(newPath, "C:\\x\\new.exe");
        var newConfig = Layouts.Config(_h.Wall, [Layouts.Tile("obj-add", new GridRect(0, 0, 1, 1), new EntryReference { RelativePath = newRel })]);
        _h.Store.Save(newConfig); // 步骤 6 已生效

        var commitId = "add6" + Guid.NewGuid().ToString("N")[..8];
        var journal = new CommitJournal(commitId, [new JournalOp("add", "obj-add", null, newRel, null, null)], CommitJournalFile.Fingerprint(newConfig));
        var stagingDir = EntryPaths.StagingDir(_h.Root, commitId);
        _h.Files.CreateDirectory(stagingDir);
        CommitJournalFile.Write(_h.Files, Path.Combine(stagingDir, CommitJournalFile.StagingFileName), journal);

        var report = _h.Recovery.Sweep();
        Assert.Contains(report.Actions, a => a.Contains(commitId, StringComparison.Ordinal) && a.Contains("已完成", StringComparison.Ordinal));

        Assert.True(_h.Files.Exists(newPath)); // 新入口保留（不误回滚）
        _h.AssertNoTransientResidue();
    }

    [Fact]
    public void F5_同路径replace加属性改动_步骤5后崩溃_Sweep回滚URL字节()
    {
        // 改 URL 行 + 改背景色同批提交：From==To，配置引用存在性判不出，属性差异只能靠指纹
        var objectId = EntryCommitHarness.ObjectId;
        var rel = EntryPaths.EntryRelativePath(objectId, "page.url");
        var path = EntryPaths.Full(_h.Root, rel);
        var oldBytes = "[InternetShortcut]\r\nURL=https://old.example/\r\nIconFile=x.ico\r\n"u8.ToArray();
        var newBytes = "[InternetShortcut]\r\nURL=https://new.example/\r\nIconFile=x.ico\r\n"u8.ToArray();
        _h.Files.WriteAllBytes(path, oldBytes);
        var oldConfig = Layouts.Config(_h.Wall, [Layouts.Tile(objectId, new GridRect(0, 0, 1, 1), new EntryReference { RelativePath = rel })]);
        _h.Store.Save(oldConfig);
        var newConfig = oldConfig with
        {
            Objects = [.. oldConfig.Objects.Select(o => o with { Visual = o.Visual with { BackgroundColor = "#FF0000" } })],
        };
        _h.Files.WriteAllBytes(path, newBytes); // 步骤 5 已生效：URL 行已是新值

        var commitId = "same5" + Guid.NewGuid().ToString("N")[..8];
        var journal = new CommitJournal(commitId, [new JournalOp("replace", objectId, rel, rel, null, null)], CommitJournalFile.Fingerprint(newConfig));
        var stagingDir = EntryPaths.StagingDir(_h.Root, commitId);
        _h.Files.CreateDirectory(stagingDir);
        CommitJournalFile.Write(_h.Files, Path.Combine(stagingDir, CommitJournalFile.StagingFileName), journal);
        var materialDir = EntryPaths.RecoveryEntriesDir(_h.Root, commitId);
        _h.Files.CreateDirectory(materialDir);
        _h.Files.WriteAllBytes(Path.Combine(materialDir, "page.url"), oldBytes); // 步骤 4 备份

        var report = _h.Recovery.Sweep();
        Assert.Contains(report.Actions, a => a.Contains(commitId, StringComparison.Ordinal) && a.Contains("未完成", StringComparison.Ordinal));

        Assert.Equal(oldBytes, _h.Files.ReadAllBytes(path)); // URL 改动随回滚还原，不与属性回退形成半提交
        Assert.True(ConfigJson.Serialize(oldConfig).AsSpan().SequenceEqual(_h.Files.ReadAllBytes(_h.ConfigPath)));
        _h.AssertNoTransientResidue();
    }

    [Fact]
    public void RemoveEntry_取消固定有入口对象_入口入removed_配置移除对象()
    {
        var config = _h.SeedLinkedTile();
        var oldRel = EntryPaths.EntryRelativePath(EntryCommitHarness.ObjectId, $"{EntryCommitHarness.EntryBaseName}.lnk");

        var report = _h.Service.RemoveEntry(config, EntryCommitHarness.ObjectId, "从磁贴墙取消固定");

        Assert.DoesNotContain(_h.LoadConfig().Objects, o => o.Id == EntryCommitHarness.ObjectId); // 对象移除
        _h.AssertNoStagingResidue(); // remove 成功：材料（removed/ 副本+日志）留作撤销材料
        Assert.NotNull(report.UndoMaterial);

        // 撤销：文件半步还原（从 removed/）+ 配置回滚（此处验证文件半步）
        Assert.True(_h.Recovery.RestoreMaterial(report.UndoMaterial));
        Assert.True(_h.Files.Exists(EntryPaths.Full(_h.Root, oldRel)));
    }

    public void Dispose() => _h.Dispose();
}
