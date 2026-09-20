using TileWall.Core.Carousel;
using TileWall.Core.Configuration;
using Xunit;

namespace TileWall.Core.Tests.Carousel;

/// <summary>可手动推进的测试时钟（M5 设计 §9.1：FakeClock 放测试工程，Core 不依赖测试件）。</summary>
internal sealed class FakeClock(DateTimeOffset initial) : IClock
{
    public DateTimeOffset Now { get; private set; } = initial;

    public DateTimeOffset UtcNow => Now;

    public void Advance(TimeSpan delta) => Now += delta;

    public void Set(DateTimeOffset value) => Now = value;
}

/// <summary>
/// T-CAR（M5 设计 §9；B14/B15/B18）：FakeClock 程序化重演 B14 全时间线；
/// B15 等剩余不重计；B18 失败×3 → Passive 零忙循环、旧图基准不动、单张恒 None；
/// 逾期只切一次；PickNext 排除当前与失败者（种子化统计）；明确检查事件复位 Passive。
/// </summary>
public sealed class CarouselSchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 15, 16, 12, TimeSpan.Zero); // 15:16:12

    private static readonly string[] Candidates = ["img-a.png", "img-b.png", "img-c.png", "img-d.png"];

    private static CarouselScheduler NewScheduler(FakeClock clock) => new(clock, new Random(42));

    // ————————————————————————————— B14 全时间线（15:16:12 切 → 15:16:15 收 → 15:25:09 开只切一次 → 下次 15:26:09） —————————————————————————————

    [Fact]
    public void B14_FullTimeline_ReplaysExactlyOneSwitchOnReopen()
    {
        var clock = new FakeClock(T0);
        var scheduler = NewScheduler(clock);
        var runtime = CarouselRuntimeFactory.New();

        // 15:16:12 首图成功显示 → CommitSwitch 建立唯一时间基准
        var state = scheduler.CommitSwitch(new CarouselState(), "img-a.png", clock.UtcNow);
        Assert.Equal(T0, state.LastSwitchUtc);

        // 15:16:15 收起（调用方停表；引擎状态与基准零改动——真实经过时间不作废）
        clock.Advance(TimeSpan.FromSeconds(3));
        var midDecision = scheduler.Evaluate(Candidates, state, runtime, explicitCheck: false);
        Assert.Equal(CarouselAction.WaitRemaining, midDecision.Action);
        Assert.Equal(TimeSpan.FromSeconds(57), midDecision.Remaining); // 剩余按基准折算

        // 收起期间不推进引擎；15:25:09 重开 → 明确检查 → 逾期 → 只切一次
        clock.Set(T0.AddMinutes(8).AddSeconds(57)); // 15:25:09
        var reopen = scheduler.Evaluate(Candidates, state, runtime, explicitCheck: true);
        Assert.Equal(CarouselAction.Switch, reopen.Action);
        Assert.NotNull(reopen.CandidateImageId);
        Assert.NotEqual("img-a.png", reopen.CandidateImageId); // 排除当前
        Assert.Equal(reopen.CandidateImageId, runtime.PendingCandidate); // 待确认候选已登记

        state = scheduler.CommitSwitch(state, reopen.CandidateImageId!, clock.UtcNow); // 新基准 = 成功时刻
        Assert.Equal(clock.UtcNow, state.LastSwitchUtc);

        // 同一时刻再检查：不补播历史轮次（只切一次）
        var again = scheduler.Evaluate(Candidates, state, runtime, explicitCheck: true);
        Assert.Equal(CarouselAction.WaitRemaining, again.Action);
        Assert.Equal(TimeSpan.FromSeconds(60), again.Remaining);

        // 下一到期 = 15:26:09：差 1 s 时等剩余，到点切
        clock.Advance(TimeSpan.FromSeconds(59)); // 15:26:08
        Assert.Equal(TimeSpan.FromSeconds(1), scheduler.Evaluate(Candidates, state, runtime, explicitCheck: false).Remaining);
        clock.Advance(TimeSpan.FromSeconds(1)); // 15:26:09
        Assert.Equal(CarouselAction.Switch, scheduler.Evaluate(Candidates, state, runtime, explicitCheck: false).Action);
    }

    // ————————————————————————————— B15：未满 60 s 重开 → 等剩余不重计 —————————————————————————————

    [Fact]
    public void B15_ReopenBeforeDue_WaitsExactRemaining_NotFullInterval()
    {
        var clock = new FakeClock(T0);
        var scheduler = NewScheduler(clock);
        var runtime = CarouselRuntimeFactory.New();
        var state = scheduler.CommitSwitch(new CarouselState(), "img-a.png", T0);

        clock.Set(T0.AddSeconds(28)); // 仅经过 28 s
        var decision = scheduler.Evaluate(Candidates, state, runtime, explicitCheck: true);

        Assert.Equal(CarouselAction.WaitRemaining, decision.Action);
        Assert.Equal(TimeSpan.FromSeconds(32), decision.Remaining); // 恰等于剩余，不从打开时重计满 60 s
        Assert.Equal(60, scheduler.Interval.TotalSeconds);
    }

    // ————————————————————————————— B18：失败保留旧图 + 上限 3 → Passive 零忙循环 —————————————————————————————

    [Fact]
    public void B18_ThreeLoadFailures_GoPassive_NoBusyLoop_OldBasisUntouched()
    {
        var clock = new FakeClock(T0);
        var scheduler = NewScheduler(clock);
        var runtime = CarouselRuntimeFactory.New();
        var switchUtc = T0.AddSeconds(-120); // 已逾期
        var state = new CarouselState { CurrentImageId = "img-a.png", LastSwitchUtc = switchUtc };
        var basisBefore = state;

        // 到期 → Switch；连续失败回填 → 下一候选（排除当前与已失败者）→ 第 3 次失败达上限 → Passive
        var first = scheduler.Evaluate(Candidates, state, runtime, explicitCheck: false);
        Assert.Equal(CarouselAction.Switch, first.Action);
        var failed1 = first.CandidateImageId!;
        var failed2 = scheduler.ReportLoadFailure(runtime, Candidates, state);
        Assert.NotNull(failed2);
        Assert.NotEqual(failed1, failed2);
        Assert.NotEqual("img-a.png", failed2);
        var failed3 = scheduler.ReportLoadFailure(runtime, Candidates, state);
        Assert.NotNull(failed3);
        Assert.NotEqual(failed2, failed3);
        Assert.Null(scheduler.ReportLoadFailure(runtime, Candidates, state)); // 第 3 次失败 → Passive
        Assert.True(runtime.Passive);
        Assert.Equal(3, runtime.AttemptsThisDue);
        Assert.Equal(new[] { failed1, failed2, failed3 }, runtime.FailedThisDue.ToArray());

        // 旧图保留：CurrentImageId / LastSwitchUtc 全程不变
        Assert.Equal("img-a.png", state.CurrentImageId);
        Assert.Equal(switchUtc, state.LastSwitchUtc);
        Assert.True(basisBefore.Equals(state));

        // Passive 后到期路径 10 轮 Evaluate(false) 零进一步候选——「不忙循环」
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(CarouselAction.None, scheduler.Evaluate(Candidates, state, runtime, explicitCheck: false).Action);
        }

        // 唯一出口 = 明确检查事件：复位后可再切（排除当前）
        var recovered = scheduler.Evaluate(Candidates, state, runtime, explicitCheck: true);
        Assert.False(runtime.Passive);
        Assert.Empty(runtime.FailedThisDue);
        Assert.Equal(CarouselAction.Switch, recovered.Action);
        Assert.NotEqual("img-a.png", recovered.CandidateImageId);
    }

    [Fact]
    public void B18_CandidateExhaustion_GoesPassive()
    {
        var clock = new FakeClock(T0);
        var scheduler = NewScheduler(clock);
        var runtime = CarouselRuntimeFactory.New();
        var state = new CarouselState { CurrentImageId = "img-a.png", LastSwitchUtc = T0.AddSeconds(-120) };
        var two = new[] { "img-a.png", "img-b.png" };

        Assert.Equal(CarouselAction.Switch, scheduler.Evaluate(two, state, runtime, explicitCheck: false).Action);
        Assert.Null(scheduler.ReportLoadFailure(runtime, two, state)); // 失败后无剩余候选
        Assert.True(runtime.Passive);
    }

    [Fact]
    public void SingleCandidate_NeverSwitches()
    {
        var clock = new FakeClock(T0);
        var scheduler = NewScheduler(clock);
        var runtime = CarouselRuntimeFactory.New();
        var state = new CarouselState { CurrentImageId = "only.png", LastSwitchUtc = T0.AddSeconds(-600) };

        Assert.Equal(CarouselAction.None, scheduler.Evaluate(["only.png"], state, runtime, explicitCheck: true).Action);
        Assert.Equal(CarouselAction.None, scheduler.Evaluate(["only.png"], state, runtime, explicitCheck: false).Action);
        Assert.Equal(CarouselAction.None, scheduler.Evaluate([], state, runtime, explicitCheck: true).Action);
    }

    // ————————————————————————————— 首图建基准前不切 / 逾期不补播 —————————————————————————————

    [Fact]
    public void NoBasis_NeverSwitches_UntilFirstCommitSwitch()
    {
        var clock = new FakeClock(T0);
        var scheduler = NewScheduler(clock);
        var runtime = CarouselRuntimeFactory.New();
        var state = new CarouselState(); // CurrentImageId/LastSwitchUtc 皆空

        Assert.Equal(CarouselAction.None, scheduler.Evaluate(Candidates, state, runtime, explicitCheck: true).Action);

        state = scheduler.CommitSwitch(state, "img-a.png", clock.UtcNow);
        clock.Advance(scheduler.Interval);
        Assert.Equal(CarouselAction.Switch, scheduler.Evaluate(Candidates, state, runtime, explicitCheck: false).Action);
    }

    [Fact]
    public void Overdue_ByFiveIntervals_SwitchesExactlyOnce()
    {
        var clock = new FakeClock(T0);
        var scheduler = NewScheduler(clock);
        var runtime = CarouselRuntimeFactory.New();
        var state = scheduler.CommitSwitch(new CarouselState(), "img-a.png", T0);

        clock.Advance(TimeSpan.FromSeconds(300)); // 逾期 5×Interval
        var decision = scheduler.Evaluate(Candidates, state, runtime, explicitCheck: false);
        Assert.Equal(CarouselAction.Switch, decision.Action);

        state = scheduler.CommitSwitch(state, decision.CandidateImageId!, clock.UtcNow);
        var followUp = scheduler.Evaluate(Candidates, state, runtime, explicitCheck: false);
        Assert.Equal(CarouselAction.WaitRemaining, followUp.Action); // 新基准起算，不连环补播
        Assert.Equal(TimeSpan.FromSeconds(60), followUp.Remaining);
    }

    // ————————————————————————————— PickNext 排除与统计（种子化） —————————————————————————————

    [Fact]
    public void PickNext_NeverReturnsExcluded_AndCoversRestOfPool()
    {
        var random = new Random(7);
        var seen = new HashSet<string>();

        for (var i = 0; i < 300; i++)
        {
            var picked = NextImagePicker.PickNext(Candidates, ["img-a.png", "img-c.png"], random);
            Assert.True(picked is "img-b.png" or "img-d.png", $"排除集内候选被选中：{picked}");
            seen.Add(picked!);
        }

        Assert.True(seen.Count >= 2, $"排除当前后剩余候选都应可被选到，实际仅出现：{string.Join(",", seen)}");
    }

    [Fact]
    public void PickNext_AllExcluded_ReturnsNull()
    {
        Assert.Null(NextImagePicker.PickNext(["a", "b"], ["a", "b"], new Random(1)));
        Assert.Null(NextImagePicker.PickNext([], [], new Random(1)));
    }

    // ————————————————————————————— 评审修复回归：失败候选不得跨轮永久排除 —————————————————————————————

    [Fact]
    public void CommitSwitch_EndsDueScope_FailedCandidateSelectableAgain_NextRound()
    {
        // 评审探针场景：3 候选、第 1 轮失败一次后成功切换；200 个种子下第 2 轮应能重新选中该失败候选。
        // 修复前（FailedThisDue 只在 Passive 复位清空）：重新可选中次数 = 0。
        var reselectable = 0;
        for (var seed = 0; seed < 200; seed++)
        {
            var clock = new FakeClock(T0);
            var scheduler = new CarouselScheduler(clock, new Random(seed));
            var runtime = CarouselRuntimeFactory.New();
            var state = scheduler.CommitSwitch(new CarouselState(), "img-a.png", T0);

            clock.Advance(scheduler.Interval); // 第 1 轮到期
            var first = scheduler.Evaluate(Candidates, state, runtime, explicitCheck: false);
            Assert.Equal(CarouselAction.Switch, first.Action);
            var failed = first.CandidateImageId!;

            var second = scheduler.ReportLoadFailure(runtime, Candidates, state); // 失败一次
            Assert.NotNull(second);
            Assert.NotEqual(failed, second);

            // 成功切换建立新基准（带运行态）＝ 本轮结束
            state = scheduler.CommitSwitch(state, runtime, second!, clock.UtcNow);
            Assert.Empty(runtime.FailedThisDue);
            Assert.Equal(0, runtime.AttemptsThisDue);

            clock.Advance(scheduler.Interval); // 第 2 轮到期
            var third = scheduler.Evaluate(Candidates, state, runtime, explicitCheck: false);
            Assert.Equal(CarouselAction.Switch, third.Action);
            if (third.CandidateImageId == failed)
            {
                reselectable++;
            }
        }

        Assert.True(reselectable > 0, $"200 个种子下失败候选在第 2 轮重新可被选中次数 = {reselectable} => 失败候选被跨轮永久排除");
        Assert.True(reselectable < 200, "排除当前图仍应生效（全部重选到失败候选说明排除集失效）");
    }

    [Fact]
    public void CommitSwitch_ThreeArgOverload_DoesNotTouchRuntime()
    {
        var clock = new FakeClock(T0);
        var scheduler = new CarouselScheduler(clock, new Random(1));
        var runtime = CarouselRuntimeFactory.New();
        var state = scheduler.CommitSwitch(new CarouselState(), "img-a.png", T0);

        clock.Advance(scheduler.Interval);
        scheduler.Evaluate(Candidates, state, runtime, explicitCheck: false);
        Assert.NotNull(scheduler.ReportLoadFailure(runtime, Candidates, state));
        Assert.NotEmpty(runtime.FailedThisDue);

        var next = scheduler.CommitSwitch(state, "img-z.png", clock.UtcNow); // §9.2 三参签名
        Assert.Equal("img-z.png", next.CurrentImageId);
        Assert.NotEmpty(runtime.FailedThisDue); // 三参重载不触运行态（由调用方管理轮次边界）
    }

    [Fact]
    public void ClockBackwardJump_WaitsInsteadOfRapidSwitching()
    {
        var clock = new FakeClock(T0);
        var scheduler = NewScheduler(clock);
        var runtime = CarouselRuntimeFactory.New();
        var state = scheduler.CommitSwitch(new CarouselState(), "img-a.png", T0);

        clock.Set(T0.AddSeconds(90)); // 已到期
        Assert.Equal(CarouselAction.Switch, scheduler.Evaluate(Candidates, state, runtime, explicitCheck: false).Action);

        clock.Set(T0.AddSeconds(30)); // 系统时钟倒拨：Δ 变小
        var decision = scheduler.Evaluate(Candidates, state, runtime, explicitCheck: false);
        Assert.Equal(CarouselAction.WaitRemaining, decision.Action); // 不连播
    }
}
