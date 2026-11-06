namespace TileWall.Core.Shell;

/// <summary>Read-only facts from a live process owning the declared window.</summary>
public sealed record ExplorerAddProcessIdentity(string ImagePath, string UserSid, int SessionId, bool IsTileWall);

/// <summary>WM_COPYDATA declared-window routing result.</summary>
public enum ExplorerAddRoutingVerdict
{
    Accept,
    RejectBadMagic,
    RejectSelfImageUnavailable,
    RejectProcessUnavailable,
    RejectUserMismatch,
    RejectSessionMismatch,
    RejectImagePathMismatch,
}

/// <summary>
/// Filters misrouted messages across copies belonging to the same user and login
/// session. A declared HWND is a routing hint, never proof of the actual sender.
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
    public static ExplorerAddRoutingVerdict Evaluate(uint dwData,
        ExplorerAddProcessIdentity? declared, ExplorerAddProcessIdentity? self)
    {
        if (!HasMagic(dwData))
        {
            return ExplorerAddRoutingVerdict.RejectBadMagic;
        }

        if (!Complete(self))
        {
            return ExplorerAddRoutingVerdict.RejectSelfImageUnavailable;
        }

        if (!Complete(declared)) return ExplorerAddRoutingVerdict.RejectProcessUnavailable;
        if (!string.Equals(declared!.UserSid, self!.UserSid, StringComparison.Ordinal))
            return ExplorerAddRoutingVerdict.RejectUserMismatch;
        if (declared.SessionId != self.SessionId) return ExplorerAddRoutingVerdict.RejectSessionMismatch;
        return DeclaredWindowImageMatches(declared.ImagePath, self.ImagePath) || declared.IsTileWall && self.IsTileWall
            ? ExplorerAddRoutingVerdict.Accept : ExplorerAddRoutingVerdict.RejectImagePathMismatch;
    }

    private static bool Complete(ExplorerAddProcessIdentity? process) => process is not null &&
        !string.IsNullOrWhiteSpace(process.ImagePath) && !string.IsNullOrWhiteSpace(process.UserSid) && process.SessionId >= 0;

    private static string Normalize(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
