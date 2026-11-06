namespace TileWall.Core.Imaging;

internal readonly record struct SystemIconAsset(string Id, string RelativePath, string Sha256, int Width, int Height);

/// <summary>Actual paths supplied by the Windows host; the matcher never guesses a user's folders.</summary>
internal sealed record SystemIconPaths(string? WindowsDirectory, string? SystemDirectory,
    IReadOnlyDictionary<string, string?> KnownFolders);

/// <summary>Finite semantic matches for application-owned system fallback artwork.</summary>
internal static partial class SystemIconCatalog
{
    internal static IReadOnlyDictionary<string, SystemIconAsset> Assets { get; } = CreateAssets();
    internal static string VersionStamp => CatalogVersion;

    private static readonly IReadOnlyDictionary<string, string> ShellNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["shell:MyComputerFolder"] = "computer", ["shell:RecycleBinFolder"] = "recycle",
            ["shell:ControlPanelFolder"] = "control-panel", ["shell:Desktop"] = "desktop",
            ["shell:Common Desktop"] = "desktop", ["shell:Profile"] = "profile",
            ["shell:UsersFilesFolder"] = "profile", ["shell:Personal"] = "documents",
            ["shell:Downloads"] = "downloads", ["shell:My Pictures"] = "pictures",
            ["shell:My Music"] = "music", ["shell:My Video"] = "videos",
            ["shell:NetworkPlacesFolder"] = "network", ["shell:ConnectionsFolder"] = "network",
            ["shell:PrintersFolder"] = "printers", ["shell:Libraries"] = "libraries",
            ["shell:Common Administrative Tools"] = "tools", ["shell:Administrative Tools"] = "tools",
            ["shell:Fonts"] = "fonts", ["shell:Startup"] = "startup", ["shell:Common Startup"] = "startup",
        };

    // Registered Shell namespace CLSIDs, not resource DLL indices or localized display names.
    private static readonly IReadOnlyDictionary<Guid, string> ShellClasses = new Dictionary<Guid, string>
    {
        [new("20D04FE0-3AEA-1069-A2D8-08002B30309D")] = "computer",
        [new("645FF040-5081-101B-9F08-00AA002F954E")] = "recycle",
        [new("21EC2020-3AEA-1069-A2DD-08002B30309D")] = "control-panel",
        [new("26EE0668-A00A-44D7-9371-BEB064C98683")] = "control-panel",
        [new("208D2C60-3AEA-1069-A2D7-08002B30309D")] = "network",
        [new("2227A280-3AEA-1069-A2DE-08002B30309D")] = "printers",
        [new("031E4825-7B94-4DC3-B131-E946B44C8DD5")] = "libraries",
        [new("59031A47-3F72-44A7-89C5-5595FE6B30EE")] = "profile",
        [new("2559A1F3-21D7-11D4-BDAF-00C04F60B9F0")] = "run",
    };

    private static readonly IReadOnlyDictionary<string, string> SettingsCategories =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ms-settings:display"] = "display", ["ms-settings:display-advanced"] = "display",
            ["ms-settings:display-advancedgraphics"] = "display", ["ms-settings:nightlight"] = "display",
            ["ms-settings:sound"] = "sound", ["ms-settings:apps-volume"] = "sound",
            ["ms-settings:sound-devices"] = "sound", ["ms-settings:powersleep"] = "power",
            ["ms-settings:batterysaver"] = "power", ["ms-settings:batterysaver-settings"] = "power",
            ["ms-settings:batterysaver-usagedetails"] = "power", ["ms-settings:storagesense"] = "storage",
            ["ms-settings:storagepolicies"] = "storage", ["ms-settings:storagerecommendations"] = "storage",
            ["ms-settings:storagespaces"] = "storage", ["ms-settings:savelocations"] = "storage",
            ["ms-settings:network-status"] = "network", ["ms-settings:network-wifi"] = "network",
            ["ms-settings:network-wifisettings"] = "network", ["ms-settings:network-ethernet"] = "network",
            ["ms-settings:network-vpn"] = "network", ["ms-settings:network-proxy"] = "network",
            ["ms-settings:network-airplanemode"] = "network", ["ms-settings:network-mobilehotspot"] = "network",
            ["ms-settings:network-dialup"] = "network", ["ms-settings:network-advancedsettings"] = "network",
            ["ms-settings:printers"] = "printers", ["ms-settings:fonts"] = "fonts",
            ["ms-settings:windowsdefender"] = "security", ["ms-settings:about"] = "computer",
        };

    private static readonly IReadOnlyDictionary<string, string> SystemTools =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Taskmgr.exe"] = "tools", ["compmgmt.msc"] = "tools", ["devmgmt.msc"] = "tools",
            ["diskmgmt.msc"] = "storage", ["services.msc"] = "tools", ["eventvwr.msc"] = "tools",
            ["taskschd.msc"] = "tools", ["perfmon.msc"] = "tools", ["resmon.exe"] = "tools",
            ["msinfo32.exe"] = "computer", ["regedt32.exe"] = "tools", ["msconfig.exe"] = "tools",
            ["OptionalFeatures.exe"] = "settings", ["dfrgui.exe"] = "storage", ["cleanmgr.exe"] = "storage",
            ["gpedit.msc"] = "tools", ["secpol.msc"] = "security", ["lusrmgr.msc"] = "profile",
            ["certmgr.msc"] = "security", ["certlm.msc"] = "security", ["WF.msc"] = "security",
            ["comexp.msc"] = "tools", ["printmanagement.msc"] = "printers", ["odbcad32.exe"] = "tools",
            ["dxdiag.exe"] = "computer", ["SystemPropertiesAdvanced.exe"] = "computer",
            ["SystemPropertiesProtection.exe"] = "security", ["SystemPropertiesRemote.exe"] = "computer",
            ["winver.exe"] = "computer", ["calc.exe"] = "calculator", ["notepad.exe"] = "notepad",
            ["cmd.exe"] = "terminal", ["WindowsPowerShell/v1.0/powershell.exe"] = "terminal",
        };

    private static readonly IReadOnlyDictionary<string, string> WindowsTools =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["explorer.exe"] = "folder", ["regedit.exe"] = "tools", ["notepad.exe"] = "notepad",
            ["ImmersiveControlPanel/SystemSettings.exe"] = "settings",
        };

    internal static bool TryGet(string? target, SystemIconPaths? paths, out SystemIconAsset asset)
    {
        asset = default;
        if (string.IsNullOrWhiteSpace(target) || target != target.Trim()) return false;
        if (ShellNames.TryGetValue(target, out var id)) return Assets.TryGetValue(id, out asset);
        var classText = target.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase) ? target[8..]
            : target.StartsWith("::", StringComparison.Ordinal) ? target[2..] : null;
        if (classText is not null && Guid.TryParseExact(classText, "B", out var clsid) &&
            ShellClasses.TryGetValue(clsid, out id)) return Assets.TryGetValue(id, out asset);

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme.Equals("ms-settings", StringComparison.OrdinalIgnoreCase))
                return Assets.TryGetValue(SettingsCategories.GetValueOrDefault(uri.OriginalString) ?? "settings", out asset);
            if (uri.Scheme.Equals("ms-search", StringComparison.OrdinalIgnoreCase))
                return Assets.TryGetValue("search", out asset);
            if (!uri.IsFile) return false;
            target = uri.LocalPath;
        }
        if (paths is null || CanonicalPath(target) is not { } path) return false;
        foreach (var folder in paths.KnownFolders)
            if (Assets.ContainsKey(folder.Key) && CanonicalPath(folder.Value) is { } known &&
                StringComparer.OrdinalIgnoreCase.Equals(path, known))
                return Assets.TryGetValue(folder.Key, out asset);
        return MatchTool(path, paths.SystemDirectory, SystemTools, out asset) ||
            MatchTool(path, paths.WindowsDirectory, WindowsTools, out asset);
    }

    private static bool MatchTool(string path, string? directory, IReadOnlyDictionary<string, string> tools,
        out SystemIconAsset asset)
    {
        asset = default;
        if (CanonicalPath(directory) is not { } root) return false;
        foreach (var tool in tools)
            if (CanonicalPath(Path.Combine(root, tool.Key.Replace('/', Path.DirectorySeparatorChar))) is { } expected &&
                StringComparer.OrdinalIgnoreCase.Equals(path, expected)) return Assets.TryGetValue(tool.Value, out asset);
        return false;
    }

    private static string? CanonicalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }
}
