using System.Runtime.InteropServices;

namespace TileWall.Shell.Interop;

/// <summary>
/// M7 全部 Win32 互操作声明的唯一汇聚文件（评审用隔离面）：托盘（shell32）、热键（user32）、
/// 隐藏宿主窗（user32 窗口类/窗）、托盘菜单（user32）、单实例互斥体（kernel32）。
/// 常量数值来源：Win32 文档 winuser.h / shellapi.h。既有 SetWindowLongPtrW 先例在 TilePropertyWindow（M4，保留原位）。
/// </summary>
internal static class Win32Api
{
    // Low-level input and the dedicated hook-thread message loop (Windows SDK winuser.h).
    internal delegate nint LowLevelHookProc(int code, nint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    internal struct LowLevelKeyboardInput
    {
        internal uint VirtualKey, ScanCode, Flags, Time;
        internal nuint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LowLevelMouseInput
    {
        internal InteropPoint Point;
        internal uint MouseData, Flags, Time;
        internal nuint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ThreadMessage
    {
        internal nint Window;
        internal uint Message;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal InteropPoint Point;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        internal ushort VirtualKey, ScanCode;
        internal uint Flags, Time;
        internal nuint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int X, Y;
        internal uint MouseData, Flags, Time;
        internal nuint ExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] internal KeyboardInput Keyboard;
        [FieldOffset(0)] internal MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SyntheticInput
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWindowsHookExW(int hookType, LowLevelHookProc callback, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, [In] SyntheticInput[] inputs, int size);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessageW(out ThreadMessage message, nint window, uint minimum, uint maximum, uint remove);

    // BOOL here is signed: -1 is failure, 0 is WM_QUIT, >0 is a message.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessageW(out ThreadMessage message, nint window, uint minimum, uint maximum);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern nint DispatchMessageW(ref ThreadMessage message);

    // Native ownership, frame suppression, and owner input gating for modal windows.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int index, int value);

    internal static IntPtr GetWindowLongPtrW(IntPtr hWnd, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr64(hWnd, index) : new IntPtr(GetWindowLong32(hWnd, index));

    internal static IntPtr SetWindowLongPtrW(IntPtr hWnd, int index, IntPtr value) => IntPtr.Size == 8
        ? SetWindowLongPtr64(hWnd, index, value) : new IntPtr(SetWindowLong32(hWnd, index, value.ToInt32()));

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnableWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hWnd, uint attribute, ref uint value, uint size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct DwmMargins { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DwmBlurBehind
    {
        public uint Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Enable;
        public IntPtr Region;
        [MarshalAs(UnmanagedType.Bool)] public bool TransitionOnMaximized;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")]
    internal static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref DwmMargins margins);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmEnableBlurBehindWindow(IntPtr hWnd, ref DwmBlurBehind blur);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    internal static extern int FillRect(IntPtr hDC, ref NativeRect rect, IntPtr brush);

    internal delegate IntPtr SubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc callback, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc callback, nuint id);

    [DllImport("comctl32.dll")]
    internal static extern IntPtr DefSubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);


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

    // ———— user32：WM_COPYDATA（M9 §4.3：携带路径负载的单实例通道；数据由系统跨进程封送） ————

    internal const uint WmCopydata = 0x004A;

    [StructLayout(LayoutKind.Sequential)]
    internal struct CopyDataStruct
    {
        public IntPtr DwData;
        public int CbData;
        public IntPtr LpData;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint msg, IntPtr wParam, ref CopyDataStruct lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

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

    // ———— WM_COPYDATA 声明 HWND 的映像路由过滤（wParam 句柄 → PID → 映像路径） ————

    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION：仅查基本信息/映像路径，权限面最小的查询权。</summary>
    internal const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // winuser.h: temporary exclusion while reading one desktop backdrop frame.
    internal const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    /// <summary>dwFlags=0：返回 Win32 路径形态（与 Environment.ProcessPath 可直接比对）。</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, System.Text.StringBuilder lpExeName, ref uint lpdwSize);

    // ———— C2/F-03：事件日志兜底（advapi32；仅「日志初始化前失败」的最后观测通道用） ————

    internal const ushort EventlogErrorType = 0x0001; // EVENTLOG_ERROR_TYPE（= managed EventLogEntryType.Error）

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr RegisterEventSourceW(string? lpUNCServerName, string lpSourceName);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool ReportEventW(
        IntPtr hEventLog, ushort wType, ushort wCategory, uint dwEventID, IntPtr lpUserSid,
        ushort wNumStrings, uint dwDataSize, ref IntPtr lpStrings, IntPtr lpRawData);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool DeregisterEventSource(IntPtr hEventLog);

    /// <summary>向 Application 日志写一条 Error（source 须已注册，否则返回 false 由调用方跳过——创建源需管理员）。</summary>
    internal static bool TryReportApplicationEventError(string source, string message)
    {
        var handle = RegisterEventSourceW(null, source);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var messagePtr = IntPtr.Zero;
        try
        {
            messagePtr = Marshal.StringToHGlobalUni(message);
            // ReportEventW expects LPCWSTR*; ref messagePtr supplies the one-element string array.
            return ReportEventW(handle, EventlogErrorType, 0, 0, IntPtr.Zero, 1, 0, ref messagePtr, IntPtr.Zero);
        }
        finally
        {
            try
            {
                if (messagePtr != IntPtr.Zero) Marshal.FreeHGlobal(messagePtr);
            }
            finally
            {
                _ = DeregisterEventSource(handle);
            }
        }
    }

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
    internal const uint TpmBottomalign = 0x0020;

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

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect iconLocation);

    internal const uint NimAdd = 0x00000000;
    internal const uint NimModify = 0x00000001;
    internal const uint NimDelete = 0x00000002;
    internal const uint NimSetversion = 0x00000004;

    internal const uint NifMessage = 0x00000001;
    internal const uint NifIcon = 0x00000002;
    internal const uint NifTip = 0x00000004;
    internal const uint NifShowTip = 0x00000080;

    internal const uint NotifyIconVersion4 = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NotifyIconIdentifier
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

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

    /// <summary>NOTIFYICON_VERSION_4：事件在 lParam 低字、图标 ID 在高字；坐标只对协议定义的事件有效。</summary>
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
