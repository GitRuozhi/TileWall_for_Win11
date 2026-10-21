using System.Buffers.Binary;
using System.Text;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using Xunit;

namespace TileWall.Core.Tests.Entries;

public sealed class ShellLinkReadTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "shortcut-read-fixtures", Guid.NewGuid().ToString("N"));
    private readonly ShellLinkFileService _links = new();

    [Theory]
    [InlineData(0)]
    [InlineData(2047)]
    [InlineData(2048)]
    [InlineData(6019)]
    [InlineData(32767)]
    public void ReadReturnsCompleteArgumentsAndTargetEditPreservesThem(int length)
    {
        var args = length == 0 ? "" : new string('x', length - 1) + "尾";
        var path = Create("arguments.lnk", args);
        var before = File.ReadAllBytes(path);
        Assert.Equal(args, _links.Read(path).Arguments);
        Assert.Equal(before, File.ReadAllBytes(path));
        _links.EditTargetOnly(path, Path.Combine(_root, "other.txt"));
        Assert.Equal(args, _links.Read(path).Arguments);
    }

    [Fact]
    public void CommonLongArgumentPrefixDoesNotMergeLaunchIdentities()
    {
        var prefix = new string('x', 6000) + " \"two words\" 中文 ";
        var first = Create("first.lnk", prefix + "--tail=A");
        var second = Create("second.lnk", prefix + "--tail=B");
        var identities = new LaunchIdentityService(new LocalFileStore(), _links);
        var a = identities.ResolveInput(first);
        var b = identities.ResolveInput(second);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(a.Key, b.Key);
        Assert.EndsWith("--tail=A", a.Arguments);
        Assert.EndsWith("--tail=B", b.Arguments);
    }

    [Fact]
    public void ArgumentPropertyPreservesLeadingTrailingAndQuotedWhitespace()
    {
        const string args = "  \"a  b\"\t--flag=中文  ";
        Assert.Equal(args, _links.Read(Create("spaces.lnk", args)).Arguments);
    }

    [Theory]
    [InlineData(259)]
    [InlineData(260)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(4095)]
    public void StoredUnicodeIconPathRemainsCompleteAcrossShellPathLimit(int length)
    {
        var path = Create("paths.lnk", "--keep");
        var text = PathOfLength(length);
        File.WriteAllBytes(path, ReplaceStringData(File.ReadAllBytes(path), 0x40, text));
        var before = File.ReadAllBytes(path);
        var fields = _links.Read(path);
        Assert.Equal(_root, fields.WorkingDirectory);
        Assert.Equal(text, fields.IconPath);
        Assert.Equal("--keep", fields.Arguments);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(259)]
    [InlineData(260)]
    public void CompleteWorkingDirectoryAtShellBoundaryIsAccepted(int length)
    {
        var path = Create("working-directory.lnk", "--keep");
        var text = PathOfLength(length);
        File.WriteAllBytes(path, ReplaceStringData(File.ReadAllBytes(path), 0x10, text));
        var fields = _links.Read(path);
        Assert.Equal(text, fields.WorkingDirectory);
        Assert.Equal("--keep", fields.Arguments);
    }

    [Fact]
    public void OversizedStoredWorkingDirectoryDoesNotReturnMisparsedArgumentIdentity()
    {
        var path = Create("invalid-working-directory.lnk", "--keep");
        File.WriteAllBytes(path, ReplaceStringData(File.ReadAllBytes(path), 0x10, PathOfLength(1024)));
        Assert.Throws<InvalidDataException>(() => _links.Read(path));
    }

    [Fact]
    public void Complete259CharacterRawTargetIsAccepted()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "target-boundary.lnk");
        var target = PathOfLength(259);
        _links.Create(path, target, "", "");
        Assert.Equal(target, _links.Read(path).TargetPath);
    }

    [Fact]
    public void NormalizeLongExistingPathRetriesAndMissingPathFallsBack()
    {
        var directory = _root;
        while (directory.Length < 1500)
        {
            directory = Path.Combine(directory, "Long folder " + new string('d', 90));
            Directory.CreateDirectory(directory);
        }
        var extended = @"\\?\" + directory;
        Assert.Equal(extended, ShellLinkFileService.NormalizePath(extended));
        var missing = Path.Combine(directory, "missing.txt");
        Assert.Equal(missing, ShellLinkFileService.NormalizePath(missing));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("invalidUnicode")]
    [InlineData("embeddedNull")]
    [InlineData("ansi")]
    [InlineData("oversizedLinkInfo")]
    public void UnprovableStoredPathDoesNotProduceAConfirmedField(string fault)
    {
        var bytes = new byte[76];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 76);
        new Guid("00021401-0000-0000-C000-000000000046").TryWriteBytes(bytes.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 0x90);
        bytes = ReplaceStringData(bytes, 0x10, "observed");
        switch (fault)
        {
            case "truncated": bytes = bytes[..^1]; break;
            case "invalidUnicode": bytes[^2] = 0; bytes[^1] = 0xD8; break;
            case "embeddedNull": bytes[^2] = 0; bytes[^1] = 0; break;
            case "ansi": bytes[20] &= 0x7F; break;
            case "oversizedLinkInfo":
                bytes[20] |= 2;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76), uint.MaxValue);
                break;
        }
        using var stream = new MemoryStream(bytes);
        if (fault == "ansi") Assert.Null(ShellLinkStringData.ReadPaths(stream));
        else Assert.Throws<InvalidDataException>(() => ShellLinkStringData.ReadPaths(stream));
    }

    private string Create(string name, string arguments)
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "target.txt");
        File.WriteAllText(target, "Read-only test target; never executed.");
        var path = Path.Combine(_root, name);
        _links.Create(path, target, arguments, _root);
        return path;
    }

    private static string PathOfLength(int length)
    {
        var path = @"C:\";
        while (path.Length + 100 < length) path += new string('p', 99) + "\\";
        return path + new string('p', length - path.Length);
    }

    private static byte[] ReplaceStringData(byte[] bytes, uint requestedFlag, string text)
    {
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20));
        var offset = 76;
        if ((flags & 1) != 0) offset += 2 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
        if ((flags & 2) != 0) offset += checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
        for (uint flag = 4; flag < requestedFlag; flag <<= 1)
            if ((flags & flag) != 0) offset += 2 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset)) * 2;
        var oldLength = (flags & requestedFlag) != 0 && offset + 2 <= bytes.Length
            ? 2 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset)) * 2 : 0;
        using var result = new MemoryStream();
        result.Write(bytes.AsSpan(0, offset));
        Span<byte> count = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(count, checked((ushort)text.Length));
        result.Write(count);
        result.Write(Encoding.Unicode.GetBytes(text));
        result.Write(bytes.AsSpan(offset + oldLength));
        var rewritten = result.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(rewritten.AsSpan(20), flags | requestedFlag);
        return rewritten;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
