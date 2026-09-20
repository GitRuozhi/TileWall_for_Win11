using TileWall.Core.Animation;
using TileWall.Core.Configuration;

namespace TileWall.Core.Carousel;

/// <summary>
/// 图片准备接缝（M6 设计 §2.1 的 ImageLoader 在 Core 侧的最小投影，T1）：
/// true = 该候选已就绪可上墙；false = 加载失败（走引擎 ReportLoadFailure 链）。
/// 生产实现（Shell）做异步解码 + 模糊后景生成；测试注入假实现。
/// </summary>
public interface ICarouselImageGate
{
    Task<bool> PrepareAsync(string groupId, string imageId);
}

/// <summary>
/// 持久化与上墙接缝（M6 设计 §2.2「凡持久化一律走既有通道」的注入面）：
/// PersistWithoutUndo = LayoutCommitService.SaveWithoutUndo（T3，不触碰撤销槽）；
/// ShowImage = 首图/启动恢复直显（无翻转）；StartFlip = T4（同步翻转启动）。
/// </summary>
public interface ICarouselDriveHost
{
    TileWallConfig CurrentConfig { get; }

    bool PersistWithoutUndo(TileWallConfig config);

    void ShowImage(string groupId, string imageId);

    void StartFlip(string groupId, string imageId);
}

/// <summary>
/// 轮播接线中枢的纯决策核（M6 设计 §7.5/§8；WallCarouselDriver 的 Core 侧，T-DRIVE 主对象）。
/// 单个哑闹钟（Shell 1 s tick）→ <see cref="TickAsync"/> → 可见组逐个 Evaluate（消费 M5 引擎零改动）；
/// 墙可见性/模态/拖动/翻转中全部收敛为 CanSwitchNow 输入（§7.5）。
/// 隐藏 = Enabled=false 停表不检查、不触碰 Passive 位（§8.1；Passive 的「失败出口」语义不被污染）。
/// 切换全序 T0→T1→T2(CommitSwitch)→T3(Persist)→T4(StartFlip)：状态先于动画（§7.2/§7.6）。
/// </summary>
public sealed class CarouselCoordinator
{
    private readonly IClock _clock;
    private readonly CarouselScheduler _scheduler;
    private readonly IFileStore _files;
    private readonly ICarouselImageGate _loader;
    private readonly ICarouselDriveHost _host;
    private readonly Dictionary<string, CarouselRuntime> _runtimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CarouselState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _flipDeadlines = new(StringComparer.Ordinal);
    private readonly List<string> _log = [];

    public CarouselCoordinator(
        IClock clock,
        CarouselScheduler scheduler,
        IFileStore files,
        ICarouselImageGate loader,
        ICarouselDriveHost host)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(host);
        _clock = clock;
        _scheduler = scheduler;
        _files = files;
        _loader = loader;
        _host = host;
    }

    /// <summary>墙可见性：隐藏 = 停表不检查（tick 与显式检查都短路），不触碰任何运行态（§8.1）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>布局就绪（Bootstrap 完成前零动作）。</summary>
    public bool LayoutReady { get; set; }

    /// <summary>模态会话进行中（暂缓切图，§7.5）。</summary>
    public bool ModalActive { get; set; }

    /// <summary>指针手势进行中（拖动判定，§7.5）。</summary>
    public bool GestureActive { get; set; }

    /// <summary>诊断/测试计数：Evaluate 实际调用次数（「模态期 tick 零 Evaluate」断言点，§8.1 消费规则）。</summary>
    public int EvaluateCount { get; private set; }

    /// <summary>事件序日志（T2&lt;T3&lt;T4 顺序断言点）：prepare/commit/persist/flip/show 依发生序追加。</summary>
    public IReadOnlyList<string> EventLog => _log;

    /// <summary>该组翻转窗口是否进行中（启动后 Duration 内；§7.6「再次到期顺延」）。</summary>
    public bool FlipActive(string groupId) =>
        _flipDeadlines.TryGetValue(groupId, out var deadline) && _clock.UtcNow < deadline;

    private bool CanSwitchNow => Enabled && LayoutReady && !ModalActive && !GestureActive;

    // ————————————————————————————— 启动基线（§8.2） —————————————————————————————

    /// <summary>
    /// 启动装配：逐组读 Carousel——无 CurrentImageId → 首候选直显 + CommitSwitch 建基准（§11.2 表首行）；
    /// 已有 CurrentImageId → 直显（基准保留，引擎既有裁决覆盖后续切换）。首图失败 → 依序试后续候选
    /// （§6.2 首图失败链的启动面简化：占位=主题兜底色，全部失败则维持占位）。
    /// </summary>
    public async Task OnConfigReadyAsync()
    {
        var config = _host.CurrentConfig;
        foreach (var group in config.Objects.OfType<GroupObject>())
        {
            if (group.Images.Kind == GroupImageSourceKind.None)
            {
                continue;
            }

            var candidates = ImageCatalog.EnumerateCandidates(group.Images, _files);
            var state = group.Carousel ?? new CarouselState();
            _states[group.Id] = state;
            if (state.CurrentImageId is not null)
            {
                if (await _loader.PrepareAsync(group.Id, state.CurrentImageId))
                {
                    _host.ShowImage(group.Id, state.CurrentImageId); // 恢复显示，不动基准
                }

                continue;
            }

            foreach (var candidate in candidates)
            {
                if (!await _loader.PrepareAsync(group.Id, candidate))
                {
                    continue;
                }

                CommitBaseline(group, state, candidate);
                break;
            }
        }
    }

    private void CommitBaseline(GroupObject group, CarouselState state, string candidate)
    {
        var now = _clock.UtcNow;
        var newState = _scheduler.CommitSwitch(state, RuntimeFor(group.Id), candidate, now); // 首图 CommitSwitch 建基准
        _states[group.Id] = newState;
        Persist(group.Id, newState);
        _host.ShowImage(group.Id, candidate); // 首图直显（无翻转）
        _log.Add($"show:{group.Id}:{candidate}");
    }

    // ————————————————————————————— 哑闹钟与显式检查（§7.5/§8.1） —————————————————————————————

    /// <summary>1 s 哑闹钟 tick（explicitCheck=false）：CanSwitchNow=false 时本 tick 什么都不发生。</summary>
    public async Task TickAsync()
    {
        if (!CanSwitchNow)
        {
            return;
        }

        await RunDuePassAsync(explicitCheck: false);
    }

    /// <summary>
    /// 明确检查事件（模态关闭 / 拖动结束 / 墙重新显示，explicitCheck=true）：
    /// 到期 → Switch 一次；未到期 → WaitRemaining（tick 轮询自然覆盖，不据此另设 timer）。
    /// Passive 的唯一出口（引擎步骤 2/3）。
    /// </summary>
    public async Task ExplicitCheckAsync()
    {
        if (!CanSwitchNow)
        {
            return;
        }

        await RunDuePassAsync(explicitCheck: true);
    }

    public void OnWallHidden() => Enabled = false; // 停表不检查；运行态（含 Passive 位）零触碰（§8.1）

    public async Task OnWallShownAsync()
    {
        Enabled = true;
        await ExplicitCheckAsync(); // 重开已到期只切一次（B14）/未到期等剩余（B15）
    }

    /// <summary>T4 附带：登记翻转窗口（600 ms 内再次到期顺延，§7.6 防御行）。</summary>
    public void NoteFlipStarted(string groupId) =>
        _flipDeadlines[groupId] = _clock.UtcNow + TimeSpan.FromMilliseconds(FlipTimeline.DurationMs);

    private async Task RunDuePassAsync(bool explicitCheck)
    {
        var config = _host.CurrentConfig;
        foreach (var group in config.Objects.OfType<GroupObject>())
        {
            if (group.Images.Kind == GroupImageSourceKind.None || FlipActive(group.Id))
            {
                continue; // 翻转窗口内顺延到下一 tick（§7.6）
            }

            var candidates = ImageCatalog.EnumerateCandidates(group.Images, _files);
            var state = EffectiveState(group);
            var runtime = RuntimeFor(group.Id);
            EvaluateCount++;
            var decision = _scheduler.Evaluate(candidates, state, runtime, explicitCheck);
            if (decision.Action != CarouselAction.Switch || decision.CandidateImageId is null)
            {
                continue; // None / WaitRemaining：轮询模式下仅诊断语义（§8.1）
            }

            await SwitchAsync(group, state, candidates, decision.CandidateImageId);
        }
    }

    private async Task SwitchAsync(GroupObject group, CarouselState state, IReadOnlyList<string> candidates, string candidate)
    {
        var runtime = RuntimeFor(group.Id);
        while (true)
        {
            _log.Add($"prepare:{group.Id}:{candidate}");
            if (await _loader.PrepareAsync(group.Id, candidate))
            {
                break; // T1 就绪
            }

            var next = _scheduler.ReportLoadFailure(runtime, candidates, state); // 失败链（引擎内上限 3→Passive）
            if (next is null)
            {
                return; // 旧图与基准全程不动（引擎语义）；Passive 等明确检查
            }

            candidate = next;
        }

        var now = _clock.UtcNow;
        var newState = _scheduler.CommitSwitch(state, runtime, candidate, now); // T2：基准 = 成功时刻
        _states[group.Id] = newState;
        _log.Add($"commit:{group.Id}:{candidate}");
        Persist(group.Id, newState); // T3：状态先于动画落盘（§7.6 一致性）
        _host.StartFlip(group.Id, candidate); // T4：同一时刻组内全部块启动（§7.2）
        _log.Add($"flip:{group.Id}:{candidate}");
        NoteFlipStarted(group.Id);
    }

    private void Persist(string groupId, CarouselState newState)
    {
        var config = _host.CurrentConfig;
        var newConfig = config with
        {
            Objects = [.. config.Objects.Select(o => o is GroupObject g && g.Id == groupId ? g with { Carousel = newState } : o)],
        };
        _host.PersistWithoutUndo(newConfig); // SaveWithoutUndo：不触碰撤销槽（M5 §9.5 既定通道）
        _log.Add($"persist:{groupId}:{newState.CurrentImageId}");
    }

    private CarouselState EffectiveState(GroupObject group) =>
        _states.TryGetValue(group.Id, out var state) ? state : group.Carousel ?? new CarouselState();

    private CarouselRuntime RuntimeFor(string groupId)
    {
        if (!_runtimes.TryGetValue(groupId, out var runtime))
        {
            runtime = CarouselRuntimeFactory.New();
            _runtimes[groupId] = runtime;
        }

        return runtime;
    }
}
