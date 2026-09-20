using TileWall.Core.Animation;
using TileWall.Core.Carousel;
using TileWall.Core.Configuration;
using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Carousel;

/// <summary>假图片准备器：按 ImageId 注入成功/失败（T-DRIVE 的 T1 接缝）。</summary>
internal sealed class FakeImageGate : ICarouselImageGate
{
    public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);
    public List<(string GroupId, string ImageId)> Calls { get; } = [];

    public Task<bool> PrepareAsync(string groupId, string imageId)
    {
        Calls.Add((groupId, imageId));
        return Task.FromResult(!Failing.Contains(imageId));
    }
}

/// <summary>假宿主：记录持久化与上墙事件（T2&lt;T3&lt;T4 顺序断言点）。</summary>
internal sealed class FakeDriveHost : ICarouselDriveHost
{
    public FakeDriveHost(TileWallConfig config) => CurrentConfig = config;

    public TileWallConfig CurrentConfig { get; private set; }

    public List<TileWallConfig> Persisted { get; } = [];

    public List<string> Shown { get; } = [];

    public List<string> Flips { get; } = [];

    public bool PersistResult { get; set; } = true;

    public bool PersistWithoutUndo(TileWallConfig config)
    {
        if (!PersistResult)
        {
            return false;
        }

        Persisted.Add(config);
        CurrentConfig = config;
        return true;
    }

    public void ShowImage(string groupId, string imageId) => Shown.Add($"{groupId}:{imageId}");

    public void StartFlip(string groupId, string imageId) => Flips.Add($"{groupId}:{imageId}");
}

/// <summary>
/// T-DRIVE（M6 设计 §11.1；V-05 接线面；B14/B15/B17/B18）：
/// B14 全时间线重演（§8.3）；隐藏停表/显示恢复（Evaluate(true) 一次）；模态/拖动期 tick 零 Evaluate、
/// 解除后恰一次显式；翻转 600 ms 窗口内到期顺延；成功序 T2(commit)&lt;T3(persist)&lt;T4(flip)；
/// 失败链 3 次 → Passive → 旧图基准不动 → 明确检查复位。
/// </summary>
public sealed class CarouselCoordinatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 15, 16, 12, TimeSpan.Zero);
    private static readonly string[] Candidates = [@"C:\imgs\a.png", @"C:\imgs\b.png", @"C:\imgs\c.png", @"C:\imgs\d.png"];

    private readonly InMemoryFileStore _files = new();
    private readonly FakeClock _clock = new(T0);

    public CarouselCoordinatorTests()
    {
        foreach (var path in Candidates)
        {
            _files.WriteAllBytes(path, [1]);
        }
    }

    private GroupObject Group(string id, IReadOnlyList<string>? paths = null) => new()
    {
        Id = id,
        Bounds = new GridRect(8, 0, 4, 4),
        Partitions =
        [
            new GridRect(0, 0, 1, 1),
            new GridRect(1, 0, 1, 1),
            new GridRect(0, 1, 1, 1),
            new GridRect(1, 1, 1, 1),
        ],
        Images = new GroupImages { Kind = GroupImageSourceKind.Multiple, ImagePaths = paths ?? Candidates },
    };

    private static (CarouselCoordinator Coordinator, FakeImageGate Gate, FakeDriveHost Host) Build(
        FakeClock clock, InMemoryFileStore files, params GroupObject[] groups)
    {
        var gate = new FakeImageGate();
        var host = new FakeDriveHost(new TileWallConfig { Wall = new WallState(2, 9), Objects = groups });
        var coordinator = new CarouselCoordinator(clock, new CarouselScheduler(clock, new Random(42)), files, gate, host)
        {
            LayoutReady = true,
        };
        return (coordinator, gate, host);
    }

    // ————————————————————————————— 启动基线（§8.2） —————————————————————————————

    [Fact]
    public async Task Baseline_NoSavedState_ShowsFirstCandidate_AndPersistsBenchmark()
    {
        var (coordinator, _, host) = Build(_clock, _files, Group("g1"));
        await coordinator.OnConfigReadyAsync();

        Assert.Equal(["g1:" + Candidates[0]], host.Shown); // 首图直显（确定性首候选）
        var state = Assert.IsType<GroupObject>(host.CurrentConfig.Objects.Single()).Carousel;
        Assert.Equal(Candidates[0], state!.CurrentImageId);
        Assert.Equal(T0, state.LastSwitchUtc);             // 基准 = 成功时刻
        Assert.NotEmpty(host.Persisted);

        // 状态先于显示：persist 日志先于 show 日志
        var log = coordinator.EventLog.ToList();
        Assert.True(
            log.IndexOf(log.First(l => l.StartsWith("persist:", StringComparison.Ordinal)))
            < log.IndexOf(log.First(l => l.StartsWith("show:", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task Baseline_ExistingState_RestoresDisplay_KeepsBenchmark()
    {
        var group = Group("g1") with { Carousel = new CarouselState { CurrentImageId = Candidates[1], LastSwitchUtc = T0 } };
        var (coordinator, _, host) = Build(_clock, _files, group);
        await coordinator.OnConfigReadyAsync();

        Assert.Equal(["g1:" + Candidates[1]], host.Shown);
        Assert.Empty(host.Persisted); // 恢复显示不改基准（引擎既有裁决覆盖后续切换）
        var state = Assert.IsType<GroupObject>(host.CurrentConfig.Objects.Single()).Carousel;
        Assert.Equal(T0, state!.LastSwitchUtc);
    }

    // ————————————————————————————— B14 全时间线（§8.3） —————————————————————————————

    [Fact]
    public async Task B14_FullTimeline_ReplaysExactlyOneSwitchOnReopen()
    {
        var (coordinator, _, host) = Build(_clock, _files, Group("g1"));
        await coordinator.OnConfigReadyAsync(); // 15:16:12 基准（日志 = persist+show 共 2 条）
        Assert.Equal(2, coordinator.EventLog.Count);

        _clock.Advance(TimeSpan.FromSeconds(61)); // 到期
        await coordinator.TickAsync();
        Assert.Equal(6, coordinator.EventLog.Count); // +prepare+commit+persist+flip 恰一组
        var firstSwitchImage = host.Flips.Single()["g1:".Length..]; // ImageId 含盘符冒号，不能按冒号切

        // 成功序：T2 commit < T3 persist < T4 flip（状态先于动画，§7.2；按本次切换候选定位）
        var log = coordinator.EventLog.ToList();
        var commitIndex = log.IndexOf($"commit:g1:{firstSwitchImage}");
        var persistIndex = log.IndexOf($"persist:g1:{firstSwitchImage}");
        var flipIndex = log.IndexOf($"flip:g1:{firstSwitchImage}");
        Assert.True(commitIndex >= 0 && persistIndex > commitIndex);
        Assert.True(flipIndex > persistIndex);
        Assert.NotEqual(Candidates[0], firstSwitchImage); // 随机不紧接重复（排除当前）

        // 翻转窗口内再次 tick：顺延（§7.6）
        await coordinator.TickAsync();
        Assert.Single(host.Flips);

        // 15:16:15 收起（停表不检查）；隐藏期零检查
        _clock.Set(T0.AddSeconds(3));
        coordinator.OnWallHidden();
        var evaluatesBefore = coordinator.EvaluateCount;
        await coordinator.TickAsync();
        Assert.Equal(evaluatesBefore, coordinator.EvaluateCount);

        // 15:25:09 重开 → 逾期只切一次
        _clock.Set(T0.AddMinutes(8).AddSeconds(57));
        await coordinator.OnWallShownAsync();
        Assert.Equal(2, host.Flips.Count);
        Assert.True(coordinator.FlipActive("g1"));

        await coordinator.TickAsync(); // 同刻 tick：不补播历史轮次
        Assert.Equal(2, host.Flips.Count);

        _clock.Set(T0.AddMinutes(10).AddSeconds(2)); // T0+602：翻转窗早已过、新基准（T0+537）已到期
        await coordinator.TickAsync();
        Assert.Equal(3, host.Flips.Count); // 下一轮正常到期
    }

    // ————————————————————————————— 暂缓与恢复（§7.5） —————————————————————————————

    [Fact]
    public async Task ModalAndGestureBlockTicks_WithZeroEvaluates_ThenExplicitCheckSwitchesOnce()
    {
        var (coordinator, _, host) = Build(_clock, _files, Group("g1"));
        await coordinator.OnConfigReadyAsync();
        var due = T0.AddSeconds(61);

        _clock.Set(due);
        coordinator.ModalActive = true;
        var evaluates = coordinator.EvaluateCount;
        await coordinator.TickAsync();
        await coordinator.ExplicitCheckAsync(); // 模态期内无检查出口
        Assert.Equal(evaluates, coordinator.EvaluateCount); // 零 Evaluate
        Assert.Empty(host.Flips);

        coordinator.ModalActive = false;
        await coordinator.ExplicitCheckAsync(); // 模态关闭 → 恰一次
        Assert.Single(host.Flips);
        Assert.Equal(evaluates + 1, coordinator.EvaluateCount);

        // 拖动同构（B17）
        _clock.Set(due.AddSeconds(61));
        coordinator.GestureActive = true;
        await coordinator.TickAsync();
        Assert.Single(host.Flips);
        coordinator.GestureActive = false;
        await coordinator.ExplicitCheckAsync();
        Assert.Equal(2, host.Flips.Count);
    }

    [Fact]
    public async Task FlipWindowDefersDue_SwitchResumesAfterDeadline()
    {
        var (coordinator, _, host) = Build(_clock, _files, Group("g1"));
        await coordinator.OnConfigReadyAsync();

        _clock.Set(T0.AddSeconds(61));
        coordinator.NoteFlipStarted("g1"); // 翻转窗口（§7.6 防御行）
        await coordinator.TickAsync();
        Assert.Empty(host.Flips);          // 窗口内顺延

        _clock.Advance(TimeSpan.FromMilliseconds(FlipTimeline.DurationMs) + TimeSpan.FromSeconds(61));
        await coordinator.TickAsync();
        Assert.Single(host.Flips);         // 窗口后正常切换
    }

    [Fact]
    public async Task Hidden_DisablesExplicitCheckToo_UntilShown()
    {
        var (coordinator, _, host) = Build(_clock, _files, Group("g1"));
        await coordinator.OnConfigReadyAsync();
        _clock.Set(T0.AddSeconds(61));

        coordinator.OnWallHidden();
        await coordinator.TickAsync();
        await coordinator.ExplicitCheckAsync();
        Assert.Empty(host.Flips);          // 隐藏=停表不检查（不触碰 Passive 位）

        await coordinator.OnWallShownAsync();
        Assert.Single(host.Flips);         // 显示恢复 → 显式检查一次
    }

    // ————————————————————————————— 失败链（§6.2/B18） —————————————————————————————

    [Fact]
    public async Task LoadFailureChain_ThreeAttemptsThenPassive_StateUntouched_ExplicitRecovers()
    {
        var gate = new FakeImageGate();
        foreach (var path in Candidates)
        {
            gate.Failing.Add(path);
        }

        var group = Group("g1") with { Carousel = new CarouselState { CurrentImageId = Candidates[0], LastSwitchUtc = T0 } };
        var host = new FakeDriveHost(new TileWallConfig { Wall = new WallState(2, 9), Objects = [group] });
        var coordinator = new CarouselCoordinator(_clock, new CarouselScheduler(_clock, new Random(42)), _files, gate, host)
        {
            LayoutReady = true,
        };

        await coordinator.OnConfigReadyAsync(); // 启动恢复显示也失败：无 show、无落盘
        Assert.Empty(host.Shown);
        Assert.Empty(host.Persisted);
        var restoreCalls = gate.Calls.Count;

        _clock.Advance(TimeSpan.FromSeconds(61));
        await coordinator.TickAsync();
        Assert.Equal(restoreCalls + 3, gate.Calls.Count); // 上限 3 次尝试（引擎内计数）
        Assert.Empty(host.Flips);                         // 无切换动画
        var state = Assert.IsType<GroupObject>(host.CurrentConfig.Objects.Single()).Carousel;
        Assert.Equal(Candidates[0], state!.CurrentImageId); // 旧图保留
        Assert.Equal(T0, state.LastSwitchUtc);              // 基准不动
        Assert.Empty(host.Persisted);                       // 零落盘

        await coordinator.TickAsync(); // Passive 到期路径不忙循环（引擎 None，无新尝试）
        Assert.Equal(restoreCalls + 3, gate.Calls.Count);

        gate.Failing.Clear(); // 文件恢复
        await coordinator.OnWallShownAsync(); // 明确检查 = Passive 唯一出口
        Assert.Single(host.Flips);
        var newState = Assert.IsType<GroupObject>(host.CurrentConfig.Objects.Single()).Carousel;
        Assert.NotEqual(Candidates[0], newState!.CurrentImageId);
        Assert.Equal(_clock.UtcNow, newState.LastSwitchUtc);
        Assert.Single(host.Persisted);
    }

    [Fact]
    public async Task MultiGroup_IndependentBenchmarksAndFlips()
    {
        var g1 = Group("g1", [Candidates[0], Candidates[1]]);
        var g2 = Group("g2", [Candidates[2], Candidates[3]]);
        var (coordinator, _, host) = Build(_clock, _files, g1, g2);
        await coordinator.OnConfigReadyAsync();

        Assert.Equal(2, host.Shown.Count); // 两组各自建基准
        _clock.Set(T0.AddSeconds(61));
        await coordinator.TickAsync();
        Assert.Equal(2, host.Flips.Count); // 同一 tick 各切一次（独立基准/独立动画，A5）
        Assert.Equal(["g1:", "g2:"], [.. host.Flips.Select(f => f[..3])]);
    }

    [Fact]
    public async Task SingleCandidate_NeverSwitches_EngineNone()
    {
        var (coordinator, _, host) = Build(_clock, _files, Group("g1", [Candidates[0]]));
        await coordinator.OnConfigReadyAsync(); // 单张：基线显示
        Assert.Single(host.Shown);

        _clock.Set(T0.AddSeconds(120));
        await coordinator.TickAsync();
        Assert.Single(host.Shown);              // B18：单张不轮播
        Assert.Empty(host.Flips);
    }

    [Fact]
    public async Task PersistFailure_EngineBenchmarkStillAdvances_NoDoubleSwitchLoop()
    {
        var (coordinator, _, host) = Build(_clock, _files, Group("g1"));
        host.PersistResult = false; // 磁盘故障
        await coordinator.OnConfigReadyAsync(); // 基线显示成功、落盘失败
        Assert.Single(host.Shown);

        _clock.Set(T0.AddSeconds(61));
        await coordinator.TickAsync();
        Assert.Single(host.Flips);              // 切换照常（T2 基准已提交，T3 失败不回滚内存态）
        await coordinator.TickAsync();
        Assert.Single(host.Flips);              // 无重复切换循环（基准在 T2 前移）
    }
}
