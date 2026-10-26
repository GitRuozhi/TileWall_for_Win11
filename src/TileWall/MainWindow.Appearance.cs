using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Views;
using TileWall.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TileWall;

public sealed partial class MainWindow
{
    private readonly UISettings _appearanceSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly Grid _backgroundLayer = new() { IsHitTestVisible = false };
    private readonly Image _backgroundImage = new() { Stretch = Stretch.UniformToFill };
    private readonly Image _snapshotImage = new() { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
    private readonly SemaphoreSlim _snapshotSlot = new(1, 1);
    private CancellationTokenSource? _snapshotRequest;
    // This window's UI thread retains the true original policy through restore failures.
    private uint? _snapshotAffinityToRestore;
    private (int X, int Y, int W, int H, double Scale, bool Dark)? _snapshotKey;
    private (int X, int Y, int W, int H, double Scale, bool Dark)? _snapshotRequestKey;
    private long _snapshotGeneration, _snapshotFocusCycle, _snapshotRequestedFocusCycle = -1;
    private bool _snapshotExternalFocus, _snapshotFocusLossRequest;
    private CancellationTokenSource? _backgroundRequest;
    private CancellationTokenSource? _backgroundCommitRequest;
    private Task<bool>? _backgroundCommitWork;
    private readonly HashSet<Task> _appearanceWork = [];
    private bool _appearanceStopping;
    private string? _backgroundPath;
    private bool _themeDark, _materialFallback = true, _materialReduced;
    private bool? _materialHighContrast;
    private bool _appearanceQueued, _highContrastSubscribed, _appearanceAfterPersistence;

    private void InitializeAppearance()
    {
        _wallBackdrop.WaitForFallbackCommit = EnsureBackgroundCommittedAsync;
        _backgroundLayer.Children.Add(_backgroundImage);
        _backgroundLayer.Children.Add(_snapshotImage);
        WallRoot.Children.Insert(0, _backgroundLayer);
        _appearanceSettings.ColorValuesChanged += OnSystemColorsChanged;
        _appearanceSettings.AdvancedEffectsEnabledChanged += OnAdvancedEffectsChanged;
        // Some unpackaged desktop hosts expose HighContrast but reject this optional
        // notification (ERROR_NOT_FOUND). UISettings and SystemBackdrop configuration
        // notifications remain the independent policy-refresh paths in that case.
        try
        {
            _accessibilitySettings.HighContrastChanged += OnHighContrastChanged;
            _highContrastSubscribed = true;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            AppLog.Warn("WallBackdrop", $"Optional high-contrast notification unavailable: 0x{ex.HResult:X8}");
        }
        RefreshAppearance();
    }

    /// <summary>Only committed Settings enter this method, including initial load and environment changes.</summary>
    internal void RefreshAppearance()
    {
        if (_windowClosing || _appearanceStopping) return;
        CancelBackgroundCommit();
        var settings = Settings;
        var foreground = _appearanceSettings.GetColorValue(UIColorType.Foreground);
        _themeDark = settings.ThemeMode == WallThemeMode.Dark || settings.ThemeMode == WallThemeMode.System &&
            (foreground.R * 299 + foreground.G * 587 + foreground.B * 114) > 128_000;
        var theme = _themeDark ? ElementTheme.Dark : ElementTheme.Light;
        if (WallRoot.RequestedTheme != theme) WallRoot.RequestedTheme = theme;
        _dialogHost?.ApplyTheme(theme);
        var contrast = _accessibilitySettings.HighContrast;
        var statusForeground = contrast
            ? _appearanceSettings.GetColorValue(UIColorType.Foreground)
            : _themeDark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        if (StatusText.Foreground is not SolidColorBrush foregroundBrush || !foregroundBrush.Color.Equals(statusForeground))
            StatusText.Foreground = new SolidColorBrush(statusForeground);
        var statusBackground = contrast
            ? _appearanceSettings.GetColorValue(UIColorType.Background)
            : _themeDark ? Color.FromArgb(240, 32, 32, 32) : Color.FromArgb(240, 243, 243, 243);
        if (StatusPanel.Background is not SolidColorBrush statusBrush || !statusBrush.Color.Equals(statusBackground))
            StatusPanel.Background = new SolidColorBrush(statusBackground);
        if (_snapshotRequestKey is not null && (settings.BackgroundMode != WallBackgroundMode.Acrylic || contrast ||
            !_appearanceSettings.AdvancedEffectsEnabled || _snapshotFocusLossRequest && !CanRefreshFocusLossSnapshot))
            CancelSnapshotRequest();
        InvalidateSnapshotIfGeometryChanged();
        _wallBackdrop.Configure(settings, _backdropActive, _themeDark, HasValidSnapshot);
        var path = settings.BackgroundMode == WallBackgroundMode.Custom ? settings.BackgroundMedia?.Sha256 ?? settings.BackgroundImagePath : null;
        if (!string.Equals(_backgroundPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _backgroundPath = path;
            _backgroundRequest?.Cancel();
            _backgroundRequest?.Dispose();
            _backgroundRequest = null;
            _backgroundImage.Source = null;
            if (path is not null)
            {
                _backgroundRequest = new CancellationTokenSource();
                TrackAppearanceWork(LoadCustomBackgroundAsync(path, settings, _backgroundRequest.Token));
            }
        }
        UpdateBackgroundLayer();
    }

    private void OnMaterialFallbackChanged(bool fallback, bool reduced)
    {
        if (!fallback) CancelBackgroundCommit();
        TraceInput($"Backdrop mode={Settings.BackgroundMode} active={_backdropActive} fallback={fallback} reduced={reduced} transparency={Settings.BackgroundTransparency}");
        var highContrast = _accessibilitySettings.HighContrast;
        var policyChanged = _materialReduced != reduced ||
            _materialHighContrast is { } previousContrast && previousContrast != highContrast || highContrast;
        _materialHighContrast = highContrast;
        _materialFallback = fallback;
        _materialReduced = reduced;
        UpdateBackgroundLayer();
        // XAML's default backdrop configuration also reports high-contrast changes;
        // update the roots/status colors even when AccessibilitySettings events fail.
        if (policyChanged) QueueAppearanceRefresh();
    }

    private Task<bool> EnsureBackgroundCommittedAsync()
    {
        if (_windowClosing || _appearanceStopping || !AppWindow.IsVisible) return Task.FromResult(true);
        if (_backgroundCommitWork is not null) return _backgroundCommitWork;
        _backgroundCommitRequest = new CancellationTokenSource();
        _backgroundCommitWork = CommitBackgroundAsync(_backgroundCommitRequest.Token);
        TrackAppearanceWork(_backgroundCommitWork);
        return _backgroundCommitWork;
    }

    private async Task<bool> CommitBackgroundAsync(CancellationToken token)
    {
        var renderedFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? rendering = null;
        EventHandler<RenderedEventArgs>? rendered = null;
        var renderingAttached = false;
        var renderedAttached = false;
        void Unsubscribe()
        {
            if (renderingAttached) { CompositionTarget.Rendering -= rendering; renderingAttached = false; }
            if (renderedAttached) { CompositionTarget.Rendered -= rendered; renderedAttached = false; }
        }
        rendered = (_, _) =>
        {
            Unsubscribe();
            renderedFrame.TrySetResult();
        };
        rendering = (_, _) =>
        {
            // Rendering precedes the frame. Observe its Rendered event instead of
            // removing native here, before XAML has submitted the replacement.
            if (renderedAttached) return;
            CompositionTarget.Rendered += rendered;
            renderedAttached = true;
        };
        try
        {
            CompositionTarget.Rendering += rendering;
            renderingAttached = true;
            TraceInput("BackdropCommit XAML wait-render");
            await renderedFrame.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            await CompositionTarget.GetCompositorForCurrentThread().RequestCommitAsync().AsTask(token);
            token.ThrowIfCancellationRequested();
            TraceInput("BackdropCommit XAML committed");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            AppLog.Warn("BackdropCommit", $"XAML commit unavailable: {ex.GetType().Name}");
            return false;
        }
        finally { Unsubscribe(); }
    }

    private void CancelBackgroundCommit()
    {
        _backgroundCommitRequest?.Cancel();
        _backgroundCommitRequest?.Dispose();
        _backgroundCommitRequest = null;
        _backgroundCommitWork = null;
    }

    private void UpdateBackgroundLayer()
    {
        if (_windowClosing || _appearanceStopping) return;
        var highContrast = _accessibilitySettings.HighContrast;
        var reduced = _materialReduced || highContrast || !_appearanceSettings.AdvancedEffectsEnabled;
        var settings = Settings;
        var snapshotReady = !reduced && settings.BackgroundMode == WallBackgroundMode.Acrylic && HasValidSnapshot &&
            (!_backdropActive || _materialFallback);
        var visibility = snapshotReady || _materialFallback || settings.BackgroundMode == WallBackgroundMode.Custom || reduced
            ? Visibility.Visible : Visibility.Collapsed;
        var opacity = reduced ? 1 : (100 - Math.Clamp(settings.BackgroundTransparency, 0, 100)) / 100d;
        if (_backgroundLayer.Opacity != opacity) _backgroundLayer.Opacity = opacity;
        var customImageReady = !reduced && settings.BackgroundMode == WallBackgroundMode.Custom &&
            _backgroundImage.Source is not null;
        // Draw exactly one background plane. Keeping a tinted plane underneath the
        // image would attenuate the desktop twice when XAML applies layer opacity.
        if (customImageReady || snapshotReady)
        {
            if (_backgroundLayer.Background is not null) _backgroundLayer.Background = null;
        }
        else
        {
            var color = highContrast ? _appearanceSettings.GetColorValue(UIColorType.Background)
                : _themeDark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
            if (_backgroundLayer.Background is not SolidColorBrush backgroundBrush || !backgroundBrush.Color.Equals(color))
                _backgroundLayer.Background = new SolidColorBrush(color);
        }
        var imageVisibility = customImageReady ? Visibility.Visible : Visibility.Collapsed;
        if (_backgroundImage.Visibility != imageVisibility) _backgroundImage.Visibility = imageVisibility;
        _snapshotImage.Visibility = snapshotReady ? Visibility.Visible : Visibility.Collapsed;
        // Publish visibility only after the replacement's image/solid and opacity
        // are ready; WallBackdrop then detaches the outgoing native material.
        if (_backgroundLayer.Visibility != visibility)
        {
            _backgroundLayer.Visibility = visibility;
            TraceInput($"BackdropLayer visible={visibility} snapshot={snapshotReady} custom={customImageReady} opacity={opacity:F2}");
        }
    }

    private async Task LoadCustomBackgroundAsync(string path, WallSettings settings, CancellationToken ct)
    {
        // The existing decoder bounds file/pixel budgets and only opens sources read-only.
        var result = await _images.LoadBackgroundAsync(settings.BackgroundMedia, settings.BackgroundImagePath, 2560, 1440, ct);
        if (ct.IsCancellationRequested || _windowClosing || !string.Equals(_backgroundPath, path, StringComparison.OrdinalIgnoreCase)) return;
        _backgroundImage.Source = result.Source;
        RefreshAppearance();
        if (result.Source is null) AppLog.Warn("WallBackdrop", $"Custom background unavailable: {result.Status}");
    }

    private (int X, int Y, int W, int H, double Scale, bool Dark) CurrentSnapshotKey =>
        (AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height, Scale, _themeDark);
    private bool HasValidSnapshot => _snapshotImage.Source is not null && _snapshotKey == CurrentSnapshotKey;

    private void InvalidateSnapshotIfGeometryChanged()
    {
        var key = CurrentSnapshotKey;
        if ((_snapshotKey is null || _snapshotKey == key) &&
            (_snapshotRequestKey is null || _snapshotRequestKey == key)) return;
        ReleaseBackdropSnapshot();
        _wallBackdrop.Configure(Settings, _backdropActive, _themeDark, false);
        UpdateBackgroundLayer();
    }

    private void ReleaseBackdropSnapshot()
    {
        CancelBackgroundCommit();
        CancelSnapshotRequest();
        if (!AppWindow.IsVisible) _snapshotExternalFocus = false;
        _snapshotKey = null;
        // Invalidating a visible snapshot must install the solid replacement before
        // removing its pixels. Hide/close use the same cancellation boundary.
        UpdateBackgroundLayer();
        _snapshotImage.Source = null;
        _snapshotImage.Visibility = Visibility.Collapsed;
    }

    private void CaptureBackdropBeforeShow()
    {
        if (_appearanceStopping || _windowClosing || AppWindow.IsVisible || _modal || Settings.BackgroundMode != WallBackgroundMode.Acrylic ||
            _accessibilitySettings.HighContrast || !_appearanceSettings.AdvancedEffectsEnabled) return;
        var key = CurrentSnapshotKey;
        BeginSnapshotRequest(key, focusLoss: false);
        var captured = DesktopBackdropSnapshot.Capture(new Windows.Graphics.RectInt32(key.X, key.Y, key.W, key.H));
        if (captured is not null)
            TrackAppearanceWork(PrepareSnapshotAsync(captured, key, _snapshotGeneration, null, _snapshotRequest!.Token));
    }

    private bool CanRefreshFocusLossSnapshot => !_appearanceStopping && !_windowClosing && !ShutdownStarted &&
        AppWindow.IsVisible && !_backdropActive && !_modal && !_contextMenuOpen &&
        Settings.StayVisibleOnFocusLoss && Settings.BackgroundMode == WallBackgroundMode.Acrylic &&
        !_accessibilitySettings.HighContrast && _appearanceSettings.AdvancedEffectsEnabled && IsForegroundOutsideWallSession();

    private void RefreshBackdropOnFocusLoss()
    {
        if (!_snapshotExternalFocus)
        {
            _snapshotExternalFocus = true;
            _snapshotFocusCycle++;
        }
        if (!CanRefreshFocusLossSnapshot || _snapshotRequestedFocusCycle == _snapshotFocusCycle) return;
        // Mark admission, including a failed capture, once per real external focus
        // cycle. Switching among external apps never starts a capture loop.
        _snapshotRequestedFocusCycle = _snapshotFocusCycle;
        var key = CurrentSnapshotKey;
        BeginSnapshotRequest(key, focusLoss: true);
        TraceInput($"BackdropSnapshot capture focusCycle={_snapshotFocusCycle} generation={_snapshotGeneration}");
        var capture = DesktopBackdropSnapshot.CaptureExcludingWindow(
            new Windows.Graphics.RectInt32(key.X, key.Y, key.W, key.H), WinRT.Interop.WindowNative.GetWindowHandle(this),
            ref _snapshotAffinityToRestore);
        if (capture is not null)
            TrackAppearanceWork(PrepareSnapshotAsync(capture, key, _snapshotGeneration, _snapshotFocusCycle, _snapshotRequest!.Token));
    }

    private void CancelFocusLossSnapshot()
    {
        _snapshotExternalFocus = false;
        // A hidden-before-show frame may still be preparing after first activation.
        // Its validity is geometric; only visible refreshes belong to a focus cycle.
        if (_snapshotFocusLossRequest) CancelSnapshotRequest();
    }

    private void CancelSnapshotRequest()
    {
        _snapshotGeneration++;
        _snapshotRequest?.Cancel(); _snapshotRequest?.Dispose(); _snapshotRequest = null;
        _snapshotRequestKey = null;
        _snapshotFocusLossRequest = false;
    }

    private void BeginSnapshotRequest((int X, int Y, int W, int H, double Scale, bool Dark) key, bool focusLoss)
    {
        CancelSnapshotRequest();
        _snapshotRequest = new();
        _snapshotRequestKey = key;
        _snapshotFocusLossRequest = focusLoss;
    }

    private async Task PrepareSnapshotAsync(DecodedImageFrame capture,
        (int X, int Y, int W, int H, double Scale, bool Dark) key,
        long generation, long? focusCycle, CancellationToken token)
    {
        try
        {
            await _snapshotSlot.WaitAsync(token);
            DecodedImageFrame blurred;
            try { blurred = await Task.Run(() => DesktopBackdropSnapshot.BlurAndTint(capture, key.Dark), token); }
            finally { _snapshotSlot.Release(); }
            bool IsCurrent() => !token.IsCancellationRequested && !_windowClosing && !_appearanceStopping && !ShutdownStarted &&
                generation == _snapshotGeneration && _snapshotRequestKey == key && CurrentSnapshotKey == key &&
                (focusCycle is null || _snapshotExternalFocus && focusCycle == _snapshotFocusCycle && CanRefreshFocusLossSnapshot);
            if (!IsCurrent()) return;
            var candidate = DesktopBackdropSnapshot.ToBitmap(blurred);
            if (!IsCurrent()) return;
            // No committed key/source changes until the independent candidate is
            // complete. Failed, canceled and stale requests leave the old plane.
            CancelBackgroundCommit();
            _snapshotImage.Source = candidate;
            _snapshotKey = key;
            TraceInput($"BackdropSnapshot accepted focusCycle={focusCycle} generation={generation}");
            RefreshAppearance();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or ArgumentException)
        { AppLog.Warn("BackdropSnapshot", ex.GetType().Name); }
    }

    private void OnSystemColorsChanged(UISettings sender, object args) => QueueAppearanceRefresh();
    private void OnAdvancedEffectsChanged(UISettings sender, object args) => QueueAppearanceRefresh();
    private void OnHighContrastChanged(AccessibilitySettings sender, object args) => QueueAppearanceRefresh();

    private void TrackAppearanceWork(Task task)
    {
        // All admission and completion acceptance happen on this window's UI thread.
        // Retain superseded requests too: cancellation does not finish their continuations.
        _appearanceWork.RemoveWhere(completed => completed.IsCompletedSuccessfully || completed.IsCanceled);
        _appearanceWork.Add(task);
    }

    private async Task ShutdownAppearanceAsync()
    {
        _appearanceStopping = true;
        DesktopBackdropSnapshot.TryRestoreWindowAffinity(WinRT.Interop.WindowNative.GetWindowHandle(this),
            ref _snapshotAffinityToRestore);
        CancelBackgroundCommit();
        _backgroundRequest?.Cancel();
        _snapshotRequest?.Cancel();
        try { await Task.WhenAll(_appearanceWork); }
        catch (Exception ex) { AppLog.Warn("WallBackdrop", $"Appearance shutdown: {ex.Message}"); }
        finally { _appearanceWork.Clear(); }
    }

    private void QueueAppearanceRefresh()
    {
        if (_windowClosing || _appearanceStopping || _appearanceQueued) return;
        _appearanceQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _appearanceQueued = false;
            if (_windowClosing || _appearanceStopping) return;
            if (_persistencePending)
            {
                _appearanceAfterPersistence = true;
                return;
            }
            RefreshAppearance();
            if (_hasDocument && !_viewBlocked)
            {
                CancelTransientState();
                Surface.Render(Document);
                Surface.RefreshClocks(_clock.UtcNow);
            }
        });
    }

    private void DisposeAppearance()
    {
        _appearanceStopping = true;
        CancelBackgroundCommit();
        _appearanceSettings.ColorValuesChanged -= OnSystemColorsChanged;
        _appearanceSettings.AdvancedEffectsEnabledChanged -= OnAdvancedEffectsChanged;
        if (_highContrastSubscribed)
        {
            try { _accessibilitySettings.HighContrastChanged -= OnHighContrastChanged; }
            catch (System.Runtime.InteropServices.COMException ex)
            { AppLog.Warn("WallBackdrop", $"Optional high-contrast notification cleanup failed: 0x{ex.HResult:X8}"); }
            _highContrastSubscribed = false;
        }
        _backgroundRequest?.Cancel();
        _backgroundRequest?.Dispose();
        _backgroundRequest = null;
        ReleaseBackdropSnapshot();
        SystemBackdrop = null;
    }
}
