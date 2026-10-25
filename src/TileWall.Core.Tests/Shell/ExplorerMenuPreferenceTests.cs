using System.Text;
using TileWall.Core.Configuration;
using TileWall.Core.Shell;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Shell;

public sealed class ExplorerMenuPreferenceTests
{
    private static string Root() => Path.Combine(Path.GetTempPath(), "TileWall.ExplorerMenu.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingRootDefaultsOnWithoutCreatingAnything()
    {
        var root = Root();
        var state = new ExplorerMenuPreference(new LocalFileStore(), root).Read();
        Assert.True(state.ReadSucceeded);
        Assert.True(state.Enabled);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData(true, "TileWall.ExplorerMenu/1\n1\n")]
    [InlineData(false, "TileWall.ExplorerMenu/1\n0\n")]
    public void LocalWritePublishesExactProtocolAndSurvivesReopen(bool enabled, string expected)
    {
        var root = Root();
        try
        {
            var files = new LocalFileStore();
            var state = new ExplorerMenuPreference(files, root).Write(enabled);
            Assert.True(state.ReadSucceeded, state.Error);
            Assert.Equal(enabled, state.Enabled);
            Assert.Equal(Encoding.ASCII.GetBytes(expected), File.ReadAllBytes(Path.Combine(root, ExplorerMenuPreference.FileName)));
            Assert.Equal(state, new ExplorerMenuPreference(files, root).Read());
            Assert.Single(Directory.GetFiles(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("TileWall.ExplorerMenu/1\n1")]
    [InlineData("TileWall.ExplorerMenu/1\r\n1\r\n")]
    [InlineData("TileWall.ExplorerMenu/2\n1\n")]
    [InlineData("TileWall.ExplorerMenu/1\n2\n")]
    [InlineData("TileWall.ExplorerMenu/1\n1\n ")]
    [InlineData("\uFEFFTileWall.ExplorerMenu/1\n1\n")]
    [InlineData("TileWall.ExplorerMenu/1\n１\n")]
    public void MalformedPreferenceFailsClosedAndCannotBeOverwritten(string content)
    {
        var root = Root();
        var files = new InMemoryFileStore();
        var path = Path.Combine(root, ExplorerMenuPreference.FileName);
        var bytes = Encoding.UTF8.GetBytes(content);
        files.WriteAllBytes(path, bytes);
        var preference = new ExplorerMenuPreference(files, root);
        Assert.False(preference.Read().Enabled);
        Assert.False(preference.Read().ReadSucceeded);
        Assert.False(preference.Write(true).ReadSucceeded);
        Assert.Equal(bytes, files.ReadAllBytes(path));
    }

    [Fact]
    public void OversizedFileIsRejectedWithBoundedRead()
    {
        var root = Root();
        var files = new FaultingFiles(new InMemoryFileStore()) { ReadStream = new OversizedStream() };
        var preference = new ExplorerMenuPreference(files, root);
        files.Inner.WriteAllBytes(Path.Combine(root, ExplorerMenuPreference.FileName), [1]);
        Assert.False(preference.Read().ReadSucceeded);
        Assert.Equal(65, ((OversizedStream)files.ReadStream!).TotalRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedStagingOrAtomicReplacementRetainsPreviousBytesAndCleansTemporary(bool failStaging)
    {
        var root = Root();
        try
        {
            var files = new FaultingFiles(new LocalFileStore());
            var preference = new ExplorerMenuPreference(files, root);
            Assert.True(preference.Write(false).ReadSucceeded);
            var original = File.ReadAllBytes(Path.Combine(root, ExplorerMenuPreference.FileName));
            files.FailWrite = failStaging;
            files.FailMove = !failStaging;
            var state = preference.Write(true);
            Assert.False(state.ReadSucceeded);
            Assert.False(state.Enabled);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, ExplorerMenuPreference.FileName)));
            Assert.False(preference.Read().Enabled);
            Assert.True(preference.Read().ReadSucceeded);
            Assert.Single(Directory.GetFiles(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LockedRealPreferenceReportsReadFailureWithoutChangingBytes()
    {
        var root = Root();
        try
        {
            var preference = new ExplorerMenuPreference(new LocalFileStore(), root);
            Assert.True(preference.Write(false).ReadSucceeded);
            var path = Path.Combine(root, ExplorerMenuPreference.FileName);
            var original = File.ReadAllBytes(path);
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.False(preference.Read().ReadSucceeded);
                Assert.False(preference.Write(true).ReadSucceeded);
            }
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.False(preference.Read().Enabled);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RootInspectionDeniedFailsClosedAndNeverWrites()
    {
        var root = Root();
        var files = new FaultingFiles(new InMemoryFileStore()) { DeniedPath = root };
        var preference = new ExplorerMenuPreference(files, root);
        Assert.False(preference.Read().ReadSucceeded);
        Assert.False(preference.Write(true).ReadSucceeded);
        Assert.Equal(0, files.WriteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReparseRootOrFileFailsClosedBeforeIo(bool file)
    {
        var root = Root();
        var memory = new InMemoryFileStore();
        memory.ReparsePaths.Add(file ? Path.Combine(root, ExplorerMenuPreference.FileName) : root);
        var files = new FaultingFiles(memory);
        var preference = new ExplorerMenuPreference(files, root);
        Assert.False(preference.Read().ReadSucceeded);
        Assert.False(preference.Write(false).ReadSucceeded);
        Assert.Equal(0, files.WriteCount);
    }

    [Fact]
    public void ReparseIntroducedAfterStagingPreventsCommit()
    {
        var root = Root();
        var memory = new InMemoryFileStore();
        var path = Path.Combine(root, ExplorerMenuPreference.FileName);
        memory.WriteAllBytes(path, "TileWall.ExplorerMenu/1\n0\n"u8.ToArray());
        var files = new FaultingFiles(memory) { AfterWrite = () => memory.ReparsePaths.Add(path) };
        Assert.False(new ExplorerMenuPreference(files, root).Write(true).ReadSucceeded);
        Assert.Equal(0, files.MoveCount);
        Assert.Equal("TileWall.ExplorerMenu/1\n0\n"u8.ToArray(), memory.ReadAllBytes(path));
    }

    private sealed class FaultingFiles(IFileStore inner) : IFileStore
    {
        internal IFileStore Inner => inner;
        internal bool FailMove { get; set; }
        internal bool FailWrite { get; set; }
        internal string? DeniedPath { get; init; }
        internal Stream? ReadStream { get; init; }
        internal Action? AfterWrite { get; init; }
        internal int WriteCount { get; private set; }
        internal int MoveCount { get; private set; }
        public FileStorePathInfo GetPathInfo(string path) => path == DeniedPath
            ? throw new UnauthorizedAccessException("Injected denied root.") : inner.GetPathInfo(path);
        public bool Exists(string path) => inner.Exists(path);
        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);
        public Stream OpenReadProtected(string path) => ReadStream ?? inner.OpenReadProtected(path);
        public void WriteAllBytes(string path, byte[] bytes)
        {
            WriteCount++;
            if (FailWrite) throw new UnauthorizedAccessException("Injected staging write denial.");
            inner.WriteAllBytes(path, bytes);
            AfterWrite?.Invoke();
        }
        public void Move(string src, string dst, bool overwrite)
        {
            MoveCount++;
            if (FailMove) throw new IOException("Injected atomic replacement failure.");
            inner.Move(src, dst, overwrite);
        }
        public void Copy(string src, string dst, bool overwrite) => inner.Copy(src, dst, overwrite);
        public void Delete(string path) => inner.Delete(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public IReadOnlyList<string> ListDirectories(string path) => inner.ListDirectories(path);
        public IReadOnlyList<string> ListFiles(string path) => inner.ListFiles(path);
        public void DeleteDirectory(string path) => inner.DeleteDirectory(path);
    }

    private sealed class OversizedStream : MemoryStream
    {
        internal OversizedStream() : base(new byte[10000], writable: false) { }
        internal int TotalRead { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            var count = base.Read(buffer);
            TotalRead += count;
            return count;
        }
    }
}
