using Microsoft.UI.Dispatching;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Resources;
using TileWall.Shell.Interop;

namespace TileWall;

public sealed partial class MainWindow
{
    private readonly WallEnvironmentDebounce _environmentDebounce = new();
    private DispatcherQueueTimer? _environmentTimer;
    private bool _environmentAdaptQueued;
    private bool _environmentSampleRequested;
    private int _environmentReadRetries;
    private bool _environmentWallShown;
    private bool _environmentOverflow;
    private bool _environmentAfterInteraction;
    private bool _environmentSaving;
    private bool _environmentRestartAfterEdit;
    private long? _environmentRenderedGeneration;
    private string? _environmentWaitingText;

    private void ResumeEnvironmentAdaptation()
    {
        if (_environmentAfterInteraction) QueueEnvironmentAdaptation();
    }

    private void RestoreScrollPosition(Windows.Foundation.Point offset) => WallScroll.ChangeView(
        Math.Clamp(offset.X, 0, WallScroll.ScrollableWidth), Math.Clamp(offset.Y, 0, WallScroll.ScrollableHeight), null, true);

    private void QueueEnvironmentAdaptation(bool observeDisplay = false)
    {
        if (!_environmentWallShown || _windowClosing || ShutdownStarted) return;
        if (observeDisplay)
        {
            _environmentSampleRequested = true;
            _environmentReadRetries = 0;
        }
        if (_environmentAdaptQueued || !_environmentSampleRequested && _environmentDebounce.Deadline is null) return;
        _environmentAdaptQueued = DispatcherQueue.TryEnqueue(async () =>
        {
            _environmentAdaptQueued = false;
            await CheckEnvironmentAdaptationAsync();
        });
    }

    internal void EnvironmentDisplayPowerChanged(bool displayOn)
    {
        if (_windowClosing || ShutdownStarted) return;
        AppLog.Info("DisplayEnvironment", displayOn ? "Session display on" : "Session display off");
        if (displayOn) QueueEnvironmentRefresh(observeDisplay: true);
        else SuspendEnvironmentAdaptation();
    }

    internal void PauseEnvironmentForHide()
    {
        _environmentWallShown = false;
        SuspendEnvironmentAdaptation();
    }

    private void SuspendEnvironmentAdaptation(bool keepTopology = false)
    {
        _environmentDebounce.Suspend(keepTopology);
        _environmentTimer?.Stop();
        _environmentSampleRequested = true;
        _environmentAfterInteraction = false;
        if (!keepTopology) _environmentRestartAfterEdit = false;
        RefreshEnvironmentStatus();
    }

    private void ObserveEnvironmentDocument(WallDocument document)
    {
        // Rendering may be retried for the same committed generation. Image and
        // carousel saves do not start a new user-edit waiting period.
        if (_environmentDebounce.Deadline is not null && _environmentRenderedGeneration is { } generation &&
            document.Generation != generation && !_environmentSaving && !_backgroundPersistencePending)
            _environmentRestartAfterEdit = true;
        _environmentRenderedGeneration = document.Generation;
    }

    private async Task CheckEnvironmentAdaptationAsync()
    {
        if (!_environmentWallShown || !AppWindow.IsVisible || _windowClosing || ShutdownStarted ||
            _welcomeInitializationPending || !_hasDocument || _viewBlocked) return;
        if (!ApplicationModel.WallApplicationLifetime.IsSessionDisplayOn())
        {
            SuspendEnvironmentAdaptation();
            return;
        }

        var now = Environment.TickCount64;
        var due = _environmentDebounce.Deadline is { } deadline && now >= deadline;
        if (_environmentSampleRequested || due)
        {
            _environmentSampleRequested = false;
            ReadPrimaryScreen();
            var width = _screenArea.Right - _screenArea.Left;
            var height = _screenArea.Bottom - _screenArea.Top;
            if (!DisplayTopology.TryRead(width, height, out var topology, out var rotation))
            {
                SuspendEnvironmentAdaptation(keepTopology: true);
                if (_environmentReadRetries++ < 3) ScheduleEnvironmentCheck(1000);
                return;
            }

            _environmentReadRetries = 0;
            now = Environment.TickCount64;
            var sample = new WallDisplayEnvironment(topology, width, height, Scale, rotation);
            var changed = _environmentDebounce.Observed != sample;
            var initialFitRequired = !_environmentDebounce.HasBaseline &&
                !WallViewportPolicy.Validate(Document, ViewportBudget(Settings), _metrics).Success;
            _environmentDebounce.Observe(sample, now, initialFitRequired);
            if (changed) ApplyViewport(animateMode: false);
        }

        if (_environmentRestartAfterEdit)
        {
            if (CanAcceptBackgroundWrite)
            {
                _environmentRestartAfterEdit = false;
                _environmentDebounce.Restart(now);
            }
            else _environmentAfterInteraction = true;
        }
        RefreshEnvironmentStatus();

        if (_environmentSaving || _environmentDebounce.Deadline is not { } next)
        {
            _environmentTimer?.Stop();
            return;
        }
        if (now < next)
        {
            ScheduleEnvironmentCheck(next - now);
            return;
        }
        if (!CanAcceptBackgroundWrite)
        {
            _environmentAfterInteraction = true;
            return;
        }

        _environmentAfterInteraction = false;
        var current = _environmentDebounce.Observed!.Value;
        if (current.Scale != Scale)
        {
            QueueEnvironmentAdaptation(observeDisplay: true);
            return;
        }
        var revision = _environmentDebounce.Revision;
        var result = WallViewportPolicy.AdaptToEnvironment(Document, ViewportBudget(Settings), _metrics);
        if (result.Proposal is not { } proposal)
        {
            _environmentDebounce.Finish(current, revision, accepted: false);
            if (MaximumTileWidthDip(Settings) <= 0) TransientStatus(ViewportFailure(result));
            RefreshEnvironmentStatus();
            return;
        }
        if (proposal.Document.Generation == Document.Generation)
        {
            _environmentDebounce.Finish(current, revision, accepted: true);
            RefreshEnvironmentStatus();
            return;
        }

        _environmentTimer?.Stop();
        _environmentSaving = true;
        var error = await CommitProposalAsync(proposal, background: true);
        _environmentSaving = false;
        var currentAttempt = revision == _environmentDebounce.Revision;
        var accepted = !SettingsPersistenceBlocked && error is null && Document.Generation == proposal.Document.Generation;
        _environmentDebounce.Finish(current, revision, accepted);
        if (!_environmentWallShown || _windowClosing || ShutdownStarted || _viewBlocked) return;
        if (currentAttempt && ApplicationModel.WallApplicationLifetime.IsSessionDisplayOn())
        {
            if (error is not null) TransientStatus(error);
            else if (accepted && _statusOverlay.Text != ProductStrings.Get("Wall.CleanupPending"))
                TransientStatus(string.Format(ProductStrings.Get("Wall.EnvironmentSaved"), Document.Columns, Document.Rows));
        }
        RefreshEnvironmentStatus();
        QueueEnvironmentAdaptation();
    }

    private void ScheduleEnvironmentCheck(long milliseconds)
    {
        if (_environmentTimer is null)
        {
            _environmentTimer = DispatcherQueue.CreateTimer();
            _environmentTimer.IsRepeating = false;
            _environmentTimer.Tick += (_, _) => QueueEnvironmentAdaptation();
        }

        _environmentTimer.Stop();
        _environmentTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds));
        _environmentTimer.Start();
    }

    private void RefreshEnvironmentStatus()
    {
        if (_viewBlocked || _upgradeOwnsStatus || _statusOverlay.HasTransient) return;
        var overflow = ProductStrings.Get("Wall.EnvironmentScroll");
        var text = _statusOverlay.Text;
        if (text.Length != 0 && text != _environmentWaitingText && text != overflow) return;

        _environmentWaitingText = _environmentWallShown && _environmentDebounce.Deadline is not null
            ? string.Format(ProductStrings.Get("Wall.EnvironmentWaiting"), _environmentDebounce.DelaySeconds) : null;
        if (_environmentWaitingText is { } waiting)
        {
            if (text != waiting) PersistentStatus(waiting);
        }
        else if (_environmentOverflow && _environmentWallShown)
        {
            if (text != overflow) PersistentStatus(overflow);
        }
        else if (text.Length != 0) ClearStatus();
    }

    // When the saved wall is clipped, editing retains the same placed content
    // budget. Changes to the display mode still use ordinary physical limits.
    private WallViewportBudget RecoverableViewportBudget(WallSettings settings)
    {
        var budget = ViewportBudget(settings);
        if (!_environmentOverflow || !_hasDocument || settings.FullScreen != Settings.FullScreen ||
            settings.ExpandColumns != Settings.ExpandColumns || settings.MenuStart != Settings.MenuStart) return budget;
        return budget with
        {
            MaxColumns = Math.Max(budget.MaxColumns, Document.Columns), MaxRows = Math.Max(budget.MaxRows, Document.Rows),
            MaxWidthDip = Math.Max(budget.MaxWidthDip, _metrics.WallWidth(Document.Columns)),
            MaxHeightDip = Math.Max(budget.MaxHeightDip, _metrics.WallHeight(Document.Rows)),
        };
    }
}
