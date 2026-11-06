using TileWall.Core.Storage;
using TileWall.Core.Tests.Fixtures;
using Xunit;

namespace TileWall.Core.Tests.Storage;

public sealed class ExternalReadPathTests
{
    private readonly string _root = Path.GetFullPath(Path.Combine("fixtures", "managed"));
    private readonly string _external = Path.GetFullPath(Path.Combine("fixtures", "selected-link"));

    [Fact]
    public void SelectedExternalJunctionCanBeReadAndCopiedWithoutChangingSource()
    {
        var memory = new InMemoryFileStore();
        var guarded = new GuardedWallFiles(memory, new WallStoragePaths(memory, _root));
        var source = Path.Combine(_external, "image.png");
        var destination = Path.Combine(_root, "staged.png");
        memory.ReparsePaths.Add(_external);
        memory.WriteAllBytes(source, [1, 2, 3]);
        Assert.True(guarded.Exists(source));
        Assert.Equal(new byte[] { 1, 2, 3 }, guarded.ReadAllBytes(source));
        guarded.Copy(source, destination, false);
        Assert.Equal(new byte[] { 1, 2, 3 }, guarded.ReadAllBytes(destination));
        Assert.Equal(new byte[] { 1, 2, 3 }, memory.ReadAllBytes(source));
        Assert.ThrowsAny<IOException>(() => guarded.WriteAllBytes(source, [4]));
        Assert.ThrowsAny<IOException>(() => guarded.Delete(source));
        Assert.ThrowsAny<IOException>(() => guarded.Move(source, destination, true));
    }

    [Fact]
    public void ManagedJunctionRemainsRejectedEvenByReadAndCopySourceMethods()
    {
        var memory = new InMemoryFileStore();
        var guarded = new GuardedWallFiles(memory, new WallStoragePaths(memory, _root));
        var directory = Path.Combine(_root, "Media");
        var source = Path.Combine(directory, "image.png");
        memory.ReparsePaths.Add(directory);
        memory.WriteAllBytes(source, [1]);
        Assert.ThrowsAny<IOException>(() => guarded.Exists(source));
        Assert.ThrowsAny<IOException>(() => guarded.ReadAllBytes(source));
        Assert.ThrowsAny<IOException>(() => guarded.Copy(source, Path.Combine(_root, "copy.png"), false));
        Assert.ThrowsAny<IOException>(() => guarded.WriteAllBytes(source, [2]));
        Assert.ThrowsAny<IOException>(() => guarded.Delete(source));
    }

    [Fact]
    public void MissingExternalTargetFailsAndDestinationRemainsAbsent()
    {
        var memory = new InMemoryFileStore();
        var guarded = new GuardedWallFiles(memory, new WallStoragePaths(memory, _root));
        memory.ReparsePaths.Add(_external);
        var source = Path.Combine(_external, "missing.png");
        var destination = Path.Combine(_root, "copy.png");
        Assert.Throws<FileNotFoundException>(() => guarded.Copy(source, destination, false));
        Assert.False(memory.Exists(destination));
    }

    [Fact]
    public void ManagedPathChangedToJunctionAfterValidationIsRejectedAtRead()
    {
        var memory = new InMemoryFileStore();
        var paths = new WallStoragePaths(memory, _root);
        var guarded = new GuardedWallFiles(memory, paths);
        var directory = Path.Combine(_root, "Media");
        var source = Path.Combine(directory, "image.png");
        memory.WriteAllBytes(source, [1]);
        paths.CreateValidationGuard()(source);
        memory.ReparsePaths.Add(directory);
        Assert.ThrowsAny<IOException>(() => guarded.ReadAllBytes(source));
    }
}
