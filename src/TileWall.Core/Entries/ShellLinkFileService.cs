using System.Runtime.InteropServices;
using System.Text;

namespace TileWall.Core.Entries;

/// <summary>
/// IShellLinkW + IPersistFile 的 P/Invoke（ComImport）实现——Core 内唯一 Windows COM（M4 设计 §3）。
/// 声明与 temp/m4-probe 同款（net10.0 + EnableNETAnalyzers + TreatWarningsAsErrors 零告警实证）：
/// 不调用 Marshal.ReleaseComObject 等平台注解 API（CA1416），RCW 交 GC 终结器释放；
/// IPersistFile.Save 返回即已落盘，正确性不受释放时机影响。
/// GetPath(SLGP_RAWPATH) 对长路径目录可能返回 8.3 短路径（探针实测）：显示/比较前经 <see cref="NormalizePath"/> 归一化。
/// </summary>
public sealed class ShellLinkFileService : ILnkFileService
{
    private const uint SlgpRawpath = 1;
    private const int PathBufferChars = 1024;
    private const int ArgumentsBufferChars = 2048;
    private const uint StgmRead = 0;

    public void Create(string path, string target, string arguments, string workingDirectory)
    {
        var link = (IShellLinkW)(object)new ShellLink();
        link.SetPath(target);
        link.SetArguments(arguments);
        link.SetWorkingDirectory(workingDirectory);
        Save(link, path);
    }

    public LinkFields Read(string path)
    {
        var link = Load(path);

        var target = new StringBuilder(PathBufferChars);
        link.GetPath(target, target.Capacity, IntPtr.Zero, SlgpRawpath);

        var arguments = new StringBuilder(ArgumentsBufferChars);
        link.GetArguments(arguments, arguments.Capacity);

        var workingDirectory = new StringBuilder(PathBufferChars);
        link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);

        var iconPath = new StringBuilder(PathBufferChars);
        link.GetIconLocation(iconPath, iconPath.Capacity, out var iconIndex);

        link.GetShowCmd(out var showCmd);
        link.GetIDList(out var pidl);
        var targetText = target.ToString();
        // 特殊 Shell 入口的判定（探针 D 实证）：GetPath(SLGP_RAWPATH) 为空且 IDList 存在。
        // 普通路径链接同样可能带 IDList（Shell 依路径自动解析），不能单独作为判据。
        var hasIdList = targetText.Length == 0 && pidl != IntPtr.Zero;
        if (pidl != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(pidl); // FreeCoTaskMem 无平台注解（探针同款）
        }

        var iconText = iconPath.ToString();
        return new LinkFields(
            targetText.Length > 0 ? targetText : null,
            arguments.ToString(),
            workingDirectory.ToString(),
            iconText.Length > 0 ? iconText : null,
            iconIndex,
            showCmd,
            hasIdList);
    }

    public void EditTargetOnly(string path, string newTarget)
    {
        var link = Load(path);
        link.SetPath(newTarget); // 仅此一处显式修改；参数/工作目录/图标/窗口方式由 Load→Save 往返保留
        Save(link, path);
    }

    /// <summary>8.3 短路径 → 长路径；GetLongPathName 失败（网络路径/不存在）回退原串（M4 设计 §11 风险 5）。</summary>
    public static string NormalizePath(string path)
    {
        var builder = new StringBuilder(PathBufferChars);
        return GetLongPathName(path, builder, builder.Capacity) > 0 ? builder.ToString() : path;
    }

    private static IShellLinkW Load(string path)
    {
        var link = (IShellLinkW)(object)new ShellLink();
        ((IPersistFile)link).Load(path, StgmRead);
        return link;
    }

    private static void Save(IShellLinkW link, string path) => ((IPersistFile)link).Save(path, fRemember: true);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink
    {
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hWnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(string lpszShortPath, [Out] StringBuilder lpszLongPath, int cchBuffer);
}
