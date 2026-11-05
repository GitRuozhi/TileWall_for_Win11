using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TileWall.Views;

/// <summary>System materials with an independent alpha mask, never opacity on the wall's content.</summary>
internal sealed class WallBackdrop(DispatcherQueue queue, Action<bool, bool> fallbackChanged, bool isTest = false) : SystemBackdrop
{
    private readonly bool _diagnosticTrace = Environment.GetEnvironmentVariable("TILEWALL_TRACE_INPUT") == "1" ||
        isTest && Environment.GetCommandLineArgs().Contains("--trace-input", StringComparer.OrdinalIgnoreCase);
    private readonly UISettings _uiSettings = new();
    private readonly Dictionary<WallBackgroundMode, MaterialSlot> _materials = [];
    private ICompositionSupportsSystemBackdrop? _target;
    private SystemBackdropConfiguration? _defaultConfiguration;
    private MaterialSlot? _selectedMaterial;
    private WallBackgroundMode _mode = WallBackgroundMode.Acrylic;
    private int _transparency = 10;
    private bool _active = true, _dark, _acrylicFailed, _updating, _disconnecting;
    private bool _snapshotAvailable;
    private bool _snapshotResidenceSuspended, _snapshotAnimationShielded, _residentUnderSnapshot;
    private AlphaBackdropTarget? _animationDetachSource;
    private long _intentGeneration;
    private bool _focusCommitRequired;
    private Task<bool>? _pendingCommit;
    private (bool Fallback, bool Reduced)? _lastNotifiedState;
    private bool? _lastNotifiedNativeAttached;
    private (bool HighContrast, Color? Background)? _lastMaterialPolicy;

    internal Func<Task<bool>>? WaitForFallbackCommit { get; set; }
    internal Func<bool>? HasOpaqueSnapshotCover { get; set; }
    internal bool NativeMaterialAttached => _selectedMaterial is not null;

    internal void SuspendSnapshotResidence(bool shieldAnimation)
    {
        _snapshotResidenceSuspended = true;
        _snapshotAnimationShielded = shieldAnimation;
        _animationDetachSource = shieldAnimation ? _selectedMaterial?.Proxy : null;
        _intentGeneration++;
        _pendingCommit = null;
        if (BackdropTrace.Enabled) TraceDiagnostic($"Residence suspended shieldAnimation={shieldAnimation}");
    }

    internal async Task<bool> DetachSnapshotForAnimationAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_snapshotAnimationShielded) return true;
        var outgoing = _selectedMaterial?.Proxy ?? _animationDetachSource;
        DisableSelectedMaterial();
        if (_target is not null) _target.SystemBackdrop = null!;
        return outgoing is null || await outgoing.WaitForDetachCommitAsync(token);
    }

    internal void ResumeSnapshotResidence(bool visible)
    {
        _snapshotResidenceSuspended = !visible;
        _snapshotAnimationShielded = false;
        _animationDetachSource = null;
        _intentGeneration++;
        _pendingCommit = null;
        if (!visible && _residentUnderSnapshot) DisableSelectedMaterial();
        if (BackdropTrace.Enabled) TraceDiagnostic($"Residence resumed visible={visible}");
    }

    internal AcrylicSnapshotRecipe? SnapshotRecipe(bool dark) =>
        _materials.TryGetValue(WallBackgroundMode.Acrylic, out var slot) &&
        slot.Configuration.Theme == (dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light) &&
        slot.Controller is DesktopAcrylicController acrylic
            ? new(acrylic.TintColor, acrylic.TintOpacity, acrylic.LuminosityOpacity,
                Math.Round(slot.Proxy.Opacity * 255) / 255d) : null;

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
        if (BackdropTrace.Enabled)
        {
            BackdropTrace.Write($"Material {message} intent={_intentGeneration} active={_active} snapshot={_snapshotAvailable} selected={_selectedMaterial?.Mode}");
            return;
        }
        if (_diagnosticTrace) AppLog.Warn("BackdropDiagnostic", message);
    }

    internal void SetFocusIntent(bool active)
    {
        if (_active == active) return;
        if (BackdropTrace.Enabled) TraceDiagnostic($"FocusIntent active={_active}->{active}");
        // Record intent synchronously with Window.Activated, before the queued
        // appearance refresh. An earlier commit cannot finish the old transition
        // in the gap between those two UI callbacks.
        _intentGeneration++;
        _pendingCommit = null;
        _active = active;
        _acrylicFailed = false;
        _focusCommitRequired = _mode == WallBackgroundMode.Acrylic;
        if (_focusCommitRequired)
        {
            _lastNotifiedState = null;
            if (_materials.TryGetValue(WallBackgroundMode.Acrylic, out var acrylic)) acrylic.Proxy.InvalidateCommit();
        }
    }

    internal void Configure(WallSettings settings, bool active, bool dark, bool snapshotAvailable = false)
    {
        if (_mode != settings.BackgroundMode || _active != active || _dark != dark ||
            _snapshotAvailable != snapshotAvailable ||
            _transparency != Math.Clamp(settings.BackgroundTransparency, 0, 100))
        {
            _intentGeneration++;
            _pendingCommit = null;
        }
        if (_mode != settings.BackgroundMode)
            _focusCommitRequired = false;
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
        _disconnecting = false;
        _lastNotifiedState = null; // A new target must receive its initial state once.
        _lastMaterialPolicy = null;
        queue.EnsureSystemDispatcherQueue();
        _target = connectedTarget;
        TraceDiagnostic("Connected mode=alpha-surface-mask");
        _defaultConfiguration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        _uiSettings.AdvancedEffectsEnabledChanged += OnEffectsChanged;
        UpdateMaterial();
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        _defaultConfiguration = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
        if (BackdropTrace.Enabled) TraceDiagnostic($"DefaultConfiguration inputActive={_defaultConfiguration.IsInputActive} highContrast={_defaultConfiguration.IsHighContrast}");
        UpdateMaterial();
    }

    private void OnEffectsChanged(UISettings sender, object args) => queue.TryEnqueue(() =>
    {
        _intentGeneration++;
        _pendingCommit = null;
        _acrylicFailed = false;
        UpdateMaterial();
    });

    private MaterialSlot GetMaterial(WallBackgroundMode mode)
    {
        if (_materials.TryGetValue(mode, out var cached)) return cached;
        var proxy = new AlphaBackdropTarget(_target!, _diagnosticTrace, OnMaterialReadinessChanged);
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
        _residentUnderSnapshot = false;
        if (_selectedMaterial is null) return;
        TraceDiagnostic($"Material.Disable mode={_selectedMaterial.Mode}");
        var outgoing = _selectedMaterial;
        _selectedMaterial = null;
        // Readiness can notify synchronously. The outgoing slot no longer owns
        // this target, so that callback must not re-enable it during detach.
        outgoing.Proxy.SetEnabled(false);
    }

    private void NotifyFallbackChanged(bool fallback, bool reduced)
    {
        if (_lastNotifiedState is { } previous && previous.Fallback == fallback && previous.Reduced == reduced &&
            _lastNotifiedNativeAttached == NativeMaterialAttached) return;
        if (BackdropTrace.Enabled) TraceDiagnostic($"Fallback notify={fallback} reduced={reduced} nativeAttached={NativeMaterialAttached}");
        fallbackChanged(fallback, reduced);
        _lastNotifiedState = (fallback, reduced);
        _lastNotifiedNativeAttached = NativeMaterialAttached;
    }

    private void OnMaterialReadinessChanged(AlphaBackdropTarget proxy)
    {
        if (!queue.HasThreadAccess)
        {
            queue.TryEnqueue(() => OnMaterialReadinessChanged(proxy));
            return;
        }
        if (_updating || _disconnecting || _target is null || _defaultConfiguration is null ||
            !ReferenceEquals(_selectedMaterial?.Proxy, proxy)) return;
        if (!proxy.IsReady)
            NotifyFallbackChanged(true, !_uiSettings.AdvancedEffectsEnabled || _defaultConfiguration.IsHighContrast);
        UpdateMaterial();
    }

    private bool HasCommitted(Task<bool> commit)
    {
        if (commit.IsCompleted) return commit.IsCompletedSuccessfully && commit.Result;
        if (!ReferenceEquals(_pendingCommit, commit))
        {
            _pendingCommit = commit;
            _ = ResumeAfterCommitAsync(commit, _intentGeneration);
        }
        return false;
    }

    private async Task ResumeAfterCommitAsync(Task<bool> commit, long generation)
    {
        try { await commit; }
        catch (Exception ex)
        {
            AppLog.Warn("BackdropCommit", $"Commit completion failed: {ex.GetType().Name}");
            return;
        }
        if (generation != _intentGeneration || !ReferenceEquals(_pendingCommit, commit))
        {
            if (BackdropTrace.Enabled) TraceDiagnostic($"Commit resume ignored capturedIntent={generation}");
            return;
        }
        if (BackdropTrace.Enabled) TraceDiagnostic($"Commit resume success={commit.IsCompletedSuccessfully && commit.Result}");
        _pendingCommit = null;
        // A canceled/superseded source can complete false without another callback.
        // Re-evaluate once; HasCommitted never queues an already completed failure.
        if (_target is not null) UpdateMaterial();
    }

    private void UpdateMaterial()
    {
        if (_target is null || _defaultConfiguration is null || _updating || _disconnecting) return;
        _updating = true;
        try
        {
            // High contrast can change while reduced stays true (for example when
            // AdvancedEffectsEnabled is already false). Keep that real policy/color
            // change observable even when the two fallback booleans are unchanged.
            var highContrast = _defaultConfiguration.IsHighContrast;
            var contrastBackground = _defaultConfiguration.HighContrastBackgroundColor;
            if (_lastMaterialPolicy is { } previousPolicy && (previousPolicy.HighContrast != highContrast ||
                !previousPolicy.Background.Equals(contrastBackground)))
            {
                _lastNotifiedState = null;
                _intentGeneration++;
                _pendingCommit = null;
            }
            _lastMaterialPolicy = (highContrast, contrastBackground);
            // Window/session handling owns focus intent. The default configuration
            // can observe deactivation earlier; it must not override that intent
            // before the queued window appearance update admits the cached F.
            var effectiveActive = _active;
            var reduced = !_uiSettings.AdvancedEffectsEnabled || _defaultConfiguration.IsHighContrast;
            // A hidden first show also needs the resolved recipe. Creating the
            // disabled proxy does not attach its brush to the window; selection
            // and the existing committed handoff still own that transition.
            if (!reduced && _mode == WallBackgroundMode.Acrylic && DesktopAcrylicController.IsSupported())
                _ = GetMaterial(WallBackgroundMode.Acrylic);
            var useSnapshot = !reduced && _snapshotAvailable && _mode == WallBackgroundMode.Acrylic &&
                (!effectiveActive || _snapshotAnimationShielded);
            var selected = useSnapshot || _snapshotAnimationShielded ? WallBackgroundMode.Custom : _mode == WallBackgroundMode.Acrylic &&
                (!effectiveActive || _acrylicFailed || !DesktopAcrylicController.IsSupported())
                ? WallBackgroundMode.Mica : _mode;
            var supported = selected == WallBackgroundMode.Acrylic ? DesktopAcrylicController.IsSupported()
                : selected == WallBackgroundMode.Mica && MicaController.IsSupported();
            var fallback = _snapshotAnimationShielded || useSnapshot || selected != WallBackgroundMode.Custom;
            // Prepare the replacement before any old target/source is detached.
            // A missing or invalid snapshot uses the existing readable solid plane.
            if (reduced || selected == WallBackgroundMode.Custom || _selectedMaterial?.Mode != selected)
            {
                NotifyFallbackChanged(fallback, reduced);
                if (_selectedMaterial is not null && WaitForFallbackCommit is { } waitForCommit &&
                    !HasCommitted(waitForCommit())) return;
            }
            // A complete opaque F can cover the already connected native graph.
            // Keep its existing alpha and source rather than clearing/re-attaching
            // two independently committed compositors on every focus transition.
            if (!effectiveActive && !reduced && !_snapshotResidenceSuspended && !_acrylicFailed &&
                _mode == WallBackgroundMode.Acrylic && useSnapshot &&
                _selectedMaterial is { Mode: WallBackgroundMode.Acrylic, Available: true } resident &&
                resident.Proxy.IsReady && resident.Controller.State != SystemBackdropState.Fallback &&
                HasOpaqueSnapshotCover?.Invoke() == true)
            {
                _residentUnderSnapshot = true;
                NotifyFallbackChanged(true, false);
                if (BackdropTrace.Enabled) TraceDiagnostic("Residence kept beneath opaque snapshot");
                return;
            }
            var material = !reduced && selected != WallBackgroundMode.Custom && supported ? GetMaterial(selected) : null;
            if (!ReferenceEquals(material, _selectedMaterial)) DisableSelectedMaterial();
            foreach (var slot in _materials.Values)
            {
                // Hold the admitted Acrylic alpha as well as its pixels while
                // inactive. A new setting takes effect on the next activation.
                if (effectiveActive || !_snapshotAvailable || slot.Mode != WallBackgroundMode.Acrylic || reduced)
                    slot.Proxy.Opacity = reduced ? 1f : slot.Mode == WallBackgroundMode.Acrylic
                        ? (float)AcrylicSnapshotRecipe.BackgroundOpacity(_transparency) : (100 - _transparency) / 100f;
                // The disabled Acrylic controller keeps its active recipe cached.
                // Toggling its input state while F is visible would let a stale
                // native fallback brush be the first committed activation source.
                UpdateConfiguration(slot, ReferenceEquals(slot, material) && effectiveActive ||
                    slot.Mode == WallBackgroundMode.Acrylic && useSnapshot);
            }
            if (material is { Available: true })
            {
                _residentUnderSnapshot = false;
                _selectedMaterial = material;
                material.Proxy.SetEnabled(true);
                if ((_diagnosticTrace || BackdropTrace.Enabled) && material.Controller is DesktopAcrylicController acrylic)
                    TraceDiagnostic($"Recipe theme={material.Configuration.Theme} state={acrylic.State} kind={acrylic.Kind} " +
                        $"tint={acrylic.TintColor} tintOpacity={acrylic.TintOpacity:R} luminosity={acrylic.LuminosityOpacity:R} " +
                        $"fallback={acrylic.FallbackColor} active={effectiveActive} snapshot={_snapshotAvailable} transparency={_transparency}");
                // A brush replacement on an already visible native graph needs no
                // new overlay. Only finish a handoff after the new native commit.
                if (material.Proxy.IsReady && _focusCommitRequired && _pendingCommit is null && BackdropTrace.Enabled)
                    TraceDiagnostic("Focus fresh native fence requested");
                if (material.Proxy.IsReady && (!_focusCommitRequired && _lastNotifiedState is { Fallback: false, Reduced: false } ||
                    HasCommitted(material.Proxy.WaitForCommitAsync())))
                {
                    if (_focusCommitRequired && BackdropTrace.Enabled)
                        TraceDiagnostic("Focus fresh native fence accepted");
                    _focusCommitRequired = false;
                    NotifyFallbackChanged(false, false);
                }
                else NotifyFallbackChanged(true, false);
            }
            else
            {
                NotifyFallbackChanged(fallback, reduced);
                DisableSelectedMaterial();
                if (_target.SystemBackdrop is not null) _target.SystemBackdrop = null!;
                // Recalculate the final solid plane after native detach. While a
                // native graph existed it had to be opaque to avoid double alpha.
                NotifyFallbackChanged(fallback, reduced);
                if (selected == WallBackgroundMode.Acrylic)
                {
                    _acrylicFailed = true;
                    queue.TryEnqueue(UpdateMaterial);
                }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            AppLog.Warn("WallBackdrop", $"System material unavailable: {ex.GetType().Name}");
            NotifyFallbackChanged(true, !_uiSettings.AdvancedEffectsEnabled || _defaultConfiguration.IsHighContrast);
            if (WaitForFallbackCommit is { } waitForCommit && !HasCommitted(waitForCommit())) return;
            DisableSelectedMaterial();
            _target.SystemBackdrop = null!;
            NotifyFallbackChanged(true, !_uiSettings.AdvancedEffectsEnabled || _defaultConfiguration.IsHighContrast);
            if (_mode == WallBackgroundMode.Acrylic && !_acrylicFailed)
            {
                _acrylicFailed = true;
                queue.TryEnqueue(UpdateMaterial);
            }
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
        _disconnecting = true;
        _pendingCommit = null;
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
            slot.Proxy.Dispose();
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
internal sealed partial class AlphaBackdropTarget(ICompositionSupportsSystemBackdrop target, bool trace = false,
    Action<AlphaBackdropTarget>? readinessChanged = null) : ICompositionSupportsSystemBackdrop, IDisposable
{
    private Windows.UI.Composition.CompositionBrush? _source;
    private Windows.UI.Composition.CompositionMaskBrush? _mask;
    private Windows.UI.Composition.CompositionColorBrush? _alpha;
    private Windows.UI.Composition.SpriteVisual? _alphaVisual;
    private Windows.UI.Composition.CompositionVisualSurface? _alphaSurface;
    private Windows.UI.Composition.CompositionSurfaceBrush? _alphaMask;
    private float _opacity = 0.85f;
    private bool _enabled, _ready, _disposed;
    private readonly CancellationTokenSource _commitLifetime = new();
    private Task<bool>? _commitWork;
    private long _revision;

    internal bool IsReady => _ready;

    internal void InvalidateCommit()
    {
        // A completed task from a previous focus cycle says nothing about the
        // current Z-order/configuration submission, even when the brush is reused.
        _revision++;
        _commitWork = null;
        if (BackdropTrace.Enabled) TraceDiagnostic("Focus fresh native fence invalidated");
    }

    private void SetReady(bool ready, bool sourceChanged = false)
    {
        if (_ready == ready && !sourceChanged) return;
        _ready = ready;
        _revision++;
        _commitWork = null;
        readinessChanged?.Invoke(this);
    }

    internal Task<bool> WaitForCommitAsync() => _commitWork ??= CommitAsync(_source, _revision, _commitLifetime.Token);

    internal async Task<bool> WaitForDetachCommitAsync(CancellationToken token)
    {
        if (_enabled || _disposed) return false;
        var source = _source;
        var revision = _revision;
        if (source is null) return true;
        try
        {
            if (BackdropTrace.Enabled) TraceDiagnostic("Detach commit requested for snapshot animation");
            await source.Compositor.RequestCommitAsync().AsTask(token);
            token.ThrowIfCancellationRequested();
            return !_enabled && !_disposed && revision == _revision;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn("BackdropCommit", $"Native detach commit unavailable: {ex.GetType().Name}");
            return false;
        }
    }

    private async Task<bool> CommitAsync(Windows.UI.Composition.CompositionBrush? source, long revision, CancellationToken token)
    {
        if (!_ready || source is null) return false;
        try
        {
            TraceDiagnostic("Native commit requested");
            await source.Compositor.RequestCommitAsync().AsTask(token);
            token.ThrowIfCancellationRequested();
            var current = !_disposed && _enabled && _ready && revision == _revision && ReferenceEquals(_source, source);
            if (BackdropTrace.Enabled) TraceDiagnostic($"Native commit returned current={current} capturedRevision={revision}");
            if (current) TraceDiagnostic("Native committed");
            return current;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            AppLog.Warn("BackdropCommit", $"Native commit unavailable: {ex.GetType().Name}");
            return false;
        }
    }

    private void TraceDiagnostic(string message)
    {
        if (BackdropTrace.Enabled)
        {
            BackdropTrace.Write($"Native {message} enabled={_enabled} ready={_ready} revision={_revision} sourceId={(_source is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_source))} alpha={_opacity:F3}");
            return;
        }
        if (trace) AppLog.Warn("BackdropDiagnostic", message);
    }

    internal void SetEnabled(bool enabled)
    {
        if (_disposed || _enabled == enabled) return;
        if (BackdropTrace.Enabled) TraceDiagnostic($"Enable begin desired={enabled}");
        _enabled = enabled;
        if (!enabled)
        {
            SetReady(false);
            target.SystemBackdrop = null!;
            if (_mask is not null) _mask.Source = null!;
            TraceDiagnostic("Proxy disabled; target/source detached without Close");
        }
        else if (_source is not null) SystemBackdrop = _source;
        if (BackdropTrace.Enabled) TraceDiagnostic($"Enable end desired={enabled}");
    }

    internal float Opacity
    {
        get => _opacity;
        set
        {
            if (_opacity == value) return;
            _opacity = value;
            if (_alpha is not null) _alpha.Color = Color.FromArgb((byte)Math.Round(255 * value), 255, 255, 255);
            _revision++;
            _commitWork = null;
            if (BackdropTrace.Enabled) TraceDiagnostic("Alpha changed");
        }
    }

    public Windows.UI.Composition.CompositionBrush SystemBackdrop
    {
        get => _source!;
        set
        {
            if (_disposed) return;
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
                SetReady(false);
                target.SystemBackdrop = null!;
                // A controller may clear/reassign a brush while changing policy. Keep
                // our graph alive for the connection and stop retaining its old source.
                if (_mask is not null) _mask.Source = null!;
                TraceDiagnostic("Setter clear target after; source detached");
                return;
            }
            // This interface consumes Windows.UI.Composition, unlike XAML visuals.
            // The entire alpha graph belongs to the incoming material's compositor.
            if (_mask is null || !_mask.Compositor.Equals(value.Compositor))
            {
                TraceDiagnostic("Setter graph replacement before");
                ReleaseMask();
                _alpha = value.Compositor.CreateColorBrush(Color.FromArgb((byte)Math.Round(255 * _opacity), 255, 255, 255));
                // The former NineGrid/ColorBrush mask can remain empty on Windows 11
                // even after a successful commit. Give the uniform alpha an explicit
                // 1x1 surface, then stretch it over only the backdrop plane.
                _alphaVisual = value.Compositor.CreateSpriteVisual();
                _alphaVisual.Size = System.Numerics.Vector2.One;
                _alphaVisual.Brush = _alpha;
                _alphaSurface = value.Compositor.CreateVisualSurface();
                _alphaSurface.SourceVisual = _alphaVisual;
                _alphaSurface.SourceSize = System.Numerics.Vector2.One;
                _alphaMask = value.Compositor.CreateSurfaceBrush(_alphaSurface);
                _alphaMask.Stretch = Windows.UI.Composition.CompositionStretch.Fill;
                _mask = value.Compositor.CreateMaskBrush();
                _mask.Mask = _alphaMask;
                TraceDiagnostic("Setter graph replacement after");
            }
            TraceDiagnostic("Setter mask inputs before");
            _mask.Source = value;
            var materialLog = $"Material source={value.GetType().Name} alpha={_opacity:F2} mask=CompositionSurfaceBrush";
            if (Environment.GetCommandLineArgs().Contains("--trace-input", StringComparer.OrdinalIgnoreCase))
                AppLog.Warn("WallBackdrop", materialLog);
            else AppLog.Info("WallBackdrop", materialLog);
            TraceDiagnostic("Setter alpha target before");
            target.SystemBackdrop = _mask;
            SetReady(true, sourceChanged: true);
            TraceDiagnostic("Setter alpha target after");
        }
    }

    private void ReleaseMask()
    {
        // Detach before Close, and remove borrowed-source edges before releasing only
        // our own graph. The incoming controller brush is never disposed here.
        SetReady(false);
        target.SystemBackdrop = null!;
        if (_mask is not null) { _mask.Source = null!; _mask.Mask = null!; }
        if (_alphaMask is not null) _alphaMask.Surface = null!;
        if (_alphaSurface is not null) _alphaSurface.SourceVisual = null!;
        if (_alphaVisual is not null) _alphaVisual.Brush = null!;
        _mask?.Dispose(); _mask = null;
        _alphaMask?.Dispose(); _alphaMask = null;
        _alphaSurface?.Dispose(); _alphaSurface = null;
        _alphaVisual?.Dispose(); _alphaVisual = null;
        _alpha?.Dispose(); _alpha = null;
    }

    internal void ResetGraph() { _source = null; ReleaseMask(); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _commitLifetime.Cancel();
        ResetGraph();
        _commitLifetime.Dispose();
    }
}


