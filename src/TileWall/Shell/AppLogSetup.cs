using Microsoft.Win32;

using TileWall.Core.Shell;
using TileWall.Shell.Interop;

namespace TileWall.Shell;

/// <summary>
/// 仅取得SingleInstanceGate所有权的主实例装配滚动文件日志（含--add冷启动）。
/// 二次启动不打开同名 writer；前置失败仅使用已注册事件源兜底。
/// 请求的日志目录由 RuntimeScope.LogRoot 统一解析；显式测试根沿各自隔离目录。
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
            var fullLogsDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(logsDirectory));
            if (!string.Equals(Path.GetFileName(fullLogsDirectory), "logs", StringComparison.OrdinalIgnoreCase))
                throw new IOException("The process log directory must be the logs child of its data root.");
            var trustedRoot = Directory.GetParent(fullLogsDirectory)?.FullName
                ?? throw new IOException("The process log directory has no trusted data root.");

            // TILEWALL_LOG_INFO=1：主实例诊断期开启INFO；默认仍只WARN+。
            AppLog.Initialize(new RollingFileSink(fullLogsDirectory, "tilewall", trustedRoot: trustedRoot),
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
