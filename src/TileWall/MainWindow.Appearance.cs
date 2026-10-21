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
    private (int X, int Y, int W, int H, double Scale, bool Dark)? _snapshotKey;
    private CancellationTokenSource? _backgroundRequest;
    private string? _backgroundPath;
    private bool _themeDark, _materialFallback = true, _materialReduced;
    private bool? _materialHighContrast;
    private bool _appearanceQueued, _highContrastSubscribed, _appearanceAfterPersistence;

    private void InitializeAppearance()
    {
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
        if (_windowClosing) return;
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
                _ = LoadCustomBackgroundAsync(path, settings, _backgroundRequest.Token);
            }
        }
        UpdateBackgroundLayer();
    }

    private void OnMaterialFallbackChanged(bool fallback, bool reduced)
    {
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

    private void UpdateBackgroundLayer()
    {
        if (_windowClosing) return;
        var highContrast = _accessibilitySettings.HighContrast;
        var reduced = _materialReduced || highContrast || !_appearanceSettings.AdvancedEffectsEnabled;
        var settings = Settings;
        var snapshotReady = !reduced && settings.BackgroundMode == WallBackgroundMode.Acrylic && HasValidSnapshot &&
            (!_backdropActive || _materialFallback);
        var visibility = snapshotReady || _materialFallback || settings.BackgroundMode == WallBackgroundMode.Custom || reduced
            ? Visibility.Visible : Visibility.Collapsed;
        if (_backgroundLayer.Visibility != visibility) _backgroundLayer.Visibility = visibility;
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
    }

    private async Task LoadCustomBackgroundAsync(string path, WallSettings settings, CancellationToken ct)
    {
        // The existing decoder bounds file/pixel budgets and only opens sources read-only.
        var result = await _images.LoadBackgroundAsync(settings.BackgroundMedia, settings.BackgroundImagePath, 2560, 1440, ct);
        if (ct.IsCancellationRequested || _windowClosing || !string.Equals(_backgroundPath, path, StringComparison.OrdinalIgnoreCase)) return;
        _backgroundImage.Source = result.Source;
        UpdateBackgroundLayer();
        if (result.Source is null) AppLog.Warn("WallBackdrop", $"Custom background unavailable: {result.Status}");
    }

    private (int X, int Y, int W, int H, double Scale, bool Dark) CurrentSnapshotKey =>
        (AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height, Scale, _themeDark);
    private bool HasValidSnapshot => _snapshotImage.Source is not null && _snapshotKey == CurrentSnapshotKey;

    private void InvalidateSnapshotIfGeometryChanged()
    {
        if (_snapshotKey is null || _snapshotKey == CurrentSnapshotKey) return;
        ReleaseBackdropSnapshot();
        _wallBackdrop.Configure(Settings, _backdropActive, _themeDark, false);
        UpdateBackgroundLayer();
    }

    private void ReleaseBackdropSnapshot()
    {
        _snapshotRequest?.Cancel(); _snapshotRequest?.Dispose(); _snapshotRequest = null;
        _snapshotKey = null;
        _snapshotImage.Source = null;
        _snapshotImage.Visibility = Visibility.Collapsed;
    }

    private void CaptureBackdropBeforeShow()
    {
        if (AppWindow.IsVisible || _modal || _windowClosing || Settings.BackgroundMode != WallBackgroundMode.Acrylic ||
            _accessibilitySettings.HighContrast || !_appearanceSettings.AdvancedEffectsEnabled) return;
        _snapshotRequest?.Cancel(); _snapshotRequest?.Dispose();
        _snapshotRequest = new();
        _snapshotImage.Source = null;
        _wallBackdrop.Configure(Settings, _backdropActive, _themeDark, false);
        var key = CurrentSnapshotKey;
        _snapshotKey = key;
        var captured = DesktopBackdropSnapshot.Capture(new Windows.Graphics.RectInt32(key.X, key.Y, key.W, key.H));
        if (captured is not null) _ = PrepareSnapshotAsync(captured, key, _snapshotRequest.Token);
    }

    private async Task PrepareSnapshotAsync(DecodedImageFrame capture,
        (int X, int Y, int W, int H, double Scale, bool Dark) key, CancellationToken token)
    {
        try
        {
            await _snapshotSlot.WaitAsync(token);
            DecodedImageFrame blurred;
            try { blurred = await Task.Run(() => DesktopBackdropSnapshot.BlurAndTint(capture, key.Dark), token); }
            finally { _snapshotSlot.Release(); }
            if (token.IsCancellationRequested || _windowClosing || _snapshotKey != key || CurrentSnapshotKey != key) return;
            _snapshotImage.Source = DesktopBackdropSnapshot.ToBitmap(blurred);
            RefreshAppearance();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or ArgumentException)
        { AppLog.Warn("BackdropSnapshot", ex.GetType().Name); }
    }

    private void OnSystemColorsChanged(UISettings sender, object args) => QueueAppearanceRefresh();
    private void OnAdvancedEffectsChanged(UISettings sender, object args) => QueueAppearanceRefresh();
    private void OnHighContrastChanged(AccessibilitySettings sender, object args) => QueueAppearanceRefresh();

    private void QueueAppearanceRefresh()
    {
        if (_windowClosing || _appearanceQueued) return;
        _appearanceQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _appearanceQueued = false;
            if (_windowClosing) return;
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
