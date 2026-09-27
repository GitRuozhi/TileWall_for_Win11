namespace TileWall.Core.Settings;

/// <summary>HKCU Run key value seam used by the current registry adapter.</summary>
public interface IRunKeyValueStore
{
    string? Read();
    void Write(string value);
    void Delete();
}

public static class LoginStartupNames
{
    public const string RunValueName = "TileWall";
}
