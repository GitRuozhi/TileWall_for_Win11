using TileWall.Core.Grid;
using TileWall.Core.Shell;
using Xunit;

namespace TileWall.Core.Tests.Shell;

/// <summary>假宿主：记录调用序、可拨动的会话/提示窗可见状态（T-ADAPT-EVAL 断言面，风格同 WallShowMachineTests）。</summary>
internal sealed class FakeAdaptationHost : IAdaptationHost
{
    public List<string> Calls { get; } = [];

    public bool HasActiveSession { get; set; }

    public bool IsPromptVisible { get; set; }

    public List<AdaptationSnapshot> PromptSnapshots { get; } = [];

    public void ShowPrompt(AdaptationSnapshot snapshot)
    {
        Calls.Add("ShowPrompt");
        IsPromptVisible = true;
        PromptSnapshots.Add(snapshot);
    }

    public void DismissPrompt()
    {
        Calls.Add("DismissPrompt");
        IsPromptVisible = false;
    }

    public void RefreshPrompt(AdaptationSnapshot snapshot)
    {
        Calls.Add($"RefreshPrompt:{snapshot.CapacityColumns}x{snapshot.CapacityRows}");
    }

    public void HideWallForAdaptation() => Calls.Add("HideWall");

    public void RestoreWallAfterAdaptation() => Calls.Add("RestoreWall");
}

/// <summary>
/// T-ADAPT-EVAL（M8 设计 §6.1/§6.3）：AdaptationEvaluator 0.5 DIP 边界（恰好放下/超出 0.1）、
/// MaxColumns/RowsFor 组合、MinShrink 收敛后 fit；AdaptationMachine 全迁移路径 + deferred 分支
/// （假宿主记录调用序）。多组分辨率/缩放组合以 (workW, workH) 参数矩阵覆盖。
/// </summary>
public sealed class AdaptationEvaluatorTests
{
    private static readonly GridMetrics Metrics = GridMetrics.Default;

    private static WallGrid Wall(int columns, int rows) => new(columns, rows);

    // 默认几何（P1 §1.1 占位参数）：栏宽 824 → WallWidth(2)=1720；WallHeight(9)=976。

    [Theory]
    [InlineData(1720.0, 976.0, true)]    // 恰好放下（容差内等号成立）
    [InlineData(1719.5, 976.0, true)]    // 宽向超工作区 0.5 DIP：容差内
    [InlineData(1719.4, 976.0, false)]   // 宽向超工作区 0.6 DIP：超容差
    [InlineData(1720.0, 976.5, true)]    // 高向超工作区 0.5 DIP：容差内
    [InlineData(1720.0, 975.4, false)]   // 高向超工作区 0.6 DIP：超容差
    [InlineData(3000.0, 2000.0, true)]
    [InlineData(800.0, 2000.0, false)]   // 宽不足一栏
    public void Fits_MatchesStartupBranchToleranceExactly(double workWidth, double workHeight, bool expected)
    {
        // 判定逐字对齐 MainWindow 既有启动分支：WallWidth ≤ work + 0.5 ∧ WallHeight ≤ workH + 0.5
        var fits = AdaptationEvaluator.Fits(Wall(2, 9), workWidth, workHeight, Metrics);
        Assert.Equal(expected, fits);
        var manual = Metrics.WallWidth(2) <= workWidth + 0.5 && Metrics.WallHeight(9) <= workHeight + 0.5;
        Assert.Equal(manual, fits);
    }

    [Fact]
    public void CapacityFor_MatchesMaxColumnsAndRows()
    {
        Assert.Equal((2, 9), AdaptationEvaluator.CapacityFor(1720.5, 976.5, Metrics));  // 容差边界内容纳
        Assert.Equal((0, 0), AdaptationEvaluator.CapacityFor(100, 100, Metrics));       // 整屏容不下一栏八格（A18）
        Assert.Equal((1, 9), AdaptationEvaluator.CapacityFor(900, 976.5, Metrics));     // 单栏 + 9 行
        Assert.Equal((0, 9), AdaptationEvaluator.CapacityFor(500, 976.5, Metrics));     // 栏 0 行 9（U-8 容量文案 0×N）
    }

    [Fact]
    public void Snapshot_CarriesRequirementAndCapacityNumbers()
    {
        var snapshot = AdaptationEvaluator.Snapshot(Wall(2, 9), 1024.0, 700.0, Metrics);
        Assert.False(snapshot.Fits);
        Assert.Equal(2, snapshot.RequiredColumns);
        Assert.Equal(9, snapshot.RequiredRows);
        Assert.Equal(144, snapshot.RequiredBaseCells); // 16 列 × 9 行
        Assert.Equal(1, snapshot.CapacityColumns);     // 1024 宽只容一栏
        Assert.Equal(6, snapshot.CapacityRows);        // 700 高约 6 行
        Assert.Contains("2 栏 × 9 行", snapshot.RequirementText);
        Assert.Contains("1 栏 × 6 行", snapshot.CapacityText);
    }

    [Fact]
    public void MinShrink_ConvergesToFit_ThenFits()
    {
        // 原 2×25 墙（超 1366×712 工作区）；剩余对象缩至 1 栏 6 行内 → MinShrink 收敛 → fit
        Assert.False(AdaptationEvaluator.Fits(Wall(2, 25), 1366.0, 712.0, Metrics));
        var shrunk = WallSizing.MinShrink(Wall(2, 25), [new GridRect(0, 0, 1, 6)]);
        Assert.Equal((1, 6), (shrunk.Columns, shrunk.Rows));
        Assert.True(AdaptationEvaluator.Fits(shrunk, 1366.0, 712.0, Metrics));
    }

    [Fact]
    public void ResolutionScaleMatrix_SmallWorkAreasDetectedAsNoFit()
    {
        // 多组分辨率/缩放组合（DIP 工作区 = 物理像素 ÷ 缩放）：降分辨率/升缩放 → NoFit
        var wall = Wall(2, 9);
        Assert.True(AdaptationEvaluator.Fits(wall, 1920.0, 1040.0, Metrics));  // 1920×1080 @100%（含任务栏）
        Assert.False(AdaptationEvaluator.Fits(wall, 1024.0, 720.0, Metrics));  // 1024×768 @100%
        Assert.False(AdaptationEvaluator.Fits(wall, 960.0, 540.0, Metrics));   // 1920×1080 @200%
        Assert.True(AdaptationEvaluator.Fits(wall, 2560.0, 1390.0, Metrics));  // 2560×1440 @100%
        Assert.False(AdaptationEvaluator.Fits(wall, 1280.0, 695.0, Metrics));  // 2560×1440 @200%
    }
}

/// <summary>T-ADAPT-EVAL 状态机部分：全迁移路径 + deferred 分支（假宿主记录调用序）。</summary>
public sealed class AdaptationMachineTests
{
    private static AdaptationSnapshot Snapshot(bool fits, int capacityColumns = 1, int capacityRows = 6) => new(
        Fits: fits,
        WallColumns: 2,
        WallRows: 9,
        RequiredColumns: 2,
        RequiredRows: 9,
        RequiredBaseCells: 144,
        CapacityColumns: capacityColumns,
        CapacityRows: capacityRows);

    private static AdaptationMachine New(out FakeAdaptationHost host) => New(AdaptationState.Fitted, out host);

    private static AdaptationMachine New(AdaptationState initial, out FakeAdaptationHost host)
    {
        host = new FakeAdaptationHost();
        return new AdaptationMachine(initial, host);
    }

    [Fact]
    public void Fitted_EvaluateFit_IsIdempotent_NoHostCalls()
    {
        var machine = New(out var host);
        machine.Evaluate(Snapshot(fits: true));
        Assert.Equal(AdaptationState.Fitted, machine.State);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Fitted_EvaluateNoFit_HidesWallThenShowsPrompt()
    {
        var machine = New(out var host);
        machine.Evaluate(Snapshot(fits: false));
        Assert.Equal(AdaptationState.AdaptationNeeded, machine.State);
        Assert.False(machine.DeferredPromptPending);
        Assert.Equal(["HideWall", "ShowPrompt"], host.Calls); // 先收墙（经状态机）再弹提示
    }

    [Fact]
    public void Fitted_NoFitWithActiveSession_DefersPrompt()
    {
        var machine = New(out var host);
        host.HasActiveSession = true;
        machine.Evaluate(Snapshot(fits: false));
        Assert.Equal(AdaptationState.AdaptationNeeded, machine.State);
        Assert.True(machine.DeferredPromptPending);
        Assert.Equal(["HideWall"], host.Calls); // 有活动会话 → 只收墙，提示延后
        Assert.False(host.IsPromptVisible);
    }

    [Fact]
    public void Deferred_SessionStillActive_KeepsWaiting()
    {
        var machine = New(out var host);
        host.HasActiveSession = true;
        machine.Evaluate(Snapshot(fits: false));
        host.Calls.Clear();
        host.HasActiveSession = true;
        machine.Evaluate(Snapshot(fits: false, capacityColumns: 1, capacityRows: 5)); // 会话仍在
        Assert.True(machine.DeferredPromptPending);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Deferred_SessionClosesOnNextEvaluate_PromptPops()
    {
        var machine = New(out var host);
        host.HasActiveSession = true;
        machine.Evaluate(Snapshot(fits: false));
        host.HasActiveSession = false;
        machine.Evaluate(Snapshot(fits: false, capacityRows: 5)); // SessionClosed → 路由器重评估
        Assert.False(machine.DeferredPromptPending);
        Assert.Equal(["HideWall", "ShowPrompt"], host.Calls);
    }

    [Fact]
    public void AdaptationNeeded_NoFitWithPromptOpen_RefreshesNumbers()
    {
        var machine = New(out var host);
        machine.Evaluate(Snapshot(fits: false));
        host.Calls.Clear();
        machine.Evaluate(Snapshot(fits: false, capacityRows: 4));
        Assert.Equal(AdaptationState.AdaptationNeeded, machine.State);
        Assert.Equal(["RefreshPrompt:1x4"], host.Calls); // 窗开着才刷（F-A2 数字更新）
    }

    [Fact]
    public void AdaptationNeeded_NoFitWithoutPrompt_NoActions()
    {
        var machine = New(out var host);
        machine.Evaluate(Snapshot(fits: false));
        host.IsPromptVisible = false; // 稍后处理之后：窗已关
        host.Calls.Clear();
        machine.Evaluate(Snapshot(fits: false, capacityRows: 3));
        Assert.Equal(AdaptationState.AdaptationNeeded, machine.State);
        Assert.Empty(host.Calls); // 窗未开 → 不刷数字、不重弹、不重复收墙（墙保持隐藏）
    }

    [Fact]
    public void AdaptationNeeded_PromptDismissed_StaysHiddenAndClearsDeferred()
    {
        var machine = New(out var host);
        host.HasActiveSession = true;
        machine.Evaluate(Snapshot(fits: false));
        host.Calls.Clear();
        machine.PromptDismissed(); // 「稍后处理」
        Assert.False(machine.DeferredPromptPending);
        Assert.Equal(AdaptationState.AdaptationNeeded, machine.State); // 墙保持隐藏
        Assert.Empty(host.Calls);
        Assert.False(machine.LastNoFitSnapshot!.Fits); // Show 类输入改弹提示窗的数据仍在
    }

    [Fact]
    public void AdaptationNeeded_EvaluateFit_RestoresWallAndExits()
    {
        var machine = New(out var host);
        machine.Evaluate(Snapshot(fits: false));
        host.Calls.Clear();
        host.IsPromptVisible = true;
        machine.Evaluate(Snapshot(fits: true));
        Assert.Equal(AdaptationState.Fitted, machine.State);
        Assert.Null(machine.LastNoFitSnapshot);
        Assert.Equal(["DismissPrompt", "RestoreWall"], host.Calls); // §6.5：关提示窗 → 重渲染 + Request(Show)
        Assert.False(host.IsPromptVisible);
    }

    [Fact]
    public void AdaptationNeeded_AdjustmentCommitted_RestoresAndExits()
    {
        var machine = New(out var host);
        machine.Evaluate(Snapshot(fits: false));
        host.Calls.Clear();
        machine.AdjustmentCommitted(); // 调整窗确认路径（CommitAdaptation 已由调用方完成）
        Assert.Equal(AdaptationState.Fitted, machine.State);
        Assert.Equal(["DismissPrompt", "RestoreWall"], host.Calls);
    }

    [Fact]
    public void Fitted_PromptDismissedOrAdjustmentCommitted_IsIdempotent()
    {
        var machine = New(out var host);
        machine.PromptDismissed();
        machine.AdjustmentCommitted();
        Assert.Equal(AdaptationState.Fitted, machine.State);
        Assert.Empty(host.Calls); // 幂等防御：无宿主动作
    }
}
