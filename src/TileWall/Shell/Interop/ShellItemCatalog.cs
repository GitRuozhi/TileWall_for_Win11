using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TileWall.Shell.Interop;

/// <summary>Read-only Shell facts; no target is launched and no COM object escapes its apartment.</summary>
internal static class ShellItemCatalog
{
    internal sealed record Item(string Name, string ParsingName, string? Path, bool IsFolder);
    internal sealed record IconResourceLocation(string? Path, int Index, uint Flags, bool CanReadResource)
    {
        internal bool CanCache => (Flags & 0x10) == 0; // GIL_DONTCACHE
    }
    private static readonly Guid EnumHandler = new("94F60519-2850-4924-AA5A-D15E84868039");
    private static readonly Guid EnumId = new("70629033-E363-4A28-A567-0DB78006E6D7");

    internal static Item Read(string parsingName) => InApartment(() =>
    {
        var item = Open(parsingName);
        try { return Describe(item); }
        finally { Marshal.ReleaseComObject(item); }
    });

    internal static IReadOnlyList<Item> Enumerate(string parsingName, CancellationToken token)
    {
        var items = new List<Item>();
        _ = EnumerateBatches(parsingName, batch => items.AddRange(batch), token);
        return items;
    }

    /// <summary>Only immutable managed descriptions leave this apartment. False means the bounded scan was truncated.</summary>
    internal static bool EnumerateBatches(string parsingName, Action<IReadOnlyList<Item>> publishBatch,
        CancellationToken token) => InApartment(() =>
    {
        ArgumentNullException.ThrowIfNull(publishBatch);
        var root = Open(parsingName);
        object? raw = null;
        try
        {
            var handler = EnumHandler; var iid = EnumId;
            root.BindToHandler(IntPtr.Zero, ref handler, ref iid, out raw);
            var enumerator = (IEnumShellItems)raw;
            var batch = new List<Item>(64);
            var progress = Stopwatch.StartNew();
            var count = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var hr = enumerator.Next(1, out var item, out var fetched);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                if (hr != 0 || fetched == 0 || item is null)
                {
                    if (item is not null) Marshal.ReleaseComObject(item);
                    Publish();
                    return true;
                }
                if (count == 50000)
                {
                    Marshal.ReleaseComObject(item);
                    Publish();
                    return false;
                }
                try { batch.Add(Describe(item)); count++; }
                finally { Marshal.ReleaseComObject(item); }
                if (batch.Count >= 64 || progress.ElapsedMilliseconds >= 150) Publish();
            }

            void Publish()
            {
                if (batch.Count == 0) return;
                token.ThrowIfCancellationRequested();
                publishBatch(Array.AsReadOnly(batch.ToArray()));
                batch.Clear(); progress.Restart();
            }
        }
        finally
        {
            if (raw is not null) Marshal.ReleaseComObject(raw);
            Marshal.ReleaseComObject(root);
        }
    });

    internal static (byte[] Pixels, int Width, int Height)? Icon(string parsingName, int physicalPixels) => InApartment(() =>
    {
        var item = Open(parsingName);
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var factory = (IShellItemImageFactory)item;
            factory.GetImage(new Size(Math.Clamp(physicalPixels, 1, 4096), Math.Clamp(physicalPixels, 1, 4096)), 0x5, out bitmap);
            if (bitmap == IntPtr.Zero || Win32Api.GetObjectW(bitmap, Marshal.SizeOf<Win32Api.BitmapW>(), out var info) == 0)
                return null;
            var width = info.bmWidth; var height = Math.Abs(info.bmHeight);
            if (width is < 1 or > 4096 || height is < 1 or > 4096 || (long)width * height > 16_777_216) return null;
            var bmi = new Win32Api.BitmapInfoW { bmiHeader = Win32Api.BitmapInfoHeaderW.BgraTopDown(width, height) };
            var pixels = new byte[checked(width * height * 4)];
            var dc = Win32Api.CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return null;
            try
            {
                if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref bmi, 0) != height) return null;
            }
            finally { Win32Api.DeleteDC(dc); }
            // These pixels go directly to a premultiplied XAML bitmap. Some
            // native icon handlers return straight-alpha color through GetDIBits,
            // even after GetImage has resized it to exactly the requested size.
            // Reject a provably invalid premultiplied plane; the caller's same-item
            // HICON/AND-mask recovery preserves native pixels and alpha without
            // guessing the provider's alpha format or its resource identity.
            var visible = false;
            for (var index = 0; index < pixels.Length; index += 4)
            {
                var alpha = pixels[index + 3];
                visible |= alpha != 0;
                if (pixels[index] > alpha || pixels[index + 1] > alpha || pixels[index + 2] > alpha)
                    return null;
            }
            return visible ? ((byte[] Pixels, int Width, int Height)?)(pixels, width, height) : null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) Win32Api.DeleteObject(bitmap);
            Marshal.ReleaseComObject(item);
        }
    });

    /// <summary>Recover pixels through this item's handler without interpreting its opaque location.</summary>
    internal static (byte[] Pixels, int Width, int Height)? ExtractIcon(string parsingName, int physicalPixels,
        Func<IntPtr, (byte[] Pixels, int Width, int Height)?> readPixels) => InApartment(() =>
    {
        IShellItem? item = null;
        object? raw = null;
        IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
        try
        {
            item = Open(parsingName);
            var handler = new Guid("3981E225-F559-11D3-8E3A-00C04F6837D5"); // BHID_SFUIObject
            var iid = typeof(IExtractIconW).GUID;
            item.BindToHandler(IntPtr.Zero, ref handler, ref iid, out raw);
            var extract = (IExtractIconW)raw;
            var location = new StringBuilder(32768);
            if (extract.GetIconLocation(0, location, (uint)location.Capacity, out var index, out _) != 0 ||
                location.Length >= location.Capacity - 1) return null;
            // Retain a native large plane for the caller's final stretch rather
            // than asking the Shell to shrink it through an intermediate GDI blit.
            var pixels = physicalPixels > 48 ? 256u : (uint)Math.Clamp(physicalPixels, 1, 48);
            if (extract.Extract(location.ToString(), unchecked((uint)index), out large, out small, pixels | (32u << 16)) != 0)
                return null;
            return readPixels(large != IntPtr.Zero ? large : small);
        }
        catch (Exception ex) when (ex is COMException or IOException or ArgumentException or InvalidCastException) { return null; }
        finally
        {
            if (large != IntPtr.Zero) Win32Api.DestroyIcon(large);
            if (small != IntPtr.Zero && small != large) Win32Api.DestroyIcon(small);
            if (raw is not null) Marshal.ReleaseComObject(raw);
            if (item is not null) Marshal.ReleaseComObject(item);
        }
    });

    /// <summary>Only a handler-approved file resource may enter the PE/ICO cache.</summary>
    internal static IconResourceLocation? IconLocation(string parsingName, int physicalPixels) => InApartment(() =>
    {
        IShellItem? item = null;
        object? raw = null;
        IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
        try
        {
            item = Open(parsingName);
            var handler = new Guid("3981E225-F559-11D3-8E3A-00C04F6837D5"); // BHID_SFUIObject
            var iid = typeof(IExtractIconW).GUID;
            item.BindToHandler(IntPtr.Zero, ref handler, ref iid, out raw);
            var extract = (IExtractIconW)raw;
            var path = new StringBuilder(32768);
            var hr = extract.GetIconLocation(0, path, (uint)path.Capacity, out var index, out var flags);
            if (hr != 0 || path.Length >= path.Capacity - 1) return null;
            if ((flags & 0x18) != 0) return new IconResourceLocation(null, index, flags, false); // NOTFILENAME / DONTCACHE
            var pixels = (uint)Math.Clamp(physicalPixels, 1, 4096);
            // shlobj_core.h permits direct file extraction only on S_FALSE.
            hr = extract.Extract(path.ToString(), unchecked((uint)index), out large, out small, pixels | (32u << 16));
            var expanded = Environment.ExpandEnvironmentVariables(path.ToString().Trim().Trim('"'));
            return new IconResourceLocation(Path.IsPathFullyQualified(expanded) ? Path.GetFullPath(expanded) : null,
                index, flags, hr == 1 && Path.IsPathFullyQualified(expanded));
        }
        catch (Exception ex) when (ex is COMException or IOException or ArgumentException or InvalidCastException) { return null; }
        finally
        {
            if (large != IntPtr.Zero) Win32Api.DestroyIcon(large);
            if (small != IntPtr.Zero && small != large) Win32Api.DestroyIcon(small);
            if (raw is not null) Marshal.ReleaseComObject(raw);
            if (item is not null) Marshal.ReleaseComObject(item);
        }
    });

    /// <summary>Read a registered protocol's icon identity without navigating or launching its URI.</summary>
    internal static IconResourceLocation? ProtocolIconLocation(string scheme) => InApartment(() =>
    {
        var value = new StringBuilder(32768); uint size = (uint)value.Capacity;
        // Windows SDK shlwapi.h: ASSOCF_IS_PROTOCOL / ASSOCSTR_DEFAULTICON.
        if (AssocQueryStringW(0x1000, 15, scheme, null, value, ref size) != 0 || size >= value.Capacity) return null;
        var text = Environment.ExpandEnvironmentVariables(value.ToString().Trim());
        var comma = text.LastIndexOf(',');
        var index = 0;
        if (comma >= 0 && !int.TryParse(text[(comma + 1)..].Trim(), out index)) return null;
        var path = (comma >= 0 ? text[..comma] : text).Trim().Trim('"');
        return Path.IsPathFullyQualified(path) ? new IconResourceLocation(Path.GetFullPath(path), index, 0, true) : null;
    });

    internal static string? ProtocolAppUserModelId(string scheme) => InApartment(() =>
    {
        var value = new StringBuilder(32768); uint size = (uint)value.Capacity;
        // Windows SDK shlwapi.h: ASSOCF_IS_PROTOCOL / ASSOCSTR_APPID.
        return AssocQueryStringW(0x1000, 21, scheme, null, value, ref size) == 0 && size < value.Capacity && value.Length != 0
            ? value.ToString() : null;
    });

    // shlwapi.h ASSOCSTR: EXECUTABLE=2, DEFAULTICON=15, PROGID=20,
    // APPID=21. ASSOCF_IS_PROTOCOL resolves the current user's effective choice.
    internal static string? AssociationValue(string association, bool protocol, uint kind) => InApartment(() =>
    {
        var value = new StringBuilder(32768); uint size = (uint)value.Capacity;
        return AssocQueryStringW(protocol ? 0x1000u : 0u, kind, association, null, value, ref size) == 0 &&
            size < value.Capacity && value.Length > 0 ? value.ToString() : null;
    });

    internal static string ProtocolAssociationIdentity(string scheme) =>
        string.Join("|", scheme.ToLowerInvariant(), AssociationValue(scheme, true, 20),
            ProtocolAppUserModelId(scheme), AssociationValue(scheme, true, 2), AssociationValue(scheme, true, 15));

    private static IShellItem Open(string parsingName)
    {
        var iid = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var item));
        return item ?? throw new IOException("The native Shell item is unavailable.");
    }

    private static Item Describe(IShellItem item)
    {
        var name = Name(item, 0) ?? "";
        var parsing = Name(item, 0x80028000) ?? throw new IOException("The Shell identity is unavailable.");
        item.GetAttributes(0x20000000, out var attributes);
        return new(name.Length == 0 ? parsing : name, parsing, Name(item, 0x80058000), (attributes & 0x20000000) != 0);
    }

    private static string? Name(IShellItem item, uint kind)
    {
        IntPtr text = IntPtr.Zero;
        try { item.GetDisplayName(kind, out text); return Marshal.PtrToStringUni(text); }
        catch (Exception ex) when (ex is COMException or ArgumentException) { return null; }
        finally { if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text); }
    }

    internal static T InApartment<T>(Func<T> work)
    {
        var result = CoInitializeEx(IntPtr.Zero, 0);
        if (result < 0 && result != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(result);
        try { return work(); }
        finally { if (result >= 0) CoUninitialize(); }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Size(int Width, int Height);
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object value);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
    [ComImport, Guid("70629033-E363-4A28-A567-0DB78006E6D7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item, out uint fetched);
        void Skip(uint count); void Reset(); void Clone(out IEnumShellItems clone);
    }
    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { void GetImage(Size size, uint flags, out IntPtr bitmap); }
    [ComImport, Guid("000214FA-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IExtractIconW
    {
        [PreserveSig] int GetIconLocation(uint flags, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path,
            uint capacity, out int index, out uint iconFlags);
        [PreserveSig] int Extract([MarshalAs(UnmanagedType.LPWStr)] string path, uint index, out IntPtr large, out IntPtr small, uint sizes);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string name, IntPtr context, ref Guid iid, out IShellItem? item);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines,
        [Out] byte[] pixels, ref Win32Api.BitmapInfoW info, uint usage);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryStringW(uint flags, uint kind, string association, string? extra,
        [Out] StringBuilder output, ref uint characters);
}
