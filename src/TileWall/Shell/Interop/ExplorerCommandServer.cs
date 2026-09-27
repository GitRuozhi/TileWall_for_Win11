using System.Runtime.InteropServices;

namespace TileWall.Shell.Interop;

/// <summary>
/// com:ExeServer 的类对象（IClassFactory）：Explorer 每次 CoCreateInstance 的入口。
/// 记录类对象发放、加锁和命令对象的外部连接，供服务器在无客户端后退出。
/// </summary>
internal sealed class ExplorerCommandClassFactory : IClassFactory
{
    private long _lastActivityUtcTicks = DateTime.UtcNow.Ticks; // 64 位读写原子，无需锁
    private int _lockCount;
    private int _externalConnections;

    /// <summary>最近一次 COM 活动（CreateInstance / LockServer）的 UTC ticks。</summary>
    public long LastActivityUtcTicks => Interlocked.Read(ref _lastActivityUtcTicks);
    public bool HasActiveClients => Volatile.Read(ref _lockCount) > 0 || Volatile.Read(ref _externalConnections) > 0;

    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        Touch();
        ppvObject = IntPtr.Zero;
        if (pUnkOuter != IntPtr.Zero)
        {
            return ExplorerCommandInterop.ClsEnoaggregation; // 非聚合实现
        }

        var instance = new TileWallExplorerCommand(this);
        if (riid == ExplorerCommandInterop.IidIUnknown)
        {
            ppvObject = Marshal.GetIUnknownForObject(instance);
        }
        else if (riid == ExplorerCommandInterop.IidIExplorerCommand)
        {
            ppvObject = Marshal.GetComInterfaceForObject(instance, typeof(IExplorerCommand));
        }
        else
        {
            return ExplorerCommandInterop.Enointerface; // 其余接口（IShellExtInit 等）不支持——CCW 自动拒
        }

        // 返回指针的引用计数归属调用方（CreateInstance 协议：成功即 +1）；instance 的 CCW 由该指针自持
        GC.KeepAlive(instance);
        Touch();
        return 0;
    }

    public int LockServer(bool fLock)
    {
        if (fLock)
        {
            Interlocked.Increment(ref _lockCount);
        }
        else
        {
            DecrementIfPositive(ref _lockCount);
        }
        Touch();
        return 0;
    }

    public void ExternalConnectionAdded()
    {
        Interlocked.Increment(ref _externalConnections);
        Touch();
    }

    public void ExternalConnectionReleased()
    {
        DecrementIfPositive(ref _externalConnections);
        Touch();
    }

    private static void DecrementIfPositive(ref int value)
    {
        int count;
        do
        {
            count = Volatile.Read(ref value);
            if (count == 0)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref value, count - 1, count) != count);
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityUtcTicks, DateTime.UtcNow.Ticks);
}

/// <summary>
/// -Embedding 进程模型（M9 设计 §4.1）：TileWall.exe 以 `-Embedding` 被 COM 激活拉起（独立于主实例），
/// 注册类对象（REGCLS_MULTIPLEUSE）→ STA 消息泵服务菜单查询/Invoke → 有限寿命后 CoRevokeClassObject → 退出
/// （客户端连接及 LockServer 均归零后静默 3 s；客户端持有菜单时进程继续服务）。
/// 红线（实现评审重点）：本进程不触碰单实例互斥体、不读数据目录、不创建任何窗口/托盘/XAML——
/// 菜单悬停绝不弹墙（§13.4），EntryRecovery.Sweep 不双跑。
/// </summary>
internal static class ExplorerCommandServer
{
    /// <summary>最后一次 COM 活动后的静默寿命。</summary>
    private static readonly TimeSpan IdleSilence = TimeSpan.FromSeconds(3);

    public static int Run()
    {
        // 显式初始化 COM（STA）：P/Invoke 不会隐式初始化；[STAThread] 只设定线程套间状态。
        // RPC_E_CHANGED_MODE（已被初始化为 MTA）不致命：仅失去 STA 派发语义，寿命逻辑照常。
        var init = ExplorerCommandInterop.CoInitializeEx(IntPtr.Zero, ExplorerCommandInterop.CoinitApartmentthreaded);
        var changedMode = init == ExplorerCommandInterop.RpcEChangedMode;
        if (init != 0 && init != ExplorerCommandInterop.SFalse && !changedMode)
        {
            return init;
        }

        var factory = new ExplorerCommandClassFactory();
        var clsid = new Guid(ExplorerCommandIds.ClsidString);
        var hr = ExplorerCommandInterop.CoRegisterClassObject(
            ref clsid,
            factory,
            ExplorerCommandInterop.ClsctxLocalServer,
            ExplorerCommandInterop.RegclsMultipleuse,
            out var cookie);
        if (hr < 0)
        {
            return hr; // 注册失败：探针式快速失败（S6 断言退出码与寿命）
        }

        try
        {
            PumpUntilLifetimeDue(factory, changedMode);
        }
        finally
        {
            _ = ExplorerCommandInterop.CoRevokeClassObject(cookie);
        }

        return 0;
    }

    /// <summary>
    /// 泵到寿命到期：有外部连接或类对象锁时继续服务；全部释放后静默 3 秒退出。
    /// （STA 下 COM 来电经消息泵进入类对象/命令实例）。changedMode（MTA）时来电不经消息泵，循环退化为纯定时器。
    /// </summary>
    private static void PumpUntilLifetimeDue(ExplorerCommandClassFactory factory, bool changedMode)
    {
        while (true)
        {
            var now = DateTime.UtcNow;
            var lastActivity = new DateTime(factory.LastActivityUtcTicks, DateTimeKind.Utc);
            var idle = now - lastActivity;
            var active = factory.HasActiveClients;
            if (!active && idle >= IdleSilence)
            {
                return;
            }

            var remaining = active ? TimeSpan.FromSeconds(1) : IdleSilence - idle;
            var waitMs = (uint)Math.Clamp((int)remaining.TotalMilliseconds, 1, int.MaxValue);
            if (changedMode)
            {
                Thread.Sleep((int)waitMs);
                continue;
            }

            var wait = ExplorerCommandInterop.MsgWaitForMultipleObjectsEx(
                0, IntPtr.Zero, waitMs, ExplorerCommandInterop.QsAllinput, ExplorerCommandInterop.MwmoInputavailable);
            if (wait == ExplorerCommandInterop.WaitObject0)
            {
                PumpAvailableMessages();
            }
        }
    }

    private static void PumpAvailableMessages()
    {
        while (ExplorerCommandInterop.PeekMessageW(
            out var message, IntPtr.Zero, 0, 0, ExplorerCommandInterop.PmRemove))
        {
            _ = ExplorerCommandInterop.TranslateMessage(ref message);
            _ = ExplorerCommandInterop.DispatchMessageW(ref message);
        }
    }
}
