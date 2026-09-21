using Microsoft.UI.Xaml;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Import;
using TileWall.Core.Settings;
using TileWall.Core.Shell;
using TileWall.Shell;
using TileWall.Shell.Interop;

namespace TileWall;

/// <summary>
/// 应用入口：M7 装配序列（M7 设计 §2.2）——
/// 1. 单实例门（绝对先行：二次启动不建窗口、不触碰数据目录，EntryRecovery.Sweep 不双跑）；
/// 2. 文件装配（M3/M4 既有序列不变：ConfigStore、清 .tmp、Sweep、Load）；
/// 3. Win32 宿主层（隐藏宿主窗 → 热键注册（失败→未注册态）→ 托盘 NIM_ADD → 登录启动注册表读）；
/// 4. 主窗与启动形态分支（手动启动 → Activate 显示墙；--background 登录启动 → 延迟 Activate 仅驻留托盘）；
/// 5. 系统输入 → Router（热键/二次启动/托盘，全部在 UI 线程收敛）。
/// Win32 注册/注销严格成对：ExitCoordinator 释放序为常规路径；本文件 catch 为装配异常路径的成对收口。
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var addRequest = ParseAddRequest(); // M9 §4.4：--add 负载（冷启动进添加流程 / 二次启动转投首实例）
        // ———— 1. 单实例（§7.1：Local\ 按会话隔离；先于任何文件/窗口副作用） ————
        var gate = SingleInstanceGate.Acquire(out var acquired);
        if (acquired != SingleInstanceAcquireResult.Acquired)
        {
            // 二次启动：已向首实例宿主窗 PostMessage「激活」（或限时未果）→ 本进程确定性退出。
            // Environment.Exit(0)：此时未建任何窗口/未启动 XAML 消息循环，直接终程比
            // Application.Exit（依赖调度器循环已运转）更确定——退出码恒 0、进程立即消失。
            if (acquired == SingleInstanceAcquireResult.NotifiedExisting && addRequest.Paths.Count > 0)
            {
                // M9 §4.4：携带 --add 的二次启动改投路径批次；投递失败回退纯激活（至少墙面唤回，不静默）
                if (!gate.PostPaths(addRequest.Paths))
                {
                    gate.PostActivate();
                }
            }

            gate.Dispose();
            Environment.Exit(0);
            return;
        }

        var background = IsBackgroundRequested(); // Run 键命令行 --background → 仅驻留托盘（§8.3）
        ShellMessageHost? host = null;
        IHotKeyRegistration? hotkeys = null;
        ShellNotifyIconHost? tray = null;
        try
        {
            // ———— 2. 文件装配（M3/M4 既有序列不变；首实例才会执行到这里） ————
            var files = new LocalFileStore();
            var directoryProvider = CreateDataDirectoryProvider();
            var store = new ConfigStore(files, directoryProvider);
            store.CleanupTempFiles(); // ConfigStore.cs:169：清残留 .tmp（尽力）
            // M9 §3.2：图标缓存根按模式选择（packaged=LocalCacheFolder，系统管理清理语义；unpackaged=数据根）
            var iconCacheRoot = CreateIconCacheRoot(directoryProvider.GetDefault().RootPath);
            new IconCache(files, iconCacheRoot).Clear(); // M8 §2.2：尽力清理缓存残留（缓存可整体重建，绝不权威）

            var linkFiles = new ShellLinkFileService();
            var recoveryReport = new EntryRecovery(files, linkFiles, directoryProvider.GetDefault()).Sweep();

            var loadResult = store.Load();

            // ———— 3. Win32 宿主层（互操作隔离在 Shell/Interop/*，§4.2/§5.2/§8.3） ————
            host = new ShellMessageHost(); // 宿主窗在身份确定后才创建（二次启动进程不得干扰 FindWindow）
            gate.BindHost(host);
            // Fresh 首启 loadResult.Config 为 null → 用 AppSettings 默认值（"Win+Oem3"），首启即注册
            var configuredHotKey = loadResult.Config?.Settings.HotKey ?? new AppSettings().HotKey;
            hotkeys = CreateHotKeyRegistration(host, configuredHotKey);
            tray = new ShellNotifyIconHost(host, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

            var login = CreateLoginStartup();

            // ———— 4. 主窗与启动形态（§2.2 第 7–8 步） ————
            _window = new MainWindow(
                store,
                loadResult,
                IsSeedRequested(),
                files,
                linkFiles,
                new ShellHostContext(host, hotkeys, tray, login, gate, iconCacheRoot)
                {
                    ConfiguredHotKey = configuredHotKey,
                },
                startHidden: background,
                new KnownFolderPaths(),          // M8 §5.1：SHGetKnownFolderPath 真身（失败 → null 来源隐藏）
                new ShortcutIconExtractor(),     // M8 §5.4：SHGetFileInfoW 真身（失败 → 占位字形）
                recoveryReport.Actions);
            if (!background)
            {
                _window.Activate(); // 手动启动：显示墙（Bootstrap 经 Loaded 执行）
                _window.MarkInitialActivation(); // 此后隐藏→唤回走 AppWindow.Show 直达（SnapShow 语义）
            }

            // ———— 5. 系统输入 → Router（唯一命令汇聚点；UI 线程收敛，§3.1） ————
            host.HotKeyPressed += _window.ToggleWall;
            host.ActivateRequested += _window.ShowOrFocus; // 二次启动 = 与托盘双击同一条命令
            tray.ShowOrFocusRequested += _window.ShowOrFocus;
            tray.SettingsRequested += _window.OpenSettings;
            tray.ExitRequested += _window.RequestExit;
            gate.Activated += _window.ShowOrFocus;
            gate.PathsReceived += _window.HandleExplorerAdd; // M9 §4.5：Explorer 添加批次 → 暂存/直处理
            UpdateTrayTooltip(tray, hotkeys);
            hotkeys.RegistrationChanged += () => UpdateTrayTooltip(tray, hotkeys); // C09：tooltip 反映真实注册状态

            if (addRequest.Paths.Count > 0 || addRequest.Truncated)
            {
                // M9 §4.4 冷启动：因添加流程启动 → 启动形态 = 显示墙（同手动启动）；此刻无会话，
                // 布局就绪后即处理；绝不自动执行被添加目标（§14.1）。--background 登录态不该携带 --add。
                _window.HandleExplorerAdd(addRequest);
            }
        }
        catch
        {
            // 装配中途失败：已获取的 Win32 资源就地成对注销（互斥体/宿主窗另有进程退出兜底）
            tray?.Dispose();
            (hotkeys as IDisposable)?.Dispose();
            host?.Dispose();
            gate.Dispose();
            throw;
        }
    }

    /// <summary>热键注册（§2.2 第 3 步）：按配置注册；解析失败/注册失败 → 「未注册」状态就地呈现，不弹窗。</summary>
    private static IHotKeyRegistration CreateHotKeyRegistration(ShellMessageHost host, string? configuredHotKey)
    {
        IHotKeyRegistration registrar = new GlobalHotKeyRegistrar(host);
#if DEBUG
        if (Environment.GetEnvironmentVariable("TILEWALL_FAIL_HOTKEY") == "1")
        {
            registrar = new FailingHotKeyRegistration(registrar); // T-UIA-⑤：假注册器（断言无虚假成功）
        }
#endif
        if (HotKeyGesture.TryParse(configuredHotKey, out var gesture, out _) && gesture is not null)
        {
            _ = registrar.TryRegister(gesture); // 失败路径是功能（C09 保底入口），非崩溃路径（[R3]）
        }

        return registrar;
    }

    private static void UpdateTrayTooltip(ShellNotifyIconHost tray, IHotKeyRegistration hotkeys) =>
        tray.UpdateTooltip(hotkeys.IsRegistered ? "TileWall" : "TileWall（快捷键未注册）");

    /// <summary>登录启动形态（§2.2 第 8 步）：命令行带 --background → 不 Activate，仅托盘驻留。</summary>
    private static bool IsBackgroundRequested() =>
        Environment.GetCommandLineArgs().Any(a =>
            string.Equals(a, RegistryLoginStartup.BackgroundArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// M9 §4.4：解析 --add 负载（`--add "路径1" …` + 可选截断标记）。
    /// Environment.GetCommandLineArgs 已按引号拆分；路径条目不含引号字符（Windows 文件名禁止）。
    /// </summary>
    private static ExplorerAddMessage ParseAddRequest()
    {
        var argv = Environment.GetCommandLineArgs();
        var index = Array.FindIndex(argv, a => string.Equals(a, ExplorerAddCommandLine.AddArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return ExplorerAddMessage.Empty;
        }

        var paths = argv.Skip(index + 1)
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .ToArray(); // 其余开关（--background 等）不并入路径
        var truncated = Array.Exists(argv, a => string.Equals(a, ExplorerAddCommandLine.TruncatedMarker, StringComparison.OrdinalIgnoreCase));
        return new ExplorerAddMessage(paths, truncated);
    }

    /// <summary>登录启动开关按模式注入（M9 §5.1）：packaged=StartupTask（卸载零残留）；unpackaged=HKCU Run 键（既有）。</summary>
    private static ILoginStartup CreateLoginStartup()
    {
#if TILEWALL_PACKAGED
        return new StartupTaskLoginStartup();
#else
        return new RegistryLoginStartup(new RegistryRunKeyStore(), Environment.ProcessPath
            ?? throw new InvalidOperationException("无法取得进程可执行文件路径"));
#endif
    }

    /// <summary>
    /// 图标缓存根（M9 §3.2）：packaged → LocalCacheFolder（升级/卸载清理语义由系统管理，§16.1）；
    /// unpackaged → 数据根（M8 既有 &lt;root&gt;/Cache/Icons 布局）。
    /// </summary>
    private static string CreateIconCacheRoot(string dataRootPath)
    {
#if TILEWALL_PACKAGED
        return DataDirectoryResolver.SelectIconCacheRoot(
            packaged: true,
            Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path,
            dataRootPath);
#else
        return DataDirectoryResolver.SelectIconCacheRoot(packaged: false, null, dataRootPath);
#endif
    }

    /// <summary>
    /// 数据目录（M9 §3.1 三级判定）：DEBUG 且设了 TILEWALL_DATA_DIR → 环境目录（UIA 测试隔离，最高优先）；
    /// TILEWALL_PACKAGED → ApplicationData.Current.LocalFolder（落盘 Packages\&lt;PFN&gt;\LocalState）；
    /// 否则 M2 既有 %LOCALAPPDATA%\TileWall。解析逻辑见 DataDirectoryResolver（两分支有单测）。
    /// </summary>
    private static IDataDirectoryProvider CreateDataDirectoryProvider()
    {
#if DEBUG
        var dataDir = Environment.GetEnvironmentVariable("TILEWALL_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            return new EnvironmentDataDirectoryProvider(dataDir);
        }
#endif
#if TILEWALL_PACKAGED
        return new PackagedDataDirectoryProvider();
#else
        return new LocalAppDataDirectoryProvider();
#endif
    }

    /// <summary>开发种子触发：TILEWALL_SEED == "1"（仅 DEBUG；Core 的 DemoSeed 不读环境，M3 设计 §8.1）。</summary>
    private static bool IsSeedRequested()
    {
#if DEBUG
        return Environment.GetEnvironmentVariable("TILEWALL_SEED") == "1";
#else
        return false;
#endif
    }
}
