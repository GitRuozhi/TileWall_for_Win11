using Microsoft.Win32;
using TileWall.Core.Settings;

namespace TileWall.Shell.Interop;

/// <summary>
/// IRunKeyValueStore 的 HKCU Run 键实现（M7 设计 §8.3；unpackaged 下 Microsoft.Win32.Registry 可用）。
/// 值名 TileWall（RegistryLoginStartup.RunValueName）；读写失败上抛，由 RegistryLoginStartup 转为就地报错。
/// </summary>
public sealed class RegistryRunKeyStore : IRunKeyValueStore
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RegistryLoginStartup.RunValueName) as string;
    }

    public void Write(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(RegistryLoginStartup.RunValueName, value, RegistryValueKind.String);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(RegistryLoginStartup.RunValueName, throwOnMissingValue: false);
    }
}
