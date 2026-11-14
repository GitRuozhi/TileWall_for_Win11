using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TileWall.Core.Entries;
using TileWall.Resources;
using TileWall.Shell.Interop;

namespace TileWall.ApplicationModel;

/// <summary>Native Windows destinations only. Reading this catalog never launches a destination.</summary>
internal static class SystemFeatureCatalog
{
    private sealed record Definition(string Id, string NameKey, string Target,
        int MinimumBuild = 10240, int MaximumBuild = int.MaxValue, string? Hardware = null,
        bool RequiresProfessionalEdition = false);

    // Shell's registered canonical names resolve redirection and per-instance identity.
    private static readonly Definition[] Locations =
    [
        new("computer", "Catalog.Location.computer", "shell:MyComputerFolder"),
        new("recycle", "Catalog.Location.recycle", "shell:RecycleBinFolder"),
        new("desktop", "Catalog.Location.desktop", "shell:Desktop"),
        new("profile", "Catalog.Location.profile", "shell:Profile"),
        new("documents", "Catalog.Location.documents", "shell:Personal"),
        new("downloads", "Catalog.Location.downloads", "shell:Downloads"),
        new("pictures", "Catalog.Location.pictures", "shell:My Pictures"),
        new("music", "Catalog.Location.music", "shell:My Music"),
        new("videos", "Catalog.Location.videos", "shell:My Video"),
        new("network", "Catalog.Location.network", "shell:NetworkPlacesFolder"),
        new("connections", "Catalog.Location.connections", "shell:ConnectionsFolder"),
        new("printers", "Catalog.Location.printers", "shell:PrintersFolder"),
        new("libraries", "Catalog.Location.libraries", "shell:Libraries"),
        new("control-panel", "Catalog.Location.control-panel", "shell:ControlPanelFolder"),
        new("windows-tools", "Catalog.Location.windows-tools", "shell:Common Administrative Tools"),
        new("user-tools", "Catalog.Location.user-tools", "shell:Administrative Tools"),
        new("fonts", "Catalog.Location.fonts", "shell:Fonts"),
        new("startup", "Catalog.Location.startup", "shell:Startup"),
        new("public-desktop", "Catalog.Location.public-desktop", "shell:Common Desktop"),
    ];

    // Public desktop ms-settings pages. Parameterized templates and mobile-only pages are deliberately absent.
    // Availability conditions are part of the definition; protocol registration alone proves no page exists.
    // Reference: https://learn.microsoft.com/windows/apps/develop/launch/launch-settings
    private static readonly Definition[] Settings =
    [
        new("home", "Catalog.Settings.home", "ms-settings:"),
        new("display", "Catalog.Settings.display", "ms-settings:display"),
        new("advanced-display", "Catalog.Settings.advanced-display", "ms-settings:display-advanced"),
        new("night-light", "Catalog.Settings.night-light", "ms-settings:nightlight", 15063),
        new("graphics", "Catalog.Settings.graphics", "ms-settings:display-advancedgraphics", 17134),
        new("sound", "Catalog.Settings.sound", "ms-settings:sound", 17134),
        new("volume", "Catalog.Settings.volume", "ms-settings:apps-volume", 17134),
        new("sound-devices", "Catalog.Settings.sound-devices", "ms-settings:sound-devices", 17763),
        new("notifications", "Catalog.Settings.notifications", "ms-settings:notifications"),
        new("focus-assist", "Catalog.Settings.focus-assist", "ms-settings:quiethours", 17134, 21999),
        new("focus", "Catalog.Settings.focus", "ms-settings:quietmomentshome", 22000),
        new("power", "Catalog.Settings.power", "ms-settings:powersleep"),
        new("battery", "Catalog.Settings.battery", "ms-settings:batterysaver", Hardware: "battery"),
        new("battery-settings", "Catalog.Settings.battery-settings", "ms-settings:batterysaver-settings", Hardware: "battery"),
        new("battery-use", "Catalog.Settings.battery-use", "ms-settings:batterysaver-usagedetails", Hardware: "battery"),
        new("storage", "Catalog.Settings.storage", "ms-settings:storagesense"),
        new("storage-policies", "Catalog.Settings.storage-policies", "ms-settings:storagepolicies", 16299),
        new("save-locations", "Catalog.Settings.save-locations", "ms-settings:savelocations"),
        new("storage-recommendations", "Catalog.Settings.storage-recommendations", "ms-settings:storagerecommendations", 22000),
        new("storage-spaces", "Catalog.Settings.storage-spaces", "ms-settings:storagespaces", 22000),
        new("multitasking", "Catalog.Settings.multitasking", "ms-settings:multitasking"),
        new("projection", "Catalog.Settings.projection", "ms-settings:project", 14393),
        new("remote-desktop", "Catalog.Settings.remote-desktop", "ms-settings:remotedesktop", 16299),
        new("clipboard", "Catalog.Settings.clipboard", "ms-settings:clipboard", 17763),
        new("about", "Catalog.Settings.about", "ms-settings:about"),
        new("bluetooth", "Catalog.Settings.bluetooth", "ms-settings:bluetooth"),
        new("connected-devices", "Catalog.Settings.connected-devices", "ms-settings:connecteddevices"),
        new("printers", "Catalog.Settings.printers", "ms-settings:printers"),
        new("mouse", "Catalog.Settings.mouse", "ms-settings:mousetouchpad"),
        new("touchpad", "Catalog.Settings.touchpad", "ms-settings:devices-touchpad", 15063),
        new("pen", "Catalog.Settings.pen", "ms-settings:pen", 14393),
        new("typing", "Catalog.Settings.typing", "ms-settings:typing"),
        new("hardware-typing", "Catalog.Settings.hardware-typing", "ms-settings:devicestyping-hwkbtextsuggestions", 17134),
        new("autoplay", "Catalog.Settings.autoplay", "ms-settings:autoplay"),
        new("usb", "Catalog.Settings.usb", "ms-settings:usb"),
        new("cameras", "Catalog.Settings.cameras", "ms-settings:camera", 22000),
        new("network", "Catalog.Settings.network", "ms-settings:network-status"),
        new("wifi", "Catalog.Settings.wifi", "ms-settings:network-wifi", Hardware: "wifi"),
        new("known-networks", "Catalog.Settings.known-networks", "ms-settings:network-wifisettings", Hardware: "wifi"),
        new("ethernet", "Catalog.Settings.ethernet", "ms-settings:network-ethernet"),
        new("vpn", "Catalog.Settings.vpn", "ms-settings:network-vpn"),
        new("proxy", "Catalog.Settings.proxy", "ms-settings:network-proxy"),
        new("airplane", "Catalog.Settings.airplane", "ms-settings:network-airplanemode"),
        new("hotspot", "Catalog.Settings.hotspot", "ms-settings:network-mobilehotspot", 14393, Hardware: "wifi"),
        new("dialup", "Catalog.Settings.dialup", "ms-settings:network-dialup"),
        new("advanced-network", "Catalog.Settings.advanced-network", "ms-settings:network-advancedsettings", 22000),
        new("background", "Catalog.Settings.background", "ms-settings:personalization-background"),
        new("colors", "Catalog.Settings.colors", "ms-settings:personalization-colors"),
        new("lock-screen", "Catalog.Settings.lock-screen", "ms-settings:lockscreen"),
        new("themes", "Catalog.Settings.themes", "ms-settings:themes"),
        new("start", "Catalog.Settings.start", "ms-settings:personalization-start"),
        new("taskbar", "Catalog.Settings.taskbar", "ms-settings:taskbar"),
        new("fonts", "Catalog.Settings.fonts", "ms-settings:fonts", 17134),
        new("text-input", "Catalog.Settings.text-input", "ms-settings:personalization-textinput", 22000),
        new("touch-keyboard", "Catalog.Settings.touch-keyboard", "ms-settings:personalization-touchkeyboard", 22000),
        new("your-info", "Catalog.Settings.your-info", "ms-settings:yourinfo"),
        new("email-accounts", "Catalog.Settings.email-accounts", "ms-settings:emailandaccounts"),
        new("signin", "Catalog.Settings.signin", "ms-settings:signinoptions"),
        new("dynamic-lock", "Catalog.Settings.dynamic-lock", "ms-settings:signinoptions-dynamiclock", 15063),
        new("work-school", "Catalog.Settings.work-school", "ms-settings:workplace"),
        new("other-users", "Catalog.Settings.other-users", "ms-settings:otherusers"),
        new("account-sync", "Catalog.Settings.account-sync", "ms-settings:sync", MaximumBuild: 21999),
        new("backup", "Catalog.Settings.backup", "ms-settings:backup", 22000),
        new("apps", "Catalog.Settings.apps", "ms-settings:appsfeatures"),
        new("default-apps", "Catalog.Settings.default-apps", "ms-settings:defaultapps"),
        new("apps-websites", "Catalog.Settings.apps-websites", "ms-settings:appsforwebsites", 14393),
        new("optional-features", "Catalog.Settings.optional-features", "ms-settings:optionalfeatures"),
        new("startup-apps", "Catalog.Settings.startup-apps", "ms-settings:startupapps", 17134),
        new("video", "Catalog.Settings.video", "ms-settings:videoplayback", 16299),
        new("offline-maps", "Catalog.Settings.offline-maps", "ms-settings:maps"),
        new("date-time", "Catalog.Settings.date-time", "ms-settings:dateandtime"),
        new("language", "Catalog.Settings.language", "ms-settings:regionlanguage"),
        new("region", "Catalog.Settings.region", "ms-settings:regionformatting", 17134),
        new("speech", "Catalog.Settings.speech", "ms-settings:speech"),
        new("keyboard", "Catalog.Settings.keyboard", "ms-settings:keyboard-advanced", 17134),
        new("gamebar", "Catalog.Settings.gamebar", "ms-settings:gaming-gamebar", 15063),
        new("game-captures", "Catalog.Settings.game-captures", "ms-settings:gaming-gamedvr", 15063),
        new("game-mode", "Catalog.Settings.game-mode", "ms-settings:gaming-gamemode", 15063),
        new("privacy-general", "Catalog.Settings.privacy-general", "ms-settings:privacy-general"),
        new("privacy-location", "Catalog.Settings.privacy-location", "ms-settings:privacy-location"),
        new("privacy-camera", "Catalog.Settings.privacy-camera", "ms-settings:privacy-webcam"),
        new("privacy-microphone", "Catalog.Settings.privacy-microphone", "ms-settings:privacy-microphone"),
        new("privacy-notifications", "Catalog.Settings.privacy-notifications", "ms-settings:privacy-notifications", 14393),
        new("privacy-speech", "Catalog.Settings.privacy-speech", "ms-settings:privacy-speech", 17763),
        new("privacy-inking", "Catalog.Settings.privacy-inking", "ms-settings:privacy-speechtyping"),
        new("privacy-feedback", "Catalog.Settings.privacy-feedback", "ms-settings:privacy-feedback"),
        new("privacy-history", "Catalog.Settings.privacy-history", "ms-settings:privacy-activityhistory", 17134),
        new("privacy-account", "Catalog.Settings.privacy-account", "ms-settings:privacy-accountinfo"),
        new("privacy-contacts", "Catalog.Settings.privacy-contacts", "ms-settings:privacy-contacts"),
        new("privacy-calendar", "Catalog.Settings.privacy-calendar", "ms-settings:privacy-calendar"),
        new("privacy-calls", "Catalog.Settings.privacy-calls", "ms-settings:privacy-callhistory"),
        new("privacy-email", "Catalog.Settings.privacy-email", "ms-settings:privacy-email"),
        new("privacy-tasks", "Catalog.Settings.privacy-tasks", "ms-settings:privacy-tasks", 14393),
        new("privacy-messaging", "Catalog.Settings.privacy-messaging", "ms-settings:privacy-messaging"),
        new("privacy-radios", "Catalog.Settings.privacy-radios", "ms-settings:privacy-radios"),
        new("privacy-devices", "Catalog.Settings.privacy-devices", "ms-settings:privacy-customdevices"),
        new("privacy-background", "Catalog.Settings.privacy-background", "ms-settings:privacy-backgroundapps", MaximumBuild: 21999),
        new("privacy-app-diagnostics", "Catalog.Settings.privacy-app-diagnostics", "ms-settings:privacy-appdiagnostics", 15063),
        new("privacy-downloads", "Catalog.Settings.privacy-downloads", "ms-settings:privacy-automaticfiledownloads", 16299),
        new("privacy-documents", "Catalog.Settings.privacy-documents", "ms-settings:privacy-documents", 17134),
        new("privacy-pictures", "Catalog.Settings.privacy-pictures", "ms-settings:privacy-pictures", 17134),
        new("privacy-videos", "Catalog.Settings.privacy-videos", "ms-settings:privacy-videos", 17134),
        new("privacy-filesystem", "Catalog.Settings.privacy-filesystem", "ms-settings:privacy-broadfilesystemaccess", 17134),
        new("privacy-downloadsfolder", "Catalog.Settings.privacy-downloadsfolder", "ms-settings:privacy-downloadsfolder", 18362),
        new("privacy-phone", "Catalog.Settings.privacy-phone", "ms-settings:privacy-phonecalls", 17763),
        new("privacy-voice", "Catalog.Settings.privacy-voice", "ms-settings:privacy-voiceactivation", 18362),
        new("privacy-screenshots", "Catalog.Settings.privacy-screenshots", "ms-settings:privacy-screenshotborders", 19041),
        new("search-permissions", "Catalog.Settings.search-permissions", "ms-settings:search-permissions", 18362),
        new("search-indexing", "Catalog.Settings.search-indexing", "ms-settings:search", 18362),
        new("update", "Catalog.Settings.update", "ms-settings:windowsupdate"),
        new("update-history", "Catalog.Settings.update-history", "ms-settings:windowsupdate-history"),
        new("update-options", "Catalog.Settings.update-options", "ms-settings:windowsupdate-options"),
        new("update-restart", "Catalog.Settings.update-restart", "ms-settings:windowsupdate-restartoptions"),
        new("delivery", "Catalog.Settings.delivery", "ms-settings:delivery-optimization", 14393),
        new("security", "Catalog.Settings.security", "ms-settings:windowsdefender"),
        new("recovery", "Catalog.Settings.recovery", "ms-settings:recovery"),
        new("activation", "Catalog.Settings.activation", "ms-settings:activation"),
        new("troubleshoot", "Catalog.Settings.troubleshoot", "ms-settings:troubleshoot", 15063),
        new("developers", "Catalog.Settings.developers", "ms-settings:developers"),
        new("accessibility-display", "Catalog.Settings.accessibility-display", "ms-settings:easeofaccess-display", 17134),
        new("accessibility-audio", "Catalog.Settings.accessibility-audio", "ms-settings:easeofaccess-audio", 17134),
        new("captions", "Catalog.Settings.captions", "ms-settings:easeofaccess-closedcaptioning"),
        new("color-filters", "Catalog.Settings.color-filters", "ms-settings:easeofaccess-colorfilter", 16299),
        new("contrast", "Catalog.Settings.contrast", "ms-settings:easeofaccess-highcontrast"),
        new("magnifier", "Catalog.Settings.magnifier", "ms-settings:easeofaccess-magnifier"),
        new("narrator", "Catalog.Settings.narrator", "ms-settings:easeofaccess-narrator"),
        new("accessibility-keyboard", "Catalog.Settings.accessibility-keyboard", "ms-settings:easeofaccess-keyboard"),
        new("accessibility-mouse", "Catalog.Settings.accessibility-mouse", "ms-settings:easeofaccess-mouse"),
        new("pointer", "Catalog.Settings.pointer", "ms-settings:easeofaccess-mousepointer", 18362),
        new("text-cursor", "Catalog.Settings.text-cursor", "ms-settings:easeofaccess-cursor", 19041),
        new("visual-effects", "Catalog.Settings.visual-effects", "ms-settings:easeofaccess-visualeffects", 22000),
    ];

    // Fixed system destinations supplement the namespace, independently of Start-menu/AdminTools shortcuts.
    private static readonly Definition[] Tools =
    [
        new("task-manager", "Catalog.Tool.task-manager", "Taskmgr.exe"),
        new("computer-management", "Catalog.Tool.computer-management", "compmgmt.msc"),
        new("device-manager", "Catalog.Tool.device-manager", "devmgmt.msc"),
        new("disk-management", "Catalog.Tool.disk-management", "diskmgmt.msc"),
        new("services", "Catalog.Tool.services", "services.msc"),
        new("events", "Catalog.Tool.events", "eventvwr.msc"),
        new("tasks", "Catalog.Tool.tasks", "taskschd.msc"),
        new("performance", "Catalog.Tool.performance", "perfmon.msc"),
        new("resources", "Catalog.Tool.resources", "resmon.exe"),
        new("system-information", "Catalog.Tool.system-information", "msinfo32.exe"),
        new("registry", "Catalog.Tool.registry", "regedt32.exe"),
        new("system-configuration", "Catalog.Tool.system-configuration", "msconfig.exe"),
        new("windows-features", "Catalog.Tool.windows-features", "OptionalFeatures.exe"),
        new("optimize", "Catalog.Tool.optimize", "dfrgui.exe"),
        new("cleanup", "Catalog.Tool.cleanup", "cleanmgr.exe"),
        new("group-policy", "Catalog.Tool.group-policy", "gpedit.msc", RequiresProfessionalEdition: true),
        new("security-policy", "Catalog.Tool.security-policy", "secpol.msc", RequiresProfessionalEdition: true),
        new("local-users", "Catalog.Tool.local-users", "lusrmgr.msc", RequiresProfessionalEdition: true),
        new("user-certificates", "Catalog.Tool.user-certificates", "certmgr.msc"),
        new("machine-certificates", "Catalog.Tool.machine-certificates", "certlm.msc"),
        new("firewall", "Catalog.Tool.firewall", "WF.msc"),
        new("component-services", "Catalog.Tool.component-services", "comexp.msc"),
        new("print-management", "Catalog.Tool.print-management", "printmanagement.msc", RequiresProfessionalEdition: true),
        new("odbc", "Catalog.Tool.odbc", "odbcad32.exe"),
        new("directx", "Catalog.Tool.directx", "dxdiag.exe"),
        new("advanced-system", "Catalog.Tool.advanced-system", "SystemPropertiesAdvanced.exe"),
        new("system-protection", "Catalog.Tool.system-protection", "SystemPropertiesProtection.exe"),
        new("remote-properties", "Catalog.Tool.remote-properties", "SystemPropertiesRemote.exe"),
        new("windows-version", "Catalog.Tool.windows-version", "winver.exe"),
    ];

    private static readonly HashSet<string> ControlPanelNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.ActionCenter", "Microsoft.AdministrativeTools", "Microsoft.AutoPlay", "Microsoft.BitLockerDriveEncryption",
        "Microsoft.ColorManagement", "Microsoft.CredentialManager", "Microsoft.DateAndTime", "Microsoft.DefaultPrograms",
        "Microsoft.DeviceManager", "Microsoft.DevicesAndPrinters", "Microsoft.EaseOfAccessCenter", "Microsoft.FileHistory",
        "Microsoft.FolderOptions", "Microsoft.Fonts", "Microsoft.IndexingOptions", "Microsoft.InternetOptions",
        "Microsoft.iSCSIInitiator", "Microsoft.Keyboard", "Microsoft.Mouse", "Microsoft.NetworkAndSharingCenter",
        "Microsoft.NotificationAreaIcons", "Microsoft.PenAndTouch", "Microsoft.Personalization", "Microsoft.PhoneAndModem",
        "Microsoft.PowerOptions", "Microsoft.ProgramsAndFeatures", "Microsoft.Recovery", "Microsoft.RegionAndLanguage",
        "Microsoft.RemoteAppAndDesktopConnections", "Microsoft.Sound", "Microsoft.SpeechRecognition", "Microsoft.StorageSpaces",
        "Microsoft.SyncCenter", "Microsoft.System", "Microsoft.TabletPCSettings", "Microsoft.Taskbar", "Microsoft.Troubleshooting",
        "Microsoft.UserAccounts", "Microsoft.WindowsDefender", "Microsoft.WindowsFirewall", "Microsoft.WindowsMobilityCenter",
        "Microsoft.WorkFolders",
    };

    private sealed record FeatureGroup(string Key, string TitleKey, int Order);
    private sealed record FeatureGroupAssignment(FeatureGroup Group, string[] ItemIds);

    private static readonly IReadOnlyDictionary<string, FeatureGroup> LocationGroups = IndexGroups(
        Group("system:locations:personal-shared-folders", "Import.SystemGroup.LocationsPersonal", 0,
            "desktop", "profile", "documents", "downloads", "pictures", "music", "videos", "startup", "public-desktop"),
        Group("system:locations:system-places", "Import.SystemGroup.LocationsPlaces", 1,
            "computer", "recycle", "network", "connections", "printers", "libraries", "control-panel", "windows-tools", "user-tools", "fonts"));

    private static readonly IReadOnlyDictionary<string, FeatureGroup> SettingsGroups = IndexGroups(
        Group("system:settings:display-sound-notifications", "Import.SystemGroup.SettingsDisplaySoundNotifications", 0,
            "home", "display", "advanced-display", "night-light", "graphics", "sound", "volume", "sound-devices", "notifications", "focus-assist", "focus", "about"),
        Group("system:settings:power-storage-operations", "Import.SystemGroup.SettingsPowerStorageOperations", 1,
            "power", "battery", "battery-settings", "battery-use", "storage", "storage-policies", "save-locations", "storage-recommendations", "storage-spaces", "multitasking", "projection", "remote-desktop", "clipboard"),
        Group("system:settings:devices-input", "Import.SystemGroup.SettingsDevicesInput", 2,
            "bluetooth", "connected-devices", "printers", "mouse", "touchpad", "pen", "typing", "hardware-typing", "autoplay", "usb", "cameras"),
        Group("system:settings:network-connectivity", "Import.SystemGroup.SettingsNetwork", 3,
            "network", "wifi", "known-networks", "ethernet", "vpn", "proxy", "airplane", "hotspot", "dialup", "advanced-network"),
        Group("system:settings:personalization", "Import.SystemGroup.SettingsPersonalization", 4,
            "background", "colors", "lock-screen", "themes", "start", "taskbar", "fonts", "text-input", "touch-keyboard"),
        Group("system:settings:accounts-sign-in", "Import.SystemGroup.SettingsAccountsSignIn", 5,
            "your-info", "email-accounts", "signin", "dynamic-lock", "work-school", "other-users", "account-sync", "backup"),
        Group("system:settings:apps-media-games", "Import.SystemGroup.SettingsAppsMediaGames", 6,
            "apps", "default-apps", "apps-websites", "optional-features", "startup-apps", "video", "offline-maps", "gamebar", "game-captures", "game-mode"),
        Group("system:settings:date-language-speech", "Import.SystemGroup.SettingsDateLanguageSpeech", 7,
            "date-time", "language", "region", "speech", "keyboard"),
        Group("system:settings:privacy-diagnostics-search", "Import.SystemGroup.SettingsPrivacyDiagnosticsSearch", 8,
            "privacy-general", "privacy-speech", "privacy-inking", "privacy-feedback", "privacy-history", "privacy-background", "privacy-app-diagnostics", "search-permissions", "search-indexing"),
        Group("system:settings:personal-data-permissions", "Import.SystemGroup.SettingsPersonalDataPermissions", 9,
            "privacy-location", "privacy-account", "privacy-contacts", "privacy-calendar", "privacy-calls", "privacy-email", "privacy-tasks", "privacy-messaging", "privacy-phone"),
        Group("system:settings:device-file-permissions", "Import.SystemGroup.SettingsDeviceFilePermissions", 10,
            "privacy-camera", "privacy-microphone", "privacy-notifications", "privacy-radios", "privacy-devices", "privacy-downloads", "privacy-documents", "privacy-pictures", "privacy-videos", "privacy-filesystem", "privacy-downloadsfolder", "privacy-voice", "privacy-screenshots"),
        Group("system:settings:update-security-recovery", "Import.SystemGroup.SettingsUpdateSecurityRecovery", 11,
            "update", "update-history", "update-options", "update-restart", "delivery", "security", "recovery", "activation", "troubleshoot", "developers"),
        Group("system:settings:accessibility", "Import.SystemGroup.SettingsAccessibility", 12,
            "accessibility-display", "accessibility-audio", "captions", "color-filters", "contrast", "magnifier", "narrator", "accessibility-keyboard", "accessibility-mouse", "pointer", "text-cursor", "visual-effects"));

    private static readonly IReadOnlyDictionary<string, FeatureGroup> ControlPanelGroups = IndexGroups(
        Group("system:control-panel:system-security", "Import.SystemGroup.ControlPanelSystemSecurity", 0,
            "Microsoft.ActionCenter", "Microsoft.AdministrativeTools", "Microsoft.BitLockerDriveEncryption", "Microsoft.CredentialManager", "Microsoft.PowerOptions", "Microsoft.Recovery", "Microsoft.System", "Microsoft.Troubleshooting", "Microsoft.UserAccounts", "Microsoft.WindowsDefender", "Microsoft.WindowsFirewall"),
        Group("system:control-panel:devices-network-sound", "Import.SystemGroup.ControlPanelDevicesNetworkSound", 1,
            "Microsoft.AutoPlay", "Microsoft.DeviceManager", "Microsoft.DevicesAndPrinters", "Microsoft.iSCSIInitiator", "Microsoft.InternetOptions", "Microsoft.NetworkAndSharingCenter", "Microsoft.PhoneAndModem", "Microsoft.RemoteAppAndDesktopConnections", "Microsoft.Sound", "Microsoft.WindowsMobilityCenter"),
        Group("system:control-panel:personalization-accessibility", "Import.SystemGroup.ControlPanelPersonalizationAccessibility", 2,
            "Microsoft.ColorManagement", "Microsoft.EaseOfAccessCenter", "Microsoft.FolderOptions", "Microsoft.Fonts", "Microsoft.Keyboard", "Microsoft.Mouse", "Microsoft.NotificationAreaIcons", "Microsoft.PenAndTouch", "Microsoft.Personalization", "Microsoft.SpeechRecognition", "Microsoft.TabletPCSettings", "Microsoft.Taskbar"),
        Group("system:control-panel:apps-storage-region", "Import.SystemGroup.ControlPanelAppsStorageRegion", 3,
            "Microsoft.DateAndTime", "Microsoft.DefaultPrograms", "Microsoft.FileHistory", "Microsoft.IndexingOptions", "Microsoft.ProgramsAndFeatures", "Microsoft.RegionAndLanguage", "Microsoft.StorageSpaces", "Microsoft.SyncCenter", "Microsoft.WorkFolders"));

    private static readonly IReadOnlyDictionary<string, FeatureGroup> ToolGroups = IndexGroups(
        Group("system:tools:administration-monitoring-diagnostics", "Import.SystemGroup.ToolsAdministrationMonitoringDiagnostics", 0,
            "task-manager", "computer-management", "device-manager", "disk-management", "services", "events", "tasks", "performance", "resources", "system-information", "system-configuration", "windows-version"),
        Group("system:tools:security-policy-credentials", "Import.SystemGroup.ToolsSecurityPolicyCredentials", 1,
            "registry", "group-policy", "security-policy", "local-users", "user-certificates", "machine-certificates", "firewall", "component-services"),
        Group("system:tools:maintenance-components-utilities", "Import.SystemGroup.ToolsMaintenanceComponentsUtilities", 2,
            "windows-features", "optimize", "cleanup", "print-management", "odbc", "directx", "advanced-system", "system-protection", "remote-properties", "run", "taskbar-search"));

    static SystemFeatureCatalog()
    {
        ValidateGroupCoverage(Locations, LocationGroups, nameof(Locations));
        ValidateGroupCoverage(Settings, SettingsGroups, nameof(Settings));
        ValidateGroupCoverage(Tools, ToolGroups, nameof(Tools), "run", "taskbar-search");
        if (!ControlPanelNames.SetEquals(ControlPanelGroups.Keys))
            throw new InvalidOperationException("Every supported Control Panel canonical name must have exactly one semantic group.");
    }

    // Capture XAML resources on the caller's UI thread before the catalog's background scan.
    internal static IReadOnlyDictionary<string, string> CaptureNames() => Locations.Concat(Settings).Concat(Tools)
        .Select(definition => definition.NameKey).Concat(["Catalog.Location.run", "Catalog.Tool.taskbar-search"])
        .Distinct(StringComparer.Ordinal).ToDictionary(key => key, ProductStrings.Get, StringComparer.Ordinal);

    internal static IReadOnlyList<NativeImportEntry> Read(CancellationToken token, IReadOnlyDictionary<string, string> names)
    {
        var result = new List<NativeImportEntry>();
        foreach (var definition in Locations) AddShell(definition, LocationGroups[definition.Id]);
        var build = Environment.OSVersion.Version.Build;
        var hasWifi = HasWifi();
        var hasBattery = HasBattery();
        foreach (var definition in Settings)
        {
            token.ThrowIfCancellationRequested();
            bool? hardware = definition.Hardware switch { "wifi" => hasWifi, "battery" => hasBattery, _ => true };
            var applicable = build >= definition.MinimumBuild && build <= definition.MaximumBuild && hardware != false;
            var available = applicable && hardware == true && RegisteredUrlProtocols.IsSupported(definition.Target);
            result.Add(CreateEntry(NativeImportCatalog.Key("system|settings|" + definition.Id), Name(definition), definition.Target,
                NativeImportTarget.Uri, SettingsGroups[definition.Id], false,
                error: !applicable ? "SourceNotApplicable" : available ? null : "SourceUnavailable"));
        }
        var professionalTools = HasProfessionalToolsEdition();
        foreach (var definition in Tools)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), definition.Target);
            string? error = definition.RequiresProfessionalEdition && !professionalTools ? "SourceUnavailable" : null;
            try { if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) error = "SourceUnavailable"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { error = "SourceUnavailable"; }
            result.Add(CreateEntry(NativeImportCatalog.Key("system|tool|" + definition.Id), Name(definition), path,
                NativeImportTarget.Path, ToolGroups[definition.Id], false, error: error));
        }
        AddShell(new("run", "Catalog.Location.run", "shell:::{2559A1F3-21D7-11D4-BDAF-00C04F60B9F0}"), ToolGroups["run"]);
        // The main integration verifies this native protocol actually presents taskbar Search, not Explorer search.
        result.Add(CreateEntry(NativeImportCatalog.Key("system|taskbar-search"), names["Catalog.Tool.taskbar-search"], "ms-search://search/",
            NativeImportTarget.Uri, ToolGroups["taskbar-search"], false,
            error: RegisteredUrlProtocols.IsSupported("ms-search:") ? null : "SourceUnavailable"));
        try
        {
            foreach (var item in ShellItemCatalog.Enumerate("shell:ControlPanelFolder", token))
            {
                token.ThrowIfCancellationRequested();
                if (ControlPanelName(item.ParsingName) is not { } canonical || !ControlPanelNames.Contains(canonical)) continue;
                result.Add(CreateEntry(NativeImportCatalog.Key("system|control-panel|" + canonical), item.Name, item.ParsingName,
                    NativeImportTarget.Shell, ControlPanelGroups[canonical], item.IsFolder,
                    identity: ShellItemCatalog.ReadForLink(item.ParsingName).Identity));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException)
        {
            result.Add(CreateEntry(NativeImportCatalog.Key("system|control-panel-unavailable"), names["Catalog.Location.control-panel"],
                "shell:ControlPanelFolder", NativeImportTarget.Shell, ControlPanelGroups["Microsoft.ActionCenter"], true,
                error: "SourceUnavailable"));
        }
        return result.DistinctBy(entry => entry.Key).ToArray();

        string Name(Definition definition) => names[definition.NameKey];
        void AddShell(Definition definition, FeatureGroup group)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var (actual, identity) = ShellItemCatalog.ReadForLink(definition.Target);
                result.Add(CreateEntry(NativeImportCatalog.Key("system|location|" + definition.Id), Name(definition), definition.Target,
                    NativeImportTarget.Shell, group, actual.IsFolder,
                    identity: identity));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException)
            {
                result.Add(CreateEntry(NativeImportCatalog.Key("system|location|" + definition.Id), Name(definition), definition.Target,
                    NativeImportTarget.Shell, group, true, error: "SourceUnavailable"));
            }
        }
    }

    private static NativeImportEntry CreateEntry(string key, string name, string target, NativeImportTarget kind,
        FeatureGroup group, bool directory, LaunchIdentity? identity = null, string? error = null) =>
        new(key, name, target, kind, group.Key, true, directory, Identity: identity, Error: error)
        {
            GroupTitleKey = group.TitleKey,
            GroupOrder = group.Order,
        };

    private static FeatureGroupAssignment Group(string key, string titleKey, int order, params string[] itemIds) =>
        new(new(key, titleKey, order), itemIds);

    private static IReadOnlyDictionary<string, FeatureGroup> IndexGroups(params FeatureGroupAssignment[] assignments)
    {
        var groups = new Dictionary<string, FeatureGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in assignments)
            foreach (var id in assignment.ItemIds)
                if (!groups.TryAdd(id, assignment.Group))
                    throw new InvalidOperationException($"Catalog item '{id}' belongs to more than one semantic group.");
        return groups;
    }

    private static void ValidateGroupCoverage(IEnumerable<Definition> definitions,
        IReadOnlyDictionary<string, FeatureGroup> groups, string catalogName, params string[] extraIds)
    {
        var ids = definitions.Select(definition => definition.Id).Concat(extraIds).ToArray();
        var expected = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expected.Count != ids.Length || expected.Count != groups.Count || !expected.SetEquals(groups.Keys))
            throw new InvalidOperationException($"Every {catalogName} catalog item must have exactly one semantic group.");
    }

    private static string? ControlPanelName(string parsingName)
    {
        foreach (Match match in Regex.Matches(parsingName, @"\{[0-9a-fA-F-]{36}\}").Cast<Match>().Reverse())
            try
            {
                using var key = Registry.ClassesRoot.OpenSubKey("CLSID\\" + match.Value);
                if (key?.GetValue("System.ApplicationName") is string name) return name;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
        return null;
    }

    private static bool? HasWifi()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces().Any(item => item.NetworkInterfaceType == NetworkInterfaceType.Wireless80211); }
        catch (NetworkInformationException) { return null; }
    }

    private static bool? HasBattery() => GetSystemPowerStatus(out var status) && status.BatteryFlag != 255
        ? (status.BatteryFlag & 128) == 0 : null;

    private static bool HasProfessionalToolsEdition()
    {
        // GetProductInfo uses SDK PRODUCT_* values, not localized registry product names.
        // This is a conservative desktop-edition gate, followed by the tool's actual file check.
        // Home/Core/Starter, unknown SKUs and a failed query cannot enable these snap-ins.
        // It does not prove optional features, policy or a tool's runtime availability.
        if (!GetProductInfo(10, 0, 0, 0, out var product)) return false;
        return product is
            0x30 or 0x31 or                         // PROFESSIONAL / PROFESSIONAL_N
            0xA1 or 0xA2 or 0xA4 or 0xA5 or         // PRO_WORKSTATION[_N] / PRO_FOR_EDUCATION[_N]
            0x79 or 0x7A or                         // EDUCATION / EDUCATION_N
            0x04 or 0x1B or 0x48 or 0x54 or         // ENTERPRISE[_N] / their evaluation editions
            0x7D or 0x7E or 0x81 or 0x82 or         // ENTERPRISE_S[_N] / their evaluation editions
            0x8C or 0x8D or 0xAB or 0xAC;           // ENTERPRISE_SUBSCRIPTION[_N] / ENTERPRISEG[N]
    }

    // Windows SDK 10.0.26100.0: SysInfoApi.h GetProductInfo / WinNT.h PRODUCT_*.
    // https://learn.microsoft.com/windows/win32/api/sysinfoapi/nf-sysinfoapi-getproductinfo
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProductInfo(uint major, uint minor, uint servicePackMajor, uint servicePackMinor, out uint product);

    // Windows SDK 10.0.26100.0, WinBase.h: SYSTEM_POWER_STATUS / GetSystemPowerStatus.
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
