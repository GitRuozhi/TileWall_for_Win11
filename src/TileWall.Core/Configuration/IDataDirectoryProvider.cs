namespace TileWall.Core.Configuration;

/// <summary>数据目录三个正式路径（拍板 Q1：存储位置抽象；设计 §16.1）。</summary>
public interface IDataDirectory
{
    /// <summary>config.json、Recovery/ 所在。</summary>
    string RootPath { get; }

    /// <summary>&lt;root&gt;/Objects——托管入口目录（M3 使用；M2 仅 schema/校验层面预留）。</summary>
    string ObjectsPath { get; }

    /// <summary>&lt;root&gt;/Recovery——安全提交材料 config.prev.json 所在（不是用户备份库）。</summary>
    string RecoveryPath { get; }
}

/// <summary>存储位置提供者抽象；实现 2（packaged → ApplicationData.Current.LocalFolder）留位在应用工程，Core 零 WinRT 引用。</summary>
public interface IDataDirectoryProvider
{
    IDataDirectory GetDefault();
}

/// <summary>
/// M2 交付实现：unpackaged 开发期 %LOCALAPPDATA%\TileWall。
/// 纯 BCL（Environment.SpecialFolder.LocalApplicationData；技术选型 §六、拍板 Q1）。
/// </summary>
public sealed class LocalAppDataDirectoryProvider : IDataDirectoryProvider
{
    public IDataDirectory GetDefault()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TileWall");
        return new FileSystemDataDirectory(root);
    }
}

/// <summary>基于文件系统路径的目录实现。</summary>
public sealed class FileSystemDataDirectory : IDataDirectory
{
    public FileSystemDataDirectory(string rootPath) => RootPath = rootPath;

    public string RootPath { get; }

    public string ObjectsPath => Path.Combine(RootPath, "Objects");

    public string RecoveryPath => Path.Combine(RootPath, "Recovery");
}
