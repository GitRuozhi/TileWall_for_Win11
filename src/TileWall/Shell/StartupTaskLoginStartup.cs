#if TILEWALL_PACKAGED
using TileWall.Core.Settings;
using Windows.ApplicationModel;

namespace TileWall.Shell;

/// <summary>
/// packaged 登录启动后端（M9 设计 §5.1）：manifest windows.startupTask 扩展（TaskId=
/// <see cref="TaskId"/>）+ Windows.ApplicationModel.StartupTask API，替代 Run 键——
/// Run 键写的是版本化 WindowsApps 路径，升级即失效、卸载即残留，正中「无残留注册」红线；
/// StartupTask 由系统随卸载自动回收。接口（ILoginStartup）零改动，按模式注入（App 装配）。
/// 注意平台规则：任务管理器里被用户禁用后应用侧无法再启用（DisabledByUser），须如实呈现失败原因，
/// 不得显示假成功。运行时全链（注册→登录→禁用→卸载）依赖真机安装，列 P5 人工项。
/// </summary>
public sealed class StartupTaskLoginStartup : ILoginStartup
{
    public const string TaskId = "TileWallLoginStartup";

    public bool IsEnabled
    {
        get
        {
            try
            {
                var state = StartupTask.GetAsync(TaskId).AsTask().GetAwaiter().GetResult().State;
                return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }
            catch (Exception)
            {
                return false; // 读失败按「未开启」呈现，不假装成功（与 RegistryLoginStartup 同语义）
            }
        }
    }

    public bool SetEnabled(bool enabled, out string? error)
    {
        error = null;
        try
        {
            var task = StartupTask.GetAsync(TaskId).AsTask().GetAwaiter().GetResult();
            if (!enabled)
            {
                task.Disable(); // 同步 API；系统随请求生效
                return true;
            }

            // 首次启用可能弹系统确认；非 Enabled 结论一律如实报原因（设置项不显示假成功）
            var result = task.RequestEnableAsync().AsTask().GetAwaiter().GetResult();
            if (result is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
            {
                return true;
            }

            error = result == StartupTaskState.DisabledByUser
                ? "登录启动已被任务管理器或系统禁用，无法在应用内重新启用；可在任务管理器的「启动应用」页重新允许。"
                : $"系统未允许启用登录启动（状态：{result}）。";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
#endif
