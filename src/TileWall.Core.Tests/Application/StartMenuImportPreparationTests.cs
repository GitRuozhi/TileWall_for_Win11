using System.Security.Cryptography;
using TileWall.Core.Application;
using TileWall.Core.Entries;
using TileWall.Core.Import;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Application;

public sealed class StartMenuImportPreparationTests
{
    private sealed class Executor : IWallStorageExecutor
    {
        private readonly Queue<Action> _jobs = [];
        public int Count => _jobs.Count;
        public Task<T> ExecuteAsync<T>(Func<T> work)
        {
            var completed = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _jobs.Enqueue(() => { try { completed.SetResult(work()); } catch (Exception error) { completed.SetException(error); } });
            return completed.Task;
        }
        public Task RunNextAsync() => Task.Run(_jobs.Dequeue());
        public Task ShutdownAsync() => Task.CompletedTask;
    }
    private sealed class Links(Func<string, LinkFields> read) : ILnkFileService
    {
        public int Reads;
        public LinkFields Read(string path) { Interlocked.Increment(ref Reads); return read(path); }
        public void Create(string path, string target, string arguments, string workingDirectory) => throw new NotSupportedException();
        public void EditTargetOnly(string path, string newTarget) => throw new NotSupportedException();
    }
    private static readonly byte[] Bytes = [1, 2, 3];
    private static readonly string Sha = Convert.ToHexString(SHA256.HashData(Bytes));
    private static StartMenuCandidate Candidate(EntryKind kind = EntryKind.Lnk) =>
        new(Path.Combine(Path.GetTempPath(), "Programs", kind == EntryKind.Lnk ? "app.lnk" : "app.url"), "App", kind, SourceKind.User, "")
            { RootPath = Path.Combine(Path.GetTempPath(), "Programs") };
    private static LinkFields Fields() => new(Path.Combine(Path.GetTempPath(), "app.exe"), "--profile", Path.GetTempPath(), "icon.ico", 2, 1, false);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationBeforeAdmissionOrWhileQueuedNeverReadsCom(bool beforeQueue)
    {
        var executor = new Executor(); var links = new Links(_ => Fields());
        using var cancel = new CancellationTokenSource();
        if (beforeQueue) cancel.Cancel();
        var operation = StartMenuImportPreparation.PrepareSourceAsync(executor, Candidate(), Bytes, Sha,
            new(new InMemoryFileStore(), links), links, cancel.Token);
        Assert.Equal(beforeQueue ? 0 : 1, executor.Count);
        if (!beforeQueue) { cancel.Cancel(); await executor.RunNextAsync(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(0, links.Reads);
    }

    [Fact]
    public async Task OneShortJobReusesObservedFieldsAndFreezesInputs()
    {
        var executor = new Executor(); var fields = Fields(); var links = new Links(_ => fields);
        var reasons = new List<string>(); var bytes = Bytes.ToArray();
        var operation = StartMenuImportPreparation.PrepareSourceAsync(executor, Candidate() with { NotImportableReasons = reasons },
            bytes, Sha, new(new InMemoryFileStore(), links), links, CancellationToken.None);
        reasons.Add("later"); bytes[0] = 99;
        await executor.RunNextAsync();
        var source = await operation;
        Assert.Equal(1, links.Reads);
        Assert.Same(fields, source.LinkFields);
        Assert.Equal(LaunchIdentityService.ForPath(fields.TargetPath!, fields.Arguments, fields.WorkingDirectory), source.Identity);
        Assert.Empty(source.Candidate.NotImportableReasons);
        Assert.Equal(Sha, source.SourceSha256);
        Assert.Equal(0, executor.Count);
    }

    [Fact]
    public async Task CancellationAfterComTailRejectsResultAndNextShortJobStillWorks()
    {
        var executor = new Executor();
        using var cancel = new CancellationTokenSource();
        var links = new Links(_ => { cancel.Cancel(); return Fields(); });
        var operation = StartMenuImportPreparation.PrepareSourceAsync(executor, Candidate(), Bytes, Sha,
            new(new InMemoryFileStore(), links), links, cancel.Token);
        await executor.RunNextAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        var nextLinks = new Links(_ => Fields());
        var next = StartMenuImportPreparation.PrepareSourceAsync(executor, Candidate(), Bytes, Sha,
            new(new InMemoryFileStore(), nextLinks), nextLinks, CancellationToken.None);
        await executor.RunNextAsync();
        Assert.NotNull((await next).Identity);
        Assert.Equal(1, nextLinks.Reads);
    }

    [Fact]
    public async Task UrlUsesObservedBytesWithoutComOrRereadingOriginal()
    {
        var executor = new Executor(); var links = new Links(_ => throw new InvalidOperationException());
        var bytes = System.Text.Encoding.UTF8.GetBytes("[InternetShortcut]\r\nURL=steam://rungameid/10\r\n");
        var operation = StartMenuImportPreparation.PrepareSourceAsync(executor, Candidate(EntryKind.Url), bytes,
            Convert.ToHexString(SHA256.HashData(bytes)), new(new InMemoryFileStore(), links), links, CancellationToken.None);
        await executor.RunNextAsync();
        Assert.Equal(LaunchIdentityService.ForUrl("steam://rungameid/10"), (await operation).Identity);
        Assert.Equal(0, links.Reads);
    }

    [Theory]
    [InlineData("utf16")]
    [InlineData("sections")]
    [InlineData("whitespace")]
    public async Task UrlIconUsesTheSameSectionAndEncodingRulesAsItsObservedTarget(string shape)
    {
        const string iconPath = @"%SystemRoot%\图标\page.ico";
        var text = shape switch
        {
            "sections" => $"[Other]\r\nIconFile=wrong.ico\r\nIconIndex=9\r\n[InternetShortcut]\r\nURL=steam://rungameid/10\r\nIconFile={iconPath}\r\nIconIndex=-2\r\n[After]\r\nIconFile=after.ico\r\nIconIndex=8\r\n",
            "whitespace" => $"  [InternetShortcut]  \r\n  URL = steam://rungameid/10  \r\n  IconFile = {iconPath}  \r\n  IconIndex = -2  \r\n",
            _ => $"[InternetShortcut]\r\nURL=steam://rungameid/10\r\nIconFile={iconPath}\r\nIconIndex=-2\r\n",
        };
        byte[] bytes = shape == "utf16"
            ? [.. System.Text.Encoding.Unicode.GetPreamble(), .. System.Text.Encoding.Unicode.GetBytes(text)]
            : System.Text.Encoding.UTF8.GetBytes(text);
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        var executor = new Executor(); var links = new Links(_ => throw new InvalidOperationException());
        var operation = StartMenuImportPreparation.PrepareSourceAsync(executor, Candidate(EntryKind.Url), bytes,
            sha, new(new InMemoryFileStore(), links), links, CancellationToken.None);
        Array.Fill(bytes, (byte)0); // The queued job must consume its frozen observation, not the caller's later bytes.
        await executor.RunNextAsync();
        var source = await operation;
        Assert.Equal(LaunchIdentityService.ForUrl("steam://rungameid/10"), source.Identity);
        Assert.Equal(iconPath, source.UrlIconPath);
        Assert.Equal(-2, source.UrlIconIndex);
        Assert.Equal(sha, source.SourceSha256);
        Assert.Equal(0, links.Reads);
    }
}
