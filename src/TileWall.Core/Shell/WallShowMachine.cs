namespace TileWall.Core.Shell;

/// <summary>墙显隐状态（M7 设计 §6.1 四态）。</summary>
public enum WallShowState
{
    Hidden,
    Showing,
    Visible,
    Hiding,
}

/// <summary>显隐触发来源（Toggle=热键；Show=托盘双击/二次启动/预置模态；Hide=Esc/失焦/点击启动收墙）。</summary>
public enum WallShowTrigger
{
    Toggle,
    Show,
    Hide,
}

/// <summary>
/// 状态机出口（物理显隐与动画的唯一执行者，MainWindow/ShowHideAnimator 实现）。
/// 物理终态只经 SnapShow/SnapHide 发生（AppWindow.Show/Hide）；动画是视觉叠加——
/// 可见性在动画首帧即成立（SnapShow 先行）、末帧才撤销（HideAnimationCompleted 后才 SnapHide），W2。
/// </summary>
public interface IWallShowHost
{
    /// <summary>A6：200ms 淡入 + 上移（动画期间物理已可见）；完成后宿主回调 <see cref="WallShowMachine.ShowAnimationCompleted"/>。</summary>
    void BeginShowAnimation();

    /// <summary>A7：150ms 淡出（动画期间物理仍可见，轮播哑闹钟仍在跑）；完成后回调 <see cref="WallShowMachine.HideAnimationCompleted"/>。</summary>
    void BeginHideAnimation();

    /// <summary>A8 打断：取消在飞动画（不排队、不补播反向动画，W1）。</summary>
    void CancelAnimations();

    /// <summary>无动画直达可见：AppWindow.Show()（或延迟 Activate 的首次显示）。</summary>
    void SnapShow();

    /// <summary>无动画直达隐藏：AppWindow.Hide()（渲染停止点唯一，W2）。</summary>
    void SnapHide();

    /// <summary>Visible 态托盘双击的「聚焦不收起」。</summary>
    void FocusWall();
}

/// <summary>
/// 显隐四态状态机（M7 设计 §6.1 转移表；纯 C#，风格同 GestureMachine——枚举 + 转移表即实现）。
/// A8「快速连按不排队、以显隐结果为准」体现为 Showing/Hiding 的打断格：新输入直接取消在飞动画落反向终态。
/// 模态防御：模态会话期间路由器不投递（W3），<see cref="InputGateClosed"/> 是状态机侧的最后防线。
/// </summary>
public sealed class WallShowMachine
{
    private readonly IWallShowHost _host;

    public WallShowMachine(WallShowState initialState, IWallShowHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        State = initialState;
        _host = host;
    }

    public WallShowState State { get; private set; }

    /// <summary>
    /// W3 防御行：置真时 Toggle/Hide 输入忽略（模态会话期由路由器同步置位）。
    /// 设计 §6.1 原文只列「Router 不向状态机投递 Toggle/Hide」；Show 必须放行——
    /// 「预置模态」序列（§8.5：Attach 置 ModalActive → 显示墙为背景 → ActivateTop）是路由器
    /// 自有的同步编排，墙恢复背景正是该序列的第 2 步，若被门控吞掉则设置窗无墙背景（§3.4）。
    /// 用户输入无任何路径在模态期投递 Show（热键=Toggle、双击/二次启动在路由器层已转聚焦）。
    /// </summary>
    public bool InputGateClosed { get; set; }

    /// <summary>投递触发（热键/托盘/失焦/Esc 统一入口）。幂等格与防御格零宿主动作。</summary>
    public void Request(WallShowTrigger trigger)
    {
        if (InputGateClosed && trigger != WallShowTrigger.Show)
        {
            return; // W3：模态防御行（只挡 Toggle/Hide）
        }

        switch (State)
        {
            case WallShowState.Hidden:
                switch (trigger)
                {
                    case WallShowTrigger.Toggle:
                    case WallShowTrigger.Show:
                        State = WallShowState.Showing;
                        _host.SnapShow(); // 物理可见先成立（W2），动画随后叠加
                        _host.BeginShowAnimation();
                        break;
                    case WallShowTrigger.Hide:
                        return; // 幂等
                }

                break;
            case WallShowState.Showing:
                // A8 打断：取消在飞动画，落最终态，不排队
                _host.CancelAnimations();
                switch (trigger)
                {
                    case WallShowTrigger.Toggle:
                    case WallShowTrigger.Hide:
                        State = WallShowState.Hidden;
                        _host.SnapHide();
                        break;
                    case WallShowTrigger.Show:
                        State = WallShowState.Visible;
                        _host.SnapShow();
                        break;
                }

                break;
            case WallShowState.Visible:
                switch (trigger)
                {
                    case WallShowTrigger.Toggle:
                    case WallShowTrigger.Hide:
                        State = WallShowState.Hiding;
                        _host.BeginHideAnimation(); // 物理仍可见（§6.3 A7 顺序保证）
                        break;
                    case WallShowTrigger.Show:
                        _host.FocusWall(); // 托盘双击/二次启动：聚焦不收起，不转移
                        break;
                }

                break;
            case WallShowState.Hiding:
                switch (trigger)
                {
                    case WallShowTrigger.Toggle:
                    case WallShowTrigger.Show:
                        _host.CancelAnimations(); // A8 打断收起 → 直达可见
                        State = WallShowState.Visible;
                        _host.SnapShow();
                        break;
                    case WallShowTrigger.Hide:
                        return; // 幂等（已在收起）
                }

                break;
        }
    }

    /// <summary>宿主在 A6 动画完成后回调；仅 Showing 态受理（过期回调忽略，W1）。</summary>
    public void ShowAnimationCompleted()
    {
        if (State == WallShowState.Showing)
        {
            State = WallShowState.Visible;
        }
    }

    /// <summary>宿主在 A7 动画完成后回调：此处才撤销物理可见（恰好一次 SnapHide，W2）。</summary>
    public void HideAnimationCompleted()
    {
        if (State != WallShowState.Hiding)
        {
            return;
        }

        State = WallShowState.Hidden;
        _host.SnapHide();
    }
}
