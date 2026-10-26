using TileWall.Core.Entries;

namespace TileWall.Core.Storage;

internal sealed class GuardedWallLinks(ILnkFileService inner, WallStoragePaths paths) : ILnkFileService
{
    public void Create(string path, string target, string arguments, string workingDirectory)
    {
        paths.Guard(path);
        inner.Create(path, target, arguments, workingDirectory);
    }

    public void CreateShell(string path, string parsingName)
    {
        paths.Guard(path);
        inner.CreateShell(path, parsingName);
    }

    public LinkFields Read(string path)
    {
        paths.Guard(path);
        return inner.Read(path);
    }

    public void EditTargetOnly(string path, string newTarget)
    {
        paths.Guard(path);
        inner.EditTargetOnly(path, newTarget);
    }
}
