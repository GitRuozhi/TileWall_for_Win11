namespace TileWall.Shell.Interop;

/// <summary>
/// Explorer 右键扩展的固定标识（M9 设计 §2.2/§4.1）：COM 类 GUID 是跨版本身份，进代码常量后不再变；
/// 与 Package.appxmanifest 的 com:Class Id / desktop4:Verb Clsid 逐字符一致。
/// </summary>
internal static class ExplorerCommandIds
{
    /// <summary>TileWall Explorer Command 的 COM 类标识（manifest com:Class Id 同值）。</summary>
    public const string ClsidString = "cb391e98-9cf2-4a54-abea-fbc529ca2d44";

    /// <summary>菜单命令的 canonical name GUID（设计 §2.2 铸造值；GetCanonicalName 恒返回）。</summary>
    public static readonly Guid CanonicalName = new("75034654-9467-4a8e-af75-84ee63ae3f8d");

    /// <summary>菜单标题（GetTitle 恒值；构建零重活 [R2]）。</summary>
    public const string MenuTitle = "添加到 TileWall";

    /// <summary>菜单图标（GetIcon 恒值：包根相对资源串 = exe 首图标 Assets/AppIcon.ico；Win11 首层显示属 P5 真机核对）。</summary>
    public const string MenuIcon = "TileWall.exe,0";
}
