namespace TileWall.Core.Shell;

/// <summary>适配状态（M8 设计 §6.3）：Fitted | AdaptationNeeded（附 deferred 提示标志）。</summary>
public enum AdaptationState
{
    /// <summary>当前工作区容得下原布局。</summary>
    Fitted,

    /// <summary>容不下：墙不渲染被裁切内容，提示窗/调整窗承载适配状态。</summary>
    AdaptationNeeded,
}

/// <summary>
/// 适配状态机宿主面（M8 设计 §6.3；MainWindow 实现，测试注入假宿主记录调用序）。
/// </summary>
public interface IAdaptationHost
{
    /// <summary>打开/聚焦提示窗（owned window，不占模态槽）。</summary>
    void ShowPrompt(AdaptationSnapshot snapshot);

    /// <summary>关闭提示窗（窗未开时为空操作）。</summary>
    void DismissPrompt();

    /// <summary>数字更新（窗开着才刷；deferred 恢复也经 Evaluate 汇入）。</summary>
    void RefreshPrompt(AdaptationSnapshot snapshot);

    /// <summary>进入适配：取消手势（T12）→ 墙可见则 Request(Hide)（不经裸 AppWindow.Hide）。</summary>
    void HideWallForAdaptation();

    /// <summary>退出适配：重渲染（当前生效配置）+ Request(Show)——恢复显示必经状态机（R-6）。</summary>
    void RestoreWallAfterAdaptation();

    /// <summary>模态单槽占用判断（deferred 提示依据）。</summary>
    bool HasActiveSession { get; }

    /// <summary>提示窗当前是否打开（RefreshPrompt 的「窗开着才刷」依据）。</summary>
    bool IsPromptVisible { get; }
}

/// <summary>
/// 适配状态机（M8 设计 §6.3；纯 C#，风格同 WallShowMachine——枚举 + 显式转移即实现）：
/// <code>
/// Fitted ──Evaluate(NoFit)──────────────→ AdaptationNeeded（deferred = 有活动会话）
/// AdaptationNeeded ──Evaluate(NoFit)────→ 原 state：窗开着 → RefreshPrompt；deferred 且会话已清 → ShowPrompt
/// AdaptationNeeded ──PromptDismissed────→ 原 state（deferred 清除；墙保持隐藏；Show 类输入由路由器改弹提示窗）
/// AdaptationNeeded ──Evaluate(Fit)──────→ Fitted：DismissPrompt → RestoreWallAfterAdaptation
/// AdaptationNeeded ──AdjustmentCommitted→ Fitted：同上（调整窗确认路径，配置已由调用方提交）
/// Fitted 内的 Evaluate(Fit) / PromptDismissed / AdjustmentCommitted → 幂等无宿主动作。
/// </code>
/// </summary>
public sealed class AdaptationMachine
{
    private readonly IAdaptationHost _host;
    private AdaptationSnapshot? _lastNoFit;

    public AdaptationMachine(IAdaptationHost host)
        : this(AdaptationState.Fitted, host)
    {
    }

    public AdaptationMachine(AdaptationState initialState, IAdaptationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        State = initialState;
        _host = host;
    }

    public AdaptationState State { get; private set; }

    /// <summary>true = 因模态会话占用而暂缓弹提示；会话关闭后经 Evaluate 补弹。</summary>
    public bool DeferredPromptPending { get; private set; }

    /// <summary>最近一次 NoFit 快照（路由器「Show 类输入改弹提示窗」的呈现数据）。</summary>
    public AdaptationSnapshot? LastNoFitSnapshot => _lastNoFit;

    /// <summary>评估入口（触发源四路 + 兜底统一汇入；调用方保证 UI 线程）。</summary>
    public void Evaluate(AdaptationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Fits)
        {
            if (State == AdaptationState.AdaptationNeeded)
            {
                // 恢复原显示条件：提示窗/调整窗由宿主在 Restore 内收口（§6.5）
                DeferredPromptPending = false;
                _lastNoFit = null;
                State = AdaptationState.Fitted;
                _host.DismissPrompt();
                _host.RestoreWallAfterAdaptation();
            }

            return; // Fitted + Fit：幂等无动作
        }

        _lastNoFit = snapshot;
        if (State == AdaptationState.Fitted)
        {
            State = AdaptationState.AdaptationNeeded;
            _host.HideWallForAdaptation(); // 取消手势 + 墙可见则 Request(Hide)（宿主内聚）
            if (_host.HasActiveSession)
            {
                DeferredPromptPending = true; // 有活动会话 → deferred，待 SessionClosed 再显
                return;
            }

            _host.ShowPrompt(snapshot);
            return;
        }

        // AdaptationNeeded + 仍是 NoFit：数字更新（窗开着才刷）；deferred 且会话已清 → 现在补弹
        if (DeferredPromptPending)
        {
            if (_host.HasActiveSession)
            {
                return; // 会话仍在：保持 deferred
            }

            DeferredPromptPending = false;
            _host.ShowPrompt(snapshot);
            return;
        }

        if (_host.IsPromptVisible)
        {
            _host.RefreshPrompt(snapshot);
        }
    }

    /// <summary>用户「稍后处理」：提示窗关、墙保持隐藏；deferred 清除（此后 Show 类输入由路由器改弹提示窗）。</summary>
    public void PromptDismissed()
    {
        DeferredPromptPending = false;
        if (State == AdaptationState.Fitted)
        {
            return; // 幂等防御（恢复与弹窗竞态时先到）
        }
    }

    /// <summary>调整布局确认成功（调用方已完成 CommitAdaptation 与配置采纳）：退出适配态。</summary>
    public void AdjustmentCommitted()
    {
        if (State != AdaptationState.AdaptationNeeded)
        {
            return; // 幂等防御
        }

        DeferredPromptPending = false;
        _lastNoFit = null;
        State = AdaptationState.Fitted;
        _host.DismissPrompt();
        _host.RestoreWallAfterAdaptation();
    }
}
