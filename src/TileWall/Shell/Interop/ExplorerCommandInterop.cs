using System.Runtime.InteropServices;

namespace TileWall.Shell.Interop;

#pragma warning disable SYSLIB1096 // InterfaceIsIInspectable 的弃用告警（M9 设计 R-E）：CCW 声明仍须精确复刻
                                 // IInspectable 派生接口的 vtable 槽位，应用工程未开 TreatWarningsAsErrors。

/// <summary>
/// M9 Explorer 右键扩展的全部 COM 互操作声明（shobjidl_core / objidl / combase）：
/// com:ExeServer 路线下主 exe 兼任 COM 服务器——CoRegisterClassObject 注册类对象（§4.1），
/// IExplorerCommand 的 CCW 必须显式补齐 IInspectable 三槽否则 vtable 错位崩 Explorer（§4.2 约束 1）。
/// 常量数值来源：Windows SDK 头文件 shobjidl_core.idl / objidl.idl / combaseapi.h。
/// </summary>
internal static class ExplorerCommandInterop
{
    // ———— ole32：COM 服务器注册（com:ExeServer 进程模型，§4.1） ————

    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    internal static extern int CoRegisterClassObject(
        ref Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
        uint dwClsContext,
        uint flags,
        out uint lpdwRegister);

    [DllImport("ole32.dll")]
    internal static extern int CoRevokeClassObject(uint dwRegister);

    internal const uint CoinitApartmentthreaded = 0x2; // COINIT_APARTMENTTHREADED
    internal const int RpcEChangedMode = unchecked((int)0x80010106); // 线程已按另一套间模型初始化
    internal const int SFalse = 1; // 已初始化（同模式）

    internal const uint ClsctxLocalServer = 0x4;  // CLSCTX_LOCAL_SERVER
    internal const uint RegclsMultipleuse = 0x1;  // REGCLS_MULTIPLEUSE：已运行进程可继续服务新激活

    // ———— shobjidl_core：IExplorerCommand 查询常量 ————

    internal const uint SigdnFilesysPath = 0x80058000; // SIGDN_FILESYSPATH（ Invoke 取文件系统路径）
    internal const uint EcfDefault = 0;                // EXPCMDFLAGS ECF_DEFAULT（GetFlags 恒值）
    internal const uint EcsEnabled = 0;                // EXPCMDSTATE ECS_ENABLED（GetState 恒值，零 IO）
    internal const int ENotimpl = unchecked((int)0x80004001);  // IInspectable 三槽的占位返回
    internal const int Enointerface = unchecked((int)0x80004002);
    internal const int ClsEnoaggregation = unchecked((int)0x80040110);

    internal static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    internal static readonly Guid IidIExplorerCommand = new("A08CE4D0-FA25-44AB-B57C-30302FF98CAE");

    // ———— user32：STA 消息泵（COM 类对象来电的派发通道；MwmoInputavailable 保证超时前有电即返回） ————

    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")]
    internal static extern int MsgWaitForMultipleObjectsEx(
        uint nCount, IntPtr pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern bool PeekMessageW(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessageW(ref Msg lpMsg);

    internal const uint QsAllinput = 0x04FF;
    internal const uint MwmoInputavailable = 0x2;
    internal const uint PmRemove = 0x1;
    internal const uint WaitObject0 = 0;
    internal const uint WaitTimeout = 0x102;
}

/// <summary>IClassFactory（objidl.idl）：com:ExeServer 的类对象必须实现的激活入口。</summary>
[ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);

    [PreserveSig]
    int LockServer(bool fLock);
}

/// <summary>IBindCtx（objidl.idl）：仅作为 Invoke 的参数类型出现，本实现从不解引用。</summary>
[ComImport, Guid("0000000E-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IBindCtx
{
}

/// <summary>IShellItem（shobjidl_core.idl）：只消费 GetDisplayName；前四槽按 IDL 次序补位。</summary>
[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig]
    int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetParent(out IShellItem ppsi);

    [PreserveSig]
    int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

    [PreserveSig]
    int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int Compare(IShellItem psi, uint hint, out int piOrder);
}

/// <summary>PROPERTYKEY（wtypes.h）：IShellItemArray.GetPropertyDescriptionList 槽位的参数形状。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;
}

/// <summary>
/// IShellItemArray（shobjidl_core.idl）：只消费 GetCount/GetItemAt；前四槽按 IDL 次序补位
/// （ComImport 声明允许接口方法的前缀子集，跳槽调用不成立）。
/// </summary>
[ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArray
{
    [PreserveSig]
    int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetPropertyStore(uint flags, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetPropertyDescriptionList(ref PropertyKey keyType, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetAttributes(uint attribFlags, uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int GetCount(out uint pdwNumItems);

    [PreserveSig]
    int GetItemAt(uint dwIndex, out IShellItem ppsiItem);
}

/// <summary>
/// IExplorerCommand（shobjidl_core.idl，IID A08CE4D0-FA25-44AB-B57C-30302FF98CAE，IInspectable 派生）。
/// C# ComImport 声明必须显式补 IInspectable 三槽（GetIids/GetRuntimeClassName/GetTrustLevel，E_NOTIMPL 可接受）
/// 再列八个命令方法——缺槽即 vtable 错位，Explorer 进程当场崩溃（M9 设计 §4.2 约束 1）。
/// </summary>
[ComImport, Guid("A08CE4D0-FA25-44AB-B57C-30302FF98CAE"), InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
internal interface IExplorerCommand
{
    // —— IInspectable 三槽（IUnknown 三槽由运行时自动供给） ——
    [PreserveSig]
    int GetIids(out uint iidCount, IntPtr iids);

    [PreserveSig]
    int GetRuntimeClassName(out IntPtr className);

    [PreserveSig]
    int GetTrustLevel(out int trustLevel);

    // —— IExplorerCommand 八法 ——
    [PreserveSig]
    int GetCanonicalName(out Guid guidCommandName);

    [PreserveSig]
    int GetFlags(out uint pFlags);

    [PreserveSig]
    int GetTitle(IShellItemArray psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

    [PreserveSig]
    int GetIcon(IShellItemArray psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string ppszIcon);

    [PreserveSig]
    int GetToolTip(IShellItemArray psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string ppszInfotip);

    [PreserveSig]
    int GetState(IShellItemArray psiItemArray, bool fOkToBeSlow, out uint pCmdState);

    [PreserveSig]
    int Invoke(IShellItemArray psiItemArray, IBindCtx pbc);
}

/// <summary>
/// IApplicationActivationManager（shobjidl_core.idl）：packaged 冷启动经系统激活保留包身份
/// （数据目录落在包 LocalState；直启 exe 会以无身份进程读 unpackaged 数据目录，§3.3 隔离语义）。
/// 只声明并调用首槽 ActivateApplication（前缀子集合法）；AUMID = &lt;PFN&gt;!App。
/// </summary>
[ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationActivationManager
{
    [PreserveSig]
    int ActivateApplication(string appUserModelId, string arguments, int options, out uint processId);
}

/// <summary>CLSID_ApplicationActivationManager（shobjidl_core.idl）。</summary>
[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
internal sealed class ApplicationActivationManagerCom
{
}
