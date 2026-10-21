using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TileWall.Views;

/// <summary>System materials with an independent alpha mask, never opacity on the wall's content.</summary>
internal sealed class WallBackdrop(DispatcherQueue queue, Action<bool, bool> fallbackChanged, bool isTest = false) : SystemBackdrop
{
    private readonly bool _diagnosticTrace = isTest && Environment.GetCommandLineArgs().Contains("--trace-input", StringComparer.OrdinalIgnoreCase);
    private readonly UISettings _uiSettings = new();
    private readonly Dictionary<WallBackgroundMode, MaterialSlot> _materials = [];
    private ICompositionSupportsSystemBackdrop? _target;
    private SystemBackdropConfiguration? _defaultConfiguration;
    private MaterialSlot? _selectedMaterial;
    private WallBackgroundMode _mode = WallBackgroundMode.Acrylic;
    private int _transparency = 10;
    private bool _active = true, _dark, _acrylicFailed, _updating;
    private bool _snapshotAvailable;
    private (bool Fallback, bool Reduced)? _lastNotifiedState;
    private (bool HighContrast, Color? Background)? _lastMaterialPolicy;

    private sealed class MaterialSlot(WallBackgroundMode mode, ISystemBackdropControllerWithTargets controller,
        AlphaBackdropTarget proxy)
    {
        internal WallBackgroundMode Mode { get; } = mode;
        internal ISystemBackdropControllerWithTargets Controller { get; } = controller;
        internal AlphaBackdropTarget Proxy { get; } = proxy;
        internal SystemBackdropConfiguration Configuration { get; } = new();
        internal bool Attached { get; set; }
        internal bool Available { get; set; }
    }

    private void TraceDiagnostic(string message)
    {
        if (_diagnosticTrace) AppLog.Warn("BackdropDiagnostic", message);
    }

    internal void Configure(WallSettings settings, bool active, bool dark, bool snapshotAvailable = false)
    {
        if (_mode != settings.BackgroundMode || _active != active || _dark != dark) _acrylicFailed = false;
        _mode = settings.BackgroundMode;
        _transparency = Math.Clamp(settings.BackgroundTransparency, 0, 100);
        _active = active;
        _dark = dark;
        _snapshotAvailable = snapshotAvailable;
        UpdateMaterial();
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _lastNotifiedState = null; // A new target must receive its initial state once.
        _lastMaterialPolicy = null;
        queue.EnsureSystemDispatcherQueue();
        _target = connectedTarget;
        TraceDiagnostic("Connected mode=alpha-mask");
        _defaultConfiguration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        _uiSettings.AdvancedEffectsEnabledChanged += OnEffectsChanged;
        UpdateMaterial();
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        _defaultConfiguration = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
        UpdateMaterial();
    }

    private void OnEffectsChanged(UISettings sender, object args) => queue.TryEnqueue(() =>
    {
        _acrylicFailed = false;
        UpdateMaterial();
    });

    private MaterialSlot GetMaterial(WallBackgroundMode mode)
    {
        if (_materials.TryGetValue(mode, out var cached)) return cached;
        var proxy = new AlphaBackdropTarget(_target!, _diagnosticTrace);
        ISystemBackdropControllerWithTargets controller = mode == WallBackgroundMode.Acrylic
            ? new DesktopAcrylicController() : new MicaController();
        var slot = new MaterialSlot(mode, controller, proxy);
        _materials.Add(mode, slot);
        UpdateConfiguration(slot, active: true);
        controller.StateChanged += OnControllerStateChanged;
        TraceDiagnostic($"Controller.AddTarget before mode={mode}");
        slot.Attached = controller.AddSystemBackdropTarget(proxy);
        slot.Available = slot.Attached;
        TraceDiagnostic($"Controller.AddTarget after added={slot.Attached}");
        controller.SetSystemBackdropConfiguration(slot.Configuration);
        TraceDiagnostic("Controller.SetConfiguration once");
        return slot;
    }

    private void UpdateConfiguration(MaterialSlot slot, bool active)
    {
        var configuration = slot.Configuration;
        var inputActive = slot.Mode == WallBackgroundMode.Mica || active;
        var theme = _dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        if (configuration.IsInputActive != inputActive) configuration.IsInputActive = inputActive;
        if (configuration.Theme != theme) configuration.Theme = theme;
        if (configuration.IsHighContrast != _defaultConfiguration!.IsHighContrast)
            configuration.IsHighContrast = _defaultConfiguration.IsHighContrast;
        if (configuration.HighContrastBackgroundColor != _defaultConfiguration.HighContrastBackgroundColor)
            configuration.HighContrastBackgroundColor = _defaultConfiguration.HighContrastBackgroundColor;
    }

    private void DisableSelectedMaterial()
    {
        if (_selectedMaterial is null) return;
        TraceDiagnostic($"Material.Disable mode={_selectedMaterial.Mode}");
        _selectedMaterial.Proxy.SetEnabled(false);
        _selectedMaterial = null;
    }

    private void NotifyFallbackChanged(bool fallback, bool reduced)
    {
        if (_lastNotifiedState is { } previous && previous.Fallback == fallback && previous.Reduced == reduced) return;
        fallbackChanged(fallback, reduced);
        _lastNotifiedState = (fallback, reduced);
    }

    private void UpdateMaterial()
    {
        if (_target is null || _defaultConfiguration is null || _updating) return;
        _updating = true;
        try
        {
            // High contrast can change while reduced stays true (for example when
            // AdvancedEffectsEnabled is already false). Keep that real policy/color
            // change observable even when the two fallback booleans are unchanged.
            var highContrast = _defaultConfiguration.IsHighContrast;
            var contrastBackground = _defaultConfiguration.HighContrastBackgroundColor;
            if (_lastMaterialPolicy is { } previousPolicy && (previousPolicy.HighContrast != highContrast ||
                !previousPolicy.Background.Equals(contrastBackground))) _lastNotifiedState = null;
            _lastMaterialPolicy = (highContrast, contrastBackground);
            // The default configuration observes native focus before Window.Activated
            // can enqueue its refresh. Stop borrowing the active brush immediately.
            var effectiveActive = _active && _defaultConfiguration.IsInputActive;
            var reduced = !_uiSettings.AdvancedEffectsEnabled || _defaultConfiguration.IsHighContrast;
            var useSnapshot = !reduced && _snapshotAvailable && _mode == WallBackgroundMode.Acrylic && !effectiveActive;
            var selected = useSnapshot ? WallBackgroundMode.Custom : _mode == WallBackgroundMode.Acrylic && (!effectiveActive || _acrylicFailed || !DesktopAcrylicController.IsSupported())
                ? WallBackgroundMode.Mica : _mode;
            var supported = selected == WallBackgroundMode.Acrylic ? DesktopAcrylicController.IsSupported()
                : selected == WallBackgroundMode.Mica && MicaController.IsSupported();
            var material = !reduced && selected != WallBackgroundMode.Custom && supported ? GetMaterial(selected) : null;
            if (!ReferenceEquals(material, _selectedMaterial)) DisableSelectedMaterial();
            foreach (var slot in _materials.Values)
            {
                // Offset only the material that really is Acrylic. Mica and the XAML
                // fallback plane retain the setting's original transparency.
                var transparency = slot.Mode == WallBackgroundMode.Acrylic ? Math.Clamp(_transparency - 6, 0, 94) : _transparency;
                slot.Proxy.Opacity = reduced ? 1f : (100 - transparency) / 100f;
                UpdateConfiguration(slot, ReferenceEquals(slot, material) && effectiveActive);
            }
            if (material is { Available: true })
            {
                _selectedMaterial = material;
                material.Proxy.SetEnabled(true);
                NotifyFallbackChanged(false, false);
            }
            else
            {
                DisableSelectedMaterial();
                if (_target.SystemBackdrop is not null) _target.SystemBackdrop = null!;
                if (selected == WallBackgroundMode.Acrylic)
                {
                    _acrylicFailed = true;
                    queue.TryEnqueue(UpdateMaterial);
                }
                NotifyFallbackChanged(useSnapshot || selected != WallBackgroundMode.Custom, reduced);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            AppLog.Warn("WallBackdrop", $"System material unavailable: {ex.GetType().Name}");
            DisableSelectedMaterial();
            _target.SystemBackdrop = null!;
            if (_mode == WallBackgroundMode.Acrylic && !_acrylicFailed)
            {
                _acrylicFailed = true;
                queue.TryEnqueue(UpdateMaterial);
            }
            NotifyFallbackChanged(true, false);
        }
        finally { _updating = false; }
    }

    private void OnControllerStateChanged(ISystemBackdropControllerWithTargets sender, object args)
    {
        if (sender.State != SystemBackdropState.Fallback) return;
        queue.TryEnqueue(() =>
        {
            if (_selectedMaterial is not { Mode: WallBackgroundMode.Acrylic } current || !ReferenceEquals(sender, current.Controller)) return;
            if (sender.State != SystemBackdropState.Fallback) return;
            _acrylicFailed = true;
            UpdateMaterial();
        });
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        DisableSelectedMaterial();
        _uiSettings.AdvancedEffectsEnabledChanged -= OnEffectsChanged;
        // Match the official backdrop lifecycle: Close controllers only at disconnect,
        // never on focus loss, a custom-image selection, or a theme change.
        foreach (var slot in _materials.Values)
        {
            slot.Controller.StateChanged -= OnControllerStateChanged;
            // Our mask graph uses the controller brush's compositor. Detach the
            // borrowed brush and release our graph before removing its last target,
            // which can close that compositor and every brush created from it.
            slot.Proxy.ResetGraph();
            slot.Controller.RemoveAllSystemBackdropTargets();
            (slot.Controller as IDisposable)?.Dispose();
        }
        _materials.Clear();
        _target = null;
        _defaultConfiguration = null;
        disconnectedTarget.SystemBackdrop = null!;
        base.OnTargetDisconnected(disconnectedTarget);
    }
}
/// <summary>Intercept every controller replacement, including asynchronous policy/theme changes.</summary>
[WinRT.GeneratedWinRTExposedType]
internal sealed partial class AlphaBackdropTarget(ICompositionSupportsSystemBackdrop target, bool trace = false) : ICompositionSupportsSystemBackdrop, IDisposable
{
    private Windows.UI.Composition.CompositionBrush? _source;
    private Windows.UI.Composition.CompositionMaskBrush? _mask;
    private Windows.UI.Composition.CompositionColorBrush? _alpha;
    private Windows.UI.Composition.CompositionNineGridBrush? _alphaMask;
    private float _opacity = 0.85f;
    private bool _enabled;

    private void TraceDiagnostic(string message)
    {
        if (trace) AppLog.Warn("BackdropDiagnostic", message);
    }

    internal void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (!enabled)
        {
            target.SystemBackdrop = null!;
            if (_mask is not null) _mask.Source = null!;
            TraceDiagnostic("Proxy disabled; target/source detached without Close");
        }
        else if (_source is not null) SystemBackdrop = _source;
    }

    internal float Opacity
    {
        get => _opacity;
        set
        {
            if (_opacity == value) return;
            _opacity = value;
            if (_alpha is not null) _alpha.Color = Color.FromArgb((byte)Math.Round(255 * value), 255, 255, 255);
        }
    }

    public Windows.UI.Composition.CompositionBrush SystemBackdrop
    {
        get => _source!;
        set
        {
            TraceDiagnostic($"Setter begin value={(value is null ? "null" : value.GetType().Name)} oldMask={_mask is not null}");
            _source = value;
            if (!_enabled)
            {
                if (_mask is not null) _mask.Source = null!;
                TraceDiagnostic("Setter cached for disabled proxy");
                return;
            }
            if (value is null)
            {
                TraceDiagnostic("Setter clear target before");
                target.SystemBackdrop = null!;
                // A controller may clear/reassign a brush while changing policy. Keep
                // our graph alive for the connection and stop retaining its old source.
                if (_mask is not null) _mask.Source = null!;
                TraceDiagnostic("Setter clear target after; source detached");
                return;
            }
            // This interface consumes Windows.UI.Composition, unlike XAML visuals.
            // Both mask brushes come from the incoming material's own compositor.
            if (_mask is null || !_mask.Compositor.Equals(value.Compositor))
            {
                TraceDiagnostic("Setter graph replacement before");
                ReleaseMask();
                _alpha = value.Compositor.CreateColorBrush(Color.FromArgb((byte)Math.Round(255 * _opacity), 255, 255, 255));
                // The narrower OS mask contract permits SurfaceBrush/NineGridBrush.
                _alphaMask = value.Compositor.CreateNineGridBrush();
                _alphaMask.Source = _alpha;
                _alphaMask.IsCenterHollow = false;
                _mask = value.Compositor.CreateMaskBrush();
                _mask.Mask = _alphaMask;
                TraceDiagnostic("Setter graph replacement after");
            }
            TraceDiagnostic("Setter mask inputs before");
            _mask.Source = value;
            var materialLog = $"Material source={value.GetType().Name} alpha={_opacity:F2} mask=CompositionNineGridBrush";
            if (Environment.GetCommandLineArgs().Contains("--trace-input", StringComparer.OrdinalIgnoreCase))
                AppLog.Warn("WallBackdrop", materialLog);
            else AppLog.Info("WallBackdrop", materialLog);
            TraceDiagnostic("Setter alpha target before");
            target.SystemBackdrop = _mask;
            TraceDiagnostic("Setter alpha target after");
        }
    }

    private void ReleaseMask()
    {
        // Detach before Close, and remove borrowed-source edges before releasing only
        // our own graph. The incoming controller brush is never disposed here.
        target.SystemBackdrop = null!;
        if (_mask is not null) { _mask.Source = null!; _mask.Mask = null!; }
        if (_alphaMask is not null) _alphaMask.Source = null!;
        _mask?.Dispose(); _mask = null;
        _alphaMask?.Dispose(); _alphaMask = null;
        _alpha?.Dispose(); _alpha = null;
    }

    internal void ResetGraph() { _source = null; ReleaseMask(); }

    public void Dispose() => ResetGraph();
}


