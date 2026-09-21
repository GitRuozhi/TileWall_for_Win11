using System.Runtime.InteropServices;
using System.Text;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Entries;

/// <summary>
/// T-LINK 集成套件（M4 设计 §10.3；P1 V-06 的 dotnet test 化）：
/// 真实 IShellLink COM + 真实临时目录 + cmd 探针（回显 CWD=%CD% / ARGS=%*）。
/// 样本 .lnk 一律带参数与工作目录；L-1..L-2、L-7 经 Process.Start(UseShellExecute=true) 真实启动。
/// 说明：设计表中 L-1 的 notepad.exe 目标替换为探针 cmd —— 语义相同（「副本独立于源、可启动」）
/// 且不弹窗、无环境依赖（无 Skip 分支）。
/// </summary>
public class LinkIntegrationTests : IDisposable
{
    private readonly LinkTestBed _bed = new();

    [Fact]
    public void L1_普通程序_导入完整副本_删源后副本可独立启动()
    {
        var sample = _bed.CreateSampleLink("sample.lnk", _bed.Probe1, arguments: string.Empty, workingDirectory: _bed.Root);
        var config = _bed.SeedEmptyTile();
        _bed.Service.Commit(config, _bed.Request(new TileDraft { TitleText = "探针", Entry = new EntryDraft.CopyFromFile(sample) }));

        File.Delete(sample); // 删源：副本独立（§12.1）
        _bed.RunManagedLink(EntryPaths.EntryRelativePath(LinkTestBed.ObjectId, "探针.lnk"));

        Assert.Contains("P1=1", _bed.OutputText()); // 进程启动且执行的是副本
    }

    [Fact]
    public void L2_带参数与工作目录_删源后启动_ARGS与CWD逐字符一致()
    {
        var arguments = "hello world --flag=1 \"quoted arg\"";
        var sample = _bed.CreateSampleLink("sample.lnk", _bed.Probe1, arguments, _bed.WorkDir);
        var config = _bed.SeedEmptyTile();

        _bed.Service.Commit(config, _bed.Request(new TileDraft { TitleText = "探针", Entry = new EntryDraft.CopyFromFile(sample) }));
        File.Delete(sample);

        // 副本字段与源逐字符一致（读回断言）
        var fields = _bed.Links.Read(_bed.ManagedPath("探针.lnk"));
        Assert.Equal(arguments, fields.Arguments);
        Assert.Equal(_bed.WorkDir, fields.WorkingDirectory);

        _bed.RunManagedLink(EntryPaths.EntryRelativePath(LinkTestBed.ObjectId, "探针.lnk"));
        var output = _bed.OutputText();
        Assert.Contains("P1=1", output);
        Assert.Contains("CWD=" + _bed.WorkDir, output); // V-06 语义保真
        Assert.Contains("ARGS=" + arguments, output);
    }

    [Fact]
    public void L3_浏览器lnk_导入后保留为lnk不改写为url()
    {
        var browser = FindBrowser();
        if (browser is null)
        {
            // 环境未覆盖：测试机无 Edge/Chrome（设计 §10.3 允许的环境探测降级）
            return;
        }

        var sample = _bed.CreateSampleLink("browser.lnk", browser.Value.Target, browser.Value.Arguments, Path.GetDirectoryName(browser.Value.Target)!);
        var config = _bed.SeedEmptyTile();
        _bed.Service.Commit(config, _bed.Request(new TileDraft { Entry = new EntryDraft.CopyFromFile(sample) }));

        var managed = _bed.ManagedPath("browser.lnk"); // 副本名 = 源名（未改名）
        Assert.True(File.Exists(managed));
        Assert.Equal(EntryKind.Lnk, EntryNames.KindOfRelativePath(managed)); // 保留为 .lnk，不被改写为 .url
        var fields = _bed.Links.Read(managed);
        Assert.Equal(ShellLinkFileService.NormalizePath(browser.Value.Target), ShellLinkFileService.NormalizePath(fields.TargetPath!));
        Assert.Equal(browser.Value.Arguments, fields.Arguments);
    }

    [Fact]
    public void L4_url完整副本_字节级一致_改URL行保留IconFile_删源后仍可编辑()
    {
        var sourceBytes = "[InternetShortcut]\r\nURL=https://old.example/\r\nIconFile=C:\\icons\\page.ico\r\nIconIndex=3\r\n"u8.ToArray();
        var source = Path.Combine(_bed.Root, "page.url");
        File.WriteAllBytes(source, sourceBytes);
        var config = _bed.SeedEmptyTile();

        _bed.Service.Commit(config, _bed.Request(new TileDraft { TitleText = "主页", Entry = new EntryDraft.CopyFromFile(source) }));
        File.Delete(source); // 删源

        var managed = _bed.ManagedPath("主页.url");
        Assert.Equal(sourceBytes, File.ReadAllBytes(managed)); // INV-E4 字节级完整副本

        _bed.Service.Commit(_bed.LoadConfig(), _bed.Request(new TileDraft { Entry = new EntryDraft.EditUrlLine("https://new.example/home") }));
        var after = Encoding.UTF8.GetString(File.ReadAllBytes(managed));
        Assert.Contains("URL=https://new.example/home", after);
        Assert.Contains("IconFile=C:\\icons\\page.ico", after); // IconFile 保留
    }

    [Fact]
    public void L5_文件夹目标_字段断言_TargetPath为目录()
    {
        var folder = Path.Combine(_bed.Root, "目标文件夹");
        Directory.CreateDirectory(folder);
        var config = _bed.SeedEmptyTile();

        _bed.Service.Commit(config, _bed.Request(new TileDraft { TitleText = "资料夹", Entry = new EntryDraft.CreateForPath(folder) }));

        var fields = _bed.Links.Read(_bed.ManagedPath("资料夹.lnk"));
        Assert.Equal(folder, ShellLinkFileService.NormalizePath(fields.TargetPath!), ignoreCase: true);
        Assert.Equal(folder, fields.WorkingDirectory); // 文件夹目标 → 工作目录 = 目录本体（§5.3）
    }

    [Fact]
    public void L6_特殊Shell项_回收站PIDL_目标编辑拒绝_整链替换允许()
    {
        var sample = _bed.CreateRecycleBinLink("special.lnk");
        var config = _bed.SeedEmptyTile();
        _bed.Service.Commit(config, _bed.Request(new TileDraft { TitleText = "回收站", Entry = new EntryDraft.CopyFromFile(sample) }));

        var managed = _bed.ManagedPath("回收站.lnk");
        var fields = _bed.Links.Read(managed);
        Assert.True(fields.HasIdList); // 探针 D 形态
        Assert.Null(fields.TargetPath);

        var before = File.ReadAllBytes(managed);
        var ex = Assert.Throws<DraftValidationException>(() =>
            _bed.Service.Commit(_bed.LoadConfig(), _bed.Request(new TileDraft { Entry = new EntryDraft.EditLnkTarget("C:\\x.exe") })));
        Assert.Equal([DraftValidator.TargetNotPathEditable], ex.Errors);
        Assert.Equal(before, File.ReadAllBytes(managed)); // 拒绝 = 零改动

        // 整链替换允许（F-9 行为）
        _bed.Service.Commit(_bed.LoadConfig(), _bed.Request(new TileDraft { Entry = new EntryDraft.CreateForPath(_bed.Probe1) }));
        Assert.False(_bed.Links.Read(_bed.ManagedPath("回收站.lnk")).HasIdList);
    }

    [Fact]
    public void L7_编辑只改目标_新目标以原参数原工作目录执行()
    {
        var arguments = "keep args \"with quotes\"";
        var sample = _bed.CreateSampleLink("sample.lnk", _bed.Probe1, arguments, _bed.WorkDir);
        var config = _bed.SeedEmptyTile();
        _bed.Service.Commit(config, _bed.Request(new TileDraft { TitleText = "探针", Entry = new EntryDraft.CopyFromFile(sample) }));

        _bed.Service.Commit(_bed.LoadConfig(), _bed.Request(new TileDraft { Entry = new EntryDraft.EditLnkTarget(_bed.Probe2) }));
        File.Delete(sample);

        var fields = _bed.Links.Read(_bed.ManagedPath("探针.lnk")); // INV-E5：参数/工作目录逐字节保留
        Assert.Equal(arguments, fields.Arguments);
        Assert.Equal(_bed.WorkDir, fields.WorkingDirectory);

        _bed.RunManagedLink(EntryPaths.EntryRelativePath(LinkTestBed.ObjectId, "探针.lnk"));
        var output = _bed.OutputText();
        Assert.Contains("P2=1", output); // 新目标执行
        Assert.Contains("CWD=" + _bed.WorkDir, output);
        Assert.Contains("ARGS=" + arguments, output);
    }

    [Fact]
    public void L8_类型切换lnk换url_唯一入口为url_旧lnk入Recovery_失败注入后旧入口完好()
    {
        var sample = _bed.CreateSampleLink("sample.lnk", _bed.Probe1, arguments: string.Empty, workingDirectory: _bed.Root);
        var config = _bed.SeedEmptyTile();
        _bed.Service.Commit(config, _bed.Request(new TileDraft { TitleText = "探针", Entry = new EntryDraft.CopyFromFile(sample) }));

        // 成功半：.lnk → .url 原子切换
        var report = _bed.Service.Commit(_bed.LoadConfig(), _bed.Request(new TileDraft { Entry = new EntryDraft.CreateFromUrl("https://tilewall.example/home") }));
        var oldLnk = _bed.ManagedPath("探针.lnk");
        var newUrl = _bed.ManagedPath("探针.url");
        Assert.False(File.Exists(oldLnk)); // 旧 .lnk 退位（INV-E1：唯一入口为 .url）
        Assert.True(File.Exists(newUrl));
        Assert.Contains("URL=https://tilewall.example/home", File.ReadAllText(newUrl));
        Assert.False(report.UndoMaterial is null);
        Assert.True(File.Exists(Path.Combine(EntryPaths.RecoveryEntriesDir(_bed.Root, report.UndoMaterial!.CommitId), LinkTestBed.ObjectId, "探针.lnk"))); // 旧链保留（M8 按对象分目录）

        // 失败半：导入源不可读（真实文件系统注入）→ 旧 .url 完好
        var urlBefore = File.ReadAllBytes(newUrl);
        var missingSource = Path.Combine(_bed.Root, "no-such.lnk");
        Assert.ThrowsAny<FileNotFoundException>(() =>
            _bed.Service.Commit(_bed.LoadConfig(), _bed.Request(new TileDraft { Entry = new EntryDraft.CopyFromFile(missingSource) })));
        Assert.Equal(urlBefore, File.ReadAllBytes(newUrl)); // 旧入口完好（F-14 真实文件系统形态）
        Assert.Equal("探针.url", Path.GetFileName(Assert.IsType<TileObject>(_bed.LoadConfig().Objects.Single(o => o.Id == LinkTestBed.ObjectId)).Entry!.RelativePath));
    }

    // ————————————————————————————— 辅助 —————————————————————————————

    private static (string Target, string Arguments)? FindBrowser()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return (candidate, "https://example.com/");
            }
        }

        return null;
    }

    public void Dispose() => _bed.Dispose();
}

/// <summary>T-LINK 测试床：真实临时目录 + cmd 探针 + 真实 COM 服务 + 真实 ConfigStore/EntryCommitService。</summary>
internal sealed class LinkTestBed : IDisposable
{
    public const string ObjectId = "obj-link";

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "m4-tlink-" + Guid.NewGuid().ToString("N")[..8]);
    public LocalFileStore Files { get; } = new();
    public ShellLinkFileService Links { get; } = new();
    public ConfigStore Store { get; }
    public EntryCommitService Service { get; }
    public WallGrid Wall { get; } = new(2, 4);

    public string Probe1 { get; }
    public string Probe2 { get; }
    public string WorkDir { get; }
    private string OutputPath { get; }

    public LinkTestBed()
    {
        Directory.CreateDirectory(Root);
        WorkDir = Path.Combine(Root, "wd");
        Directory.CreateDirectory(WorkDir);
        OutputPath = Path.Combine(Root, "probe-out.txt");
        Probe1 = Path.Combine(Root, "probe.cmd");
        Probe2 = Path.Combine(Root, "probe2.cmd");
        File.WriteAllText(Probe1, ProbeScript("P1"));
        File.WriteAllText(Probe2, ProbeScript("P2"));

        Store = new ConfigStore(Files, new TempDirectoryProvider(Root));
        Service = new EntryCommitService(Files, Store, Links, new TempDirectoryProvider(Root));
    }

    /// <summary>测试用数据目录提供者（EnvironmentDataDirectoryProvider 属应用工程，测试内自备同型实现）。</summary>
    private sealed class TempDirectoryProvider(string root) : IDataDirectoryProvider
    {
        public IDataDirectory GetDefault() => new FileSystemDataDirectory(root);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    public string ManagedPath(string fileName) => Path.Combine(Root, "Objects", ObjectId, fileName);

    public TileWallConfig SeedEmptyTile()
    {
        var config = Layouts.Config(Wall, [Layouts.Tile(ObjectId, new GridRect(0, 0, 1, 1))]);
        Store.Save(config);
        return config;
    }

    public EntryCommitRequest Request(TileDraft draft) => new(ObjectId, "T-LINK", draft);

    public TileWallConfig LoadConfig() => ConfigJson.Deserialize(Files.ReadAllBytes(Store.ConfigPath));

    public string CreateSampleLink(string fileName, string target, string arguments, string workingDirectory)
    {
        var path = Path.Combine(Root, fileName);
        Links.Create(path, target, arguments, workingDirectory); // 真实 IShellLink 建样本（带参数与工作目录）
        return path;
    }

    /// <summary>回收站 PIDL 链接（探针 D 构造；IShellLink.SetIDList）。</summary>
    public string CreateRecycleBinLink(string fileName)
    {
        var path = Path.Combine(Root, fileName);
        if (SHParseDisplayName("::{645FF040-5081-101B-9F08-00AA002F954E}", IntPtr.Zero, out var pidl, 0, out _) != 0)
        {
            throw new InvalidOperationException("SHParseDisplayName 失败：回收站");
        }

        try
        {
            var link = (ITestShellLink)(object)new TestShellLink();
            link.SetIDList(pidl);
            ((ITestPersistFile)link).Save(path, fRemember: true);
            return path;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>ShellExecute 启动托管入口并等待探针落盘（temp/m4-probe 同款时序）。</summary>
    public void RunManagedLink(string relativePath)
    {
        File.Delete(OutputPath);
        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar))) { UseShellExecute = true });
        Assert.NotNull(process);
        Assert.True(process!.WaitForExit(30000), "探针启动超时");
        Thread.Sleep(400); // cmd 重定向落盘余量
    }

    public string OutputText() => File.ReadAllText(OutputPath);

    private static string ProbeScript(string marker) =>
        $"@echo off\r\n(echo {marker}=1)>>\"%~dp0probe-out.txt\"\r\n(echo CWD=%CD%)>>\"%~dp0probe-out.txt\"\r\n(echo ARGS=%*)>>\"%~dp0probe-out.txt\"\r\n";

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // 杀软/索引器瞬时占用：尽力清理（临时目录由系统回收）
        }
    }

    // 测试本地 COM（仅用于构造 PIDL 样本；产品 COM 在 TileWall.Core 的 ShellLinkFileService）
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class TestShellLink
    {
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface ITestShellLink
    {
        void Reserved0();
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
    private interface ITestPersistFile
    {
        void ReservedGetClassID(); // IPersist slot 0
        [PreserveSig]
        int IsDirty(); // slot 1
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode); // slot 2
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember); // slot 3
    }
}
