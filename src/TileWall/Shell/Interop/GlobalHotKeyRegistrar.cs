using System.Runtime.InteropServices;
using TileWall.Core.Settings;

namespace TileWall.Shell.Interop;

/// <summary>
/// RegisterHotKey/UnregisterHotKey 封装：落在共享宿主窗的独立槽（id=1/2），带 MOD_NOREPEAT（按住不重复——
/// 官方语义，不自造去抖）。注册/注销严格成对：<see cref="TryRegister"/> 内部先注销旧组合，
/// <see cref="Dispose"/> 是退出/异常路径的兜底注销；ExitCoordinator 释放序中的 Unregister 为常规路径（C11）。
/// </summary>
public sealed class GlobalHotKeyRegistrar : IDisposable
{
    private readonly int _hotKeyId;

    private readonly ShellMessageHost _host;

    private bool _disposed;

    public GlobalHotKeyRegistrar(ShellMessageHost host, int hotKeyId)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (hotKeyId is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(hotKeyId));
        _host = host;
        _hotKeyId = hotKeyId;
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
        if (Win32Api.RegisterHotKey(_host.Hwnd, _hotKeyId, modifiers, gesture.VirtualKey))
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

        _ = Win32Api.UnregisterHotKey(_host.Hwnd, _hotKeyId);
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
