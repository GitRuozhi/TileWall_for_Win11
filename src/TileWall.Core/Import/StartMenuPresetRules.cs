using TileWall.Core.Entries;

namespace TileWall.Core.Import;

/// <summary>Local, reviewable whitelist. A name is evidence only at a token boundary;
/// public launch hosts never contribute their publisher to a hosted application's identity.</summary>
public static class StartMenuPresetRules
{
    private sealed record Rule(string Key, string Title, string[] Paths, string[] Names, string[] Companies,
        string[]? Protocols = null, string[]? Files = null);

    // Product families precede publisher fallbacks. Paths are complete installation segments.
    private static readonly Rule[] Rules =
    [
        new("office", "Office", ["Microsoft Office", "Microsoft 365"], ["Microsoft Office", "Microsoft 365"], [],
            Files: ["winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe", "onenote.exe", "msaccess.exe", "mspub.exe", "visio.exe", "winproj.exe"]),
        new("visual-studio", "Visual Studio", ["Microsoft Visual Studio"], ["Visual Studio", "Developer Command Prompt for VS", "Developer PowerShell for VS"], [], Files: ["devenv.exe", "vs_installer.exe"]),
        new("sql-server", "SQL Server", ["Microsoft SQL Server", "Microsoft SQL Server Management Studio"], ["Microsoft SQL Server", "SQL Server Management Studio"], []),
        new("anaconda", "Anaconda", ["Anaconda", "Anaconda3", "Anaconda2", "Miniconda3", "Miniconda2"], ["Anaconda", "Anaconda Navigator", "Anaconda Prompt", "Anaconda PowerShell Prompt"], ["Anaconda, Inc."]),
        new("adobe", "Adobe", ["Adobe"], ["Adobe Acrobat", "Adobe Photoshop", "Adobe Illustrator", "Adobe Creative Cloud", "Adobe Premiere Pro", "Adobe After Effects", "Adobe InDesign", "Adobe Lightroom"], ["Adobe Inc.", "Adobe Systems Incorporated"]),
        new("autodesk", "Autodesk", ["Autodesk"], ["Autodesk", "AutoCAD", "Autodesk Maya", "Autodesk 3ds Max", "Autodesk Revit"], ["Autodesk, Inc.", "Autodesk"]),
        new("jetbrains", "JetBrains", ["JetBrains"], ["IntelliJ IDEA", "PyCharm", "WebStorm", "PhpStorm", "CLion", "DataGrip", "GoLand", "Rider", "JetBrains Toolbox", "RubyMine", "RustRover"], ["JetBrains s.r.o.", "JetBrains"]),
        new("affinity", "Affinity", ["Affinity", "Affinity Photo", "Affinity Designer", "Affinity Publisher"], ["Affinity Photo", "Affinity Designer", "Affinity Publisher"], ["Serif (Europe) Ltd", "Serif (Europe) Limited"]),
        new("corel", "Corel", ["Corel"], ["CorelDRAW", "Corel PHOTO-PAINT", "Corel Painter", "Corel VideoStudio"], ["Corel Corporation", "Alludo"]),
        new("wps", "WPS", ["Kingsoft", "WPS Office"], ["WPS Office", "WPS文字", "WPS表格", "WPS演示"], ["Zhuhai Kingsoft Office Software Co.,Ltd", "Kingsoft Corporation"], Files: ["wps.exe", "et.exe", "wpp.exe"]),
        new("libreoffice", "LibreOffice", ["LibreOffice"], ["LibreOffice"], ["The Document Foundation"]),
        new("nvidia", "NVIDIA", ["NVIDIA Corporation"], ["NVIDIA Control Panel", "NVIDIA App", "NVIDIA GeForce Experience"], ["NVIDIA Corporation"]),
        new("amd", "AMD", ["AMD", "AMD Software"], ["AMD Software", "AMD Radeon Software", "AMD Ryzen Master"], ["Advanced Micro Devices, Inc."]),
        new("intel", "Intel", ["Intel"], ["Intel Driver & Support Assistant", "Intel Graphics Command Center", "Intel Graphics Control Panel", "Intel Arc Control"], ["Intel Corporation"]),
        new("logitech", "Logitech", ["Logitech", "LogiOptionsPlus", "Logitech Gaming Software"], ["Logitech G HUB", "Logi Options+", "Logitech Options", "Logitech Gaming Software"], ["Logitech", "Logitech Inc."]),
        new("razer", "Razer", ["Razer"], ["Razer Synapse", "Razer Central", "Razer Cortex"], ["Razer Inc."]),
        new("steam", "Steam", ["Steam"], ["Steam"], ["Valve Corporation"], ["steam"]),
        new("epic", "Epic Games", ["Epic Games", "EpicGamesLauncher"], ["Epic Games Launcher"], ["Epic Games, Inc."], ["com.epicgames.launcher"]),
        new("ea", "EA", ["Electronic Arts", "EA Desktop", "Origin"], ["EA app", "EA Desktop", "Origin"], ["Electronic Arts", "Electronic Arts Inc."], ["origin", "origin2", "link2ea"]),
        new("ubisoft", "Ubisoft", ["Ubisoft", "Ubisoft Game Launcher"], ["Ubisoft Connect", "Uplay"], ["Ubisoft", "Ubisoft Entertainment"], ["uplay", "ubisoftconnect"]),
        new("blizzard", "Blizzard", ["Battle.net", "Blizzard Entertainment"], ["Battle.net", "Blizzard Battle.net"], ["Blizzard Entertainment", "Blizzard Entertainment, Inc."], ["battlenet"]),
        new("tencent", "腾讯", ["Tencent", "腾讯", "WeChat", "Weixin"], ["腾讯QQ", "QQ", "微信", "WeChat", "Weixin", "腾讯会议", "Tencent Meeting", "企业微信", "WeCom", "腾讯文档"], ["Tencent", "Tencent Technology (Shenzhen) Company Limited", "腾讯科技（深圳）有限公司"]),
        new("netease", "网易", ["NetEase", "网易", "CloudMusic"], ["网易云音乐", "网易有道词典", "网易邮箱大师", "NetEase Cloud Music", "Youdao Dictionary"], ["NetEase", "NetEase, Inc.", "网易（杭州）网络有限公司"]),
        new("microsoft", "Microsoft", ["Microsoft", "Microsoft VS Code", "Microsoft OneDrive", "Microsoft Edge"], ["Microsoft Edge", "Microsoft OneDrive", "Microsoft Teams", "Visual Studio Code", "Microsoft PowerToys"], ["Microsoft Corporation"]),
    ];

    public static StartMenuBucket? Match(StartMenuCandidate candidate, LaunchIdentity identity, string? company = null)
    {
        var target = identity.Target.Replace('/', '\\');
        var file = target[(target.LastIndexOf('\\') + 1)..];
        var host = new[] { "explorer.exe", "rundll32.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "msiexec.exe", "wscript.exe", "cscript.exe" }
            .Contains(file, StringComparer.OrdinalIgnoreCase);
        var folders = candidate.RelativeDir.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var segments = target.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var packageTarget = target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase) ? target :
            host && identity.Arguments.Trim().Trim('"').StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)
                ? identity.Arguments.Trim().Trim('"') : null;
        var scheme = identity.IsUrl && Uri.TryCreate(identity.Target, UriKind.Absolute, out var uri) ? uri.Scheme : null;
        foreach (var rule in Rules)
        {
            var companyMatch = !identity.IsUrl && !host && company is not null && rule.Companies.Contains(company.Trim(), StringComparer.OrdinalIgnoreCase);
            var pathMatch = !identity.IsUrl && !host && segments.Any(segment => rule.Paths.Any(path => InstallationSegment(segment, path)));
            var nameMatch = !identity.IsUrl && rule.Names.Any(name => TokenPrefix(candidate.DisplayName, name) || folders.Any(folder => TokenPrefix(folder, name)));
            if (rule.Key == "visual-studio" && TokenPrefix(candidate.DisplayName, "Visual Studio Code")) nameMatch = false;
            if (rule.Key is "nvidia" or "amd" or "intel" or "logitech" or "razer")
                pathMatch &= nameMatch || companyMatch;
            // Common Office executable names alone are not sufficient: use Microsoft publisher or its install path.
            var fileMatch = !host && rule.Files?.Contains(file, StringComparer.OrdinalIgnoreCase) == true &&
                (pathMatch || company?.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) == true ||
                 rule.Key == "wps" && (nameMatch || companyMatch));
            var protocolMatch = scheme is not null && rule.Protocols?.Contains(scheme, StringComparer.OrdinalIgnoreCase) == true;
            var packageMatch = !identity.IsUrl && packageTarget is not null &&
                (rule.Key == "microsoft" && packageTarget[17..].StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                 rule.Key == "office" && packageTarget[17..].StartsWith("Microsoft.Office.", StringComparison.OrdinalIgnoreCase));
            if (companyMatch || pathMatch || nameMatch || fileMatch || protocolMatch || packageMatch)
                return new("preset:" + rule.Key, rule.Title, 0);
        }
        return null;
    }

    private static bool TokenPrefix(string value, string alias) =>
        value.Equals(alias, StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith(alias, StringComparison.OrdinalIgnoreCase) && value.Length > alias.Length &&
        (char.IsWhiteSpace(value[alias.Length]) || char.IsDigit(value[alias.Length]) || "-_.([{（".Contains(value[alias.Length]));

    private static bool InstallationSegment(string value, string alias)
    {
        if (value.Equals(alias, StringComparison.OrdinalIgnoreCase)) return true;
        if (!value.StartsWith(alias, StringComparison.OrdinalIgnoreCase)) return false;
        var version = value[alias.Length..].TrimStart(' ', '-', '_', '.');
        return version.Length != 0 && char.IsDigit(version[0]);
    }
}

public sealed record StartMenuGroupingItem(StartMenuCandidate Candidate, LaunchIdentity? Identity, bool CanAdd, string? Company = null);

public static class StartMenuGroupingRules
{
    /// <summary>Selection is not an input; counts are calculated from the caller's current visible candidate set.</summary>
    public static IReadOnlyList<StartMenuBucket> Assign(IReadOnlyList<StartMenuGroupingItem> items, bool recognizePresets = true)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!recognizePresets) return AssignExistingFoldersAndLetters(items);

        var presets = items.Select(item => item.Identity is null ? null : StartMenuPresetRules.Match(item.Candidate, item.Identity, item.Company)).ToArray();
        var presetCounts = items.Select((item, index) => (item, index))
            .Where(pair => pair.item.CanAdd && presets[pair.index] is not null)
            .GroupBy(pair => presets[pair.index]!.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var folderCounts = items.Select((item, index) => (item, index))
            .Where(pair => pair.item.CanAdd && presets[pair.index] is null)
            .Select(pair => TopFolder(pair.item.Candidate)).Where(folder => folder.Length != 0)
            .GroupBy(folder => folder, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        return items.Select((item, index) =>
        {
            if (presets[index] is { } preset)
                return presetCounts.GetValueOrDefault(preset.Key) > 1 ? preset : Letter(item.Candidate);
            var folder = TopFolder(item.Candidate);
            if (folder.Length != 0 && folderCounts.GetValueOrDefault(folder) > 1)
                return Folder(folder);
            return Letter(item.Candidate);
        }).ToArray();
    }

    private static IReadOnlyList<StartMenuBucket> AssignExistingFoldersAndLetters(IReadOnlyList<StartMenuGroupingItem> items) =>
        items.Select(item =>
        {
            var folder = TopFolder(item.Candidate);
            return folder.Length == 0 ? Letter(item.Candidate) : Folder(folder);
        }).ToArray();

    private static StartMenuBucket Folder(string folder) =>
        new("folder:" + folder.ToUpperInvariant(), folder, 100);

    private static StartMenuBucket Letter(StartMenuCandidate candidate)
    {
        var initial = StartMenuSelectionRules.Initial(candidate.DisplayName);
        return new StartMenuBucket("letter:" + initial, initial.ToString(), 200 + (initial == '#' ? 0 : initial - 'A' + 1));
    }

    private static string TopFolder(StartMenuCandidate candidate) =>
        candidate.RelativeDir.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
}
