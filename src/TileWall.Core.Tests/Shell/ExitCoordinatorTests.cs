using TileWall.Core.Shell;
using Xunit;

namespace TileWall.Core.Tests.Shell;

/// <summary>假宿主：记录调用序；可配置单步抛异常（异常路径覆盖断言）。</summary>
internal sealed class FakeExitHost : IExitHost
{
    public List<string> Calls { get; } = [];

    public IExitParticipant? TopParticipant { get; set; }

    public bool ThrowOnRemoveTrayIcon { get; set; }

    public bool CloseTopSessionCalled => Calls.Contains("CloseTopSession");

    public void CloseTopSession() => Calls.Add("CloseTopSession");

    public void CancelTransientState() => Calls.Add("CancelTransientState");

    public void SnapHideWall() => Calls.Add("SnapHideWall");

    public void UnregisterHotKey() => Calls.Add("UnregisterHotKey");

    public void RemoveTrayIcon()
    {
        Calls.Add("RemoveTrayIcon");
        if (ThrowOnRemoveTrayIcon)
        {
            throw new InvalidOperationException("shell already removed the icon");
        }
    }

    public void DestroyShellHostWindow() => Calls.Add("DestroyShellHostWindow");

    public void ReleaseSingleInstanceMutex() => Calls.Add("ReleaseSingleInstanceMutex");

    public void ExitApplication() => Calls.Add("ExitApplication");
}

/// <summary>假参与面：可配置草稿与保存结果，记录方法调用。</summary>
internal sealed class FakeParticipant : IExitParticipant
{
    public List<string> Calls { get; } = [];

    public bool HasUnsavedDraftValue { get; set; } = true;

    public string? SaveError { get; set; }

    public bool HasUnsavedDraft
    {
        get
        {
            Calls.Add("HasUnsavedDraft");
            return HasUnsavedDraftValue;
        }
    }

    public string? TrySaveNow()
    {
        Calls.Add("TrySaveNow");
        return SaveError;
    }

    public void DiscardDraft() => Calls.Add("DiscardDraft");
}

/// <summary>
/// T-EXIT（M7 设计 §12.1 / §9）：三分支调用序；保存失败/取消零副作用；无草稿直通；
/// 固定释放序断言（§10）；单步抛异常不阻断后续释放与 Exit（异常路径全覆盖）。
/// </summary>
public sealed class ExitCoordinatorTests
{
    private static readonly string[] TeardownOrder =
    [
        "CancelTransientState",
        "SnapHideWall",
        "UnregisterHotKey",
        "RemoveTrayIcon",
        "DestroyShellHostWindow",
        "ReleaseSingleInstanceMutex",
        "ExitApplication",
    ];

    // ————————————————————————————— ShouldConfirmDraft —————————————————————————————

    [Fact]
    public void ShouldConfirmDraft_NullParticipant_IsFalse()
    {
        var exit = new ExitCoordinator(new FakeExitHost());
        Assert.False(exit.ShouldConfirmDraft(null));
    }

    [Fact]
    public void ShouldConfirmDraft_DraftlessParticipant_IsFalse()
    {
        var exit = new ExitCoordinator(new FakeExitHost());
        var p = new FakeParticipant { HasUnsavedDraftValue = false }; // 设置窗：自动保存，无草稿
        Assert.False(exit.ShouldConfirmDraft(p));
    }

    [Fact]
    public void ShouldConfirmDraft_DirtyParticipant_IsTrue()
    {
        var exit = new ExitCoordinator(new FakeExitHost());
        Assert.True(exit.ShouldConfirmDraft(new FakeParticipant { HasUnsavedDraftValue = true }));
    }

    // ————————————————————————————— 三分支 —————————————————————————————

    [Fact]
    public void SaveAndExit_Success_SavesClosesSessionAndProceeds()
    {
        var host = new FakeExitHost();
        var exit = new ExitCoordinator(host);
        var p = new FakeParticipant { SaveError = null };

        Assert.True(exit.ApplyDecision(p, ExitDecision.SaveAndExit));
        Assert.Equal(["CloseTopSession"], host.Calls); // 宿主只关会话；保存动作在参与面上
        Assert.Equal(["TrySaveNow"], p.Calls);
    }

    [Fact]
    public void SaveAndExit_Failure_AbortsWithZeroSideEffects()
    {
        var host = new FakeExitHost();
        var exit = new ExitCoordinator(host);
        var p = new FakeParticipant { SaveError = "磁盘已满" };

        Assert.False(exit.ApplyDecision(p, ExitDecision.SaveAndExit)); // 保存失败不继续退出
        Assert.Empty(host.Calls); // 不关会话、不解锁、不摘托盘
        Assert.Equal(["TrySaveNow"], p.Calls); // 不触碰 DiscardDraft
    }

    [Fact]
    public void DiscardAndExit_DiscardsThenCloses()
    {
        var host = new FakeExitHost();
        var exit = new ExitCoordinator(host);
        var p = new FakeParticipant();

        Assert.True(exit.ApplyDecision(p, ExitDecision.DiscardAndExit));
        Assert.Equal(["DiscardDraft"], p.Calls);
        Assert.Equal(["CloseTopSession"], host.Calls);
    }

    [Fact]
    public void CancelExit_AbortsWithZeroSideEffects()
    {
        var host = new FakeExitHost();
        var exit = new ExitCoordinator(host);
        var p = new FakeParticipant();

        Assert.False(exit.ApplyDecision(p, ExitDecision.CancelExit));
        Assert.Empty(host.Calls);
        Assert.Empty(p.Calls); // 原窗口与阻塞状态均不变（§3.5）
    }

    [Fact]
    public void NoSession_AnyDecision_ProceedsWithoutCalls()
    {
        var host = new FakeExitHost();
        var exit = new ExitCoordinator(host);
        Assert.True(exit.ApplyDecision(null, ExitDecision.SaveAndExit));
        Assert.True(exit.ApplyDecision(null, ExitDecision.DiscardAndExit));
        Assert.False(exit.ApplyDecision(null, ExitDecision.CancelExit));
        Assert.Empty(host.Calls);
    }

    // ————————————————————————————— 无草稿直通 —————————————————————————————

    [Fact]
    public void CloseDraftlessSession_ClosesSessionOnly()
    {
        var host = new FakeExitHost();
        var exit = new ExitCoordinator(host);
        exit.CloseDraftlessSession();
        Assert.Equal(["CloseTopSession"], host.Calls);
    }

    // ————————————————————————————— 固定释放序（§10） —————————————————————————————

    [Fact]
    public void RunTeardown_FollowsFixedReleaseOrder()
    {
        var host = new FakeExitHost();
        var exit = new ExitCoordinator(host);
        exit.RunTeardown();
        Assert.Equal(TeardownOrder, host.Calls);
        Assert.True(host.Calls.IndexOf("UnregisterHotKey") < host.Calls.IndexOf("RemoveTrayIcon"));
        Assert.True(host.Calls.IndexOf("UnregisterHotKey") < host.Calls.IndexOf("DestroyShellHostWindow"));
        Assert.Equal("ExitApplication", host.Calls[^1]); // 必达退出
    }

    // ————————————————————————————— 异常路径 —————————————————————————————

    [Fact]
    public void RunTeardown_StepThrows_SubsequentStepsStillRunAndExitReached()
    {
        var host = new FakeExitHost { ThrowOnRemoveTrayIcon = true };
        var exit = new ExitCoordinator(host);
        exit.RunTeardown(); // 不上抛：单步失败不阻断序列
        Assert.Equal(TeardownOrder, host.Calls);
        Assert.Equal("ExitApplication", host.Calls[^1]);
    }
}
