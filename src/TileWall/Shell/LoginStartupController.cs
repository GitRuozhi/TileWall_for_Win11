using TileWall.ApplicationModel;
using TileWall.Core.Settings;
using TileWall.Shell.Interop;
using TileWall.Resources;

namespace TileWall.Shell;

internal sealed record LoginStartupState(bool? Enabled, string? Error = null);

/// <summary>Async operating-system state; test registrations use their own Run value and scope argument.</summary>
internal sealed class LoginStartupController(RuntimeScope scope)
{
    private readonly RunLoginStartup _run = new(new RegistryRunKeyStore(scope.LoginValueName));

    public async Task<LoginStartupState> ReadAsync()
    {
        try
        {
#if TILEWALL_PACKAGED
            if (!scope.IsTest)
            {
                var task = await Windows.ApplicationModel.StartupTask.GetAsync(LoginStartupTaskId.TaskId);
                return new(task.State is Windows.ApplicationModel.StartupTaskState.Enabled or
                    Windows.ApplicationModel.StartupTaskState.EnabledByPolicy);
            }
#endif
            await Task.CompletedTask;
            var state = _run.Read();
            return new(state.Enabled, state.Error);
        }
        catch (Exception ex) { return new(null, ex.Message); }
    }

    public async Task<LoginStartupState> SetAsync(bool enabled)
    {
        try
        {
#if TILEWALL_PACKAGED
            if (!scope.IsTest)
            {
                var task = await Windows.ApplicationModel.StartupTask.GetAsync(LoginStartupTaskId.TaskId);
                if (enabled) await task.RequestEnableAsync();
                else task.Disable();
                var actual = await ReadAsync();
                return actual.Enabled == enabled ? actual : actual with
                { Error = actual.Error ?? string.Format(ProductStrings.Get("Wall.LoginDenied"), task.State) };
            }
#endif
            var executable = enabled
                ? Environment.ProcessPath ?? throw new InvalidOperationException(ProductStrings.Get("Wall.ExecutableMissing"))
                : string.Empty;
            var state = _run.Set(enabled, executable, scope.IsTest ? scope.DataRoot : null);
            return new(state.Enabled, state.Error);
        }
        catch (Exception ex)
        {
            var actual = await ReadAsync();
            return actual with { Error = ex.Message };
        }
    }
}

