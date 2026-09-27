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

    internal App(RuntimeScope scope, StartupIntent intent)
    {
        _scope = scope;
        _intent = intent;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var gate = SingleInstanceGate.Acquire(_scope.InstanceMutexName, _scope.ShellWindowName,
            !_intent.Background && _intent.Add is null, out var acquired);
        if (acquired != SingleInstanceAcquireResult.Acquired)
        {
            if (_intent.Add is { } add && !gate.PostPaths(add))
                AppLog.Error("Startup", "外部添加请求未送达；请重试。");
            gate.Dispose();
            Exit();
            return;
        }
        try
        {
            _lifetime = new WallApplicationLifetime(_scope, gate, this);
            _ = _lifetime.StartAsync(_intent);
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup", ex.Message);
            gate.Dispose();
            Exit();
        }
    }
}
