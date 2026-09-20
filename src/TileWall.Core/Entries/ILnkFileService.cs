namespace TileWall.Core.Entries;

/// <summary>.lnk 内部字段快照（只读；显示与编辑基线，M4 设计 §3.3）。</summary>
public sealed record LinkFields(
    string? TargetPath,        // GetPath(SLGP_RAWPATH)；IDList 链接为 null（探针 D 实测）
    string Arguments,          // [R13] 编辑目标时不得无提示清空
    string WorkingDirectory,   // [R13] 同上
    string? IconPath,
    int IconIndex,
    int ShowCmd,               // 窗口方式：表单不暴露，Load→Save 往返自动保留
    bool HasIdList);           // true = 特殊 Shell 入口（目标不可编辑 [R5]，仅可整体替换）

/// <summary>
/// .lnk 读写薄适配（M4 设计 §3.3）。COM 真身只在 <see cref="ShellLinkFileService"/>；
/// 纯逻辑测试注入 Fake（存于 IFileStore 字节内，随 Move 自动跟随，语义与真文件一致）。
/// </summary>
public interface ILnkFileService
{
    /// <summary>新建 .lnk（程序/文件/文件夹目标）；不设 IconLocation → 系统按目标解析。</summary>
    void Create(string path, string target, string arguments, string workingDirectory);

    /// <summary>属性窗显示 + 启动预检 + 编辑基线读取。</summary>
    LinkFields Read(string path);

    /// <summary>Load→SetPath→Save：只改目标，其余字段原样（[R12][R13]；探针 C 实测）。</summary>
    void EditTargetOnly(string path, string newTarget);
}
