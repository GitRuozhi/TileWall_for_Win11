using System.Diagnostics;
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

    [Fact]
    public void TrustedRoot是目录联接时不创建或写入外部日志()
    {
        if (!OperatingSystem.IsWindows()) throw Xunit.Sdk.SkipException.ForSkip("Windows junction fixture required.");
        Directory.CreateDirectory(_dir);
        var outside = _dir + "-outside";
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "leave-alone.txt");
        File.WriteAllBytes(sentinel, [3, 1, 4, 1, 5]);
        var trustedRoot = Path.Combine(_dir, "trusted");

        try
        {
            CreateDirectoryLink(trustedRoot, outside);
            using var sink = new RollingFileSink(Path.Combine(trustedRoot, "logs"), trustedRoot: trustedRoot);
            sink.Write(Entry("blocked-root-link"));

            Assert.Equal(new byte[] { 3, 1, 4, 1, 5 }, File.ReadAllBytes(sentinel));
            Assert.Equal(new[] { "leave-alone.txt" }, Directory.GetFileSystemEntries(outside).Select(path => Path.GetFileName(path)!));
            Assert.False(File.Exists(Path.Combine(outside, "logs", "tilewall.log")));
        }
        finally
        {
            DeleteDirectoryLink(trustedRoot);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Logs目录联接到外部时不写入外部日志()
    {
        if (!OperatingSystem.IsWindows()) throw Xunit.Sdk.SkipException.ForSkip("Windows junction fixture required.");
        Directory.CreateDirectory(_dir);
        var outside = _dir + "-outside";
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "leave-alone.txt");
        File.WriteAllBytes(sentinel, [2, 7, 1, 8]);
        var logs = Path.Combine(_dir, "logs");

        try
        {
            CreateDirectoryLink(logs, outside);
            using var sink = new RollingFileSink(logs, trustedRoot: _dir);
            sink.Write(Entry("blocked-logs-link"));

            Assert.Equal(new byte[] { 2, 7, 1, 8 }, File.ReadAllBytes(sentinel));
            Assert.Equal(new[] { "leave-alone.txt" }, Directory.GetFileSystemEntries(outside).Select(path => Path.GetFileName(path)!));
        }
        finally
        {
            DeleteDirectoryLink(logs);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void 实际Windows目录Junction根和Logs均拒绝且外部Sentinel不变()
    {
        if (!OperatingSystem.IsWindows()) throw Xunit.Sdk.SkipException.ForSkip("Windows mklink /J fixture required.");
        Directory.CreateDirectory(_dir);
        var fixtureRoot = Path.GetFullPath(_dir);
        var outside = Path.Combine(fixtureRoot, "outside-target");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "leave-alone.txt");
        var expected = new byte[] { 4, 2, 4, 2, 1 };
        File.WriteAllBytes(sentinel, expected);
        var rootJunction = Path.Combine(fixtureRoot, "data-root-junction");
        var logsRoot = Path.Combine(fixtureRoot, "logs-root");
        Directory.CreateDirectory(logsRoot);
        var logsJunction = Path.Combine(logsRoot, "logs");

        try
        {
            CreateDirectoryJunction(rootJunction, outside, fixtureRoot);
            using (var sink = new RollingFileSink(Path.Combine(rootJunction, "logs"), trustedRoot: rootJunction))
                sink.Write(Entry("blocked-root-junction"));
            AssertExternalFixtureUnchanged(outside, sentinel, expected);

            CreateDirectoryJunction(logsJunction, outside, fixtureRoot);
            using (var sink = new RollingFileSink(logsJunction, trustedRoot: logsRoot))
                sink.Write(Entry("blocked-logs-junction"));
            AssertExternalFixtureUnchanged(outside, sentinel, expected);
        }
        finally
        {
            DeleteFixtureJunctionIfPresent(logsJunction, fixtureRoot);
            DeleteFixtureJunctionIfPresent(rootJunction, fixtureRoot);
        }
    }

    [Fact]
    public void 当前日志是外部文件符号链接时保持链接和目标不变()
    {
        if (!OperatingSystem.IsWindows()) throw Xunit.Sdk.SkipException.ForSkip("Windows symbolic-link fixture required.");
        Directory.CreateDirectory(_dir);
        var outside = _dir + "-outside.log";
        File.WriteAllBytes(outside, [9, 2, 6, 5]);
        var current = Path.Combine(_dir, "tilewall.log");

        try
        {
            CreateFileLink(current, outside);
            using var sink = new RollingFileSink(_dir);
            sink.Write(Entry("blocked-current-link"));

            Assert.True((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(new byte[] { 9, 2, 6, 5 }, File.ReadAllBytes(outside));
        }
        finally
        {
            File.Delete(current);
            File.Delete(outside);
        }
    }

    [Fact]
    public void 历史日志是外部文件符号链接时轮转拒绝且外部目标不变()
    {
        if (!OperatingSystem.IsWindows()) throw Xunit.Sdk.SkipException.ForSkip("Windows symbolic-link fixture required.");
        Directory.CreateDirectory(_dir);
        var outside = _dir + "-outside.log";
        File.WriteAllBytes(outside, [5, 3, 5, 8]);
        var history = Path.Combine(_dir, "tilewall.1.log");

        try
        {
            CreateFileLink(history, outside);
            using var sink = NewSink(maxFileBytes: 1, maxHistoryFiles: 1);
            sink.Write(Entry("force-roll"));

            Assert.True((File.GetAttributes(history) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(new byte[] { 5, 3, 5, 8 }, File.ReadAllBytes(outside));
        }
        finally
        {
            File.Delete(history);
            File.Delete(outside);
        }
    }

    [Fact]
    public void 活动日志句柄打开期间不能被替换()
    {
        if (!OperatingSystem.IsWindows()) throw Xunit.Sdk.SkipException.ForSkip("Windows file-sharing fixture required.");
        using var sink = NewSink(maxFileBytes: 1 << 20, maxHistoryFiles: 1);
        sink.Write(Entry("pin-active-file"));
        var current = sink.CurrentFilePath;
        var moved = current + ".attacker-move";

        Assert.Throws<IOException>(() => File.Move(current, moved));
        Assert.True(File.Exists(current));
        Assert.False(File.Exists(moved));
    }

    private static AppLogEntry Entry(string message) =>
        new(DateTimeOffset.UtcNow, AppLogLevel.Warn, "PathGuard", message);

    private static void CreateDirectoryLink(string path, string target)
    {
        try { Directory.CreateSymbolicLink(path, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { throw Xunit.Sdk.SkipException.ForSkip($"Windows directory link fixture unavailable: {ex.GetType().Name}"); }
    }

    private static void CreateFileLink(string path, string target)
    {
        try { File.CreateSymbolicLink(path, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { throw Xunit.Sdk.SkipException.ForSkip($"Windows file link fixture unavailable: {ex.GetType().Name}"); }
    }

    private static void CreateDirectoryJunction(string path, string target, string fixtureRoot)
    {
        EnsureFixturePath(path, fixtureRoot);
        EnsureFixturePath(target, fixtureRoot);
        if (!Directory.Exists(target)) throw new IOException("Junction target must be an existing fixture directory.");
        if (new[] { path, target }.Any(value => value.IndexOfAny(['"', '&', '|', '<', '>', '^', '%', '!', '\r', '\n']) >= 0))
            throw Xunit.Sdk.SkipException.ForSkip("Fixture path contains cmd.exe metacharacters.");

        var start = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{path}\" \"{target}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var process = Process.Start(start)
            ?? throw new IOException("Could not start cmd.exe for the junction fixture.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new IOException("mklink /J did not finish.");
        }
        Task.WaitAll(output, error);
        if (process.ExitCode != 0)
            throw Xunit.Sdk.SkipException.ForSkip($"mklink /J fixture unavailable: {error.Result.Trim()} {output.Result.Trim()}");

        EnsureFixturePath(path, fixtureRoot);
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory) || !attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("mklink /J did not create a directory reparse point.");
    }

    private static void AssertExternalFixtureUnchanged(string outside, string sentinel, byte[] expected)
    {
        Assert.Equal(expected, File.ReadAllBytes(sentinel));
        Assert.Equal(new[] { Path.GetFileName(sentinel) },
            Directory.GetFileSystemEntries(outside).Select(path => Path.GetFileName(path)!));
    }

    private static void EnsureFixturePath(string path, string fixtureRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixtureRoot));
        var full = Path.GetFullPath(path);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Junction fixture path is outside its temporary test directory.");
    }

    private static void DeleteFixtureJunctionIfPresent(string path, string fixtureRoot)
    {
        EnsureFixturePath(path, fixtureRoot);
        if (!Directory.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory) || !attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Refusing to remove a fixture path that is not a directory reparse point.");
        Directory.Delete(path, recursive: false);
    }

    private static void DeleteDirectoryLink(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: false);
    }

    private RollingFileSink NewSink(long maxFileBytes, int maxHistoryFiles) =>
        new(_dir, "tilewall", maxFileBytes, maxHistoryFiles);
}
