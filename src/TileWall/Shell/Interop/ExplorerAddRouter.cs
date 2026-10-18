using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

using TileWall.Core.Shell;

namespace TileWall.Shell.Interop;

/// <summary>
/// Explorer「添加到 TileWall」的进程间送达（M9 设计 §4.4）：
/// 已运行（本模式宿主窗存在）→ WM_COPYDATA 投递一次；未运行 → 按打包模式冷启动自身进添加流程
/// （`--add`，绝不自动执行被添加目标，§14.1）：packaged 经系统激活（IApplicationActivationManager、
/// AUMID=&lt;PFN&gt;!App）保留包身份，unpackaged 直启 exe。
/// C2/F-03：任一步失败一律记日志后放弃——packaged 激活失败**不再退化直启**（无身份进程会落进
/// unpackaged 数据目录形成「影子实例」）；宿主存在但投递未送达也不再冷启动（二次启动只会再投一次
/// 并再次失败）。菜单命令静默收场（不弹 Explorer 通用错误 UI），失败面由 C4 日志承载。
/// 路由决策本体在 Core（ExplorerAddRoutePolicy，可测）；本类只执行动作与落日志。
/// </summary>
internal static class ExplorerAddRouter
{
    public static void Route(IReadOnlyList<string> paths)
    {
        try
        {
            RouteCore(paths);
        }
        catch (Exception ex)
        {
            var message = $"Explorer「添加到 TileWall」路由异常，本次添加已放弃。原因：{ex}";
            AppLog.Error("ExplorerAdd", message);
            WriteRouteFailureEvent(message);
        }
    }

    private static void RouteCore(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var routable = ExplorerAddPathClassifier.PrepareForRouting(paths);
        var filtered = routable.Count != paths.Count;
        var identity = ApplicationModel.RuntimeScope.FromArguments([]).ShellWindowName;
        var plan = ExplorerAddRoutePolicy.Plan(IsPackagedProcess, ExplorerAddChannel.TryFindHostWindow(identity, out var hwnd));
        switch (plan)
        {
            case ExplorerAddRouteAction.DeliverToHost:
                if (ExplorerAddChannel.TryPostPaths(hwnd, routable, filtered))
                {
                    AppLog.Info("ExplorerAdd", $"已投递 {routable.Count} 条路径至运行中的实例");
                    return;
                }

                var message = $"未送达：宿主窗存在但 WM_COPYDATA 投递失败（拒绝/超时），放弃（{routable.Count} 条路径）";
                AppLog.Warn("ExplorerAdd", message);
                WriteRouteFailureEvent(message);
                return;
            case ExplorerAddRouteAction.ActivatePackagedSelf:
#if TILEWALL_PACKAGED
                LaunchSelfPackaged(routable, filtered); // unpackaged 编译下该动作不可达（Plan(packaged:false) 不产出）
#endif
                return;
            case ExplorerAddRouteAction.LaunchUnpackagedSelf:
                LaunchSelfUnpackaged(routable, filtered);
                return;
            default:
                // Plan 不产出放弃态；防御性兜底（不可达）
                return;
        }
    }

    private static bool IsPackagedProcess
    {
#if TILEWALL_PACKAGED
        get => true;
#else
        get => false;
#endif
    }

    private static string BuildArguments(IReadOnlyList<string> paths, bool filtered)
    {
        var (arguments, _, truncated) = ExplorerAddCommandLine.Build(paths);
        if (truncated || filtered)
        {
            arguments = $"{arguments} {ExplorerAddCommandLine.TruncatedMarker}"; // 墙上提示「请分批添加」
        }

        return arguments;
    }

#if TILEWALL_PACKAGED
    /// <summary>packaged 冷启动：仅系统激活（保留包身份）。失败 → ERROR 日志 + 放弃（绝不直启 exe）。</summary>
    private static void LaunchSelfPackaged(IReadOnlyList<string> paths, bool filtered)
    {
        var arguments = BuildArguments(paths, filtered);
        try
        {
            var aumid = Windows.ApplicationModel.Package.Current.Id.FamilyName + "!App"; // Application Id="App"
            var activator = (IApplicationActivationManager)(object)new ApplicationActivationManagerCom();
            var hr = activator.ActivateApplication(aumid, arguments, options: 0, out _);
            if (hr >= 0)
            {
                AppLog.Info("ExplorerAdd", $"packaged 已激活主实例（{aumid}）");
                return;
            }

            var message = $"packaged 激活失败（hr=0x{hr:X8}），放弃投递：不退化直启 exe（避免无身份进程落入 unpackaged 数据目录形成影子实例）";
            AppLog.Error("ExplorerAdd", message);
            WriteRouteFailureEvent(message);
            return;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or PlatformNotSupportedException)
        {
            var message = $"packaged 激活异常（{ex.Message}），放弃投递：不退化直启 exe（避免影子实例）";
            AppLog.Error("ExplorerAdd", message);
            WriteRouteFailureEvent(message);
        }
    }
#endif

    private static void LaunchSelfUnpackaged(IReadOnlyList<string> paths, bool filtered)
    {
        var arguments = BuildArguments(paths, filtered);
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            const string message = "unpackaged 冷启动失败：无法取得进程可执行文件路径，放弃投递";
            AppLog.Error("ExplorerAdd", message);
            WriteRouteFailureEvent(message);
            return;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = false, // 不经 shell 关联，参数原样到达新进程
            });
            AppLog.Info("ExplorerAdd", "unpackaged 已冷启动自身进入添加流程");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            var message = $"unpackaged 冷启动失败（{ex.Message}），放弃投递";
            AppLog.Error("ExplorerAdd", message);
            WriteRouteFailureEvent(message);
        }
    }

    private static void WriteRouteFailureEvent(string message) =>
        AppLogSetup.WriteWindowsEventLogError($"TileWall：Explorer「添加到 TileWall」失败，本次添加已放弃。原因：{message}");
}
