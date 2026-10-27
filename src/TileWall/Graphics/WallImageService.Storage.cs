using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text.Json;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TileWall.Graphics;

public sealed partial class WallImageService
{
    private ManagedImageCache? _disk;
    private Func<Action, Task>? _publishCache;
    private Func<ManagedGalleryReference?, ManagedMediaReference?, IDisposable>? _formalResourceLease;
    private readonly CancellationTokenSource _cacheLifetime = new();
    private readonly object _workGate = new();
    private readonly HashSet<Task> _cacheWork = [];
    private bool _cacheStopping;
    private readonly Dictionary<string, string> _verifiedContentHashes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions CacheJson = new() { PropertyNameCaseInsensitive = false };

    public void ConfigureStorage(string dataRoot, Func<Action, Task> publishShortCacheAction,
        Func<ManagedGalleryReference?, ManagedMediaReference?, IDisposable>? formalResourceLease = null)
    {
        ArgumentNullException.ThrowIfNull(publishShortCacheAction);
        if (!Path.IsPathFullyQualified(dataRoot)) throw new ArgumentException("A full data root is required.", nameof(dataRoot));
        _disk = new ManagedImageCache(dataRoot); _disk.Guard(_disk.Root);
        _publishCache = publishShortCacheAction; _formalResourceLease = formalResourceLease;
    }

    public Task<PreparedMediaAsset> PrepareMediaAsync(string path, CancellationToken ct = default)
    {
        lock (_workGate)
        {
            if (_cacheStopping || _disposed || ct.IsCancellationRequested)
                return Task.FromCanceled<PreparedMediaAsset>(new CancellationToken(true));
            var request = CancellationTokenSource.CreateLinkedTokenSource(ct, _cacheLifetime.Token);
            return TrackCacheWork(PrepareMediaCoreAsync(path, request));
        }
    }

    private static async Task<PreparedMediaAsset> PrepareMediaCoreAsync(string path, CancellationTokenSource request)
    {
        using (request)
            return await Task.Run(async () => (await VerifyOriginalAsync(path, request.Token).ConfigureAwait(false)).Asset,
                request.Token).ConfigureAwait(false);
    }

    private sealed record VerifiedOriginal(PreparedMediaAsset Asset, byte[] Bytes);

    public Task<bool> VerifyPackageImageAsync(ReadOnlyMemory<byte> bytes, string extension, CancellationToken ct = default)
    {
        lock (_workGate)
        {
            if (_cacheStopping || _disposed || ct.IsCancellationRequested)
                return Task.FromCanceled<bool>(new CancellationToken(true));
            var request = CancellationTokenSource.CreateLinkedTokenSource(ct, _cacheLifetime.Token);
            return TrackCacheWork(VerifyPackageImageCoreAsync(bytes, extension, request));
        }
    }

    private static async Task<bool> VerifyPackageImageCoreAsync(ReadOnlyMemory<byte> bytes, string extension,
        CancellationTokenSource request)
    {
        using (request)
        {
            try
            {
                var actual = await Task.Run(() => ProbeOriginalBytesAsync(bytes, request.Token), request.Token).ConfigureAwait(false);
                return PreparedMediaAsset.NormalizeExtension(extension) == actual;
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or
                System.Runtime.InteropServices.COMException or OverflowException)
            {
                return false;
            }
        }
    }

    private static async Task<VerifiedOriginal> VerifyOriginalAsync(string path, CancellationToken ct)
    {
        var full = NormalizeImageFilePath(path) ?? throw new IOException("Invalid image path.");
        await using var source = BoundedOriginalFileRead.OpenRead(full);
        var bytes = await BoundedOriginalFileRead.ReadAsync(source, PreparedMediaAsset.MaximumBytes, ct).ConfigureAwait(false);
        var extension = await ProbeOriginalBytesAsync(bytes, ct).ConfigureAwait(false);
        // The decoder probe and PreparedMediaAsset hash both consume this one frozen byte array.
        return new(PreparedMediaAsset.FromVerifiedImage(bytes, extension), bytes);
    }

    private static async Task<string> ProbeOriginalBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (bytes.Length is < 1 or > PreparedMediaAsset.MaximumBytes)
            throw new InvalidDataException("Image byte length exceeds the source budget.");
        ct.ThrowIfCancellationRequested();
        using var stream = new InMemoryRandomAccessStream();
        var buffer = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bytes, out var segment) &&
            segment.Offset == 0 && segment.Count == segment.Array!.Length ? segment.Array : bytes.ToArray();
        using (var writer = new DataWriter(stream)) { writer.WriteBytes(buffer); await writer.StoreAsync().AsTask(ct).ConfigureAwait(false); writer.DetachStream(); }
        stream.Seek(0); var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 || (long)decoder.PixelWidth * decoder.PixelHeight > 80_000_000)
            throw new InvalidDataException("Image dimensions exceed the source budget.");
        var sample = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform { ScaledWidth = 1, ScaledHeight = 1 }, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(ct).ConfigureAwait(false);
        if (sample.DetachPixelData().Length != 4) throw new InvalidDataException("Image cannot be decoded.");
        ct.ThrowIfCancellationRequested();
        var extension = decoder.DecoderInformation.FileExtensions.FirstOrDefault()
            ?? throw new InvalidDataException("Image codec supplies no file extension.");
        return PreparedMediaAsset.NormalizeExtension(extension);
    }

    private string? ManagedMediaPath(ManagedMediaReference? media)
    {
        if (_disk is null || !PreparedMediaAsset.IsValidReference(media)) return null;
        return _disk.Under("Media", media!.Sha256, "original" + media.Extension);
    }

    private IDisposable? OpenManagedGalleryRead(MemberImageSource source, string? selected = null)
    {
        if (source.ManagedGallery is not { } reference) return null;
        if (_disk is not { } disk || !PreparedManagedGallery.IsValidReference(reference))
            throw new IOException("Invalid managed gallery reference.");
        var directory = disk.Under("Sources", reference.ContentId);
        if (!StringComparer.OrdinalIgnoreCase.Equals(directory, NormalizePath(source.Path)))
            throw new IOException("Managed gallery path differs from its reference.");
        var resources = new FormalImageRead();
        try
        {
            if (_formalResourceLease is { } acquire) resources.Items.Add(acquire(reference, null));
            FileStream Open(string path, long limit)
            {
                disk.Guard(path);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                resources.Items.Add(stream);
                if (stream.Length is < 1 || stream.Length > limit) throw new IOException("Oversized managed gallery file.");
                return stream;
            }
            var manifestRead = Open(Path.Combine(directory, "manifest.json"), 1024 * 1024);
            var bytes = new byte[checked((int)manifestRead.Length)]; manifestRead.ReadExactly(bytes);
            var manifest = PreparedManagedGallery.ReadManifest(bytes, reference);
            if (selected is null)
            {
                disk.Guard(directory);
                var actual = Directory.EnumerateFileSystemEntries(directory).ToArray();
                var allowed = manifest.Files.Select(file => file.RelativePath).Append("manifest.json").ToHashSet(StringComparer.Ordinal);
                if (actual.Length != allowed.Count || actual.Any(path => !allowed.Contains(Path.GetFileName(path))))
                    throw new IOException("Managed gallery contains unexpected files.");
            }
            var candidates = selected is null ? manifest.Files : manifest.Files.Where(file =>
                StringComparer.OrdinalIgnoreCase.Equals(Path.Combine(directory, file.RelativePath), selected)).ToArray();
            if (selected is not null && !candidates.Any()) throw new IOException("Image is outside its managed gallery.");
            foreach (var item in candidates)
            {
                var stream = Open(Path.Combine(directory, item.RelativePath), PreparedMediaAsset.MaximumBytes);
                if (stream.Length != item.Media.Length || Convert.ToHexString(SHA256.HashData(stream)) != item.Media.Sha256)
                    throw new IOException("Managed gallery original differs from its digest.");
                stream.Position = 0;
            }
            return resources;
        }
        catch { resources.Dispose(); throw; }
    }

    private sealed class FormalImageRead : IDisposable
    {
        internal List<IDisposable> Items { get; } = [];
        public void Dispose()
        {
            for (var index = Items.Count - 1; index >= 0; index--) Items[index].Dispose();
            Items.Clear();
        }
    }
    public Task<WallImageResult> LoadBackgroundAsync(ManagedMediaReference? media, string? sourcePath,
        int widthPixels, int heightPixels, CancellationToken ct = default) =>
        LoadAsync(token => PrepareFileAsync(media is null ? sourcePath : ManagedMediaPath(media),
            "background", widthPixels, heightPixels, token, media), widthPixels, heightPixels, ct);

    private (FileStream? Lease, string? Diagnostic) OpenVerifiedMedia(string path, ManagedMediaReference media)
    {
        FileStream? lease = null;
        try
        {
            if (_disk is not { } disk || !PreparedMediaAsset.IsValidReference(media) ||
                !StringComparer.OrdinalIgnoreCase.Equals(path, ManagedMediaPath(media)))
                return (null, "InvalidManagedMediaReference");
            disk.Guard(path);
            // Keep the verified file open without write/delete sharing through decode
            // and cache acceptance. The decoder must read this same file version.
            lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (lease.Length != media.Length) return (null, "ManagedMediaLengthMismatch");
            var actualHash = Convert.ToHexString(SHA256.HashData(lease));
            lease.Position = 0;
            if (actualHash != media.Sha256) return (null, "ManagedMediaHashMismatch");
            var accepted = lease;
            lease = null;
            return (accepted, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { return (null, "ManagedMediaReadFailed:" + ex.GetType().Name); }
        finally { lease?.Dispose(); }
    }

    private Task<T> TrackCacheWork<T>(Task<T> task)
    {
        TrackCacheWork((Task)task);
        return task;
    }

    // Public display requests own their complete caller-thread continuation. Shared
    // producers register separately and use only the service lifetime, so cancelling
    // one caller never abandons a producer another caller is still awaiting.
    private Task<T> StartImageRequest<T>(Func<CancellationToken, Task<T>> start, CancellationToken ct, T cancelled)
    {
        lock (_workGate)
        {
            if (_cacheStopping || _disposed || ct.IsCancellationRequested) return Task.FromResult(cancelled);
            var request = CancellationTokenSource.CreateLinkedTokenSource(ct, _cacheLifetime.Token);
            return TrackCacheWork(CompleteImageRequestAsync(start, request, cancelled));
        }
    }

    private static async Task<T> CompleteImageRequestAsync<T>(Func<CancellationToken, Task<T>> start,
        CancellationTokenSource request, T cancelled)
    {
        using (request)
        {
            try { return await start(request.Token); }
            catch (OperationCanceledException) { return cancelled; }
        }
    }
    private Task TrackCacheWork(Task task)
    {
        lock (_workGate) _cacheWork.Add(task);
        _ = task.ContinueWith(completed => { lock (_workGate) _cacheWork.Remove(completed); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }
    private async Task PublishCacheAsync(Action action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); _cacheLifetime.Token.ThrowIfCancellationRequested();
        var publish = _publishCache ?? throw new InvalidOperationException("Image storage is not configured.");
        await publish(() =>
        {
            ct.ThrowIfCancellationRequested(); _cacheLifetime.Token.ThrowIfCancellationRequested();
            if (_cacheStopping || _disposed) return;
            action();
        }).ConfigureAwait(false);
    }
    public async Task ShutdownAsync()
    {
        lock (_workGate) _cacheStopping = true;
        _cacheLifetime.Cancel();
        while (true)
        {
            Task[] pending;
            lock (_workGate)
            {
                // A WhenAll continuation may run inline before the task's removal
                // continuation. Prune here too, so draining never spins on an
                // already completed task while preventing that continuation from running.
                _cacheWork.RemoveWhere(task => task.IsCompleted);
                pending = _cacheWork.ToArray();
            }
            if (pending.Length == 0) return;
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch (Exception) { /* Cache requests report their own recoverable errors; shutdown still drains them. */ }
        }
    }

    private async Task PublishBytesAsync(string destination, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (_disk is not { } disk) return;
        var staged = await disk.StageAsync(bytes, ct).ConfigureAwait(false);
        try { await PublishCacheAsync(() => disk.Publish(staged, destination), ct).ConfigureAwait(false); }
        finally { disk.Delete(staged); }
    }

    private string FileContentHash(string path)
    {
        if (_disk is { } disk && path.StartsWith(disk.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) disk.Guard(path);
        var key = Fingerprint(path, "verified-image-content");
        lock (_workGate) if (key is not null && _verifiedContentHashes.TryGetValue(key, out var cached)) return cached;
        using var stream = File.OpenRead(path); var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (key != Fingerprint(path, "verified-image-content")) throw new IOException("Image changed while its content was verified.");
        lock (_workGate)
        {
            if (_cacheStopping) return hash;
            if (_verifiedContentHashes.Count >= 2048) _verifiedContentHashes.Clear();
            if (key is not null) _verifiedContentHashes[key] = hash;
        }
        return hash;
    }
}
