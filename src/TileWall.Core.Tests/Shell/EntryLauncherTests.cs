using System.ComponentModel;
using System.Diagnostics;
using TileWall.Core.Entries;
using TileWall.Core.Shell;
using Xunit;

namespace TileWall.Core.Tests.Shell;

public sealed class EntryLauncherTests
{
    [Theory]
    [InlineData(".lnk")]
    [InlineData(".url")]
    public void LaunchPassesManagedEntryUnchangedToShell(string extension)
    {
        using var entry = new EntryFixture(extension);
        if (extension == ".lnk")
        {
            var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(entry.Path)!, "target.exe");
            File.WriteAllBytes(target, []);
            var links = new ShellLinkFileService();
            links.Create(entry.Path, target, "--keep-arguments", System.IO.Path.GetDirectoryName(entry.Path)!);
            File.Delete(target);
            Assert.False(System.IO.Path.Exists(target));
            Assert.Equal(target, links.Read(entry.Path).TargetPath);
        }
        else File.WriteAllBytes(entry.Path, UrlShortcut.CreateContent("https://example.test/"));
        var bytesBeforeLaunch = File.ReadAllBytes(entry.Path);
        ProcessStartInfo? request = null;
        var launcher = new EntryLauncher(info => { request = info; return null; });

        var result = launcher.Launch(entry.Path);

        Assert.Equal(LaunchOutcome.Launched, result.Outcome);
        Assert.NotNull(request);
        Assert.Equal(entry.Path, request.FileName);
        Assert.True(request.UseShellExecute);
        Assert.Equal(string.Empty, request.Arguments);
        Assert.Equal(string.Empty, request.Verb);
        Assert.Equal(bytesBeforeLaunch, File.ReadAllBytes(entry.Path));
    }

    [Fact]
    public void LaunchRejectsMissingEntryWithoutStartingShell()
    {
        using var entry = new EntryFixture(".lnk");
        File.Delete(entry.Path);
        var calls = 0;
        var launcher = new EntryLauncher(_ => { calls++; return null; });

        var result = launcher.Launch(entry.Path);

        Assert.Equal(LaunchOutcome.EntryMissing, result.Outcome);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void LaunchRejectsOutsideManagedPathWithoutStartingShell()
    {
        var calls = 0;
        var launcher = new EntryLauncher(_ => { calls++; return null; });

        var result = launcher.Launch(Path.Combine(Path.GetTempPath(), "outside.lnk"));

        Assert.Equal(LaunchOutcome.LaunchFailed, result.Outcome);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void LaunchReportsSynchronousShellFailure()
    {
        using var entry = new EntryFixture(".lnk");
        var launcher = new EntryLauncher(_ => throw new Win32Exception(2, "launch failed"));

        var result = launcher.Launch(entry.Path);

        Assert.Equal(LaunchOutcome.LaunchFailed, result.Outcome);
        Assert.Equal("launch failed", result.FailureReason);
    }

    [Fact]
    public void ElevatedCancellationDoesNotRetry()
    {
        using var entry = new EntryFixture(".lnk");
        var calls = 0;
        var launcher = new EntryLauncher(info =>
        {
            calls++;
            Assert.Equal(entry.Path, info.FileName);
            Assert.Equal("runas", info.Verb);
            throw new Win32Exception(1223);
        });

        var result = launcher.LaunchElevated(entry.Path);

        Assert.Equal(LaunchOutcome.ElevatedDeclined, result.Outcome);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RevealSelectsRealFileAndReturnsExplorerFailure()
    {
        using var entry = new EntryFixture(".lnk");
        var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(entry.Path)!, "real file.txt");
        File.WriteAllText(target, "test");
        var links = new ShellLinkFileService();
        links.Create(entry.Path, target, "", "");
        ProcessStartInfo? request = null;
        var launcher = new EntryLauncher(info =>
        {
            request = info;
            throw new IOException("explorer failed");
        });

        var result = launcher.RevealInExplorer(entry.Path, links);

        Assert.Equal(LaunchOutcome.LaunchFailed, result.Outcome);
        Assert.Equal("explorer failed", result.FailureReason);
        Assert.NotNull(request);
        Assert.Equal("explorer.exe", request.FileName);
        Assert.Equal($"/select,\"{target}\"", request.Arguments);
        Assert.True(request.UseShellExecute);
    }

    [Fact]
    public void RevealDirectoryOpensDirectoryAndUrlHasNoLocalTarget()
    {
        using var entry = new EntryFixture(".lnk");
        var folder = System.IO.Path.GetDirectoryName(entry.Path)!;
        var links = new ShellLinkFileService(); links.Create(entry.Path, folder, "", "");
        ProcessStartInfo? request = null;
        var launcher = new EntryLauncher(info => { request = info; return null; });
        Assert.Equal(LaunchOutcome.Launched, launcher.RevealInExplorer(entry.Path, links).Outcome);
        Assert.Equal($"\"{folder}\"", request!.Arguments);
        using var web = new EntryFixture(".url");
        File.WriteAllBytes(web.Path, UrlShortcut.CreateContent("https://example.test/"));
        request = null;
        Assert.Null(EntryLauncher.ResolveLocalTarget(web.Path, links));
        Assert.Equal(LaunchOutcome.LaunchFailed, launcher.RevealInExplorer(web.Path, links).Outcome);
        Assert.Null(request);
    }

    private sealed class EntryFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "TileWall-EntryLauncherTests-" + Guid.NewGuid().ToString("N"));

        public EntryFixture(string extension)
        {
            var directory = System.IO.Path.Combine(_root, "Objects", "member");
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "entry" + extension);
            File.WriteAllBytes(Path, []);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
