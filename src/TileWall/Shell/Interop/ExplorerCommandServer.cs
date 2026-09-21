using System.Runtime.InteropServices;

namespace TileWall.Shell.Interop;

/// <summary>
/// com:ExeServer 的类对象（IClassFactory）：Explorer 每次 CoCreateInstance 的入口。
/// 记录「类对象发放/加锁」作为活动信号，供 <see cref="ExplorerCommandServer"/> 的有限寿命判定
/// （最后一次发放后静默 3 s 退出；30 s 硬上限兜底，M9 设计 §4.1）。
/// </summary>
internal sealed class ExplorerCommandClassFactory : IClassFactory
{
    private long _lastActivityUtcTicks = DateTime.UtcNow.Ticks; // 64 位读写原子，无需锁
    private int _lockCount;

    /// <summary>最近一次 COM 活动（CreateInstance / LockServer）的 UTC ticks。</summary>
    public long LastActivityUtcTicks => Interlocked.Read(ref _lastActivityUtcTicks);

    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        Touch();
        ppvObject = IntPtr.Zero;
        if (pUnkOuter != IntPtr.Zero)
        {
            return ExplorerCommandInterop.ClsEnoaggregation; // 非聚合实现
        }

        var instance = new TileWallExplorerCommand();
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
        _ = Interlocked.Add(ref _lockCount, fLock ? 1 : -1);
        Touch();
        return 0;
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityUtcTicks, DateTime.UtcNow.Ticks);
}

/// <summary>
/// -Embedding 进程模型（M9 设计 §4.1）：TileWall.exe 以 `-Embedding` 被 COM 激活拉起（独立于主实例），
/// 注册类对象（REGCLS_MULTIPLEUSE）→ STA 消息泵服务菜单查询/Invoke → 有限寿命后 CoRevokeClassObject → 退出
/// （引用归零静默 3 s 或硬上限 30 s，无常驻 [§18.2]）。
/// 红线（实现评审重点）：本进程不触碰单实例互斥体、不读数据目录、不创建任何窗口/托盘/XAML——
/// 菜单悬停绝不弹墙（§13.4），EntryRecovery.Sweep 不双跑。
/// </summary>
internal static class ExplorerCommandServer
{
    /// <summary>最后一次 COM 活动后的静默寿命。</summary>
    private static readonly TimeSpan IdleSilence = TimeSpan.FromSeconds(3);

    /// <summary>注册起的硬上限（活动持续刷新也不得超过）。</summary>
    private static readonly TimeSpan HardLifetime = TimeSpan.FromSeconds(30);

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
    /// 泵到寿命到期：每轮按「静默到期 / 硬上限到期」取最近期限做带超时等待，输入可达即派发
    /// （STA 下 COM 来电经消息泵进入类对象/命令实例）。changedMode（MTA）时来电不经消息泵，循环退化为纯定时器。
    /// </summary>
    private static void PumpUntilLifetimeDue(ExplorerCommandClassFactory factory, bool changedMode)
    {
        var startedUtc = DateTime.UtcNow;
        while (true)
        {
            var now = DateTime.UtcNow;
            var elapsed = now - startedUtc;
            if (elapsed >= HardLifetime)
            {
                return; // 硬上限：即使持续有活动也收摊（重激活由系统再拉起新 -Embedding 进程）
            }

            var lastActivity = new DateTime(factory.LastActivityUtcTicks, DateTimeKind.Utc);
            var idle = now - lastActivity;
            if (idle >= IdleSilence)
            {
                return; // 引用静默：最后一次发放/加锁后 3 s 无活动
            }

            var remaining = TimeSpan.FromTicks(Math.Min(
                IdleSilence.Ticks - idle.Ticks,
                HardLifetime.Ticks - elapsed.Ticks));
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
