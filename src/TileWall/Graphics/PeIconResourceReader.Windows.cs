using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TileWall.Graphics;

public sealed partial class PeIconResourceReader
{
    // Resource loading lets Windows resolve the actual .mun/.mui association.
    // It never runs the module, invents a companion filename, or resizes a frame.
    private sealed record WindowsResources(IReadOnlyList<PeIconGroup> Groups, string Stamp);
    private readonly record struct ResourceMapping(nuint Start, nuint Length, string Path);

    /// <summary>Fresh physical resource/locale facts, including Windows-resolved backing files.</summary>
    internal static string ResourceDependencyStamp(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "resource:none";
        var baseFacts = "base:unavailable:" + source;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(source);
            if (!Path.IsPathFullyQualified(expanded)) return "resource:non-file:" + source;
            var path = Path.GetFullPath(expanded);
            var info = new FileInfo(path);
            if (!info.Exists) return "resource:missing:" + path;
            // Keep independently observable original-file facts even when Windows cannot
            // map a redirected resource back to a confirmed DOS/UNC path.
            baseFacts = $"base:{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            if (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase) || !OperatingSystem.IsWindows())
                return baseFacts + "|" + ResourceFileStamp(path);
            return baseFacts + "|" + ReadWindowsResourceDirectory(path, includeFrames: false).Stamp;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return baseFacts + "|resource:unavailable:" + source + ":" + ex.GetType().Name; }
    }

    private static PeIconReadResult ReadWindowsResources(string path, FileInfo info)
    {
        try
        {
            var before = ReadWindowsResourceDirectory(path, includeFrames: false).Stamp;
            var native = ReadWindowsResourceDirectory(path, includeFrames: true);
            if (before != native.Stamp || before != ReadWindowsResourceDirectory(path, includeFrames: false).Stamp)
                return new(PeIconReadStatus.InvalidResource, null, "ResourceChanged");
            return native.Groups.Any(group => group.Frames.Count != 0)
                ? new(PeIconReadStatus.Success, new PeIconCatalog(path, info.Length, info.LastWriteTimeUtc, native.Groups))
                : new(PeIconReadStatus.NoIconGroups, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(PeIconReadStatus.InvalidResource, null, ex.GetType().Name); }
    }

    private static WindowsResources ReadWindowsResourceDirectory(string path, bool includeFrames)
    {
        var files = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { [path] = ResourceFileStamp(path) };
        var groups = new List<PeIconGroup>();
        var identities = new List<string>();
        // These facts belong only to this one directory read. A new capture/check
        // gets fresh mappings, ancestry checks and metadata, including after awaits.
        var mappedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mappings = new List<ResourceMapping>();
        var mappedPath = new StringBuilder(260);
        List<(string Device, string Drive)>? drives = null;
        var module = LoadLibraryExW(path, IntPtr.Zero, 0x22); // AS_DATAFILE | AS_IMAGE_RESOURCE
        if (module == IntPtr.Zero) return Result("load=" + Marshal.GetLastWin32Error());
        Exception? failure = null;
        try
        {
            ResourceNameCallback names = (owner, type, name, _) =>
            {
                try
                {
                    var numeric = (unchecked((ulong)name.ToInt64()) >> 16) == 0;
                    var id = numeric ? (uint)name.ToInt64() : 0;
                    var text = numeric ? null : Marshal.PtrToStringUni(name);
                    var languages = new HashSet<ushort>();
                    ResourceLanguageCallback language = (_, _, _, lang, _) => { languages.Add(lang); return true; };
                    // Zero flags search LN and Windows-associated MUI resources, retaining languages separately.
                    if (!EnumResourceLanguagesExW(owner, type, name, language, IntPtr.Zero, 0, 0))
                        throw new IOException("Cannot enumerate icon resource languages: " + Marshal.GetLastWin32Error());
                    GC.KeepAlive(language);
                    foreach (var lang in languages.Order())
                    {
                        identities.Add($"group={id}:{text}:language={lang}");
                        var bytes = Resource(owner, type, name, lang, true);
                        var frames = new List<PeIconFrame>();
                        if (bytes.Length >= 6 && BinaryPrimitives.ReadUInt16LittleEndian(bytes) == 0 &&
                            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)) == 1)
                        {
                            var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
                            if (count > 0 && 6L + count * 14L <= bytes.Length)
                                for (var index = 0; index < count; index++)
                                {
                                    var offset = 6 + index * 14;
                                    var width = bytes[offset] == 0 ? 256 : bytes[offset];
                                    var height = bytes[offset + 1] == 0 ? 256 : bytes[offset + 1];
                                    var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 8));
                                    var frameId = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 12));
                                    // Resolving each declared RT_ICON also identifies any independently backed file.
                                    var raw = Resource(owner, new IntPtr(3), new IntPtr(frameId), lang, includeFrames);
                                    identities.Add($"frame={frameId}:{width}x{height}:{length}");
                                    if (!includeFrames || raw.LongLength != length || raw.Length == 0) continue;
                                    var format = DetectFormat(raw, width, height);
                                    if (format == PeIconFrameFormat.Unknown) continue;
                                    frames.Add(new(frameId, lang, width, height,
                                        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 6)), bytes[offset + 2],
                                        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4)), format,
                                        raw, Convert.ToHexString(SHA256.HashData(raw))));
                                }
                        }
                        // Empty/invalid groups retain their original ordinal, just like direct PE parsing.
                        groups.Add(new(id, text, lang, frames));
                    }
                    return true;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { failure = ex; return false; }
            };
            var complete = EnumResourceNamesW(module, new IntPtr(14), names, IntPtr.Zero);
            var error = complete ? 0 : Marshal.GetLastWin32Error();
            GC.KeepAlive(names);
            if (failure is not null) throw new IOException("Cannot read native icon resources.", failure);
            if (!complete && error is not (1812 or 1813 or 1814 or 1815))
                throw new IOException("Cannot enumerate native icon resources: " + error);
            return Result("enumeration=" + error);
        }
        finally { _ = FreeLibrary(module); }

        byte[] Resource(IntPtr owner, IntPtr type, IntPtr name, ushort language, bool copy)
        {
            var handle = FindResourceExW(owner, type, name, language);
            if (handle == IntPtr.Zero) return [];
            var length = SizeofResource(owner, handle);
            var address = LockResource(LoadResource(owner, handle));
            if (address == IntPtr.Zero || length == 0 || length > int.MaxValue)
                throw new IOException("Invalid native icon resource data.");
            var actual = MappedResourcePath(address, mappedPath, mappedFiles, mappings, ref drives);
            // Resource copies use the same mapped module. Guard each actual file on
            // first observation and recheck it after the complete read, not per frame.
            if (!files.ContainsKey(actual)) files[actual] = ResourceFileStamp(actual);
            if (!copy) return [];
            var result = new byte[checked((int)length)];
            Marshal.Copy(address, result, 0, result.Length);
            return result;
        }
        WindowsResources Result(string status)
        {
            foreach (var pair in files)
                if (pair.Value != ResourceFileStamp(pair.Key)) throw new IOException("Resource backing file changed.");
            return new(groups, "windows-resource-v1|" + CultureInfo.CurrentUICulture.Name + "|" + status + "|" +
                string.Join("|", identities) + "|" + string.Join("|", files.Values));
        }
    }

    private static string ResourceFileStamp(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("Resource path is not absolute.");
        var full = Path.GetFullPath(path);
        if (full[Path.GetPathRoot(full)!.Length..].Contains(':')) throw new IOException("Resource stream path is not allowed.");
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current)))
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || current != full && (attributes & FileAttributes.Directory) == 0)
                throw new IOException("Unsafe resource file ancestry.");
            if (Path.TrimEndingDirectorySeparator(current) == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!)) break;
        }
        var info = new FileInfo(full);
        if (!info.Exists) throw new IOException("Resource file is missing.");
        return $"{full}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    private static string MappedResourcePath(IntPtr address, StringBuilder path,
        Dictionary<string, string> mappedFiles, List<ResourceMapping> mappings,
        ref List<(string Device, string Drive)>? drives)
    {
        var pointer = (nuint)address;
        foreach (var mapping in mappings)
            if (pointer >= mapping.Start && pointer - mapping.Start < mapping.Length) return mapping.Path;
        if (VirtualQuery(address, out var region, (nuint)Marshal.SizeOf<ResourceMemoryInformation>()) == 0 || region.RegionSize == 0)
            throw new IOException("Resource memory mapping is unavailable.");
        while (true)
        {
            path.Clear();
            var length = K32GetMappedFileNameW(GetCurrentProcess(), address, path, (uint)path.Capacity);
            if (length == 0) throw new IOException("Resource backing path is unavailable.");
            if (length < path.Capacity - 1) break;
            path.Capacity = checked(path.Capacity * 2);
        }
        var actual = path.ToString();
        if (mappedFiles.TryGetValue(actual, out var known)) return Remember(known);
        if (drives is null)
        {
            drives = [];
            var target = new StringBuilder(260);
            foreach (var drive in Environment.GetLogicalDrives())
            {
                var label = drive[..2];
                while (true)
                {
                    target.Clear();
                    if (QueryDosDeviceW(label, target, (uint)target.Capacity) != 0)
                    { drives.Add((target.ToString(), label)); break; }
                    if (Marshal.GetLastWin32Error() is not (122 or 234)) break;
                    target.Capacity = checked(target.Capacity * 2);
                }
            }
        }
        foreach (var drive in drives)
            if (actual.StartsWith(drive.Device + "\\", StringComparison.OrdinalIgnoreCase))
            {
                var resolved = drive.Drive + actual[drive.Device.Length..];
                mappedFiles[actual] = resolved;
                return Remember(resolved);
            }
        throw new IOException("Resource mapping has no confirmed file-system path.");

        string Remember(string resolved)
        {
            mappings.Add(new((nuint)region.BaseAddress, region.RegionSize, resolved));
            return resolved;
        }
    }

    // Native alignment leaves the Win64 PartitionId in the padding before RegionSize;
    // the same fields also match the Win32 layout, which has no PartitionId.
    [StructLayout(LayoutKind.Sequential)]
    private struct ResourceMemoryInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ResourceNameCallback(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ResourceLanguageCallback(IntPtr module, IntPtr type, IntPtr name, ushort language, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, ResourceNameCallback callback, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumResourceLanguagesExW(IntPtr module, IntPtr type, IntPtr name, ResourceLanguageCallback callback, IntPtr parameter, uint flags, ushort language);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr FindResourceExW(IntPtr module, IntPtr type, IntPtr name, ushort language);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nuint VirtualQuery(IntPtr address, out ResourceMemoryInformation information, nuint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint K32GetMappedFileNameW(IntPtr process, IntPtr address, StringBuilder path, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string device, StringBuilder target, uint length);
}
