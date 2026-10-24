using System.Globalization;
using System.Runtime.InteropServices;

namespace TileWall.Shell.Interop;

/// <summary>
/// Native UIA only. Slots/signatures verified against Windows SDK 10.0.26100.0 um/UIAutomationClient.h:
/// IUIAutomation2 inherits IUIAutomation slots 0..57; timeout setters are 61/63.
/// IUIAutomationElement::CurrentBoundingRectangle is slot 43 and returns Win32 RECT (four LONGs), not UiaRect doubles.
/// Raw pointers and SAFEARRAY/BSTR ownership remain entirely on the dedicated MTA; no RCW crosses threads.
/// </summary>
internal static class StartButtonAutomationInterop
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly PhysicalRect ToPhysical() => new(Left, Top, Right, Bottom);
    }

    internal sealed record ButtonData(string RuntimeId, string AutomationId, string ClassName,
        nint NativeWindow, PhysicalRect Bounds, bool IsEnabled, bool IsOffscreen);

    internal sealed class Session : IDisposable
    {
        private nint _automation;
        private nint _buttonCondition;
        private bool _initialized;
        internal int LastReadElementCount { get; private set; }

        public Session()
        {
            Check(CoInitializeEx(0, 0), "CoInitializeEx(MTA)");
            _initialized = true;
            try
            {
                var clsid = new Guid("e22ad333-b25f-460c-83d0-0581107395c9"); // CUIAutomation8
                var iid = new Guid("34723aff-0c9d-49d0-9896-7ab52df8cd8a"); // IUIAutomation2
                Check(CoCreateInstance(ref clsid, 0, 1, ref iid, out _automation), "CoCreateInstance(CUIAutomation8/IUIAutomation2)");
                if (_automation == 0) throw new StartButtonLocatorException(StartButtonLocatorFailure.AutomationUnavailable);
                Check(Method<SetUInt>(_automation, 61)(_automation, 250), "IUIAutomation2.put_ConnectionTimeout(250)");
                Check(Method<SetUInt>(_automation, 63)(_automation, 250), "IUIAutomation2.put_TransactionTimeout(250)");
                // IUIAutomation::CreatePropertyCondition, UIA_ControlTypePropertyId, UIA_ButtonControlTypeId.
                // Filtering in the provider avoids reading every taskbar descendant across COM.
                Check(Method<CreatePropertyCondition>(_automation, 23)(_automation, 30003, 50000, out _buttonCondition),
                    "IUIAutomation.CreatePropertyCondition(ControlType=Button)");
                if (_buttonCondition == 0) throw new StartButtonLocatorException(StartButtonLocatorFailure.AutomationUnavailable);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public IReadOnlyList<ButtonData> ReadButtons(nint taskbar, int expectedProcessId, Func<bool> cancelled)
        {
            LastReadElementCount = 0;
            nint root = 0, elements = 0;
            try
            {
                Check(Method<ElementFromHandle>(_automation, 6)(_automation, taskbar, out root), "IUIAutomation.ElementFromHandle(taskbar)");
                if (root == 0) throw new StartButtonLocatorException(StartButtonLocatorFailure.ButtonUnrecognized,
                    context: "ElementFromHandle returned no taskbar element.");
                Check(Method<FindAll>(root, 6)(root, 4, _buttonCondition, out elements),
                    "IUIAutomationElement.FindAll(Descendants, Button)");
                if (elements == 0) throw new StartButtonLocatorException(StartButtonLocatorFailure.ButtonUnrecognized,
                    context: "FindAll returned no button array.");
                var count = ReadInt(elements, 3, "IUIAutomationElementArray.Length");
                LastReadElementCount = count;
                if (count < 0 || count > 1024) throw new StartButtonLocatorException(StartButtonLocatorFailure.AmbiguousButton);
                var found = new List<ButtonData>();
                for (var index = 0; index < count; index++)
                {
                    if (cancelled()) throw new OperationCanceledException();
                    nint element = 0;
                    try
                    {
                        Check(Method<ArrayElement>(elements, 4)(elements, index, out element), "IUIAutomationElementArray.GetElement");
                        if (element == 0 || ReadInt(element, 21, "IUIAutomationElement.CurrentControlType") != 50000) continue;
                        var automationId = ReadString(element, 29, "IUIAutomationElement.CurrentAutomationId");
                        var className = ReadString(element, 30, "IUIAutomationElement.CurrentClassName");
                        if (automationId is not ("StartButton" or "Start") && className != "Start") continue;
                        if (ReadInt(element, 20, "IUIAutomationElement.CurrentProcessId") != expectedProcessId) continue;
                        Check(Method<GetPointer>(element, 36)(element, out var nativeWindow), "IUIAutomationElement.CurrentNativeWindowHandle");
                        // Win10's Start has no AutomationId. Require the real Start child window for this fallback.
                        if (automationId is not ("StartButton" or "Start")
                            && (nativeWindow == 0 || !StartButtonLocator.Native.IsStartChild(nativeWindow, taskbar, expectedProcessId))) continue;
                        if (nativeWindow != 0 && !StartButtonLocator.Native.BelongsToTaskbar(nativeWindow, taskbar, expectedProcessId)) continue;
                        Check(Method<GetRect>(element, 43)(element, out var rectangle), "IUIAutomationElement.CurrentBoundingRectangle");
                        var bounds = rectangle.ToPhysical();
                        // Hidden auto-hide elements may have an empty rectangle. Identity can still be recognized;
                        // only the locator's visible, valid-rectangle filter may produce a capture target.
                        var runtimeId = ReadRuntimeId(element);
                        if (runtimeId.Length == 0) continue;
                        found.Add(new(runtimeId, automationId, className, nativeWindow, bounds,
                            ReadInt(element, 28, "IUIAutomationElement.CurrentIsEnabled") != 0,
                            ReadInt(element, 38, "IUIAutomationElement.CurrentIsOffscreen") != 0));
                    }
                    catch (StartButtonLocatorException error)
                    {
                        throw new StartButtonLocatorException(error.Code, error.HResult,
                            $"Button array index {index}: {error.Message}");
                    }
                    finally { Release(ref element); }
                }

                return found;
            }
            finally
            {
                Release(ref elements);
                Release(ref root);
            }
        }

        public void Dispose()
        {
            Release(ref _buttonCondition);
            Release(ref _automation);
            if (_initialized)
            {
                _initialized = false;
                CoUninitialize();
            }
        }
    }

    private static int ReadInt(nint instance, int slot, string operation)
    {
        Check(Method<GetInt>(instance, slot)(instance, out var result), operation);
        return result;
    }

    private static string ReadString(nint instance, int slot, string operation)
    {
        nint value = 0;
        try
        {
            Check(Method<GetPointer>(instance, slot)(instance, out value), operation);
            return value == 0 ? string.Empty : Marshal.PtrToStringBSTR(value);
        }
        finally { if (value != 0) Marshal.FreeBSTR(value); }
    }

    private static string ReadRuntimeId(nint element)
    {
        nint array = 0;
        try
        {
            Check(Method<GetPointer>(element, 4)(element, out array), "IUIAutomationElement.GetRuntimeId");
            if (array == 0 || SafeArrayGetDim(array) != 1 || SafeArrayGetElemsize(array) != sizeof(int)) return string.Empty;
            Check(SafeArrayGetLBound(array, 1, out var lower), "GetRuntimeId.SafeArrayGetLBound");
            Check(SafeArrayGetUBound(array, 1, out var upper), "GetRuntimeId.SafeArrayGetUBound");
            var length = (long)upper - lower + 1;
            if (length <= 0 || length > 64) return string.Empty;
            Check(SafeArrayAccessData(array, out var data), "GetRuntimeId.SafeArrayAccessData");
            try
            {
                var values = new int[(int)length];
                Marshal.Copy(data, values, 0, values.Length);
                return string.Join(".", values.Select(value => value.ToString(CultureInfo.InvariantCulture)));
            }
            finally { Check(SafeArrayUnaccessData(array), "GetRuntimeId.SafeArrayUnaccessData"); }
        }
        finally { if (array != 0) Check(SafeArrayDestroy(array), "GetRuntimeId.SafeArrayDestroy"); }
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(instance);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * nint.Size));
    }

    private static void Release(ref nint instance)
    {
        if (instance == 0) return;
        Marshal.Release(instance);
        instance = 0;
    }

    private static void Check(int result, string operation)
    {
        if (result < 0) throw new StartButtonLocatorException(StartButtonLocatorFailure.AutomationUnavailable, result,
            $"{operation}: HRESULT 0x{result:X8}.");
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetUInt(nint self, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPointer(nint self, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInt(nint self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreatePropertyCondition(nint self,
        int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, out nint condition);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ElementFromHandle(nint self, nint window, out nint element);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FindAll(nint self, int scope, nint condition, out nint elements);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ArrayElement(nint self, int index, out nint element);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetRect(nint self, out NativeRect rectangle);

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint coInit);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);
    [DllImport("oleaut32.dll")] private static extern uint SafeArrayGetDim(nint array);
    [DllImport("oleaut32.dll")] private static extern uint SafeArrayGetElemsize(nint array);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayGetLBound(nint array, uint dimension, out int lower);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayGetUBound(nint array, uint dimension, out int upper);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayAccessData(nint array, out nint data);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayUnaccessData(nint array);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayDestroy(nint array);
}
