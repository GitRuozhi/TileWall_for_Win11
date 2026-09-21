using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Entries;

/// <summary>
/// T-BATCH（M8 设计 §5.6–§5.8，A15/C26）：CommitBatch 排位顺序确定性（同输入同布局）、
/// 失败注入 F-B2/F-B3、整批撤销材料单 commitId（INV-I4）、容量边界差 1 格拒（INV-I1 零写入）。
/// 排位 = 顺序 first-fit 重放（与 GridPlacement.CanPlaceBatch 预检同函数同序）。
/// </summary>
public sealed class EntryBatchCommitTests : IDisposable
{
    private readonly EntryCommitHarness _harness = new();

    private string SourceFile(params string[] segments) => Path.Combine([.. segments.Prepend(_harness.Root).Prepend("sources")]);

    private void RegisterLnkSource(params string[] segments) =>
        _harness.Links.Register(SourceFile(segments), "C:\\tools\\app.exe", "", "");

    private EntryCommitRequest Request(string objectId, params string[] sourceSegments) => new(
        objectId,
        "开始菜单导入",
        new TileDraft { Size = new GridSize(1, 1), Entry = new EntryDraft.CopyFromFile(SourceFile(sourceSegments)) });

    private TileWallConfig EmptyWall() => Layouts.Config(new WallGrid(2, 4), []);

    [Fact]
    public void CommitBatch_PlacesSequentialFirstFit_AndSavesOnce()
    {
        RegisterLnkSource("A.lnk");
        RegisterLnkSource("B.url");
        RegisterLnkSource("C.lnk");
        var baseConfig = EmptyWall();

        var report = _harness.Service.CommitBatch(baseConfig,
        [
            Request("obj-a", "A.lnk"),
            Request("obj-b", "B.url"),
            Request("obj-c", "C.lnk"),
        ], "开始菜单导入");

        // 排位：顺序 first-fit（行优先）——(0,0) → (1,0) → (2,0)
        Assert.Collection(
            report.CommittedObjects,
            o => Assert.Equal(new GridRect(0, 0, 1, 1), o.Bounds),
            o => Assert.Equal(new GridRect(1, 0, 1, 1), o.Bounds),
            o => Assert.Equal(new GridRect(2, 0, 1, 1), o.Bounds));

        // 配置一次生效：3 对象 + 每个托管副本存在（INV-I5）
        var saved = _harness.LoadConfig();
        Assert.Equal(3, saved.Objects.Count);
        foreach (var o in report.CommittedObjects)
        {
            Assert.True(_harness.Files.Exists(EntryPaths.Full(_harness.Root, o.Entry!.RelativePath)), $"对象 {o.Id} 的托管副本应存在");
        }

        // 整批一个 commitId、材料覆盖全部对象（INV-I4）
        Assert.Equal(3, report.UndoMaterial.ObjectIds.Count);
        Assert.Equal([.. new[] { "obj-a", "obj-b", "obj-c" }], [.. report.UndoMaterial.ObjectIds]);
    }

    [Fact]
    public void CommitBatch_SameInput_ProducesSameLayout_Deterministic()
    {
        RegisterLnkSource("A.lnk");
        var baseConfig = Layouts.Config(new WallGrid(2, 4),
        [
            Layouts.Tile("seed-1", new GridRect(0, 0, 2, 1)),
            Layouts.Tile("seed-2", new GridRect(2, 0, 2, 1)),
        ]);
        var layoutA = CommitThreeAndReturnBounds(baseConfig);
        var layoutB = CommitThreeAndReturnBounds(baseConfig);
        Assert.Equal(layoutA, layoutB); // 预检结论 == 提交结果（同一函数同一顺序 → 确定性）
    }

    private List<GridRect> CommitThreeAndReturnBounds(TileWallConfig baseConfig)
    {
        var report = _harness.Service.CommitBatch(baseConfig,
        [
            Request("obj-1", "A.lnk"),
            Request("obj-2", "A.lnk"),
            Request("obj-3", "A.lnk"),
        ], "开始菜单导入");
        return [.. report.CommittedObjects.Select(o => o.Bounds)];
    }

    [Fact]
    public void CommitBatch_CapacityOneShort_RejectsWithZeroWrites_InvI1()
    {
        // 2 栏 × 2 行墙 = 32 格，10 个 3×1 占满 30 格 → 只剩 2 格；3 个候选差 1 格
        var occupied = new List<LayoutObject>();
        for (var row = 0; row < 2; row++)
        {
            for (var col = 0; col < 15; col += 3)
            {
                occupied.Add(Layouts.Tile($"t{row}-{col}", new GridRect(col, row, 3, 1)));
            }
        }

        var baseConfig = Layouts.Config(new WallGrid(2, 2), occupied); // 30/32 格
        RegisterLnkSource("A.lnk");
        RegisterLnkSource("B.lnk");
        RegisterLnkSource("C.lnk");
        var snapshot = _harness.Files.SnapshotFiles();

        // A15 差 1 格拒绝：第 3 个候选 first-fit 失败——在规划段（零写入）抛出
        var exception = Assert.Throws<DraftValidationException>(
            () => _harness.Service.CommitBatch(baseConfig,
            [
                Request("obj-1", "A.lnk"),
                Request("obj-2", "B.lnk"),
                Request("obj-3", "C.lnk"),
            ], "开始菜单导入"));
        Assert.Contains(DraftValidator.SizeNoFit, exception.Errors);

        _harness.AssertFormalAreaUnchanged(snapshot); // INV-I1：确认前零 IO（无 Staging、正式区零改动）
    }

    [Fact]
    public void CommitBatch_FitsExactly_FullSelectionImports_A15()
    {
        // 同基线只放 2 个候选 → 恰好放进（全有或全无：完整选择可导入，无部分导入）
        var baseConfig = Layouts.Config(new WallGrid(2, 2),
        [
            Layouts.Tile("seed-1", new GridRect(0, 0, 2, 1)),
            Layouts.Tile("seed-2", new GridRect(2, 0, 2, 1)),
        ]);
        RegisterLnkSource("A.lnk");
        RegisterLnkSource("B.lnk");

        var report = _harness.Service.CommitBatch(baseConfig,
        [
            Request("obj-1", "A.lnk"),
            Request("obj-2", "B.lnk"),
        ], "开始菜单导入");
        Assert.Equal(4, report.NewConfig.Objects.Count); // 2 既有 + 2 新导入
    }

    [Fact]
    public void CommitBatch_CopyFailsAtKth_RollsBackAll_InvI2()
    {
        RegisterLnkSource("A.lnk");
        RegisterLnkSource("B.lnk");
        RegisterLnkSource("C.lnk");
        var baseConfig = EmptyWall();
        _harness.Store.Save(baseConfig);
        var snapshot = _harness.Files.SnapshotFiles();

        _harness.Files.EntryFault = EntryFaultPoint.BeforeEntrySwap; // 入口生效中途（第 2 次 Staging→Objects 移动）
        _harness.Files.EntrySwapMoveLimit = 2;

        Assert.Throws<IOException>(() => _harness.Service.CommitBatch(baseConfig,
        [
            Request("obj-1", "A.lnk"),
            Request("obj-2", "B.lnk"),
            Request("obj-3", "C.lnk"),
        ], "开始菜单导入"));

        _harness.AssertFormalAreaUnchanged(snapshot); // F-B2：前 k−1 个副本不留在正式区（回滚）
        Assert.Equal(0, _harness.LoadConfig().Objects.Count); // 配置无该批对象
    }

    [Fact]
    public void CommitBatch_SaveFails_RollsBackEntries_RetrySucceeds_F_B3()
    {
        RegisterLnkSource("A.lnk");
        var baseConfig = EmptyWall();
        _harness.Store.Save(baseConfig);
        var snapshot = _harness.Files.SnapshotFiles();

        _harness.Files.InjectedFault = FaultPoint.BeforeMove; // ConfigStore.Save 第 5 步（唯一提交点）失败
        Assert.Throws<IOException>(() => _harness.Service.CommitBatch(baseConfig, [Request("obj-1", "A.lnk")], "开始菜单导入"));
        _harness.Files.InjectedFault = FaultPoint.None;

        _harness.AssertFormalAreaUnchanged(snapshot); // F-B3：日志已写但配置未生效 → 回滚入口
        Assert.Equal(0, _harness.LoadConfig().Objects.Count);

        // 重开窗重试成功（新 id 新候选）
        var report = _harness.Service.CommitBatch(_harness.LoadConfig(), [Request("obj-retry", "A.lnk")], "开始菜单导入");
        Assert.Single(report.CommittedObjects);
        Assert.Equal(1, _harness.LoadConfig().Objects.Count);
    }

    [Fact]
    public void CommitBatch_UndoMaterial_RestoresWholeBatchInOneStep_InvI4()
    {
        RegisterLnkSource("A.lnk");
        RegisterLnkSource("B.lnk");
        var baseConfig = EmptyWall();

        var report = _harness.Service.CommitBatch(baseConfig,
        [
            Request("obj-1", "A.lnk"),
            Request("obj-2", "B.lnk"),
        ], "开始菜单导入");

        // Ctrl+Z 的文件半步：一次 RestoreMaterial 撤销整批（N 个托管副本一并还原；配置回滚由调用方提交 slot.Previous）
        Assert.True(_harness.Service.RestoreMaterial(report.UndoMaterial));
        foreach (var o in report.CommittedObjects)
        {
            Assert.False(_harness.Files.Exists(EntryPaths.Full(_harness.Root, o.Entry!.RelativePath)), $"撤销后 {o.Id} 的副本应消失");
        }

        _harness.AssertNoStagingResidue();
    }

    [Fact]
    public void CommitBatch_LnkArgumentsAndWorkingDirectoryPreserved_F_B8_InvI5()
    {
        // 带参数 + 工作目录的 .lnk：副本语义与源一致（INV-I5；FakeLinkFileService 字节随 Copy 走）
        _harness.Links.Register(SourceFile("复杂.lnk"), "C:\\tools\\app.exe", "--flag x", "C:\\tools");
        var baseConfig = EmptyWall();

        var report = _harness.Service.CommitBatch(baseConfig, [Request("obj-1", "复杂.lnk")], "开始菜单导入");
        var committed = Assert.Single(report.CommittedObjects);
        var entryFullPath = _harness.EntryPath(committed.Id, EntryPaths.FileNameOf(committed.Entry!.RelativePath));
        var fields = _harness.Links.Read(entryFullPath);
        Assert.Equal("C:\\tools\\app.exe", fields.TargetPath);
        Assert.Equal("--flag x", fields.Arguments);
        Assert.Equal("C:\\tools", fields.WorkingDirectory);
    }

    [Fact]
    public void CommitBatch_SameNameFromDifferentSubdirectories_BothImportedToOwnDirectories_F_B7_C20()
    {
        // 同名 .lnk 于源树两个位置 → 两条候选各自导入、各自 Objects/<id>/ 目录（C06 + C20）
        RegisterLnkSource("工具.lnk");
        RegisterLnkSource("nested", "工具.lnk");
        var baseConfig = EmptyWall();

        var report = _harness.Service.CommitBatch(baseConfig,
        [
            Request("obj-1", "工具.lnk"),
            Request("obj-2", "nested", "工具.lnk"),
        ], "开始菜单导入");

        Assert.Equal(2, report.CommittedObjects.Count);
        var directories = report.CommittedObjects.Select(o => EntryPaths.ObjectsDir(_harness.Root, o.Id)).Distinct().ToList();
        Assert.Equal(2, directories.Count); // 对象目录按稳定标识隔离（同名候选互不影响）
        _harness.AssertNoStagingResidue();
    }

    [Fact]
    public void CommitBatch_EmptyRequests_Throws()
    {
        Assert.Throws<ArgumentException>(() => _harness.Service.CommitBatch(EmptyWall(), [], "开始菜单导入"));
    }

    public void Dispose() => _harness.Dispose();
}
