using System.Runtime.InteropServices;
using Windows.Graphics;

namespace TileWall.Shell.Interop;

internal sealed record DisplayMonitor(nint Id, bool IsPrimary, RectInt32 OuterBounds, RectInt32 WorkArea);

internal static class DisplayTopology
{
    private const uint OnlyActivePaths = 2;
    private const int InsufficientBuffer = 122;
    private const uint SourceMode = 1;
    private const uint GetTargetName = 2;

    internal static nint? MonitorAt(PointInt32 point)
    {
        var monitor = MonitorFromPoint(new Win32Api.InteropPoint { X = point.X, Y = point.Y }, 1);
        return monitor == 0 ? null : monitor;
    }

    internal static IReadOnlyList<DisplayMonitor> ReadMonitors()
    {
        var result = new List<DisplayMonitor>();
        bool Add(nint monitor, nint dc, nint clip, nint context)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(monitor, ref info) && info.Screen.Right > info.Screen.Left && info.Screen.Bottom > info.Screen.Top)
                result.Add(new(monitor, (info.Flags & 1) != 0, info.Screen.Bounds, info.Work.Bounds));
            return true;
        }
        return EnumDisplayMonitors(0, 0, Add, 0) ? result : [];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly RectInt32 Bounds => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal MonitorRect Screen, Work;
        internal uint Flags;
    }

    private delegate bool MonitorCallback(nint monitor, nint dc, nint clip, nint context);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromPoint(Win32Api.InteropPoint point, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint context);

    internal static bool TryRead(int primaryWidth, int primaryHeight, out string topology, out string primaryRotation)
    {
        topology = primaryRotation = string.Empty;
        // Windows can change the number of paths between the size query and the read.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(OnlyActivePaths, out var pathCount, out var modeCount) != 0 ||
                pathCount == 0 || modeCount == 0) return false;
            var paths = new DisplayPath[pathCount];
            var modes = new DisplayMode[modeCount];
            var error = QueryDisplayConfig(OnlyActivePaths, ref pathCount, paths, ref modeCount, modes, 0);
            if (error == InsufficientBuffer) continue;
            if (error != 0) return false;

            var targets = new List<string>();
            var primaryTargets = new List<string>();
            var rotations = new List<uint>();
            for (var index = 0; index < pathCount; index++)
            {
                var path = paths[index];
                if (path.Target.Available == 0 || path.Source.ModeIndex >= modeCount) return false;
                var mode = modes[path.Source.ModeIndex];
                if (mode.Type != SourceMode) return false;

                var name = new TargetName
                {
                    Header = new DeviceInfoHeader
                    {
                        Type = GetTargetName,
                        Size = (uint)Marshal.SizeOf<TargetName>(),
                        Adapter = path.Target.Adapter,
                        Id = path.Target.Id,
                    },
                };
                if (DisplayConfigGetDeviceInfo(ref name) != 0) return false;
                // Virtual or forced targets can have no monitor interface path. Their
                // adapter/target pair is usable for comparisons within this session.
                var target = string.IsNullOrEmpty(name.DevicePath)
                    ? FormattableString.Invariant($"target:{path.Target.Adapter.High:X8}:{path.Target.Adapter.Low:X8}:{path.Target.Id}")
                    : name.DevicePath.ToUpperInvariant();
                targets.Add(target);
                // The desktop coordinate origin identifies the primary source.
                if (mode.X != 0 || mode.Y != 0) continue;
                if (mode.Width != primaryWidth || mode.Height != primaryHeight) return false;
                primaryTargets.Add(target);
                rotations.Add(path.Target.Rotation);
            }

            if (primaryTargets.Count == 0) return false;
            topology = string.Join('\n', targets.Order(StringComparer.Ordinal)) + "\n\n" +
                string.Join('\n', primaryTargets.Order(StringComparer.Ordinal));
            primaryRotation = string.Join(',', rotations.Distinct().Order());
            return true;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterId
    {
        internal uint Low;
        internal int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourcePath
    {
        internal AdapterId Adapter;
        internal uint Id, ModeIndex, Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TargetPath
    {
        internal AdapterId Adapter;
        internal uint Id, ModeIndex, Technology, Rotation, Scaling;
        internal uint RefreshNumerator, RefreshDenominator, ScanOrder;
        internal int Available;
        internal uint Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayPath
    {
        internal SourcePath Source;
        internal TargetPath Target;
        internal uint Flags;
    }

    // DISPLAYCONFIG_MODE_INFO has a 48-byte native union at byte 16. Only
    // DISPLAYCONFIG_SOURCE_MODE is used here, so no target-mode fields are projected.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DisplayMode
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(16)] internal int Width;
        [FieldOffset(20)] internal int Height;
        [FieldOffset(28)] internal int X;
        [FieldOffset(32)] internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        internal uint Type, Size;
        internal AdapterId Adapter;
        internal uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetName
    {
        internal DeviceInfoHeader Header;
        internal uint Flags, Technology;
        internal ushort Manufacturer, Product;
        internal uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DevicePath;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayPath[] paths,
        ref uint modeCount, [Out] DisplayMode[] modes, nint topology);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref TargetName name);
}
