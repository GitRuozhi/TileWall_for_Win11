using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using TileWall.Core.Shell;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using TileWall.Resources;

namespace TileWall.Graphics;

public sealed partial class WallImageService
{
    private const long GalleryLimit = 1024L * 1024 * 1024;
    private static readonly TimeSpan GalleryCheckInterval = TimeSpan.FromSeconds(60);
    private readonly Dictionary<string, GallerySourceState> _gallerySources = new(StringComparer.OrdinalIgnoreCase);
    private sealed class GallerySourceState(string source, ManagedGalleryReference? managedGallery)
    {
        internal string Source = source;
        internal ManagedGalleryReference? ManagedGallery = managedGallery;
        internal GalleryIndex? Index;
        internal PuzzleCandidateResult? Snapshot;
        internal DateTimeOffset Due = DateTimeOffset.MinValue;
        internal Task<PuzzleCandidateResult>? InFlight;
        internal bool IndexLoaded;
    }
    private sealed record GalleryEntry(string Name, long Length, long ModifiedTicks, string? Hash, string? Extension);
    private sealed record GalleryIndex(int Format, string Source, string Version, IReadOnlyList<GalleryEntry> Entries);

    public DateTimeOffset GalleryCheckDue(MemberImageSource source)
    {
        var folder = NormalizeImageFilePath(source.Path);
        lock (_workGate) return _cacheStopping || _disposed ? DateTimeOffset.UtcNow + GalleryCheckInterval :
            folder is not null && _gallerySources.TryGetValue(folder, out var state) ? state.Due : DateTimeOffset.MinValue;
    }

    public Task<PuzzleCandidateResult> RefreshGalleryAsync(MemberImageSource source, CancellationToken ct = default) =>
        StartImageRequest(token => RefreshGalleryRequestAsync(source, token), ct,
            new PuzzleCandidateResult([], WallImageStatus.Cancelled));

    private async Task<PuzzleCandidateResult> RefreshGalleryRequestAsync(MemberImageSource source, CancellationToken ct)
    {
        var folder = NormalizeImageFilePath(source.Path);
        if (source.Kind != MemberImageSourceKind.Folder || folder is null) return new([], WallImageStatus.Invalid, "InvalidGallerySource");
        Task<PuzzleCandidateResult> task;
        lock (_workGate)
        {
            if (_cacheStopping || _disposed) return new([], WallImageStatus.Cancelled);
            if (!_gallerySources.TryGetValue(folder, out var state)) _gallerySources.Add(folder, state = new(folder, source.ManagedGallery));
            if (state.InFlight is { } active) task = active;
            else if (state.Snapshot is { } snapshot && state.Due > DateTimeOffset.UtcNow) return snapshot;
            else
            {
                task = Task.Run(() => RefreshGalleryCoreAsync(state, _cacheLifetime.Token), _cacheLifetime.Token);
                state.InFlight = task; _ = TrackCacheWork(task);
            }
        }
        try { return await task.WaitAsync(ct); }
        catch (OperationCanceledException) { return new([], WallImageStatus.Cancelled); }
    }

    private string GalleryDirectory(string source) => _disk!.Under("Cache", "Images", "Galleries", ManagedImageCache.Key(source.ToUpperInvariant()));
    private string GalleryOriginal(string source, string hash) => Path.Combine(GalleryDirectory(source), hash + ".original");

    private async Task<PuzzleCandidateResult> RefreshGalleryCoreAsync(GallerySourceState state, CancellationToken ct)
    {
        var entered = false;
        try
        {
            using var formalRead = OpenManagedGalleryRead(new() { Kind = MemberImageSourceKind.Folder,
                Path = state.Source, ManagedGallery = state.ManagedGallery });
            await _slots.WaitAsync(ct).ConfigureAwait(false); entered = true;
            if (!state.IndexLoaded)
            {
                state.IndexLoaded = true; state.Index = ReadGalleryIndex(state.Source);
            }
            ct.ThrowIfCancellationRequested();
            // Materialize the top-level listing before accepting any deletion. A
            // failed listing is unavailable, never an authoritative empty gallery.
            var files = Directory.EnumerateFiles(state.Source, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ThenBy(path => path, StringComparer.Ordinal).ToArray();
            var previous = state.Index;
            var oldEntries = previous?.Entries.ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, GalleryEntry>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<GalleryEntry>(); var count = 0;
            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(path); var name = info.Name;
                if (!info.Exists) throw new IOException("Gallery source changed during enumeration.");
                GalleryEntry entry;
                if (oldEntries.TryGetValue(name, out var old) && old.Length == info.Length && old.ModifiedTicks == info.LastWriteTimeUtc.Ticks &&
                    (old.Hash is null || _disk is null || File.Exists(GalleryOriginal(state.Source, old.Hash)))) entry = old;
                else if (info.Length is < 1 or > PreparedMediaAsset.MaximumBytes)
                    entry = new(name, info.Length, info.LastWriteTimeUtc.Ticks, null, null);
                else
                {
                    try
                    {
                        var original = await VerifyOriginalAsync(path, ct).ConfigureAwait(false);
                        entry = new(name, info.Length, info.LastWriteTimeUtc.Ticks, original.Asset.Reference.Sha256, original.Asset.Reference.Extension);
                        if (_disk is { } disk)
                        {
                            var destination = GalleryOriginal(state.Source, entry.Hash!); disk.Guard(destination);
                            if (!File.Exists(destination)) await PublishBytesAsync(destination, original.Bytes, ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is COMException or InvalidDataException or ArgumentException or OverflowException)
                    { entry = new(name, info.Length, info.LastWriteTimeUtc.Ticks, null, null); }
                }
                entries.Add(entry);
                if (entry.Hash is not null && ++count >= 99) break;
            }
            var version = ManagedImageCache.Key(string.Join('\n', entries.Where(entry => entry.Hash is not null)
                .Select(entry => entry.Name.ToUpperInvariant() + "|" + entry.Hash)));
            var index = new GalleryIndex(1, state.Source, version, entries);
            if (_disk is { } cache && (previous is null || previous.Version != index.Version || !previous.Entries.SequenceEqual(entries)))
                await PublishBytesAsync(Path.Combine(GalleryDirectory(state.Source), "index.json"), JsonSerializer.SerializeToUtf8Bytes(index, CacheJson), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var changed = ChangedGalleryPaths(state.Source, previous, index);
            var result = GalleryResult(state, index, WallImageStatus.Loaded, null, true, changed);
            lock (_workGate)
            {
                ct.ThrowIfCancellationRequested();
                state.Index = index;
                state.Snapshot = result;
            }
            if (_disk is { } storage)
                try { storage.Evict(storage.Under("Cache", "Images", "Galleries"), GalleryLimit, ".original"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("ImageCache", ex.Message); }
            return result;
        }
        catch (OperationCanceledException) { return new([], WallImageStatus.Cancelled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (state.ManagedGallery is not null)
                return new([], WallImageStatus.Invalid, "ManagedGalleryReadFailed") { IsComplete = false, DiagnosticDetail = ex.Message };
            var fallback = state.Index;
            var result = fallback is null ? new PuzzleCandidateResult([], WallImageStatus.Missing, "Gallery.SourceUnavailableNoCache") { IsComplete = false }
                : GalleryResult(state, fallback, WallImageStatus.Fallback, "Gallery.SourceUnavailable", false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            result = result with { DiagnosticDetail = ex.Message,
                Diagnostic = result.Paths.Count == 0 ? "Gallery.SourceUnavailableNoCache" : "Gallery.SourceUnavailable" };
            lock (_workGate)
            {
                if (_cacheStopping) return new([], WallImageStatus.Cancelled);
                state.Snapshot = result;
            }
            return result;
        }
        finally
        {
            if (entered) _slots.Release();
            lock (_workGate) { state.InFlight = null; state.Due = DateTimeOffset.UtcNow + GalleryCheckInterval; }
        }
    }

    public static string? DescribeCandidateDiagnostic(PuzzleCandidateResult result) => result.Diagnostic is
        "Gallery.SourceUnavailable" or "Gallery.SourceUnavailableNoCache"
            ? string.Format(ProductStrings.Get(result.Diagnostic), result.DiagnosticDetail ?? string.Empty) : result.Diagnostic;

    private async Task EnsureGalleryOriginalAsync(MemberImageSource source, string? identity, CancellationToken ct)
    {
        if (_disk is not { } disk || identity is null || _cacheStopping) return;
        var folder = NormalizeImageFilePath(source.Path); if (folder is null) return;
        GallerySourceState? state; lock (_workGate) state = _gallerySources.GetValueOrDefault(folder);
        var entry = state?.Index?.Entries.FirstOrDefault(entry => StringComparer.OrdinalIgnoreCase.Equals(entry.Name, Path.GetFileName(identity)));
        if (entry?.Hash is not { } hash) return;
        try
        {
            await Task.Run(async () =>
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(identity);
                if (!info.Exists || info.Length != entry.Length || info.LastWriteTimeUtc.Ticks != entry.ModifiedTicks)
                { lock (_workGate) state!.Due = DateTimeOffset.MinValue; return; }
                await _slots.WaitAsync(ct).ConfigureAwait(false);
                VerifiedOriginal original;
                try { original = await VerifyOriginalAsync(identity, ct).ConfigureAwait(false); }
                finally { _slots.Release(); }
                if (original.Asset.Reference.Sha256 != hash) { lock (_workGate) state!.Due = DateTimeOffset.MinValue; return; }
                await PublishBytesAsync(GalleryOriginal(folder, hash), original.Bytes, ct).ConfigureAwait(false);
            }, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException)
        { /* A missing disposable package keeps the existing frame or placeholder. */ }
    }

    private GalleryIndex? ReadGalleryIndex(string source)
    {
        if (_disk is not { } disk) return null;
        try
        {
            var path = Path.Combine(GalleryDirectory(source), "index.json");
            var index = JsonSerializer.Deserialize<GalleryIndex>(disk.Read(path, 8 * 1024 * 1024), CacheJson);
            if (index is null || index.Format != 1 || !StringComparer.OrdinalIgnoreCase.Equals(index.Source, source) ||
                index.Entries is null || index.Entries.Count > 100_000 || index.Entries.Any(entry =>
                    entry is null || string.IsNullOrWhiteSpace(entry.Name) || Path.GetFileName(entry.Name) != entry.Name || entry.Name is "." or ".." ||
                    entry.Hash is { } hash && (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || entry.Length is < 1 or > PreparedMediaAsset.MaximumBytes))) return null;
            if (index.Entries.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != index.Entries.Count) return null;
            return index;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }
    private PuzzleCandidateResult GalleryResult(GallerySourceState state, GalleryIndex index, WallImageStatus status,
        string? diagnostic, bool complete, IReadOnlySet<string> changed)
    {
        var paths = index.Entries.Where(entry => entry.Hash is not null &&
            (_disk is null || CachedOriginalExists(state.Source, entry.Hash))).Select(entry => Path.Combine(state.Source, entry.Name)).ToArray();
        return new(paths, status, diagnostic) { SourceVersion = index.Version, IsComplete = complete, ChangedPaths = changed };
    }
    private bool CachedOriginalExists(string source, string hash)
    {
        var path = GalleryOriginal(source, hash); _disk!.Guard(path); return File.Exists(path);
    }
    private static IReadOnlySet<string> ChangedGalleryPaths(string source, GalleryIndex? previous, GalleryIndex next)
    {
        var before = previous?.Entries.Where(entry => entry.Hash is not null).ToDictionary(entry => entry.Name, entry => entry.Hash, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var after = next.Entries.Where(entry => entry.Hash is not null).ToDictionary(entry => entry.Name, entry => entry.Hash, StringComparer.OrdinalIgnoreCase);
        return before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(name => before.GetValueOrDefault(name) != after.GetValueOrDefault(name))
            .Select(name => Path.Combine(source, name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private (string? Path, string? Hash) GalleryReadPath(MemberImageSource source, string? identity)
    {
        if (identity is null || _disk is null) return (identity, null);
        var folder = NormalizeImageFilePath(source.Path);
        if (folder is null) return (null, null);
        GalleryIndex? index;
        lock (_workGate) index = _gallerySources.GetValueOrDefault(folder)?.Index;
        var name = Path.GetFileName(identity);
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(identity), folder)) return (null, null);
        var entry = index?.Entries.FirstOrDefault(entry => StringComparer.OrdinalIgnoreCase.Equals(entry.Name, name));
        if (entry?.Hash is not { } hash) return (null, null);
        var path = GalleryOriginal(folder, hash); _disk.Guard(path); return (path, hash);
    }
}
