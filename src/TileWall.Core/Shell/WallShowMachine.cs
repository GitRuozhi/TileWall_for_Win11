namespace TileWall.Core.Shell;

public enum WallShowState { Hidden, Visible }

public enum WallShowTrigger { Toggle, Show, Hide }

public interface IWallShowHost
{
    void SnapShow();
    void SnapHide();
    void FocusWall();
}

/// <summary>Routes wall visibility and focus requests synchronously.</summary>
public sealed class WallShowMachine(WallShowState initialState, IWallShowHost host)
{
    private readonly IWallShowHost _host = host ?? throw new ArgumentNullException(nameof(host));

    public WallShowState State { get; private set; } = initialState;

    public void Request(WallShowTrigger trigger)
    {
        var next = (State, trigger) switch
        {
            (WallShowState.Hidden, WallShowTrigger.Show or WallShowTrigger.Toggle) => WallShowState.Visible,
            (WallShowState.Visible, WallShowTrigger.Hide or WallShowTrigger.Toggle) => WallShowState.Hidden,
            _ => State,
        };
        if (next == State)
        {
            if (State == WallShowState.Visible && trigger == WallShowTrigger.Show) _host.FocusWall();
            return;
        }

        State = next;
        if (next == WallShowState.Visible) _host.SnapShow();
        else _host.SnapHide();
    }
}
