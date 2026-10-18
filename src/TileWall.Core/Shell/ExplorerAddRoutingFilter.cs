namespace TileWall.Core.Shell;

/// <summary>WM_COPYDATA 声明 HWND 映像路由过滤结果。</summary>
public enum ExplorerAddRoutingVerdict
{
    /// <summary>魔数匹配，且声明 HWND 所属进程映像路径匹配当前进程。</summary>
    Accept,

    /// <summary>dwData 魔数不符。</summary>
    RejectBadMagic,

    /// <summary>声明 HWND 所属进程映像路径与当前进程不一致。</summary>
    RejectImagePathMismatch,

    /// <summary>本进程主模块路径不可得，无法进行路由过滤。</summary>
    RejectSelfImageUnavailable,
}

/// <summary>
/// WM_COPYDATA 声明 HWND 映像路由过滤——纯函数，无 Win32 依赖：
/// 私有魔数（dwData，'TWL1' little-endian）与声明 HWND 所属进程映像路径共同过滤消息。
/// Win32 侧（wParam HWND → GetWindowThreadProcessId → OpenProcess → QueryFullProcessImageName）
/// 留在 UI Interop。本过滤器不认证实际发送进程，也不证明消息来源可信。
/// </summary>
public static class ExplorerAddRoutingFilter
{
    /// <summary>私有魔数：'T'(0x54) 'W'(0x57) 'L'(0x4C) '1'(0x31) 的 little-endian uint。</summary>
    public const uint Magic = 0x314C5754u;

    /// <summary>dwData 魔数校验。</summary>
    public static bool HasMagic(uint dwData) => dwData == Magic;

    /// <summary>
    /// 声明 HWND 所属进程映像路径一致性：忽略大小写、容忍尾部路径分隔符差异；
    /// 任一侧为空/空白一律不通过（保守拒绝）。
    /// </summary>
    public static bool DeclaredWindowImageMatches(string? declaredWindowImagePath, string? expectedImagePath)
    {
        if (string.IsNullOrWhiteSpace(declaredWindowImagePath) || string.IsNullOrWhiteSpace(expectedImagePath))
        {
            return false;
        }

        return string.Equals(Normalize(declaredWindowImagePath), Normalize(expectedImagePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>综合路由过滤（接收端 WndProc 的唯一入口）。</summary>
    public static ExplorerAddRoutingVerdict Evaluate(uint dwData, string? declaredWindowImagePath, string? selfImagePath)
    {
        if (!HasMagic(dwData))
        {
            return ExplorerAddRoutingVerdict.RejectBadMagic;
        }

        if (string.IsNullOrWhiteSpace(selfImagePath))
        {
            return ExplorerAddRoutingVerdict.RejectSelfImageUnavailable;
        }

        return DeclaredWindowImageMatches(declaredWindowImagePath, selfImagePath)
            ? ExplorerAddRoutingVerdict.Accept
            : ExplorerAddRoutingVerdict.RejectImagePathMismatch;
    }

    private static string Normalize(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
