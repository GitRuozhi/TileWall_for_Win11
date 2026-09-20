using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TileWall.Core.Entries;

namespace TileWall.Shell;

/// <summary>启动结果（M4 设计 §6.1）。</summary>
public enum LaunchOutcome
{
    /// <summary>系统接受启动请求 → 调用方收起墙（§12.1；不监视进程加载状态）。</summary>
    Launched,

    /// <summary>入口文件缺失 → 保留墙并提示。</summary>
    EntryMissing,

    /// <summary>普通路径目标缺失（预检拒绝）→ 保留墙并提示；不给 Shell 弹错框的机会。</summary>
    TargetMissing,

    /// <summary>ShellExecute 抛异常（UAC 取消除外）→ 保留墙并提示原因。</summary>
    LaunchFailed,

    /// <summary>runas 且用户取消 UAC（Win32Exception 1223）→ 不重试、不改配置 [R4]。</summary>
    ElevatedDeclined,
}

/// <param name="Outcome">结果枚举。</param>
/// <param name="FailureReason">LaunchFailed 时的系统原因文本（提示「启动失败：&lt;原因&gt;」）。</param>
public sealed record LaunchResult(LaunchOutcome Outcome, string? FailureReason = null);

/// <summary>
/// 点击启动 / 管理员启动 / 打开文件位置（M4 设计 §6；范围 B）。主程序恒为普通权限，
/// 提权只作用于 ShellExecute 派生的子进程（§12.4）。
/// </summary>
public sealed class EntryLauncher
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED：UAC 用户取消

    /// <summary>默认启动：入口存在性 → .lnk 目标存在性预检（HasIdList 特殊项跳过交系统处理）→ ShellExecute。</summary>
    public LaunchResult Launch(string entryFullPath, ILnkFileService linkFiles)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryFullPath);
        ArgumentNullException.ThrowIfNull(linkFiles);

        if (!File.Exists(entryFullPath))
        {
            return new LaunchResult(LaunchOutcome.EntryMissing);
        }

        if (EntryNames.KindOfRelativePath(entryFullPath) == EntryKind.Lnk)
        {
            try
            {
                var fields = linkFiles.Read(entryFullPath);
                if (!fields.HasIdList && !string.IsNullOrEmpty(fields.TargetPath))
                {
                    // 目标可为文件或文件夹（CreateForPath 支持文件夹入口，§6.1）→ Path.Exists 两者皆认；
                    // 环境变量路径（%windir%\...）先展开再判，避免预检同类误判
                    var target = ShellLinkFileService.NormalizePath(Environment.ExpandEnvironmentVariables(fields.TargetPath));
                    if (!Path.Exists(target))
                    {
                        return new LaunchResult(LaunchOutcome.TargetMissing); // §12.1「立即失败保留墙」的可观察预检
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or FileNotFoundException or UnauthorizedAccessException or COMException)
            {
                // 预检读取失败（含损坏 .lnk 的 COMException）不阻断：交系统 ShellExecute 处理
            }
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(entryFullPath) { UseShellExecute = true });
            return new LaunchResult(LaunchOutcome.Launched);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            return new LaunchResult(LaunchOutcome.LaunchFailed, ex.Message);
        }
    }

    /// <summary>「以管理员身份启动」：Verb=runas；用户取消 UAC → ElevatedDeclined（不重试不改配置 [R4]）。</summary>
    public LaunchResult LaunchElevated(string entryFullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryFullPath);

        if (!File.Exists(entryFullPath))
        {
            return new LaunchResult(LaunchOutcome.EntryMissing);
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(entryFullPath) { UseShellExecute = true, Verb = "runas" });
            return new LaunchResult(LaunchOutcome.Launched);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new LaunchResult(LaunchOutcome.ElevatedDeclined);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            return new LaunchResult(LaunchOutcome.LaunchFailed, ex.Message);
        }
    }

    /// <summary>「打开文件位置」：explorer /select 定位托管入口文件（非原始目录，§12.4）；失败静默（资源管理器自身提示）。</summary>
    public void RevealInExplorer(string entryFullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryFullPath);
        if (!File.Exists(entryFullPath))
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entryFullPath}\"") { UseShellExecute = true });
    }
}
