using System.Diagnostics;

namespace TileWall.Core.Storage;

/// <summary>Explicit optional wall-time diagnostics. No environment, IO or observers in Core.</summary>
public sealed class WallCommitTimings
{
    private readonly Dictionary<string, (long Ticks, int Count)> _stages = new(StringComparer.Ordinal);

    public IDisposable? Measure(string stage)
    {
        try { return new Measurement(this, stage); }
        catch { return null; }
    }

    private void Record(string stage, long ticks)
    {
        // Diagnostics must never change a transaction's exception or success behavior.
        try
        {
            var previous = _stages.GetValueOrDefault(stage);
            _stages[stage] = (previous.Ticks + ticks, previous.Count + 1);
        }
        catch { }
    }

    public IReadOnlyList<WallCommitStageTiming> Snapshot() => _stages.Select(stage =>
        new WallCommitStageTiming(stage.Key, stage.Value.Ticks * 1000d / Stopwatch.Frequency,
            stage.Value.Count)).ToArray();

    private sealed class Measurement(WallCommitTimings owner, string stage) : IDisposable
    {
        private WallCommitTimings? _owner = owner;
        private readonly long _started = Stopwatch.GetTimestamp();
        public void Dispose()
        {
            var active = _owner;
            _owner = null;
            if (active is not null) active.Record(stage, Stopwatch.GetTimestamp() - _started);
        }
    }
}

public sealed record WallCommitStageTiming(string Stage, double ElapsedWallMs, int Count);
