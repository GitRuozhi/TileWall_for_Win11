using System.Collections.Concurrent;

namespace TileWall.Core.Shell;

/// <summary>日志等级（C4/F-07）：WARN/ERROR 默认落盘，INFO 由开关显式开启。</summary>
public enum AppLogLevel
{
    Info = 0,
    Warn = 1,
    Error = 2,
}

/// <summary>单条日志记录。Timestamp 由引擎在入队时刻打点（非落盘时刻）。</summary>
public sealed record AppLogEntry(DateTimeOffset Timestamp, AppLogLevel Level, string Category, string Message);

/// <summary>日志落地抽象（C4）：默认实现为滚动文件，测试用捕获实现断言内容。</summary>
public interface ILogSink
{
    void Write(AppLogEntry entry);

    /// <summary>把实现侧缓冲刷到介质（如FileStream缓冲）。失败由实现自行吞掉。</summary>
    void Flush();
}

/// <summary>
/// 日志引擎（C4/F-07）：线程安全队列 + 专用后台写线程。Write 永不抛出（失败路径记录日志时
/// 不得引入新的失败面），未初始化的静态门面同理为无操作。Flush 同步排空队列（测试确定性）。
/// 热路径约束：轮播 tick 等每秒路径不打常规日志，仅失败分支调用。
/// </summary>
public sealed class AppLogEngine : IDisposable
{
    /// <summary>后台写线程的排空轮询间隔。</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly ILogSink _sink;
    private readonly bool _infoEnabled;
    private readonly ConcurrentQueue<AppLogEntry> _queue = new();
    private readonly object _gate = new(); // 排空互斥：Flush 与后台线程互斥进入 sink
    private readonly object _lifecycleGate = new(); // 串行化入队与关闭，不受 sink IO 阻塞
    private readonly object _disposeGate = new(); // 并发 Dispose 返回前都等待 sink 释放完成
    private readonly Thread _worker;
    private volatile bool _stopRequested;
    private volatile bool _disposed;

    public AppLogEngine(ILogSink sink, bool infoEnabled = AppLog.InfoEnabledByDefault)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _infoEnabled = infoEnabled;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true, // 进程退出不因日志线程挂起
            Name = "TileWall.AppLog",
        };
        _worker.Start();
    }

    /// <summary>待落盘条数（诊断用）。</summary>
    public int PendingCount => _queue.Count;

    /// <summary>记一条日志：入队即返回；Info 受开关抑制。category/message 为空时按占位值兜底，绝不抛出。</summary>
    public void Write(AppLogLevel level, string category, string message)
    {
        if (level == AppLogLevel.Info && !_infoEnabled)
        {
            return;
        }

        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _queue.Enqueue(new AppLogEntry(
                DateTimeOffset.Now,
                level,
                string.IsNullOrWhiteSpace(category) ? "App" : category,
                message ?? string.Empty));
        }
    }

    /// <summary>同步排空队列到 sink（与后台写线程互斥）；返回时队列已空。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                DrainCore();
            }
        }
    }

    /// <summary>停止后台线程、排空队列并释放可释放的 sink。本引擎拥有传入 sink 的生命周期。</summary>
    public void Dispose()
    {
        lock (_disposeGate)
        {
            lock (_lifecycleGate)
            {
                if (_disposed)
                {
                    return;
                }

                // Reject new writes before joining the worker. Any write already enqueued
                // is drained below after the worker has stopped.
                _disposed = true;
                _stopRequested = true;
            }

            _worker.Join(); // Never close a sink while a background write could still be in flight.

            lock (_gate)
            {
                DrainCore();
                if (_sink is IDisposable disposableSink)
                {
                    try
                    {
                        disposableSink.Dispose();
                    }
                    catch (Exception)
                    {
                        // Shutdown must not make logging failures escape into the host.
                    }
                }
            }

        }
    }

    private void WorkerLoop()
    {
        while (!_stopRequested)
        {
            Thread.Sleep(PollInterval);
            lock (_gate)
            {
                if (_stopRequested)
                {
                    return;
                }

                DrainCore();
            }
        }
    }

    private void DrainCore()
    {
        var wrote = false;
        while (_queue.TryDequeue(out var entry))
        {
            try
            {
                _sink.Write(entry);
                wrote = true;
            }
            catch (Exception)
            {
                // 单条落地失败丢弃该条：日志绝不反过来杀死调用方
            }
        }

        if (wrote)
        {
            try
            {
                _sink.Flush();
            }
            catch (Exception)
            {
                // 同上
            }
        }
    }
}

/// <summary>进程级日志门面（C4）：UI 各失败路径经此落 WARN/ERROR；未初始化（如测试环境）一律无操作。</summary>
public static class AppLog
{
    /// <summary>INFO 等级默认关（常量开关）：失败链排查只依赖 WARN/ERROR。</summary>
    public const bool InfoEnabledByDefault = false;

    private static readonly object Gate = new();
    private static AppLogEngine? _engine;

    /// <summary>是否已初始化（诊断用）。</summary>
    public static bool IsInitialized
    {
        get
        {
            lock (Gate)
            {
                return _engine is not null;
            }
        }
    }

    /// <summary>初始化（幂等：重复调用释放旧引擎换新）。必须在任何可能失败的装配步骤之前调用。</summary>
    public static void Initialize(ILogSink sink, bool infoEnabled = InfoEnabledByDefault)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (Gate)
        {
            _engine?.Dispose();
            _engine = new AppLogEngine(sink, infoEnabled);
        }
    }

    /// <summary>停止日志后台线程并释放当前引擎的 sink；重复调用及未初始化时均无操作。</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            var engine = _engine;
            _engine = null;
            engine?.Dispose();
        }
    }

    /// <summary>排空待落盘条目（退出序列可调用；未初始化时无操作）。</summary>
    public static void Flush()
    {
        lock (Gate)
        {
            _engine?.Flush();
        }
    }

    /// <summary>记 INFO（默认关：未显式开启时不落盘）。</summary>
    public static void Info(string category, string message)
    {
        lock (Gate)
        {
            _engine?.Write(AppLogLevel.Info, category, message);
        }
    }

    /// <summary>记 WARN（默认落盘）。</summary>
    public static void Warn(string category, string message)
    {
        lock (Gate)
        {
            _engine?.Write(AppLogLevel.Warn, category, message);
        }
    }

    /// <summary>记 ERROR（默认落盘）。</summary>
    public static void Error(string category, string message)
    {
        lock (Gate)
        {
            _engine?.Write(AppLogLevel.Error, category, message);
        }
    }
}
