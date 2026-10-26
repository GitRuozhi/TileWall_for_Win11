using TileWall.Core.Shell;

namespace TileWall.ApplicationModel;

internal sealed record StartupIntent(bool Background, ExplorerAddMessage? Add)
{
    public bool NotificationActivation { get; init; }

    public static StartupIntent Parse(string[] arguments, bool startupTask = false)
    {
        var background = startupTask;
        var adding = false;
        var truncated = false;
        var paths = new List<string>();
        var characters = 0;
        for (var i = 0; i < arguments.Length; i++)
        {
            var value = arguments[i];
            if (value.Equals("--test-root", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            if (value.Equals("--background", StringComparison.OrdinalIgnoreCase)) { background = true; continue; }
            if (value.Equals(ExplorerAddCommandLine.AddArgument, StringComparison.OrdinalIgnoreCase)) { adding = true; continue; }
            if (value.Equals(ExplorerAddCommandLine.TruncatedMarker, StringComparison.OrdinalIgnoreCase)) { truncated = true; continue; }
            if (!adding) continue;
            if (paths.Count >= 256 || characters + value.Length > ExplorerAddCommandLine.MaxArgumentsLength)
            { truncated = true; continue; }
            if (!Path.IsPathFullyQualified(value) || !ExplorerAddPayload.IsAddableExistingPath(value)) { truncated = true; continue; }
            paths.Add(Path.GetFullPath(value));
            characters += value.Length;
        }
        return new(background, adding ? new ExplorerAddMessage(paths, truncated) : null);
    }

#if TILEWALL_PACKAGED
    internal static string[] ReadLaunchArguments(string arguments)
    {
        // Launch.Arguments is an argument payload. A dummy executable gives every real argument
        // the normal Windows quoting rules, including paths with spaces and trailing slashes.
        var native = CommandLineToArgvW("TileWall.exe " + arguments, out var count);
        if (native == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        try
        {
            var result = new string[Math.Max(0, count - 1)];
            for (var i = 1; i < count; i++)
                result[i - 1] = System.Runtime.InteropServices.Marshal.PtrToStringUni(
                    System.Runtime.InteropServices.Marshal.ReadIntPtr(native, i * IntPtr.Size)) ?? string.Empty;
            return result;
        }
        finally { LocalFree(native); }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
#endif
}
