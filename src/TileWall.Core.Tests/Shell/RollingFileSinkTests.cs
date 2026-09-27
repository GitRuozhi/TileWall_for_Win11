using Xunit;

using TileWall.Core.Shell;

namespace TileWall.Core.Tests.Shell;

/// <summary>C4/F-07：滚动文件落地——写入格式、超限滚动、历史保留、持续失败放弃。</summary>
public sealed class RollingFileSinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tilewall-logtests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void 写入落盘_单行格式()
    {
        using (var sink = NewSink(maxFileBytes: 1 << 20, maxHistoryFiles: 3))
        {
            sink.Write(new AppLogEntry(DateTimeOffset.Parse("2026-09-21T10:00:00+08:00"), AppLogLevel.Error, "Save", "保存失败：磁盘已满"));
        }

        var lines = File.ReadAllLines(Path.Combine(_dir, "tilewall.log"));
        var line = Assert.Single(lines);
        Assert.Contains("[ERROR] [Save] 保存失败：磁盘已满", line);
        Assert.StartsWith("2026-09-21", line);
    }

    [Fact]
    public void 超限滚动_历史按新到旧移位_最旧删除()
    {
        using (var sink = NewSink(maxFileBytes: 128, maxHistoryFiles: 2))
        {
            for (var i = 1; i <= 25; i++) // 每行约 48 字节 → 约每 3 行滚动一次；末条写后当前文件重建
            {
                sink.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppLogLevel.Warn, "T", $"第{i:00}条"));
            }
        }

        Assert.True(File.Exists(Path.Combine(_dir, "tilewall.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "tilewall.1.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "tilewall.2.log")));
        Assert.False(File.Exists(Path.Combine(_dir, "tilewall.3.log"))); // 保留数上限生效

        var all = File.ReadAllText(Path.Combine(_dir, "tilewall.log"))
            + File.ReadAllText(Path.Combine(_dir, "tilewall.1.log"))
            + File.ReadAllText(Path.Combine(_dir, "tilewall.2.log"));
        Assert.DoesNotContain("第01条", all); // 早期内容已被滚出删除（保留数上限的语义）
        Assert.Contains("第25条", all); // 最新内容在当前文件
        Assert.Equal(7, all.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length); // 当前 1 + 历史 3 + 3
    }

    [Fact]
    public void 重开追加_不覆盖既有内容()
    {
        using (var sink = NewSink(maxFileBytes: 1 << 20, maxHistoryFiles: 3))
        {
            sink.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppLogLevel.Warn, "T", "第一次"));
        }

        using (var sink = NewSink(maxFileBytes: 1 << 20, maxHistoryFiles: 3))
        {
            sink.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppLogLevel.Warn, "T", "第二次"));
        }

        var lines = File.ReadAllLines(Path.Combine(_dir, "tilewall.log"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("第一次", lines[0]);
        Assert.Contains("第二次", lines[1]);
    }

    [Fact]
    public void 持续失败达上限后放弃且不抛出()
    {
        var filePath = Path.Combine(_dir, "occupy"); // 目标目录被同名文件占据 → 建目录必败
        Directory.CreateDirectory(_dir);
        File.WriteAllText(filePath, "block");

        using var sink = new RollingFileSink(filePath, "tilewall", maxFileBytes: 1024, maxHistoryFiles: 1);
        for (var i = 0; i < RollingFileSink.MaxConsecutiveFailures + 3; i++)
        {
            sink.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppLogLevel.Error, "T", "必败写"));
        }

        Assert.True(sink.Abandoned); // 达上限后停止尝试，宿主业务不受影响
    }

    private RollingFileSink NewSink(long maxFileBytes, int maxHistoryFiles) =>
        new(_dir, "tilewall", maxFileBytes, maxHistoryFiles);
}
