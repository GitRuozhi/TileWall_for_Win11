using System.Runtime.InteropServices;
using TileWall.Core.Settings;

namespace TileWall.Shell.Interop;

/// <summary>
/// 全局热键注册面（M7 设计 §5.2）：状态真值 + 变更通知（墙 InfoBar / 托盘 tooltip / 设置状态行三处呈现的依据）。
/// 假实现可注入设置窗（T-UIA-⑤ 的 DEBUG 假注册器走同缝）。
/// </summary>
public interface IHotKeyRegistration
{
    /// <summary>当前是否已注册（真值，不缓存「成功」字样）。</summary>
    bool IsRegistered { get; }

    /// <summary>最近一次注册失败原因；null = 已注册（或从未尝试）。</summary>
    string? LastFailureReason { get; }

    /// <summary>注册组合（内部先 Unregister 旧的）。失败返回 false 并填 LastFailureReason。</summary>
    bool TryRegister(HotKeyGesture gesture);

    /// <summary>注销（幂等；未注册时为空操作）。</summary>
    void Unregister();

    /// <summary>注册状态变化（成功/失败/注销）后的刷新钩子。</summary>
    event Action? RegistrationChanged;
}

/// <summary>
/// RegisterHotKey/UnregisterHotKey 封装：落在共享宿主窗上（id=1），带 MOD_NOREPEAT（按住不重复——
/// 官方语义，不自造去抖）。注册/注销严格成对：<see cref="TryRegister"/> 内部先注销旧组合，
/// <see cref="Dispose"/> 是退出/异常路径的兜底注销；ExitCoordinator 释放序中的 Unregister 为常规路径（C11）。
/// </summary>
public sealed class GlobalHotKeyRegistrar : IHotKeyRegistration, IDisposable
{
    private const int HotKeyId = 1;

    private readonly ShellMessageHost _host;

    private bool _disposed;

    public GlobalHotKeyRegistrar(ShellMessageHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public bool IsRegistered { get; private set; }

    public string? LastFailureReason { get; private set; }

    /// <summary>当前生效组合（已注册时非空；tooltip/状态行展示用）。</summary>
    public HotKeyGesture? CurrentGesture { get; private set; }

    public event Action? RegistrationChanged;

    public bool TryRegister(HotKeyGesture gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Unregister(); // 成对原则：先注销旧组合再试新组合

        var modifiers = gesture.Modifiers | Win32Api.ModNorepeat;
        if (Win32Api.RegisterHotKey(_host.Hwnd, HotKeyId, modifiers, gesture.VirtualKey))
        {
            IsRegistered = true;
            LastFailureReason = null;
            CurrentGesture = gesture;
        }
        else
        {
            IsRegistered = false;
            CurrentGesture = null;
            // Windows 文档将大量 Win 组合列为系统保留 [R3]：失败是功能路径（C09 保底入口），非崩溃路径
            LastFailureReason = $"组合可能已被其他程序占用或为系统保留（错误码 {Marshal.GetLastWin32Error()}）";
        }

        RegistrationChanged?.Invoke();
        return IsRegistered;
    }

    public void Unregister()
    {
        if (!IsRegistered)
        {
            return;
        }

        _ = Win32Api.UnregisterHotKey(_host.Hwnd, HotKeyId);
        IsRegistered = false;
        CurrentGesture = null;
        RegistrationChanged?.Invoke();
    }

    /// <summary>兜底注销（进程退出路径的成对收口；常规路径在 ExitCoordinator 释放序中已注销时为空操作）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
    }
}

#if DEBUG
/// <summary>
/// T-UIA-⑤ 假注册器（仅 DEBUG）：环境变量 TILEWALL_FAIL_HOTKEY=1 时经 App 注入，
/// 一律注册失败且如实呈现，用于断言「无虚假成功状态」。
/// </summary>
public sealed class FailingHotKeyRegistration : IHotKeyRegistration
{
    private readonly IHotKeyRegistration _inner;

    public FailingHotKeyRegistration(IHotKeyRegistration inner)
    {
        _inner = inner;
        _inner.RegistrationChanged += () => RegistrationChanged?.Invoke();
    }

    public bool IsRegistered => false;

    public string? LastFailureReason => "（测试）模拟组合被占用";

    public event Action? RegistrationChanged;

    public bool TryRegister(HotKeyGesture gesture)
    {
        _ = _inner.TryRegister(gesture); // 内部真实注销旧组合，随后报告失败
        RegistrationChanged?.Invoke();
        return false;
    }

    public void Unregister() => _inner.Unregister();
}
#endif
