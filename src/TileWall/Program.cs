namespace TileWall;

/// <summary>
/// 自有入口（M9 设计 §1.2）：csproj 定义 DISABLE_XAML_GENERATED_MAIN 后，XAML 生成 Main 让位于本类。
/// 管理命令在 WinUI 初始化前处理；普通命令行（含 --background / --add）按生成代码的原序列启动应用。
/// 打包版 Explorer 命令由独立原生 DLL 承担，只通过系统应用激活进入 --add。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (ApplicationModel.ContextMenuUserCommand.IsRequested(args))
        {
            // Retired and mixed helper requests must fail before COM, RuntimeScope, and XAML initialization.
            if (!ApplicationModel.ContextMenuUserCommand.TryParse(args, out _))
                return ApplicationModel.ContextMenuUserCommand.InvalidArguments;
#if TILEWALL_PACKAGED
            // The package owns this user's menu registration; only unpackaged distributions use HKCU verbs.
            return ApplicationModel.ContextMenuUserCommand.OperationFailed;
#else
            return ApplicationModel.ContextMenuUserCommand.Run(args);
#endif
        }
        if (ApplicationModel.CurrentUserUninstallCommand.IsRequested(args))
        {
            if (!ApplicationModel.CurrentUserUninstallCommand.TryParse(args, out _))
                return ApplicationModel.ContextMenuUserCommand.InvalidArguments;
#if TILEWALL_PACKAGED
            return ApplicationModel.ContextMenuUserCommand.OperationFailed;
#else
            return ApplicationModel.CurrentUserUninstallCommand.Run(args, Shell.Interop.ExplorerAddNotification.UnregisterForUninstall);
#endif
        }
        try { return RunApplication(args); }
        catch (Exception ex)
        {
            if (Core.Shell.AppLog.IsInitialized)
            {
                Core.Shell.AppLog.Error("Startup", ex.ToString());
                Core.Shell.AppLog.Shutdown();
            }
            else Shell.AppLogSetup.WriteWindowsEventLogError($"TileWall startup: {ex}");
            return 1;
        }
    }

    private static int RunApplication(string[] args)
    {
        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        var startupTask = false;
        var notificationActivation = Shell.Interop.ExplorerAddNotification.IsActivation(args);
        // COM notification activation carries no import payload. Register its sink only
        // after acquiring our existing single-instance gate; GetActivatedEventArgs can
        // otherwise wait for an AppNotificationManager registration that does not exist yet.
        if (notificationActivation) args = Array.Empty<string>();
#if TILEWALL_PACKAGED
        // Packaged activation arguments are consumable only once; read before XAML startup.
        var activation = notificationActivation ? null : global::Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
        startupTask = activation?.Kind == global::Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask;
        if (activation is not null && activation.Kind == global::Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Launch &&
            activation.Data is global::Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch &&
            !string.IsNullOrWhiteSpace(launch.Arguments))
            args = ApplicationModel.StartupIntent.ReadLaunchArguments(launch.Arguments);
#endif
        var scope = ApplicationModel.RuntimeScope.FromArguments(args);
        var intent = ApplicationModel.StartupIntent.Parse(args, startupTask) with { NotificationActivation = notificationActivation };

        // Preserve the generated Main's XAML dispatcher/synchronization-context startup sequence.
        global::Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(scope, intent);
        });
        return 0;
    }

}
