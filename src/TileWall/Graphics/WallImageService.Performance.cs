using System.Text.Json;
using System.Diagnostics;
using TileWall.Core.Shell;

namespace TileWall.Graphics;

public sealed partial class WallImageService
{
    private long _resourceStampRequests, _resourceStampUnique;
    private long _automaticSourceObservations;
    private long _nativePackageMemoryHits, _nativePackageDiskHits, _nativeResourceReads;
    private long _displayMemoryHits, _displayDiskHits, _shellIconReads, _packageIconDecodedFrames;
    private static long _nativeDecodedFrames;
    private static long _nextImageTrace;

    private sealed class ImageCandidateTrace : IDisposable
    {
        private readonly long _id = Interlocked.Increment(ref _nextImageTrace);
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly string _kind;
        private readonly string? _source;
        private readonly int _width, _height;
        private readonly Dictionary<string, double> _stages = new(StringComparer.Ordinal);

        private ImageCandidateTrace(string kind, string? source, int width, int height)
        {
            _kind = kind; _source = source; _width = width; _height = height;
            AppLog.Warn("ImageCandidatePerf", JsonSerializer.Serialize(new { id = _id, kind, source, width, height, stage = "begin" }));
        }
        internal static ImageCandidateTrace? Start(string kind, string? source, int width, int height) =>
            Environment.GetEnvironmentVariable("TILEWALL_PERF_DETAIL") == "1" ? new(kind, source, width, height) : null;
        internal IDisposable Measure(string stage, string? key = null)
        {
            AppLog.Warn("ImageCandidatePerf", JsonSerializer.Serialize(new { id = _id, stage, key, state = "enter" }));
            return new Stage(this, stage, key);
        }
        internal void Mark(string stage, string detail) => AppLog.Warn("ImageCandidatePerf",
            JsonSerializer.Serialize(new { id = _id, stage, detail }));
        public void Dispose() => AppLog.Warn("ImageCandidatePerf", JsonSerializer.Serialize(new
        {
            id = _id, kind = _kind, source = _source, width = _width, height = _height,
            stage = "end", totalMs = Stopwatch.GetElapsedTime(_started).TotalMilliseconds, stagesMs = _stages,
        }));
        private sealed class Stage(ImageCandidateTrace owner, string stage, string? key) : IDisposable
        {
            private readonly long _started = Stopwatch.GetTimestamp();
            public void Dispose()
            {
                var elapsedMs = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
                owner._stages[stage] = owner._stages.GetValueOrDefault(stage) + elapsedMs;
                AppLog.Warn("ImageCandidatePerf", JsonSerializer.Serialize(new { id = owner._id, stage, key, state = "leave", elapsedMs }));
            }
        }
    }

    // Cumulative work counters, sampled at preview boundaries. They measure IO /
    // decode work, not presentation latency; no per-frame logging in the hot path.
    internal void TraceImageWork(string stage)
    {
        if (Environment.GetEnvironmentVariable("TILEWALL_PERF") != "1") return;
        AppLog.Warn("ImagePreparationPerf", JsonSerializer.Serialize(new
        {
            stage, automaticSourceObservations = Interlocked.Read(ref _automaticSourceObservations),
            resourceStampRequests = Interlocked.Read(ref _resourceStampRequests),
            resourceStampUnique = Interlocked.Read(ref _resourceStampUnique),
            nativeMemoryHits = Interlocked.Read(ref _nativePackageMemoryHits),
            nativeDiskHits = Interlocked.Read(ref _nativePackageDiskHits),
            nativeResourceReads = Interlocked.Read(ref _nativeResourceReads),
            nativeFramesDecoded = Interlocked.Read(ref _nativeDecodedFrames),
            packageFramesDecoded = Interlocked.Read(ref _packageIconDecodedFrames),
            imagePlanesDecoded = ImageFrameDecoder.DecodedPlaneCount,
            displayMemoryHits = Interlocked.Read(ref _displayMemoryHits),
            displayDiskHits = Interlocked.Read(ref _displayDiskHits),
            shellIconReads = Interlocked.Read(ref _shellIconReads),
        }));
    }

    private PeIconReadResult ReadNativeResource(PeIconResourceReader reader, string path, bool allowResourceModule = false)
    {
        Interlocked.Increment(ref _nativeResourceReads);
        return reader.Read(path, allowResourceModule);
    }
}
