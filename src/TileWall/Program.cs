namespace TileWall;

/// <summary>
/// 自有入口（M9 设计 §1.2）：csproj 定义 DISABLE_XAML_GENERATED_MAIN 后，XAML 生成 Main 让位于本类。
/// `-Embedding`（COM 以此参数激活 com:ExeServer）→ ExplorerCommandServer：仅 COM 分支——不触单实例门/
/// 数据目录/XAML/窗口（§4.1 红线）；其余命令行（含 --background / --add）按生成代码的原序列启动 WinUI 应用。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (IsEmbedding(args))
        {
            return Shell.Interop.ExplorerCommandServer.Run();
        }

        // —— 与 App.g.i.cs 生成的 Main 逐句一致（原启动序列不变，§1.2）——
        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        global::Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(); // 丢弃赋值，非 lambda 参数 p
        });
        return 0;
    }

    /// <summary>COM 激活标记：系统以 `TileWall.exe -Embedding` 拉起 com:ExeServer 进程。</summary>
    private static bool IsEmbedding(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "-Embedding", StringComparison.OrdinalIgnoreCase);
}
