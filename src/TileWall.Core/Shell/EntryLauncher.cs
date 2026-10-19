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

    /// <summary>「打开文件位置」：定位托管入口文件（非原始目录，§12.4）；同步失败返回可显示结果。</summary>
    public LaunchResult RevealInExplorer(string entryFullPath)
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
            using var process = _startProcess(new ProcessStartInfo("explorer.exe", $"/select,\"{entryFullPath}\"") { UseShellExecute = true });
            return new LaunchResult(LaunchOutcome.Launched);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            return new LaunchResult(LaunchOutcome.LaunchFailed, ex.Message);
        }
    }
}
