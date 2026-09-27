namespace TileWall.Core.Entries;

public static class EntryPaths
{
    public const string ObjectsDirName = "Objects";
    public static string ObjectsDir(string rootPath, string objectId) =>
        Path.Combine(rootPath, ObjectsDirName, objectId);
}
