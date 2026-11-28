using System.Runtime.Versioning;
using TileWall.Core.Shell;
using TileWall.Core.Tests.Fixtures;
using TileWall.Shell.Interop;
using Xunit;
using static TileWall.Shell.Interop.ExplorerContextMenuRegistration;

namespace TileWall.Core.Tests.Shell;

[SupportedOSPlatform("windows")]
public sealed class ExplorerContextMenuHandoffTests
{
    private static readonly string InstalledExe = Path.Combine(Path.GetTempPath(), "installed", "TileWall.exe");
    private static readonly string PortableExe = Path.Combine(Path.GetTempPath(), "portable", "TileWall.exe");
    private const string NewLabel = "Add to TileWall";
    private const string OldLabel = "添加到 TileWall";

    [Fact]
    public void InstallationTakesOverKnownClassicAndStoreMenusBeforeReturningSuccess()
    {
        var fixture = new Fixture();
        fixture.Registry.Write(FileVerbPath, Verb(FileVerbPath, PortableExe, OldLabel));
        fixture.Registry.Write(LegacyInternetShortcutVerbPath, Verb(LegacyInternetShortcutVerbPath, PortableExe, OldLabel));
        fixture.Registry.BeforeDelete = _ => Assert.False(fixture.Preference.ReadOverride());

        var result = fixture.Handoff.EnableForInstallation(NewLabel);

        Assert.True(result.Success, result.Error);
        fixture.AssertInstalled();
        Assert.Equal(1, fixture.Notifications);
    }

    [Fact]
    public void UnknownOwnershipLeavesBothProvidersAndPendingMaterialUntouched()
    {
        var fixture = new Fixture();
        var own = Verb(FileVerbPath, InstalledExe, OldLabel);
        var unknown = Verb(DirectoryVerbPath, Path.Combine(Path.GetTempPath(), "unknown", "TileWall.exe"), OldLabel);
        fixture.Registry.Write(FileVerbPath, own);
        fixture.Registry.Write(DirectoryVerbPath, unknown);

        var result = fixture.Handoff.EnableForInstallation(NewLabel);

        Assert.False(result.Success);
        Assert.False(result.RollbackIncomplete);
        Assert.Contains("Unrecognized", result.Error);
        Assert.Equal(own, fixture.Registry.Read(FileVerbPath));
        Assert.Equal(unknown, fixture.Registry.Read(DirectoryVerbPath));
        Assert.Null(fixture.Preference.ReadOverride());
        Assert.Null(fixture.Preference.ReadHandoff());
        Assert.Equal(0, fixture.Notifications);
    }

    [Fact]
    public void FailedRegistrationReadbackRestoresPriorOwnersIncludingThisInstallation()
    {
        var fixture = new Fixture();
        var own = Verb(FileVerbPath, InstalledExe, OldLabel);
        var previous = Verb(DirectoryVerbPath, PortableExe, OldLabel);
        fixture.Registry.Write(FileVerbPath, own);
        fixture.Registry.Write(DirectoryVerbPath, previous);
        fixture.Registry.SkipValue = path => path == UrlVerbPath + @"\command";

        var result = fixture.Handoff.EnableForInstallation(NewLabel);

        Assert.False(result.Success);
        Assert.False(result.RollbackIncomplete);
        Assert.Contains("verify", result.Error);
        Assert.Equal(own, fixture.Registry.Read(FileVerbPath));
        Assert.Equal(previous, fixture.Registry.Read(DirectoryVerbPath));
        Assert.Null(fixture.Registry.Read(UrlVerbPath));
        Assert.Null(fixture.Preference.ReadOverride());
        Assert.Null(fixture.Preference.ReadHandoff());
        Assert.Equal(1, fixture.Notifications);
    }

    [Fact]
    public void ExplicitInstallationRetryResolvesInterruptedTransferAndCompletesThisAttempt()
    {
        var fixture = new Fixture();
        fixture.Registry.Write(FileVerbPath, Verb(FileVerbPath, PortableExe, OldLabel));
        fixture.Registry.SkipValue = path => path == DirectoryVerbPath + @"\command"
            ? throw new TestInterruption() : false;
        Assert.Throws<TestInterruption>(() => fixture.Handoff.EnableForInstallation(NewLabel));
        Assert.NotNull(fixture.Preference.ReadHandoff());
        Assert.False(fixture.Preference.ReadOverride());

        fixture.Registry.SkipValue = null;
        var result = fixture.Handoff.EnableForInstallation(NewLabel);

        Assert.True(result.Success, result.Error);
        fixture.AssertInstalled();
        Assert.Equal(2, fixture.Notifications);
    }

    private static ExplorerMenuVerb Verb(string path, string executable, string label) =>
        new(label, label, $"\"{executable}\"", "Single", path == UrlVerbPath ? UrlAppliesTo : null,
            BuildCommand(executable));

    private sealed class TestInterruption : Exception;

    private sealed class Fixture
    {
        internal MemoryRegistry Registry { get; } = new();
        internal ExplorerMenuPreference Preference { get; } = new(new InMemoryFileStore(),
            Path.Combine(Path.GetTempPath(), "TileWall.ExplorerHandoff.Tests"));
        internal int Notifications { get; private set; }
        internal ExplorerContextMenuHandoff Handoff =>
            new(Preference, false, InstalledExe, () => Registry, () => true, path => path == PortableExe,
                () => Notifications++);

        internal void AssertInstalled()
        {
            foreach (var path in HandoffPaths.Take(4)) Assert.Equal(Verb(path, InstalledExe, NewLabel), Registry.Read(path));
            foreach (var path in HandoffPaths.Skip(4)) Assert.Null(Registry.Read(path));
            Assert.False(Preference.ReadOverride());
            Assert.Null(Preference.ReadHandoff());
        }
    }

    private sealed class MemoryRegistry : ExplorerContextMenuRegistry
    {
        private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.Ordinal);
        internal Action<string>? BeforeDelete { get; set; }
        internal Func<string, bool>? SkipValue { get; set; }

        protected override Key? ReadKey(string path)
        {
            if (!_keys.TryGetValue(path, out var values)) return null;
            var children = _keys.ContainsKey(path + @"\command") ? new[] { "command" } : [];
            return new(children, values);
        }

        protected override void DeleteVerb(string path, string commandPath)
        {
            BeforeDelete?.Invoke(path);
            _keys.Remove(commandPath);
            _keys.Remove(path);
        }

        protected override void CreateKey(string path) => _keys.Add(path, new(StringComparer.Ordinal));

        protected override void SetString(string path, string name, string value)
        {
            if (SkipValue?.Invoke(path) != true) _keys[path].Add(name, value);
        }

        public override void Dispose() { }
    }
}
