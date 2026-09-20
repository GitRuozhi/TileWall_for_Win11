using System.Text;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Entries;

/// <summary>
/// T-COMMIT（M4 设计 §10.2）：§5.5 协议成功路径（INV-E1..E5 断言）+ F-1..F-4、F-7..F-14 故障注入矩阵
/// （每例断言 INV-E6：失败后正式区逐字节 == 操作前）。撤销材料行为（F-12）同文件覆盖。
/// </summary>
public class EntryCommitServiceTests : IDisposable
{
    private readonly EntryCommitHarness _h = new();

    // ————————————————————————————— 成功路径（INV-E1..E5） —————————————————————————————

    [Fact]
    public void 新建_程序目标_加号_唯一入口_撤销材料就位()
    {
        var config = _h.SeedEmptyTile();
        var draft = new TileDraft { TitleText = "计算器", Entry = new EntryDraft.CreateForPath("C:\\tools\\calc.exe") };
        var report = _h.Service.Commit(config, _h.Request(draft, objectId: "obj-new"));

        // INV-E1：每对象至多一个活动入口
        var entryRel = EntryPaths.EntryRelativePath("obj-new", "计算器.lnk");
        var entryPath = EntryPaths.Full(_h.Root, entryRel);
        Assert.True(_h.Files.Exists(entryPath));
        Assert.Single(_h.Files.FilePaths, p => p.StartsWith(EntryPaths.ObjectsDir(_h.Root, "obj-new") + Path.DirectorySeparatorChar, StringComparison.Ordinal));

        // INV-E2：config 指向存在的文件
        var loaded = report.NewConfig;
        var obj = Assert.IsType<TileObject>(loaded.Objects.Single(o => o.Id == "obj-new"));
        Assert.Equal(entryRel, obj.Entry!.RelativePath);
        Assert.True(_h.Files.Exists(EntryPaths.Full(_h.Root, obj.Entry.RelativePath)));

        // INV-E3：入口名过 EntryNames
        Assert.Empty(EntryNames.Validate(EntryNames.BaseNameOf(entryRel)));

        // 新建 .lnk 语义：目标=所选对象、参数空、工作目录=目标父目录（§5.3）
        var state = _h.Links.StateOf(entryPath);
        Assert.Equal("C:\\tools\\calc.exe", state.TargetPath);
        Assert.Equal(string.Empty, state.Arguments);
        Assert.Equal("C:\\tools", state.WorkingDirectory);

        // 可视状态：有入口 → TitleText 必须 null（单一真值），ShowTitle=文字非空
        Assert.Null(obj.Visual.TitleText);
        Assert.True(obj.Visual.ShowTitle);
        Assert.Equal(new GridRect(1, 0, 1, 1), obj.Bounds); // firstFit：(0,0) 被种子磁贴占住

        // 提交后：暂存区零残留 + 撤销材料（journal.json）就位
        _h.AssertNoStagingResidue();
        Assert.NotNull(report.UndoMaterial);
        Assert.True(_h.Files.Exists(Path.Combine(EntryPaths.RecoveryEntriesDir(_h.Root, report.UndoMaterial!.CommitId), CommitJournalFile.UndoFileName)));
        Assert.Equal(ConfigLoadStatus.Loaded, _h.Store.Load().Status);
    }

    [Fact]
    public void 新建_网址_url内容逐字节_CreateContent()
    {
        var config = _h.SeedEmptyTile();
        var url = "https://example.com/path?q=1";
        var report = _h.Service.Commit(config, _h.Request(new TileDraft { Entry = new EntryDraft.CreateFromUrl(url) }, objectId: "obj-url"));

        var entryPath = EntryPaths.Full(_h.Root, EntryPaths.EntryRelativePath("obj-url", "example.com.url")); // 推导名=主机
        Assert.True(_h.Files.Exists(entryPath));
        Assert.Equal(UrlShortcut.CreateContent(url), _h.Files.ReadAllBytes(entryPath)); // INV-E4：新建内容即规范字节

        var obj = Assert.IsType<TileObject>(report.NewConfig.Objects.Single(o => o.Id == "obj-url"));
        Assert.EndsWith(".url", obj.Entry!.RelativePath, StringComparison.Ordinal);
        Assert.False(obj.Visual.ShowTitle); // 文字为空 → 隐藏标题
    }

    [Fact]
    public void 改名_KeepCurrent加新文字_就地改名_字段保留()
    {
        var config = _h.SeedLinkedTile();
        var oldPath = _h.EntryPath(EntryCommitHarness.ObjectId, EntryNames.CombineName(EntryCommitHarness.EntryBaseName, EntryKind.Lnk));

        var report = _h.Service.Commit(config, _h.Request(new TileDraft { TitleText = "主力浏览器", Entry = new EntryDraft.KeepCurrent() }));

        var newRel = EntryPaths.EntryRelativePath(EntryCommitHarness.ObjectId, "主力浏览器.lnk");
        var newPath = EntryPaths.Full(_h.Root, newRel);
        Assert.False(_h.Files.Exists(oldPath)); // 旧名不在
        Assert.True(_h.Files.Exists(newPath)); // 新名就位（扩展名不变 [R10]）
        var state = _h.Links.StateOf(newPath); // INV-E5：入口内容不改写
        Assert.Equal("C:\\tools\\app.exe", state.TargetPath);
        Assert.Equal("--flag x", state.Arguments);
        Assert.Equal("C:\\tools", state.WorkingDirectory);
        Assert.Equal(newRel, Assert.IsType<TileObject>(report.NewConfig.Objects.Single(o => o.Id == EntryCommitHarness.ObjectId)).Entry!.RelativePath);
        _h.AssertNoStagingResidue(); // 改名成功：暂存清空；Recovery 材料（旧名副本+日志）留作撤销材料
    }

    [Fact]
    public void 类型切换_lnk换url_唯一入口换型_旧入口入Recovery()
    {
        var config = _h.SeedLinkedTile();
        var oldPath = _h.EntryPath(EntryCommitHarness.ObjectId, EntryNames.CombineName(EntryCommitHarness.EntryBaseName, EntryKind.Lnk));

        var report = _h.Service.Commit(config, _h.Request(new TileDraft { Entry = new EntryDraft.CreateFromUrl("https://portal.example/") }));

        var newRel = EntryPaths.EntryRelativePath(EntryCommitHarness.ObjectId, $"{EntryCommitHarness.EntryBaseName}.url");
        var newPath = EntryPaths.Full(_h.Root, newRel);
        Assert.False(_h.Files.Exists(oldPath)); // INV-E1：旧 .lnk 退位
        Assert.True(_h.Files.Exists(newPath));
        Assert.EndsWith("URL=https://portal.example/\r\n", Encoding.UTF8.GetString(_h.Files.ReadAllBytes(newPath)), StringComparison.Ordinal);

        var obj = Assert.IsType<TileObject>(report.NewConfig.Objects.Single(o => o.Id == EntryCommitHarness.ObjectId));
        Assert.Equal(newRel, obj.Entry!.RelativePath);

        // 旧入口短暂保留（§12.4）：Recovery/Entries/<cid>/<旧名> + journal.json
        Assert.NotNull(report.UndoMaterial);
        var materialDir = EntryPaths.RecoveryEntriesDir(_h.Root, report.UndoMaterial!.CommitId);
        Assert.True(_h.Files.Exists(Path.Combine(materialDir, $"{EntryCommitHarness.EntryBaseName}.lnk")));
        Assert.True(_h.Files.Exists(Path.Combine(materialDir, CommitJournalFile.UndoFileName)));
        _h.AssertNoStagingResidue();
    }

    [Fact]
    public void 清链接_NoneDraft_入口移除_配置置空()
    {
        var config = _h.SeedLinkedTile();
        var entryPath = _h.EntryPath(EntryCommitHarness.ObjectId, EntryNames.CombineName(EntryCommitHarness.EntryBaseName, EntryKind.Lnk));

        var report = _h.Service.Commit(config, _h.Request(new TileDraft { TitleText = "便签", Entry = new EntryDraft.NoneDraft() }));

        Assert.False(_h.Files.Exists(entryPath));
        Assert.DoesNotContain(_h.Files.FilePaths, p => p.Contains(EntryPaths.ObjectsDir(_h.Root, EntryCommitHarness.ObjectId) + Path.DirectorySeparatorChar, StringComparison.Ordinal)); // 无入口对象无目录残留（INV-E2 反向）
        var obj = Assert.IsType<TileObject>(report.NewConfig.Objects.Single(o => o.Id == EntryCommitHarness.ObjectId));
        Assert.Null(obj.Entry);
        Assert.Equal("便签", obj.Visual.TitleText); // 无入口对象直写 TitleText
        _h.AssertNoStagingResidue();
    }

    [Fact]
    public void 改名与改目标并存_先改内容后改名_新名下字段完整_INV_E5()
    {
        var config = _h.SeedLinkedTile();
        var oldPath = _h.EntryPath(EntryCommitHarness.ObjectId, EntryNames.CombineName(EntryCommitHarness.EntryBaseName, EntryKind.Lnk));

        var report = _h.Service.Commit(config, _h.Request(new TileDraft
        {
            TitleText = "主力浏览器",
            Entry = new EntryDraft.EditLnkTarget("C:\\tools\\app2.exe"),
        }));

        var newRel = EntryPaths.EntryRelativePath(EntryCommitHarness.ObjectId, "主力浏览器.lnk");
        var newPath = EntryPaths.Full(_h.Root, newRel);
        Assert.False(_h.Files.Exists(oldPath)); // 旧名退位
        Assert.True(_h.Files.Exists(newPath));
        var state = _h.Links.StateOf(newPath); // 新目标 + 原参数/工作目录（内容修改未被改名丢弃）
        Assert.Equal("C:\\tools\\app2.exe", state.TargetPath);
        Assert.Equal("--flag x", state.Arguments);
        Assert.Equal("C:\\tools", state.WorkingDirectory);
        Assert.Equal(newRel, Assert.IsType<TileObject>(report.NewConfig.Objects.Single(o => o.Id == EntryCommitHarness.ObjectId)).Entry!.RelativePath);
        _h.AssertNoStagingResidue();
    }

    [Fact]
    public void 改名与改URL行并存_新名下URL已改其余行保留()
    {
        var objectId = EntryCommitHarness.ObjectId;
        var oldRel = EntryPaths.EntryRelativePath(objectId, "page.url");
        var oldPath = EntryPaths.Full(_h.Root, oldRel);
        _h.Files.WriteAllBytes(oldPath, "[InternetShortcut]\r\nURL=https://old.example/\r\nIconFile=x.ico\r\n"u8.ToArray());
        var config = Layouts.Config(_h.Wall, [Layouts.Tile(objectId, new GridRect(0, 0, 1, 1), new EntryReference { RelativePath = oldRel })]);
        _h.Store.Save(config);

        var report = _h.Service.Commit(config, _h.Request(new TileDraft
        {
            TitleText = "新主页",
            Entry = new EntryDraft.EditUrlLine("https://new.example/"),
        }));

        var newRel = EntryPaths.EntryRelativePath(objectId, "新主页.url");
        var newPath = EntryPaths.Full(_h.Root, newRel);
        Assert.False(_h.Files.Exists(oldPath));
        Assert.Equal("[InternetShortcut]\r\nURL=https://new.example/\r\nIconFile=x.ico\r\n", Encoding.UTF8.GetString(_h.Files.ReadAllBytes(newPath)));
        Assert.Equal(newRel, Assert.IsType<TileObject>(report.NewConfig.Objects.Single(o => o.Id == objectId)).Entry!.RelativePath);
        _h.AssertNoStagingResidue();
    }

    [Fact]
    public void 编辑目标_只改目标_参数与工作目录原样_INV_E5()
    {
        var config = _h.SeedLinkedTile();
        var entryPath = _h.EntryPath(EntryCommitHarness.ObjectId, EntryNames.CombineName(EntryCommitHarness.EntryBaseName, EntryKind.Lnk));

        _h.Service.Commit(config, _h.Request(new TileDraft { Entry = new EntryDraft.EditLnkTarget("C:\\tools\\app2.exe") }));

        var state = _h.Links.StateOf(entryPath); // 探针 C 的测试化：新目标 + 原参数 + 原工作目录
        Assert.Equal("C:\\tools\\app2.exe", state.TargetPath);
        Assert.Equal("--flag x", state.Arguments);
        Assert.Equal("C:\\tools", state.WorkingDirectory);
    }

    [Fact]
    public void 编辑URL行_只改URL_其余行逐字节保留()
    {
        var objectId = EntryCommitHarness.ObjectId;
        var original = "[InternetShortcut]\r\nURL=https://old.example/\r\nIconFile=C:\\icons\\page.ico\r\nIconIndex=2\r\n"u8.ToArray();
        var entryRel = EntryPaths.EntryRelativePath(objectId, "page.url");
        var entryPath = EntryPaths.Full(_h.Root, entryRel);
        _h.Files.WriteAllBytes(entryPath, original);
        var config = Layouts.Config(_h.Wall, [Layouts.Tile(objectId, new GridRect(0, 0, 1, 1), new EntryReference { RelativePath = entryRel })]);
        _h.Store.Save(config);

        _h.Service.Commit(config, _h.Request(new TileDraft { Entry = new EntryDraft.EditUrlLine("https://new.example/") }));

        var after = _h.Files.ReadAllBytes(entryPath);
        Assert.Equal("[InternetShortcut]\r\nURL=https://new.example/\r\nIconFile=C:\\icons\\page.ico\r\nIconIndex=2\r\n", Encoding.UTF8.GetString(after));
    }

    [Fact]
    public void 导入完整副本_字节与源一致_INV_E4()
    {
        var config = _h.SeedEmptyTile();
        var sourcePath = Path.Combine(_h.Root, "import-src.url"); // 源在数据根外层（模拟用户所选文件）
        _h.Files.CreateDirectory(_h.Root);
        var sourceBytes = "[InternetShortcut]\r\nURL=https://import.example/\r\nIconFile=x.ico\r\n"u8.ToArray();
        _h.Files.WriteAllBytes(sourcePath, sourceBytes);

        _h.Service.Commit(config, _h.Request(new TileDraft { TitleText = "收藏页", Entry = new EntryDraft.CopyFromFile(sourcePath) }, objectId: "obj-import"));

        Assert.Equal(sourceBytes, _h.Files.ReadAllBytes(EntryPaths.Full(_h.Root, EntryPaths.EntryRelativePath("obj-import", "收藏页.url"))));
    }

    [Fact]
    public void 仅外观改动_零入口操作_入口字节不动_无撤销材料()
    {
        var config = _h.SeedLinkedTile();
        var entryPath = _h.EntryPath(EntryCommitHarness.ObjectId, EntryNames.CombineName(EntryCommitHarness.EntryBaseName, EntryKind.Lnk));
        var entryBytesBefore = _h.Files.ReadAllBytes(entryPath);

        var report = _h.Service.Commit(config, _h.Request(new TileDraft
        {
            TitleText = EntryCommitHarness.EntryBaseName, // 与当前名相同 → 无改名
            Entry = new EntryDraft.KeepCurrent(),
            BackgroundColorHex = "#0078D4",
        }));

        Assert.Equal(entryBytesBefore, _h.Files.ReadAllBytes(entryPath)); // §6.5：不为此重写 .lnk
        _h.AssertNoTransientResidue();
        Assert.Null(report.UndoMaterial); // 退化路径：纯配置撤销
        Assert.Equal("#0078D4", Assert.IsType<TileObject>(report.NewConfig.Objects.Single(o => o.Id == EntryCommitHarness.ObjectId)).Visual.BackgroundColor);
    }

    // ————————————————————————————— 故障注入矩阵 —————————————————————————————

    [Fact]
    public void F1_暂存写入失败_正式区逐字节不变()
    {
        var config = _h.SeedLinkedTile();
        var before = _h.Files.SnapshotFiles();
        _h.Files.EntryFault = EntryFaultPoint.BeforeStageWrite; // 新建 .url 的 WriteAllBytes 前抛

        var ex = Assert.Throws<IOException>(() => _h.Service.Commit(config, _h.Request(new TileDraft { Entry = new EntryDraft.CreateFromUrl("https://x.example/") })));
        Assert.Contains("BeforeStageWrite", ex.Message);
        _h.AssertFormalAreaUnchanged(before);
    }

    [Fact]
    public void F2_旧入口备份失败_正式区逐字节不变()
    {
        var config = _h.SeedLinkedTile();
        var before = _h.Files.SnapshotFiles();
        _h.Files.EntryFault = EntryFaultPoint.BeforeEntryBackup; // 类型切换的备份 Copy 前抛

        Assert.Throws<IOException>(() => _h.Service.Commit(config, _h.Request(new TileDraft { Entry = new EntryDraft.CreateFromUrl("https://x.example/") })));
        _h.AssertFormalAreaUnchanged(before);
    }

    [Fact]
    public void F3_入口生效中途失败_回滚后所有入口旧态()
    {
        // 单对象提交的生效阶段含「暂存→正式」与「旧入口→Recovery」两类移动；
        // 第 1 次暂存→正式移动前失败 = 新文件未落（最早可注入点），必须整体回滚。
        var config = _h.SeedLinkedTile();
        var before = _h.Files.SnapshotFiles();
        var oldPath = _h.EntryPath(EntryCommitHarness.ObjectId, EntryNames.CombineName(EntryCommitHarness.EntryBaseName, EntryKind.Lnk));
        var oldState = _h.Links.StateOf(oldPath);
        _h.Files.EntryFault = EntryFaultPoint.BeforeEntrySwap;
        _h.Files.EntrySwapMoveLimit = 1;

        Assert.Throws<IOException>(() => _h.Service.Commit(config, _h.Request(new TileDraft { Entry = new EntryDraft.CreateFromUrl("https://x.example/") })));

        // INV-E6 + INV-E1：旧 .lnk 原位原态、新 .url 不存在、配置未动、无残留
        Assert.Equal(oldState, _h.Links.StateOf(oldPath));
        _h.AssertFormalAreaUnchanged(before);
    }

    [Fact]
    public void F6_API路_步骤7收尾中断_提交仍成功_残留由Sweep清理()
    {
        // 步骤 6 配置已生效后，任何失败（此处注入在日志移交 Move）都不得回滚入口——
        // 协议吞掉收尾失败，暂存残留留给下次启动 Sweep 按「已完成」清理（§5.6 F-6 窗口的 API 内形态）。
        var config = _h.SeedLinkedTile();
        var oldPath = _h.EntryPath(EntryCommitHarness.ObjectId, EntryNames.CombineName(EntryCommitHarness.EntryBaseName, EntryKind.Lnk));
        _h.Files.EntryFault = EntryFaultPoint.BeforeEntrySwap;
        _h.Files.EntrySwapMoveLimit = 2; // 第 1 次=暂存→正式；第 2 次=步骤 7 日志移交（src 仍在 Staging）

        var report = _h.Service.Commit(config, _h.Request(new TileDraft { Entry = new EntryDraft.CreateFromUrl("https://x.example/") }));

        var newRel = EntryPaths.EntryRelativePath(EntryCommitHarness.ObjectId, $"{EntryCommitHarness.EntryBaseName}.url");
        Assert.Equal(newRel, Assert.IsType<TileObject>(_h.LoadConfig().Objects.Single(o => o.Id == EntryCommitHarness.ObjectId)).Entry!.RelativePath);
        Assert.True(_h.Files.Exists(EntryPaths.Full(_h.Root, newRel))); // 新入口保留（不回滚已提交配置）

        Assert.NotNull(report.UndoMaterial);
        var commitId = report.UndoMaterial!.CommitId;
        Assert.True(_h.Files.Exists(Path.Combine(EntryPaths.StagingDir(_h.Root, commitId), CommitJournalFile.StagingFileName))); // 残留：日志未移交
        var sweep = _h.Recovery.Sweep(); // 重演启动
        Assert.Contains(sweep.Actions, a => a.Contains(commitId, StringComparison.Ordinal) && a.Contains("已完成", StringComparison.Ordinal));
        Assert.False(_h.Files.Exists(Path.Combine(EntryPaths.StagingDir(_h.Root, commitId), CommitJournalFile.StagingFileName)));
        Assert.False(_h.Files.Exists(Path.Combine(EntryPaths.RecoveryEntriesDir(_h.Root, commitId), CommitJournalFile.UndoFileName))); // 材料一并清理（启动时撤销槽为空）
    }

    [Theory]
    [InlineData(FaultPoint.BeforeWriteTemp)]
    [InlineData(FaultPoint.BeforeBackup)]
    [InlineData(FaultPoint.BeforeMove)]
    public void F4_配置保存三点失败_入口同样回滚旧态(FaultPoint fault)
    {
        var config = _h.SeedLinkedTile();
        var before = _h.Files.SnapshotFiles();
        _h.Files.InjectedFault = fault;

        Assert.Throws<IOException>(() => _h.Service.Commit(config, _h.Request(new TileDraft { Entry = new EntryDraft.CreateFromUrl("https://x.example/") })));

        _h.AssertFormalAreaUnchanged(before); // 入口已回滚 + config.json 旧内容（M2 已证，此处连同断言）
    }

    [Fact]
    public void F7_改名目标非法_就地错误码_零写入()
    {
        foreach (var badName in new[] { "con", "名字:", "尾点.", "尾空格 ", new string('a', 201) })
        {
            var config = _h.SeedLinkedTile();
            var before = _h.Files.SnapshotFiles();

            var ex = Assert.Throws<DraftValidationException>(() => _h.Service.Commit(config, _h.Request(new TileDraft { TitleText = badName, Entry = new EntryDraft.KeepCurrent() })));
            Assert.NotEmpty(ex.Errors);
            Assert.All(ex.Errors, code => Assert.StartsWith("NAME_", code, StringComparison.Ordinal));
            _h.AssertFormalAreaUnchanged(before);
        }
    }

    [Fact]
    public void F8_改名Move失败_回滚旧名保留新名不存在()
    {
        var config = _h.SeedLinkedTile();
        var before = _h.Files.SnapshotFiles();
        var oldPath = _h.EntryPath(EntryCommitHarness.ObjectId, EntryNames.CombineName(EntryCommitHarness.EntryBaseName, EntryKind.Lnk));
        _h.Files.EntryFault = EntryFaultPoint.BeforeRenameMove;

        Assert.Throws<IOException>(() => _h.Service.Commit(config, _h.Request(new TileDraft { TitleText = "新名字", Entry = new EntryDraft.KeepCurrent() })));

        Assert.True(_h.Files.Exists(oldPath)); // INV-E6：旧名保留
        _h.AssertFormalAreaUnchanged(before);
    }

    [Fact]
    public void F9_特殊Shell入口_目标编辑拒绝_整体替换允许()
    {
        var config = _h.SeedLinkedTile();
        var entryPath = _h.EntryPath(EntryCommitHarness.ObjectId, EntryNames.CombineName(EntryCommitHarness.EntryBaseName, EntryKind.Lnk));
        _h.Links.Register(entryPath, target: null, hasIdList: true); // 探针 D 形态：GetPath 失败、IDList 存在
        var before = _h.Files.SnapshotFiles();

        var ex = Assert.Throws<DraftValidationException>(() => _h.Service.Commit(config, _h.Request(new TileDraft { Entry = new EntryDraft.EditLnkTarget("C:\\x.exe") })));
        Assert.Equal([DraftValidator.TargetNotPathEditable], ex.Errors);
        _h.AssertFormalAreaUnchanged(before);

        // 仅「替换整链」可用
        _h.Service.Commit(config, _h.Request(new TileDraft { Entry = new EntryDraft.CreateForPath("C:\\tools\\explorer.exe") }));
        var state = _h.Links.StateOf(entryPath);
        Assert.Equal("C:\\tools\\explorer.exe", state.TargetPath);
        Assert.False(state.HasIdList);
    }

    [Fact]
    public void F10_URL非法_就地错误码_零写入()
    {
        var config = _h.SeedEmptyTile();
        var before = _h.Files.SnapshotFiles();

        var ex = Assert.Throws<DraftValidationException>(() => _h.Service.Commit(config, _h.Request(new TileDraft { Entry = new EntryDraft.CreateFromUrl("ftp://x.example/f") })));
        Assert.Equal([UrlShortcut.UrlSchemeUnsupported], ex.Errors);
        _h.AssertFormalAreaUnchanged(before);
    }

    [Fact]
    public void F11_导入源不可读_零残留()
    {
        var config = _h.SeedEmptyTile();
        var before = _h.Files.SnapshotFiles();
        var missingSource = Path.Combine(_h.Root, "no-such-source.lnk");

        Assert.ThrowsAny<FileNotFoundException>(() => _h.Service.Commit(config, _h.Request(new TileDraft { Entry = new EntryDraft.CopyFromFile(missingSource) })));
        _h.AssertFormalAreaUnchanged(before); // §6.1「文件不可读…不留残片」
    }

    [Fact]
    public void F13_双对象同名改名_各自目录独立互不影响_C20()
    {
        var relA = EntryPaths.EntryRelativePath("obj-a", "浏览器.lnk");
        var relB = EntryPaths.EntryRelativePath("obj-b", "浏览器.lnk");
        _h.Links.Register(EntryPaths.Full(_h.Root, relA), "C:\\a\\browser.exe");
        _h.Links.Register(EntryPaths.Full(_h.Root, relB), "C:\\b\\browser.exe");
        var config = Layouts.Config(_h.Wall,
        [
            Layouts.Tile("obj-a", new GridRect(0, 0, 1, 1), new EntryReference { RelativePath = relA }),
            Layouts.Tile("obj-b", new GridRect(1, 0, 1, 1), new EntryReference { RelativePath = relB }),
        ]);
        _h.Store.Save(config);

        _h.Service.Commit(config, _h.Request(new TileDraft { TitleText = "浏览器A", Entry = new EntryDraft.KeepCurrent() }, objectId: "obj-a"));

        // obj-a 已改名、obj-b 原名不动（§6.3「不必自动改成 浏览器 (2)」）：各自 Objects/<id>/ 独立 Move
        Assert.True(_h.Files.Exists(_h.EntryPath("obj-a", "浏览器A.lnk")));
        Assert.False(_h.Files.Exists(_h.EntryPath("obj-a", "浏览器.lnk")));
        Assert.True(_h.Files.Exists(_h.EntryPath("obj-b", "浏览器.lnk")));
        Assert.Equal(EntryPaths.EntryRelativePath("obj-b", "浏览器.lnk"), Assert.IsType<TileObject>(_h.LoadConfig().Objects.Single(o => o.Id == "obj-b")).Entry!.RelativePath);
    }

    [Fact]
    public void F14_类型切换暂存失败_旧lnk原样仍唯一活动入口()
    {
        var config = _h.SeedLinkedTile();
        var before = _h.Files.SnapshotFiles();
        var oldPath = _h.EntryPath(EntryCommitHarness.ObjectId, EntryNames.CombineName(EntryCommitHarness.EntryBaseName, EntryKind.Lnk));
        _h.Files.EntryFault = EntryFaultPoint.BeforeStageWrite;

        Assert.Throws<IOException>(() => _h.Service.Commit(config, _h.Request(new TileDraft { Entry = new EntryDraft.CreateFromUrl("https://x.example/") })));

        // 旧 .lnk 仍唯一活动入口；外观/文字/位置不变（config 整体未动）
        var obj = Assert.IsType<TileObject>(_h.LoadConfig().Objects.Single(o => o.Id == EntryCommitHarness.ObjectId));
        Assert.EndsWith(".lnk", obj.Entry!.RelativePath, StringComparison.Ordinal);
        Assert.Equal(new GridRect(0, 0, 1, 1), obj.Bounds);
        _h.AssertFormalAreaUnchanged(before);
    }

    // ————————————————————————————— 撤销材料（F-12 服务侧） —————————————————————————————

    [Fact]
    public void 撤销材料还原_替换提交后恢复旧入口_材料仍在待配置回滚()
    {
        var config = _h.SeedLinkedTile();
        var oldPath = _h.EntryPath(EntryCommitHarness.ObjectId, EntryNames.CombineName(EntryCommitHarness.EntryBaseName, EntryKind.Lnk));
        var oldState = _h.Links.StateOf(oldPath);

        var report = _h.Service.Commit(config, _h.Request(new TileDraft { Entry = new EntryDraft.CreateForPath("C:\\tools\\new.exe") }));
        Assert.NotEqual(oldState, _h.Links.StateOf(oldPath)); // 已被替换（目标路径没变时旧内容已被覆盖/移除）

        Assert.True(_h.Recovery.RestoreMaterial(report.UndoMaterial!)); // §8.3 文件还原半步
        Assert.Equal(oldState, _h.Links.StateOf(oldPath));
        _h.Recovery.DeleteMaterial(report.UndoMaterial!.CommitId); // 配置回滚成功后清材料
        Assert.False(_h.Files.Exists(EntryPaths.RecoveryEntriesDir(_h.Root, report.UndoMaterial.CommitId)));
    }

    [Fact]
    public void 撤销材料缺失_还原失败返回false_F12语义()
    {
        var report = new EntryUndoMaterial("nonexistent-commit", [EntryCommitHarness.ObjectId]);
        Assert.False(_h.Recovery.RestoreMaterial(report)); // 调用方必须中止撤销
    }

    public void Dispose() => _h.Dispose();
}
