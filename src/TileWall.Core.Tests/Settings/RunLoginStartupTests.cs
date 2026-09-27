using TileWall.Core.Settings;
using Xunit;

namespace TileWall.Core.Tests.Settings;

public sealed class RunLoginStartupTests
{
    private const string Exe = @"C:\Apps\TileWall\TileWall.exe";

    [Fact]
    public void ValueNameMatchesCurrentUnpackagedRunRegistration()
    {
        Assert.Equal("TileWall", LoginStartupNames.RunValueName);
    }

    [Fact]
    public void EnableWritesQuotedExecutableAndBackgroundArgumentThenReadsRegistryTruth()
    {
        var store = new FakeRunKeyStore();
        var login = new RunLoginStartup(store);
        Assert.False(login.Read().Enabled);
        Assert.Equal(new RunLoginState(true), login.Set(true, Exe));
        Assert.Equal($"\"{Exe}\" --background", store.Value);
        Assert.True(login.Read().Enabled);
    }

    [Fact]
    public void TestRegistrationKeepsItsIsolatedRootInCommand()
    {
        var store = new FakeRunKeyStore();
        var login = new RunLoginStartup(store);
        var root = @"C:\Temp\tilewall-test";
        Assert.True(login.Set(true, Exe, root).Enabled);
        Assert.Equal($"\"{Exe}\" --background --test-root \"{root}\"", store.Value);
    }

    [Fact]
    public void DisableDeletesValueAndRepeatedDisableLeavesNoResidue()
    {
        var store = new FakeRunKeyStore { Value = "existing" };
        var login = new RunLoginStartup(store);
        Assert.Equal(new RunLoginState(false), login.Set(false, string.Empty));
        Assert.Equal(new RunLoginState(false), login.Set(false, string.Empty));
        Assert.Null(store.Value);
    }

    [Fact]
    public void WriteFailureReturnsActualOffStateAndErrorWithoutChangingValue()
    {
        var store = new FakeRunKeyStore { ThrowOnWrite = true };
        var result = new RunLoginStartup(store).Set(true, Exe);
        Assert.False(result.Enabled);
        Assert.Contains("denied", result.Error);
        Assert.Null(store.Value);
    }

    [Fact]
    public void ReadFailureRemainsUnknownRatherThanReportingFalseSuccess()
    {
        var store = new FakeRunKeyStore { ThrowOnRead = true };
        var result = new RunLoginStartup(store).Read();
        Assert.Null(result.Enabled);
        Assert.Contains("unavailable", result.Error);
    }

    private sealed class FakeRunKeyStore : IRunKeyValueStore
    {
        public string? Value { get; set; }
        public bool ThrowOnWrite { get; set; }
        public bool ThrowOnRead { get; set; }
        public string? Read() => ThrowOnRead ? throw new IOException("Run value unavailable") : Value;
        public void Write(string value)
        {
            if (ThrowOnWrite) throw new IOException("Run write denied");
            Value = value;
        }
        public void Delete() => Value = null;
    }
}
