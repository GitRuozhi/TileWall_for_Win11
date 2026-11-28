using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Views;
using TileWall.Graphics;
using TileWall.Shell.Interop;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TileWall;

public sealed partial class MainWindow
{
    private readonly UISettings _appearanceSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private DecodedImageFrame? _backgroundFrame, _snapshotFrame;
    private ImageSource? _dialogSnapshotSource;
    private bool? _dialogSnapshotDark;
    private readonly SemaphoreSlim _snapshotSlot = new(1, 1);
    private CancellationTokenSource? _snapshotRequest;
    private CancellationTokenSource? _dialogSnapshotRequest;
    private DesktopBackdropSnapshot.CapturedFrame? _snapshotCapture;
    private (int X, int Y, int W, int H, double Scale)? _snapshotCaptureGeometry;
    private (int X, int Y, int W, int H, double Scale, bool Dark, AcrylicSnapshotRecipe? Recipe)? _snapshotKey;
    private (int X, int Y, int W, int H, double Scale, bool Dark, AcrylicSnapshotRecipe? Recipe)? _snapshotRequestKey;
    private long _snapshotGeneration;
    private CancellationTokenSource? _backgroundRequest;
    private CancellationTokenSource? _backgroundCommitRequest;
    private Task<bool>? _backgroundCommitWork;
    private long _backgroundCommitRevision = -1;
    private readonly HashSet<Task> _appearanceWork = [];
    private bool _appearanceStopping;
    private bool _preparingSharedBackdrop;
    private bool _backdropTransitionCover;
    private bool _backgroundCoverFailed;
    private string? _backgroundPath;
    private bool _themeDark, _materialFallback = true, _materialReduced;
    private bool? _materialHighContrast;
    private bool _appearanceQueued, _highContrastSubscribed, _appearanceAfterPersistence;

    private void InitializeAppearance()
    {
        DispatcherQueue.EnsureSystemDispatcherQueue();
        ReadPrimaryScreen();
        if (BackdropTrace.Enabled)
            BackdropTrace.Write($"Session frequency={System.Diagnostics.Stopwatch.Frequency} pid={Environment.ProcessId}");
        _wallBackdrop.WaitForFallbackCommit = EnsureBackgroundCommittedAsync;
        _wallBackdrop.HasOpaqueSnapshotCover = () => AppWindow.IsVisible && HasValidSnapshot &&
            _nativeBackground.HasOpaqueSnapshotCover;
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

    internal void SetBackdropVisibility(bool visible)
    {
        if (visible) _preparingSharedBackdrop = false;
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
        if (!CanUseWallSnapshot)
        {
            _backdropTransitionCover = false;
            if (_snapshotFrame is not null || _snapshotRequestKey is not null)
                ReleaseBackdropSnapshot(releaseCapture: false);
        }
        InvalidateSnapshotIfGeometryChanged();
        _wallBackdrop.Configure(settings, _backdropActive, _themeDark, HasValidSnapshot);
        if (_backdropActive || !HasValidSnapshot && _dialogHost is not null)
            PrepareCachedBackdropIfNeeded();
        var path = settings.BackgroundMode == WallBackgroundMode.Custom ? settings.BackgroundMedia?.Sha256 ?? settings.BackgroundImagePath : null;
        if (!string.Equals(_backgroundPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _backgroundPath = path;
            _backgroundRequest?.Cancel();
            _backgroundRequest?.Dispose();
            _backgroundRequest = null;
            var previous = _backgroundFrame;
            _backgroundFrame = null;
            UpdateBackgroundLayer();
            _nativeBackground.ForgetImage(previous);
            if (path is not null)
            {
                _backgroundRequest = new CancellationTokenSource();
                TrackAppearanceWork(LoadCustomBackgroundAsync(path, settings, _backgroundRequest.Token));
            }
        }
        UpdateBackgroundLayer();
        RefreshDialogAppearance();
        RefreshSecondaryAppearance();
    }

    private void RefreshDialogAppearance()
    {
        if (_dialogHost is not { } host) return;
        var contrast = _accessibilitySettings.HighContrast
            ? _appearanceSettings.GetColorValue(UIColorType.Background) : (Color?)null;
        ImageSource? snapshot = null;
        RectInt32? bounds = null;
        if (!_appearanceStopping && contrast is null && _snapshotCapture is { } capture &&
            _snapshotCaptureGeometry is { } captured)
        {
            if (_dialogSnapshotDark != _themeDark)
            {
                CancelDialogSnapshot();
                _dialogSnapshotDark = _themeDark;
                _dialogSnapshotRequest = new();
                TrackAppearanceWork(PrepareDialogSnapshotAsync(capture, _themeDark, _dialogSnapshotRequest.Token));
            }
            snapshot = _dialogSnapshotSource;
            bounds = new(captured.X, captured.Y, captured.W, captured.H);
        }
        host.ApplyAppearance(WallRoot.RequestedTheme, contrast, snapshot, bounds);
    }

    private async Task PrepareDialogSnapshotAsync(DesktopBackdropSnapshot.CapturedFrame capture, bool dark,
        CancellationToken token)
    {
        // Use a fixed, fully frosted recipe for the modal surface in each theme.
        var tint = dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 239, 239, 239);
        var recipe = new AcrylicSnapshotRecipe(tint, .65f, .85f, 1);
        try
        {
            await _snapshotSlot.WaitAsync(token);
            DecodedImageFrame blurred;
            try { blurred = await Task.Run(() => DesktopBackdropSnapshot.BlurAndTint(capture, recipe, token), token); }
            finally { _snapshotSlot.Release(); }
            if (token.IsCancellationRequested || _windowClosing || _appearanceStopping || ShutdownStarted ||
                !ReferenceEquals(capture, _snapshotCapture) || _themeDark != dark) return;
            _dialogSnapshotSource = DesktopBackdropSnapshot.ToBitmap(blurred);
            RefreshDialogAppearance();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is OutOfMemoryException or System.Runtime.InteropServices.COMException or
            IOException or ArgumentException or InvalidOperationException)
        { AppLog.Warn("DialogBackdrop", $"Fixed frost unavailable: {ex.GetType().Name}"); }
    }

    private void CancelDialogSnapshot()
    {
        _dialogSnapshotRequest?.Cancel();
        _dialogSnapshotRequest?.Dispose();
        _dialogSnapshotRequest = null;
        _dialogSnapshotSource = null;
        _dialogSnapshotDark = null;
    }

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
        if (_backgroundCoverFailed) return Task.FromResult(false);
        var revision = _nativeBackground.CoverRevision;
        if (_backgroundCommitWork is not null && _backgroundCommitRevision == revision) return _backgroundCommitWork;
        CancelBackgroundCommit();
        _backgroundCommitRevision = revision;
        _backgroundCommitRequest = new CancellationTokenSource();
        _backgroundCommitWork = CommitBackgroundAsync(_backgroundCommitRequest.Token);
        TrackAppearanceWork(_backgroundCommitWork);
        return _backgroundCommitWork;
    }

    private async Task<bool> CommitBackgroundAsync(CancellationToken token)
    {
        try
        {
            var accepted = await _nativeBackground.WaitForCoverCommitAsync(token);
            if (BackdropTrace.Enabled)
                BackdropTrace.Write($"HWND cover commit accepted={accepted} generation={_snapshotGeneration}");
            return accepted;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            AppLog.Warn("BackdropCommit", $"HWND cover commit unavailable: {ex.GetType().Name}");
            return false;
        }
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
        var transitionCover = _backdropTransitionCover && !reduced && settings.BackgroundMode == WallBackgroundMode.Acrylic;
        var snapshotReady = !reduced && settings.BackgroundMode == WallBackgroundMode.Acrylic && HasValidSnapshot &&
            (transitionCover || !_backdropActive || _materialFallback);
        var visible = transitionCover || snapshotReady || _materialFallback || settings.BackgroundMode == WallBackgroundMode.Custom || reduced;
        var opacity = transitionCover || reduced || snapshotReady || _materialFallback && _wallBackdrop.NativeMaterialAttached
            ? 1f : (float)WallBackdrop.LinearBackgroundOpacity(settings.BackgroundTransparency);
        var color = highContrast ? _appearanceSettings.GetColorValue(UIColorType.Background)
            : WallBackdrop.SolidBackgroundColor(_themeDark);
        var image = snapshotReady ? _snapshotFrame : !reduced && settings.BackgroundMode == WallBackgroundMode.Custom
            ? _backgroundFrame : null;
        RectInt32? bounds = snapshotReady && _snapshotCaptureGeometry is { } screen
            ? new(screen.X, screen.Y, screen.W, screen.H) : null;
        if (image is not null) UpdateNativeBackgroundViewport();
        try
        {
            var generation = _nativeBackground.CoverRevision;
            _nativeBackground.SetCover(visible ? new NativeWallBackground.Cover(color, opacity, image, bounds,
                SuppressMaterial: settings.BackgroundMode == WallBackgroundMode.Custom) : null);
            _backgroundCoverFailed = false;
            if (_nativeBackground.CoverRevision != generation)
                TraceInput($"BackdropLayer HWND visible={visible} snapshot={snapshotReady} image={image is not null} opacity={opacity:F2}");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            _backgroundCoverFailed = true;
            CancelBackgroundCommit();
            AppLog.Warn("WallBackdrop", $"HWND background unavailable: 0x{ex.HResult:X8}");
            return;
        }
        if (visible && (opacity == 1 || _wallBackdrop.NativeMaterialAttached)) _ = EnsureBackgroundCommittedAsync();
    }

    private async Task LoadCustomBackgroundAsync(string path, WallSettings settings, CancellationToken ct)
    {
        var result = await _images.LoadBackgroundFrameAsync(settings.BackgroundMedia, settings.BackgroundImagePath, 2560, 1440, ct);
        if (ct.IsCancellationRequested || _windowClosing || !string.Equals(_backgroundPath, path, StringComparison.OrdinalIgnoreCase)) return;
        var previous = _backgroundFrame;
        _backgroundFrame = result.Frame;
        RefreshAppearance();
        if (!ReferenceEquals(previous, _backgroundFrame)) _nativeBackground.ForgetImage(previous);
        if (result.Frame is null) AppLog.Warn("WallBackdrop", $"Custom background unavailable: {result.Status}");
    }

    private (int X, int Y, int W, int H, double Scale, bool Dark, AcrylicSnapshotRecipe? Recipe) CurrentSnapshotKey =>
        (_screenArea.Left, _screenArea.Top, _screenArea.Right - _screenArea.Left, _screenArea.Bottom - _screenArea.Top, Scale, _themeDark,
            _wallBackdrop.SnapshotRecipe(_themeDark));

    private void UpdateNativeBackgroundViewport()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var origin = new CursorPoint();
        if (!ClientToScreen(hwnd, ref origin) || !Win32Api.GetClientRect(hwnd, out var client)) return;
        try { _nativeBackground.SetClientBounds(new(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top)); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        { AppLog.Warn("WallBackdrop", $"HWND background position unavailable: 0x{ex.HResult:X8}"); }
    }
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

    private bool CanUseWallSnapshot => Settings.BackgroundMode == WallBackgroundMode.Acrylic &&
        !_accessibilitySettings.HighContrast && _appearanceSettings.AdvancedEffectsEnabled;

    private bool HasValidSnapshot => _snapshotFrame is not null && _snapshotKey is { } key && MatchesSnapshotEnvironment(key);

    private void InvalidateSnapshotIfGeometryChanged()
    {
        if (_snapshotFrame is not null || _backgroundFrame is not null) UpdateNativeBackgroundViewport();
        var current = CurrentSnapshotKey;
        var geometryChanged = _snapshotCaptureGeometry is { } geometry &&
            geometry != (current.X, current.Y, current.W, current.H, current.Scale);
        if (!geometryChanged && (_snapshotKey is null || MatchesSnapshotEnvironment(_snapshotKey.Value)) &&
            (_snapshotRequestKey is null || MatchesSnapshotEnvironment(_snapshotRequestKey.Value))) return;
        // Only screen/DPI changes invalidate the full-screen capture. Moving or
        // resizing the wall changes its crop and never starts another capture.
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
            _preparingSharedBackdrop = false;
            _snapshotCapture = null;
            _snapshotCaptureGeometry = null;
            CancelDialogSnapshot();
        }
        if (!AppWindow.IsVisible) _backdropTransitionCover = false;
        _snapshotKey = null;
        // Invalidating a visible snapshot installs the safe replacement before
        // removing its pixels. Hide/close use the same cancellation boundary.
        UpdateBackgroundLayer();
        _nativeBackground.ForgetImage(_snapshotFrame);
        _snapshotFrame = null;
        RefreshDialogAppearance();
    }

    private DesktopBackdropSnapshot.CaptureRequest? HiddenBackdropRequest()
    {
        if (_appearanceStopping || _windowClosing || AppWindow.IsVisible || _modal || _accessibilitySettings.HighContrast) return null;
        var key = CurrentSnapshotKey;
        return new(new RectInt32(key.X, key.Y, key.W, key.H), key.Scale);
    }

    internal bool NeedsSharedDesktopCapture => CanUseWallSnapshot;

    internal DesktopBackdropSnapshot.CaptureRequest? SharedPrimaryBackdropRequest() =>
        NeedsSharedDesktopCapture ? HiddenBackdropRequest() : null;

    internal async Task<bool> PrepareSharedPrimaryBackdropAsync(DesktopBackdropSnapshot.CaptureRequest request,
        DesktopBackdropSnapshot.CapturedFrame captured, CancellationToken token)
    {
        if (token.IsCancellationRequested || SharedPrimaryBackdropRequest() != request) return false;
        ReleaseBackdropSnapshot();
        SetBackdropActive(false);
        if (_wallBackdrop.PrepareHiddenSnapshotRecipe(_themeDark) is null) return false;

        _preparingSharedBackdrop = true;
        var derive = UseHiddenBackdrop(request, captured, token);
        if (derive is null) return false;
        await derive;

        bool IsCurrent() => !token.IsCancellationRequested && _preparingSharedBackdrop &&
            SharedPrimaryBackdropRequest() == request && ReferenceEquals(_snapshotCapture, captured) &&
            HasValidSnapshot && !_backgroundCoverFailed && _nativeBackground.HasOpaqueSnapshotCover;
        return IsCurrent() && await CommitBackgroundAsync(token) && IsCurrent();
    }

    internal void CancelSharedPrimaryBackdrop()
    {
        if (!_preparingSharedBackdrop || AppWindow.IsVisible) return;
        ReleaseBackdropSnapshot();
        _nativeBackground.ReleaseIdleUploadDevice();
    }

    private void CaptureBackdropBeforeShow()
    {
        if (HasVisibleSecondaryWalls) return;
        if (HiddenBackdropRequest() is not { } request) return;
        var region = request.Region;
        var geometry = (region.X, region.Y, region.Width, region.Height, request.Scale);
        if (_snapshotCapture is not null && _snapshotCaptureGeometry == geometry)
        {
            PrepareCachedBackdropIfNeeded();
            return;
        }
        ReleaseBackdropSnapshot();
        TraceInput($"BackdropSnapshot capture source=hidden-before-show x={region.X} y={region.Y} width={region.Width} height={region.Height} scale={request.Scale}");
        DesktopBackdropSnapshot.CapturedFrame? captured;
        try { captured = DesktopBackdropSnapshot.Capture(region, request.Scale); }
        catch (Exception ex) when (ex is OutOfMemoryException or System.Runtime.InteropServices.COMException or
            ArgumentException or InvalidOperationException)
        {
            AppLog.Warn("BackdropSnapshot", $"Hidden capture unavailable: {ex.GetType().Name}");
            return;
        }
        if (captured is not null) UseHiddenBackdrop(request, captured);
    }

    private Task? UseHiddenBackdrop(DesktopBackdropSnapshot.CaptureRequest request,
        DesktopBackdropSnapshot.CapturedFrame captured, CancellationToken token = default)
    {
        if (HiddenBackdropRequest() != request) return null;
        var region = request.Region;
        _snapshotCapture = captured;
        _snapshotCaptureGeometry = (region.X, region.Y, region.Width, region.Height, request.Scale);
        return PrepareCachedBackdropIfNeeded(token);
    }

    private Task? PrepareCachedBackdropIfNeeded(CancellationToken token = default)
    {
        if (_appearanceStopping || _windowClosing || ShutdownStarted || !CanUseWallSnapshot ||
            _snapshotCapture is not { } capture) return null;
        var key = CurrentSnapshotKey;
        if (key.Recipe is null || _snapshotCaptureGeometry != (key.X, key.Y, key.W, key.H, key.Scale) ||
            _snapshotKey == key || _snapshotRequestKey == key) return null;
        CancelSnapshotRequest();
        _snapshotRequest = token.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(token) : new();
        _snapshotRequestKey = key;
        TraceInput($"BackdropSnapshot derive source=hidden-capture generation={_snapshotGeneration} bakedAlpha={key.Recipe.Value.EffectiveOpacity:F3}");
        var derive = PrepareSnapshotAsync(capture, key, _snapshotGeneration, _snapshotRequest.Token);
        TrackAppearanceWork(derive);
        return derive;
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
            var previous = _snapshotFrame;
            CancelBackgroundCommit();
            _snapshotFrame = blurred;
            _snapshotKey = key;
            TraceInput($"BackdropSnapshot accepted source=hidden-capture generation={generation} opacity=1 bakedAlpha={key.Recipe!.Value.EffectiveOpacity:F3}");
            RefreshAppearance();
            _nativeBackground.ForgetImage(previous);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or ArgumentException or OutOfMemoryException)
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
        _dialogSnapshotRequest?.Cancel();
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
            // Keep the live resize preview. Release and cancellation both render
            // the wall again using the refreshed appearance.
            if (_resizing)
            {
                TraceInput("Resize kept its preview during an appearance refresh.");
                return;
            }
            if (_hasDocument && !_viewBlocked)
            {
                CancelTransientState();
                Surface.Render(DropBasis);
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
        _wallBackdrop.ReleaseDetachedMaterials();
        _nativeBackground.Dispose();
    }
}
