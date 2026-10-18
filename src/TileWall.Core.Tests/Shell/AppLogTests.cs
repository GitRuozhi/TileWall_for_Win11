using Xunit;

using TileWall.Core.Shell;

namespace TileWall.Core.Tests.Shell;

[CollectionDefinition("AppLog facade", DisableParallelization = true)]
public sealed class AppLogFacadeCollection { }

/// <summary>C4/F-07：日志引擎核心行为——等级过滤、线程安全队列、同步排空。</summary>
[Collection("AppLog facade")]
public sealed class AppLogTests
{
    private static AppLogEntry Entry(AppLogLevel level, string message) =>
        new(DateTimeOffset.Parse("2026-09-21T10:00:00+08:00"), level, "Test", message);

    [Fact]
    public void WARN_ERROR_默认落盘_INFO_默认关()
    {
        var sink = new CapturingSink();
        using var engine = new AppLogEngine(sink);
        engine.Write(AppLogLevel.Info, "Test", "不应出现");
        engine.Write(AppLogLevel.Warn, "Test", "警告");
        engine.Write(AppLogLevel.Error, "Test", "错误");
        engine.Flush();

        Assert.Equal(2, sink.Entries.Count); // INFO 默认关（InfoEnabledByDefault=false）
        Assert.All(sink.Entries, e => Assert.NotEqual(AppLogLevel.Info, e.Level));
        Assert.Equal([AppLogLevel.Warn, AppLogLevel.Error], sink.Entries.Select(e => e.Level).ToArray());
    }

    [Fact]
    public void 开启开关后_INFO_照常落盘()
    {
        var sink = new CapturingSink();
        using var engine = new AppLogEngine(sink, infoEnabled: true);
        engine.Write(AppLogLevel.Info, "Test", "提示");
        engine.Flush();

        var entry = Assert.Single(sink.Entries);
        Assert.Equal(AppLogLevel.Info, entry.Level);
        Assert.Equal("提示", entry.Message);
    }

    [Fact]
    public void 并发写不丢条目()
    {
        var sink = new CapturingSink();
        using var engine = new AppLogEngine(sink);
        const int tasks = 8;
        const int perTask = 200;
        Parallel.For(0, tasks, t =>
        {
            for (var i = 0; i < perTask; i++)
            {
                engine.Write(AppLogLevel.Warn, "Test", $"t{t}-{i}");
            }
        });
        engine.Flush();

        Assert.Equal(tasks * perTask, sink.Entries.Count);
        Assert.Equal(tasks * perTask, sink.Entries.Select(e => e.Message).Distinct().Count());
    }

    [Fact]
    public void 空分类空消息兜底不抛出()
    {
        var sink = new CapturingSink();
        using var engine = new AppLogEngine(sink);
        engine.Write(AppLogLevel.Error, string.Empty, null!);
        engine.Flush();

        var entry = Assert.Single(sink.Entries);
        Assert.Equal("App", entry.Category);
        Assert.Equal(string.Empty, entry.Message);
    }

    [Fact]
    public void 静态门面未初始化时无操作不抛出()
    {
        // 前置关闭避免其它静态门面测试遗留全局实例，保持本测试确实覆盖未初始化路径。
        AppLog.Shutdown();
        Assert.False(AppLog.IsInitialized);
        AppLog.Warn("Test", "无操作");
        AppLog.Error("Test", "无操作");
        AppLog.Info("Test", "无操作");
        AppLog.Flush();
        AppLog.Shutdown();
        Assert.False(AppLog.IsInitialized);
    }

    [Fact]
    public void Shutdown_排空队列并释放引擎_sink_晚到日志无操作且可重复调用()
    {
        AppLog.Shutdown();
        var sink = new CapturingSink();
        AppLog.Initialize(sink);

        const int expectedCount = 64;
        for (var i = 0; i < expectedCount; i++)
        {
            AppLog.Warn("Shutdown", $"queued-{i}");
        }

        try
        {
            AppLog.Shutdown();
            AppLog.Shutdown();
            AppLog.Warn("Shutdown", "late-warn");
            AppLog.Error("Shutdown", "late-error");
            AppLog.Info("Shutdown", "late-info");
            AppLog.Flush();

            Assert.False(AppLog.IsInitialized);
            Assert.True(sink.IsDisposed);
            Assert.Equal(expectedCount, sink.Entries.Count);
            Assert.DoesNotContain(sink.Entries, entry => entry.Message.StartsWith("late-", StringComparison.Ordinal));
        }
        finally
        {
            AppLog.Shutdown();
        }
    }

    [Fact]
    public async Task Shutdown_等待在途写结束并排空其间入队的记录()
    {
        AppLog.Shutdown();
        var sink = new BlockingSink();
        AppLog.Initialize(sink);
        try
        {
            AppLog.Warn("Shutdown", "in-flight");
            Assert.True(sink.FirstWriteStarted.Wait(TimeSpan.FromSeconds(5)), "后台 sink 没有开始首条写入。");
            const int queuedCount = 64;
            for (var i = 0; i < queuedCount; i++)
            {
                AppLog.Warn("Shutdown", $"queued-{i}");
            }

            var shutdown = Task.Run(AppLog.Shutdown);
            await Assert.ThrowsAsync<TimeoutException>(() => shutdown.WaitAsync(TimeSpan.FromMilliseconds(50)));
            sink.ReleaseFirstWrite.Set();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(queuedCount + 1, sink.Entries.Count);
            Assert.True(sink.IsDisposed);
        }
        finally
        {
            sink.ReleaseFirstWrite.Set();
            AppLog.Shutdown();
            sink.DisposeWaitHandles();
        }
    }

    [Fact]
    public void Shutdown_关闭真实滚动文件句柄后新sink可立即追加()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tilewall-applog-shutdown-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppLog.Shutdown();
            AppLog.Initialize(new RollingFileSink(directory));
            AppLog.Warn("Shutdown", "before-shutdown");

            AppLog.Shutdown();
            AppLog.Warn("Shutdown", "late-after-shutdown");
            AppLog.Shutdown();

            using (var replacement = new RollingFileSink(directory))
            {
                replacement.Write(Entry(AppLogLevel.Warn, "replacement-after-shutdown"));
                Assert.False(replacement.Abandoned);
            }

            var lines = File.ReadAllLines(Path.Combine(directory, "tilewall.log"));
            Assert.Equal(2, lines.Length);
            Assert.Contains("before-shutdown", lines[0]);
            Assert.Contains("replacement-after-shutdown", lines[1]);
            Assert.DoesNotContain("late-after-shutdown", string.Join('\n', lines));
            Assert.False(AppLog.IsInitialized);
        }
        finally
        {
            AppLog.Shutdown();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void FormatLine_单条单行_换行折叠()
    {
        var line = RollingFileSink.FormatLine(Entry(AppLogLevel.Warn, "第一行\n第二行\r\n第三行"));
        Assert.EndsWith("[WARN ] [Test] 第一行\\n第二行\\n第三行\n", line);
        Assert.Equal(1, line.Count(c => c == '\n'));
    }

    private sealed class CapturingSink : ILogSink, IDisposable
    {
        private readonly object _gate = new();
        private bool _disposed;

        public List<AppLogEntry> Entries { get; } = [];

        public bool IsDisposed
        {
            get
            {
                lock (_gate)
                {
                    return _disposed;
                }
            }
        }

        public void Write(AppLogEntry entry)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(CapturingSink));
                }

                Entries.Add(entry);
            }
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
            }
        }
    }

    private sealed class BlockingSink : ILogSink, IDisposable
    {
        private readonly object _gate = new();
        private int _blockNextWrite = 1;
        private bool _disposed;

        public ManualResetEventSlim FirstWriteStarted { get; } = new(false);
        public ManualResetEventSlim ReleaseFirstWrite { get; } = new(false);
        public List<AppLogEntry> Entries { get; } = [];

        public bool IsDisposed
        {
            get
            {
                lock (_gate)
                {
                    return _disposed;
                }
            }
        }

        public void Write(AppLogEntry entry)
        {
            if (Interlocked.Exchange(ref _blockNextWrite, 0) == 1)
            {
                FirstWriteStarted.Set();
                ReleaseFirstWrite.Wait();
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(BlockingSink));
                }

                Entries.Add(entry);
            }
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
            }
        }

        public void DisposeWaitHandles()
        {
            FirstWriteStarted.Dispose();
            ReleaseFirstWrite.Dispose();
        }
    }
}
