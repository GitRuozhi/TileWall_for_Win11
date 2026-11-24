using TileWall.Core.Wall;
using TileWall.Resources;

namespace TileWall;

public sealed partial class MainWindow
{
    private readonly LinkedList<WallDropPlan> _drops = new();

    private WallDocument DropBasis => _drops.Last?.Value.Proposal.Document ?? Document;
    private static string StaleDropMessage() => string.Format(ProductStrings.Get("Wall.DropFailed"),
        ProductStrings.WallRejectionReason(WallRejection.StaleGeneration));

    private bool CanContinueDrops => _drops.Count != 0 && _userMutationReservations == 1 &&
        !_persistenceAdmissionClosed && !_openingWall && !_windowClosing && !ShutdownStarted;

    private bool KeepDropPresentation(WallDocument committed) =>
        _drops.First is { Value: var first } && first.Proposal.Document.Generation == committed.Generation &&
        (_drops.Count > 1 || _gestureDocument?.Generation == committed.Generation);

    private void EnqueueDrop(WallDropPlan drop)
    {
        _drops.AddLast(drop);
        TraceInput($"DropQueue release base={drop.Basis.Generation} next={drop.Proposal.Document.Generation} pending={_drops.Count}");
        if (_drops.Count == 1) _ = QueueUserMutationAsync(SaveDropsAsync);
    }

    private async Task SaveDropsAsync()
    {
        try
        {
            while (_drops.First is { Value: var drop })
            {
                var before = Document;
                var proposal = drop.Revalidate(before, WorkWidthDip, WorkHeightDip, _metrics);
                if (proposal is null)
                {
                    RejectDrops(StaleDropMessage());
                    return;
                }
                var error = await CommitProposalAsync(proposal, allowInteraction: true, layoutDrop: true);
                if (error is not null || _viewBlocked || !drop.Matches(Document))
                {
                    RejectDrops(error);
                    return;
                }

                _drops.RemoveFirst();
                TraceInput($"DropQueue saved generation={Document.Generation} pending={_drops.Count}");
                if (_drops.Count == 0 && _gesture.Phase == ApplicationModel.GesturePhase.Idle)
                {
                    ShowCommittedDropFeedback(drop.Displayed, before,
                        (drop.Action as MoveMemberAction)?.MemberId, (drop.Action as MoveGroupAction)?.GroupId);
                    RestoreDropHover();
                }
            }
        }
        catch (Exception ex)
        {
            RejectDrops(string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message));
        }
    }

    private void RejectDrops(string? error)
    {
        var rejected = _drops.Count;
        _drops.Clear();
        if (_windowClosing || ShutdownStarted) return;
        TerminateGesture(restoreScroll: true, restoreView: false);
        _performance.End("rejected-basis", Surface.VisualCreationCount, Surface.GridCreationCount);
        if (_viewBlocked) return;
        try
        {
            StopViewportTransition();
            if (!Settings.FullScreen && Surface.Geometry?.WallHeight != _metrics.WallHeight(Document.Rows))
                Surface.BeginAutomaticHeightPresentation();
            Surface.Render(Document);
            Surface.RefreshClocks(_clock.UtcNow);
            ApplyViewport(animateMode: false, animateHeight: true);
            RestoreDropHover();
            Status(error ?? StaleDropMessage());
            TraceInput($"DropQueue restored generation={Document.Generation} rejected={rejected}");
        }
        catch (Exception ex)
        {
            BlockView(string.Format(ProductStrings.Get("Wall.GestureViewFailed"), ex.Message), retry: true);
        }
    }
}
