using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using TileWall.Resources;
using TileWall.Core.Shell;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TileWall.Core.Entries;
using TileWall.Core.Configuration;
using TileWall.Core.Grid;
using TileWall.Core.Import;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using TileWall.Shell.Interop;

namespace TileWall.ApplicationModel;

internal enum NativeImportTarget { Shortcut, Path, Shell, Uri }
internal sealed class ImportSourceChangedException(string message, Exception? inner = null) : IOException(message, inner);
internal sealed record NativeImportEntry(string Key, string Name, string Target, NativeImportTarget Kind,
    string Group, bool SystemFeature, bool Directory, string? SourcePath = null, string? Sha256 = null,
    string? Stamp = null, LaunchIdentity? Identity = null, string? Error = null, int Priority = 0,
    ImportTargetAvailability Availability = ImportTargetAvailability.Unknown, bool TargetIsDirectory = false,
    StartMenuExclusion Exclusion = StartMenuExclusion.None, bool Stale = false)
{
    /// <summary>Localized resource key for a catalog-provided group; never used as its identity.</summary>
    public string? GroupTitleKey { get; init; }
    /// <summary>Stable display order within the source catalog.</summary>
    public int GroupOrder { get; init; }
    /// <summary>Actual AppsFolder membership facts, independent of the resolved launch identity.</summary>
    public ShellItemCatalog.Item? AppSource { get; init; }
    public string? Company { get; init; }
    /// <summary>EXE metadata dependency; its path is fixed by Identity.Target.</summary>
    public string? CompanyStamp { get; init; }
    /// <summary>Explicit Shell link metadata, used only for availability; never a shortcut source or launch identity.</summary>
    public string? LinkedTargetPath { get; init; }
}

/// <summary>Disposable source facts. Cache contents never authorize an import or contain user selection.</summary>
internal sealed class NativeImportCatalog(string dataRoot, ILnkFileService links)
{
    private const int MaximumEntries = 50000;
    private const int MaximumCacheBytes = 16 * 1024 * 1024;
    private readonly string _dataRoot = Path.GetFullPath(dataRoot);
    private readonly object _sync = new();
    private readonly Dictionary<ImportPageSourceKind, IReadOnlyList<NativeImportEntry>> _snapshots = [];
    private readonly Dictionary<ImportPageSourceKind, int> _skipped = [];
    private readonly Dictionary<ImportPageSourceKind, string> _scopes = [];
    private readonly Dictionary<ImportPageSourceKind, bool> _refreshing = [];
    private readonly Dictionary<ImportPageSourceKind, string?> _errors = [];
    private const int CacheVersion = 3, SystemCatalogVersion = 4, AppsCatalogVersion = 4, OtherNativeCatalogVersion = 1;
    private readonly Dictionary<ImportPageSourceKind, SemaphoreSlim> _refreshGates = [];
    private readonly Dictionary<ImportPageSourceKind, (IReadOnlyList<NativeImportEntry> Entries, ImportPageSource Source)> _projections = [];

    internal bool TryMemory(ImportPageSourceKind kind, out IReadOnlyList<NativeImportEntry> entries)
    {
        lock (_sync) return _snapshots.TryGetValue(kind, out entries!);
    }

    internal IReadOnlyList<NativeImportEntry> Snapshot(ImportPageSourceKind kind)
    { lock (_sync) return _snapshots.GetValueOrDefault(kind) ?? []; }

    internal Task<IReadOnlyList<NativeImportEntry>> CachedAsync(ImportPageSourceKind kind, CancellationToken token) =>
        Task.Run(() => { token.ThrowIfCancellationRequested(); var entries = Cached(kind); token.ThrowIfCancellationRequested(); return entries; }, token);

    private IReadOnlyList<NativeImportEntry> Cached(ImportPageSourceKind kind)
    {
        var scope = CacheScope(kind);
        lock (_sync)
            if (_snapshots.TryGetValue(kind, out var cached) && _scopes.GetValueOrDefault(kind) == scope) return cached;
        try
        {
            var path = CachePath(kind);
            if (!File.Exists(path) || UnsafeCache(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > MaximumCacheBytes) return [];
            var value = JsonSerializer.Deserialize<CacheEnvelope>(stream);
            if (UnsafeCache(path)) return [];
            if (value is not null && (value.Version == CacheVersion && value.Scope == scope ||
                    kind == ImportPageSourceKind.Desktop && value.Version == 2 && value.Scope == LegacyCacheScope()) && value.Kind == kind &&
                value.Entries is { Count: <= MaximumEntries } && value.Entries.All(ValidCachedEntry) &&
                value.Entries.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count() == value.Entries.Count)
            {
                var stale = value.Entries.Select(entry => entry with { Stale = true }).ToArray();
                lock (_sync) { _snapshots[kind] = stale; _scopes[kind] = scope; }
                return stale;
            }
        }
        catch (Exception ex) when (SourceReadError(ex) || ex is JsonException) { }
        return [];
    }

    internal Task<IReadOnlyList<NativeImportEntry>> ReadAsync(ImportPageSourceKind kind, CancellationToken token,
        Action? changed = null)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync) _refreshing[kind] = true;
        return Task.Run(async () =>
    {
        SemaphoreSlim gate;
        lock (_sync)
        {
            if (!_refreshGates.TryGetValue(kind, out gate!)) _refreshGates[kind] = gate = new SemaphoreSlim(1, 1);
        }
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var clock = Stopwatch.StartNew();
            var previous = Cached(kind);
            var scope = CacheScope(kind);
            var observed = new Dictionary<string, NativeImportEntry>(StringComparer.Ordinal);
            var targets = new Dictionary<string, StartMenuImportProjection.TargetObservation>(StringComparer.OrdinalIgnoreCase);
            var companies = new Dictionary<string, (string? Company, string? Stamp)>(StringComparer.OrdinalIgnoreCase);
            var progress = Stopwatch.StartNew();
            double? firstFactsMs = null;
            var skipped = 0;
            var complete = false;
            string? error = null;
            Publish(previous.Select(entry => entry with { Stale = true }).ToArray(), refreshing: true);
            void Observe(NativeImportEntry entry)
            {
                token.ThrowIfCancellationRequested();
                if (observed.Count >= MaximumEntries && !observed.ContainsKey(entry.Key)) { skipped++; return; }
                var current = ObserveTarget(entry with { Stale = false }, kind, token, targets);
                observed[entry.Key] = kind == ImportPageSourceKind.InstalledApplications ? ObserveCompany(current, token, companies) : current;
                if (observed.Count == 1 || progress.ElapsedMilliseconds >= 150)
                {
                    Publish(Merge(completeScan: false), refreshing: true);
                    firstFactsMs ??= clock.Elapsed.TotalMilliseconds; progress.Restart();
                }
            }
            IReadOnlyList<NativeImportEntry> Merge(bool completeScan)
            {
                var merged = new Dictionary<string, NativeImportEntry>(observed, StringComparer.Ordinal);
                if (!completeScan)
                    foreach (var prior in previous)
                        merged.TryAdd(prior.Key, prior with { Stale = true });
                return merged.Values.ToArray();
            }
            void Publish(IReadOnlyList<NativeImportEntry> entries, bool refreshing)
            {
                token.ThrowIfCancellationRequested();
                if (CacheScope(kind) != scope) throw new OperationCanceledException("Native source scope changed.", token);
                var notify = true;
                lock (_sync)
                {
                    token.ThrowIfCancellationRequested();
                    if (_snapshots.TryGetValue(kind, out var prior) && prior.SequenceEqual(entries)) entries = prior;
                    notify = !ReferenceEquals(_snapshots.GetValueOrDefault(kind), entries) || _refreshing.GetValueOrDefault(kind) != refreshing ||
                        _skipped.GetValueOrDefault(kind) != skipped || _errors.GetValueOrDefault(kind) != error;
                    _snapshots[kind] = entries; _scopes[kind] = scope;
                    _skipped[kind] = skipped; _refreshing[kind] = refreshing; _errors[kind] = error;
                }
                if (notify) changed?.Invoke();
            }
            try
            {
                if (kind == ImportPageSourceKind.InstalledApplications)
                    complete = await ReadAppsAsync(Observe, previous, token).ConfigureAwait(false);
                else
                {
                    var entries = kind == ImportPageSourceKind.Desktop ? Desktop(token, out skipped) : SystemFeatureCatalog.Read(token);
                    foreach (var entry in entries) Observe(entry);
                    complete = skipped == 0 && !entries.Any(entry => entry.Key == Key("system|control-panel-unavailable"));
                }
            }
            catch (Exception ex) when (SourceReadError(ex)) { error = "RefreshFailed"; }
            token.ThrowIfCancellationRequested();
            var result = Merge(complete);
            var factsReadyMs = clock.Elapsed.TotalMilliseconds;
            Publish(result, refreshing: false);
            Save(kind, result, scope, token);
            AppLog.Warn("ImportSources", JsonSerializer.Serialize(new { source = kind.ToString(), firstFactsMs,
                factsReadyMs, completeMs = clock.Elapsed.TotalMilliseconds, count = result.Count, targetReads = targets.Count, complete, skipped }));
            return Snapshot(kind);
        }
        finally
        {
            lock (_sync) _refreshing[kind] = false;
            gate.Release();
        }
    }, token);
    }

    private async Task<bool> ReadAppsAsync(Action<NativeImportEntry> observe, IReadOnlyList<NativeImportEntry> previous, CancellationToken token)
    {
        var prior = previous.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        using var enumeration = CancellationTokenSource.CreateLinkedTokenSource(token);
        var batches = Channel.CreateBounded<IReadOnlyList<ShellItemCatalog.Item>>(new BoundedChannelOptions(8)
            { SingleReader = true, SingleWriter = true });
        var complete = false;
        Exception? failure = null;
        var producer = Task.Run(() =>
        {
            try
            {
                complete = ShellItemCatalog.EnumerateBatches("shell:AppsFolder", batch =>
                    batches.Writer.WriteAsync(batch, enumeration.Token).AsTask().GetAwaiter().GetResult(), enumeration.Token);
            }
            catch (Exception ex) { failure = ex; }
            finally { batches.Writer.TryComplete(); }
        }, token);
        try
        {
            await foreach (var batch in batches.Reader.ReadAllAsync(token).ConfigureAwait(false))
                foreach (var item in batch)
                {
                    token.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(item.ParsingName))
                        observe(AppEntry(item, token, prior.GetValueOrDefault(Key("app|" + item.ParsingName))));
                }
        }
        finally
        {
            enumeration.Cancel();
            try { await producer.ConfigureAwait(false); }
            catch (OperationCanceledException) when (enumeration.IsCancellationRequested) { }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return complete;
    }

    internal ImportPageSource Project(ImportPageSourceKind kind, IReadOnlyList<NativeImportEntry> entries)
    {
        lock (_sync)
        {
        var refreshing = _refreshing.GetValueOrDefault(kind) || !ReferenceEquals(_snapshots.GetValueOrDefault(kind), entries);
        var skipped = _skipped.GetValueOrDefault(kind); var error = _errors.GetValueOrDefault(kind);
        _projections.TryGetValue(kind, out var priorProjection);
        if (ReferenceEquals(priorProjection.Entries, entries))
            return priorProjection.Source with { Refreshing = refreshing, Skipped = skipped, ErrorCode = error };
        var previousRows = priorProjection.Source?.Candidates.ToDictionary(row => row.SourceKey, StringComparer.Ordinal);
        var rows = entries.Select((entry, index) =>
        {
            var identity = entry.Identity;
            var urlEntry = entry.Kind == NativeImportTarget.Uri || entry.Kind == NativeImportTarget.Shortcut &&
                EntryNames.KindOfRelativePath(entry.SourcePath ?? entry.Target) == EntryKind.Url;
            var id = "source-" + entry.Key;
            var policy = entry.SystemFeature ? ImportSourcePolicy.SystemFeature : kind == ImportPageSourceKind.InstalledApplications
                ? ImportSourcePolicy.Application : entry.Kind == NativeImportTarget.Shortcut
                ? ImportSourcePolicy.Shortcut : ImportSourcePolicy.DirectTarget;
            var targetKind = entry.SystemFeature || entry.Exclusion == StartMenuExclusion.WindowsBuiltIn ? ImportTargetKind.SystemFeature :
                identity is null || identity.Key.StartsWith("opaque", StringComparison.Ordinal) ? ImportTargetKind.Unknown :
                entry.Directory || entry.TargetIsDirectory ? ImportTargetKind.Folder :
                identity.IsUrl ? (identity.Target.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || identity.Target.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
                    ? ImportTargetKind.WebLink : ImportTargetKind.ProtocolLink) :
                entry.Kind == NativeImportTarget.Shell && !Path.IsPathFullyQualified(identity.Target) ? ImportTargetKind.Program :
                entry.Availability != ImportTargetAvailability.Available ? ImportTargetKind.Unknown :
                Path.GetExtension(identity.Target).ToLowerInvariant() is ".exe" or ".com"
                    ? ImportTargetKind.Program : ImportTargetKind.File;
            return new ImportSourceCandidate(entry.Key, entry.Name, new TileMember
            {
                Id = id, LocalBounds = new(0, 0, 1, 1), TargetInput = entry.SourcePath ?? entry.Target,
                Entry = new(id, $"Objects/{id}/entry.{(urlEntry ? "url" : "lnk")}",
                    urlEntry ? ManagedEntryKind.Url : ManagedEntryKind.Lnk),
                Visual = new MemberVisual { Title = entry.Name, TitleMode = MemberTitleMode.Manual },
            })
            {
                SourceKind = kind, Policy = policy, TargetKind = targetKind, HasLaunchTarget = true,
                SourcePriority = entry.Priority, SourceOrder = index, SourceGroup = SourceGroup(entry),
                Identity = identity, RequiresIdentity = true, ErrorCode = entry.Error ?? (entry.Stale ? "SourceChanged" : null),
                Exclusion = entry.Exclusion,
                TargetAvailability = entry.Availability,
                Company = entry.Company,
            };
        }).ToArray();
        if (previousRows is not null)
            for (var index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                if (previousRows.TryGetValue(row.SourceKey, out var prior) && row.Member.TargetInput == prior.Member.TargetInput && row.Member.Entry?.Kind == prior.Member.Entry?.Kind &&
                    (row with { Member = prior.Member }) == prior) rows[index] = prior;
            }
        IReadOnlyList<ImportSourceCandidate> candidates = priorProjection.Source is { } priorSource && priorSource.Candidates.Count == rows.Length &&
            rows.Select((row, index) => ReferenceEquals(row, priorSource.Candidates[index])).All(same => same) ? priorSource.Candidates : rows;
        var result = new ImportPageSource(candidates, new Dictionary<string, TileWall.Core.Application.StartMenuImportCandidate>(), Scanned: entries.Count, Skipped: skipped,
            ReadErrors: entries.Count(entry => entry.Error is not null and not "SourceNotApplicable"), Refreshing: refreshing, ErrorCode: error)
        { AvailabilityPrepared = true, AvailabilityFacts = entries.ToDictionary(entry => entry.Key, Facts, StringComparer.Ordinal) };
        _projections[kind] = (entries, result);
        return result;
        }
    }

    private static ImportSourceGroup? SourceGroup(NativeImportEntry entry) => entry.Group.Length == 0 ? null :
        new(entry.Group, entry.GroupTitleKey is { Length: > 0 } titleKey ? ProductStrings.Get(titleKey) : entry.Group,
            entry.SystemFeature ? entry.GroupOrder : 100);

    internal static string Facts(NativeImportEntry entry) => JsonSerializer.Serialize(entry);

    internal NativeImportEntry Find(ImportPageSourceKind kind, string key)
    { lock (_sync) return (_snapshots.GetValueOrDefault(kind) ?? []).SingleOrDefault(entry => entry.Key == key)
        ?? throw new ImportSourceChangedException("The selected source no longer exists."); }

    internal NativeImportEntry Confirm(ImportPageSourceKind kind, string key) => ConfirmMany(kind, [key])[key];

    internal IReadOnlyDictionary<string, NativeImportEntry> ConfirmMany(ImportPageSourceKind kind, IReadOnlyCollection<string> keys)
    {
        try
        {
            var selected = keys.Distinct(StringComparer.Ordinal).ToDictionary(key => key, key => Find(kind, key), StringComparer.Ordinal);
            if (selected.Count == 0) return selected;
            if (kind != ImportPageSourceKind.InstalledApplications)
                return selected.Keys.ToDictionary(key => key, key => ConfirmCurrent(kind, key), StringComparer.Ordinal);
            if (selected.Values.Any(entry => entry.Error is not null || entry.Stale || entry.AppSource is null))
                throw new IOException("The source is unavailable.");
            // Membership belongs to the actual AppsFolder enumeration. Opening an arbitrary
            // filesystem parsing name alone would not prove that it is still an app source.
            var items = new Dictionary<string, ShellItemCatalog.Item>(StringComparer.Ordinal);
            var complete = ShellItemCatalog.EnumerateBatches("shell:AppsFolder", batch =>
            {
                foreach (var item in batch)
                {
                    var key = Key("app|" + item.ParsingName);
                    if (selected.ContainsKey(key)) items[key] = item;
                }
            }, CancellationToken.None);
            if (!complete || items.Count != selected.Count) throw new IOException("The app source changed.");
            var targets = new Dictionary<string, StartMenuImportProjection.TargetObservation>(StringComparer.OrdinalIgnoreCase);
            var companies = new Dictionary<string, (string? Company, string? Stamp)>(StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, NativeImportEntry>(StringComparer.Ordinal);
            foreach (var (key, old) in selected)
            {
                var item = items[key];
                if (item != old.AppSource) throw new IOException("The app source changed.");
                var current = ObserveCompany(ObserveTarget(AppEntry(item, CancellationToken.None), kind, CancellationToken.None, targets),
                    CancellationToken.None, companies);
                if (Facts(current) != Facts(old)) throw new IOException("The app facts changed.");
                result.Add(key, current);
            }
            return result;
        }
        catch (Exception ex) when (SourceReadError(ex))
        { throw new ImportSourceChangedException("The selected source changed or is unavailable.", ex); }
    }

    private NativeImportEntry ConfirmCurrent(ImportPageSourceKind kind, string key)
    {
        var old = Find(kind, key);
        if (old.Error is not null || old.Stale) throw new IOException("The source is unavailable.");
        if (kind == ImportPageSourceKind.Desktop)
        {
            ValidateDesktopPath(old.SourcePath ?? old.Target);
        }
        if (old.Kind == NativeImportTarget.Shortcut)
        {
            if (old.SourcePath is null) throw new IOException("The source changed.");
            var observed = ObserveShortcut(old.SourcePath, CancellationToken.None);
            if (observed.Hash != old.Sha256 || observed.Identity?.Key != old.Identity?.Key || Stamp(old.SourcePath) != old.Stamp)
                throw new IOException("The shortcut changed.");
            if (observed.Identity is { IsUrl: true } url && !url.Key.StartsWith("opaque", StringComparison.Ordinal) &&
                !RegisteredUrlProtocols.IsSupported(url.Target)) throw new IOException("The shortcut protocol is unavailable.");
        }
        else if (old.Kind == NativeImportTarget.Path)
        {
            var attributes = File.GetAttributes(old.Target);
            if (((attributes & FileAttributes.Directory) != 0) != old.Directory ||
                old.Stamp is not null && Stamp(old.Target) != old.Stamp) throw new IOException("The target changed.");
        }
        else if (old.Kind == NativeImportTarget.Shell)
        {
            var (_, identity) = ShellItemCatalog.ReadForLink(old.Target);
            if (identity.Key != (old.Identity ?? LaunchIdentityService.ForShell(old.Target)).Key) throw new IOException("The Shell target changed.");
        }
        else if (!RegisteredUrlProtocols.IsSupported(old.Target)) throw new IOException("The native protocol is unavailable.");
        if (kind == ImportPageSourceKind.Desktop) ValidateDesktopPath(old.SourcePath ?? old.Target);
        return old;
    }

    private IReadOnlyList<NativeImportEntry> Desktop(CancellationToken token, out int skipped)
    {
        skipped = 0;
        var result = new List<NativeImportEntry>();
        var old = Cached(ImportPageSourceKind.Desktop).ToDictionary(entry => entry.Key);
        foreach (var (root, priority) in new[] { (KnownFolderPaths.Desktop, 0), (KnownFolderPaths.PublicDesktop, 1) })
        {
            if (root is null) continue;
            token.ThrowIfCancellationRequested();
            string[] paths;
            try
            {
                ValidateDirectoryRoot(root);
                paths = Directory.EnumerateFileSystemEntries(root).Take(MaximumEntries + 1).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                ValidateDirectoryRoot(root);
            }
            catch (Exception ex) when (SourceReadError(ex))
            {
                skipped++;
                // Retain known rows only as disabled diagnostics; an unreadable root
                // must never make its cached entries eligible for confirmation.
                foreach (var prior in old.Values.Where(entry => SameParent(entry.SourcePath ?? entry.Target, root)).Take(MaximumEntries - result.Count))
                    result.Add(prior with { Error = "SourceUnavailable" });
                continue;
            }
            foreach (var path in paths)
            {
                if (result.Count >= MaximumEntries) { skipped++; break; }
                token.ThrowIfCancellationRequested();
                var key = Key("desktop|" + root + "|" + Path.GetFileName(path));
                var entry = new NativeImportEntry(key, Path.GetFileNameWithoutExtension(path), path,
                    NativeImportTarget.Path, "", false, false, Priority: priority);
                try
                {
                    ValidateDesktopPath(path);
                    var directory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
                    var shortcut = !directory && EntryNames.KindOfRelativePath(path) != EntryKind.None;
                    var stamp = Stamp(path);
                    entry = entry with { Name = directory ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path),
                        Directory = directory, Kind = shortcut ? NativeImportTarget.Shortcut : NativeImportTarget.Path,
                        SourcePath = shortcut ? path : null, Stamp = stamp };
                    if (shortcut)
                    {
                        // Always observe bytes. Equal timestamps and lengths do not
                        // prove a shortcut still has the same target or icon metadata.
                        old.TryGetValue(key, out var prior);
                        var observed = ObserveShortcut(path, token, prior);
                        entry = entry with { Sha256 = observed.Hash, Identity = observed.Identity };
                        if (entry.Identity is null) entry = entry with { Error = "InvalidTarget" };
                    }
                    else entry = entry with { Identity = LaunchIdentityService.ForPath(path) };
                    if (Stamp(path) != stamp) throw new IOException("The source changed while reading.");
                    ValidateDesktopPath(path);
                }
                catch (Exception ex) when (SourceReadError(ex))
                { entry = entry with { Error = "UnsafeSource" }; }
                result.Add(entry);
            }
        }
        return result;
    }

    private NativeImportEntry AppEntry(ShellItemCatalog.Item item, CancellationToken token, NativeImportEntry? prior = null)
    {
        var parsing = item.ParsingName;
        var target = AppSourcePath(item) ?? parsing;
        NativeImportTarget kind;
        if (Path.IsPathFullyQualified(target)) kind = NativeImportTarget.Path;
        else if (target.StartsWith("::", StringComparison.Ordinal) || target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) kind = NativeImportTarget.Shell;
        // A URI exposed by AppsFolder is still this registered Shell item. Its
        // parent supplies instance icon/activation facts which a bare URI loses.
        else { target = "shell:AppsFolder\\" + parsing; kind = NativeImportTarget.Shell; }
        var entry = new NativeImportEntry(Key("app|" + parsing), item.Name, target, kind, "", false, item.IsFolder)
            { AppSource = item };
        try
        {
            token.ThrowIfCancellationRequested();
            if (kind == NativeImportTarget.Path)
            {
                ValidateAppPath(target, item);
                var directory = (File.GetAttributes(target) & FileAttributes.Directory) != 0;
                var stamp = Stamp(target);
                var shortcut = !directory && EntryNames.KindOfRelativePath(target) != EntryKind.None;
                entry = entry with { Directory = directory, Stamp = stamp,
                    Kind = shortcut ? NativeImportTarget.Shortcut : NativeImportTarget.Path, SourcePath = shortcut ? target : null };
                if (shortcut)
                {
                    var observed = ObserveShortcut(target, token, prior, path => ValidateAppPath(path, item));
                    entry = entry with { Identity = observed.Identity, Sha256 = observed.Hash };
                }
                else entry = entry with { Identity = LaunchIdentityService.ForPath(target) };
                if (Stamp(target) != stamp) throw new IOException("The app source changed while reading.");
                token.ThrowIfCancellationRequested(); ValidateAppPath(target, item);
                return entry;
            }
            var identity = LaunchIdentityService.ForShell(parsing);
            token.ThrowIfCancellationRequested();
            return entry with { Identity = identity, LinkedTargetPath = ShellItemCatalog.LinkedTargetPath(target) };
        }
        catch (Exception ex) when ((ex is FileNotFoundException or DirectoryNotFoundException) &&
            kind == NativeImportTarget.Path && EntryNames.KindOfRelativePath(target) == EntryKind.None)
        {
            try
            {
                // A registered direct target may be provably missing. Keep its path
                // identity for the common missing-target filter; a missing shortcut
                // cannot supply its launch identity and remains an invalid source.
                ValidateDirectoryRoot(Path.GetDirectoryName(Path.GetFullPath(target))!);
                var identity = LaunchIdentityService.ForPath(target);
                token.ThrowIfCancellationRequested();
                if (StartMenuImportProjection.ObserveTarget(identity).Availability == ImportTargetAvailability.Missing)
                    return entry with { Identity = identity, Stamp = null };
            }
            catch (Exception missingError) when (SourceReadError(missingError)) { }
            return entry with { Error = "InvalidTarget" };
        }
        catch (Exception ex) when (SourceReadError(ex)) { return entry with { Error = "InvalidTarget" }; }
    }

    private void Save(ImportPageSourceKind kind, IReadOnlyList<NativeImportEntry> entries, string scope, CancellationToken token)
    {
        string? temporary = null;
        try
        {
            var path = CachePath(kind);
            token.ThrowIfCancellationRequested();
            if (UnsafeCache(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (UnsafeCache(path)) return;
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            if (UnsafeCache(temporary)) return;
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new CacheEnvelope(CacheVersion, scope, kind, entries));
                if (bytes.Length > MaximumCacheBytes) return;
                token.ThrowIfCancellationRequested(); stream.Write(bytes); stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (UnsafeCache(path) || UnsafeCache(temporary) || CacheScope(kind) != scope) return;
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (SourceReadError(ex)) { }
        finally
        {
            try { if (temporary is not null && !UnsafeCache(temporary) && File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (SourceReadError(ex)) { }
        }
    }
    private string CachePath(ImportPageSourceKind kind) => Path.Combine(_dataRoot, "Cache", "ImportSources", kind + ".json");
    private bool UnsafeCache(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(_dataRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        return false;
    }
    private static string Stamp(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            var directory = new DirectoryInfo(path);
            return $"directory:{directory.CreationTimeUtc.Ticks}:{directory.LastWriteTimeUtc.Ticks}";
        }
        var info = new FileInfo(path); return $"file:{info.Length}:{info.CreationTimeUtc.Ticks}:{info.LastWriteTimeUtc.Ticks}";
    }

    private (string Hash, LaunchIdentity? Identity) ObserveShortcut(string path, CancellationToken token, NativeImportEntry? prior = null,
        Action<string>? validatePath = null)
    {
        validatePath ??= ValidateDesktopPath;
        validatePath(path);
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new IOException("The shortcut became a directory.");
        // The COM reader opens the same path while this handle denies replacement.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > StartMenuSourceCache.MaximumSourceBytes) throw new IOException("Invalid shortcut size.");
        var bytes = new byte[checked((int)stream.Length)];
        token.ThrowIfCancellationRequested(); stream.ReadExactly(bytes);
        validatePath(path);
        var hash = Hash(bytes);
        var identity = prior is { Error: null, Identity: not null, Kind: NativeImportTarget.Shortcut } &&
            prior.Sha256 == hash && string.Equals(prior.SourcePath, path, StringComparison.OrdinalIgnoreCase)
            ? prior.Identity : ShellItemCatalog.InApartment(() => EntryObservation.Observe(new LaunchIdentityService(new LocalFileStore(), links),
                EntryNames.KindOfRelativePath(path) == EntryKind.Url ? ManagedEntryKind.Url : ManagedEntryKind.Lnk, path, bytes));
        token.ThrowIfCancellationRequested(); validatePath(path);
        return (hash, identity);
    }

    private static NativeImportEntry ObserveTarget(NativeImportEntry entry, ImportPageSourceKind kind, CancellationToken token,
        IDictionary<string, StartMenuImportProjection.TargetObservation> observations)
    {
        token.ThrowIfCancellationRequested();
        var identity = entry.Identity;
        if (identity is null && entry.Error is null)
        {
            if (entry.Kind == NativeImportTarget.Uri) identity = LaunchIdentityService.ForUrl(entry.Target);
            else if (entry.Kind == NativeImportTarget.Path) identity = LaunchIdentityService.ForPath(entry.Target);
        }
        var availability = ImportTargetAvailability.Unknown;
        var directory = false;
        if (entry.Error is null && kind == ImportPageSourceKind.InstalledApplications &&
            entry.Kind == NativeImportTarget.Shell && entry.LinkedTargetPath is { } linked)
        {
            if (!observations.TryGetValue(linked, out var observation))
            { observation = ObserveLinkedTarget(linked); observations.Add(linked, observation); }
            directory = observation.IsDirectory == true; availability = observation.Availability;
        }
        else if (entry.Error is null && identity is not null && !identity.IsUrl && !identity.Key.StartsWith("opaque", StringComparison.Ordinal) && Path.IsPathFullyQualified(identity.Target))
        {
            if (entry.Kind == NativeImportTarget.Path && entry.Stamp is not null)
            { directory = entry.Directory; availability = ImportTargetAvailability.Available; }
            else
            {
                if (!observations.TryGetValue(identity.Target, out var observation))
                { observation = StartMenuImportProjection.ObserveTarget(identity); observations.Add(identity.Target, observation); }
                directory = observation.IsDirectory == true; availability = observation.Availability;
            }
        }
        var error = entry.Error ?? (EntryNames.Validate(entry.Name).Count != 0 ? "InvalidTarget" : null);
        if (error is null && identity is { IsUrl: true } url && !url.Key.StartsWith("opaque", StringComparison.Ordinal) &&
            !RegisteredUrlProtocols.IsSupported(url.Target)) error = "UnsupportedProtocol";
        var exclusion = identity is not null && (entry.Kind == NativeImportTarget.Shortcut || kind == ImportPageSourceKind.InstalledApplications)
            ? StartMenuSelectionRules.Classify(entry.Name, identity, entry.Directory || directory) : StartMenuExclusion.None;
        token.ThrowIfCancellationRequested();
        return entry with { Identity = identity, Availability = availability, TargetIsDirectory = directory,
            Error = error, Exclusion = exclusion };
    }

    private static StartMenuImportProjection.TargetObservation ObserveLinkedTarget(string path)
    {
        var unknown = new StartMenuImportProjection.TargetObservation(ImportTargetAvailability.Unknown, null);
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (parent is null) return unknown;
            ValidateDirectoryRoot(parent);
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) != 0 ? unknown :
                new(ImportTargetAvailability.Available, (attributes & FileAttributes.Directory) != 0);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A missing intermediate directory can still be proved absent from
            // its nearest readable, non-redirected ancestor. No path/name guess
            // is involved: every component comes from the Shell's link metadata.
            var absent = path;
            for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            {
                try
                {
                    ValidateDirectoryRoot(parent);
                    var attributes = File.GetAttributes(parent);
                    if ((attributes & FileAttributes.Directory) == 0) return unknown;
                    try { _ = File.GetAttributes(absent); return unknown; }
                    catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
                    { return new(ImportTargetAvailability.Missing, null); }
                }
                catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
                { absent = parent; }
                catch (Exception uncertain) when (SourceReadError(uncertain)) { return unknown; }
            }
        }
        catch (Exception ex) when (SourceReadError(ex)) { }
        return unknown;
    }

    private static NativeImportEntry ObserveCompany(NativeImportEntry entry, CancellationToken token,
        IDictionary<string, (string? Company, string? Stamp)> observations)
    {
        token.ThrowIfCancellationRequested();
        var identity = entry.Identity;
        if (entry.Error is not null || identity is null || identity.IsUrl || identity.Key.StartsWith("opaque", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(identity.Target) || !Path.GetExtension(identity.Target).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return entry with { Company = null, CompanyStamp = null };
        if (!observations.TryGetValue(identity.Target, out var fact))
        {
            fact = ReadCompany(identity.Target, token);
            observations.Add(identity.Target, fact);
        }
        token.ThrowIfCancellationRequested();
        return entry with { Company = fact.Company, CompanyStamp = fact.Stamp };
    }

    private static (string? Company, string? Stamp) ReadCompany(string path, CancellationToken token)
    {
        try
        {
            ValidateFilePath(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var stamp = Stamp(path);
            token.ThrowIfCancellationRequested();
            var company = FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim();
            token.ThrowIfCancellationRequested();
            ValidateFilePath(path);
            if (Stamp(path) != stamp) throw new IOException("The executable changed while reading its company.");
            return (string.IsNullOrEmpty(company) ? null : company, stamp);
        }
        catch (Exception ex) when (SourceReadError(ex) || ex is System.ComponentModel.Win32Exception)
        { return default; }
    }

    private static string? AppSourcePath(ShellItemCatalog.Item item) =>
        item.Path is { } path && Path.IsPathFullyQualified(path) ? path :
        Path.IsPathFullyQualified(item.ParsingName) ? item.ParsingName : null;

    private static void ValidateAppPath(string path, ShellItemCatalog.Item item)
    {
        if (AppSourcePath(item) is not { } source || !Path.IsPathFullyQualified(path) ||
            !Path.GetFullPath(path).Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The app source path changed.");
        ValidateDirectoryRoot(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse source is unavailable.");
    }

    private static void ValidateFilePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("The file path is unavailable.");
        ValidateDirectoryRoot(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("The file is unavailable or redirected through a reparse point.");
    }

    private static void ValidateDesktopPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !new[] { KnownFolderPaths.Desktop, KnownFolderPaths.PublicDesktop }
                .Any(root => root is not null && SameParent(path, root)))
            throw new IOException("The desktop root changed.");
        ValidateDirectoryRoot(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse source is unavailable.");
    }

    private static bool SameParent(string path, string root) => Path.IsPathFullyQualified(path) &&
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase);

    private static void ValidateDirectoryRoot(string root)
    {
        for (var current = Path.GetFullPath(root); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
                throw new IOException("The source root is unavailable or redirected through a reparse point.");
    }

    private string CacheScope(ImportPageSourceKind kind)
    {
        using var account = System.Security.Principal.WindowsIdentity.GetCurrent();
        return JsonSerializer.Serialize(new { Account = account.User?.Value, Root = _dataRoot,
            Desktop = KnownFolderPaths.Desktop, PublicDesktop = KnownFolderPaths.PublicDesktop,
            OS = Environment.OSVersion.VersionString, Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            Language = ProductStrings.Language.ToString(), UICulture = CultureInfo.CurrentUICulture.Name,
            SystemCatalogVersion = kind == ImportPageSourceKind.SystemFeatures ? SystemCatalogVersion :
                kind == ImportPageSourceKind.InstalledApplications ? AppsCatalogVersion : OtherNativeCatalogVersion });
    }

    private string LegacyCacheScope()
    {
        using var account = System.Security.Principal.WindowsIdentity.GetCurrent();
        return JsonSerializer.Serialize(new { Account = account.User?.Value, Root = _dataRoot,
            Desktop = KnownFolderPaths.Desktop, PublicDesktop = KnownFolderPaths.PublicDesktop });
    }

    private static bool ValidCachedEntry(NativeImportEntry entry) => entry is not null &&
        entry.Key is { Length: 64 } && entry.Key.All(Uri.IsHexDigit) &&
        entry.Name is { Length: > 0 and <= 32768 } && entry.Target is { Length: > 0 and <= 32768 } &&
        entry.Group is { Length: <= 32768 } && Enum.IsDefined(entry.Kind) && Enum.IsDefined(entry.Availability) && Enum.IsDefined(entry.Exclusion) &&
        (entry.SourcePath is null || entry.SourcePath.Length <= 32768) &&
        (entry.Stamp is null || entry.Stamp.Length <= 32768) &&
        (entry.Sha256 is null || entry.Sha256.Length == 64 && entry.Sha256.All(Uri.IsHexDigit)) &&
        (entry.Error is null || entry.Error.Length <= 1024) &&
        (entry.Company is null || entry.Company.Length <= 32768) &&
        (entry.CompanyStamp is null || entry.CompanyStamp.Length <= 32768) &&
        (entry.LinkedTargetPath is null || entry.LinkedTargetPath.Length is > 0 and < 32768 && Path.IsPathFullyQualified(entry.LinkedTargetPath)) &&
        (entry.AppSource is null || entry.AppSource.Name is { Length: > 0 and <= 32768 } &&
            entry.AppSource.ParsingName is { Length: > 0 and <= 32768 } &&
            (entry.AppSource.Path is null || entry.AppSource.Path.Length <= 32768) &&
            entry.Key == Key("app|" + entry.AppSource.ParsingName)) &&
        (entry.Identity is null || entry.Identity.Key is { Length: > 0 and <= 131072 } &&
            entry.Identity.Target is { Length: > 0 and <= 32768 } && entry.Identity.Arguments is { Length: <= 32768 } &&
            entry.Identity.WorkingDirectory is { Length: <= 32768 });

    private static bool SourceReadError(Exception exception) => exception is IOException or UnauthorizedAccessException or ArgumentException
        or System.Runtime.InteropServices.COMException or System.Security.SecurityException;
    internal static string Key(string value) => Hash(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private sealed record CacheEnvelope(int Version, string Scope, ImportPageSourceKind Kind, IReadOnlyList<NativeImportEntry> Entries);
}
