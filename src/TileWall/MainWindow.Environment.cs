using TileWall.Core.Wall;
using TileWall.Resources;

namespace TileWall;

public sealed partial class MainWindow
{
    private bool _environmentAdaptQueued;
    private bool _environmentOverflow;
    private bool _environmentAfterInteraction;

    private void ResumeEnvironmentAdaptation()
    {
        if (_environmentAfterInteraction) QueueEnvironmentAdaptation();
    }

    private void RestoreScrollPosition(Windows.Foundation.Point offset) => WallScroll.ChangeView(
        Math.Clamp(offset.X, 0, WallScroll.ScrollableWidth), Math.Clamp(offset.Y, 0, WallScroll.ScrollableHeight), null, true);

    private void QueueEnvironmentAdaptation()
    {
        if (_environmentAdaptQueued || _windowClosing || ShutdownStarted || !_hasDocument || _viewBlocked) return;
        _environmentAdaptQueued = DispatcherQueue.TryEnqueue(async () =>
        {
            _environmentAdaptQueued = false;
            if (_windowClosing || ShutdownStarted || !_hasDocument || _viewBlocked) return;
            if (PersistenceInteractionBlocked) { _environmentAfterPersistence = true; return; }
            if (_modal || _resizing || _gesture.Phase != ApplicationModel.GesturePhase.Idle)
            { _environmentAfterInteraction = true; return; }
            _environmentAfterInteraction = false;
            var budget = ViewportBudget(Settings);
            var result = WallViewportPolicy.AdaptToEnvironment(Document, budget, _metrics);
            if (result.Proposal is not { } proposal)
            {
                if (budget.MaxColumns == 0)
                {
                    // There is no accepted narrow projection: neither an unavailable
                    // fixed-rail area nor a failed expansion can become a rescue mode.
                    TransientStatus(ViewportFailure(result));
                    return;
                }
                SetEnvironmentOverflow(true);
                return;
            }
            if (proposal.Document.Generation != Document.Generation)
            {
                var opensExpansion = !Settings.ExpandColumns && proposal.Document.Settings.ExpandColumns;
                var error = await CommitProposalAsync(proposal);
                if (_windowClosing || ShutdownStarted || _viewBlocked) return;
                if (error is not null)
                {
                    if (!opensExpansion) SetEnvironmentOverflow(true);
                    TransientStatus(error);
                    return;
                }
            }
            SetEnvironmentOverflow(false);
        });
    }

    private void SetEnvironmentOverflow(bool value)
    {
        if (_environmentOverflow == value) return;
        _environmentOverflow = value;
        ApplyViewport(animateMode: false);
        if (value) Status(ProductStrings.Get("Wall.EnvironmentScroll"));
        else if (StatusText.Text == ProductStrings.Get("Wall.EnvironmentScroll"))
            ClearStatus();
    }

    // Existing oversized content stays editable so the user can remove/shrink it. A
    // proposed mode change still goes through its ordinary screen-capacity proposal.
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
