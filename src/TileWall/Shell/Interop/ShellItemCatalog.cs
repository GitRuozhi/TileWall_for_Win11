using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using TileWall.Core.Entries;
using TileWall.Core.Shell;

namespace TileWall.Shell.Interop;

/// <summary>Read-only Shell facts; no target is launched and no COM object escapes its apartment.</summary>
internal static class ShellItemCatalog
{
    internal sealed record Item(string Name, string ParsingName, string? Path, bool IsFolder);
    internal sealed record NativeLinkObservation(Item Item, LaunchIdentity Identity,
        NativeShellTargetSnapshot? Target, string? PackageFullName, string? PackageInstallPath, string? LinkedTargetPath);
    internal sealed record IconResourceLocation(string? Path, int Index, uint Flags, bool CanReadResource)
    {
        internal bool CanCache => (Flags & 0x10) == 0; // GIL_DONTCACHE
    }
    internal sealed record IconMetadataObservation(string? ParsingName, IconResourceLocation? IconLocation, string? LinkedTargetPath);
    private static readonly Guid EnumHandler = new("94F60519-2850-4924-AA5A-D15E84868039");
    private static readonly Guid EnumId = new("70629033-E363-4A28-A567-0DB78006E6D7");
    private const int NoInterface = unchecked((int)0x80004002);
    private static readonly SemaphoreSlim IconStaSlots = new(2, 2);
    private static readonly PropertyKey LinkTargetParsingPath = new(new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);

    internal static Item Read(string parsingName) => InApartment(() =>
    {
        var item = Open(parsingName);
        try { return Describe(item); }
        finally { Marshal.ReleaseComObject(item); }
    });

    /// <summary>One icon-dependency observation; only managed values leave this apartment.</summary>
    internal static IconMetadataObservation ObserveIconMetadata(string source, int physicalPixels, bool includeLinkedTarget) => InApartment(() =>
    {
        IShellItem item;
        try { item = Open(source); }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or InvalidCastException)
        {
            // A failed shared bind does not suppress independent fields that a
            // later legacy bind may still provide. Keep their individual policies.
            return ReadIconMetadata(null, source, physicalPixels, includeLinkedTarget);
        }
        try { return ReadIconMetadata(item, source, physicalPixels, includeLinkedTarget); }
        finally { Marshal.ReleaseComObject(item); }
    });

    private static IconMetadataObservation ReadIconMetadata(IShellItem? item, string source, int physicalPixels, bool includeLinkedTarget)
    {
        string? parsing;
        // Match Graphics' AppsFolderIconParsingName boundary, including Describe's
        // original success conditions. InvalidCast is deliberately not swallowed.
        try { parsing = item is null ? Read(source).ParsingName : Describe(item).ParsingName; }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { parsing = null; }
        IconResourceLocation? location;
        try
        {
            if (item is null) location = IconLocation(source, physicalPixels);
            else
            {
                try { location = ReadIconLocation(item, physicalPixels); }
                catch (Exception ex) when (ex.HResult == NoInterface)
                {
                    // Reopen only this failed field on the existing one-shot STA.
                    location = IconStaRetry<IconResourceLocation?>(() => ReadFreshIconLocation(source, physicalPixels),
                        new IconResourceLocation(null, 0, 0x10, false), null);
                }
            }
        }
        // Match the existing SourceStamp shell-policy wrapper, not a new policy.
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { location = null; }
        var linked = includeLinkedTarget ? ReadMetadataLinkedTarget(item, source) : null;
        return new(parsing, location, linked);
    }

    private static string? ReadMetadataLinkedTarget(IShellItem? item, string source)
    {
        if (item is null) return LinkedTargetPath(source);
        try { return ReadOptionalLinkedTargetPath(item, null, null); }
        catch (Exception ex) when (ex.HResult == NoInterface)
        { return IconStaRetry(() => ReadFreshLinkedTargetPath(source, null), (string?)null, null); }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidCastException)
        { return null; }
    }

    /// <summary>Read an explicit link target for icon fallback, never a source path or launch identity.</summary>
    internal static string? LinkedTargetPath(string parsingName) => LinkedTargetPathTraced(parsingName, null);

    internal static string? LinkedTargetPathTraced(string parsingName, SourceReadTrace? trace) =>
        IconApartment(() => ReadFreshLinkedTargetPath(parsingName, trace), (string?)null, trace);

    private static string? ReadFreshLinkedTargetPath(string parsingName, SourceReadTrace? trace)
    {
        IShellItem? item = null;
        try
        {
            using (trace?.Measure("linked-target.open")) item = Open(parsingName);
            return ReadOptionalLinkedTargetPath(item, null, trace);
        }
        catch (Exception ex) when (ex.HResult != NoInterface &&
            (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidCastException)) { return null; }
        finally { if (item is not null) Marshal.ReleaseComObject(item); }
    }

    private static string? ReadOptionalLinkedTargetPath(IShellItem item, IPropertyStore? properties, SourceReadTrace? trace)
    {
        IShellItem? linked = null;
        try
        {
            var direct = properties is null ? ReadLinkedTargetPath(item, trace) :
                FullLinkedTargetPath(ReadLinkedTargetParsingProperty(properties, trace));
            if (direct is not null) return direct;
            // A managed Shell LNK's own property is its AppsFolder identity.
            // Read the linked Shell item's property once to retain that context.
            var handler = new Guid("3981E228-F559-11D3-8E3A-00C04F6837D5"); // BHID_LinkTargetItem
            var iid = typeof(IShellItem).GUID;
            using (trace?.Measure("linked-target.bind-target"))
                ((IShellItemLinkTarget)item).BindToHandler(IntPtr.Zero, ref handler, ref iid, out linked);
            return ReadLinkedTargetPath(linked, trace);
        }
        catch (Exception ex) when (ex.HResult != NoInterface &&
            (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidCastException)) { return null; }
        finally
        {
            if (linked is not null) Marshal.ReleaseComObject(linked);
        }
    }

    /// <summary>The Shell's target description from the actual managed link, including an AppsFolder AUMID.</summary>
    internal static string? LinkTargetParsingName(string linkPath) => IconApartment<string?>(() =>
    {
        IShellItem? item = null;
        try { item = Open(linkPath); return ReadLinkedTargetParsingName(item); }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidCastException)
        { return null; }
        finally { if (item is not null) Marshal.ReleaseComObject(item); }
    }, null);

    private static string? ReadLinkedTargetPath(IShellItem item, SourceReadTrace? trace = null) =>
        FullLinkedTargetPath(ReadLinkedTargetParsingName(item, trace));

    private static string? FullLinkedTargetPath(string? path) =>
        path is not null && Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;

    private static string? ReadLinkedTargetParsingName(IShellItem item, SourceReadTrace? trace = null)
    {
        IPropertyStore? store = null;
        try
        {
            using (trace?.Measure("linked-target.bind-properties")) store = BindPropertyStore(item);
            return ReadLinkedTargetParsingProperty(store, trace);
        }
        finally { if (store is not null) Marshal.ReleaseComObject(store); }
    }

    private static string? ReadLinkedTargetParsingProperty(IPropertyStore store, SourceReadTrace? trace)
    {
        var value = new PropertyVariant();
        try
        {
            int hr;
            using (trace?.Measure("linked-target.read-property")) hr = store.GetValue(in LinkTargetParsingPath, out value);
            Marshal.ThrowExceptionForHR(hr);
            if (hr != 0 || value.Pointer == IntPtr.Zero) return null;
            var path = value.Type switch
            {
                31 => Marshal.PtrToStringUni(value.Pointer), // VT_LPWSTR
                8 => Marshal.PtrToStringBSTR(value.Pointer), // VT_BSTR
                _ => null,
            };
            return path is { Length: > 0 and < 32768 } && !path.Contains('\0') ? path : null;
        }
        finally { Marshal.ThrowExceptionForHR(PropVariantClear(ref value)); }
    }

    /// <summary>Observe the identity that a standard PIDL Shell link will persist.</summary>
    internal static (Item Item, LaunchIdentity Identity) ReadForLink(string parsingName)
    {
        var observed = ReadNativeLink(parsingName, observeRegistration: false);
        return (observed.Item, observed.Identity);
    }

    internal static NativeLinkObservation ReadNativeLink(string parsingName, bool observeRegistration = true) =>
        ReadNativeLinkTraced(parsingName, observeRegistration, false, null);

    internal static NativeLinkObservation ReadNativeLinkTraced(string parsingName, bool observeRegistration,
        bool observeLinkedTarget, SourceReadTrace? trace) => InApartment(() =>
    {
        IShellItem item;
        using (trace?.Measure("native-link.open")) item = Open(parsingName);
        IntPtr pidl = IntPtr.Zero;
        IntPtr linkPidl = IntPtr.Zero;
        IShellLinkPath? link = null;
        IPropertyStore? properties = null;
        try
        {
            var actual = Describe(item, trace);
            // A virtual Shell item can be represented by a filesystem path in
            // IShellLink.GetPath even when SIGDN_FILESYSPATH rejects it (Libraries).
            // Use the same PIDL/GetPath contract as the eventual managed LNK.
            using (trace?.Measure("native-link.get-pidl")) Marshal.ThrowExceptionForHR(SHGetIDListFromObject(item, out pidl));
            if (pidl == IntPtr.Zero) throw new IOException("The Shell link target is unavailable.");
            using (trace?.Measure("native-link.create-link")) link = (IShellLinkPath)(object)new ShellLinkPath();
            using (trace?.Measure("native-link.set-pidl")) link.SetIDList(pidl);
            var path = new StringBuilder(32768);
            using (trace?.Measure("native-link.get-path")) link.GetPath(path, path.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH
            if (path.Length == path.Capacity - 1) throw new IOException("The Shell link target path is incomplete.");
            LaunchIdentity identity;
            NativeShellTargetSnapshot? target = null;
            if (path.Length != 0) identity = LaunchIdentityService.ForPath(path.ToString());
            else
            {
                // GetIDList returns the validated PIDL owned by the Shell link object.
                IntPtr storedPidl;
                using (trace?.Measure("native-link.get-stored-pidl")) link.GetIDList(out storedPidl);
                if (storedPidl == IntPtr.Zero) throw new IOException("The Shell link target IDList is unavailable.");
                linkPidl = storedPidl;
                var hash = ShellLinkFileService.IdListHash(linkPidl);
                identity = LaunchIdentityService.ForShellIdList(hash, actual.ParsingName);
            }
            var registration = (FullName: (string?)null, InstallPath: (string?)null);
            if (observeRegistration)
            {
                using (trace?.Measure("registration.bind-properties")) properties = BindPropertyStore(item);
                registration = ReadPackageRegistrationValues(properties, trace);
            }
            string? linkedTarget = null;
            if (observeLinkedTarget)
            {
                try { using (trace?.Measure("facts.linked-target")) linkedTarget = ReadOptionalLinkedTargetPath(item, properties, trace); }
                catch (Exception ex) when (ex.HResult == NoInterface)
                {
                    // Retry only the optional property on its own newly opened STA
                    // item. Neither the source RCW nor its store crosses apartments.
                    linkedTarget = IconStaRetry(() => ReadFreshLinkedTargetPath(parsingName, trace), (string?)null, trace);
                }
            }
            if (observeRegistration && linkPidl != IntPtr.Zero)
            {
                NativeShellTargetSnapshot? frozen = null;
                frozen = NativeShellTargetSnapshot.Capture(linkPidl, identity, parsingName, () =>
                {
                    // Store preparation is a separate observation boundary as well.
                    // The captured delegate contains managed facts, never an RCW.
                    var current = ReadNativeLink(parsingName);
                    if (current.Target is null || current.PackageFullName != registration.FullName ||
                        current.PackageInstallPath != registration.InstallPath || !SameNativeTarget(frozen!, current.Target))
                        throw new IOException("The native Shell source changed during preparation.");
                });
                target = frozen;
            }
            return new NativeLinkObservation(actual, identity, target, registration.FullName, registration.InstallPath, linkedTarget);
        }
        finally
        {
            if (link is not null) using (trace?.Measure("native-link.release-link")) Marshal.ReleaseComObject(link);
            if (properties is not null) using (trace?.Measure("native-link.release-properties")) Marshal.ReleaseComObject(properties);
            if (linkPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(linkPidl);
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            using (trace?.Measure("native-link.release-item")) Marshal.ReleaseComObject(item);
        }
    });

    /// <summary>Only process-local PIDLs freshly returned by Windows may enter this namespace comparison.</summary>
    internal static bool SameNativeTarget(NativeShellTargetSnapshot expected, NativeShellTargetSnapshot current)
    {
        using var trace = new SourceReadTrace("NativeCanonical");
        return SameNativeTargetTraced(expected, current, trace);
    }

    internal static bool SameNativeTargetTraced(NativeShellTargetSnapshot expected, NativeShellTargetSnapshot current,
        SourceReadTrace? trace) => InApartment(() =>
    {
        trace?.Mark("canonical-enter", $"apartment={Thread.CurrentThread.GetApartmentState()} samePidl={expected.IdListSha256 == current.IdListSha256}");
        if (expected.Identity.Arguments != current.Identity.Arguments ||
            !string.Equals(expected.Identity.WorkingDirectory, current.Identity.WorkingDirectory, StringComparison.OrdinalIgnoreCase)) return false;
        if (expected.IdListSha256 == current.IdListSha256) return true;
        var left = expected.CopyIdList(); var right = current.CopyIdList();
        var a = Marshal.AllocCoTaskMem(left.Length); var b = IntPtr.Zero;
        IShellItem? x = null, y = null;
        try
        {
            b = Marshal.AllocCoTaskMem(right.Length);
            Marshal.Copy(left, 0, a, left.Length); Marshal.Copy(right, 0, b, right.Length);
            var iid = typeof(IShellItem).GUID;
            using (trace?.Measure("canonical.create-left")) Marshal.ThrowExceptionForHR(SHCreateItemFromIDList(a, ref iid, out x));
            using (trace?.Measure("canonical.create-right")) Marshal.ThrowExceptionForHR(SHCreateItemFromIDList(b, ref iid, out y));
            if (x is null || y is null) throw new IOException("The native Shell snapshot is unavailable.");
            int result, order;
            using (trace?.Measure("canonical.compare")) result = x.Compare(y, 0x10000000, out order); // SICHINT_CANONICAL; excludes presentation metadata.
            Marshal.ThrowExceptionForHR(result);
            return result == 0 && order == 0;
        }
        finally
        {
            if (x is not null) using (trace?.Measure("canonical.release-left")) Marshal.ReleaseComObject(x);
            if (y is not null) using (trace?.Measure("canonical.release-right")) Marshal.ReleaseComObject(y);
            Marshal.FreeCoTaskMem(a); Marshal.FreeCoTaskMem(b);
        }
    });

    private static (string? FullName, string? InstallPath) ReadPackageRegistration(IShellItem item) =>
        ReadPackageRegistrationTraced(item, null);

    private static (string? FullName, string? InstallPath) ReadPackageRegistrationTraced(IShellItem item, SourceReadTrace? trace)
    {
        IPropertyStore? store = null;
        try
        {
            using (trace?.Measure("registration.bind-properties")) store = BindPropertyStore(item);
            return ReadPackageRegistrationValues(store, trace);
        }
        finally { if (store is not null) Marshal.ReleaseComObject(store); }
    }

    private static (string? FullName, string? InstallPath) ReadPackageRegistrationValues(IPropertyStore store, SourceReadTrace? trace)
    {
        var fullName = Read("System.AppUserModel.PackageFullName");
        var installPath = Read("System.AppUserModel.PackageInstallPath");
        if ((fullName is null) != (installPath is null)) throw new IOException("The native package registration is incomplete.");
        return (fullName, installPath);

        string? Read(string name)
        {
            Marshal.ThrowExceptionForHR(PSGetPropertyKeyFromName(name, out var key));
            var value = new PropertyVariant();
            try
            {
                int result;
                using (trace?.Measure("registration.read:" + name)) result = store.GetValue(in key, out value);
                Marshal.ThrowExceptionForHR(result);
                if (result != 0) throw new IOException("The native package registration was not returned completely.");
                var text = value.Type switch
                {
                    0 => null,
                    31 when value.Pointer != IntPtr.Zero => Marshal.PtrToStringUni(value.Pointer),
                    8 when value.Pointer != IntPtr.Zero => Marshal.PtrToStringBSTR(value.Pointer),
                    _ => throw new IOException("The native package registration is not a complete string."),
                };
                if (text is not null && (text.Length > 32768 || text.Contains('\0')))
                    throw new IOException("The native package registration is invalid.");
                return string.IsNullOrEmpty(text) ? null : text;
            }
            finally { Marshal.ThrowExceptionForHR(PropVariantClear(ref value)); }
        }
    }

    private static IPropertyStore BindPropertyStore(IShellItem item)
    {
        var iid = typeof(IPropertyStore).GUID;
        // BHID_PropertyStore is unsupported by some valid namespace items (for
        // example, Recycle Bin). The Shell's recommended item property-store
        // entry point supplies their complete read-only default store as well.
        // Do not use FASTPROPERTIESONLY or BESTEFFORT: missing registration
        // values must come from successful GetValue calls, never omitted work.
        ((IShellItem2PropertyStore)item).GetPropertyStore(0, ref iid, out var store); // GPS_DEFAULT
        return store ?? throw new IOException("The Shell property store is unavailable.");
    }

    internal static IReadOnlyList<Item> Enumerate(string parsingName, CancellationToken token)
    {
        var items = new List<Item>();
        _ = EnumerateBatches(parsingName, batch => items.AddRange(batch), token);
        return items;
    }

    /// <summary>Only immutable managed descriptions leave this apartment. False means the bounded scan was truncated.</summary>
    internal static bool EnumerateBatches(string parsingName, Action<IReadOnlyList<Item>> publishBatch,
        CancellationToken token) => EnumerateBatchesTraced(parsingName, publishBatch, token, null);

    internal static bool EnumerateBatchesTraced(string parsingName, Action<IReadOnlyList<Item>> publishBatch,
        CancellationToken token, SourceReadTrace? trace) => InApartment(() =>
    {
        ArgumentNullException.ThrowIfNull(publishBatch);
        IShellItem root;
        using (trace?.Measure("enumeration.open", untilFirstBatch: true)) root = Open(parsingName);
        object? raw = null;
        try
        {
            var handler = EnumHandler; var iid = EnumId;
            using (trace?.Measure("enumeration.bind", untilFirstBatch: true)) root.BindToHandler(IntPtr.Zero, ref handler, ref iid, out raw);
            var enumerator = (IEnumShellItems)raw;
            var batch = new List<Item>(64);
            var progress = Stopwatch.StartNew();
            var count = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int hr;
                IShellItem? item;
                uint fetched;
                using (trace?.Measure("enumeration.next", untilFirstBatch: true)) hr = enumerator.Next(1, out item, out fetched);
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
                try
                {
                    using (trace?.Measure("enumeration.describe", untilFirstBatch: true)) batch.Add(Describe(item, trace));
                    count++;
                }
                finally { Marshal.ReleaseComObject(item); }
                if (batch.Count >= 64 || progress.ElapsedMilliseconds >= 150) Publish();
            }

            void Publish()
            {
                if (batch.Count == 0) return;
                token.ThrowIfCancellationRequested();
                trace?.BatchReady(batch.Count);
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
            var requestedPixels = Math.Max(physicalPixels, 1);
            factory.GetImage(new Size(requestedPixels, requestedPixels), 0x5, out bitmap);
            if (bitmap == IntPtr.Zero || Win32Api.GetObjectW(bitmap, Marshal.SizeOf<Win32Api.BitmapW>(), out var info) == 0)
                return null;
            var width = info.bmWidth;
            var heightLong = Math.Abs((long)info.bmHeight);
            if (width < 1 || heightLong < 1 || heightLong > int.MaxValue) return null;
            var height = (int)heightLong;
            var bmi = new Win32Api.BitmapInfoW { bmiHeader = Win32Api.BitmapInfoHeaderW.BgraTopDown(width, height) };
            int pixelBytes;
            try { pixelBytes = checked(width * height * 4); }
            catch (OverflowException) { return null; }
            var pixels = new byte[pixelBytes];
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
        Func<IntPtr, (byte[] Pixels, int Width, int Height)?> readPixels) => IconApartment<(byte[] Pixels, int Width, int Height)?>(() =>
    {
        IShellItem? item = null;
        object? raw = null;
        IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
        try
        {
            if (!TryPackExtractIconSize(Math.Max(physicalPixels, 1), out var packedSize)) return null;
            item = Open(parsingName);
            var handler = new Guid("3981E225-F559-11D3-8E3A-00C04F6837D5"); // BHID_SFUIObject
            var iid = typeof(IExtractIconW).GUID;
            item.BindToHandler(IntPtr.Zero, ref handler, ref iid, out raw);
            var extract = (IExtractIconW)raw;
            var location = new StringBuilder(32768);
            var hr = extract.GetIconLocation(0, location, (uint)location.Capacity, out var index, out _);
            if (hr == NoInterface) Marshal.ThrowExceptionForHR(hr);
            if (hr != 0 ||
                location.Length >= location.Capacity - 1) return null;
            // Retain the requested native plane for the caller's final stretch
            // rather than asking the Shell to shrink it through an intermediate GDI blit.
            hr = extract.Extract(location.ToString(), unchecked((uint)index), out large, out small, packedSize);
            if (hr == NoInterface) Marshal.ThrowExceptionForHR(hr);
            if (hr != 0)
                return null;
            return readPixels(large != IntPtr.Zero ? large : small);
        }
        catch (Exception ex) when (ex.HResult != NoInterface && (ex is COMException or IOException or ArgumentException or InvalidCastException)) { return null; }
        finally
        {
            if (large != IntPtr.Zero) Win32Api.DestroyIcon(large);
            if (small != IntPtr.Zero && small != large) Win32Api.DestroyIcon(small);
            if (raw is not null) Marshal.ReleaseComObject(raw);
            if (item is not null) Marshal.ReleaseComObject(item);
        }
    }, null);

    /// <summary>Only a handler-approved file resource may enter the PE/ICO cache.</summary>
    internal static IconResourceLocation? IconLocation(string parsingName, int physicalPixels) =>
        IconApartment<IconResourceLocation?>(() => ReadFreshIconLocation(parsingName, physicalPixels), new IconResourceLocation(null, 0, 0x10, false));

    private static IconResourceLocation? ReadFreshIconLocation(string parsingName, int physicalPixels)
    {
        IShellItem? item = null;
        try
        {
            try { item = Open(parsingName); }
            catch (Exception ex) when (ex.HResult != NoInterface && (ex is COMException or IOException or ArgumentException or InvalidCastException))
            { return null; }
            return ReadIconLocation(item, physicalPixels);
        }
        finally { if (item is not null) Marshal.ReleaseComObject(item); }
    }

    private static IconResourceLocation? ReadIconLocation(IShellItem item, int physicalPixels)
    {
        object? raw = null;
        IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
        try
        {
            var handler = new Guid("3981E225-F559-11D3-8E3A-00C04F6837D5"); // BHID_SFUIObject
            var iid = typeof(IExtractIconW).GUID;
            item.BindToHandler(IntPtr.Zero, ref handler, ref iid, out raw);
            var extract = (IExtractIconW)raw;
            var path = new StringBuilder(32768);
            var hr = extract.GetIconLocation(0, path, (uint)path.Capacity, out var index, out var flags);
            if (hr == NoInterface) Marshal.ThrowExceptionForHR(hr);
            if (hr != 0 || path.Length >= path.Capacity - 1) return null;
            if ((flags & 0x18) != 0) return new IconResourceLocation(null, index, flags, false); // NOTFILENAME / DONTCACHE
            if (!TryPackExtractIconSize(Math.Max(physicalPixels, 1), out var packedSize))
                return new IconResourceLocation(null, index, flags, false);
            // shlobj_core.h permits direct file extraction only on S_FALSE.
            hr = extract.Extract(path.ToString(), unchecked((uint)index), out large, out small, packedSize);
            if (hr == NoInterface) Marshal.ThrowExceptionForHR(hr);
            var expanded = Environment.ExpandEnvironmentVariables(path.ToString().Trim().Trim('"'));
            return new IconResourceLocation(Path.IsPathFullyQualified(expanded) ? Path.GetFullPath(expanded) : null,
                index, flags, hr == 1 && Path.IsPathFullyQualified(expanded));
        }
        catch (Exception ex) when (ex.HResult != NoInterface && (ex is COMException or IOException or ArgumentException or InvalidCastException)) { return null; }
        finally
        {
            if (large != IntPtr.Zero) Win32Api.DestroyIcon(large);
            if (small != IntPtr.Zero && small != large) Win32Api.DestroyIcon(small);
            if (raw is not null) Marshal.ReleaseComObject(raw);
        }
    }

    private static T IconApartment<T>(Func<T> work, T unavailable, SourceReadTrace? trace = null)
    {
        try { using (trace?.Measure("linked-target.mta")) return InApartment(work); }
        catch (Exception ex) when (ex.HResult == NoInterface)
        { return IconStaRetry(work, unavailable, trace); }
    }

    private static T IconStaRetry<T>(Func<T> work, T unavailable, SourceReadTrace? trace)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA || !IconStaSlots.Wait(0))
            return unavailable;
        // Some registered instance handlers expose properties or icons only from
        // an STA. Retry this one call, without moving scans or persistence
        // onto an apartment service or retaining COM objects across threads.
        T result = unavailable;
        ExceptionDispatchInfo? failure = null;
        trace?.Mark("linked-target.sta-retry");
        var thread = new Thread(() =>
        {
            var initialized = CoInitializeEx(IntPtr.Zero, 2); // COINIT_APARTMENTTHREADED
            try
            {
                Marshal.ThrowExceptionForHR(initialized);
                using (trace?.Measure("linked-target.sta-work")) result = work();
            }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
            finally
            {
                if (initialized >= 0) CoUninitialize();
                IconStaSlots.Release();
            }
        }) { IsBackground = true, Name = "TileWall icon STA retry" };
        try { thread.SetApartmentState(ApartmentState.STA); thread.Start(); }
        catch { IconStaSlots.Release(); throw; }
        // The slot count bounds concurrency, not time. The owning source or
        // image request still drains this native call through completion.
        using (trace?.Measure("linked-target.sta-join")) thread.Join();
        if (failure?.SourceException.HResult == NoInterface) return unavailable;
        failure?.Throw();
        return result;
    }

    private static bool TryPackExtractIconSize(int physicalPixels, out uint packedSize)
    {
        if (physicalPixels is < 1 or > ushort.MaxValue)
        {
            packedSize = 0;
            return false;
        }
        // IExtractIcon::Extract places the desired pixel edge in the low word
        // and the requested 32bpp depth in the high word.
        packedSize = (uint)physicalPixels | (32u << 16);
        return true;
    }

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

    private static Item Describe(IShellItem item, SourceReadTrace? trace = null)
    {
        var name = Name(item, 0, trace) ?? "";
        var parsing = Name(item, 0x80028000, trace) ?? throw new IOException("The Shell identity is unavailable.");
        uint attributes;
        using (trace?.Measure("describe.attributes", untilFirstBatch: true)) item.GetAttributes(0x20000000, out attributes);
        return new(name.Length == 0 ? parsing : name, parsing, Name(item, 0x80058000, trace), (attributes & 0x20000000) != 0);
    }

    private static string? Name(IShellItem item, uint kind, SourceReadTrace? trace = null)
    {
        IntPtr text = IntPtr.Zero;
        try
        {
            using (trace?.Measure("describe.name:" + kind.ToString("X"), untilFirstBatch: true)) item.GetDisplayName(kind, out text);
            return Marshal.PtrToStringUni(text);
        }
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

    // One requested source read owns these counters. No timer, native hook or global tracing service is installed.
    internal sealed class SourceReadTrace : IDisposable
    {
        private static long _nextId;
        private readonly bool _enabled = Environment.GetEnvironmentVariable("TILEWALL_PERF") == "1";
        private readonly long _id = Interlocked.Increment(ref _nextId);
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly object _sync = new();
        private readonly Dictionary<string, (int Count, double Total, double Maximum)> _stages = [];
        private bool _firstBatch, _firstFact;
        private long _callId;

        internal SourceReadTrace(string source) { if (_enabled) Write("begin", source); }

        internal IDisposable? Measure(string stage, string? sourceKey = null, bool untilFirstBatch = false)
        {
            if (!_enabled) return null;
            var call = Interlocked.Increment(ref _callId);
            bool entered;
            lock (_sync) entered = !_firstFact && (!untilFirstBatch || !_firstBatch);
            if (entered) Write("enter", $"call={call} stage={stage} key={sourceKey}");
            return new Measurement(this, stage, sourceKey, call, entered);
        }

        internal void BatchReady(int count)
        {
            if (!_enabled) return;
            lock (_sync) _firstBatch = true;
            Write("batch-ready", "count=" + count);
        }

        internal void FirstFact()
        {
            if (!_enabled) return;
            lock (_sync) _firstFact = true;
            Write("first-fact", string.Empty);
        }

        internal void Mark(string stage, string? detail = null) { if (_enabled) Write(stage, detail ?? string.Empty); }

        internal void Count(string stage)
        {
            if (!_enabled) return;
            lock (_sync)
            {
                var prior = _stages.GetValueOrDefault(stage);
                _stages[stage] = (prior.Count + 1, prior.Total, prior.Maximum);
            }
        }

        private void Write(string stage, string detail) => AppLog.Warn("ImportSourcesPerf",
            $"scan={_id} elapsedMs={Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F2} event={stage} {detail}");

        public void Dispose()
        {
            if (!_enabled) return;
            lock (_sync)
                foreach (var (stage, value) in _stages)
                    Write("summary", $"stage={stage} count={value.Count} totalMs={value.Total:F2} maxMs={value.Maximum:F2}");
            Write("end", string.Empty);
        }

        private sealed class Measurement(SourceReadTrace owner, string stage, string? sourceKey, long call, bool entered) : IDisposable
        {
            private readonly long _started = Stopwatch.GetTimestamp();
            public void Dispose()
            {
                var elapsed = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
                lock (owner._sync)
                {
                    var prior = owner._stages.GetValueOrDefault(stage);
                    owner._stages[stage] = (prior.Count + 1, prior.Total + elapsed, Math.Max(prior.Maximum, elapsed));
                }
                if (entered || elapsed >= 100) owner.Write("leave", $"call={call} stage={stage} key={sourceKey} durationMs={elapsed:F2}");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Size(int Width, int Height);
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object value);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }
    // Flatten the inherited IShellItem methods before IShellItem2's first method.
    [ComImport, Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2PropertyStore
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object value);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
        void GetPropertyStore(uint flags, ref Guid iid, out IPropertyStore store);
    }
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemLinkTarget
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IShellItem item);
    }
    [ComImport, Guid("70629033-E363-4A28-A567-0DB78006E6D7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item, out uint fetched);
        void Skip(uint count); void Reset(); void Clone(out IEnumShellItems clone);
    }
    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { void GetImage(Size size, uint flags, out IntPtr bitmap); }
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLinkPath { }
    // Windows SDK ShObjIdl_core.h: first three IShellLinkW methods, in vtable order.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkPath
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }
    // PROPVARIANT's union starts at byte 8, occupying two native pointer slots.
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyVariant
    {
        public ushort Type, Reserved1, Reserved2, Reserved3;
        public IntPtr Pointer, UnionRemainder;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(in PropertyKey key, out PropertyVariant value);
        void SetValue(in PropertyKey key, in PropertyVariant value);
        void Commit();
    }
    [ComImport, Guid("000214FA-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IExtractIconW
    {
        [PreserveSig] int GetIconLocation(uint flags, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path,
            uint capacity, out int index, out uint iconFlags);
        [PreserveSig] int Extract([MarshalAs(UnmanagedType.LPWStr)] string path, uint index, out IntPtr large, out IntPtr small, uint sizes);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string name, IntPtr context, ref Guid iid, out IShellItem? item);
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHCreateItemFromIDList(IntPtr pidl, ref Guid iid, out IShellItem? item);
    [DllImport("propsys.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int PSGetPropertyKeyFromName(string name, out PropertyKey key);
    [DllImport("shell32.dll")]
    private static extern int SHGetIDListFromObject([MarshalAs(UnmanagedType.IUnknown)] object unknown, out IntPtr pidl);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropertyVariant value);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines,
        [Out] byte[] pixels, ref Win32Api.BitmapInfoW info, uint usage);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryStringW(uint flags, uint kind, string association, string? extra,
        [Out] StringBuilder output, ref uint characters);
}
