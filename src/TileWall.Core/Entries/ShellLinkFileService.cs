using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TileWall.Core.Entries;

/// <summary>
/// IShellLinkW + IPersistFile 的 P/Invoke（ComImport）实现——Core 内唯一 Windows COM（M4 设计 §3）。
/// 声明与 temp/m4-probe 同款（net10.0 + EnableNETAnalyzers + TreatWarningsAsErrors 零告警实证）：
/// 不调用 Marshal.ReleaseComObject 等平台注解 API（CA1416），RCW 交 GC 终结器释放；
/// IPersistFile.Save 返回即已落盘，正确性不受释放时机影响。
/// GetPath 使用 SLGP_RAWPATH (0x4) 读取原始目标路径；SLGP_SHORTPATH (0x1) 会请求 8.3 短路径。
/// <see cref="NormalizePath"/> 仍供其它可能带 8.3 别名的输入归一化。
/// </summary>
public sealed class ShellLinkFileService : ILnkFileService
{
    private const uint SlgpRawpath = 0x4;
    private const int MaxPathChars = 260; // Windows SDK MAX_PATH, including the terminating null.
    private const int PathBufferChars = 1024;
    private static readonly PropertyKey LinkArguments = new(new Guid("436F2667-14E2-4FEB-B30A-146C53B5B674"), 100);
    private static readonly PropertyKey LinkTargetParsingPath = new(new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);
    private const uint StgmRead = 0;

    public void Create(string path, string target, string arguments, string workingDirectory)
    {
        var link = (IShellLinkW)(object)new ShellLink();
        link.SetPath(target);
        link.SetArguments(arguments);
        link.SetWorkingDirectory(workingDirectory);
        Save(link, path);
    }

    public void CreateShell(string path, string parsingName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parsingName);
        Marshal.ThrowExceptionForHR(SHParseDisplayName(parsingName, IntPtr.Zero, out var pidl, 0, out _));
        if (pidl == IntPtr.Zero) throw new InvalidDataException("The native Shell target is unavailable.");
        try
        {
            var link = (IShellLinkW)(object)new ShellLink();
            link.SetIDList(pidl);
            Save(link, path);
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    public void CreateShell(string path, NativeShellTargetSnapshot target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.ConfirmCurrent();
        var bytes = target.CopyIdList();
        if (Convert.ToHexString(SHA256.HashData(bytes)) != target.IdListSha256)
            throw new InvalidDataException("The native Shell snapshot is corrupt.");
        var pidl = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pidl, bytes.Length);
            var link = (IShellLinkW)(object)new ShellLink();
            link.SetIDList(pidl);
            Save(link, path);
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    public LinkFields Read(string path) => ReadWithIdList(path, out _);

    public LinkFields ReadWithIdList(string path, out ReadOnlyMemory<byte> idList)
    {
        idList = default;
        // Keep COM and persisted path fields on the same read-only file snapshot.
        using var source = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var link = Load(path);
        var storedPaths = ShellLinkStringData.ReadPaths(source);

        var targetText = ReadPathText(buffer => link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, SlgpRawpath),
            observed => ConfirmTarget(link, observed));
        var arguments = ReadStringProperty(link, in LinkArguments) ?? string.Empty;
        var workingDirectory = storedPaths?.WorkingDirectory ?? ReadPathText(buffer => link.GetWorkingDirectory(buffer, buffer.Capacity),
            _ => throw new InvalidDataException("The ANSI shortcut working directory cannot be read with confirmed completeness."));
        var iconIndex = 0;
        var iconText = ReadPathText(buffer => link.GetIconLocation(buffer, buffer.Capacity, out iconIndex),
            _ => storedPaths?.IconPath ?? throw new InvalidDataException("The ANSI shortcut icon location cannot be read with confirmed completeness."));
        if (storedPaths is not null) iconText = storedPaths.Value.IconPath;

        link.GetShowCmd(out var showCmd);
        link.GetIDList(out var pidl);
        // 特殊 Shell 入口的判定（探针 D 实证）：GetPath(SLGP_RAWPATH) 为空且 IDList 存在。
        // 普通路径链接同样可能带 IDList（Shell 依路径自动解析），不能单独作为判据。
        var hasIdList = targetText.Length == 0 && pidl != IntPtr.Zero;
        string? shellName = null;
        string? idListHash = null;
        if (pidl != IntPtr.Zero)
        {
            try
            {
                // Read the link object's persisted parsing property. Do not bind
                // arbitrary package PIDLs to namespace handlers to request a name.
                if (hasIdList)
                {
                    var bytes = CopyIdList(pidl);
                    idList = bytes;
                    idListHash = Convert.ToHexString(SHA256.HashData(bytes));
                    shellName = ReadStringProperty(link, in LinkTargetParsingPath);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl); // FreeCoTaskMem 无平台注解（探针同款）
            }
        }

        return new LinkFields(
            targetText.Length > 0 ? targetText : null,
            arguments,
            workingDirectory,
            iconText.Length > 0 ? iconText : null,
            iconIndex,
            showCmd,
            hasIdList) { ShellParsingName = shellName, ShellIdListHash = idListHash };
    }

    /// <summary>Fingerprint a valid PIDL returned by Shell, without resolving a namespace name.</summary>
    internal static string IdListHash(IntPtr pidl)
        => Convert.ToHexString(SHA256.HashData(CopyIdList(pidl)));

    internal static byte[] CopyIdList(IntPtr pidl)
    {
        // GetIDList/SHGetIDListFromObject supply valid, Shell-owned PIDLs. ILGetSize
        // is not a validator for arbitrary pointers. The LNK IDListSize field is UInt16;
        // reject anything that cannot be persisted before allocating our bounded copy.
        if (pidl == IntPtr.Zero) throw new InvalidDataException("The Shell IDList is unavailable.");
        var size = ILGetSize(pidl);
        if (size is < 2 or > ushort.MaxValue)
            throw new InvalidDataException("The Shell IDList exceeds the shortcut budget.");
        var bytes = new byte[size];
        Marshal.Copy(pidl, bytes, 0, checked((int)size));
        return bytes;
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
        while (true)
        {
            var length = GetLongPathName(path, builder, builder.Capacity);
            if (length == 0) return path;
            if (length < builder.Capacity) return builder.ToString();
            // On insufficient capacity the result includes the terminating null, not copied text.
            if (length >= int.MaxValue) return path;
            builder = new StringBuilder(Math.Max((int)length, builder.Capacity + 1));
        }
    }

    private static string? ReadStringProperty(IShellLinkW link, in PropertyKey key)
    {
        // GetArguments silently truncates. The Shell property store allocates the complete value.
        var value = new PropVariant();
        try
        {
            var result = ((IPropertyStore)link).GetValue(in key, out value);
            Marshal.ThrowExceptionForHR(result);
            // INPLACE_S_TRUNCATED can report canonicalization (including trimming); identity needs the exact value.
            if (result != 0) throw new InvalidDataException("The shortcut property was canonicalized instead of returned exactly.");
            return value.Type switch
            {
                0 => null, // VT_EMPTY: no property.
                31 when value.Pointer != IntPtr.Zero => Marshal.PtrToStringUni(value.Pointer)!, // VT_LPWSTR
                _ => throw new InvalidDataException("The shortcut property is not a complete Unicode string.")
            };
        }
        finally
        {
            Marshal.ThrowExceptionForHR(PropVariantClear(ref value));
        }
    }

    private static string ConfirmTarget(IShellLinkW link, string observed)
    {
        if (string.Equals(ReadStringProperty(link, in LinkTargetParsingPath), observed, StringComparison.Ordinal)) return observed;
        link.GetIDList(out var pidl);
        try
        {
            // Convert the stored filesystem ID list to its path; do not resolve
            // the target, expand aliases, or request a namespace display name.
            var path = new StringBuilder(32768);
            if (pidl != IntPtr.Zero && SHGetPathFromIDListEx(pidl, path, (uint)path.Capacity, 0) &&
                string.Equals(path.ToString(), observed, StringComparison.Ordinal)) return observed;
        }
        finally { if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
        throw new InvalidDataException("The shortcut target cannot be read with confirmed completeness.");
    }

    private static string ReadPathText(Action<StringBuilder> read, Func<string, string> confirmBoundary)
    {
        var buffer = new StringBuilder(PathBufferChars);
        while (true)
        {
            read(buffer);
            // Shell may internally cap path fields at MAX_PATH even with a larger caller buffer.
            // At that boundary neither S_OK nor the returned length proves completeness.
            if (buffer.Length >= MaxPathChars - 1 && buffer.Length <= MaxPathChars)
                return confirmBoundary(buffer.ToString());
            if (buffer.Length < buffer.Capacity - 1) return buffer.ToString();
            // No required-length result is exposed by IShellLink; a full buffer is inconclusive.
            if (buffer.Capacity > int.MaxValue / 2)
                throw new InvalidDataException("The shortcut path could not be read completely.");
            buffer = new StringBuilder(buffer.Capacity * 2);
        }
    }

    private static IShellLinkW Load(string path)
    {
        var link = (IShellLinkW)(object)new ShellLink();
        ((IPersistFile)link).Load(path, StgmRead);
        return link;
    }

    private static void Save(IShellLinkW link, string path) => ((IPersistFile)link).Save(path, fRemember: true);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    // PROPVARIANT's union starts at byte 8 and has two pointer-sized slots (24 bytes on x64, 16 on x86).
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
        public IntPtr UnionRemainder;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        [PreserveSig]
        int GetValue(in PropertyKey key, out PropVariant value);
        void SetValue(in PropertyKey key, in PropVariant value);
        void Commit();
    }

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

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListEx(IntPtr pidl, StringBuilder path, uint characterCount, int flags);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern uint ILGetSize(IntPtr pidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributes, out uint returnedAttributes);
}
