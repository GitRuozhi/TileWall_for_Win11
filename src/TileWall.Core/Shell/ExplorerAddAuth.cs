namespace TileWall.Core.Shell;

/// <summary>发送方鉴权判定结论（C1/F-01）。</summary>
public enum ExplorerAddAuthVerdict
{
    /// <summary>魔数与发送方映像均校验通过。</summary>
    Accept,

    /// <summary>dwData 魔数不符（非本应用投递）。</summary>
    RejectBadMagic,

    /// <summary>发送方进程映像与本进程不一致（同会话其它进程仿投/另一份安装副本）。</summary>
    RejectUnknownSender,

    /// <summary>本进程主模块路径不可得，无法完成比对（保守拒绝）。</summary>
    RejectSelfPathUnavailable,
}

/// <summary>
/// WM_COPYDATA 发送方鉴权判定（C1/F-01）——纯函数，无 Win32 依赖：
/// 双重校验 = 私有魔数（dwData，'TWL1' little-endian）+ 发送方进程映像路径与本进程一致
/// （大小写不敏感、容忍尾部分隔符差异）。Win32 侧（wParam 句柄 → GetWindowThreadProcessId →
/// OpenProcess → QueryFullProcessImageName）留在 UI Interop，仅把解得的映像路径传入本判定。
/// 拒绝面：宿主窗类名/窗名是稳定常量，同会话任意进程可 FindWindow 投递；魔数挡通用脚本，
/// 映像比对挡「另一份 TileWall 副本」（不同安装路径）与伪装进程。
/// </summary>
public static class ExplorerAddAuth
{
    /// <summary>私有魔数：'T'(0x54) 'W'(0x57) 'L'(0x4C) '1'(0x31) 的 little-endian uint。</summary>
    public const uint Magic = 0x314C5754u;

    /// <summary>dwData 魔数校验。</summary>
    public static bool HasMagic(uint dwData) => dwData == Magic;

    /// <summary>
    /// 发送方映像路径一致性：忽略大小写、容忍尾部路径分隔符差异；
    /// 任一侧为空/空白一律不通过（保守拒绝）。
    /// </summary>
    public static bool SenderImageMatches(string? senderImagePath, string? expectedImagePath)
    {
        if (string.IsNullOrWhiteSpace(senderImagePath) || string.IsNullOrWhiteSpace(expectedImagePath))
        {
            return false;
        }

        return string.Equals(Normalize(senderImagePath), Normalize(expectedImagePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>综合判定（接收端 WndProc 的唯一入口）。</summary>
    public static ExplorerAddAuthVerdict Verify(uint dwData, string? senderImagePath, string? selfImagePath)
    {
        if (!HasMagic(dwData))
        {
            return ExplorerAddAuthVerdict.RejectBadMagic;
        }

        if (string.IsNullOrWhiteSpace(selfImagePath))
        {
            return ExplorerAddAuthVerdict.RejectSelfPathUnavailable;
        }

        return SenderImageMatches(senderImagePath, selfImagePath)
            ? ExplorerAddAuthVerdict.Accept
            : ExplorerAddAuthVerdict.RejectUnknownSender;
    }

    private static string Normalize(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
