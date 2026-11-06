using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TileWall.Core.Entries;
using TileWall.Core.Wall;
using TileWall.Resources;
using TileWall.Shell.Interop;

namespace TileWall.ApplicationModel;

/// <summary>Native Windows destinations only. Reading this catalog never launches a destination.</summary>
internal static class SystemFeatureCatalog
{
    private sealed record Definition(string Id, string Chinese, string English, string Target,
        int MinimumBuild = 10240, int MaximumBuild = int.MaxValue, string? Hardware = null,
        bool RequiresProfessionalEdition = false);

    // Shell's registered canonical names resolve redirection and per-instance identity.
    private static readonly Definition[] Locations =
    [
        new("computer", "此电脑", "This PC", "shell:MyComputerFolder"),
        new("recycle", "回收站", "Recycle Bin", "shell:RecycleBinFolder"),
        new("desktop", "桌面", "Desktop", "shell:Desktop"),
        new("profile", "用户目录", "User profile", "shell:Profile"),
        new("documents", "文档", "Documents", "shell:Personal"),
        new("downloads", "下载", "Downloads", "shell:Downloads"),
        new("pictures", "图片", "Pictures", "shell:My Pictures"),
        new("music", "音乐", "Music", "shell:My Music"),
        new("videos", "视频", "Videos", "shell:My Video"),
        new("network", "网络", "Network", "shell:NetworkPlacesFolder"),
        new("connections", "网络连接", "Network connections", "shell:ConnectionsFolder"),
        new("printers", "打印机", "Printers", "shell:PrintersFolder"),
        new("libraries", "库", "Libraries", "shell:Libraries"),
        new("control-panel", "控制面板", "Control Panel", "shell:ControlPanelFolder"),
        new("windows-tools", "Windows 工具", "Windows Tools", "shell:Common Administrative Tools"),
        new("user-tools", "用户管理工具", "User administrative tools", "shell:Administrative Tools"),
        new("fonts", "字体", "Fonts", "shell:Fonts"),
        new("startup", "启动文件夹", "Startup folder", "shell:Startup"),
        new("public-desktop", "公共桌面", "Public desktop", "shell:Common Desktop"),
    ];

    // Public desktop ms-settings pages. Parameterized templates and mobile-only pages are deliberately absent.
    // Availability conditions are part of the definition; protocol registration alone proves no page exists.
    // Reference: https://learn.microsoft.com/windows/apps/develop/launch/launch-settings
    private static readonly Definition[] Settings =
    [
        new("home", "设置", "Settings", "ms-settings:"),
        new("display", "显示", "Display", "ms-settings:display"),
        new("advanced-display", "高级显示", "Advanced display", "ms-settings:display-advanced"),
        new("night-light", "夜间模式", "Night light", "ms-settings:nightlight", 15063),
        new("graphics", "图形设置", "Graphics settings", "ms-settings:display-advancedgraphics", 17134),
        new("sound", "声音", "Sound", "ms-settings:sound", 17134),
        new("volume", "音量合成器", "Volume mixer", "ms-settings:apps-volume", 17134),
        new("sound-devices", "声音设备", "Sound devices", "ms-settings:sound-devices", 17763),
        new("notifications", "通知", "Notifications", "ms-settings:notifications"),
        new("focus-assist", "专注助手", "Focus assist", "ms-settings:quiethours", 17134, 21999),
        new("focus", "专注", "Focus", "ms-settings:quietmomentshome", 22000),
        new("power", "电源和睡眠", "Power and sleep", "ms-settings:powersleep"),
        new("battery", "节电模式", "Battery saver", "ms-settings:batterysaver", Hardware: "battery"),
        new("battery-settings", "节电模式设置", "Battery saver settings", "ms-settings:batterysaver-settings", Hardware: "battery"),
        new("battery-use", "电池使用情况", "Battery usage", "ms-settings:batterysaver-usagedetails", Hardware: "battery"),
        new("storage", "存储", "Storage", "ms-settings:storagesense"),
        new("storage-policies", "存储感知", "Storage Sense", "ms-settings:storagepolicies", 16299),
        new("save-locations", "新内容的保存位置", "Where new content is saved", "ms-settings:savelocations"),
        new("storage-recommendations", "清理建议", "Cleanup recommendations", "ms-settings:storagerecommendations", 22000),
        new("storage-spaces", "存储空间", "Storage Spaces", "ms-settings:storagespaces", 22000),
        new("multitasking", "多任务处理", "Multitasking", "ms-settings:multitasking"),
        new("projection", "投影到此电脑", "Projecting to this PC", "ms-settings:project", 14393),
        new("remote-desktop", "远程桌面", "Remote Desktop", "ms-settings:remotedesktop", 16299),
        new("clipboard", "剪贴板", "Clipboard", "ms-settings:clipboard", 17763),
        new("about", "系统信息", "About", "ms-settings:about"),
        new("bluetooth", "蓝牙", "Bluetooth", "ms-settings:bluetooth"),
        new("connected-devices", "已连接设备", "Connected devices", "ms-settings:connecteddevices"),
        new("printers", "打印机和扫描仪", "Printers and scanners", "ms-settings:printers"),
        new("mouse", "鼠标", "Mouse", "ms-settings:mousetouchpad"),
        new("touchpad", "触摸板", "Touchpad", "ms-settings:devices-touchpad", 15063),
        new("pen", "笔和 Windows Ink", "Pen and Windows Ink", "ms-settings:pen", 14393),
        new("typing", "输入", "Typing", "ms-settings:typing"),
        new("hardware-typing", "硬件键盘文本建议", "Hardware keyboard text suggestions", "ms-settings:devicestyping-hwkbtextsuggestions", 17134),
        new("autoplay", "自动播放", "AutoPlay", "ms-settings:autoplay"),
        new("usb", "USB", "USB", "ms-settings:usb"),
        new("cameras", "摄像头", "Cameras", "ms-settings:camera", 22000),
        new("network", "网络和 Internet", "Network and Internet", "ms-settings:network-status"),
        new("wifi", "Wi-Fi", "Wi-Fi", "ms-settings:network-wifi", Hardware: "wifi"),
        new("known-networks", "管理已知网络", "Manage known networks", "ms-settings:network-wifisettings", Hardware: "wifi"),
        new("ethernet", "以太网", "Ethernet", "ms-settings:network-ethernet"),
        new("vpn", "VPN", "VPN", "ms-settings:network-vpn"),
        new("proxy", "代理", "Proxy", "ms-settings:network-proxy"),
        new("airplane", "飞行模式", "Airplane mode", "ms-settings:network-airplanemode"),
        new("hotspot", "移动热点", "Mobile hotspot", "ms-settings:network-mobilehotspot", 14393, Hardware: "wifi"),
        new("dialup", "拨号", "Dial-up", "ms-settings:network-dialup"),
        new("advanced-network", "高级网络设置", "Advanced network settings", "ms-settings:network-advancedsettings", 22000),
        new("background", "桌面背景", "Desktop background", "ms-settings:personalization-background"),
        new("colors", "颜色", "Colors", "ms-settings:personalization-colors"),
        new("lock-screen", "锁屏界面", "Lock screen", "ms-settings:lockscreen"),
        new("themes", "主题", "Themes", "ms-settings:themes"),
        new("start", "开始菜单设置", "Start settings", "ms-settings:personalization-start"),
        new("taskbar", "任务栏设置", "Taskbar settings", "ms-settings:taskbar"),
        new("fonts", "字体设置", "Font settings", "ms-settings:fonts", 17134),
        new("text-input", "文本输入个性化", "Text input personalization", "ms-settings:personalization-textinput", 22000),
        new("touch-keyboard", "触摸键盘外观", "Touch keyboard appearance", "ms-settings:personalization-touchkeyboard", 22000),
        new("your-info", "账户信息", "Your info", "ms-settings:yourinfo"),
        new("email-accounts", "电子邮件和账户", "Email and accounts", "ms-settings:emailandaccounts"),
        new("signin", "登录选项", "Sign-in options", "ms-settings:signinoptions"),
        new("dynamic-lock", "动态锁", "Dynamic lock", "ms-settings:signinoptions-dynamiclock", 15063),
        new("work-school", "访问工作或学校账户", "Access work or school", "ms-settings:workplace"),
        new("other-users", "其他用户", "Other users", "ms-settings:otherusers"),
        new("account-sync", "同步设置", "Sync settings", "ms-settings:sync", MaximumBuild: 21999),
        new("backup", "Windows 备份", "Windows backup", "ms-settings:backup", 22000),
        new("apps", "已安装应用", "Installed apps", "ms-settings:appsfeatures"),
        new("default-apps", "默认应用", "Default apps", "ms-settings:defaultapps"),
        new("apps-websites", "网站的应用", "Apps for websites", "ms-settings:appsforwebsites", 14393),
        new("optional-features", "可选功能", "Optional features", "ms-settings:optionalfeatures"),
        new("startup-apps", "启动应用", "Startup apps", "ms-settings:startupapps", 17134),
        new("video", "视频播放", "Video playback", "ms-settings:videoplayback", 16299),
        new("offline-maps", "离线地图", "Offline maps", "ms-settings:maps"),
        new("date-time", "日期和时间", "Date and time", "ms-settings:dateandtime"),
        new("language", "语言", "Language", "ms-settings:regionlanguage"),
        new("region", "区域格式", "Regional format", "ms-settings:regionformatting", 17134),
        new("speech", "语音", "Speech", "ms-settings:speech"),
        new("keyboard", "高级键盘设置", "Advanced keyboard settings", "ms-settings:keyboard-advanced", 17134),
        new("gamebar", "游戏栏", "Game Bar", "ms-settings:gaming-gamebar", 15063),
        new("game-captures", "游戏录制", "Game captures", "ms-settings:gaming-gamedvr", 15063),
        new("game-mode", "游戏模式", "Game Mode", "ms-settings:gaming-gamemode", 15063),
        new("privacy-general", "常规隐私", "General privacy", "ms-settings:privacy-general"),
        new("privacy-location", "位置权限", "Location permissions", "ms-settings:privacy-location"),
        new("privacy-camera", "相机权限", "Camera permissions", "ms-settings:privacy-webcam"),
        new("privacy-microphone", "麦克风权限", "Microphone permissions", "ms-settings:privacy-microphone"),
        new("privacy-notifications", "通知权限", "Notification permissions", "ms-settings:privacy-notifications", 14393),
        new("privacy-speech", "语音隐私", "Speech privacy", "ms-settings:privacy-speech", 17763),
        new("privacy-inking", "墨迹和键入个性化", "Inking and typing personalization", "ms-settings:privacy-speechtyping"),
        new("privacy-feedback", "诊断和反馈", "Diagnostics and feedback", "ms-settings:privacy-feedback"),
        new("privacy-history", "活动历史记录", "Activity history", "ms-settings:privacy-activityhistory", 17134),
        new("privacy-account", "账户信息权限", "Account information permissions", "ms-settings:privacy-accountinfo"),
        new("privacy-contacts", "联系人权限", "Contacts permissions", "ms-settings:privacy-contacts"),
        new("privacy-calendar", "日历权限", "Calendar permissions", "ms-settings:privacy-calendar"),
        new("privacy-calls", "通话记录权限", "Call history permissions", "ms-settings:privacy-callhistory"),
        new("privacy-email", "电子邮件权限", "Email permissions", "ms-settings:privacy-email"),
        new("privacy-tasks", "任务权限", "Tasks permissions", "ms-settings:privacy-tasks", 14393),
        new("privacy-messaging", "消息权限", "Messaging permissions", "ms-settings:privacy-messaging"),
        new("privacy-radios", "无线电权限", "Radio permissions", "ms-settings:privacy-radios"),
        new("privacy-devices", "其他设备权限", "Other device permissions", "ms-settings:privacy-customdevices"),
        new("privacy-background", "后台应用", "Background apps", "ms-settings:privacy-backgroundapps", MaximumBuild: 21999),
        new("privacy-app-diagnostics", "应用诊断权限", "App diagnostics permissions", "ms-settings:privacy-appdiagnostics", 15063),
        new("privacy-downloads", "自动文件下载", "Automatic file downloads", "ms-settings:privacy-automaticfiledownloads", 16299),
        new("privacy-documents", "文档权限", "Documents permissions", "ms-settings:privacy-documents", 17134),
        new("privacy-pictures", "图片权限", "Pictures permissions", "ms-settings:privacy-pictures", 17134),
        new("privacy-videos", "视频权限", "Videos permissions", "ms-settings:privacy-videos", 17134),
        new("privacy-filesystem", "文件系统权限", "File system permissions", "ms-settings:privacy-broadfilesystemaccess", 17134),
        new("privacy-downloadsfolder", "下载文件夹权限", "Downloads folder permissions", "ms-settings:privacy-downloadsfolder", 18362),
        new("privacy-phone", "电话权限", "Phone call permissions", "ms-settings:privacy-phonecalls", 17763),
        new("privacy-voice", "语音激活权限", "Voice activation permissions", "ms-settings:privacy-voiceactivation", 18362),
        new("privacy-screenshots", "屏幕截图权限", "Screenshot permissions", "ms-settings:privacy-screenshotborders", 19041),
        new("search-permissions", "搜索权限", "Search permissions", "ms-settings:search-permissions", 18362),
        new("search-indexing", "搜索索引设置", "Search indexing settings", "ms-settings:search", 18362),
        new("update", "Windows 更新", "Windows Update", "ms-settings:windowsupdate"),
        new("update-history", "更新历史记录", "Update history", "ms-settings:windowsupdate-history"),
        new("update-options", "更新高级选项", "Advanced update options", "ms-settings:windowsupdate-options"),
        new("update-restart", "更新重启选项", "Update restart options", "ms-settings:windowsupdate-restartoptions"),
        new("delivery", "传递优化", "Delivery Optimization", "ms-settings:delivery-optimization", 14393),
        new("security", "Windows 安全中心", "Windows Security", "ms-settings:windowsdefender"),
        new("recovery", "恢复", "Recovery", "ms-settings:recovery"),
        new("activation", "激活", "Activation", "ms-settings:activation"),
        new("troubleshoot", "疑难解答", "Troubleshoot", "ms-settings:troubleshoot", 15063),
        new("developers", "开发者选项", "For developers", "ms-settings:developers"),
        new("accessibility-display", "辅助显示", "Accessibility display", "ms-settings:easeofaccess-display", 17134),
        new("accessibility-audio", "辅助音频", "Accessibility audio", "ms-settings:easeofaccess-audio", 17134),
        new("captions", "字幕", "Captions", "ms-settings:easeofaccess-closedcaptioning"),
        new("color-filters", "颜色滤镜", "Color filters", "ms-settings:easeofaccess-colorfilter", 16299),
        new("contrast", "高对比度", "High contrast", "ms-settings:easeofaccess-highcontrast"),
        new("magnifier", "放大镜设置", "Magnifier settings", "ms-settings:easeofaccess-magnifier"),
        new("narrator", "讲述人设置", "Narrator settings", "ms-settings:easeofaccess-narrator"),
        new("accessibility-keyboard", "辅助键盘", "Accessibility keyboard", "ms-settings:easeofaccess-keyboard"),
        new("accessibility-mouse", "辅助鼠标", "Accessibility mouse", "ms-settings:easeofaccess-mouse"),
        new("pointer", "鼠标指针", "Mouse pointer", "ms-settings:easeofaccess-mousepointer", 18362),
        new("text-cursor", "文本光标", "Text cursor", "ms-settings:easeofaccess-cursor", 19041),
        new("visual-effects", "视觉效果", "Visual effects", "ms-settings:easeofaccess-visualeffects", 22000),
    ];

    // Fixed system destinations supplement the namespace, independently of Start-menu/AdminTools shortcuts.
    private static readonly Definition[] Tools =
    [
        new("task-manager", "任务管理器", "Task Manager", "Taskmgr.exe"),
        new("computer-management", "计算机管理", "Computer Management", "compmgmt.msc"),
        new("device-manager", "设备管理器", "Device Manager", "devmgmt.msc"),
        new("disk-management", "磁盘管理", "Disk Management", "diskmgmt.msc"),
        new("services", "服务", "Services", "services.msc"),
        new("events", "事件查看器", "Event Viewer", "eventvwr.msc"),
        new("tasks", "任务计划程序", "Task Scheduler", "taskschd.msc"),
        new("performance", "性能监视器", "Performance Monitor", "perfmon.msc"),
        new("resources", "资源监视器", "Resource Monitor", "resmon.exe"),
        new("system-information", "系统信息", "System Information", "msinfo32.exe"),
        new("registry", "注册表编辑器", "Registry Editor", "regedt32.exe"),
        new("system-configuration", "系统配置", "System Configuration", "msconfig.exe"),
        new("windows-features", "Windows 功能", "Windows Features", "OptionalFeatures.exe"),
        new("optimize", "优化驱动器", "Optimize Drives", "dfrgui.exe"),
        new("cleanup", "磁盘清理", "Disk Cleanup", "cleanmgr.exe"),
        new("group-policy", "本地组策略编辑器", "Local Group Policy Editor", "gpedit.msc", RequiresProfessionalEdition: true),
        new("security-policy", "本地安全策略", "Local Security Policy", "secpol.msc", RequiresProfessionalEdition: true),
        new("local-users", "本地用户和组", "Local Users and Groups", "lusrmgr.msc", RequiresProfessionalEdition: true),
        new("user-certificates", "当前用户证书", "Current user certificates", "certmgr.msc"),
        new("machine-certificates", "本地计算机证书", "Local computer certificates", "certlm.msc"),
        new("firewall", "高级安全 Windows 防火墙", "Windows Firewall with Advanced Security", "WF.msc"),
        new("component-services", "组件服务", "Component Services", "comexp.msc"),
        new("print-management", "打印管理", "Print Management", "printmanagement.msc", RequiresProfessionalEdition: true),
        new("odbc", "ODBC 数据源", "ODBC Data Sources", "odbcad32.exe"),
        new("directx", "DirectX 诊断工具", "DirectX Diagnostic Tool", "dxdiag.exe"),
        new("advanced-system", "高级系统设置", "Advanced system settings", "SystemPropertiesAdvanced.exe"),
        new("system-protection", "系统保护", "System Protection", "SystemPropertiesProtection.exe"),
        new("remote-properties", "远程系统属性", "Remote system properties", "SystemPropertiesRemote.exe"),
        new("windows-version", "Windows 版本", "Windows version", "winver.exe"),
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

    internal static IReadOnlyList<NativeImportEntry> Read(CancellationToken token)
    {
        var chinese = ProductStrings.Language == WallLanguageMode.Chinese || ProductStrings.Language == WallLanguageMode.System &&
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
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
        AddShell(new("run", "运行", "Run", "shell:::{2559A1F3-21D7-11D4-BDAF-00C04F60B9F0}"), ToolGroups["run"]);
        // The main integration verifies this native protocol actually presents taskbar Search, not Explorer search.
        result.Add(CreateEntry(NativeImportCatalog.Key("system|taskbar-search"), chinese ? "Windows 搜索" : "Windows Search", "ms-search://search/",
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
            result.Add(CreateEntry(NativeImportCatalog.Key("system|control-panel-unavailable"), chinese ? "控制面板" : "Control Panel",
                "shell:ControlPanelFolder", NativeImportTarget.Shell, ControlPanelGroups["Microsoft.ActionCenter"], true,
                error: "SourceUnavailable"));
        }
        return result.DistinctBy(entry => entry.Key).ToArray();

        string Name(Definition definition) => chinese ? definition.Chinese : definition.English;
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
