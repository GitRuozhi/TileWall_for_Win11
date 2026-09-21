using System.Diagnostics;
using System.Runtime.InteropServices;
using TileWall.Core.Shell;

namespace TileWall.Shell.Interop;

/// <summary>
/// Explorer「添加到 TileWall」的路由（M9 设计 §4.4）：
/// 已运行（本模式宿主窗存在）→ WM_COPYDATA 投递；未运行 → 启动自身进添加流程（`--add`，
/// 绝不自动执行被添加目标，§14.1）。packaged 模式必须经系统激活（IApplicationActivationManager、
/// AUMID=&lt;PFN&gt;!App）保留包身份——直启 exe 会以无身份进程读 unpackaged 数据目录，破坏 §3.3 隔离。
/// 路由失败静默返回（菜单命令不得弹 Explorer 通用错误 UI）。
/// </summary>
internal static class ExplorerAddRouter
{
    public static void Route(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var routable = ExplorerAddPathClassifier.PrepareForRouting(paths);
        if (ExplorerAddChannel.TryFindHostWindow(out var hwnd)
            && ExplorerAddChannel.TryPostPaths(hwnd, routable))
        {
            return;
        }

        LaunchSelf(routable);
    }

    private static void LaunchSelf(IReadOnlyList<string> paths)
    {
        var (arguments, _, truncated) = ExplorerAddCommandLine.Build(paths);
        if (truncated)
        {
            arguments = $"{arguments} {ExplorerAddCommandLine.TruncatedMarker}"; // 墙上提示「请分批添加」
        }

#if TILEWALL_PACKAGED
        LaunchSelfPackaged(arguments);
#else
        LaunchSelfUnpackaged(arguments);
#endif
    }

#if TILEWALL_PACKAGED
    private static void LaunchSelfPackaged(string arguments)
    {
        try
        {
            var aumid = Windows.ApplicationModel.Package.Current.Id.FamilyName + "!App"; // Application Id="App"
            var activator = (IApplicationActivationManager)(object)new ApplicationActivationManagerCom();
            var hr = activator.ActivateApplication(aumid, arguments, options: 0, out _);
            if (hr >= 0)
            {
                return;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or PlatformNotSupportedException)
        {
            // 身份/AUMID 不可得（异常环境）：退化直启，宁可落 unpackaged 数据目录也不静默吞掉添加手势
        }

        LaunchSelfUnpackaged(arguments);
    }
#endif

    private static void LaunchSelfUnpackaged(string arguments)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
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
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 冷启动失败：无 UI 可呈现，菜单命令静默收场（P5 观察项）
        }
    }
}
