using System.Diagnostics;
using System.Text.Json;
using TileWall.Core.Application;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Import;
using TileWall.Core.Shell;

namespace TileWall.ApplicationModel;

/// <summary>Primary-session-owned catalog. It never calls WallSession or writes a formal member/entry.</summary>
internal sealed class StartMenuImportCoordinator
{
    private readonly object _sync = new();
    private readonly string _dataRoot;
    private readonly string _cacheRoot;
    private readonly IWallStorageExecutor _executor;
    private readonly LaunchIdentityService _identities;
    private readonly ILnkFileService _links;
    private readonly CancellationTokenSource _lifetime = new();
    private StartMenuCatalogSnapshot _snapshot = new();
    private Task? _initialize, _refresh, _startup, _stop;
    private bool _stopping, _startupScheduled, _refreshAgain, _refreshActive;
    private long _refreshEpoch;

    public StartMenuImportCoordinator(string dataRoot, IWallStorageExecutor executor,
        LaunchIdentityService identities, ILnkFileService links)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _cacheRoot = Path.Combine(_dataRoot, "Cache", "StartMenu");
        _executor = executor; _identities = identities; _links = links;
    }

    public event Action? Changed;
    public Func<StartMenuCachedSource, string, CancellationToken, Task<string?>>? IconPrepare { get; set; }
    public StartMenuCatalogSnapshot Snapshot { get { lock (_sync) return _snapshot; } }

    public string IconsDirectory(StartMenuCandidate candidate) => Path.Combine(_dataRoot, "Cache", "Images", "Icons", "Packages");

    public void ScheduleStartupRefresh()
    {
        lock (_sync)
        {
            if (_stopping || _startupScheduled) return;
            _startupScheduled = true;
            _startup = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(45), _lifetime.Token).ConfigureAwait(false);
                    await RequestRefreshAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
            });
        }
    }

    public Task ReadCachedAsync()
    {
        lock (_sync)
        {
            if (_stopping) return Task.CompletedTask;
            return _initialize ??= Task.Run(ReadCache, _lifetime.Token);
        }
    }

    /// <summary>The returned task belongs to the primary session. A page cancels only its own WaitAsync/subscription.</summary>
    public Task RequestRefreshAsync(bool afterCurrent = false)
    {
        lock (_sync)
        {
            if (_stopping) return Task.CompletedTask;
            if (_refreshActive && _refresh is { } running)
            {
                _refreshAgain |= afterCurrent;
                return running;
            }
            _refreshActive = true;
            var epoch = ++_refreshEpoch;
            return _refresh = Task.Run(async () =>
            {
                try
                {
                    do
                    {
                        await RefreshAsync().ConfigureAwait(false);
                        lock (_sync)
                        {
                            if (_stopping || !_refreshAgain) { _refreshActive = false; return; }
                            _refreshAgain = false;
                        }
                    } while (true);
                }
                finally { lock (_sync) { if (_refreshEpoch == epoch) _refreshActive = false; } }
            });
        }
    }

    public void InvalidateSource(StartMenuCandidate candidate)
    {
        var snapshot = Snapshot;
        Publish(snapshot with { Sources = snapshot.Sources.Select(source => source.Candidate.SourceKey == candidate.SourceKey
            ? source with { Stale = true, ErrorCode = "SourceChanged" } : source).ToArray() });
        _ = RequestRefreshAsync(afterCurrent: true);
    }

    public Task StopAsync()
    {
        lock (_sync)
        {
            if (_stop is not null) return _stop;
            _stopping = true;
            _lifetime.Cancel();
            var pending = new[] { _initialize, _refresh, _startup }.OfType<Task>().ToArray();
            return _stop = DrainAsync(pending);
        }
    }

    private static async Task DrainAsync(Task[] pending)
    {
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Warn("StartMenuCache", $"Cache shutdown completed after {error.GetType().Name}."); }
    }

    private void ReadCache()
    {
        var token = _lifetime.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var path = CachePath("index.json");
            CheckCachePath(path, createDirectories: false);
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("CacheTooLarge");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > 16 * 1024 * 1024) throw new IOException("CacheTooLarge");
            var snapshot = JsonSerializer.Deserialize<StartMenuCatalogSnapshot>(stream);
            CheckCachePath(path, createDirectories: false);
            var known = new TileWall.Shell.Interop.KnownFolderPaths();
            var roots = new Dictionary<SourceKind, string?> { [SourceKind.User] = known.UserPrograms, [SourceKind.Common] = known.CommonPrograms };
            if (snapshot is null || snapshot.FormatVersion != StartMenuSourceCache.FormatVersion ||
                snapshot.Sources is null || snapshot.Roots is null || snapshot.Sources.Count > 50000 ||
                snapshot.ScannedCount < 0 || snapshot.SkippedDirectories < 0 ||
                snapshot.Roots.Any(root => !IsCachedRootValid(root)) ||
                snapshot.Roots.Select(root => root.Source).Distinct().Count() != snapshot.Roots.Count ||
                snapshot.Sources.Any(source => !IsCachedSourceValid(source)) ||
                snapshot.Sources.Select(source => source.Candidate.SourceKey).Distinct(StringComparer.Ordinal).Count() != snapshot.Sources.Count)
                throw new IOException("InvalidCache");
            Publish(snapshot with
            {
                Sources = snapshot.Sources.Where(source => roots[source.Candidate.Source] is not { } current ||
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(current)).Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Candidate.RootPath!)), StringComparison.OrdinalIgnoreCase))
                    .Select(source => source with { Stale = true }).ToArray(),
                Refreshing = false, IconPreparing = false, ErrorCode = null,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
        {
            AppLog.Warn("StartMenuCache", $"Cache rebuild required: {ex.GetType().Name}");
            Publish(new() { ErrorCode = "CacheInvalid" });
        }
    }

    private static bool IsCachedSourceValid(StartMenuCachedSource source)
    {
        if (source is null || source.Candidate is null || source.Candidate.RootPath is not { Length: > 0 } root ||
            source.Candidate.DisplayName is null || source.Candidate.RelativeDir is null || source.SourceSha256 is null ||
            !Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(source.Candidate.FullPath) ||
            !StartMenuSourceCache.Within(root, source.Candidate.FullPath) ||
            !Enum.IsDefined(source.Candidate.Source) || source.Candidate.Kind is not (EntryKind.Lnk or EntryKind.Url) ||
            EntryNames.KindOfRelativePath(source.Candidate.FullPath) != source.Candidate.Kind ||
            source.Candidate.RelativePath.Split('\\', '/').Any(segment => segment is "" or "." or ".." || segment.Contains(':')) ||
            !Enum.IsDefined(source.TargetAvailability) || source.Dependencies is null || source.Dependencies.Any(dependency => !IsCachedDependencyValid(dependency)) ||
            source.Candidate.NotImportableReasons is null || source.Candidate.NotImportableReasons.Any(string.IsNullOrWhiteSpace) ||
            source.LinkFields is { } fields && (fields.Arguments is null || fields.WorkingDirectory is null) ||
            source.Identity is { } identity && (string.IsNullOrEmpty(identity.Key) || identity.Target is null ||
                identity.Arguments is null || identity.WorkingDirectory is null) ||
            source.ErrorCode is null && !StartMenuSourceCache.IsHash(source.SourceSha256)) return false;
        return true;
    }

    private static bool IsCachedDependencyValid(StartMenuDependencyStamp? dependency) =>
        dependency is not null && !string.IsNullOrWhiteSpace(dependency.Path) && Path.IsPathFullyQualified(dependency.Path) &&
        dependency.Length >= 0 && dependency.LastWriteUtcTicks >= 0 && dependency.LastWriteUtcTicks <= DateTime.MaxValue.Ticks &&
        (dependency.Available || dependency.Length == 0 && dependency.LastWriteUtcTicks == 0);

    private static bool IsCachedRootValid(StartMenuRootScan? root) =>
        root is not null && Enum.IsDefined(root.Source) && root.RejectedPaths is not null &&
        (root.Path is { } rootPath ?
            !string.IsNullOrWhiteSpace(rootPath) && Path.IsPathFullyQualified(rootPath) &&
            root.RejectedPaths.All(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
                StartMenuSourceCache.Within(rootPath, path)) : !root.Complete && root.RejectedPaths.Count == 0);

    internal static bool SnapshotRootMatches(StartMenuCandidate candidate, bool requireAvailable)
    {
        var known = new TileWall.Shell.Interop.KnownFolderPaths();
        var current = candidate.Source == SourceKind.User ? known.UserPrograms : known.CommonPrograms;
        return current is null ? !requireAvailable : candidate.RootPath is { } root &&
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(current)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshAsync()
    {
        var token = _lifetime.Token;
        var elapsed = Stopwatch.StartNew();
        double? firstFactsMs = null;
        var targetReads = 0;
        try
        {
            await ReadCachedAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var previous = Snapshot;
            Publish(previous with { Refreshing = true, IconPreparing = false, ErrorCode = null });
            var scan = await StartMenuImportScan.RunAsync(token).ConfigureAwait(false);
            var old = previous.Sources.ToDictionary(source => source.Candidate.SourceKey, StringComparer.Ordinal);
            var versions = new Dictionary<StartMenuDependencyStamp, (string? Company, string? Version)>();
            foreach (var cached in previous.Sources.Where(source => source.ErrorCode is null))
                if (VersionStamp(cached, cached.Dependencies) is { } stamp)
                    versions.TryAdd(stamp, (cached.Company, cached.FileVersion));
            var protocols = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var targetObservations = new Dictionary<string, StartMenuImportProjection.TargetObservation>(StringComparer.OrdinalIgnoreCase);
            var observed = new List<StartMenuCachedSource>(scan.Candidates.Count);
            var icons = new List<(int Index, StartMenuCachedSource Source)>();
            var progress = Stopwatch.StartNew();
            foreach (var candidate in scan.Candidates.OrderBy(candidate => candidate.Source)
                         .ThenBy(candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                StartMenuCachedSource source;
                try
                {
                    if (!candidate.IsImportable) source = new()
                    {
                        Candidate = candidate, SourceSha256 = string.Empty,
                        ErrorCode = candidate.NotImportableReasons.FirstOrDefault() ?? "InvalidTarget",
                    };
                    else
                    {
                        var bytes = StartMenuImportScan.ReadSource(candidate, token);
                        old.TryGetValue(candidate.SourceKey, out var cached);
                        var sameContent = cached is not null && cached.SourceSha256.Equals(bytes.Sha256, StringComparison.OrdinalIgnoreCase) &&
                            cached.ErrorCode is null or "InvalidTarget" or "UnsupportedProtocol";
                        source = sameContent ? cached! with { Candidate = candidate, Stale = false, SourceSha256 = bytes.Sha256 } :
                            await StartMenuImportPreparation.PrepareSourceAsync(_executor, candidate, bytes.Bytes,
                                bytes.Sha256, _identities, _links, token).ConfigureAwait(false);
                        var before = DependencyStamps(source);
                        var dependenciesChanged = !sameContent || !before.SequenceEqual(source.Dependencies);
                        if (dependenciesChanged)
                        {
                            var stamp = VersionStamp(source, before);
                            var version = stamp is not null && versions.TryGetValue(stamp, out var knownVersion)
                                ? knownVersion : ReadVersion(source.Identity);
                            if (stamp is not null) versions[stamp] = version;
                            source = source with
                            {
                                Dependencies = before, Company = version.Company, FileVersion = version.Version, IconKey = null,
                            };
                        }
                        var after = StartMenuImportScan.ReadSource(candidate, token);
                        if (!bytes.Sha256.Equals(after.Sha256, StringComparison.OrdinalIgnoreCase) ||
                            !before.SequenceEqual(DependencyStamps(source))) throw new IOException("SourceChanged");
                        var target = source.Identity;
                        if (target is { IsUrl: true } && !target.Key.StartsWith("opaque", StringComparison.Ordinal))
                        {
                            var scheme = target.Target[..target.Target.IndexOf(':')];
                            if (!protocols.TryGetValue(scheme, out var supported))
                                protocols.Add(scheme, supported = TileWall.Shell.Interop.RegisteredUrlProtocols.IsSupported(target.Target));
                            source = source with { ErrorCode = supported ? source.ErrorCode == "UnsupportedProtocol" ? null : source.ErrorCode : "UnsupportedProtocol" };
                        }
                        var observation = new StartMenuImportProjection.TargetObservation(ImportTargetAvailability.Unknown, null);
                        if (target is not null && !target.IsUrl && Path.IsPathFullyQualified(target.Target) && !target.Key.StartsWith("opaque", StringComparison.Ordinal))
                        {
                            if (!targetObservations.TryGetValue(target.Target, out observation))
                            {
                                observation = StartMenuImportProjection.ObserveTarget(target);
                                targetObservations.Add(target.Target, observation); targetReads++;
                            }
                        }
                        source = source with { TargetAvailability = observation.Availability, TargetIsDirectory = observation.IsDirectory == true };
                        token.ThrowIfCancellationRequested();
                        try { WriteShortcutCopy(source, bytes.Bytes); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { AppLog.Warn("StartMenuCache", $"Source cache write deferred: {ex.GetType().Name}"); }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                    System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
                {
                    source = new()
                    {
                        Candidate = candidate, SourceSha256 = string.Empty, ErrorCode = SourceError(ex),
                    };
                }
                token.ThrowIfCancellationRequested();
                observed.Add(source);
                if (source.Identity is not null && source.ErrorCode is null && source.IconKey is null)
                    icons.Add((observed.Count - 1, source));
                if (observed.Count == 1 || progress.ElapsedMilliseconds >= 150)
                {
                    PublishProgress(previous, scan, observed);
                    firstFactsMs ??= elapsed.Elapsed.TotalMilliseconds;
                    progress.Restart();
                }
            }
            token.ThrowIfCancellationRequested();
            var facts = StartMenuSourceCache.Merge(previous, scan, observed, iconPreparing: IconPrepare is not null && icons.Count != 0);
            var factsReadyMs = elapsed.Elapsed.TotalMilliseconds;
            Publish(facts);
            TryWriteIndex(facts);
            // The whole candidate list is already usable. Native icon preparation may complete progressively.
            if (IconPrepare is { } prepareIcon)
            {
                foreach (var (index, source) in icons)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var directory = IconsDirectory(source.Candidate);
                        var key = await prepareIcon(source, directory, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        // An icon descriptor can never outlive its shortcut/dependencies.
                        var current = StartMenuImportScan.ReadSource(source.Candidate, token);
                        if (current.Sha256.Equals(source.SourceSha256, StringComparison.OrdinalIgnoreCase) &&
                            source.Dependencies.SequenceEqual(DependencyStamps(source)))
                            observed[index] = source with { IconKey = key };
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
                        System.Runtime.InteropServices.COMException)
                    { AppLog.Warn("StartMenuCache", $"Icon preparation deferred: {ex.GetType().Name}"); }
                    if (progress.ElapsedMilliseconds >= 150) { PublishProgress(previous, scan, observed, iconPreparing: true); progress.Restart(); }
                }
            }
            token.ThrowIfCancellationRequested();
            var complete = StartMenuSourceCache.Merge(previous, scan, observed);
            Publish(complete);
            TryWriteIndex(complete);
            AppLog.Warn("ImportSources", JsonSerializer.Serialize(new { source = "StartMenu", firstFactsMs, factsReadyMs, completeMs = elapsed.Elapsed.TotalMilliseconds,
                count = complete.Sources.Count, targetReads, iconsPrepared = icons.Count }));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
        {
            AppLog.Warn("StartMenuCache", $"Refresh failed: {ex.GetType().Name}");
            Publish(Snapshot with { Refreshing = false, IconPreparing = false, ErrorCode = "RefreshFailed" });
        }
    }

    private void PublishProgress(StartMenuCatalogSnapshot previous, StartMenuScanResult scan, List<StartMenuCachedSource> observed,
        bool iconPreparing = false) => Publish(StartMenuSourceCache.Merge(previous, iconPreparing ? scan : scan with
        { Roots = scan.Roots.Select(root => root with { Complete = false }).ToArray() }, observed,
            refreshing: !iconPreparing, iconPreparing: iconPreparing));

    private static IReadOnlyList<StartMenuDependencyStamp> DependencyStamps(StartMenuCachedSource source)
    {
        var paths = new[] { source.Identity?.IsUrl == false ? source.Identity.Target : null, source.LinkFields?.IconPath ?? source.UrlIconPath }
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Environment.ExpandEnvironmentVariables(path!))
            .Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase);
        var stamps = new List<StartMenuDependencyStamp>();
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                stamps.Add(new(path, info.Exists ? info.Length : 0, info.Exists ? info.LastWriteTimeUtc.Ticks : 0, info.Exists));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { stamps.Add(new(path, 0, 0, false)); }
        }
        return stamps;
    }

    private static (string? Company, string? Version) ReadVersion(LaunchIdentity? identity)
    {
        if (identity is null || identity.IsUrl || !Path.IsPathFullyQualified(identity.Target) ||
            !Path.GetExtension(identity.Target).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return default;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(identity.Target);
            return (info.CompanyName, info.FileVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        { return default; }
    }

    private static StartMenuDependencyStamp? VersionStamp(StartMenuCachedSource source, IReadOnlyList<StartMenuDependencyStamp> dependencies)
    {
        if (source.Identity is not { IsUrl: false } identity) return null;
        var stamp = dependencies.FirstOrDefault(dependency => dependency.Path.Equals(identity.Target, StringComparison.OrdinalIgnoreCase));
        return stamp is null ? null : stamp with { Path = stamp.Path.ToUpperInvariant() };
    }

    private void WriteShortcutCopy(StartMenuCachedSource source, byte[] bytes)
    {
        var name = source.Candidate.Kind == EntryKind.Lnk ? "source.lnk" : "source.url";
        var path = CachePath("Sources", source.Candidate.SourceKey, name);
        CheckCachePath(path, createDirectories: true);
        if (File.Exists(path) && new FileInfo(path).Length <= StartMenuSourceCache.MaximumSourceBytes &&
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
            .Equals(source.SourceSha256, StringComparison.OrdinalIgnoreCase)) return;
        WriteAtomic(path, bytes);
    }

    private void TryWriteIndex(StartMenuCatalogSnapshot snapshot)
    {
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            var path = CachePath("index.json");
            CheckCachePath(path, createDirectories: true);
            WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(snapshot with { Refreshing = false, IconPreparing = false }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("StartMenuCache", $"Index write deferred: {ex.GetType().Name}");
            Publish(snapshot with { ErrorCode = "CacheWriteFailed" });
        }
    }

    private void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            CheckCachePath(temporary, createDirectories: true);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            CheckCachePath(path, createDirectories: true);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CheckCachePath(temporary, createDirectories: false);
            File.Delete(temporary);
        }
    }

    private string CachePath(params string[] parts)
    {
        if (parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.IndexOfAny(['/', '\\', ':']) >= 0))
            throw new IOException("UnsafeCachePath");
        return Path.Combine([_cacheRoot, .. parts]);
    }

    private void CheckCachePath(string path, bool createDirectories)
    {
        if (!StartMenuSourceCache.Within(_cacheRoot, path)) throw new IOException("UnsafeCachePath");
        var local = new LocalFileStore();
        var rootInfo = local.GetPathInfo(_dataRoot);
        if (!rootInfo.Exists || !rootInfo.IsDirectory || rootInfo.IsReparsePoint) throw new IOException("UnsafeCachePath");
        var relative = Path.GetRelativePath(_dataRoot, path).Split(Path.DirectorySeparatorChar);
        var current = _dataRoot;
        for (var index = 0; index < relative.Length; index++)
        {
            current = Path.Combine(current, relative[index]);
            var info = local.GetPathInfo(current);
            var directory = index < relative.Length - 1;
            if (!info.Exists && createDirectories && directory)
            {
                Directory.CreateDirectory(current);
                info = local.GetPathInfo(current);
            }
            if (info.IsReparsePoint || info.Exists && info.IsDirectory != directory) throw new IOException("UnsafeCachePath");
        }
    }

    private void Publish(StartMenuCatalogSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_stopping || _lifetime.IsCancellationRequested) return;
            _snapshot = snapshot;
        }
        Changed?.Invoke();
    }

    private static string SourceError(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException => "SourceMissing",
        UnauthorizedAccessException => "SourceUnavailable",
        IOException { Message: "UnsafeSource" } => "UnsafeSource",
        IOException { Message: "SourceChanged" } => "SourceChanged",
        IOException { Message: "SourceTooLarge" } => "SourceTooLarge",
        _ => "SourceUnavailable",
    };
}
