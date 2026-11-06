using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using TileWall.Shell.Interop;
using Xunit;

namespace TileWall.Core.Tests.Shell;

public sealed class DesktopUserUninstallLauncherTests
{
    [DesktopFact]
    [SupportedOSPlatform("windows")]
    public void DesktopAutomationCanBeAcquiredOnStaWithoutLaunchingAnything()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var acquired = false;
        var worker = new Thread(() =>
        {
            try
            {
                // This exercises the real desktop COM chain and native signatures.
                // Submit/ShellExecute is deliberately never called by this check.
                using var desktop = DesktopUserUninstallLauncher.AcquireDesktopApplication();
                acquired = true;
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "The read-only desktop COM query did not finish.");
        Assert.Null(failure);
        Assert.True(acquired);
    }

    private sealed class DesktopFactAttribute : FactAttribute
    {
        public DesktopFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || GetShellWindow() == IntPtr.Zero)
                Skip = "Requires an interactive Windows desktop; no windows are launched by this check.";
        }

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr GetShellWindow();
    }
}
