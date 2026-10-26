using TileWall.Core.Configuration;

namespace TileWall.Core.Storage;

/// <summary>Rechecks the root and every path component at the actual IO boundary.</summary>
internal sealed class GuardedWallFiles(IFileStore inner, WallStoragePaths paths) : IFileStore
{
    private readonly IFileStore _inner = inner;
    private readonly WallStoragePaths _paths = paths;

    public FileStorePathInfo GetPathInfo(string path)
    {
        _paths.Guard(path, allowExternalRead: true);
        return _inner.GetPathInfo(path);
    }

    public bool Exists(string path)
    {
        _paths.Guard(path, allowExternalRead: true);
        return _inner.Exists(path);
    }

    public byte[] ReadAllBytes(string path)
    {
        _paths.Guard(path, allowExternalRead: true);
        return _inner.ReadAllBytes(path);
    }

    public Stream OpenReadProtected(string path)
    {
        _paths.Guard(path);
        return _inner.OpenReadProtected(path);
    }

    public void WriteAllBytes(string path, byte[] bytes)
    {
        _paths.Guard(path);
        _inner.WriteAllBytes(path, bytes);
    }

    public void Move(string src, string dst, bool overwrite)
    {
        _paths.Guard(src);
        _paths.Guard(dst);
        _inner.Move(src, dst, overwrite);
    }

    public void Copy(string src, string dst, bool overwrite)
    {
        _paths.Guard(src, allowExternalRead: true);
        _paths.Guard(dst);
        _inner.Copy(src, dst, overwrite);
    }

    public void MoveDirectory(string source, string destination)
    {
        _paths.GuardTree(source);
        _paths.Guard(destination);
        if (_inner.GetPathInfo(destination).Exists) throw new IOException("Immutable directory already exists.");
        _inner.MoveDirectory(source, destination);
        _paths.GuardTree(destination);
    }

    public void Delete(string path)
    {
        _paths.Guard(path);
        _inner.Delete(path);
    }

    public void CreateDirectory(string path)
    {
        _paths.Guard(path);
        _inner.CreateDirectory(path);
    }

    public IReadOnlyList<string> ListDirectories(string directoryPath)
    {
        _paths.Guard(directoryPath);
        var listed = _inner.ListDirectories(directoryPath);
        foreach (var child in listed) _paths.Guard(child);
        return listed;
    }

    public IReadOnlyList<string> ListFiles(string directoryPath)
    {
        _paths.Guard(directoryPath);
        var listed = _inner.ListFiles(directoryPath);
        foreach (var child in listed) _paths.Guard(child);
        return listed;
    }

    public void DeleteDirectory(string directoryPath)
    {
        _paths.GuardTree(directoryPath);
        _inner.DeleteDirectory(directoryPath);
    }
}
