using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Shell;
using TileWall.Core.Tests.Entries;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Shell;

/// <summary>
/// M9 测试 T3（设计 §6.1）：批量容量计划——N×1×1 的统一容量检查（与 M8 导入同款 CanPlaceBatch）
/// 不足 → 整批拒绝、零写入（INV-I1 同款）；恰好容纳 → 一次 CommitBatch 提交（整批一个 commitId）。
/// 复用 M8 批量提交测试基建（EntryCommitHarness / InMemoryFileStore / FakeLinkFileService）。
/// </summary>
public sealed class ExplorerAddBatchPlanTests
{
    /// <summary>N×1×1 的 Explorer 添加统一容量检查（与 MainWindow.ProcessExplorerAdd 同一函数）。</summary>
    private static bool CanPlaceAll(WallGrid wall, IReadOnlyList<GridRect> existing, int count) =>
        GridPlacement.CanPlaceBatch(wall, existing, Enumerable.Repeat(new GridSize(1, 1), count).ToList());

    [Fact]
    public void CapacityCheck_ShortByOne_RejectsWholeBatch()
    {
        // 1 栏 × 1 行 = 8 基础格：占满 8 格后再加 1 → 整批拒绝
        var wall = new WallGrid(1, 1);
        var full = Enumerable.Range(0, 8).Select(i => new GridRect(i, 0, 1, 1)).ToList();

        Assert.True(CanPlaceAll(wall, full, 0));
        Assert.False(CanPlaceAll(wall, full, 1));
        Assert.False(CanPlaceAll(wall, [], 9)); // 空墙放不下 9 格
    }

    [Fact]
    public void CommitBatch_CapacityShortByOne_ThrowsBeforeAnyWrite()
    {
        using var harness = new EntryCommitHarness();
        var wall = new WallGrid(1, 1);
        var existing = Enumerable.Range(0, 8)
            .Select(i => Layouts.Tile($"seed-{i}", new GridRect(i, 0, 1, 1)))
            .ToArray();
        var baseConfig = Layouts.Config(wall, existing);
        harness.Store.Save(baseConfig);
        harness.Links.Register(Path.Combine(harness.Root, "sources", "九号.lnk"), "C:\\tools\\app.exe", "", "");
        var request = new EntryCommitRequest(
            "obj-new",
            "添加到 TileWall",
            new TileDraft
            {
                Size = new GridSize(1, 1),
                Entry = new EntryDraft.CopyFromFile(Path.Combine(harness.Root, "sources", "九号.lnk")),
            });

        var before = harness.Files.SnapshotFiles();

        // 应用侧容量闸门（CanPlaceBatch）虽先行拦截，这里验证协议兜底：计划段 SIZE_NO_FIT → 异常零写入
        var probe = Assert.ThrowsAny<Exception>(() => harness.Service.CommitBatch(baseConfig, [request], "添加到 TileWall"));
        Assert.True(probe is DraftValidationException or ConfigValidationException, $"意外异常：{probe.GetType().Name}");

        harness.AssertFormalAreaUnchanged(before); // INV-I1：正式区 == 操作前、无暂存残留
        Assert.Equal(8, harness.LoadConfig().Objects.Count); // 配置未动
    }

    [Fact]
    public void CommitBatch_ExactlyFits_OneCommitForWholeBatch()
    {
        using var harness = new EntryCommitHarness();
        var wall = new WallGrid(1, 1); // 8 格
        var sources = Enumerable.Range(0, 8)
            .Select(i =>
            {
                var name = $"候选{i}.lnk";
                var path = Path.Combine(harness.Root, "sources", name);
                harness.Links.Register(path, $"C:\\tools\\app{i}.exe", "", "");
                return (Path: path, Name: name);
            })
            .ToArray();
        var requests = sources.Select((s, i) => new EntryCommitRequest(
            $"obj-{i}",
            "添加到 TileWall",
            new TileDraft { Size = new GridSize(1, 1), Entry = new EntryDraft.CopyFromFile(s.Path) })).ToList();

        Assert.True(CanPlaceAll(wall, [], requests.Count)); // 恰好容纳 → 计划成立

        var report = harness.Service.CommitBatch(Layouts.Config(wall, []), requests, "添加到 TileWall");

        Assert.Equal(8, report.CommittedObjects.Count);
        Assert.Equal(8, report.UndoMaterial.ObjectIds.Count); // 整批一个 commitId 的撤销材料
        Assert.Equal(8, harness.LoadConfig().Objects.Count); // 一次提交全量生效
        foreach (var o in report.CommittedObjects)
        {
            Assert.True(harness.Files.Exists(EntryPaths.Full(harness.Root, o.Entry!.RelativePath)));
        }
    }

    [Fact]
    public void Build_CommandLineOverLimit_TruncatesByWholePaths()
    {
        // ~2000 路径 × ~30 字符 > 32000 上限 → 整条截断、首条恒保留
        var paths = Enumerable.Range(0, 2000).Select(i => $"C:\\目录{i:D4}\\很长的目标名称文件{i:D4}.txt").ToList();

        var (arguments, selected, truncated) = ExplorerAddCommandLine.Build(paths);

        Assert.True(truncated);
        Assert.True(selected < paths.Count);
        Assert.Equal(ExplorerAddCommandLine.AddArgument, arguments.Split(' ')[0]);
        Assert.True(arguments.Length <= ExplorerAddCommandLine.MaxArgumentsLength, $"参数串长度 {arguments.Length} 超上限");
        Assert.DoesNotContain(ExplorerAddCommandLine.TruncatedMarker, arguments); // 标记由调用方追加，不混入参数串
    }

    [Fact]
    public void Build_SmallBatch_NoTruncation()
    {
        var (arguments, selected, truncated) =
            ExplorerAddCommandLine.Build([@"C:\带 空格 目录\a.txt", @"C:\b.exe"]);

        Assert.False(truncated);
        Assert.Equal(2, selected);
        Assert.Equal("--add \"C:\\带 空格 目录\\a.txt\" \"C:\\b.exe\"", arguments);
    }
}
