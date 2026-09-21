using System.Runtime.InteropServices;
using TileWall.Core.Import;

namespace TileWall.Shell.Interop;

/// <summary>
/// IKnownFolderPaths 的 Shell 真身（M8 设计 §5.1）：SHGetKnownFolderPath 查询
/// FOLDERID_Programs / FOLDERID_CommonPrograms（GUID 取自 Windows SDK knownfolders.h 常量）。
/// 任一查询失败（HRESULT != 0）→ 对应属性 null，导入窗隐藏该来源——不回退硬编码路径（[R6]/R-5）。
/// </summary>
public sealed class KnownFolderPaths : IKnownFolderPaths
{
    internal static readonly Guid FolderIdPrograms = new("905e63b6-c1bf-494e-b29c-65b732d3d21a");

    internal static readonly Guid FolderIdCommonPrograms = new("0139D44E-6AFE-49F2-8690-3DAFCAE6FFB8");

    public string? UserPrograms => Query(FolderIdPrograms);

    public string? CommonPrograms => Query(FolderIdCommonPrograms);

    private static string? Query(Guid folderId)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var result = SHGetKnownFolderPath(folderId, KfFlagDefault, IntPtr.Zero, out buffer);
            if (result != 0 || buffer == IntPtr.Zero)
            {
                return null; // 查询失败 → 来源隐藏（null），绝不硬编码回退
            }

            return Marshal.PtrToStringUni(buffer);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null; // 极老系统缺 shell32 导出：同失败语义
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }
    }

    private const uint KfFlagDefault = 0;

    [DllImport("shell32.dll", SetLastError = false)]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
