using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TileWall.Shell.Interop;

/// <summary>Asks this session's desktop Explorer to start the fixed current-user uninstall entry.</summary>
/// <remarks>
/// Uses Microsoft's ExecInExplorer sample, not a newly created Shell.Application:
/// https://learn.microsoft.com/windows/win32/shell/samples-execinexplorer .
/// Raw vtable slots/signatures verified against Windows SDK 10.0.26100.0:
/// ExDisp.Idl (IShellWindows.FindWindowSW: 15), servprov.idl (QueryService: 3),
/// ShObjIdl_core.idl (IOleWindow.GetWindow: 3, IShellBrowser.QueryActiveShellView: 15,
/// IShellView.GetItemObject: 15), ShlDisp.Idl (IShellFolderViewDual.Application: 7,
/// IShellDispatch2.ShellExecute: 31). All pointers stay on the acquiring STA.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class DesktopUserUninstallLauncher
{
    internal static void Launch(string validatedExecutable, bool quiet)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(validatedExecutable);
        if (!Path.IsPathFullyQualified(validatedExecutable))
            throw new ArgumentException("The installed executable must have an absolute path.", nameof(validatedExecutable));
        var executable = Path.GetFullPath(validatedExecutable);
        using var desktop = AcquireDesktopApplication();
        desktop.Submit(executable, quiet);
        // ShellExecute accepting the request does not establish process startup or uninstall completion.
        // The fixed receiver must reject elevation, including when --from-desktop is present.
    }

    // Acquisition only reads desktop COM/window identity. It does not invoke ShellExecute or drive the UI.
    internal static DesktopApplication AcquireDesktopApplication() => new();

    internal sealed class DesktopApplication : IDisposable
    {
        private readonly int _threadId = Environment.CurrentManagedThreadId;
        private nint _application;
        private bool _initialized;

        internal DesktopApplication()
        {
            // S_FALSE still owns a matching CoUninitialize; RPC_E_CHANGED_MODE owns none.
            Check(CoInitializeEx(0, 0x2 | 0x4), "CoInitializeEx(STA)");
            _initialized = true;
            nint windows = 0, desktop = 0, service = 0, browser = 0, view = 0, background = 0, folder = 0, application = 0;
            try
            {
                var shellWindow = GetShellWindow();
                if (shellWindow == 0) throw new IOException("The desktop Shell is unavailable.");
                var shellProcess = WindowProcess(shellWindow);
                var callerSession = SessionId((uint)Environment.ProcessId);
                if (SessionId(shellProcess) != callerSession)
                    throw new IOException("The desktop Shell belongs to another session.");

                var clsid = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"); // ShellWindows
                var iidWindows = new Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85");
                Check(CoCreateInstance(ref clsid, 0, 0x4, ref iidWindows, out windows), "CoCreateInstance(ShellWindows)");
                Require(windows, "ShellWindows");
                // The official ExecInExplorer sample uses two VT_EMPTY values with SWC_DESKTOP.
                object? location = null, root = null;
                Check(Method<FindWindow>(windows, 15)(windows, ref location, ref root, 0x8, out _, 0x1, out desktop),
                    "IShellWindows.FindWindowSW(desktop)");
                Require(desktop, "Desktop dispatch");
                service = Query(desktop, new Guid("6D5140C1-7436-11CE-8034-00AA006009FA"));
                var topLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
                var iidBrowser = new Guid("000214E2-0000-0000-C000-000000000046");
                Check(Method<QueryService>(service, 3)(service, ref topLevelBrowser, ref iidBrowser, out browser),
                    "IServiceProvider.QueryService(STopLevelBrowser)");
                Require(browser, "Desktop Shell browser");
                Check(Method<GetPointer>(browser, 3)(browser, out var browserWindow), "IShellBrowser.GetWindow");
                if (browserWindow == 0 || WindowProcess(browserWindow) != shellProcess ||
                    SessionId(shellProcess) != callerSession)
                    throw new IOException("The desktop automation browser does not belong to this session's Shell.");

                Check(Method<GetPointer>(browser, 15)(browser, out view), "IShellBrowser.QueryActiveShellView");
                Require(view, "Desktop Shell view");
                var iidDispatch = new Guid("00020400-0000-0000-C000-000000000046");
                Check(Method<GetItemObject>(view, 15)(view, 0, ref iidDispatch, out background),
                    "IShellView.GetItemObject(SVGIO_BACKGROUND)");
                Require(background, "Desktop view automation");
                folder = Query(background, new Guid("E7A1AF80-4D96-11CF-960C-0080C7F4EE85"));
                // ShlDisp.Idl: Application([out, retval] IDispatch **ppid), NOT VARIANT*.
                // Reading the native interface pointer avoids default object/VARIANT return marshaling.
                Check(Method<GetPointer>(folder, 7)(folder, out application), "IShellFolderViewDual.Application");
                Require(application, "Desktop Shell application");
                _application = Query(application, new Guid("A4C6892C-3BA9-11D2-9DEA-00C04FB16162"));
                // Reject a desktop replacement during acquisition rather than use a stale Shell object.
                if (GetShellWindow() != shellWindow || WindowProcess(shellWindow) != shellProcess)
                    throw new IOException("The desktop Shell changed during uninstall handoff.");
            }
            catch
            {
                Release(ref _application);
                throw;
            }
            finally
            {
                Release(ref application);
                Release(ref folder);
                Release(ref background);
                Release(ref view);
                Release(ref browser);
                Release(ref service);
                Release(ref desktop);
                Release(ref windows);
                if (_application == 0) Dispose();
            }
        }

        internal void Submit(string executable, bool quiet)
        {
            CheckThread();
            ObjectDisposedException.ThrowIf(_application == 0, this);
            var directory = Path.GetDirectoryName(executable) ?? throw new IOException("Missing installation directory.");
            var arguments = "--uninstall-current-user" + (quiet ? " --quiet" : string.Empty) + " --from-desktop";
            Check(Method<ShellExecute>(_application, 31)(_application, executable, arguments, directory, "open", 1),
                "IShellDispatch2.ShellExecute(current-user uninstall)");
        }

        public void Dispose()
        {
            CheckThread();
            Release(ref _application);
            if (_initialized)
            {
                _initialized = false;
                CoUninitialize();
            }
        }

        private void CheckThread()
        {
            if (Environment.CurrentManagedThreadId != _threadId)
                throw new InvalidOperationException("Desktop COM ownership must remain on the acquiring STA thread.");
        }
    }

    private static uint WindowProcess(nint window)
    {
        if (GetWindowThreadProcessId(window, out var process) == 0 || process == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot identify the desktop Shell window process.");
        return process;
    }

    private static uint SessionId(uint process)
    {
        if (!ProcessIdToSessionId(process, out var session))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot identify the desktop Shell session.");
        return session;
    }

    private static nint Query(nint instance, Guid iid)
    {
        nint result = 0;
        try
        {
            Check(Marshal.QueryInterface(instance, in iid, out result), "QueryInterface(" + iid + ")");
            Require(result, "COM interface " + iid);
            return result;
        }
        catch
        {
            Release(ref result);
            throw;
        }
    }

    private static void Require(nint instance, string name)
    {
        if (instance == 0) throw new IOException(name + " is unavailable.");
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));

    private static void Release(ref nint instance)
    {
        if (instance == 0) return;
        Marshal.Release(instance);
        instance = 0;
    }

    private static void Check(int result, string operation)
    {
        if (result < 0) throw new COMException($"{operation}: HRESULT 0x{result:X8}.", result);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FindWindow(nint self,
        [In, MarshalAs(UnmanagedType.Struct)] ref object? location, [In, MarshalAs(UnmanagedType.Struct)] ref object? root,
        int windowClass, out int window, int options, out nint dispatch);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryService(nint self,
        ref Guid service, ref Guid iid, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPointer(nint self, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetItemObject(nint self,
        uint item, ref Guid iid, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ShellExecute(nint self,
        [MarshalAs(UnmanagedType.BStr)] string file, [MarshalAs(UnmanagedType.Struct)] object arguments,
        [MarshalAs(UnmanagedType.Struct)] object directory, [MarshalAs(UnmanagedType.Struct)] object operation,
        [MarshalAs(UnmanagedType.Struct)] object show);

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context,
        ref Guid iid, out nint instance);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint GetShellWindow();
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] private static extern uint GetWindowThreadProcessId(
        nint window, out uint process);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint process, out uint session);
}
