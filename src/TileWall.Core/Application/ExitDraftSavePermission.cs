using TileWall.Core.Shell;

namespace TileWall.Core.Application;

/// <summary>Allows only the existing participant's synchronous exit-save call.</summary>
internal sealed class ExitDraftSavePermission
{
    private IExitParticipant? _saving;

    public bool Allows(IExitParticipant? active) => _saving is not null && ReferenceEquals(_saving, active);
    public bool BlocksMutation(bool pending, bool admissionClosed, IExitParticipant? active) =>
        pending || admissionClosed && !Allows(active);

    public T Run<T>(IExitParticipant participant, Func<IExitParticipant?> active, Func<T> save)
    {
        if (_saving is not null || !ReferenceEquals(participant, active()))
            throw new InvalidOperationException("The exit participant is no longer active.");
        _saving = participant;
        try { return save(); }
        finally { _saving = null; }
    }
}
