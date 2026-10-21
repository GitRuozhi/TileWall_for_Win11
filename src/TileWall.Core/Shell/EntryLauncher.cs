using System.ComponentModel;
using System.Diagnostics;
using TileWall.Core.Entries;

namespace TileWall.Core.Shell;

/// <summary>启动结果（M4 设计 §6.1）。</summary>
public enum LaunchOutcome
{
    /// <summary>系统接受启动请求 → 调用方收起墙（§12.1；不监视进程加载状态）。</summary>
    Launched,

    /// <summary>入口文件缺失 → 保留墙并提示。</summary>
    EntryMissing,

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
/// F-04/SC-08 增量：三个入口只接受托管目录内的规范化完整路径
///（&lt;root&gt;\Objects\&lt;对象Id&gt;\&lt;名&gt;.lnk|.url，经 <see cref="EntryPathGuard.IsManagedEntryFullPath"/> 判定）；
/// 越界路径一律返回 LaunchFailed，不触碰文件系统。
/// </summary>
public sealed class EntryLauncher
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED：UAC 用户取消
    private const string ManagedPathRejection = "入口路径不在托管目录（Objects/<对象>/）内，已拒绝。";
    private readonly Func<ProcessStartInfo, Process?> _startProcess;

    public EntryLauncher() : this(Process.Start) { }

    internal EntryLauncher(Func<ProcessStartInfo, Process?> startProcess) =>
        _startProcess = startProcess ?? throw new ArgumentNullException(nameof(startProcess));

    /// <summary>默认启动：验证托管入口文件，然后将完整副本交给 Windows Shell 解析。</summary>
    public LaunchResult Launch(string entryFullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryFullPath);

        if (!EntryPathGuard.IsManagedEntryFullPath(entryFullPath))
        {
            return new LaunchResult(LaunchOutcome.LaunchFailed, ManagedPathRejection);
        }

        if (!File.Exists(entryFullPath))
        {
            return new LaunchResult(LaunchOutcome.EntryMissing);
        }

        try
        {
            using var process = _startProcess(new ProcessStartInfo(entryFullPath) { UseShellExecute = true });
            return new LaunchResult(LaunchOutcome.Launched);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            return new LaunchResult(LaunchOutcome.LaunchFailed, ex.Message);
        }
    }

    /// <summary>「以管理员身份启动」：托管路径守卫 → Verb=runas；用户取消 UAC → ElevatedDeclined（不重试不改配置 [R4]）。</summary>
    public LaunchResult LaunchElevated(string entryFullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryFullPath);

        if (!EntryPathGuard.IsManagedEntryFullPath(entryFullPath))
        {
            return new LaunchResult(LaunchOutcome.LaunchFailed, ManagedPathRejection);
        }

        if (!File.Exists(entryFullPath))
        {
            return new LaunchResult(LaunchOutcome.EntryMissing);
        }

        try
        {
            using var process = _startProcess(new ProcessStartInfo(entryFullPath) { UseShellExecute = true, Verb = "runas" });
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

    /// <summary>Resolve the managed entry to its real filesystem target; URLs and shell-only targets have no location.</summary>
    public LaunchResult RevealInExplorer(string entryFullPath) => RevealInExplorer(entryFullPath, new ShellLinkFileService());

    public static string? ResolveLocalTarget(string? entryFullPath, ILnkFileService links)
    {
        if (entryFullPath is null || !EntryPathGuard.IsManagedEntryFullPath(entryFullPath) || !File.Exists(entryFullPath)) return null;
        try
        {
            var path = entryFullPath;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var depth = 0; depth < 16; depth++)
            {
                if (!seen.Add(path)) return null;
                if (Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase)) return null;
                if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                    return File.Exists(path) || Directory.Exists(path) ? path : null;
                var target = links.Read(path).TargetPath;
                if (string.IsNullOrWhiteSpace(target)) return null;
                target = Environment.ExpandEnvironmentVariables(target);
                if (!Path.IsPathFullyQualified(target)) return null;
                path = LaunchIdentityService.NormalizePath(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            System.Runtime.InteropServices.COMException or NotSupportedException) { }
        return null;
    }

    public LaunchResult RevealInExplorer(string entryFullPath, ILnkFileService links)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryFullPath);
        if (!EntryPathGuard.IsManagedEntryFullPath(entryFullPath))
        {
            return new LaunchResult(LaunchOutcome.LaunchFailed, ManagedPathRejection);
        }

        if (!File.Exists(entryFullPath))
        {
            return new LaunchResult(LaunchOutcome.EntryMissing);
        }

        try
        {
            var target = ResolveLocalTarget(entryFullPath, links);
            if (target is null) return new(LaunchOutcome.LaunchFailed, "LOCAL_TARGET_UNAVAILABLE");
            using var process = _startProcess(new ProcessStartInfo("explorer.exe",
                Directory.Exists(target) ? $"\"{target}\"" : $"/select,\"{target}\"") { UseShellExecute = true });
            return new LaunchResult(LaunchOutcome.Launched);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            return new LaunchResult(LaunchOutcome.LaunchFailed, ex.Message);
        }
    }
}
