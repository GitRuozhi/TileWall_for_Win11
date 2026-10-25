using Microsoft.Win32;
using System.Runtime.Versioning;
using TileWall.Shell.Interop;
using Xunit;

namespace TileWall.Core.Tests.Shell;

[SupportedOSPlatform("windows")]
public sealed class ExplorerContextMenuRegistrationTests
{
    private const string Executable = @"C:\Program Files\TileWall 测试\TileWall.exe";
    private const string OtherExecutable = @"D:\Portable\TileWall.exe";
    private static readonly string[] Paths = [ExplorerContextMenuRegistration.FileVerbPath,
        ExplorerContextMenuRegistration.DirectoryVerbPath, ExplorerContextMenuRegistration.UrlVerbPath];
    private static readonly string[] LegacyPaths = [ExplorerContextMenuRegistration.LegacyInternetShortcutVerbPath,
        ExplorerContextMenuRegistration.LegacyUrlAssociationVerbPath, ExplorerContextMenuRegistration.LegacyVerbPath];

    [Fact]
    public void ProductionRegistrationIs64BitCurrentUserAndLockIsUserScoped()
    {
        Assert.Equal(RegistryHive.CurrentUser, ExplorerContextMenuRegistration.RegistrationHive);
        Assert.Equal(RegistryView.Registry64, ExplorerContextMenuRegistration.RegistrationView);
        var firstUser = ExplorerContextMenuRegistration.WriteMutexName("S-1-5-21-1001");
        Assert.Equal(firstUser, ExplorerContextMenuRegistration.WriteMutexName("S-1-5-21-1001"));
        Assert.NotEqual(firstUser, ExplorerContextMenuRegistration.WriteMutexName("S-1-5-21-1002"));
    }

    [Fact]
    public void RegistrationWritesThreeClassesAndDoesNotTouchLegacyOrParentHandlers()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = new RegistrySandbox();
        foreach (var path in LegacyPaths) SeedVerb(sandbox.Root, path, Executable);
        using (var parent = sandbox.Root.CreateSubKey(@"Software\Classes\Directory\shell")) parent.SetValue(string.Empty, "open");
        using (var other = sandbox.Root.CreateSubKey(@"Software\Classes\InternetShortcut\shell\open\command")) other.SetValue(string.Empty, "original URL open command");
        var notifications = 0;
        for (var repeat = 0; repeat < 2; repeat++)
            Assert.True(ExplorerContextMenuRegistration.RegisterCurrentUser("Add to TileWall", sandbox.Root, Executable, () => notifications++, _ => false).Success);
        foreach (var path in Paths)
        {
            using var key = sandbox.Root.OpenSubKey(path)!;
            Assert.Equal("Add to TileWall", key.GetValue(string.Empty));
            Assert.Equal("Add to TileWall", key.GetValue("MUIVerb"));
            Assert.Equal('"' + Executable + '"', key.GetValue("Icon"));
            Assert.Equal("Single", key.GetValue("MultiSelectModel"));
            if (path == ExplorerContextMenuRegistration.UrlVerbPath)
            {
                Assert.Equal("System.FileExtension:=\".url\"", key.GetValue("AppliesTo"));
                Assert.Equal(RegistryValueKind.String, key.GetValueKind("AppliesTo"));
            }
            else Assert.Null(key.GetValue("AppliesTo"));
            using var command = key.OpenSubKey("command")!;
            Assert.Equal('"' + Executable + "\" --add \"%1\"", command.GetValue(string.Empty));
        }
        foreach (var path in LegacyPaths) AssertSeedVerb(sandbox.Root, path, Executable);
        using var parentAfter = sandbox.Root.OpenSubKey(@"Software\Classes\Directory\shell")!;
        Assert.Equal("open", parentAfter.GetValue(string.Empty));
        using var openAfter = sandbox.Root.OpenSubKey(@"Software\Classes\InternetShortcut\shell\open\command")!;
        Assert.Equal("original URL open command", openAfter.GetValue(string.Empty));
        Assert.Null(sandbox.Root.OpenSubKey(@"Software\Classes\Directory\Background"));
        Assert.Equal(2, notifications);
        Assert.Equal(ContextMenuStatus.CurrentExecutable, State(sandbox).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("\"D:\\Other\\TileWall.exe\" --add \"%1\"")]
    [InlineData("\"C:\\Program Files\\TileWall 测试\\TileWall.exe\" --add \"%1\" --background")]
    public void UnknownOrAdditionalArgumentCommandBlocksAllEnableWrites(string? commandText)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = new RegistrySandbox();
        using (var key = sandbox.Root.CreateSubKey(Paths[2]))
        {
            key.SetValue(string.Empty, "external");
            if (commandText is not null)
            {
                using var command = key.CreateSubKey("command");
                command.SetValue(string.Empty, commandText);
            }
        }
        var notifications = 0;
        var result = ExplorerContextMenuRegistration.RegisterCurrentUser("Add", sandbox.Root, Executable, () => notifications++, _ => false);
        Assert.False(result.Success);
        Assert.False(result.RollbackIncomplete);
        Assert.Null(sandbox.Root.OpenSubKey(Paths[0]));
        using var preserved = sandbox.Root.OpenSubKey(Paths[2])!;
        Assert.Equal("external", preserved.GetValue(string.Empty));
        Assert.Equal(0, notifications);
        Assert.True(State(sandbox).HasUnrecognizedConflict);
    }

    [Theory]
    [InlineData(ExplorerContextMenuRegistration.DirectoryVerbPath)]
    [InlineData(ExplorerContextMenuRegistration.UrlVerbPath)]
    public void FailedEnableRestoresCompleteSnapshotsAndRemovesOnlyNewVerbs(string failAfter)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = new RegistrySandbox();
        SeedVerb(sandbox.Root, Paths[0], Executable);
        SeedVerb(sandbox.Root, Paths[2], OtherExecutable);
        var result = ExplorerContextMenuRegistration.RegisterCurrentUser("Add", sandbox.Root, Executable, () => { }, _ => true,
            path => { if (path == failAfter) throw new IOException("injected write"); });
        Assert.False(result.Success);
        Assert.False(result.RollbackIncomplete);
        Assert.Contains("injected write", result.Error);
        AssertSeedVerb(sandbox.Root, Paths[0], Executable);
        AssertSeedVerb(sandbox.Root, Paths[2], OtherExecutable);
        Assert.Null(sandbox.Root.OpenSubKey(Paths[1]));
    }

    [Fact]
    public void RollbackFailureIsDistinctAndOtherKeysStillRestore()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = new RegistrySandbox();
        SeedVerb(sandbox.Root, Paths[0], Executable);
        var result = ExplorerContextMenuRegistration.RegisterCurrentUser("Add", sandbox.Root, Executable, () => { }, _ => false,
            path => { if (path == Paths[2]) throw new IOException("write failed"); },
            path => { if (path == Paths[1]) throw new UnauthorizedAccessException("rollback denied"); });
        Assert.False(result.Success);
        Assert.True(result.RollbackIncomplete);
        Assert.Contains("rollback denied", result.Error);
        AssertSeedVerb(sandbox.Root, Paths[0], Executable);
        Assert.Null(sandbox.Root.OpenSubKey(Paths[2]));
    }

    [Fact]
    public void DisableDeletesOnlyExactCurrentCommandAndLegacyCleanupIsExplicit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = new RegistrySandbox();
        SeedVerb(sandbox.Root, Paths[0], Executable);
        SeedVerb(sandbox.Root, Paths[1], OtherExecutable);
        SeedVerb(sandbox.Root, Paths[2], Executable);
        using (var command = sandbox.Root.OpenSubKey(Paths[2] + @"\command", writable: true)!)
            command.SetValue(string.Empty, ExplorerContextMenuRegistration.BuildCommand(Executable) + " --background");
        foreach (var path in LegacyPaths) SeedVerb(sandbox.Root, path, Executable);
        using (var command = sandbox.Root.OpenSubKey(ExplorerContextMenuRegistration.LegacyUrlAssociationVerbPath + @"\command", writable: true)!)
            command.SetValue(string.Empty, ExplorerContextMenuRegistration.BuildCommand(OtherExecutable));
        Assert.True(ExplorerContextMenuRegistration.UnregisterOwned(sandbox.Root, Executable, () => { }).Success);
        Assert.Null(sandbox.Root.OpenSubKey(Paths[0]));
        AssertSeedVerb(sandbox.Root, Paths[1], OtherExecutable);
        using (var preserved = sandbox.Root.OpenSubKey(Paths[2])) Assert.NotNull(preserved);
        AssertSeedVerb(sandbox.Root, ExplorerContextMenuRegistration.LegacyInternetShortcutVerbPath, Executable);
        AssertSeedVerb(sandbox.Root, ExplorerContextMenuRegistration.LegacyUrlAssociationVerbPath, OtherExecutable);
        AssertSeedVerb(sandbox.Root, ExplorerContextMenuRegistration.LegacyVerbPath, Executable);
        Assert.True(ExplorerContextMenuRegistration.UnregisterOwned(sandbox.Root, Executable, () => { }, includeLegacy: true).Success);
        Assert.Null(sandbox.Root.OpenSubKey(ExplorerContextMenuRegistration.LegacyInternetShortcutVerbPath));
        AssertSeedVerb(sandbox.Root, ExplorerContextMenuRegistration.LegacyUrlAssociationVerbPath, OtherExecutable);
        Assert.Null(sandbox.Root.OpenSubKey(ExplorerContextMenuRegistration.LegacyVerbPath));
        Assert.True(ExplorerContextMenuRegistration.UnregisterOwned(sandbox.Root, Executable, () => { }, includeLegacy: true).Success);
    }

    [Fact]
    public void FailedDisableRestoresDeletedKeys()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = new RegistrySandbox();
        foreach (var path in Paths) SeedVerb(sandbox.Root, path, Executable);
        var result = ExplorerContextMenuRegistration.UnregisterOwned(sandbox.Root, Executable, () => { },
            path => { if (path == Paths[2]) throw new UnauthorizedAccessException("delete denied"); });
        Assert.False(result.Success);
        Assert.False(result.RollbackIncomplete);
        foreach (var path in Paths) AssertSeedVerb(sandbox.Root, path, Executable);
    }

    [Fact]
    public void FailedPostRemovalHandoffRestoresAllOwnedSnapshotsAndPreservesOtherTargets()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = new RegistrySandbox();
        foreach (var path in Paths.Concat(LegacyPaths)) SeedVerb(sandbox.Root, path, Executable);
        SeedVerb(sandbox.Root, LegacyPaths[1], OtherExecutable);
        var owned = Paths.Concat(LegacyPaths.Where(path => path != LegacyPaths[1])).ToArray();
        var notifications = 0;
        var handoffs = 0;
        var result = ExplorerContextMenuRegistration.UnregisterOwned(sandbox.Root, Executable,
            () => notifications++, includeLegacy: true, afterMutation: () =>
            {
                handoffs++;
                Assert.Equal(0, notifications);
                foreach (var path in owned)
                {
                    using var removed = sandbox.Root.OpenSubKey(path);
                    Assert.Null(removed);
                }
                AssertSeedVerb(sandbox.Root, LegacyPaths[1], OtherExecutable);
                // A launch failure need not be a registry exception; it must still restore the menu.
                throw new InvalidOperationException("uninstall handoff failed");
            });
        Assert.False(result.Success);
        Assert.False(result.RollbackIncomplete);
        Assert.Contains("uninstall handoff failed", result.Error);
        Assert.Equal(1, handoffs);
        Assert.Equal(1, notifications);
        foreach (var path in owned) AssertSeedVerb(sandbox.Root, path, Executable);
        AssertSeedVerb(sandbox.Root, LegacyPaths[1], OtherExecutable);
    }

    [Fact]
    public void RemovalHandoffRunsWhenNoOwnedMenuExistsAndBeforeSuccessfulNotification()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = new RegistrySandbox();
        var notifications = 0;
        var handoffs = 0;
        Assert.True(ExplorerContextMenuRegistration.UnregisterOwned(sandbox.Root, Executable,
            () => notifications++, afterMutation: () => handoffs++).Success);
        Assert.Equal(1, handoffs);
        Assert.Equal(0, notifications);

        foreach (var path in Paths) SeedVerb(sandbox.Root, path, Executable);
        Assert.True(ExplorerContextMenuRegistration.UnregisterOwned(sandbox.Root, Executable,
            () => notifications++, afterMutation: () =>
            {
                Assert.Equal(0, notifications);
                foreach (var path in Paths)
                {
                    using var removed = sandbox.Root.OpenSubKey(path);
                    Assert.Null(removed);
                }
                handoffs++;
            }).Success);
        Assert.Equal(2, handoffs);
        Assert.Equal(1, notifications);
    }

    [Theory]
    [InlineData(ExplorerContextMenuRegistration.LegacyInternetShortcutVerbPath)]
    [InlineData(ExplorerContextMenuRegistration.LegacyUrlAssociationVerbPath)]
    [InlineData(ExplorerContextMenuRegistration.LegacyVerbPath)]
    public void FailedLegacyDisableRestoresActiveAndLegacyKeys(string failAfter)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = new RegistrySandbox();
        foreach (var path in Paths.Concat(LegacyPaths)) SeedVerb(sandbox.Root, path, Executable);
        var result = ExplorerContextMenuRegistration.UnregisterOwned(sandbox.Root, Executable, () => { },
            path => { if (path == failAfter) throw new UnauthorizedAccessException("legacy delete denied"); },
            includeLegacy: true);
        Assert.False(result.Success);
        Assert.False(result.RollbackIncomplete);
        Assert.Contains("legacy delete denied", result.Error);
        foreach (var path in Paths.Concat(LegacyPaths)) AssertSeedVerb(sandbox.Root, path, Executable);
    }

    [Fact]
    public void StateDistinguishesAbsentCurrentOtherPartialMixedAndConflict()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = new RegistrySandbox();
        Assert.Equal(ContextMenuStatus.NotRegistered, State(sandbox).Status);
        Assert.True(ExplorerContextMenuRegistration.RegisterCurrentUser("Add", sandbox.Root, Executable, () => { }, _ => true).Success);
        Assert.Equal(ContextMenuStatus.CurrentExecutable, State(sandbox).Status);
        Assert.True(ExplorerContextMenuRegistration.RegisterCurrentUser("Add", sandbox.Root, OtherExecutable, () => { }, _ => true).Success);
        Assert.Equal(ContextMenuStatus.OtherExecutable, State(sandbox).Status);
        using (var command = sandbox.Root.OpenSubKey(Paths[0] + @"\command", writable: true)!) command.SetValue(string.Empty, ExplorerContextMenuRegistration.BuildCommand(Executable));
        Assert.Equal(ContextMenuStatus.PartialOrMixed, State(sandbox).Status);
        sandbox.Root.DeleteSubKeyTree(Paths[0]);
        Assert.Equal(ContextMenuStatus.PartialOrMixed, State(sandbox).Status);
        using (var command = sandbox.Root.OpenSubKey(Paths[1] + @"\command", writable: true)!) command.SetValue("DelegateExecute", "foreign-handler");
        Assert.True(State(sandbox).HasUnrecognizedConflict);
    }

    [Theory]
    [InlineData(ExplorerContextMenuRegistration.UrlVerbPath, null)]
    [InlineData(ExplorerContextMenuRegistration.UrlVerbPath, "")]
    [InlineData(ExplorerContextMenuRegistration.UrlVerbPath, "System.FileExtension:=\".txt\"")]
    [InlineData(ExplorerContextMenuRegistration.FileVerbPath, ExplorerContextMenuRegistration.UrlAppliesTo)]
    [InlineData(ExplorerContextMenuRegistration.DirectoryVerbPath, ExplorerContextMenuRegistration.UrlAppliesTo)]
    public void IncorrectScopeFilterReadsPartialAndEnableRepairsIt(string path, string? appliesTo)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = new RegistrySandbox();
        Assert.True(ExplorerContextMenuRegistration.RegisterCurrentUser("Add", sandbox.Root, Executable, () => { }, _ => false).Success);
        using (var key = sandbox.Root.OpenSubKey(path, writable: true)!)
        {
            if (appliesTo is null) key.DeleteValue("AppliesTo", throwOnMissingValue: false);
            else key.SetValue("AppliesTo", appliesTo, RegistryValueKind.String);
        }
        var state = State(sandbox);
        Assert.Equal(ContextMenuStatus.PartialOrMixed, state.Status);
        Assert.False(state.HasUnrecognizedConflict);

        Assert.True(ExplorerContextMenuRegistration.RegisterCurrentUser("Add", sandbox.Root, Executable, () => { }, _ => false).Success);
        Assert.Equal(ContextMenuStatus.CurrentExecutable, State(sandbox).Status);
        using var repaired = sandbox.Root.OpenSubKey(path)!;
        Assert.Equal(path == ExplorerContextMenuRegistration.UrlVerbPath ? ExplorerContextMenuRegistration.UrlAppliesTo : null,
            repaired.GetValue("AppliesTo"));
    }

    [Theory]
    [InlineData("TileWall.exe --add \"%1\"")]
    [InlineData("\" --add \"%1\"")]
    [InlineData("\"\" --add \"%1\"")]
    [InlineData("\"C:\\TileWall.exe\" --add %1")]
    [InlineData("\"C:\\TileWall.exe\" --add \"%1\" extra")]
    [InlineData("\"C:\\TileWall.exe\" --background --add \"%1\"")]
    public void CommandParserRejectsNonTemplateCommands(string command) => Assert.False(ExplorerContextMenuRegistration.TryParseCommand(command, out _));

    private static ContextMenuState State(RegistrySandbox sandbox) => ExplorerContextMenuRegistration.ReadCurrentUserState(sandbox.Root, Executable, target => target == OtherExecutable);

    private static void SeedVerb(RegistryKey root, string path, string executable)
    {
        using var key = root.CreateSubKey(path);
        key.SetValue(string.Empty, "old label");
        key.SetValue("Unexpanded", @"%TEMP%\original", RegistryValueKind.ExpandString);
        using var child = key.CreateSubKey("custom-child");
        child.SetValue("Bytes", new byte[] { 3, 7, 9 }, RegistryValueKind.Binary);
        using var command = key.CreateSubKey("command");
        command.SetValue(string.Empty, ExplorerContextMenuRegistration.BuildCommand(executable));
    }

    private static void AssertSeedVerb(RegistryKey root, string path, string executable)
    {
        using var key = root.OpenSubKey(path)!;
        Assert.Equal("old label", key.GetValue(string.Empty));
        Assert.Equal(@"%TEMP%\original", key.GetValue("Unexpanded", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("Unexpanded"));
        using var child = key.OpenSubKey("custom-child")!;
        Assert.Equal(new byte[] { 3, 7, 9 }, Assert.IsType<byte[]>(child.GetValue("Bytes")));
        Assert.Equal(RegistryValueKind.Binary, child.GetValueKind("Bytes"));
        using var command = key.OpenSubKey("command")!;
        Assert.Equal(ExplorerContextMenuRegistration.BuildCommand(executable), command.GetValue(string.Empty));
    }

    private sealed class RegistrySandbox : IDisposable
    {
        private readonly string _path = @"Software\TileWall.Tests\ContextMenu-" + Guid.NewGuid().ToString("N");
        public RegistryKey Root { get; } = null!;
        public RegistrySandbox() => Root = Registry.CurrentUser.CreateSubKey(_path);
        public void Dispose()
        {
            Root.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
        }
    }
}
