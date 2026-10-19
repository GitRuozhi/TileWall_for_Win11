using System.Text;
using System.Text.RegularExpressions;
using TileWall.Core.Entries;

namespace TileWall.Core.Import;

public sealed record StartMenuBucket(string Key, string Title, int Order);
public sealed record StartMenuSelectionMigration(IReadOnlyList<string> SelectedKeys, int RemovedCount);
public enum StartMenuExclusion { None, WindowsBuiltIn, Uninstall, Maintenance, DocumentOrWeb }

/// <summary>Deterministic local selection policy. It describes purpose, never usage frequency.</summary>
public static partial class StartMenuSelectionRules
{
    private static readonly HashSet<string> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        "notepad.exe", "mspaint.exe", "calc.exe", "calculator.exe", "snippingtool.exe", "snipandsketch.exe",
        "wordpad.exe", "write.exe", "charmap.exe", "magnify.exe", "narrator.exe", "osk.exe", "stikynot.exe",
        "soundrecorder.exe", "psr.exe", "mstsc.exe", "msinfo32.exe", "resmon.exe", "perfmon.exe", "regedit.exe",
        "taskmgr.exe", "cleanmgr.exe", "dfrgui.exe", "dxdiag.exe", "control.exe", "wfs.exe", "winver.exe",
        "iscsicpl.exe", "odbcad32.exe", "optionalfeatures.exe", "recoverydrive.exe", "mdsched.exe",
        "systempropertiesadvanced.exe", "systempropertiescomputername.exe", "systempropertiesprotection.exe",
        "systempropertiesremote.exe", "mblctr.exe", "credwiz.exe", "eudcedit.exe", "printmanagement.msc",
        "rstrui.exe", "quickassist.exe", "utilman.exe", "msconfig.exe", "systemsettings.exe", "vmcreate.exe",
        "msra.exe", "dvdplay.exe", "wmplayer.exe", "documentscan.exe", "sigverif.exe", "verifier.exe",
        "mip.exe", "sapisvr.exe", "iexplore.exe", "xpsrchvw.exe",
    };
    private static readonly string[] BuiltInPackages =
    [
        "Microsoft.WindowsCalculator_", "Microsoft.WindowsNotepad_", "Microsoft.Paint_", "Microsoft.ScreenSketch_",
        "Microsoft.Windows.Photos_", "Microsoft.WindowsCamera_", "Microsoft.WindowsSoundRecorder_",
        "Microsoft.WindowsAlarms_", "Microsoft.WindowsMaps_", "Microsoft.MicrosoftStickyNotes_",
        "Microsoft.WindowsTerminal_", "Microsoft.WindowsStore_", "Microsoft.WindowsFeedbackHub_",
        "Microsoft.GetHelp_", "Microsoft.Getstarted_", "Microsoft.People_", "Microsoft.YourPhone_",
        "Microsoft.MicrosoftSolitaireCollection_", "Microsoft.Xbox", "Microsoft.ZuneMusic_", "Microsoft.ZuneVideo_",
        "microsoft.windowscommunicationsapps_", "Microsoft.BingWeather_", "Microsoft.BingNews_",
        "Microsoft.Todos_", "Microsoft.OutlookForWindows_", "Microsoft.Windows.DevHome_", "Microsoft.PowerAutomateDesktop_",
    ];
    private static readonly string[] BuiltInShellIds =
    [
        "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", // Computer
        "{645FF040-5081-101B-9F08-00AA002F954E}", // Recycle bin
        "{26EE0668-A00A-44D7-9371-BEB064C98683}", // Control panel
        "{21EC2020-3AEA-1069-A2DD-08002B30309D}", // Control panel legacy
        "{D20EA4E1-3957-11D2-A40B-0C5020524153}", // Administrative tools
        "{2559A1F3-21D7-11D4-BDAF-00C04F60B9F0}", // Run
    ];

    /// <summary>Keeps previous SelectionKey values that are still addable. Matching is ordinal and ignores display names.</summary>
    public static StartMenuSelectionMigration MigrateSelection(IEnumerable<string> previousKeys, IEnumerable<string> addableKeys)
    {
        ArgumentNullException.ThrowIfNull(previousKeys);
        ArgumentNullException.ThrowIfNull(addableKeys);
        var addable = new HashSet<string>(addableKeys.Where(key => !string.IsNullOrEmpty(key)), StringComparer.Ordinal);
        var selected = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var removed = 0;
        foreach (var key in previousKeys)
        {
            if (string.IsNullOrEmpty(key) || !seen.Add(key)) continue;
            if (addable.Contains(key)) selected.Add(key);
            else removed++;
        }
        return new StartMenuSelectionMigration(selected, removed);
    }

    public static char Initial(string name)
    {
        var value = name.TrimStart();
        if (value.Length == 0) return '#';
        var first = char.ToUpperInvariant(value[0]);
        if (first is >= 'A' and <= 'Z') return first;
        // Fixed preferred readings for common polyphonic surname/place initials.
        var preferred = first switch { '重' => 'C', '长' or '長' => 'C', '沈' => 'S', '厦' or '廈' => 'X', '单' or '單' => 'D', _ => '\0' };
        if (preferred != '\0') return preferred;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        byte[] encoded;
        try { encoded = bytes.GetBytes(value[..1]); }
        catch (EncoderFallbackException) { return '#'; }
        if (encoded.Length != 2) return '#';
        var code = encoded[0] * 256 + encoded[1] - 65536;
        int[] bounds = [-20319, -20284, -19776, -19219, -18711, -18527, -18240, -17923, -17418, -16475, -16213,
            -15641, -15166, -14923, -14915, -14631, -14150, -14091, -13319, -12839, -12557, -11848, -11056];
        const string letters = "ABCDEFGHJKLMNOPQRSTWXYZ";
        if (code < bounds[0] || code > -10247) return '#';
        for (var index = bounds.Length - 1; index >= 0; index--) if (code >= bounds[index]) return letters[index];
        return '#';
    }

    public static StartMenuExclusion Classify(string displayName, LaunchIdentity identity)
    {
        if (identity.IsUrl && Uri.TryCreate(identity.Target, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            return StartMenuExclusion.DocumentOrWeb;
        var target = identity.Target.Replace('/', '\\');
        var fileName = target[(target.LastIndexOf('\\') + 1)..];
        var args = identity.Arguments;
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        var isSystemPath = systemRoot.Length != 0 && target.StartsWith(systemRoot + "\\", StringComparison.OrdinalIgnoreCase);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var isAccessories = target.Contains("\\Windows NT\\Accessories\\", StringComparison.OrdinalIgnoreCase) ||
            target.Contains("\\Windows Photo Viewer\\", StringComparison.OrdinalIgnoreCase) ||
            target.Contains("\\Windows Media Player\\", StringComparison.OrdinalIgnoreCase) ||
            target.Contains("\\Internet Explorer\\", StringComparison.OrdinalIgnoreCase) ||
            target.Contains("\\Common Files\\Microsoft Shared\\ink\\", StringComparison.OrdinalIgnoreCase) ||
            target.Contains("\\Common Files\\Microsoft Shared\\Speech\\", StringComparison.OrdinalIgnoreCase);
        var knownShell = BuiltInShellIds.Any(id => (target + " " + args).Contains(id, StringComparison.OrdinalIgnoreCase));
        var consoleHost = new[] { "cmd.exe", "powershell.exe", "powershell_ise.exe" }.Contains(fileName, StringComparer.OrdinalIgnoreCase);
        if (((isSystemPath || isAccessories) && BuiltIns.Contains(fileName)) ||
            (isSystemPath && fileName.EndsWith(".msc", StringComparison.OrdinalIgnoreCase)) ||
            (isSystemPath && consoleHost && string.IsNullOrWhiteSpace(args)) ||
            (isSystemPath && fileName.Equals("mmc.exe", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(args) || (args.Contains(systemRoot + "\\", StringComparison.OrdinalIgnoreCase) && args.Contains(".msc", StringComparison.OrdinalIgnoreCase)))) ||
            BuiltInPackages.Any(package => (target + " " + args).Contains(package, StringComparison.OrdinalIgnoreCase)) ||
            target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("windowsdefender:", StringComparison.OrdinalIgnoreCase) ||
            (fileName.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) &&
                target.Contains("\\Microsoft\\Edge\\Application\\", StringComparison.OrdinalIgnoreCase)) ||
            (fileName.Equals("onedrive.exe", StringComparison.OrdinalIgnoreCase) &&
                (target.Contains("\\Microsoft\\OneDrive\\", StringComparison.OrdinalIgnoreCase) ||
                    target.Contains("\\Microsoft OneDrive\\", StringComparison.OrdinalIgnoreCase))) ||
            (target.StartsWith("::", StringComparison.Ordinal) && knownShell) ||
            (isSystemPath && fileName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(args) || args.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ||
                    knownShell || args.Equals("shell:ControlPanelFolder", StringComparison.OrdinalIgnoreCase) ||
                    args.Equals("shell:Administrative Tools", StringComparison.OrdinalIgnoreCase))) ||
            (isSystemPath && fileName.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) &&
                (args.StartsWith("shell32.dll,Control_RunDLL", StringComparison.OrdinalIgnoreCase) ||
                    args.StartsWith("shell32.dll,Options_RunDLL", StringComparison.OrdinalIgnoreCase) ||
                    args.Equals("shell32.dll,#61", StringComparison.OrdinalIgnoreCase))) ||
            (programFiles.Length != 0 && target.StartsWith(programFiles + "\\Windows Defender\\", StringComparison.OrdinalIgnoreCase)))
            return StartMenuExclusion.WindowsBuiltIn;
        if (UninstallFile().IsMatch(fileName) || UninstallWords().IsMatch(displayName) ||
            (fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) && MsiUninstall().IsMatch(args)))
            return StartMenuExclusion.Uninstall;
        if ((MaintenanceWords().IsMatch(displayName) && MaintenanceFile().IsMatch(fileName)) ||
            (fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) && MsiMaintenance().IsMatch(args)))
            return StartMenuExclusion.Maintenance;
        var extension = Path.GetExtension(fileName);
        if (new[] { ".pdf", ".txt", ".rtf", ".doc", ".docx", ".htm", ".html", ".chm", ".hlp", ".md" }
            .Contains(extension, StringComparer.OrdinalIgnoreCase)) return StartMenuExclusion.DocumentOrWeb;
        if (fileName.Equals("hh.exe", StringComparison.OrdinalIgnoreCase) && args.Contains(".chm", StringComparison.OrdinalIgnoreCase))
            return StartMenuExclusion.DocumentOrWeb;
        return StartMenuExclusion.None;
    }

    [GeneratedRegex(@"^(?:uninstall(?:er)?|unins\d*|unwise)(?:\.exe)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UninstallFile();
    [GeneratedRegex(@"(?:卸载|卸載|\buninstall(?:er)?\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UninstallWords();
    [GeneratedRegex(@"(?:^|\s)[/-]x(?:\s|\{|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MsiUninstall();
    [GeneratedRegex(@"(?:修复|修復|更新器|检查更新|檢查更新|安装|安裝|维护|維護|\b(?:repair|updater|installer|maintenance|check for updates)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MaintenanceWords();
    [GeneratedRegex(@"^(?:setup|install|installer|update|updater|repair|maintenance|vs_installer|checkforupdates|sldcheckforupdates|sldim|sldbgdwld)(?:[._-].*)?\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MaintenanceFile();
    [GeneratedRegex(@"(?:^|\s)[/-][fip](?:\s|\{|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MsiMaintenance();
}
