using TileWall.Core.Configuration;

namespace TileWall.Core.Carousel;

/// <summary>时间唯一入口（M5 设计 §9.1）：生产注入 <see cref="SystemClock"/>，测试注入可手动推进的 FakeClock。</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>系统时钟（纯 BCL；Shell 装配用）。</summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    private SystemClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>轮播决策动作（§9.2）。</summary>
public enum CarouselAction
{
    /// <summary>无事可做（单张/无候选、Passive 防御、无基准、候选耗尽）。</summary>
    None,

    /// <summary>到期切一次：<see cref="CarouselDecision.CandidateImageId"/> 为本次待加载候选。</summary>
    Switch,

    /// <summary>未到期等剩余：<see cref="CarouselDecision.Remaining"/> 为距基准到期剩余（UI 据此设单发计时器）。</summary>
    WaitRemaining,
}

/// <summary>轮播决策（§9.2）：Switch 携带候选；WaitRemaining 携带剩余时间。</summary>
public sealed record CarouselDecision(CarouselAction Action, string? CandidateImageId, TimeSpan? Remaining)
{
    public static CarouselDecision None { get; } = new(CarouselAction.None, null, null);

    public static CarouselDecision Wait(TimeSpan remaining) => new(CarouselAction.WaitRemaining, null, remaining);

    public static CarouselDecision SwitchTo(string candidate) => new(CarouselAction.Switch, candidate, null);
}

/// <summary>
/// 轮播易失尝试状态（§9.2；不入配置——重启后按已保存基准检查，§11.4）：
/// Passive = 尝试上限已达，唯一出口是明确检查事件；FailedThisDue = 本轮（一次到期事件内）已失败候选。
/// </summary>
public sealed class CarouselRuntime
{
    private readonly List<string> _failedThisDue = [];

    public bool Passive { get; internal set; }

    public IReadOnlyList<string> FailedThisDue => _failedThisDue;

    /// <summary>本轮已尝试次数（含失败回填）。</summary>
    public int AttemptsThisDue { get; internal set; }

    /// <summary>已给出、等待加载结果确认的候选（加载失败回填时定位失败者）。</summary>
    public string? PendingCandidate { get; internal set; }

    internal void RecordFailure(string imageId)
    {
        _failedThisDue.Add(imageId);
        AttemptsThisDue++;
        PendingCandidate = null;
    }

    /// <summary>
    /// 本轮（一次到期事件内）结束：成功切换建立新基准后，失败清单与尝试计数清空——
    /// 本轮失败过的候选重新进入候选池（§9.2「本轮」语义、§11.1「不维护播放历史」）。
    /// 与 <see cref="Reset"/>（Passive 的唯一出口）不同：不动 Passive 位。
    /// </summary>
    internal void ResetDueScope()
    {
        AttemptsThisDue = 0;
        _failedThisDue.Clear();
        PendingCandidate = null;
    }

    internal void Reset()
    {
        Passive = false;
        AttemptsThisDue = 0;
        _failedThisDue.Clear();
        PendingCandidate = null;
    }
}

/// <summary>轮播运行态工厂（§9.2）。</summary>
public static class CarouselRuntimeFactory
{
    public static CarouselRuntime New() => new();
}

/// <summary>
/// 纯函数随机选下一张（§9.1）：排除 excluded（当前 + 本轮失败者）；无可用 → null。
/// 随机只经注入 <see cref="Random"/> 进入（生产 Random.Shared，测试种子化 → 同输入同输出）。
/// </summary>
public static class NextImagePicker
{
    public static string? PickNext(IReadOnlyList<string> candidates, IEnumerable<string> excluded, Random random)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(excluded);
        ArgumentNullException.ThrowIfNull(random);

        var excludedSet = new HashSet<string>(excluded, StringComparer.Ordinal);
        List<string>? pool = null;
        foreach (var candidate in candidates)
        {
            if (excludedSet.Contains(candidate))
            {
                continue;
            }

            pool ??= [];
            pool.Add(candidate);
        }

        return pool is null || pool.Count == 0 ? null : pool[random.Next(pool.Count)];
    }
}

/// <summary>
/// 轮播决策状态机（M5 设计 §9）：唯一时间基准 = 持久化 CarouselState{CurrentImageId, LastSwitchUtc}；
/// 引擎不持计时器——UI 的 DispatcherTimer 只是哑闹钟，Evaluate 是唯一检查入口；
/// CommitSwitch 是唯一写基准处（新基准 = 成功时刻非到期时刻）；ReportLoadFailure 计数达上限 → Passive。
/// 判定只用 now − LastSwitchUtc 单向差：时钟倒拨 → WaitRemaining 不连播；前跳 → 逾期只切一次不卡死。
/// </summary>
public sealed class CarouselScheduler
{
    private readonly IClock _clock;
    private readonly Random _random;

    /// <summary>默认切换间隔（§11.2：60 s）。</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>一次到期事件内的加载尝试上限（任务书 B / V-05：3）。</summary>
    public int MaxAttemptsPerDue { get; init; } = 3;

    public CarouselScheduler(IClock clock, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _random = random ?? Random.Shared;
    }

    /// <summary>
    /// 唯一的检查入口（§9.3 固定裁决次序）：墙显示 / 模态会话结束 / 配置变化 / 计时器到期全部收敛到它。
    /// explicitCheck=true 仅用于「明确检查机会」（墙重新显示、会话结束、图片集变化）；
    /// 计时器到期传 false（Passive 时防御性返回 None，不形成重试循环）。
    /// </summary>
    public CarouselDecision Evaluate(IReadOnlyList<string> candidates, CarouselState state, CarouselRuntime runtime, bool explicitCheck)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(runtime);

        if (candidates.Count <= 1)
        {
            return CarouselDecision.None; // 1. 单张/无候选不轮播（§11.1；B18 半）
        }

        if (runtime.Passive && !explicitCheck)
        {
            return CarouselDecision.None; // 2. 到期路径不打破 Passive（不忙循环，B18）
        }

        if (runtime.Passive)
        {
            runtime.Reset(); // 3. 明确检查事件是 Passive 的唯一出口
        }

        if (state.LastSwitchUtc is null)
        {
            return CarouselDecision.None; // 4. 首图显示由首图 CommitSwitch(when=now) 建立基准；建立前不切（§11.2）
        }

        var elapsed = _clock.UtcNow - state.LastSwitchUtc.Value;
        if (elapsed < Interval)
        {
            return CarouselDecision.Wait(Interval - elapsed); // 5. 未到期等剩余，不从打开时重计满（B15）
        }

        // 6. 到期切一次；不补播历史轮次（B14）。候选全在排除集 → None
        var candidate = NextImagePicker.PickNext(candidates, ExcludedCandidates(state, runtime), _random);
        if (candidate is null)
        {
            return CarouselDecision.None;
        }

        runtime.PendingCandidate = candidate; // 等待加载结果（ReportLoadFailure 据此回填失败者）
        return CarouselDecision.SwitchTo(candidate);
    }

    /// <summary>唯一写基准处：新图成功进入切换。when = 成功时刻（非到期时刻，§9.2）。</summary>
    public CarouselState CommitSwitch(CarouselState state, string newImageId, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(newImageId);
        return new CarouselState { CurrentImageId = newImageId, LastSwitchUtc = when };
    }

    /// <summary>
    /// 带运行态的写基准（推荐入口）：成功切换 = 本轮（一次到期事件内）结束——FailedThisDue 与尝试计数
    /// 随新基准清空，本轮失败过的候选在之后的轮次重新可被选中（§9.2「本轮」语义、§11.1 不维护播放历史）。
    /// 不经此重载（仅 3 参）时运行态不在本方法内变动，调用方须自行管理轮次边界。
    /// </summary>
    public CarouselState CommitSwitch(CarouselState state, CarouselRuntime runtime, string newImageId, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var next = CommitSwitch(state, newImageId, when);
        runtime.ResetDueScope();
        return next;
    }

    /// <summary>
    /// 加载失败回填（§9.4）：当前待确认候选计入 FailedThisDue；返回下一候选（仍低于上限且有剩余），
    /// 达上限 / 无剩余候选 → runtime.Passive = true 并返回 null（唯一出口 = 明确检查事件，不忙循环，B18）。
    /// 旧图保留：本方法不改 CarouselState（CurrentImageId / LastSwitchUtc 不动）。
    /// </summary>
    public string? ReportLoadFailure(CarouselRuntime runtime, IReadOnlyList<string> candidates, CarouselState state)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(state);

        var failed = runtime.PendingCandidate;
        if (failed is null)
        {
            return null; // 无待确认候选：无操作
        }

        runtime.RecordFailure(failed);
        if (runtime.AttemptsThisDue >= MaxAttemptsPerDue)
        {
            runtime.Passive = true; // 尝试达上限：等待下一次明确检查机会（§11.4）
            return null;
        }

        var next = NextImagePicker.PickNext(candidates, ExcludedCandidates(state, runtime), _random);
        if (next is null)
        {
            runtime.Passive = true; // 候选耗尽：同样等明确检查
            return null;
        }

        runtime.PendingCandidate = next;
        return next;
    }

    private static IEnumerable<string> ExcludedCandidates(CarouselState state, CarouselRuntime runtime)
    {
        if (state.CurrentImageId is not null)
        {
            yield return state.CurrentImageId;
        }

        foreach (var failed in runtime.FailedThisDue)
        {
            yield return failed;
        }
    }
}
