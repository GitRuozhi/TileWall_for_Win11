namespace TileWall.Core.Shell;

/// <summary>退出三分支的用户决定（M7 设计 §9.1）。</summary>
public enum ExitDecision
{
    /// <summary>保存草稿后退出。</summary>
    SaveAndExit,

    /// <summary>放弃草稿并退出。</summary>
    DiscardAndExit,

    /// <summary>取消退出（原窗口与阻塞状态均不变）。</summary>
    CancelExit,
}

/// <summary>会话窗的退出参与面（属性窗有草稿；设置窗自动保存故无草稿，直通关闭）。</summary>
public interface IExitParticipant
{
    /// <summary>存在未保存草稿 → true（决定是否弹三分支确认）。</summary>
    bool HasUnsavedDraft { get; }

    /// <summary>同步保存草稿。返回 null = 成功（窗体自行关闭）；否则错误文本（就地显示、窗口保持、零副作用）。</summary>
    string? TrySaveNow();

    /// <summary>丢弃草稿（此后关闭窗体不再弹确认；「放弃退出」分支用）。</summary>
    void DiscardDraft();
}

/// <summary>退出序列的宿主动作面（MainWindow 实现；假实现记录调用序供单测断言）。</summary>
public interface IExitHost
{
    /// <summary>顶层模态会话窗的参与面；null = 无会话。</summary>
    IExitParticipant? TopParticipant { get; }

    /// <summary>关闭顶层会话窗（解锁主墙；会话已不存在时为空操作）。</summary>
    void CloseTopSession();

    /// <summary>Fallout 兜底：取消在飞手势（T12 全量恢复）并关闭残余 Flyout。</summary>
    void CancelTransientState();

    /// <summary>墙隐藏化（无动画 SnapHide；渲染停止点唯一）。</summary>
    void SnapHideWall();

    /// <summary>注销全局热键（C11：先于托盘/宿主窗销毁——退出后不响应已注册热键）。</summary>
    void UnregisterHotKey();

    /// <summary>摘除托盘图标（NIM_DELETE + DestroyIcon；不留幽灵图标）。</summary>
    void RemoveTrayIcon();

    /// <summary>销毁共享宿主窗（托盘回调/热键消息的 HWND）。</summary>
    void DestroyShellHostWindow();

    /// <summary>显式释放单实例互斥体（双保险：进程退出时内核兜底回收，但不依赖该兜底）。</summary>
    void ReleaseSingleInstanceMutex();

    /// <summary>结束进程（Application.Exit）。</summary>
    void ExitApplication();
}

/// <summary>
/// 退出编排（M7 设计 §9）：草稿三分支 → 固定释放序列 → Exit。
/// 「保存有效状态」= 零待写确认：所有变更通道（Commit/SaveWithoutUndo）均为即时原子落盘，
/// 不存在退出时未落盘的脏缓冲。中止路径（取消/保存失败）零副作用——不写盘、不解锁、不摘托盘。
/// </summary>
public sealed class ExitCoordinator
{
    private readonly IExitHost _host;

    public ExitCoordinator(IExitHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <summary>是否需要草稿三分支确认（仅当有会话且其有未保存草稿；设置窗无草稿直通）。</summary>
    public bool ShouldConfirmDraft(IExitParticipant? participant) =>
        participant is not null && participant.HasUnsavedDraft;

    /// <summary>无草稿会话直通：直接关闭会话窗，继续退出（§14.4 已自动保存项不重复确认）。</summary>
    public void CloseDraftlessSession() => _host.CloseTopSession();

    /// <summary>
    /// 应用用户三分支决定。返回 true = 继续退出；false = 中止（此时零宿主动作、零副作用）。
    /// </summary>
    public bool ApplyDecision(IExitParticipant? participant, ExitDecision decision)
    {
        switch (decision)
        {
            case ExitDecision.SaveAndExit:
                if (participant is not null)
                {
                    var error = participant.TrySaveNow();
                    if (error is not null)
                    {
                        return false; // 保存失败不继续退出（错误已由会话窗就地显示；原窗口+阻塞态原样）
                    }

                    _host.CloseTopSession(); // 保存成功（会话窗通常已自行关闭；此处幂等）
                }

                return true;
            case ExitDecision.DiscardAndExit:
                if (participant is not null)
                {
                    participant.DiscardDraft();
                    _host.CloseTopSession();
                }

                return true;
            case ExitDecision.CancelExit:
            default:
                return false; // 取消：不触碰 participant、不写盘、不解锁
        }
    }

    /// <summary>
    /// 固定释放序列（§10 顺序：草稿处置 → 会话窗关闭 → SnapHide → UnregisterHotKey →
    /// NIM_DELETE+DestroyIcon → 宿主窗销毁 → 互斥体 → Exit）。
    /// 每步独立 try/catch：单步失败（如 Shell 已先摘图标）不阻断后续释放与退出——异常路径也保证成对注销与必然退出。
    /// </summary>
    public void RunTeardown()
    {
        RunStep(_host.CancelTransientState);
        RunStep(_host.SnapHideWall);
        RunStep(_host.UnregisterHotKey);        // R5：先于托盘/宿主窗销毁
        RunStep(_host.RemoveTrayIcon);          // R1+R2
        RunStep(_host.DestroyShellHostWindow);  // R4
        RunStep(_host.ReleaseSingleInstanceMutex); // R7（显式双保险）
        RunStep(_host.ExitApplication);
    }

    private static void RunStep(Action step)
    {
        try
        {
            step();
        }
        catch (Exception)
        {
            // 释放路径的单步失败不阻断序列（泄漏后果清单见设计 §10；进程退出为最终兜底）
        }
    }
}
