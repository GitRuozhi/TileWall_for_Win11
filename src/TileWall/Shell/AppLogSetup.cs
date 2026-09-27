using Microsoft.Win32;

using TileWall.Core.Shell;
using TileWall.Shell.Interop;

namespace TileWall.Shell;

/// <summary>
/// 进程级日志装配（C4/F-07）：TileWall.exe 的三种进程形态（主实例 / --add 冷启动 / -Embedding COM）
/// 统一在 Program.Main 最前初始化——COM 路由失败发生在主实例装配之前，COM 进程自身也必须可落盘。
/// 落盘位置：unpackaged = &lt;LOCALAPPDATA&gt;\TileWall\logs\；packaged = LocalFolder\logs\。
/// 初始化失败（磁盘满/权限）静默降级为无日志：日志系统绝不能反过来阻断启动。
/// </summary>
internal static class AppLogSetup
{
    /// <summary>Windows 事件日志兜底源（C2：日志初始化前失败的最后一道观测通道）。</summary>
    private const string EventLogSource = "TileWall";

    /// <summary>Application 事件日志的源注册键（源存在性检查：不存在则跳过——创建源需管理员）。</summary>
    private const string EventLogSourceKeyPath = @"SYSTEM\CurrentControlSet\Services\EventLog\Application\" + EventLogSource;

    /// <summary>初始化滚动文件日志（1 MB × 当前+3 份历史；INFO 默认关）。</summary>
    public static void InitializeForCurrentProcess(string logsDirectory)
    {
        try
        {
            // TILEWALL_LOG_INFO=1（任意进程形态可设）：诊断期开启 INFO 输入/状态日志；默认仍只 WARN+
            AppLog.Initialize(new RollingFileSink(logsDirectory, "tilewall"),
                infoEnabled: AppLog.InfoEnabledByDefault
                    || Environment.GetEnvironmentVariable("TILEWALL_LOG_INFO") == "1");
        }
        catch (Exception)
        {
            // 日志不可用 → 门面保持无操作；启动与业务不受影响（观测性让位于可用性）
        }
    }

    /// <summary>
    /// C2/F-03：日志初始化之前的失败兜底——写 Windows 事件日志一条（Application 日志、Error 级）。
    /// 源不存在（非管理员无法创建）或写入失败一律跳过不抛。
    /// </summary>
    public static void WriteWindowsEventLogError(string message)
    {
        try
        {
            using var sourceKey = Registry.LocalMachine.OpenSubKey(EventLogSourceKeyPath);
            if (sourceKey is null)
            {
                return; // 源不存在时跳过：不尝试创建（需管理员），绝不抛出
            }

            _ = Win32Api.TryReportApplicationEventError(EventLogSource, message);
        }
        catch (Exception)
        {
            // 事件日志同样不可用：彻底放弃（该场景下无任何观测通道可救）
        }
    }

}
