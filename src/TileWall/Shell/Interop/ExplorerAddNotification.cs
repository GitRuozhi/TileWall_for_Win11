using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using TileWall.Core.Shell;
using TileWall.Resources;

namespace TileWall.Shell.Interop;

/// <summary>Optional local completion feedback; never owns or changes an import transaction.</summary>
internal sealed class ExplorerAddNotification : IDisposable
{
    // Windows App SDK's COM launch marker; the MSIX ExeServer uses the same value.
    private const string ActivationArgument = "----AppNotificationActivated:";
    private const string ActionKey = "action";
    private const string ShowAction = "show-wall";
    private readonly bool _disabled;
    private readonly Action _showWall;
    private AppNotificationManager? _manager;

    internal ExplorerAddNotification(bool disabled, Action showWall)
    {
        _disabled = disabled;
        _showWall = showWall;
    }

    internal static bool IsActivation(string[] arguments) => arguments.Any(value =>
        value.StartsWith(ActivationArgument, StringComparison.Ordinal));

    internal void Register(bool notificationActivation)
    {
        if (_disabled) return; // Isolated test roots must not replace the real executable's registration.
        try
        {
            if (!AppNotificationManager.IsSupported()) return;
            var manager = AppNotificationManager.Default;
            _manager = manager;
            manager.NotificationInvoked += OnNotificationInvoked;
#if TILEWALL_PACKAGED
            manager.Register(); // Package COM registration is owned and removed by Windows.
#else
            // Windows App SDK registers activation for this executable and current user.
            manager.Register("TileWall", new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.scale-200.png")));
#endif
            if (notificationActivation)
            {
                // Register before consuming COM activation. Depending on the runtime,
                // arguments arrive here or through NotificationInvoked; Show is idempotent.
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                if (activation.Kind == ExtendedActivationKind.AppNotification &&
                    activation.Data is AppNotificationActivatedEventArgs args)
                    OnNotificationInvoked(manager, args);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("ExternalAddNotification", $"Registration unavailable: {ex.Message}");
            Dispose();
        }
    }

    internal void ShowAdded(int addedCount, bool partial)
    {
        if (_manager is not { } manager || addedCount <= 0) return;
        try
        {
            if (manager.Setting != AppNotificationSetting.Enabled) return;
            var builder = new AppNotificationBuilder()
                .AddArgument(ActionKey, ShowAction)
                .AddText("TileWall")
                .AddText(string.Format(ProductStrings.Get("Import.NotificationAdded"), addedCount));
            builder.AddText((partial ? ProductStrings.Get("Import.NotificationPartial") + " " : string.Empty)
                + ProductStrings.Get("Import.NotificationView"));
            var notification = builder.BuildNotification();
            manager.Show(notification);
            AppLog.Info("ExternalAddNotification", $"Submitted completion notification for {addedCount} added tiles (partial={partial}, id={notification.Id}).");
        }
        catch (Exception ex) { AppLog.Warn("ExternalAddNotification", $"Delivery unavailable: {ex.Message}"); }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue(ActionKey, out var action) && action == ShowAction)
            _showWall(); // The callback only queues the lifetime's explicit Show request.
    }

    public void Dispose()
    {
        if (_manager is not { } manager) return;
        _manager = null;
        try { manager.NotificationInvoked -= OnNotificationInvoked; }
        catch (Exception ex) { AppLog.Warn("ExternalAddNotification", $"Handler cleanup: {ex.Message}"); }
        // Keep persistent COM activation so an existing notification can cold-start
        // this executable. UnregisterAll belongs to uninstall, never normal exit.
        try { manager.Unregister(); }
        catch (Exception ex) { AppLog.Warn("ExternalAddNotification", $"Unregister: {ex.Message}"); }
    }

    internal static void UnregisterForUninstall()
    {
        try
        {
            global::WinRT.ComWrappersSupport.InitializeComWrappers();
            AppNotificationManager.Default.UnregisterAll();
        }
        catch (Exception) { } // Notification cleanup must not prevent file uninstall.
    }
}
