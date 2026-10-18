using Microsoft.UI.Xaml;
using TileWall.ApplicationModel;
using TileWall.Core.Shell;
using TileWall.Shell.Interop;

namespace TileWall;

/// <summary>Single composition point for the 0.2.1 window and its process scope.</summary>
public partial class App : Application
{
    private readonly RuntimeScope _scope;
    private readonly StartupIntent _intent;
    private WallApplicationLifetime? _lifetime;
    private bool _ownsInstance;

    internal App(RuntimeScope scope, StartupIntent intent)
    {
        _scope = scope;
        _intent = intent;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            var message = e.Exception?.ToString() ?? e.Message;
            if (_ownsInstance)
            {
                AppLog.Error("UnhandledXaml", message);
                AppLog.Flush();
            }
            else Shell.AppLogSetup.WriteWindowsEventLogError($"TileWall startup: {message}");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        SingleInstanceGate? gate = null;
        try
        {
            gate = SingleInstanceGate.Acquire(_scope.InstanceMutexName, _scope.ShellWindowName,
                !_intent.Background && _intent.Add is null, out var acquired);
            if (acquired != SingleInstanceAcquireResult.Acquired)
            {
                if (_intent.Add is { } add && !gate.PostPaths(add))
                    Shell.AppLogSetup.WriteWindowsEventLogError("TileWall：外部添加请求未送达；请重试。");
                else if (acquired == SingleInstanceAcquireResult.GaveUp)
                    Shell.AppLogSetup.WriteWindowsEventLogError("TileWall：未能取得单实例所有权或找到既有实例。");
                gate.Dispose();
                Exit();
                return;
            }
            _ownsInstance = true;
            Shell.AppLogSetup.InitializeForCurrentProcess(_scope.LogRoot);
            _lifetime = new WallApplicationLifetime(_scope, gate, this);
            _ = _lifetime.StartAsync(_intent);
        }
        catch (Exception ex)
        {
            if (_ownsInstance)
            {
                AppLog.Error("Startup", ex.ToString());
                AppLog.Shutdown();
            }
            else Shell.AppLogSetup.WriteWindowsEventLogError($"TileWall startup: {ex}");
            gate?.Dispose();
            Exit();
        }
    }
}
