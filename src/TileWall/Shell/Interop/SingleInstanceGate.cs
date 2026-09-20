using System.Runtime.InteropServices;

namespace TileWall.Shell.Interop;

/// <summary>
/// 二次启动信号通道（M7 设计 §7.2）：M8 的 Explorer「添加到 TileWall」需携带负载时，
/// 换成命名管道实现，首实例代码（只依赖本接口）不改。
/// </summary>
public interface ISingleInstanceChannel
{
    /// <summary>向首实例投递「激活」信号（显示或聚焦现有会话，不叠开第二个会话）。</summary>
    void PostActivate();

    /// <summary>首实例收到激活信号。</summary>
    event Action? Activated;
}

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
public sealed class SingleInstanceGate : ISingleInstanceChannel, IDisposable
{
    public const string MutexName = @"Local\TileWall.SingleInstance.v1";

    private static readonly TimeSpan LocateWaitBudget = TimeSpan.FromSeconds(2);

    private readonly List<IDisposable> _subscriptions = []; // BindHost 的订阅记录（Dispose 反挂）

    private IntPtr _mutex;

    private bool _owned;

    private bool _disposed;

    private SingleInstanceGate()
    {
    }

    /// <summary>首实例收到二次启动信号（App 转接 Router.ShowOrFocus）。</summary>
    public event Action? Activated;

    /// <summary>
    /// 抢占单实例身份。必须在任何文件/窗口副作用之前调用（§2.2 第 1 步）：
    /// NotifiedExisting 的进程不得触碰数据目录（Sweep 双跑会互相干扰 CommitJournal 语义）。
    /// </summary>
    public static SingleInstanceGate Acquire(out SingleInstanceAcquireResult result)
    {
        var gate = new SingleInstanceGate();
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
        _mutex = Win32Api.CreateMutexW(IntPtr.Zero, bInitialOwner: true, MutexName);
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
        var hwnd = Win32Api.FindWindowW(ShellMessageHost.WindowClassName, ShellMessageHost.WindowName);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var message = Win32Api.RegisterWindowMessageW("TileWall.SingleInstance.Activate");
        return Win32Api.PostMessageW(hwnd, message, IntPtr.Zero, IntPtr.Zero);
    }

    /// <inheritdoc cref="ISingleInstanceChannel.PostActivate"/>
    public void PostActivate() => NotifyExisting();

    /// <summary>
    /// 首实例装配：把宿主窗的激活消息转接为本通道的 Activated 事件
    /// （宿主窗在身份确定后才创建——二次启动进程不得创建同名宿主窗干扰 FindWindow）。
    /// </summary>
    public void BindHost(ShellMessageHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        void OnActivated() => Activated?.Invoke();
        host.ActivateRequested += OnActivated;
        _subscriptions.Add(new Subscription(() => host.ActivateRequested -= OnActivated));
    }

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
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
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

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose()
        {
            _unsubscribe?.Invoke();
            _unsubscribe = null;
        }
    }
}
