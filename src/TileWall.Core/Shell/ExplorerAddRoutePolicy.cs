namespace TileWall.Core.Shell;

/// <summary>
/// Explorer「添加到 TileWall」的路由动作（C2/F-03）：宿主窗在 → 投递一次；
/// 无宿主按打包模式选择冷启动通道；任一步失败 → 放弃并记日志，**绝不退化直启 exe**——
/// packaged 直启会以无身份进程落进 unpackaged 数据目录（「影子实例」读另一套数据）；
/// 宿主存在但投递失败时冷启动同样无意义（二次启动只会再投一次并失败）。
/// </summary>
public enum ExplorerAddRouteAction
{
    /// <summary>宿主窗存在：WM_COPYDATA 投递一次。</summary>
    DeliverToHost,

    /// <summary>packaged 无宿主：经系统激活（AUMID）冷启动自身，保留包身份。</summary>
    ActivatePackagedSelf,

    /// <summary>unpackaged 无宿主：直启自身冷启动（--add 参数）。</summary>
    LaunchUnpackagedSelf,

    /// <summary>投递失败（接收方拒绝/超时/不在）：记 WARN 后放弃。</summary>
    AbandonDeliveryFailed,

    /// <summary>packaged 激活失败：记 ERROR 后放弃（不直启 exe）。</summary>
    AbandonPackagedActivationFailed,

    /// <summary>unpackaged 冷启动失败：记 ERROR 后放弃。</summary>
    AbandonUnpackagedLaunchFailed,
}

/// <summary>
/// Explorer 添加路由的纯判定核（C2/F-03）：进程内路由决策下沉 Core 可测；
/// Win32（FindWindow/SendMessage/激活管理器）留在 UI Interop，只执行本判定给出的动作。
/// 注意与 <see cref="ExplorerAddRouter"/>（启动阶段三态路由，A10）区分：那管「到达后怎么处理」，
/// 本类管「进程间怎么送达」。
/// </summary>
public static class ExplorerAddRoutePolicy
{
    /// <summary>初判：无宿主时按打包模式选择冷启动通道。</summary>
    public static ExplorerAddRouteAction Plan(bool packaged, bool hostWindowFound)
    {
        if (hostWindowFound)
        {
            return ExplorerAddRouteAction.DeliverToHost;
        }

        return packaged
            ? ExplorerAddRouteAction.ActivatePackagedSelf
            : ExplorerAddRouteAction.LaunchUnpackagedSelf;
    }

    /// <summary>
    /// 所计划动作执行失败的收口：三路各对应一个「放弃」终态。
    /// 关键断言点：ActivatePackagedSelf 的失败态**不是** LaunchUnpackagedSelf——
    /// 退化直启正是 F-03 影子实例的成因。
    /// </summary>
    public static ExplorerAddRouteAction ResolveStepFailure(ExplorerAddRouteAction planned) => planned switch
    {
        ExplorerAddRouteAction.DeliverToHost => ExplorerAddRouteAction.AbandonDeliveryFailed,
        ExplorerAddRouteAction.ActivatePackagedSelf => ExplorerAddRouteAction.AbandonPackagedActivationFailed,
        ExplorerAddRouteAction.LaunchUnpackagedSelf => ExplorerAddRouteAction.AbandonUnpackagedLaunchFailed,
        _ => planned,
    };
}
