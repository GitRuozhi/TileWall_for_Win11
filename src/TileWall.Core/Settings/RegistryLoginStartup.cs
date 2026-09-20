namespace TileWall.Core.Settings;

/// <summary>
/// HKCU Run 键值的读写 seam：真实实现（Microsoft.Win32.Registry）在 Shell 层，
/// Core 保持零 Windows 依赖；单测以假实现捕获写入内容。
/// </summary>
public interface IRunKeyValueStore
{
    /// <summary>读值；不存在 → null。读失败可上抛。</summary>
    string? Read();

    /// <summary>写值（失败上抛，调用方就地报错且 UI 保持原态）。</summary>
    void Write(string value);

    /// <summary>删值；值本就不存在时静默成功。</summary>
    void Delete();
}

/// <summary>登录后启动开关（设置窗消费；T-LOGIN 测试面）。</summary>
public interface ILoginStartup
{
    /// <summary>当前是否已启用（真值源 = 注册表有值）。</summary>
    bool IsEnabled { get; }

    /// <summary>设置开关。失败返回 false 并给出原因（调用方不把界面显示为已生效）。</summary>
    bool SetEnabled(bool enabled, out string? error);
}

/// <summary>
/// 登录后启动（M7 设计 §8.3）：HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run 值名
/// <see cref="RunValueName"/>，数据 = "&lt;exe 全路径&gt;" --background。
/// 注册表是唯一真值源——config.Settings.RunAtLogin 字段停止消费（schema 保留仅为旧配置反序列化兼容，
/// 两个真值源必然漂移；该偏离已列为向人类报备项，见设计 §14 风险 5）。
/// </summary>
public sealed class RegistryLoginStartup : ILoginStartup
{
    public const string RunValueName = "TileWall";

    public const string BackgroundArgument = "--background";

    private readonly IRunKeyValueStore _store;
    private readonly string _exePath;

    public RegistryLoginStartup(IRunKeyValueStore store, string exePath)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        _store = store;
        _exePath = exePath;
    }

    public bool IsEnabled
    {
        get
        {
            try
            {
                return _store.Read() is not null;
            }
            catch (Exception)
            {
                return false; // 读失败按「未开启」呈现，不假装成功
            }
        }
    }

    public bool SetEnabled(bool enabled, out string? error)
    {
        error = null;
        try
        {
            if (enabled)
            {
                _store.Write(ComposeCommand());
            }
            else
            {
                _store.Delete();
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Run 值命令行：带 --background → 登录启动仅驻留托盘（手动启动仍显示墙）。</summary>
    public string ComposeCommand() => $"\"{_exePath}\" {BackgroundArgument}";
}
