using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Entries;

/// <summary>
/// T-ADAPT（M8 设计 §6.6）：CommitAdaptation——remove 计划 + finalConfig 校验、
/// 保留对象入口零触碰（防御性校验）、回滚后正式区 == 操作前（F-A1 / INV-I2）、
/// 撤销材料一次整体回滚 N 个入口、纯布局收敛退化路径（零入口操作）。
/// </summary>
public sealed class CommitAdaptationTests : IDisposable
{
    private readonly EntryCommitHarness _harness = new();

    /// <summary>装配：两个带入口磁贴（可缩、可移除）+ 一个空目标磁贴。</summary>
    private TileWallConfig SeedThree()
    {
        _harness.Links.Register(
            _harness.EntryPath("obj-a", "应用A.lnk"), "C:\\a.exe", "--x", "C:\\");
        _harness.Links.Register(
            _harness.EntryPath("obj-b", "应用B.lnk"), "C:\\b.exe", "", "");
        var config = Layouts.Config(_harness.Wall,
        [
            Layouts.Tile("obj-a", new GridRect(0, 0, 2, 1), new EntryReference { RelativePath = "Objects/obj-a/应用A.lnk" }),
            Layouts.Tile("obj-b", new GridRect(0, 1, 1, 1), new EntryReference { RelativePath = "Objects/obj-b/应用B.lnk" }),
            Layouts.Tile("obj-c", new GridRect(0, 2, 1, 1)), // 空目标：纯对象仅从 finalConfig 消失
        ]);
        _harness.Store.Save(config);
        return config;
    }

    private TileWallConfig ShrunkFinal(TileWallConfig current, params string[] removeIds) => current with
    {
        // 剩余 obj-a 缩放为 1×1（锚定原点），墙收敛到 1 栏 × 1 行（MinShrink 语义）
        Wall = new WallState(1, 1),
        Objects = current.Objects
            .Where(o => !removeIds.Contains(o.Id))
            .Select(o => o with { Bounds = new GridRect(o.Bounds.Column, o.Bounds.Row, 1, 1) })
            .ToList(),
    };

    [Fact]
    public void CommitAdaptation_RemovesEntriesAndShrinksWall_SingleTransaction()
    {
        var current = SeedThree();
        var finalConfig = ShrunkFinal(current, "obj-b", "obj-c");

        var report = _harness.Service.CommitAdaptation(current, ["obj-b", "obj-c"], finalConfig, "调整布局以适配显示");

        // 配置一次生效：剩余 1 对象、墙 1×1
        var saved = _harness.LoadConfig();
        Assert.Equal(1, saved.Objects.Count);
        Assert.Equal(1, saved.Wall.Columns);
        Assert.Equal(1, saved.Wall.Rows);

        // 被移除入口离开正式区（remove 生效）；保留对象入口零触碰
        Assert.False(_harness.Files.Exists(_harness.EntryPath("obj-b", "应用B.lnk")));
        Assert.True(_harness.Files.Exists(_harness.EntryPath("obj-a", "应用A.lnk")));

        // 撤销材料覆盖全部移除 Id：一次 RestoreMaterial 整体回滚 N 个入口
        Assert.Equal(["obj-b", "obj-c"], report.UndoMaterial!.ObjectIds);
        Assert.True(_harness.Service.RestoreMaterial(report.UndoMaterial));
        Assert.True(_harness.Files.Exists(_harness.EntryPath("obj-b", "应用B.lnk"))); // 从 removed/ 还原
        _harness.AssertNoStagingResidue();
    }

    [Fact]
    public void CommitAdaptation_PureLayoutConvergence_DegeneratesToSaveOnly()
    {
        var current = SeedThree();
        // 只移除空目标对象 + 缩墙：零入口文件操作 → 退化路径（Save only，撤销材料 null → 布局级单槽）
        var finalConfig = current with
        {
            Wall = new WallState(1, 2),
            Objects = current.Objects
                .Where(o => o.Id != "obj-c")
                .Select(o => o with { Bounds = new GridRect(0, o.Bounds.Row, 1, 1) })
                .ToList(),
        };
        var before = _harness.Files.SnapshotFiles();

        var report = _harness.Service.CommitAdaptation(current, ["obj-c"], finalConfig, "调整布局以适配显示");

        Assert.Null(report.UndoMaterial); // 无入口移除 → 无入口撤销材料
        var after = _harness.Files.SnapshotFiles();
        Assert.Equal(before.Keys.Count(k => k.StartsWith(Path.Combine(_harness.Root, "Objects"), StringComparison.Ordinal)),
            after.Keys.Count(k => k.StartsWith(Path.Combine(_harness.Root, "Objects"), StringComparison.Ordinal)));
        Assert.Equal(2, _harness.LoadConfig().Objects.Count);
        _harness.AssertNoStagingResidue();
    }

    [Fact]
    public void CommitAdaptation_SaveFails_RollsBackRemovals_F_A1()
    {
        var current = SeedThree();
        var finalConfig = ShrunkFinal(current, "obj-b", "obj-c");
        var snapshot = _harness.Files.SnapshotFiles();

        _harness.Files.InjectedFault = FaultPoint.BeforeMove; // ConfigStore.Save（唯一提交点）失败
        Assert.Throws<IOException>(() =>
            _harness.Service.CommitAdaptation(current, ["obj-b", "obj-c"], finalConfig, "调整布局以适配显示"));
        _harness.Files.InjectedFault = FaultPoint.None;

        _harness.AssertFormalAreaUnchanged(snapshot); // F-A1：入口移除全部回滚，原布局配置不动
        Assert.True(_harness.Files.Exists(_harness.EntryPath("obj-b", "应用B.lnk"))); // remove 已还原
        var restored = _harness.LoadConfig();
        Assert.Equal(3, restored.Objects.Count);
    }

    [Fact]
    public void CommitAdaptation_RemovedIdStillInFinal_Throws()
    {
        var current = SeedThree();
        var invalidFinal = current with { Wall = new WallState(2, 4) }; // obj-b/obj-c 仍在 finalConfig

        Assert.Throws<InvalidOperationException>(() =>
            _harness.Service.CommitAdaptation(current, ["obj-b"], invalidFinal, "调整布局以适配显示"));
    }

    [Fact]
    public void CommitAdaptation_RetainedEntryChanged_Throws()
    {
        var current = SeedThree();
        var invalidFinal = current with
        {
            Objects = current.Objects
                .Where(o => o.Id != "obj-b" && o.Id != "obj-c")
                .Select(o => o.Id == "obj-a"
                    ? o with
                    {
                        Bounds = new GridRect(0, 0, 1, 1),
                        Entry = new EntryReference { RelativePath = "Objects/obj-a/改名.lnk" }, // 保留对象入口被改写
                    }
                    : o)
                .ToList(),
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            _harness.Service.CommitAdaptation(current, ["obj-b", "obj-c"], invalidFinal, "调整布局以适配显示"));
        Assert.Contains("obj-a", exception.Message);
    }

    [Fact]
    public void CommitAdaptation_UnknownRemovedId_Throws()
    {
        var current = SeedThree();
        Assert.Throws<InvalidOperationException>(() =>
            _harness.Service.CommitAdaptation(current, ["obj-x"], current, "调整布局以适配显示"));
    }

    [Fact]
    public void CommitAdaptation_FinalConfigInvalid_ThrowsWithZeroWrites()
    {
        var current = SeedThree();
        // finalConfig 两对象重叠 → ConfigValidationException（零写入预检）
        var invalidFinal = current with
        {
            Objects = current.Objects
                .Where(o => o.Id != "obj-b")
                .Select(o => o.Id == "obj-c" ? o with { Bounds = new GridRect(0, 0, 1, 1) } : o) // 与 obj-a 相交
                .ToList(),
        };
        var snapshot = _harness.Files.SnapshotFiles();

        Assert.Throws<ConfigValidationException>(() =>
            _harness.Service.CommitAdaptation(current, ["obj-b"], invalidFinal, "调整布局以适配显示"));

        _harness.AssertFormalAreaUnchanged(snapshot);
    }

    public void Dispose() => _harness.Dispose();
}
