using Microsoft.Win32;
using System.Runtime.Versioning;
using TileWall.Shell.Interop;
using Xunit;

namespace TileWall.Core.Tests.Shell;

[SupportedOSPlatform("windows")]
public sealed class ExplorerContextMenuRegistrationTests
{
    private const string Executable = @"C:\Program Files\TileWall 测试\TileWall.exe";
    private static readonly string[] UrlShellPaths =
        [@"Software\Classes\InternetShortcut\shell", @"Software\Classes\IE.AssocFile.URL\shell"];

    [Fact]
    public void RegistrationCoversFilesDirectoriesAndInternetShortcutsAndReplacesOnlyLegacyVerb()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = new RegistrySandbox();
        SeedVerb(sandbox.Root, ExplorerContextMenuRegistration.LegacyVerbPath, "old-shortcut");
        using (var key = sandbox.Root.CreateSubKey(ExplorerContextMenuRegistration.FileVerbPath))
        {
            foreach (var flag in new[] { "Extended", "LegacyDisable", "ProgrammaticAccessOnly" }) key.SetValue(flag, "");
            using var command = key.CreateSubKey("command");
            command.SetValue("DelegateExecute", "previous-handler");
        }
        using (var other = sandbox.Root.CreateSubKey(@"Software\Classes\lnkfile\shell\OtherApp"))
            other.SetValue(string.Empty, "other application");
        using (var parent = sandbox.Root.CreateSubKey(@"Software\Classes\Directory\shell"))
            parent.SetValue(string.Empty, "open");
        foreach (var shellPath in UrlShellPaths)
        {
            using var shell = sandbox.Root.CreateSubKey(shellPath);
            shell.SetValue("CLSID", "built-in URL handler");
            using var command = shell.CreateSubKey(@"open\command");
            command.SetValue(string.Empty, "original URL open command");
        }
        var notifications = 0;

        Assert.Null(ExplorerContextMenuRegistration.Register("Add to TileWall", sandbox.Root, Executable, () => notifications++));
        Assert.Null(ExplorerContextMenuRegistration.Register("Add to TileWall", sandbox.Root, Executable, () => notifications++));

        foreach (var path in new[] { ExplorerContextMenuRegistration.FileVerbPath, ExplorerContextMenuRegistration.DirectoryVerbPath,
            ExplorerContextMenuRegistration.InternetShortcutVerbPath, ExplorerContextMenuRegistration.UrlAssociationVerbPath })
        {
            using var key = sandbox.Root.OpenSubKey(path)!;
            Assert.Equal("Add to TileWall", key.GetValue(string.Empty));
            Assert.Equal("Add to TileWall", key.GetValue("MUIVerb"));
            Assert.Equal('"' + Executable + '"', key.GetValue("Icon"));
            Assert.Equal("Single", key.GetValue("MultiSelectModel"));
            foreach (var flag in new[] { "Extended", "LegacyDisable", "ProgrammaticAccessOnly" }) Assert.Null(key.GetValue(flag));
            using var command = key.OpenSubKey("command")!;
            Assert.Equal('"' + Executable + "\" --add \"%1\"", command.GetValue(string.Empty));
            Assert.Null(command.GetValue("DelegateExecute"));
        }
        Assert.Null(sandbox.Root.OpenSubKey(ExplorerContextMenuRegistration.LegacyVerbPath));
        using var otherAfter = sandbox.Root.OpenSubKey(@"Software\Classes\lnkfile\shell\OtherApp")!;
        Assert.Equal("other application", otherAfter.GetValue(string.Empty));
        using var parentAfter = sandbox.Root.OpenSubKey(@"Software\Classes\Directory\shell")!;
        Assert.Equal("open", parentAfter.GetValue(string.Empty));
        Assert.Equal(new[] { "TileWall.AddToWall" }, parentAfter.GetSubKeyNames());
        foreach (var shellPath in UrlShellPaths)
        {
            using var shell = sandbox.Root.OpenSubKey(shellPath)!;
            Assert.Equal("built-in URL handler", shell.GetValue("CLSID"));
            Assert.Equal(new[] { "open", "TileWall.AddToWall" }, shell.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase));
            using var command = shell.OpenSubKey(@"open\command")!;
            Assert.Equal("original URL open command", command.GetValue(string.Empty));
        }
        Assert.Null(sandbox.Root.OpenSubKey(@"Software\Classes\Directory\Background"));
        Assert.Null(sandbox.Root.OpenSubKey(@"Software\Classes\Drive"));
        Assert.Null(sandbox.Root.OpenSubKey(@"Software\Classes\Folder"));
        Assert.Equal(2, notifications);
    }

    [Theory]
    [InlineData(ExplorerContextMenuRegistration.DirectoryVerbPath)]
    [InlineData(ExplorerContextMenuRegistration.InternetShortcutVerbPath)]
    [InlineData(ExplorerContextMenuRegistration.UrlAssociationVerbPath)]
    [InlineData(ExplorerContextMenuRegistration.LegacyVerbPath)]
    public void FailureAfterMultipleKeyChangesRestoresOldVerbsAndRemovesNewKeys(string failAfter)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = new RegistrySandbox();
        SeedVerb(sandbox.Root, ExplorerContextMenuRegistration.FileVerbPath, "old-file");
        SeedVerb(sandbox.Root, ExplorerContextMenuRegistration.InternetShortcutVerbPath, "old-url");
        SeedVerb(sandbox.Root, ExplorerContextMenuRegistration.LegacyVerbPath, "old-shortcut");
        var notifications = 0;

        var error = ExplorerContextMenuRegistration.Register("Add to TileWall", sandbox.Root, Executable,
            () => notifications++, path =>
            {
                if (path == failAfter) throw new IOException("Injected registry write failure.");
            });

        Assert.Contains("Injected registry write failure.", error);
        AssertSeedVerb(sandbox.Root, ExplorerContextMenuRegistration.FileVerbPath, "old-file");
        AssertSeedVerb(sandbox.Root, ExplorerContextMenuRegistration.InternetShortcutVerbPath, "old-url");
        AssertSeedVerb(sandbox.Root, ExplorerContextMenuRegistration.LegacyVerbPath, "old-shortcut");
        Assert.Null(sandbox.Root.OpenSubKey(ExplorerContextMenuRegistration.DirectoryVerbPath));
        Assert.Null(sandbox.Root.OpenSubKey(ExplorerContextMenuRegistration.UrlAssociationVerbPath));
        Assert.Equal(1, notifications);
    }

    private static void SeedVerb(RegistryKey root, string path, string title)
    {
        using var key = root.CreateSubKey(path);
        key.SetValue(string.Empty, title);
        key.SetValue("Unexpanded", @"%TEMP%\original", RegistryValueKind.ExpandString);
        using var child = key.CreateSubKey("custom-child");
        child.SetValue("Bytes", new byte[] { 3, 7, 9 }, RegistryValueKind.Binary);
    }

    private static void AssertSeedVerb(RegistryKey root, string path, string title)
    {
        using var key = root.OpenSubKey(path)!;
        Assert.Equal(new[] { string.Empty, "Unexpanded" }, key.GetValueNames().Order(StringComparer.Ordinal));
        Assert.Equal(title, key.GetValue(string.Empty));
        Assert.Equal(@"%TEMP%\original", key.GetValue("Unexpanded", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("Unexpanded"));
        Assert.Equal(new[] { "custom-child" }, key.GetSubKeyNames());
        using var child = key.OpenSubKey("custom-child")!;
        Assert.Equal(new byte[] { 3, 7, 9 }, Assert.IsType<byte[]>(child.GetValue("Bytes")));
        Assert.Equal(RegistryValueKind.Binary, child.GetValueKind("Bytes"));
    }

    private sealed class RegistrySandbox : IDisposable
    {
        // All relative production paths are nested beneath this random, test-owned HKCU key.
        private readonly string _path = @"Software\TileWall.Tests\ContextMenu-" + Guid.NewGuid().ToString("N");
        public RegistryKey Root { get; }

        public RegistrySandbox() => Root = Registry.CurrentUser.CreateSubKey(_path);

        public void Dispose()
        {
            Root.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
        }
    }
}
