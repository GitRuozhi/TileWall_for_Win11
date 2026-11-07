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
    private readonly Image _snapshotImage = new() { Stretch = Stretch.Fill, Opacity = 1, Visibility = Visibility.Collapsed };
    private readonly SemaphoreSlim _snapshotSlot = new(1, 1);
    private CancellationTokenSource? _snapshotRequest;
    private DesktopBackdropSnapshot.CapturedFrame? _snapshotCapture;
    private (int X, int Y, int W, int H, double Scale)? _snapshotCaptureGeometry;
    private (int X, int Y, int W, int H, double Scale, bool Dark, AcrylicSnapshotRecipe? Recipe)? _snapshotKey;
    private (int X, int Y, int W, int H, double Scale, bool Dark, AcrylicSnapshotRecipe? Recipe)? _snapshotRequestKey;
    private long _snapshotGeneration;
    private CancellationTokenSource? _backgroundRequest;
    private CancellationTokenSource? _backgroundCommitRequest;
    private Task<bool>? _backgroundCommitWork;
    private long _backdropTraceCommitSequence;
    private readonly HashSet<Task> _appearanceWork = [];
    private bool _appearanceStopping;
    private bool _backdropAnimationSuspended;
    private bool _backdropTransitionCover;
    private string? _backgroundPath;
    private bool _themeDark, _materialFallback = true, _materialReduced;
    private bool? _materialHighContrast;
    private bool _appearanceQueued, _highContrastSubscribed, _appearanceAfterPersistence;

    private void InitializeAppearance()
    {
        if (BackdropTrace.Enabled)
            BackdropTrace.Write($"Session frequency={System.Diagnostics.Stopwatch.Frequency} pid={Environment.ProcessId}");
        _wallBackdrop.WaitForFallbackCommit = EnsureBackgroundCommittedAsync;
        _wallBackdrop.HasOpaqueSnapshotCover = () => !_backdropAnimationSuspended && HasValidSnapshot &&
            _snapshotImage.Visibility == Visibility.Visible && _backgroundLayer.Visibility == Visibility.Visible &&
            _snapshotImage.Opacity == 1 && _backgroundLayer.Opacity == 1 && WallRoot.Opacity == 1 &&
            (WallRoot.RenderTransform is not TranslateTransform translate || translate.Y == 0);
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

    internal Task<bool> PrepareBackdropForAnimationAsync(CancellationToken token)
    {
        var work = PrepareBackdropForAnimationCoreAsync(token);
        TrackAppearanceWork(work);
        return work;
    }

    private async Task<bool> PrepareBackdropForAnimationCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _backdropAnimationSuspended = true;
        // Preserve the independent hidden-before-show preparation and F fence.
        var shield = HasValidSnapshot && _snapshotImage.Visibility == Visibility.Visible &&
            _backgroundLayer.Visibility == Visibility.Visible;
        _wallBackdrop.SuspendSnapshotResidence(shield);
        if (!shield) return true; // Preserve the existing active-native animation path.
        RefreshAppearance();
        if (!await EnsureBackgroundCommittedAsync().WaitAsync(token)) return false;
        token.ThrowIfCancellationRequested();
        if (!await _wallBackdrop.DetachSnapshotForAnimationAsync(token)) return false;
        // The host's target-clear and the XAML animation belong to this compositor.
        // Submit that clear before changing root alpha/translation; no timed delay.
        await CompositionTarget.GetCompositorForCurrentThread().RequestCommitAsync().AsTask(token);
        token.ThrowIfCancellationRequested();
        if (BackdropTrace.Enabled) BackdropTrace.Write("Animation snapshot shield detached native");
        return true;
    }

    internal void CompleteBackdropAnimation(bool visible)
    {
        _backdropAnimationSuspended = !visible;
        _wallBackdrop.ResumeSnapshotResidence(visible);
        if (visible && !_windowClosing && !_appearanceStopping)
        {
            RefreshAppearance();
        }
    }

    /// <summary>Only committed Settings enter this method, including initial load and environment changes.</summary>
    internal void RefreshAppearance()
    {
        if (_windowClosing || _appearanceStopping) return;
        var settings = Settings;
        var foreground = _appearanceSettings.GetColorValue(UIColorType.Foreground);
        _themeDark = settings.ThemeMode == WallThemeMode.Dark || settings.ThemeMode == WallThemeMode.System &&
            (foreground.R * 299 + foreground.G * 587 + foreground.B * 114) > 128_000;
        var theme = _themeDark ? ElementTheme.Dark : ElementTheme.Light;
        if (WallRoot.RequestedTheme != theme) WallRoot.RequestedTheme = theme;
        var contrast = _accessibilitySettings.HighContrast;
        if (settings.BackgroundMode != WallBackgroundMode.Acrylic || contrast || !_appearanceSettings.AdvancedEffectsEnabled)
            _backdropTransitionCover = false;
        if (_snapshotCapture is not null && (settings.BackgroundMode != WallBackgroundMode.Acrylic || contrast ||
            !_appearanceSettings.AdvancedEffectsEnabled))
            ReleaseBackdropSnapshot();
        InvalidateSnapshotIfGeometryChanged();
        _wallBackdrop.Configure(settings, _backdropActive, _themeDark, HasValidSnapshot);
        if (_backdropActive) PrepareCachedBackdropIfNeeded();
        var path = settings.BackgroundMode == WallBackgroundMode.Custom ? settings.BackgroundMedia?.Sha256 ?? settings.BackgroundImagePath : null;
        if (!string.Equals(_backgroundPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _backgroundPath = path;
            _backgroundRequest?.Cancel();
            _backgroundRequest?.Dispose();
            _backgroundRequest = null;
            if (_backgroundImage.Source is not null) CancelBackgroundCommit();
            _backgroundImage.Source = null;
            if (path is not null)
            {
                _backgroundRequest = new CancellationTokenSource();
                TrackAppearanceWork(LoadCustomBackgroundAsync(path, settings, _backgroundRequest.Token));
            }
        }
        UpdateBackgroundLayer();
        RefreshDialogAppearance(settings);
    }

    private void RefreshDialogAppearance(WallSettings settings) => _dialogHost?.ApplyAppearance(
        WallRoot.RequestedTheme, settings, _backgroundImage.Source,
        _accessibilitySettings.HighContrast ? _appearanceSettings.GetColorValue(UIColorType.Background) : null);

    private void OnMaterialFallbackChanged(bool fallback, bool reduced)
    {
        // A foreground native source may release the cover only after WallBackdrop
        // has admitted the current focus revision's commit.
        if (!fallback && (_backdropActive || !HasValidSnapshot))
            _backdropTransitionCover = false;
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
        var traceCommitId = BackdropTrace.Enabled ? ++_backdropTraceCommitSequence : 0;
        if (BackdropTrace.Enabled)
            BackdropTrace.Write($"XAML commit begin id={traceCommitId} generation={_snapshotGeneration}");
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
            if (BackdropTrace.Enabled)
                BackdropTrace.Write($"XAML rendered id={traceCommitId} snapshot={_snapshotImage.Visibility} layer={_backgroundLayer.Visibility} generation={_snapshotGeneration}");
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
            if (BackdropTrace.Enabled) BackdropTrace.Write($"XAML commit returned id={traceCommitId}");
            return true;
        }
        catch (OperationCanceledException)
        {
            if (BackdropTrace.Enabled) BackdropTrace.Write($"XAML commit canceled id={traceCommitId}");
            return false;
        }
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
        var previousLayerVisibility = _backgroundLayer.Visibility;
        var previousSnapshotVisibility = _snapshotImage.Visibility;
        var previousOpacity = _backgroundLayer.Opacity;
        var highContrast = _accessibilitySettings.HighContrast;
        var reduced = _materialReduced || highContrast || !_appearanceSettings.AdvancedEffectsEnabled;
        var settings = Settings;
        var transitionCover = _backdropTransitionCover && !reduced && settings.BackgroundMode == WallBackgroundMode.Acrylic;
        var snapshotReady = !reduced && settings.BackgroundMode == WallBackgroundMode.Acrylic && HasValidSnapshot &&
            (transitionCover || !_backdropActive && HasValidSnapshot || _materialFallback);
        var visibility = transitionCover || snapshotReady || _materialFallback || settings.BackgroundMode == WallBackgroundMode.Custom || reduced
            ? Visibility.Visible : Visibility.Collapsed;
        // F already contains its captured transparency. It shields the whole native
        // graph during either handoff, with no live desktop contribution underneath.
        // A real solid fallback also shields an outgoing native graph until detach.
        var opacity = transitionCover || reduced || snapshotReady || _materialFallback && _wallBackdrop.NativeMaterialAttached
            ? 1 : WallBackdrop.LinearBackgroundOpacity(settings.BackgroundTransparency);
        var customImageReady = !reduced && settings.BackgroundMode == WallBackgroundMode.Custom &&
            _backgroundImage.Source is not null;
        var imageVisibility = customImageReady ? Visibility.Visible : Visibility.Collapsed;
        var snapshotVisibility = snapshotReady ? Visibility.Visible : Visibility.Collapsed;
        var imagePlane = customImageReady || snapshotReady;
        var color = highContrast ? _appearanceSettings.GetColorValue(UIColorType.Background)
            : WallBackdrop.SolidBackgroundColor(_themeDark);
        var backgroundChanged = imagePlane ? _backgroundLayer.Background is not null
            : _backgroundLayer.Background is not SolidColorBrush existingBrush || !existingBrush.Color.Equals(color);
        var planeChanged = previousLayerVisibility != visibility || previousSnapshotVisibility != snapshotVisibility ||
            previousOpacity != opacity || _backgroundImage.Visibility != imageVisibility || backgroundChanged;
        // Own a fence per actual visual plane, not per appearance/focus callback.
        // Subscribe in this same UI turn, before its frame can be rendered; an
        // unchanged already submitted F must not wait for a new idle-screen frame.
        if (planeChanged) CancelBackgroundCommit();
        if (_backgroundLayer.Opacity != opacity) _backgroundLayer.Opacity = opacity;
        // Draw exactly one background plane. Keeping a tinted plane underneath the
        // image would attenuate the desktop twice when XAML applies layer opacity.
        if (imagePlane)
        {
            if (_backgroundLayer.Background is not null) _backgroundLayer.Background = null;
        }
        else
        {
            if (_backgroundLayer.Background is not SolidColorBrush backgroundBrush || !backgroundBrush.Color.Equals(color))
                _backgroundLayer.Background = new SolidColorBrush(color);
        }
        if (_backgroundImage.Visibility != imageVisibility) _backgroundImage.Visibility = imageVisibility;
        _snapshotImage.Visibility = snapshotVisibility;
        // Publish visibility only after the replacement's image/solid and opacity
        // are ready; WallBackdrop then detaches the outgoing native material.
        if (_backgroundLayer.Visibility != visibility)
        {
            _backgroundLayer.Visibility = visibility;
            TraceInput($"BackdropLayer visible={visibility} snapshot={snapshotReady} custom={customImageReady} opacity={opacity:F2}");
        }
        if (visibility == Visibility.Visible && (opacity == 1 || _wallBackdrop.NativeMaterialAttached))
            _ = EnsureBackgroundCommittedAsync();
        if (BackdropTrace.Enabled && (previousLayerVisibility != _backgroundLayer.Visibility ||
            previousSnapshotVisibility != _snapshotImage.Visibility || previousOpacity != _backgroundLayer.Opacity))
            BackdropTrace.Write($"XAML plane layer={previousLayerVisibility}->{_backgroundLayer.Visibility} snapshot={previousSnapshotVisibility}->{_snapshotImage.Visibility} source={_snapshotImage.Source is not null} layerAlpha={_backgroundLayer.Opacity:F3} imageAlpha={_snapshotImage.Opacity:F3} rootAlpha={WallRoot.Opacity:F3} rootY={(WallRoot.RenderTransform as TranslateTransform)?.Y:F2} native={_wallBackdrop.NativeMaterialAttached} active={_backdropActive} fallback={_materialFallback} generation={_snapshotGeneration}");
    }

    private async Task LoadCustomBackgroundAsync(string path, WallSettings settings, CancellationToken ct)
    {
        // The existing decoder bounds file/pixel budgets and only opens sources read-only.
        var result = await _images.LoadBackgroundAsync(settings.BackgroundMedia, settings.BackgroundImagePath, 2560, 1440, ct);
        if (ct.IsCancellationRequested || _windowClosing || !string.Equals(_backgroundPath, path, StringComparison.OrdinalIgnoreCase)) return;
        if (!ReferenceEquals(_backgroundImage.Source, result.Source)) CancelBackgroundCommit();
        _backgroundImage.Source = result.Source;
        RefreshAppearance();
        if (result.Source is null) AppLog.Warn("WallBackdrop", $"Custom background unavailable: {result.Status}");
    }

    private (int X, int Y, int W, int H, double Scale, bool Dark, AcrylicSnapshotRecipe? Recipe) CurrentSnapshotKey =>
        (AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height, Scale, _themeDark,
            _wallBackdrop.SnapshotRecipe(_themeDark));
    private bool MatchesSnapshotEnvironment(
        (int X, int Y, int W, int H, double Scale, bool Dark, AcrylicSnapshotRecipe? Recipe) key)
    {
        var current = CurrentSnapshotKey;
        return (key.X, key.Y, key.W, key.H, key.Scale, key.Dark) ==
            (current.X, current.Y, current.W, current.H, current.Scale, current.Dark) &&
            // Alpha belongs to this captured frame; a later transparency setting
            // cannot invalidate it. Retain the resolved material/policy guard.
            key.Recipe is { } captured && current.Recipe is { } resolved &&
            captured.Tint.Equals(resolved.Tint) && captured.TintOpacity == resolved.TintOpacity &&
            captured.LuminosityOpacity == resolved.LuminosityOpacity;
    }

    private bool HasValidSnapshot => _snapshotImage.Source is not null && _snapshotKey is { } key && MatchesSnapshotEnvironment(key);

    private void InvalidateSnapshotIfGeometryChanged()
    {
        var current = CurrentSnapshotKey;
        var geometryChanged = _snapshotCaptureGeometry is { } geometry &&
            geometry != (current.X, current.Y, current.W, current.H, current.Scale);
        if (!geometryChanged && (_snapshotKey is null || MatchesSnapshotEnvironment(_snapshotKey.Value)) &&
            (_snapshotRequestKey is null || MatchesSnapshotEnvironment(_snapshotRequestKey.Value))) return;
        // A recipe change can reuse the original screen pixels. A different
        // geometry cannot; the next actual hidden show owns the next capture.
        ReleaseBackdropSnapshot(releaseCapture: geometryChanged);
        _wallBackdrop.Configure(Settings, _backdropActive, _themeDark, false);
        UpdateBackgroundLayer();
    }

    private void ReleaseBackdropSnapshot(bool releaseCapture = true)
    {
        CancelBackgroundCommit();
        CancelSnapshotRequest();
        if (releaseCapture)
        {
            _snapshotCapture = null;
            _snapshotCaptureGeometry = null;
        }
        if (!AppWindow.IsVisible) _backdropTransitionCover = false;
        _snapshotKey = null;
        // Invalidating a visible snapshot installs the safe replacement before
        // removing its pixels. Hide/close use the same cancellation boundary.
        UpdateBackgroundLayer();
        _snapshotImage.Source = null;
        _snapshotImage.Visibility = Visibility.Collapsed;
    }

    private void CaptureBackdropBeforeShow()
    {
        if (_appearanceStopping || _windowClosing || AppWindow.IsVisible || _modal || Settings.BackgroundMode != WallBackgroundMode.Acrylic ||
            _accessibilitySettings.HighContrast || !_appearanceSettings.AdvancedEffectsEnabled) return;
        ReleaseBackdropSnapshot();
        var key = CurrentSnapshotKey;
        if (key.Recipe is null) return;
        TraceInput($"BackdropSnapshot capture source=hidden-before-show x={key.X} y={key.Y} width={key.W} height={key.H} scale={key.Scale}");
        var captured = DesktopBackdropSnapshot.Capture(new Windows.Graphics.RectInt32(key.X, key.Y, key.W, key.H), key.Scale);
        if (captured is null) return;
        _snapshotCapture = captured;
        _snapshotCaptureGeometry = (key.X, key.Y, key.W, key.H, key.Scale);
        PrepareCachedBackdropIfNeeded();
    }

    private void PrepareCachedBackdropIfNeeded()
    {
        if (_appearanceStopping || _windowClosing || ShutdownStarted || _snapshotCapture is not { } capture) return;
        var key = CurrentSnapshotKey;
        if (key.Recipe is null || _snapshotCaptureGeometry != (key.X, key.Y, key.W, key.H, key.Scale) ||
            _snapshotKey == key || _snapshotRequestKey == key) return;
        CancelSnapshotRequest();
        _snapshotRequest = new();
        _snapshotRequestKey = key;
        TraceInput($"BackdropSnapshot derive source=hidden-capture generation={_snapshotGeneration} bakedAlpha={key.Recipe.Value.EffectiveOpacity:F3}");
        TrackAppearanceWork(PrepareSnapshotAsync(capture, key, _snapshotGeneration, _snapshotRequest.Token));
    }

    private void CancelSnapshotRequest()
    {
        _snapshotGeneration++;
        _snapshotRequest?.Cancel(); _snapshotRequest?.Dispose(); _snapshotRequest = null;
        _snapshotRequestKey = null;
    }

    private async Task PrepareSnapshotAsync(DesktopBackdropSnapshot.CapturedFrame capture,
        (int X, int Y, int W, int H, double Scale, bool Dark, AcrylicSnapshotRecipe? Recipe) key,
        long generation, CancellationToken token)
    {
        try
        {
            await _snapshotSlot.WaitAsync(token);
            DecodedImageFrame blurred;
            try { blurred = await Task.Run(() => DesktopBackdropSnapshot.BlurAndTint(capture, key.Recipe!.Value, token), token); }
            finally { _snapshotSlot.Release(); }
            bool IsCurrent() => !token.IsCancellationRequested && !_windowClosing && !_appearanceStopping && !ShutdownStarted &&
                generation == _snapshotGeneration && _snapshotRequestKey == key && ReferenceEquals(capture, _snapshotCapture) &&
                MatchesSnapshotEnvironment(key);
            if (!IsCurrent()) return;
            var candidate = DesktopBackdropSnapshot.ToBitmap(blurred);
            if (!IsCurrent()) return;
            CancelBackgroundCommit();
            _snapshotImage.Source = candidate;
            _snapshotKey = key;
            TraceInput($"BackdropSnapshot accepted source=hidden-capture generation={generation} opacity=1 bakedAlpha={key.Recipe!.Value.EffectiveOpacity:F3}");
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
