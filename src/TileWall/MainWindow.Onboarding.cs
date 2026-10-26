using TileWall.Core.Application;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Shell;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using TileWall.Resources;
using TileWall.Shell.Interop;

namespace TileWall;

public sealed partial class MainWindow
{
    private WallDocument? _freshWelcomeDocument;
    private bool _welcomeInitializationPending;
    private CancellationTokenSource? _welcomePreparation;
    private Task<string?>? _welcomeInitializationTask;

    // Called immediately after Open, before rendering can enqueue an environment adaptation.
    private void BeginFreshWelcomeInitialization(WallOpenResult result)
    {
        _freshWelcomeDocument = result.InitializedFresh ? result.Document : null;
        _welcomeInitializationPending = _freshWelcomeDocument is not null;
    }

    internal Task<string?> InitializeWelcomeWallAsync(bool fresh, CancellationToken token = default)
    {
        if (fresh && _freshWelcomeDocument is null) return Task.FromResult<string?>(null);
        if (_welcomeInitializationTask is { IsCompleted: false })
            return Task.FromResult<string?>(ProductStrings.Get("Wall.SettingsBlocked"));
        Task<string?> Reject()
        {
            if (fresh) { _freshWelcomeDocument = null; _welcomeInitializationPending = false; }
            return Task.FromResult<string?>(ProductStrings.Get("Wall.SettingsBlocked"));
        }
        if (!fresh && _welcomeInitializationPending || PersistenceInteractionBlocked ||
            !_hasDocument || _viewBlocked || SettingsPersistenceBlocked || ShutdownStarted || _windowClosing)
            return Reject();
        if (token.IsCancellationRequested)
        {
            if (fresh) { _freshWelcomeDocument = null; _welcomeInitializationPending = false; }
            return Task.FromCanceled<string?>(token);
        }
        var before = Document;
        if (fresh && (!ReferenceEquals(before, _freshWelcomeDocument) || before.EnumerateAllMembers().Any()))
            return Reject();
        _freshWelcomeDocument = null; // A failed first preparation is not retried in this process.
        _welcomeInitializationPending = true;
        var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        _welcomePreparation = request;
        return _welcomeInitializationTask = InitializeWelcomeWallCoreAsync(before, fresh, request);
    }

    internal void CancelWelcomeInitialization() => _welcomePreparation?.Cancel();

    internal async Task WaitForWelcomeInitializationAsync()
    {
        if (_welcomeInitializationTask is not { } pending) return;
        try { await pending; }
        catch (OperationCanceledException) { }
    }

    private async Task<string?> InitializeWelcomeWallCoreAsync(WallDocument before, bool fresh,
        CancellationTokenSource request)
    {
        var token = request.Token;
        try
        {
            var targets = await Task.Run(() => ReadWelcomeTargets(token), token);
            RequireCurrentWelcomeSource(before, token);
            var originals = new List<PreparedMediaAsset>();
            foreach (var name in WelcomeWallBuilder.SeasonFileNames)
            {
                var assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WelcomeWall", "Seasons", name);
                originals.Add(await _images.PrepareMediaAsync(assetPath, token));
                RequireCurrentWelcomeSource(before, token);
            }
            var gallery = await Task.Run(() => WelcomeWallGallerySource.Prepare(
                new LocalFileStore(), _session.DataRoot, originals, token), token);
            RequireCurrentWelcomeSource(before, token);
            var currentTargets = await Task.Run(() => ReadWelcomeTargets(token), token);
            RequireCurrentWelcomeSource(before, token);
            if (!targets.Items.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .SequenceEqual(currentTargets.Items.OrderBy(item => item.Key, StringComparer.Ordinal)))
                throw new IOException("A welcome target changed during preparation.");
            string Text(string key) => ProductStrings.Get("Welcome." + key, before.Settings.LanguageMode);
            var text = new WelcomeWallText(WelcomeWallBuilder.NativeTargetKeys.Append("Settings")
                .ToDictionary(key => key, Text, StringComparer.Ordinal));
            var prepared = WelcomeWallBuilder.Prepare(before, targets, text, _session.ManagedGalleryPath(gallery.Reference),
                ViewportBudget(before.Settings), _metrics, gallery.Reference);
            if (prepared.Layout.Proposal is not { } proposal) return ViewportFailure(prepared.Layout);
            RequireCurrentWelcomeSource(before, token);
            // This is the acceptance boundary. Cancellation after it cannot abandon the joint transaction.
            var error = await CommitProposalAsync(proposal, prepared.EntryChanges, managedGalleries: [gallery]);
            if (error is not null) AppLog.Warn("WelcomeWall", (fresh ? "First initialization: " : "Manual initialization: ") + error);
            return error;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn("WelcomeWall", ex.Message);
            return string.Format(ProductStrings.Get("Welcome.Failed", before.Settings.LanguageMode), ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_welcomePreparation, request)) _welcomePreparation = null;
            request.Dispose();
            _welcomeInitializationPending = false;
            if (!_windowClosing && !ShutdownStarted && !_persistenceAdmissionClosed) QueueEnvironmentAdaptation();
        }
    }

    private void RequireCurrentWelcomeSource(WallDocument before, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_windowClosing || ShutdownStarted || _persistenceAdmissionClosed || !_hasDocument ||
            _viewBlocked || SettingsPersistenceBlocked || !ReferenceEquals(Document, before) ||
            Document.Generation != before.Generation)
            throw new InvalidOperationException(ProductStrings.Get("Wall.SettingsBlocked"));
    }

    private static WelcomeWallTargets ReadWelcomeTargets(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var targets = new Dictionary<string, WelcomeWallTarget>(StringComparer.Ordinal);
        // The same native Shell names as SystemFeatureCatalog preserve redirected Known Folders.
        foreach (var (key, parsingName) in new[]
        {
            ("ThisPC", "shell:MyComputerFolder"), ("ControlPanel", "shell:ControlPanelFolder"),
            ("RecycleBin", "shell:RecycleBinFolder"), ("Network", "shell:NetworkPlacesFolder"),
            ("Desktop", "shell:Desktop"), ("Downloads", "shell:Downloads"), ("Videos", "shell:My Video"),
            ("Pictures", "shell:My Pictures"), ("Documents", "shell:Personal"), ("Music", "shell:My Music"),
        })
        {
            token.ThrowIfCancellationRequested();
            var item = ShellItemCatalog.Read(parsingName);
            targets.Add(key, item.Path is { } path
                ? new(path) : new(item.ParsingName, LaunchIdentityService.ForShell(item.ParsingName).Key));
        }
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (var (key, name) in new[] { ("Calculator", "calc.exe"), ("Notepad", "notepad.exe"), ("CMD", "cmd.exe") })
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(system, name);
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
                throw new IOException("A required Windows application is unavailable: " + name);
            targets.Add(key, new(path));
        }
        var applications = ShellItemCatalog.Enumerate("shell:AppsFolder", token);
        var store = applications.FirstOrDefault(item => AppId(item).StartsWith("Microsoft.WindowsStore_", StringComparison.OrdinalIgnoreCase));
        var edge = applications.Where(item => AppId(item).Equals("MSEdge", StringComparison.OrdinalIgnoreCase) ||
                AppId(item).StartsWith("MSEdge.UserData.", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => AppId(item).Equals("MSEdge", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(AppId, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        AddApplication("Store", store);
        AddApplication("Edge", edge);
        foreach (var url in WelcomeWallBuilder.WebsiteUrls.Concat(
            [WelcomeWallBuilder.SettingsUrl, WelcomeWallBuilder.ProjectUrl, WelcomeWallBuilder.DiscussionsUrl]))
        {
            token.ThrowIfCancellationRequested();
            if (!RegisteredUrlProtocols.IsSupported(url)) throw new IOException("A required URL protocol is unavailable: " + url);
        }
        token.ThrowIfCancellationRequested();
        return new(targets);

        static string AppId(ShellItemCatalog.Item item) => item.ParsingName.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)
            ? item.ParsingName["shell:AppsFolder\\".Length..] : item.ParsingName;
        void AddApplication(string key, ShellItemCatalog.Item? item)
        {
            token.ThrowIfCancellationRequested();
            if (item is null) throw new IOException("A required installed application is unavailable: " + key);
            var parsingName = item.ParsingName.StartsWith("::", StringComparison.Ordinal) ||
                item.ParsingName.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                    ? item.ParsingName : "shell:AppsFolder\\" + item.ParsingName;
            var actual = ShellItemCatalog.Read(parsingName);
            var identity = actual.Path is { } path ? LaunchIdentityService.ForPath(path) : LaunchIdentityService.ForShell(actual.ParsingName);
            targets.Add(key, new(parsingName, identity.Key));
        }
    }
}
