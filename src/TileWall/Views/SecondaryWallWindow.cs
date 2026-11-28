using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TileWall.ApplicationModel;
using TileWall.Core.Application;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;
using TileWall.Shell.Interop;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace TileWall.Views;

// A projection of the primary wall's committed document. This window does not own a session.
internal sealed class SecondaryWallWindow : Window
{
    private readonly Grid _root = new();
    private readonly StatusOverlay _status = new();
    private readonly Grid _sidebarHost = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly WindowsSidebar _sidebar;
    private readonly MenuStartView _menu;
    private readonly ScrollViewer _scroll = new()
    {
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollMode = ScrollMode.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Left,
        VerticalContentAlignment = VerticalAlignment.Top,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        IsTabStop = true,
        ZoomMode = ZoomMode.Disabled,
    };
    private readonly WallScrollRail _horizontalRail = new();
    private readonly IntPtr _hwnd;
    private readonly NativeBackdropSurface _nativeBackdropSurface;
    private readonly NativeWallBackground _background;
    private readonly WallBackdrop _backdrop;
    private readonly UISettings _colors = new();
    private readonly SemaphoreSlim _snapshotSlot;
    private readonly Action<Task> _trackAppearance;
    private DisplayMonitor _display;
    private RectInt32? _taskbarArea;
    private TaskbarWindowAnchor _taskbarAnchor;
    private bool? _excludeFromFullscreen;
    private WallDocument? _document;
    private DecodedImageFrame? _customImage, _snapshot;
    private DesktopBackdropSnapshot.CapturedFrame? _capture;
    private AcrylicSnapshotRecipe? _snapshotRecipe;
    private CancellationTokenSource? _snapshotRequest;
    private XamlRoot? _xamlRoot;
    private double _renderScale, _captureScale;
    private WallLanguageMode _language = ProductStrings.Language;
    private bool _dark, _highContrast, _reduced, _fallback = true, _materialReduced, _active, _closing;
    private bool _preparedToShow, _sharedCapture;
    private bool _tileMenuOpen, _sidebarMenuOpen, _interactionEnabled = true;

    internal SecondaryWallWindow(DisplayMonitor display, SemaphoreSlim snapshotSlot, Action<Task> trackAppearance, bool isTest,
        WallImageService images, IReadOnlyList<string> expandedMenuGroups)
    {
        _display = display;
        _snapshotSlot = snapshotSlot;
        _trackAppearance = trackAppearance;
        _menu = new MenuStartView(Surface, images, id => MenuEntryInvoked?.Invoke(id), () => CurrentScale, CanInteract,
            new MenuGroupExpansion(expandedMenuGroups));
        _sidebar = new WindowsSidebar(ShowStatus, OnSidebarMenuVisibilityChanged,
            () => _document?.Settings.ShowUnavailableControls != false, CanInteract);
        _sidebarHost.Children.Add(_sidebar);
        _scroll.Content = Surface;
        _root.Children.Add(_scroll);
        _horizontalRail.Connect(_scroll, Surface.Metrics);
        _root.Children.Add(_horizontalRail);
        _root.Children.Add(_menu.Element);
        _root.Children.Add(_sidebarHost);
        _root.Children.Add(_status);
        AutomationProperties.SetAutomationId(_root, "secondary-wall-root");
        AutomationProperties.SetAutomationId(_scroll, "secondary-wall-scroll");
        Content = _root;
        Title = $"TileWall {ProductVersion.Display}";
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var style = Win32Api.GetWindowLongPtrW(_hwnd, -16).ToInt64();
        Win32Api.SetWindowLongPtrW(_hwnd, -16, new IntPtr(style & ~0x00C40000L)); // WS_CAPTION | WS_THICKFRAME
        Win32Api.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0037); // FRAMECHANGED | NOACTIVATE | NOZORDER | NOMOVE | NOSIZE
        _nativeBackdropSurface = new(_hwnd);
        // A first move flush with an adjoining display can retain the old
        // window DPI on Windows 10. Enter the target monitor before positioning the wall.
        var screen = display.OuterBounds;
        AppWindow.MoveAndResize(new RectInt32(screen.X + screen.Width / 4, screen.Y + screen.Height / 4,
            Math.Max(1, screen.Width / 2), Math.Max(1, screen.Height / 2)));
        _background = new NativeWallBackground(_hwnd);
        _backdrop = new WallBackdrop(DispatcherQueue, OnMaterialChanged, isTest, _background)
        {
            HasOpaqueSnapshotCover = () => _snapshot is not null && _background.HasOpaqueSnapshotCover,
            WaitForFallbackCommit = WaitForCoverAsync,
        };
        SystemBackdrop = _backdrop;
        _root.Loaded += (_, _) =>
        {
            if (_xamlRoot is not null) return;
            _xamlRoot = _root.XamlRoot;
            if (_xamlRoot is not null) _xamlRoot.Changed += XamlRootChanged;
            RefreshScale();
            if (_active) FocusContent();
        };
        _root.KeyDown += (_, e) =>
        {
            if (!_interactionEnabled || e.Key != Windows.System.VirtualKey.Escape || MenuOpen) return;
            e.Handled = true;
            HideRequested?.Invoke();
        };
        _scroll.ViewChanged += (_, _) => ViewChanged?.Invoke();
        _scroll.SizeChanged += (_, _) => ViewChanged?.Invoke();
        _menu.Element.ViewChanged += (_, _) => ViewChanged?.Invoke();
        _menu.Element.SizeChanged += (_, _) => ViewChanged?.Invoke();
        _menu.RowsChanged += () => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => { if (IsVisible) ViewChanged?.Invoke(); });
        Surface.ContextMenuVisibilityChanged += open =>
        {
            _tileMenuOpen = open;
            if (!open) ForegroundChanged?.Invoke();
        };
        Activated += (_, args) =>
        {
            _active = args.WindowActivationState != WindowActivationState.Deactivated;
            _backdrop.SetFocusIntent(_active);
            if (AppWindow.IsVisible)
            {
                UpdateZOrder();
                RefreshBackdrop();
            }
            if (!_active) ForegroundChanged?.Invoke();
        };
        AppWindow.Closing += (_, args) =>
        {
            if (_closing) return;
            args.Cancel = true;
            HideRequested?.Invoke();
        };
        Closed += (_, _) => DisposeResources();
    }

    internal WallSurface Surface { get; } = new() { IsSecondaryView = true };
    internal bool IsVisible => !_closing && AppWindow.IsVisible;
    internal nint WindowHandle => _hwnd;
    internal bool MenuOpen => _tileMenuOpen || _sidebarMenuOpen;
    internal event Action<string>? MenuEntryInvoked;
    internal event Action? HideRequested, ForegroundChanged, ViewChanged;

    internal void ShowStatus(string message) => _status.Show(message, transient: true);

    private bool CanInteract() => _interactionEnabled && IsVisible;

    private void OnSidebarMenuVisibilityChanged(bool open)
    {
        _sidebarMenuOpen = open;
        if (!open) ForegroundChanged?.Invoke();
    }

    internal void SetInteractionEnabled(bool enabled)
    {
        if (_interactionEnabled == enabled) return;
        _interactionEnabled = enabled;
        _root.IsHitTestVisible = enabled;
        _scroll.IsEnabled = enabled;
        _menu.Element.IsEnabled = enabled;
        if (!enabled)
        {
            _sidebar.Collapse();
            Surface.ClearTransientInteraction();
        }
    }

    internal void SetTaskbarPlacement(RectInt32? effectiveArea, TaskbarWindowAnchor anchor)
    {
        var geometryChanged = _taskbarArea != effectiveArea;
        _taskbarArea = effectiveArea;
        _taskbarAnchor = anchor;
        if (!IsVisible) return;
        if (geometryChanged) Place();
        else UpdateZOrder();
    }

    internal void UpdateMenuFrame(string memberId, PuzzleFrame? frame) => _menu.UpdateFrame(memberId, frame);

    internal void RefreshProductStrings()
    {
        if (_language == ProductStrings.Language) return;
        _language = ProductStrings.Language;
        _sidebar.RefreshStrings();
        _menu.RefreshStrings();
        _horizontalRail.RefreshStrings();
        if (IsVisible && _document is { } document) Surface.Render(document, rasterizationScale: CurrentScale);
    }

    internal void UpdateDisplay(DisplayMonitor display)
    {
        if (_display.OuterBounds != display.OuterBounds) ClearSnapshot();
        _display = display;
        if (IsVisible) Place();
    }

    internal void Present(WallDocument document)
    {
        if (ReferenceEquals(_document, document)) return;
        _document = document;
        if (IsVisible) Render();
    }

    internal void SetAppearance(bool dark, bool highContrast, bool reduced, DecodedImageFrame? customFrame)
    {
        if (_dark != dark || _highContrast != highContrast || _reduced != reduced) ClearSnapshot(keepCapture: true);
        _dark = dark;
        _highContrast = highContrast;
        _reduced = reduced;
        var theme = dark ? ElementTheme.Dark : ElementTheme.Light;
        if (_root.RequestedTheme != theme) _root.RequestedTheme = theme;
        var previous = _customImage;
        _customImage = customFrame;
        if (IsVisible) RefreshBackdrop();
        if (!ReferenceEquals(previous, customFrame)) _background.ForgetImage(previous);
    }

    internal void PrepareToShow(bool captureBackdrop)
    {
        if (IsVisible || _document is null) return;
        _preparedToShow = true;
        Surface.ImagesActive = true;
        Render();
        if (captureBackdrop && HiddenBackdropRequest() is { } request)
        {
            try
            {
                _captureScale = request.Scale;
                _capture = DesktopBackdropSnapshot.Capture(request.Region, request.Scale);
            }
            catch (Exception ex) when (ex is OutOfMemoryException or COMException or ArgumentException or InvalidOperationException)
            { AppLog.Warn("BackdropSnapshot", $"Secondary capture unavailable: {ex.GetType().Name}"); }
        }
        _backdrop.ResumeSnapshotResidence(visible: true);
        RefreshBackdrop();
    }

    internal DesktopBackdropSnapshot.CaptureRequest? HiddenBackdropRequest() =>
        !_closing && !IsVisible && !_reduced && !_highContrast &&
        _document?.Settings.BackgroundMode == WallBackgroundMode.Acrylic
            ? new(_display.OuterBounds, CurrentScale) : null;

    internal async Task<bool> PrepareSharedBackdropAsync(DesktopBackdropSnapshot.CaptureRequest request,
        DesktopBackdropSnapshot.CapturedFrame frame, CancellationToken token)
    {
        if (token.IsCancellationRequested || !_preparedToShow || HiddenBackdropRequest() != request) return false;
        _active = false;
        _backdrop.SetFocusIntent(active: false);
        if (_backdrop.PrepareHiddenSnapshotRecipe(_dark) is not { } recipe) return false;
        ClearSnapshot();
        _captureScale = request.Scale;
        _capture = frame;
        _sharedCapture = true;
        await StartSnapshotDerivation(frame, recipe, token);

        bool IsCurrent() => !token.IsCancellationRequested && _preparedToShow &&
            HiddenBackdropRequest() == request && ReferenceEquals(_capture, frame) &&
            !_materialReduced && _snapshot is not null && _background.HasOpaqueSnapshotCover;
        // A hidden window can receive its first XAML/composition attachment or
        // bounds update while the cover is committing. Commit the new revision
        // while the same snapshot still covers the same requested display.
        for (var attempt = 0; attempt < 3 && IsCurrent(); attempt++)
        {
            if (await CommitCoverAsync(token) && IsCurrent())
            {
                BackdropTrace.Write($"SharedSummon cover ready id={_display.Id}");
                return true;
            }
        }
        BackdropTrace.Write($"SharedSummon cover unavailable id={_display.Id} requestCurrent={HiddenBackdropRequest() == request} " +
            $"active={_active} materialReduced={_materialReduced} image={_snapshot is not null} opaque={_background.HasOpaqueSnapshotCover}");
        return false;
    }

    internal void ShowPreparedView()
    {
        if (IsVisible || _document is null) return;
        AppWindow.Show(activateWindow: false);
        _preparedToShow = false;
        RefreshScale();
        UpdateZOrder();
        RefreshBackdrop();
    }

    internal void FocusView()
    {
        Activate();
        Win32Api.SetForegroundWindow(_hwnd);
        FocusContent();
    }

    private void FocusContent()
    {
        if (_root.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is null)
            _scroll.Focus(FocusState.Programmatic);
    }

    internal void HideView()
    {
        if (!IsVisible && !_preparedToShow) return;
        _preparedToShow = false;
        if (IsVisible) AppWindow.Hide();
        if (_document is { } document) _menu.Render(document, active: false);
        _sidebar.Collapse();
        Surface.ImagesActive = false;
        Surface.ClearTransientInteraction();
        _status.Clear();
        _tileMenuOpen = false;
        _sidebarMenuOpen = false;
        ClearSnapshot();
        _backdrop.ResumeSnapshotResidence(visible: false);
        _background.SetCover(null);
        _background.ReleaseIdleUploadDevice();
    }

    internal void CloseView()
    {
        HideView();
        DisposeResources();
        Close();
    }

    internal void AddVisibleMembers(HashSet<string> members, WallMetrics metrics)
    {
        if (!IsVisible || Surface.Geometry is not { } geometry) return;
        var left = _scroll.HorizontalOffset - metrics.LaneWidth / 2;
        var right = _scroll.HorizontalOffset + _scroll.ActualWidth + metrics.LaneWidth / 2;
        var top = _scroll.VerticalOffset - metrics.Pitch;
        var bottom = _scroll.VerticalOffset + _scroll.ActualHeight + metrics.Pitch;
        foreach (var member in geometry.Groups.SelectMany(group => group.Members))
            if (member.Bounds.X < right && member.Bounds.X + member.Bounds.Width > left &&
                member.Bounds.Y < bottom && member.Bounds.Y + member.Bounds.Height > top)
                members.Add(member.MemberId);
        _menu.AddVisibleMembers(members);
    }

    private double CurrentScale
    {
        get
        {
            var dpi = GetDpiForWindow(_hwnd);
            return dpi > 0 ? dpi / 96d : 1;
        }
    }

    private void XamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => RefreshScale();

    private void RefreshScale()
    {
        if (_closing || !AppWindow.IsVisible || _renderScale == CurrentScale) return;
        Render();
    }

    private void Render()
    {
        if (_document is null) return;
        // The HWND is already on the target monitor. XamlRoot can still be
        // unattached when its tile visuals are built for the first show.
        _renderScale = CurrentScale;
        Surface.Render(_document, rasterizationScale: _renderScale);
        Place();
        _menu.Render(_document, active: true);
        RefreshBackdrop();
    }

    private void Place()
    {
        if (_document is null || Surface.Geometry is not { } geometry) return;
        var settings = _document.Settings;
        var screen = _display.OuterBounds;
        var bounds = _taskbarArea ?? _display.WorkArea;
        var presentationArea = WallPresentationGeometry.ResolveArea(new(screen.X, screen.Y, screen.Width, screen.Height),
            new WallPresentationBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height), settings.ActivateTaskbar);
        var fixedWidth = WallViewportPolicy.FixedWidth(settings.MenuStart);
        var scale = CurrentScale;
        ApplyBounds(scale);
        var placedScale = CurrentScale;
        if (placedScale != scale) ApplyBounds(placedScale);
        UpdateZOrder();

        void ApplyBounds(double dpiScale)
        {
            // Fit the committed wall and rails to this screen before enabling horizontal scrolling.
            var width = (int)Math.Ceiling((geometry.WallWidth + fixedWidth) * dpiScale);
            var height = (int)Math.Ceiling(geometry.WallHeight * dpiScale);
            var placement = WallPresentationGeometry.Anchor(presentationArea,
                width, height, settings.AnchorCorner, settings.FullScreen);
            var rect = new RectInt32(placement.X, placement.Y, placement.Width, placement.Height);
            if (AppWindow.Position != new PointInt32(rect.X, rect.Y) || AppWindow.Size != new SizeInt32(rect.Width, rect.Height))
                AppWindow.MoveAndResize(rect);
            PlaceChrome(rect.Width / dpiScale, rect.Height / dpiScale, settings, geometry);
            _background.SetScreenExtent(screen.Width, screen.Height);
            _background.SetClientBounds(rect);
        }
    }

    private void PlaceChrome(double width, double height, WallSettings settings, WallGeometry geometry)
    {
        var sidebarWidth = Math.Min(WallViewportPolicy.SidebarWidthDip, Math.Max(0, width - 1));
        var menuWidth = settings.MenuStart
            ? Math.Min(WallViewportPolicy.MenuWidthDip, Math.Max(0, width - sidebarWidth - 1)) : 0;
        var fixedWidth = sidebarWidth + menuWidth;
        var right = settings.AnchorCorner is AnchorCorner.TopRight or AnchorCorner.BottomRight;
        var viewportWidth = Math.Max(1, width - fixedWidth);
        var viewportHeight = height;
        var y = 0d;
        if (settings.FullScreen)
        {
            viewportWidth = Math.Min(viewportWidth, geometry.WallWidth);
            viewportHeight = Math.Max(1, height * .9);
            y = height * .05;
        }
        var x = right ? Math.Max(0, width - viewportWidth - fixedWidth) : fixedWidth;
        var region = new Thickness(x, y, 0, 0);
        _scroll.Width = _status.Width = viewportWidth;
        _scroll.Height = _status.Height = viewportHeight;
        _scroll.Margin = _status.Margin = region;
        _status.HorizontalAlignment = HorizontalAlignment.Left;
        _status.VerticalAlignment = VerticalAlignment.Top;

        var horizontal = settings.ExpandColumns || geometry.WallWidth > viewportWidth + .01;
        _horizontalRail.Place(_scroll, new Rect(x, y, viewportWidth, viewportHeight), horizontal);
        Surface.SetViewportMinimum(settings.FullScreen || horizontal ? viewportWidth : 0, 0);

        _sidebarHost.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _sidebarHost.Height = height;
        _sidebar.MaxWidth = Math.Max(WallViewportPolicy.SidebarWidthDip, width);
        _sidebar.Configure(settings.AnchorCorner, settings.FullScreen);
        _menu.Position(settings.AnchorCorner, menuWidth);
    }

    private void UpdateZOrder()
    {
        if (_closing || _document is not { Settings: var settings } ||
            AppWindow.Presenter is not OverlappedPresenter presenter) return;
        if (_excludeFromFullscreen != settings.ActivateTaskbar)
        {
            TaskbarActivationBackend.PrepareWall(_hwnd, settings.ActivateTaskbar);
            _excludeFromFullscreen = settings.ActivateTaskbar;
        }
        if (presenter.IsAlwaysOnTop != settings.AlwaysOnTop) presenter.IsAlwaysOnTop = settings.AlwaysOnTop;
        if (!settings.ActivateTaskbar || !IsVisible || !settings.AlwaysOnTop) return;
        const int topmostStyle = 8;
        const uint retainGeometry = 0x0013; // SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
        if (TaskbarActivationBackend.IsCurrentPlacementAnchor(_taskbarAnchor, isPrimary: false) &&
            (Win32Api.GetWindowLongPtrW(_taskbarAnchor.Window, -20).ToInt64() & topmostStyle) != 0)
        {
            Win32Api.SetWindowPos(_hwnd, _taskbarAnchor.Window, 0, 0, 0, 0, retainGeometry);
            if ((Win32Api.GetWindowLongPtrW(_hwnd, -20).ToInt64() & topmostStyle) == 0)
                Win32Api.SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, retainGeometry);
        }
    }

    private void RefreshBackdrop()
    {
        if (_closing || _document is null) return;
        var settings = _document.Settings;
        var reduced = _highContrast || _reduced || _materialReduced;
        var acrylic = settings.BackgroundMode == WallBackgroundMode.Acrylic;
        var custom = settings.BackgroundMode == WallBackgroundMode.Custom;
        if (_capture is not null && (_captureScale != CurrentScale || !acrylic || reduced)) ClearSnapshot();
        var useSnapshot = acrylic && !reduced && _snapshot is not null && (!_active || _fallback);
        var color = _highContrast ? _colors.GetColorValue(UIColorType.Background) : WallBackdrop.SolidBackgroundColor(_dark);
        var image = useSnapshot ? _snapshot : custom && !reduced ? _customImage : null;
        var opacity = reduced || useSnapshot || _fallback && _backdrop.NativeMaterialAttached ? 1f
            : (float)WallBackdrop.LinearBackgroundOpacity(settings.BackgroundTransparency);
        _background.SetCover(useSnapshot || custom || _fallback || reduced
            ? new NativeWallBackground.Cover(color, opacity, image, useSnapshot ? _display.OuterBounds : null, custom) : null);
        _backdrop.Configure(settings, _active, _dark, _snapshot is not null);
        // A staged F already has a resolved recipe. Keep it through the hidden
        // preparation and the handoff to live Acrylic, while allowing policy,
        // theme and display changes to invalidate it through ClearSnapshot.
        var keepSharedSnapshot = _sharedCapture && (_preparedToShow ||
            _snapshot is not null && (!_active || _fallback));
        if (acrylic && !reduced && !keepSharedSnapshot && _capture is { } capture &&
            _backdrop.SnapshotRecipe(_dark) is { } recipe && _snapshotRecipe != recipe)
        {
            ClearSnapshot(keepCapture: true);
            _ = StartSnapshotDerivation(capture, recipe);
        }
    }

    private Task StartSnapshotDerivation(DesktopBackdropSnapshot.CapturedFrame capture, AcrylicSnapshotRecipe recipe,
        CancellationToken token = default)
    {
        _snapshotRecipe = recipe;
        _snapshotRequest = token.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(token) : new();
        var derive = DeriveSnapshotAsync(capture, recipe, _snapshotRequest.Token);
        _trackAppearance(derive);
        return derive;
    }

    private void OnMaterialChanged(bool fallback, bool reduced)
    {
        _fallback = fallback;
        _materialReduced = reduced;
        // The material can notify synchronously from Configure. Settle the new
        // cover after the current system backdrop transition has returned.
        DispatcherQueue.TryEnqueue(() => { if (IsVisible) RefreshBackdrop(); });
    }

    private Task<bool> WaitForCoverAsync() => IsVisible ? CommitCoverAsync(CancellationToken.None) : Task.FromResult(true);

    private async Task<bool> CommitCoverAsync(CancellationToken token)
    {
        try { return await _background.WaitForCoverCommitAsync(token); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or OperationCanceledException) { return false; }
    }

    private async Task DeriveSnapshotAsync(DesktopBackdropSnapshot.CapturedFrame capture, AcrylicSnapshotRecipe recipe, CancellationToken token)
    {
        try
        {
            await _snapshotSlot.WaitAsync(token);
            DecodedImageFrame frame;
            try { frame = await Task.Run(() => DesktopBackdropSnapshot.BlurAndTint(capture, recipe, token), token); }
            finally { _snapshotSlot.Release(); }
            if (token.IsCancellationRequested || !(_preparedToShow || IsVisible) || !ReferenceEquals(capture, _capture)) return;
            _snapshot = frame;
            RefreshBackdrop();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is COMException or IOException or ArgumentException or OutOfMemoryException)
        { AppLog.Warn("BackdropSnapshot", $"Secondary snapshot unavailable: {ex.GetType().Name}"); }
    }

    private void ClearSnapshot(bool keepCapture = false)
    {
        _snapshotRequest?.Cancel();
        _snapshotRequest?.Dispose();
        _snapshotRequest = null;
        _snapshotRecipe = null;
        if (!keepCapture)
        {
            _capture = null;
            _sharedCapture = false;
        }
        var previous = _snapshot;
        _snapshot = null;
        _background.ForgetImage(previous);
    }

    private void DisposeResources()
    {
        if (_closing) return;
        _closing = true;
        if (_xamlRoot is not null) _xamlRoot.Changed -= XamlRootChanged;
        _trackAppearance(_menu.ShutdownAsync());
        ClearSnapshot();
        SystemBackdrop = null;
        _backdrop.ReleaseDetachedMaterials();
        _background.Dispose();
        _nativeBackdropSurface.Dispose();
    }

    [DllImport("user32.dll", ExactSpelling = true)] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
