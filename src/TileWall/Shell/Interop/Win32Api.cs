using System.Runtime.InteropServices;

namespace TileWall.Shell.Interop;

/// <summary>
/// M7 全部 Win32 互操作声明的唯一汇聚文件（评审用隔离面）：托盘（shell32）、热键（user32）、
/// 隐藏宿主窗（user32 窗口类/窗）、托盘菜单（user32）、单实例互斥体（kernel32）。
/// 常量数值来源：Win32 文档 winuser.h / shellapi.h。既有 SetWindowLongPtrW 先例在 TilePropertyWindow（M4，保留原位）。
/// </summary>
internal static class Win32Api
{
    // ————————————————————————————— kernel32：单实例互斥体（§7.1） —————————————————————————————

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateMutexW(IntPtr lpMutexAttributes, bool bInitialOwner, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReleaseMutex(IntPtr hMutex);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    internal const uint ErrorAlreadyExists = 183;

    // ————————————————————————————— user32：宿主窗（§4.2） —————————————————————————————

    internal delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WndClassExW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern ushort RegisterClassExW(ref WndClassExW lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    internal const uint WsPopup = 0x80000000;
    internal const uint WsExToolWindow = 0x00000080;
    internal const uint WsExNoActivate = 0x08000000;

    // ————————————————————————————— user32：消息 —————————————————————————————

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    internal const uint WmNull = 0x0000;
    internal const uint WmDestroy = 0x0002;
    internal const uint WmContextmenu = 0x007B;
    internal const uint WmMousemove = 0x0200;
    internal const uint WmLbuttondown = 0x0201;
    internal const uint WmLbuttonup = 0x0202;
    internal const uint WmLbuttondblclk = 0x0203;
    internal const uint WmHotkey = 0x0312;
    internal const uint WmSettingchange = 0x001A;   // WM_SETTINGCHANGE（SPI_SETWORKAREA 广播同路，M8 §6.2）
    internal const uint WmDisplaychange = 0x007E;   // WM_DISPLAYCHANGE（分辨率/显示器变更，M8 §6.2）

    // ————————————————————————————— user32：托盘菜单（§4.2，即建即毁） —————————————————————————————

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIdNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetCursorPos(out InteropPoint lpPoint);

    internal const uint MfString = 0x00000000;
    internal const uint MfSeparator = 0x00000800;
    internal const uint TpmReturncmd = 0x0100;
    internal const uint TpmRightbutton = 0x0002;

    internal struct InteropPoint
    {
        public int X;
        public int Y;
    }

    // ————————————————————————————— user32：图标（§4.2 R2） —————————————————————————————

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr LoadImageW(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    internal const uint ImageIcon = 1;
    internal const uint LrLoadfromfile = 0x00000010;
    internal const int SmCxsmicon = 49;
    internal const int SmCysmicon = 50;

    // ————————————————————————————— user32：全局热键（§5.2） —————————————————————————————

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;
    internal const uint ModNorepeat = 0x4000; // 按住不重复（Win7+ 文档语义；不自造去抖）

    // ————————————————————————————— shell32：托盘图标（§4.1/§4.2） —————————————————————————————

    [DllImport("shell32.dll", SetLastError = true)]
    internal static extern bool Shell_NotifyIconW(uint dwMessage, ref NotifyIconDataW lpData);

    internal const uint NimAdd = 0x00000000;
    internal const uint NimModify = 0x00000001;
    internal const uint NimDelete = 0x00000002;
    internal const uint NimSetversion = 0x00000004;

    internal const uint NifMessage = 0x00000001;
    internal const uint NifIcon = 0x00000002;
    internal const uint NifTip = 0x00000004;

    internal const uint NotifyIconVersion4 = 4;

    /// <summary>NOTIFYICONDATAW（shellapi.h 布局；szTip 128 / szInfo 256 / szInfoTitle 64 字符）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconDataW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion; // 与 uTimeout/AI 时间构成 union（v4 协议用 uVersion）
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    internal static NotifyIconDataW NewNotifyIconData(IntPtr hwnd, uint id) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconDataW>(),
        hWnd = hwnd,
        uID = id,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    /// <summary>NOTIFYICON_VERSION_4：事件在 lParam 低字；屏幕坐标在 wParam 低/高字（WM_CONTEXTMENU 键盘触发时为 -1）。</summary>
    internal static uint LowWord(IntPtr value) => (uint)((long)value & 0xFFFF);

    internal static uint HighWord(IntPtr value) => (uint)(((long)value >> 16) & 0xFFFF);

    // ————————————————————————————— shell32/user32/gdi32：候选图标提取（M8 §5.4） —————————————————————————————

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref ShFileInfoW psfi, uint cbFileInfo, uint uFlags);

    internal const uint ShgfiIcon = 0x000000100;
    internal const uint ShgfiLargeicon = 0x000000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShFileInfoW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IconInfoW
    {
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetIconInfo(IntPtr hIcon, ref IconInfoW piconinfo);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapW
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int GetObjectW(IntPtr hgdiobj, int cbBuffer, out BitmapW lpvObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, IntPtr lpvBits, ref BitmapInfoW lpbmi, uint usage);

    internal const uint DibRgbColors = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeaderW
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;

        public static BitmapInfoHeaderW BgraTopDown(int width, int height) => new()
        {
            biSize = (uint)Marshal.SizeOf<BitmapInfoHeaderW>(),
            biWidth = width,
            biHeight = -height, // 负高 = 自上而下行序
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoW
    {
        public BitmapInfoHeaderW bmiHeader;
        public uint bmiColors; // BI_RGB 32bpp 不用调色板，占位一个 DWORD
    }
}
