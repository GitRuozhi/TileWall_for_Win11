namespace TileWall.Core.Configuration;

public interface IDataDirectory
{
    string RootPath { get; }
    string ObjectsPath { get; }
    string RecoveryPath { get; }
}

public sealed class FileSystemDataDirectory : IDataDirectory
{
    public FileSystemDataDirectory(string rootPath) => RootPath = rootPath;
    public string RootPath { get; }
    public string ObjectsPath => Path.Combine(RootPath, "Objects");
    public string RecoveryPath => Path.Combine(RootPath, "Recovery");
}
