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
    internal static readonly Guid FolderIdPrograms = new("A77F5D77-2E2B-44C3-A6A2-ABA601054A51");

    internal static readonly Guid FolderIdCommonPrograms = new("0139D44E-6AFE-49F2-8690-3DAFCAE6FFB8");

    internal static readonly Guid FolderIdLocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
    internal static readonly Guid FolderIdDesktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    // Windows SDK 10.0.26100.0, um/KnownFolders.h: FOLDERID_Downloads / FOLDERID_Documents.
    internal static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");
    internal static readonly Guid FolderIdDocuments = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");

    public string? UserPrograms => Query(FolderIdPrograms);

    public string? CommonPrograms => Query(FolderIdCommonPrograms);

    internal static string? Desktop => Query(FolderIdDesktop);
    internal static string? PublicDesktop => Query(new Guid("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25"));
    internal static string? Downloads => Query(FolderIdDownloads);
    internal static string? Documents => Query(FolderIdDocuments);

    // Path resolution does not disable package file-system virtualization;
    // sharing still has to be checked against the files actually opened.
    internal static string LocalAppData => Query(FolderIdLocalAppData, KfFlagNoPackageRedirection)
        ?? throw new IOException("The current user's LocalAppData folder is unavailable.");

    private static string? Query(Guid folderId, uint flags = KfFlagDefault)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var result = SHGetKnownFolderPath(folderId, flags, IntPtr.Zero, out buffer);
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
    private const uint KfFlagNoPackageRedirection = 0x00010000;

    [DllImport("shell32.dll", SetLastError = false)]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
