using System.Runtime.InteropServices;

namespace TileWall.Views;

/// <summary>One native file dialog with an explicit action returning its current directory.</summary>
internal static class TargetItemPicker
{
    private static readonly Guid ShellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private const uint FolderButton = 0x5001;
    internal static string? Pick(IntPtr owner, string folderAction, string? initialFolder)
    {
        var dialog = (IFileDialog)new FileOpenDialog();
        uint cookie = 0;
        var sink = new Events(dialog);
        try
        {
            dialog.GetOptions(out var options);
            // FORCEFILESYSTEM | FILEMUSTEXIST | PATHMUSTEXIST | NODEREFERENCELINKS.
            dialog.SetOptions(options | 0x40 | 0x1000 | 0x800 | 0x100000);
            SetFolder(dialog, initialFolder);
            ((IFileDialogCustomize)dialog).AddPushButton(FolderButton, folderAction);
            dialog.Advise(sink, out cookie);
            var hr = dialog.Show(owner);
            if (sink.Folder is not null) return sink.Folder;
            if (hr == unchecked((int)0x800704C7)) return null;
            Marshal.ThrowExceptionForHR(hr);
            dialog.GetResult(out var item);
            try { return PathOf(item); }
            finally { Marshal.ReleaseComObject(item); }
        }
        finally
        {
            if (cookie != 0) dialog.Unadvise(cookie);
            Marshal.ReleaseComObject(dialog);
        }
    }

    internal static string? GetInitialFolderForPath(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;
        try
        {
            var path = Environment.ExpandEnvironmentVariables(sourcePath.Trim());
            if (!Path.IsPathFullyQualified(path)) return null;
            var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) ? Path.GetFullPath(folder) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static void SetSuggestedFolder(Microsoft.Windows.Storage.Pickers.FileOpenPicker picker, string? sourcePath)
    {
        var folder = GetInitialFolderForPath(sourcePath);
        if (folder is null) return;
        picker.SuggestedFolder = folder;
        picker.SuggestedStartFolder = folder;
    }

    internal static void SetSuggestedFolder(Microsoft.Windows.Storage.Pickers.FolderPicker picker, string? sourcePath)
    {
        var folder = GetInitialFolderForPath(sourcePath);
        if (folder is null) return;
        picker.SuggestedFolder = folder;
        picker.SuggestedStartFolder = folder;
    }

    private static void SetFolder(IFileDialog dialog, string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;
        IShellItem? folder = null;
        try
        {
            var shellItemId = ShellItemId;
            if (SHCreateItemFromParsingName(folderPath, IntPtr.Zero, ref shellItemId, out folder) < 0 || folder is null) return;
            dialog.SetFolder(folder);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        finally
        {
            if (folder is not null) Marshal.ReleaseComObject(folder);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    private static string PathOf(IShellItem item)
    {
        item.GetDisplayName(0x80058000, out var name);
        try { return Marshal.PtrToStringUni(name) ?? string.Empty; }
        finally { Marshal.FreeCoTaskMem(name); }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Events(IFileDialog dialog) : IFileDialogEvents, IFileDialogControlEvents
    {
        internal string? Folder { get; private set; }
        public int OnFileOk(IFileDialog value) => 0;
        public int OnFolderChanging(IFileDialog value, IShellItem folder) => 0;
        public int OnFolderChange(IFileDialog value) => 0;
        public int OnSelectionChange(IFileDialog value) => 0;
        public int OnShareViolation(IFileDialog value, IShellItem item, out uint response) { response = 0; return 0; }
        public int OnTypeChange(IFileDialog value) => 0;
        public int OnOverwrite(IFileDialog value, IShellItem item, out uint response) { response = 0; return 0; }
        public int OnItemSelected(IFileDialogCustomize value, uint control, uint item) => 0;
        public int OnButtonClicked(IFileDialogCustomize value, uint control)
        {
            if (control != FolderButton) return 0;
            try
            {
                dialog.GetFolder(out var item);
                try { Folder = PathOf(item); }
                finally { Marshal.ReleaseComObject(item); }
                dialog.Close(0);
                return 0;
            }
            catch (COMException ex) { return ex.HResult; }
        }
        public int OnCheckButtonToggled(IFileDialogCustomize value, uint control, bool check) => 0;
        public int OnControlActivating(IFileDialogCustomize value, uint control) => 0;
    }
    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, IntPtr filters); void SetFileTypeIndex(uint index); void GetFileTypeIndex(out uint index);
        void Advise(IFileDialogEvents events, out uint cookie); void Unadvise(uint cookie);
        void SetOptions(uint options); void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder); void SetFolder(IShellItem folder); void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name); void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title); void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text); void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, uint location); void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result); void SetClientGuid(in Guid guid); void ClearClientData(); void SetFilter(IntPtr filter);
    }
    [ComImport, Guid("B4DB1657-70D7-485E-8E3E-6FCB5A5C1802"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogEvents
    {
        [PreserveSig] int OnFileOk(IFileDialog dialog);
        [PreserveSig] int OnFolderChanging(IFileDialog dialog, IShellItem folder);
        [PreserveSig] int OnFolderChange(IFileDialog dialog);
        [PreserveSig] int OnSelectionChange(IFileDialog dialog);
        [PreserveSig] int OnShareViolation(IFileDialog dialog, IShellItem item, out uint response);
        [PreserveSig] int OnTypeChange(IFileDialog dialog);
        [PreserveSig] int OnOverwrite(IFileDialog dialog, IShellItem item, out uint response);
    }
    [ComImport, Guid("36116642-D713-4B97-9B83-7484A9D00433"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogControlEvents
    {
        [PreserveSig] int OnItemSelected(IFileDialogCustomize customize, uint control, uint item);
        [PreserveSig] int OnButtonClicked(IFileDialogCustomize customize, uint control);
        [PreserveSig] int OnCheckButtonToggled(IFileDialogCustomize customize, uint control, [MarshalAs(UnmanagedType.Bool)] bool check);
        [PreserveSig] int OnControlActivating(IFileDialogCustomize customize, uint control);
    }
    [ComImport, Guid("E6FDD21A-163F-4975-9C8C-A69F1BA37034"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogCustomize
    {
        void EnableOpenDropDown(uint control);
        void AddMenu(uint control, [MarshalAs(UnmanagedType.LPWStr)] string label);
        void AddPushButton(uint control, [MarshalAs(UnmanagedType.LPWStr)] string label);
    }
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bind, in Guid handler, in Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent); void GetDisplayName(uint type, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes); void Compare(IShellItem other, uint hint, out int order);
    }
}
