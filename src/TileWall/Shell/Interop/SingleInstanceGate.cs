using System.Runtime.InteropServices;
using TileWall.Core.Shell;

namespace TileWall.Shell.Interop;

/// <summary>Acquire 结果：本进程成为首实例 / 已通知既有实例 / 限时未果（静默退出）。</summary>
public enum SingleInstanceAcquireResult
{
    Acquired,
    NotifiedExisting,
    GaveUp,
}

/// <summary>
/// 单实例门（M7 设计 §7.1）：命名互斥体 <see cref="MutexName"/>——「Local\」按登录会话隔离，
/// 快速用户切换下另一用户的会话互不误杀。二次启动 FindWindowW 宿主窗 + PostMessage 注册消息后
/// 干净退出（不建任何窗口、不触碰数据目录，EntryRecovery.Sweep 不双跑）；
/// 宿主窗找不到（首实例正在退出）→ 等待互斥体归属 ≤2s：获得则升级为首实例，超时静默退出（无 UI 僵局）。
/// 互斥体由内核随进程回收兜底；<see cref="Release"/> 是退出序列里的显式成对释放（双保险）。
/// </summary>
public sealed class SingleInstanceGate : IDisposable
{
#if TILEWALL_PACKAGED
    /// <summary>互斥体名（packaged 带 .pkg 后缀，M9 设计 §3.3）：两模式数据目录不同，身份必须隔离防互串。</summary>
    public const string MutexName = @"Local\TileWall.SingleInstance.pkg.v1";
#else
    public const string MutexName = @"Local\TileWall.SingleInstance.v1";
#endif

    private static readonly TimeSpan LocateWaitBudget = TimeSpan.FromSeconds(2);

    private IntPtr _mutex;

    private bool _owned;

    private bool _disposed;

    private readonly string _mutexName;
    private readonly string _windowName;
    private readonly bool _activateExisting;
    private SingleInstanceGate(string mutexName, string windowName, bool activateExisting)
    {
        _mutexName = mutexName;
        _windowName = windowName;
        _activateExisting = activateExisting;
    }

    /// <summary>
    /// 抢占单实例身份。必须在任何文件/窗口副作用之前调用（§2.2 第 1 步）：
    /// NotifiedExisting 的进程不得触碰数据目录（Sweep 双跑会互相干扰 CommitJournal 语义）。
    /// </summary>
    public static SingleInstanceGate Acquire(string mutexName, string windowName, bool activateExisting,
        out SingleInstanceAcquireResult result)
    {
        var gate = new SingleInstanceGate(mutexName, windowName, activateExisting);
        if (gate.TryOwn(out result))
        {
            return gate;
        }

        if (gate.NotifyExisting())
        {
            result = SingleInstanceAcquireResult.NotifiedExisting;
            return gate;
        }

        // 宿主窗找不到（首实例正在退出）：限时重试抢互斥体归属
        var deadline = DateTime.UtcNow + LocateWaitBudget;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
            if (gate.TryOwn(out var retry))
            {
                result = retry;
                return gate;
            }

            if (gate.NotifyExisting())
            {
                result = SingleInstanceAcquireResult.NotifiedExisting;
                return gate;
            }
        }

        result = SingleInstanceAcquireResult.GaveUp;
        return gate;
    }

    /// <summary>尝试创建并持有互斥体（bInitialOwner=true 保证 Release 的所有权语义）。</summary>
    private bool TryOwn(out SingleInstanceAcquireResult result)
    {
        CloseMutexHandle();
        _mutex = Win32Api.CreateMutexW(IntPtr.Zero, bInitialOwner: true, _mutexName);
        var error = Marshal.GetLastWin32Error();
        if (_mutex != IntPtr.Zero && error != Win32Api.ErrorAlreadyExists)
        {
            _owned = true;
            result = SingleInstanceAcquireResult.Acquired;
            return true;
        }

        result = SingleInstanceAcquireResult.GaveUp;
        return false;
    }

    /// <summary>找到首实例宿主窗并投递激活消息（注册消息系统级分配，跨进程同值）。</summary>
    private bool NotifyExisting()
    {
        var hwnd = Win32Api.FindWindowW(ShellMessageHost.WindowClassName, _windowName);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!_activateExisting) return true;
        if (Win32Api.GetWindowThreadProcessId(hwnd, out var ownerProcessId) == 0 || ownerProcessId == 0)
            return false;
        // A user-launched secondary process can transfer its foreground right to the existing
        // process before that process focuses its wall or current modal. Background starts stop above.
        if (Win32Api.AllowSetForegroundWindow(ownerProcessId))
            AppLog.Info("SingleInstance", $"Foreground permission transferred to process {ownerProcessId}.");
        else
            AppLog.Warn("SingleInstance", $"Foreground permission transfer failed for process {ownerProcessId}: {Marshal.GetLastWin32Error()}.");
        var message = Win32Api.RegisterWindowMessageW("TileWall.SingleInstance.Activate");
        return Win32Api.PostMessageW(hwnd, message, IntPtr.Zero, IntPtr.Zero);
    }

    public bool PostPaths(ExplorerAddMessage message) =>
        ExplorerAddChannel.TryFindHostWindow(_windowName, out var hwnd) &&
        ExplorerAddChannel.TryPostPaths(hwnd, message.Paths, message.Truncated);

    /// <summary>显式释放互斥体（ExitCoordinator 释放序的成对收口；异常/超时路径由内核兜底）。</summary>
    public void Release()
    {
        if (_owned && _mutex != IntPtr.Zero)
        {
            _ = Win32Api.ReleaseMutex(_mutex);
            _owned = false;
        }

        CloseMutexHandle();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release();
    }

    private void CloseMutexHandle()
    {
        if (_mutex != IntPtr.Zero)
        {
            _ = Win32Api.CloseHandle(_mutex);
            _mutex = IntPtr.Zero;
        }
    }

}
