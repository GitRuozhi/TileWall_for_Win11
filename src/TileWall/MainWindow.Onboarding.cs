using TileWall.Core.Application;
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
            var assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WelcomeWall", "brand-puzzle.png");
            var brand = await _images.PrepareMediaAsync(assetPath, token);
            RequireCurrentWelcomeSource(before, token);
            var currentTargets = await Task.Run(() => ReadWelcomeTargets(token), token);
            RequireCurrentWelcomeSource(before, token);
            if (currentTargets != targets) throw new IOException("A welcome target changed during preparation.");
            string Text(string key) => ProductStrings.Get("Welcome." + key, before.Settings.LanguageMode);
            var text = new WelcomeWallText(Text("Windows"), Text("Websites"), Text("Explore"),
                Text("Explorer"), Text("Settings"), Text("Downloads"), Text("Documents"),
                Text("Tutorial"), Text("Showcase"), Text("Brand"));
            var prepared = WelcomeWallBuilder.Prepare(before, targets, text, brand.Reference,
                ViewportBudget(before.Settings), _metrics);
            if (prepared.Layout.Proposal is not { } proposal) return ViewportFailure(prepared.Layout);
            RequireCurrentWelcomeSource(before, token);
            // This is the acceptance boundary. Cancellation after it cannot abandon the joint transaction.
            var error = await CommitProposalAsync(proposal, prepared.EntryChanges, preparedMedia: [brand]);
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
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var explorer = Path.Combine(windows, "explorer.exe");
        var downloads = KnownFolderPaths.Downloads;
        var documents = KnownFolderPaths.Documents;
        if (!Path.IsPathFullyQualified(explorer) || !File.Exists(explorer) ||
            downloads is null || !Path.IsPathFullyQualified(downloads) || !Directory.Exists(downloads) ||
            documents is null || !Path.IsPathFullyQualified(documents) || !Directory.Exists(documents))
            throw new IOException("A required Windows or known-folder target is unavailable.");
        foreach (var url in new[] { WelcomeWallBuilder.SettingsUrl, WelcomeWallBuilder.MicrosoftUrl,
            WelcomeWallBuilder.GitHubUrl, WelcomeWallBuilder.DiscussionsUrl })
        {
            token.ThrowIfCancellationRequested();
            if (!RegisteredUrlProtocols.IsSupported(url)) throw new IOException("A required URL protocol is unavailable: " + url);
        }
        token.ThrowIfCancellationRequested();
        return new(explorer, downloads, documents);
    }
}
