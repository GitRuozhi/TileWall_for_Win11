using System.Text.Json;
using TileWall.Core.Application;
using TileWall.Core.Configuration;

namespace TileWall.Core.Storage;

/// <summary>A small, replaceable UI preference; expanding a menu never commits or undoes wall content.</summary>
public sealed class MenuStartStateStore
{
    private readonly IFileStore _files;
    private readonly WallStoragePaths _paths;
    public MenuStartStateStore(IFileStore files, string dataRoot)
    {
        _paths = new(files, dataRoot);
        _files = new GuardedWallFiles(files, _paths);
    }

    public MenuGroupExpansion Read()
    {
        var path = _paths.Under("menu-start.json");
        if (!_files.Exists(path)) return new();
        var bytes = _files.ReadAllBytes(path);
        if (bytes.Length > 16384) throw new InvalidDataException("Menu state is too large.");
        return new(JsonSerializer.Deserialize<string[]>(bytes));
    }

    public void Write(IReadOnlyList<string> expanded)
    {
        var path = _paths.Under("menu-start.json");
        var temporary = _paths.Under("menu-start.tmp");
        _files.CreateDirectory(_paths.Root);
        _files.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new MenuGroupExpansion(expanded).Expanded));
        _files.Move(temporary, path, true);
    }
}
