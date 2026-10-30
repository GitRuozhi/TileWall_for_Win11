using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using TileWall.Core.Entries;
using Xunit;

namespace TileWall.Core.Tests.Entries;

public sealed class NativeShellBackgroundTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "Compatibility", "NativeShellBackground", name + ".pidl"));

    private static bool Matches(byte[] fresh, byte[] stored, string arguments = "", string workingDirectory = "")
    {
        var identity = LaunchIdentityService.ForShellIdList(Convert.ToHexString(SHA256.HashData(fresh)));
        var fields = new LinkFields(null, arguments, workingDirectory, null, 0, 1, true)
        { ShellIdListHash = Convert.ToHexString(SHA256.HashData(stored)), ShellParsingName = "Untrusted descriptive value" };
        return NativeShellTargetSnapshot.MatchesBackgroundVariant(fresh, identity, fields, stored);
    }

    [Fact]
    public void CapturedWindowsBackgroundChangeMatchesWithoutChangingEitherRawIdentity()
    {
        var before = Fixture("settings-before"); var after = Fixture("settings-after");
        var beforeHash = SHA256.HashData(before); var afterHash = SHA256.HashData(after);
        Assert.NotEqual(beforeHash, afterHash);
        Assert.True(Matches(after, before));
        Assert.True(Matches(before, after));
        Assert.Equal(beforeHash, SHA256.HashData(before));
        Assert.Equal(afterHash, SHA256.HashData(after));
    }

    [Fact]
    public void CapturedPackageUpgradeRemainsDifferentDespiteCanonicalAppIdentity()
    {
        Assert.False(Matches(Fixture("store-after"), Fixture("store-before")));
    }

    [Theory]
    [InlineData("--different-argument", "")]
    [InlineData("", @"C:\different-working-directory")]
    public void BackgroundChangeDoesNotHideDifferentLaunchFields(string arguments, string workingDirectory)
    {
        Assert.False(Matches(Fixture("settings-after"), Fixture("settings-before"), arguments, workingDirectory));
    }

    [Fact]
    public void DifferentTargetAndAnotherPresentationPropertyDoNotMatch()
    {
        var fresh = Fixture("settings-after"); var stored = Fixture("settings-before");
        var target = Find(stored, Encoding.Unicode.GetBytes("windows.immersivecontrolpanel"));
        Assert.True(target >= 0); stored[target] ^= 1;
        Assert.False(Matches(fresh, stored));

        stored = Fixture("settings-before");
        var foreground = Find(stored, new byte[] { 17, 0, 0, 0, 5, 0, 0, 0, 0, 19, 0, 0, 0 });
        Assert.True(foreground >= 0); stored[foreground + 13] ^= 1;
        Assert.False(Matches(fresh, stored));
    }

    [Theory]
    [InlineData("type")]
    [InlineData("reserved")]
    [InlineData("padding")]
    [InlineData("value-size")]
    [InlineData("duplicate-property")]
    [InlineData("duplicate-format")]
    [InlineData("unknown-envelope")]
    [InlineData("truncated")]
    [InlineData("trailing-data")]
    public void MalformedOrUnknownNativeTemplateNeverAuthorizesAColorException(string fault)
    {
        var fresh = Fixture("settings-after");
        var background = Find(fresh, new byte[] { 17, 0, 0, 0, 4, 0, 0, 0, 0, 19, 0, 0, 0 });
        Assert.True(background >= 0);
        switch (fault)
        {
            case "type": fresh[background + 9] = 31; break;
            case "reserved": fresh[background + 8] = 1; break;
            case "padding": fresh[background + 11] = 1; break;
            case "value-size": BinaryPrimitives.WriteUInt32LittleEndian(fresh.AsSpan(background), uint.MaxValue); break;
            case "duplicate-property":
                var foreground = Find(fresh, new byte[] { 17, 0, 0, 0, 5, 0, 0, 0, 0, 19, 0, 0, 0 });
                Assert.True(foreground >= 0); fresh[foreground + 4] = 4;
                break;
            case "duplicate-format":
                var appFormat = Find(fresh, new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3").ToByteArray());
                Assert.True(appFormat >= 0);
                new Guid("86D40B4D-9069-443C-819A-2A54090DCCEC").TryWriteBytes(fresh.AsSpan(appFormat, 16));
                break;
            case "unknown-envelope": fresh[Find(fresh, "APPS"u8.ToArray())] ^= 1; break;
            case "truncated": fresh = fresh[..^1]; break;
            case "trailing-data": fresh = [.. fresh, 0]; break;
        }
        // Even equal data cannot turn an unsupported layout into a semantic alias.
        Assert.False(Matches(fresh, fresh));
    }

    [Fact]
    public void FakeNestedStorageInAnOpaqueValueIsNotTreatedAsTheBackgroundProperty()
    {
        var fresh = Fixture("settings-after");
        var payload = Find(fresh, Encoding.Unicode.GetBytes("windows.immersivecontrolpanel"));
        Assert.True(payload >= 0);
        byte[] fake = new byte[45];
        BinaryPrimitives.WriteUInt32LittleEndian(fake, 45);
        BinaryPrimitives.WriteUInt32LittleEndian(fake.AsSpan(4), 0x53505331);
        new Guid("86D40B4D-9069-443C-819A-2A54090DCCEC").TryWriteBytes(fake.AsSpan(8, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(fake.AsSpan(24), 17);
        BinaryPrimitives.WriteUInt32LittleEndian(fake.AsSpan(28), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(fake.AsSpan(33), 19);
        fake.CopyTo(fresh, payload);
        var stored = (byte[])fresh.Clone(); stored[payload + 37] ^= 1;
        Assert.False(Matches(fresh, stored));
    }

    private static int Find(byte[] bytes, byte[] value) => bytes.AsSpan().IndexOf(value);
}
