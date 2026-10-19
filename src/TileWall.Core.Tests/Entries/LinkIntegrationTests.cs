using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Xunit;

namespace TileWall.Core.Tests.Entries;

/// <summary>Real shell links through the current schema-two WallStore transaction path.</summary>
public sealed class LinkIntegrationTests : IDisposable
{
    private readonly LinkTestBed _bed = new();

    [Fact]
    public void RawPathReadUsesShellRawFlagWithoutChangingLinkBytes()
    {
        var longDirectory = Path.Combine(_bed.SourceRoot, "Long folder name for raw shortcut path");
        Directory.CreateDirectory(longDirectory);
        var target = Path.Combine(longDirectory, "Long target file name for raw shortcut path.txt");
        File.WriteAllText(target, "read-only shortcut target");
        var source = _bed.CreateLink("raw-path.lnk", target, "--preserve", longDirectory, target, 0, 1);
        var linkBefore = File.ReadAllBytes(source);

        var rawFromCom = _bed.ReadPathWithFlag(source, 0x4); // SDK SLGP_RAWPATH
        var shortFromCom = _bed.ReadPathWithFlag(source, 0x1); // SDK SLGP_SHORTPATH
        var fields = _bed.Links.Read(source);

        Assert.Equal(target, rawFromCom);
        Assert.Equal(rawFromCom, fields.TargetPath);
        if (!string.Equals(shortFromCom, rawFromCom, StringComparison.OrdinalIgnoreCase))
            Assert.NotEqual(shortFromCom, fields.TargetPath);
        Assert.Equal(linkBefore, File.ReadAllBytes(source));
    }

    [Fact]
    public void CopiedLinkKeepsArgumentsWorkingDirectoryIconAndWindowModeAfterSourceDeletion()
    {
        var args = "hello world --flag=1 \"quoted arg\"";
        var source = _bed.CreateLink("source.lnk", _bed.Probe1, args, _bed.WorkDir, _bed.Probe1, 2, 7);
        var result = _bed.Add(new EntryChange.CopyShortcut(LinkTestBed.MemberId, source));
        Assert.Equal(WallCommitStatus.Committed, result.Status);
        File.Delete(source);

        var fields = _bed.Links.Read(_bed.ManagedPath(result));
        Assert.Equal(ShellLinkFileService.NormalizePath(_bed.Probe1), ShellLinkFileService.NormalizePath(fields.TargetPath!));
        Assert.Equal(args, fields.Arguments);
        Assert.Equal(_bed.WorkDir, fields.WorkingDirectory);
        Assert.Equal(_bed.Probe1, fields.IconPath);
        Assert.Equal(2, fields.IconIndex);
        Assert.Equal(7, fields.ShowCmd);
        _bed.RunManagedLink(result);
        Assert.Contains("P1=1", _bed.OutputText());
        Assert.Contains("CWD=" + _bed.WorkDir, _bed.OutputText());
        Assert.Contains("ARGS=" + args, _bed.OutputText());
    }

    [Fact]
    public void TargetEditPreservesEveryOtherLinkField()
    {
        var source = _bed.CreateLink("source.lnk", _bed.Probe1, "keep args", _bed.WorkDir, _bed.Probe1, 3, 7);
        var first = _bed.Add(new EntryChange.CopyShortcut(LinkTestBed.MemberId, source));
        Assert.Equal(WallCommitStatus.Committed, first.Status);
        var before = _bed.Links.Read(_bed.ManagedPath(first));
        var changed = _bed.Change(first.CommittedDocument!, new EntryChange.ChangeTarget(LinkTestBed.MemberId, _bed.Probe2));
        Assert.Equal(WallCommitStatus.Committed, changed.Status);

        var after = _bed.Links.Read(_bed.ManagedPath(changed));
        Assert.Equal(ShellLinkFileService.NormalizePath(_bed.Probe2), ShellLinkFileService.NormalizePath(after.TargetPath!));
        Assert.Equal(before.Arguments, after.Arguments);
        Assert.Equal(before.WorkingDirectory, after.WorkingDirectory);
        Assert.Equal(before.IconPath, after.IconPath);
        Assert.Equal(before.IconIndex, after.IconIndex);
        Assert.Equal(before.ShowCmd, after.ShowCmd);
        _bed.RunManagedLink(changed);
        Assert.Contains("P2=1", _bed.OutputText());
        Assert.Contains("ARGS=keep args", _bed.OutputText());
    }

    [Fact]
    public void PidlShortcutRejectsTargetEditAndAllowsWholeReplacement()
    {
        var source = _bed.CreateRecycleBinLink("recycle.lnk");
        var first = _bed.Add(new EntryChange.CopyShortcut(LinkTestBed.MemberId, source));
        Assert.Equal(WallCommitStatus.Committed, first.Status);
        var path = _bed.ManagedPath(first);
        Assert.True(_bed.Links.Read(path).HasIdList);
        var bytes = File.ReadAllBytes(path);

        var rejected = _bed.PlanChange(first.CommittedDocument!, new EntryChange.ChangeTarget(LinkTestBed.MemberId, _bed.Probe1));
        Assert.Null(rejected.Request);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(first.CommittedDocument!.Generation, _bed.Store.Open().Document!.Generation);

        var replaced = _bed.Change(first.CommittedDocument!, new EntryChange.CreateLink(LinkTestBed.MemberId, _bed.Probe1));
        Assert.Equal(WallCommitStatus.Committed, replaced.Status);
        Assert.False(_bed.Links.Read(_bed.ManagedPath(replaced)).HasIdList);
    }

    [Fact]
    public void UrlCopyAndTargetEditRetainOtherIniFields()
    {
        var bytes = "[InternetShortcut]\r\nURL=https://old.example/\r\nIconFile=C:\\icons\\page.ico\r\nIconIndex=3\r\n"u8.ToArray();
        var source = Path.Combine(_bed.SourceRoot, "page.url");
        File.WriteAllBytes(source, bytes);
        var first = _bed.Add(new EntryChange.CopyShortcut(LinkTestBed.MemberId, source));
        Assert.Equal(WallCommitStatus.Committed, first.Status);
        File.Delete(source);
        Assert.Equal(bytes, File.ReadAllBytes(_bed.ManagedPath(first)));

        var changed = _bed.Change(first.CommittedDocument!, new EntryChange.ChangeTarget(LinkTestBed.MemberId, "https://new.example/home"));
        Assert.Equal(WallCommitStatus.Committed, changed.Status);
        var text = Encoding.UTF8.GetString(File.ReadAllBytes(_bed.ManagedPath(changed)));
        Assert.Contains("URL=https://new.example/home", text);
        Assert.Contains("IconFile=C:\\icons\\page.ico", text);
        Assert.Contains("IconIndex=3", text);
    }

    [Fact]
    public void MissingReplacementSourceLeavesPriorEntryUntouched()
    {
        var first = _bed.Add(new EntryChange.CreateUrl(LinkTestBed.MemberId, "https://old.example/"));
        Assert.Equal(WallCommitStatus.Committed, first.Status);
        var path = _bed.ManagedPath(first);
        var before = File.ReadAllBytes(path);
        var failed = _bed.PlanChange(first.CommittedDocument!, new EntryChange.CopyShortcut(LinkTestBed.MemberId, Path.Combine(_bed.SourceRoot, "missing.lnk")));
        Assert.Null(failed.Request);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(first.CommittedDocument!.Generation, _bed.Store.Open().Document!.Generation);
    }

    public void Dispose() => _bed.Dispose();
}

internal sealed class LinkTestBed : IDisposable
{
    public const string MemberId = "member-link";
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "wall-link-" + Guid.NewGuid().ToString("N"));
    public string SourceRoot { get; } = Path.Combine(Path.GetTempPath(), "wall-link-source-" + Guid.NewGuid().ToString("N"));
    public string WorkDir { get; }
    public string Probe1 { get; }
    public string Probe2 { get; }
    public ShellLinkFileService Links { get; } = new();
    public WallStore Store { get; }
    private readonly string _output;

    public LinkTestBed()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(SourceRoot);
        WorkDir = Path.Combine(SourceRoot, "wd");
        Directory.CreateDirectory(WorkDir);
        _output = Path.Combine(SourceRoot, "probe-out.txt");
        Probe1 = Path.Combine(SourceRoot, "probe1.cmd");
        Probe2 = Path.Combine(SourceRoot, "probe2.cmd");
        File.WriteAllText(Probe1, ProbeScript("P1"));
        File.WriteAllText(Probe2, ProbeScript("P2"));
        Store = new WallStore(new LocalFileStore(), Links, Root);
        Assert.Equal(WallOpenStatus.Loaded, Store.InitializeFresh(WallDocument.Empty(1, 4)).Status);
    }

    private static WallDocument WithMember(long generation) => new()
    {
        Generation = generation, Columns = 1, Rows = 4,
        Lanes = [new WallLane { Groups = [new StartGroup
        {
            Id = "group-link", Members = [new TileMember
            {
                Id = MemberId, LocalBounds = new GridRect(0, 0, 1, 1),
                Visual = new MemberVisual { Title = "Probe" }
            }]
        }] }]
    };

    public WallCommitResult Add(EntryChange change)
    {
        var before = Store.Open().Document!;
        var plan = Store.PlanEntries(before, WithMember(before.Generation + 1), [change]);
        Assert.NotNull(plan.Request);
        return Store.Commit(before, plan.Request, true);
    }

    public WallCommitResult Change(WallDocument before, EntryChange change)
    {
        var plan = PlanChange(before, change);
        Assert.NotNull(plan.Request);
        return Store.Commit(before, plan.Request, true);
    }

    public WallPlanResult PlanChange(WallDocument before, EntryChange change) =>
        Store.PlanEntries(before, before with { Generation = before.Generation + 1 }, [change]);

    public string ManagedPath(WallCommitResult result)
    {
        var entry = result.CommittedDocument!.Lanes[0].Groups[0].Members[0].Entry!;
        return Path.Combine(Root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string CreateLink(string fileName, string target, string args, string workingDirectory,
        string iconPath, int iconIndex, int showCmd)
    {
        var path = Path.Combine(SourceRoot, fileName);
        Links.Create(path, target, args, workingDirectory);
        var link = (ITestShellLinkW)(object)new TestShellLink();
        ((ITestPersistFile)link).Load(path, 0);
        link.SetIconLocation(iconPath, iconIndex);
        link.SetShowCmd(showCmd);
        ((ITestPersistFile)link).Save(path, true);
        return path;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszName, IntPtr pbc, out IntPtr ppidl,
        uint sfgaoIn, out uint psfgaoOut);

    public string CreateRecycleBinLink(string fileName)
    {
        var path = Path.Combine(SourceRoot, fileName);
        if (SHParseDisplayName("::{645FF040-5081-101B-9F08-00AA002F954E}", IntPtr.Zero, out var pidl, 0, out _) != 0)
            throw new InvalidOperationException("Recycle Bin PIDL lookup failed.");
        try
        {
            var link = (ITestShellLinkW)(object)new TestShellLink();
            link.SetIDList(pidl);
            ((ITestPersistFile)link).Save(path, true);
            return path;
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    public string ReadPathWithFlag(string source, uint flag)
    {
        var link = (ITestShellLinkW)(object)new TestShellLink();
        ((ITestPersistFile)link).Load(source, 0);
        var buffer = new StringBuilder(1024);
        link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, flag);
        return buffer.ToString();
    }

    public void RunManagedLink(WallCommitResult result)
    {
        File.Delete(_output);
        using var process = Process.Start(new ProcessStartInfo(ManagedPath(result))
        { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true });
        Assert.NotNull(process);
        Assert.True(process!.WaitForExit(30000), "Shortcut probe timed out.");
        SpinWait.SpinUntil(() => File.Exists(_output), 3000);
    }

    public string OutputText() => File.ReadAllText(_output);
    private static string ProbeScript(string marker) =>
        $"@echo off\r\n(echo {marker}=1)>>\"%~dp0probe-out.txt\"\r\n(echo CWD=%CD%)>>\"%~dp0probe-out.txt\"\r\n(echo ARGS=%*)>>\"%~dp0probe-out.txt\"\r\n";

    public void Dispose()
    {
        try { Directory.Delete(Root, true); Directory.Delete(SourceRoot, true); }
        catch (IOException) { }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class TestShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface ITestShellLinkW
    {
        void GetPath([Out] StringBuilder pszFile, int cch, IntPtr pfd, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out] StringBuilder path, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
    private interface ITestPersistFile
    {
        void GetClassID(out Guid id);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string path, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string path);
    }
}

