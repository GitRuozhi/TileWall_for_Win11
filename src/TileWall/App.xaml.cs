using Microsoft.UI.Xaml;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Shell;

namespace TileWall;

/// <summary>
/// 应用入口：启动装配序列（M3 设计 §2.2 + M4 §2.2）——建 ConfigStore、清残留 .tmp、
/// EntryRecovery.Sweep（M4 §5.6：未完成提交幂等回滚/纯残留清理/过期撤销材料清理，先于 Load()）、
/// Load()、DEBUG 数据目录隔离与种子触发判定；配置状态分支与渲染在 MainWindow。
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var files = new LocalFileStore();
        var directoryProvider = CreateDataDirectoryProvider();
        var store = new ConfigStore(files, directoryProvider);
        store.CleanupTempFiles(); // ConfigStore.cs:169：清残留 .tmp（尽力）

        var linkFiles = new ShellLinkFileService();
        var recoveryReport = new EntryRecovery(files, linkFiles, directoryProvider.GetDefault()).Sweep();

        var loadResult = store.Load();

        _window = new MainWindow(store, loadResult, IsSeedRequested(), files, linkFiles, recoveryReport.Actions);
        _window.Activate();
    }

    /// <summary>
    /// 数据目录：DEBUG 且设了 TILEWALL_DATA_DIR → 环境目录（UIA 测试隔离）；
    /// 否则 M2 既有 %LOCALAPPDATA%\TileWall。Release 构建无环境判定（编译期剔除）。
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
        return new LocalAppDataDirectoryProvider();
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
