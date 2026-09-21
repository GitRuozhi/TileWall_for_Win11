using TileWall.Core.Shell;
using Xunit;

namespace TileWall.Core.Tests.Shell;

/// <summary>假宿主：记录调用序与计数（T-SHOW 断言面）。</summary>
internal sealed class FakeWallShowHost : IWallShowHost
{
    public List<string> Calls { get; } = [];

    public void BeginShowAnimation() => Calls.Add("BeginShow");

    public void BeginHideAnimation() => Calls.Add("BeginHide");

    public void CancelAnimations() => Calls.Add("Cancel");

    public void SnapShow() => Calls.Add("SnapShow");

    public void SnapHide() => Calls.Add("SnapHide");

    public void FocusWall() => Calls.Add("FocusWall");

    public int Count(string call) => Calls.Count(c => c == call);
}

/// <summary>
/// T-SHOW（M7 设计 §12.1 / §6.1）：转移表全格；A8 三连按不排队；W2 恰好一次 Snap（任一到达
/// Hidden 的路径最终恰好一次 SnapHide，Visible 与 SnapShow 同理）；W3 模态防御行忽略；W4 背景启动无动画。
/// </summary>
public sealed class WallShowMachineTests
{
    private static WallShowMachine New(WallShowState initial, out FakeWallShowHost host)
    {
        host = new FakeWallShowHost();
        return new WallShowMachine(initial, host);
    }

    // ————————————————————————————— 转移表：Hidden —————————————————————————————

    [Fact]
    public void Hidden_Toggle_GoesShowingWithSnapShowThenAnimation()
    {
        var m = New(WallShowState.Hidden, out var host);
        m.Request(WallShowTrigger.Toggle);
        Assert.Equal(WallShowState.Showing, m.State);
        Assert.Equal(["SnapShow", "BeginShow"], host.Calls); // 物理可见先成立，动画随后（W2）
    }

    [Fact]
    public void Hidden_Show_BehavesLikeToggle()
    {
        var m = New(WallShowState.Hidden, out var host);
        m.Request(WallShowTrigger.Show);
        Assert.Equal(WallShowState.Showing, m.State);
        Assert.Equal(["SnapShow", "BeginShow"], host.Calls);
    }

    [Fact]
    public void Hidden_Hide_IsIgnoredIdempotently()
    {
        var m = New(WallShowState.Hidden, out var host);
        m.Request(WallShowTrigger.Hide);
        Assert.Equal(WallShowState.Hidden, m.State);
        Assert.Empty(host.Calls);
    }

    // ————————————————————————————— 转移表：Showing（A8 打断格） —————————————————————————————

    [Fact]
    public void Showing_Toggle_InterruptsToHiddenWithSingleSnapHide()
    {
        var m = New(WallShowState.Hidden, out var host);
        m.Request(WallShowTrigger.Toggle);
        m.Request(WallShowTrigger.Toggle); // A8：不排队，直接打断
        Assert.Equal(WallShowState.Hidden, m.State);
        Assert.Equal(1, host.Count("Cancel"));
        Assert.Equal(1, host.Count("SnapHide"));
        Assert.Equal(1, host.Count("SnapShow"));
    }

    [Fact]
    public void Showing_Show_InterruptsToVisibleWithoutReplayingShowAnimation()
    {
        var m = New(WallShowState.Hidden, out var host);
        m.Request(WallShowTrigger.Show);
        m.Request(WallShowTrigger.Show);
        Assert.Equal(WallShowState.Visible, m.State);
        Assert.Equal(1, host.Count("BeginShow")); // 不叠加动画（W1）
        Assert.Equal(2, host.Count("SnapShow"));
    }

    [Fact]
    public void Showing_Hide_InterruptsToHidden()
    {
        var m = New(WallShowState.Hidden, out var host);
        m.Request(WallShowTrigger.Show);
        m.Request(WallShowTrigger.Hide);
        Assert.Equal(WallShowState.Hidden, m.State);
        Assert.Equal(1, host.Count("SnapHide"));
    }

    [Fact]
    public void Showing_Completion_GoesVisibleWithNoExtraCalls()
    {
        var m = New(WallShowState.Hidden, out var host);
        m.Request(WallShowTrigger.Toggle);
        m.ShowAnimationCompleted();
        Assert.Equal(WallShowState.Visible, m.State);
        Assert.Equal(["SnapShow", "BeginShow"], host.Calls);
    }

    // ————————————————————————————— 转移表：Visible —————————————————————————————

    [Fact]
    public void Visible_Toggle_GoesHidingWithAnimationOnly_NoSnapYet()
    {
        var m = New(WallShowState.Visible, out var host);
        m.Request(WallShowTrigger.Toggle);
        Assert.Equal(WallShowState.Hiding, m.State);
        Assert.Equal(["BeginHide"], host.Calls); // 物理仍可见；SnapHide 在动画完成后（W2）
    }

    [Fact]
    public void Visible_Show_FocusesWallWithoutTransition()
    {
        var m = New(WallShowState.Visible, out var host);
        m.Request(WallShowTrigger.Show);
        Assert.Equal(WallShowState.Visible, m.State);
        Assert.Equal(["FocusWall"], host.Calls); // 托盘双击：聚焦不收起
    }

    [Fact]
    public void Visible_Hide_GoesHiding()
    {
        var m = New(WallShowState.Visible, out var host);
        m.Request(WallShowTrigger.Hide);
        Assert.Equal(WallShowState.Hiding, m.State);
        Assert.Equal(["BeginHide"], host.Calls);
    }

    // ————————————————————————————— 转移表：Hiding（A8 打断格） —————————————————————————————

    [Fact]
    public void Hiding_Toggle_InterruptsToVisible()
    {
        var m = New(WallShowState.Visible, out var host);
        m.Request(WallShowTrigger.Toggle);
        m.Request(WallShowTrigger.Toggle);
        Assert.Equal(WallShowState.Visible, m.State);
        Assert.Equal(["BeginHide", "Cancel", "SnapShow"], host.Calls);
    }

    [Fact]
    public void Hiding_Show_InterruptsToVisible()
    {
        var m = New(WallShowState.Visible, out var host);
        m.Request(WallShowTrigger.Hide);
        m.Request(WallShowTrigger.Show);
        Assert.Equal(WallShowState.Visible, m.State);
        Assert.Equal(1, host.Count("BeginHide")); // 不补播反向动画（W1）
        Assert.Equal(1, host.Count("SnapShow"));
    }

    [Fact]
    public void Hiding_Hide_IsIgnoredIdempotently()
    {
        var m = New(WallShowState.Visible, out var host);
        m.Request(WallShowTrigger.Hide);
        m.Request(WallShowTrigger.Hide);
        Assert.Equal(WallShowState.Hiding, m.State);
        Assert.Equal(1, host.Count("BeginHide"));
    }

    [Fact]
    public void Hiding_Completion_SnapsHideExactlyOnce()
    {
        var m = New(WallShowState.Visible, out var host);
        m.Request(WallShowTrigger.Toggle);
        m.HideAnimationCompleted();
        Assert.Equal(WallShowState.Hidden, m.State);
        Assert.Equal(1, host.Count("SnapHide"));
    }

    // ————————————————————————————— 过期完成回调（W1） —————————————————————————————

    [Fact]
    public void StaleCompletions_AreIgnored()
    {
        var m = New(WallShowState.Visible, out var host);
        m.ShowAnimationCompleted(); // Visible 态收到的 Show 完成回调 = 过期
        Assert.Equal(WallShowState.Visible, m.State);

        m.Request(WallShowTrigger.Hide); // Hiding
        m.ShowAnimationCompleted(); // Hiding 态收到的 Show 完成回调 = 过期
        Assert.Equal(WallShowState.Hiding, m.State);

        m.HideAnimationCompleted();
        m.HideAnimationCompleted(); // Hidden 态收到的 Hide 完成回调 = 过期
        Assert.Equal(WallShowState.Hidden, m.State);
        Assert.Equal(1, host.Count("SnapHide")); // 恰好一次
    }

    // ————————————————————————————— A8 三连按不排队 —————————————————————————————

    [Fact]
    public void ToggleX3Rapidly_NoQueueing_FinalStateConsistent()
    {
        var m = New(WallShowState.Hidden, out var host);
        m.Request(WallShowTrigger.Toggle); // Hidden → Showing
        m.Request(WallShowTrigger.Toggle); // 打断 → Hidden
        m.Request(WallShowTrigger.Toggle); // Hidden → Showing
        Assert.Equal(WallShowState.Showing, m.State);
        Assert.Equal(2, host.Count("BeginShow")); // 两次显示动画（无排队叠加）
        Assert.Equal(1, host.Count("Cancel"));
        Assert.Equal(1, host.Count("SnapHide")); // W2：一次中间收起恰好一次 SnapHide
        m.ShowAnimationCompleted();
        Assert.Equal(WallShowState.Visible, m.State);
    }

    // ————————————————————————————— W3 模态防御行 —————————————————————————————

    [Fact]
    public void InputGateClosed_ToggleAndHideIgnored()
    {
        var m = New(WallShowState.Visible, out var host);
        m.InputGateClosed = true;
        m.Request(WallShowTrigger.Toggle);
        m.Request(WallShowTrigger.Hide);
        Assert.Equal(WallShowState.Visible, m.State);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void InputGateClosed_ShowPassesForPresetModalSequence()
    {
        // §8.5 预置模态：Attach（ModalActive=true → 门关）→ Request(Show) 恢复墙为背景 → ActivateTop。
        // 门控行吞掉 Show 会让设置窗弹出却无墙背景（评审修复回归点）。
        var m = New(WallShowState.Hidden, out var host);
        m.InputGateClosed = true;
        m.Request(WallShowTrigger.Show);
        Assert.Equal(WallShowState.Showing, m.State);
        Assert.Equal(["SnapShow", "BeginShow"], host.Calls);
        m.ShowAnimationCompleted();
        Assert.Equal(WallShowState.Visible, m.State);

        // 门关期间 Hide 依旧被挡（设置窗为背景的墙不得被失焦等输入收起）
        m.Request(WallShowTrigger.Hide);
        Assert.Equal(WallShowState.Visible, m.State);
        Assert.DoesNotContain("BeginHide", host.Calls);
    }

    // ————————————————————————————— W4 背景启动 / 初始态 —————————————————————————————

    [Fact]
    public void BackgroundStart_InitialHidden_NoHostCallsAtConstruction()
    {
        var m = New(WallShowState.Hidden, out var host);
        Assert.Equal(WallShowState.Hidden, m.State);
        Assert.Empty(host.Calls); // 无动画（W4）
    }

    [Fact]
    public void ManualStart_InitialVisible_FirstToggleGoesHiding_NeverSnapsShow()
    {
        var m = New(WallShowState.Visible, out var host);
        m.Request(WallShowTrigger.Toggle);
        m.HideAnimationCompleted();
        Assert.Equal(WallShowState.Hidden, m.State);
        Assert.DoesNotContain("SnapShow", host.Calls); // 初始可见是物理事实，不经状态机
        Assert.Equal(1, host.Count("SnapHide"));
    }

    // ————————————————————————————— W2：长链路恰好一次 Snap —————————————————————————————

    [Fact]
    public void LongPathThroughInterrupts_KeepsSnapCountsExact()
    {
        var m = New(WallShowState.Hidden, out var host);
        m.Request(WallShowTrigger.Show);   // Showing (SnapShow#1)
        m.Request(WallShowTrigger.Hide);   // 打断 → Hidden (SnapHide#1)
        m.Request(WallShowTrigger.Toggle); // Showing (SnapShow#2)
        m.ShowAnimationCompleted();        // Visible
        m.Request(WallShowTrigger.Toggle); // Hiding
        m.HideAnimationCompleted();        // Hidden (SnapHide#2)
        Assert.Equal(WallShowState.Hidden, m.State);
        Assert.Equal(2, host.Count("SnapShow"));
        Assert.Equal(2, host.Count("SnapHide")); // 两次到达 Hidden，恰好各一次
    }
}
