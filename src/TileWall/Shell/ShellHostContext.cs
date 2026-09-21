using TileWall.Core.Settings;
using TileWall.Shell.Interop;

namespace TileWall.Shell;

/// <summary>
/// M7 宿主层注入面（App 装配 §2.2 第 2–5 步的产物 → MainWindow）：
/// 路由器/退出序列/设置窗全部只经本面触达 Win32 资源，互操作细节隔离在 Shell/Interop/*。
/// </summary>
/// <param name="MessageHost">共享隐藏宿主窗（托盘回调/热键/二次启动/TaskbarCreated 的 HWND）。</param>
/// <param name="HotKeys">全局热键注册面（注册失败就地真实状态三呈现的依据）。</param>
/// <param name="Tray">托盘宿主（NIM_ADD 失败时对象仍存活，仅 IsAdded=false 的失真路径）。</param>
/// <param name="LoginStartup">登录后启动开关（注册表唯一真值源）。</param>
/// <param name="SingleInstance">单实例门（互斥体 + 二次启动信号通道，M9 起含 WM_COPYDATA 路径投递）。</param>
/// <param name="IconCacheRoot">图标缓存根基准（M9 §3.2：packaged=LocalCacheFolder，unpackaged=数据根；IconCache 自拼 Cache/Icons）。</param>
public sealed record ShellHostContext(
    ShellMessageHost MessageHost,
    IHotKeyRegistration HotKeys,
    ShellNotifyIconHost Tray,
    ILoginStartup LoginStartup,
    SingleInstanceGate SingleInstance,
    string IconCacheRoot)
{
    /// <summary>配置里的快捷键原文（未配置/非法时由消费方自行降级呈现）。</summary>
    public string? ConfiguredHotKey { get; init; }
}
