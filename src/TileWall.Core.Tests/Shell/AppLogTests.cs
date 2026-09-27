using Xunit;

using TileWall.Core.Shell;

namespace TileWall.Core.Tests.Shell;

/// <summary>C4/F-07：日志引擎核心行为——等级过滤、线程安全队列、同步排空。</summary>
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
        // 只验证「未初始化也不炸」；Initialize 属进程级装配（UI 侧），不在单测污染全局态
        Assert.False(AppLog.IsInitialized);
        AppLog.Warn("Test", "无操作");
        AppLog.Error("Test", "无操作");
        AppLog.Info("Test", "无操作");
        AppLog.Flush();
        Assert.False(AppLog.IsInitialized);
    }

    [Fact]
    public void FormatLine_单条单行_换行折叠()
    {
        var line = RollingFileSink.FormatLine(Entry(AppLogLevel.Warn, "第一行\n第二行\r\n第三行"));
        Assert.EndsWith("[WARN ] [Test] 第一行\\n第二行\\n第三行\n", line);
        Assert.Equal(1, line.Count(c => c == '\n'));
    }

    private sealed class CapturingSink : ILogSink
    {
        private readonly object _gate = new();

        public List<AppLogEntry> Entries { get; } = [];

        public void Write(AppLogEntry entry)
        {
            lock (_gate)
            {
                Entries.Add(entry);
            }
        }

        public void Flush()
        {
        }
    }
}
