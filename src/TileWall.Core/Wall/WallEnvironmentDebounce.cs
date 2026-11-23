namespace TileWall.Core.Wall;

internal readonly record struct WallDisplayEnvironment(string Topology, int Width, int Height, double Scale,
    string PrimaryRotation);

internal sealed class WallEnvironmentDebounce
{
    internal const int TopologyDelaySeconds = 15;
    internal const int ParameterDelaySeconds = 5;

    private WallDisplayEnvironment? _baseline;
    private bool _initialFitRequired;
    private bool _topologyChanged;
    private long _observedAt;

    internal bool HasBaseline => _baseline is not null;
    internal WallDisplayEnvironment? Observed { get; private set; }
    internal long Revision { get; private set; }
    internal long? Deadline { get; private set; }
    internal int DelaySeconds { get; private set; }

    internal void Observe(WallDisplayEnvironment sample, long now, bool initialFitRequired)
    {
        if (Observed == sample) return;
        if (_baseline is null)
        {
            _baseline = sample;
            _initialFitRequired = initialFitRequired;
        }

        var previous = Observed ?? _baseline.Value;
        Observed = sample;
        _observedAt = now;
        Revision++;
        _topologyChanged |= previous.Topology != sample.Topology || _baseline.Value.Topology != sample.Topology;
        Schedule(sample, now);
    }

    internal void Restart(long now)
    {
        if (Deadline is null) return;
        Revision++;
        _observedAt = now;
        Deadline = now + DelaySeconds * 1000;
    }

    internal void Suspend(bool keepTopology = false)
    {
        if (Observed is not null) Revision++;
        Observed = null;
        Deadline = null;
        DelaySeconds = 0;
        if (!keepTopology) _topologyChanged = false;
    }

    internal void Finish(WallDisplayEnvironment sample, long revision, bool accepted)
    {
        if (accepted)
        {
            _baseline = sample;
            _initialFitRequired = false;
        }

        if (revision == Revision)
            ClearDeadline();
        else if (accepted && Observed is { } current)
        {
            // A save already admitted may finish after a new observation. Keep that
            // observation and measure its wait from its own change, not this save.
            _topologyChanged |= current.Topology != sample.Topology;
            Schedule(current, _observedAt);
        }
    }

    private void Schedule(WallDisplayEnvironment sample, long now)
    {
        if (sample == _baseline && !_initialFitRequired)
        {
            ClearDeadline();
            return;
        }

        DelaySeconds = _initialFitRequired || _topologyChanged ? TopologyDelaySeconds : ParameterDelaySeconds;
        Deadline = now + DelaySeconds * 1000;
    }

    private void ClearDeadline()
    {
        Deadline = null;
        DelaySeconds = 0;
        _topologyChanged = false;
    }
}
