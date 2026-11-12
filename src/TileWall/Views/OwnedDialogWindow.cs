using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Shell.Interop;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace TileWall.Views;

/// <summary>A content-sized modal window; the wall remains in its anchored viewport.</summary>
internal sealed class OwnedDialogWindow : Window
{
    private sealed class ResizeOption { }
    private sealed class PrimaryPage(FrameworkElement content)
    {
        internal FrameworkElement Content { get; } = content;
        internal string? Name { get; set; }
    }
    private static readonly ConditionalWeakTable<ContentDialog, ResizeOption> ResizableDialogs = new();
    private static readonly ConditionalWeakTable<ContentDialog, PrimaryPage> PrimaryPages = new();
    internal static void EnableUserResize(ContentDialog dialog) => ResizableDialogs.GetValue(dialog, static _ => new ResizeOption());

    internal static void EnablePageChrome(ContentDialog dialog, FrameworkElement page)
    {
        PrimaryPages.Add(dialog, new PrimaryPage(page));
        if (page is Panel panel) panel.Background ??= Transparent;
    }

    internal static void SetPageName(ContentDialog dialog, string name)
    {
        if (!PrimaryPages.TryGetValue(dialog, out var page)) return;
        page.Name = name;
        if (dialog.XamlRoot?.Content is DialogRoot root) root.Host.UpdatePageName(dialog);
    }

    private static readonly SolidColorBrush Transparent = new(Microsoft.UI.Colors.Transparent);
    private static readonly MemberVisual DefaultTileVisual = new();
    internal static SolidColorBrush? PreviewCardBackground(XamlRoot? root) =>
        root?.Content is DialogRoot dialogRoot ? dialogRoot.Host._previewCardBackground : null;
    private bool _resizable, _userSized;
    private uint? _resizePointer;
    private int _resizeEdges;
    private RectInt32 _resizeOrigin;
    private readonly Window _owner;
    private readonly DialogRoot _root;
    private NativeBackdropSurface? _nativeBackdropSurface;
    private WallBackdrop? _backdrop;
    private readonly Grid _backgroundLayer = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Image _backgroundImage = new() { Stretch = Stretch.UniformToFill };
    private readonly Image _snapshotImage = new() { Stretch = Stretch.Fill, Opacity = 1 };
    private readonly Canvas _snapshotViewport = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly RectangleGeometry _snapshotClip = new();
    private RectInt32? _snapshotBounds;
    private CancellationTokenSource? _backgroundCommitRequest;
    private Task<bool>? _backgroundCommitWork;
    private readonly HashSet<Task> _backgroundWork = [];
    private bool _backgroundClosing;
    private double _appearanceScale;
    private readonly SolidColorBrush _solidBackground = new();
    private readonly SolidColorBrush _previewCardBackground = new();
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ContentDialog? _dialog;
    private FrameworkElement? _title, _contentPresenter, _card;
    private Grid? _pageFrame;
    private Panel? _layoutRoot;
    private Shadow? _stockCardShadow;
    private Brush? _stockFrameBackground, _stockLayoutBackground;
    private Thickness _stockFrameBorder;
    private string? _stockName;
    private bool _pageChrome, _materialActive, _materialFallback = true, _materialReduced;
    private WallBackgroundMode _backgroundMode;
    private int _backgroundTransparency;
    private Color? _highContrastBackground;
    private double _resizeContentInset;
    private bool _releasing, _fitQueued, _fitting, _shown, _opened;
    private uint? _dragPointer;
    private NativePoint _dragStart;
    private PointInt32 _dragOrigin;
    private (int Width, int Height, int Radius) _regionSize;
    private ElementTheme _pendingTheme;
    private ComboBox? _themeWaitCombo;
    private bool _themeQueued;
    private readonly bool _traceInput = Environment.GetEnvironmentVariable("TILEWALL_TRACE_INPUT") == "1";
    internal bool IsMaterialActive => !_releasing && !_backgroundClosing && _pageChrome && _materialActive;

    internal OwnedDialogWindow(Window owner, Action dismiss)
    {
        _owner = owner;
        _root = new DialogRoot(this) { Background = Transparent };
        _backgroundLayer.Children.Add(_backgroundImage);
        _snapshotViewport.Children.Add(_snapshotImage);
        _snapshotViewport.Clip = _snapshotClip;
        _backgroundLayer.Children.Add(_snapshotViewport);
        _root.Children.Add(_backgroundLayer);
        _root.RequestedTheme = (owner.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;
        Title = "TileWall";
        Content = _root;
        _root.Loaded += (_, _) => _loaded.TrySetResult();
        _root.LayoutUpdated += (_, _) => QueueFit();
        // The stock card wraps its title in a ScrollViewer, which may handle pointer
        // events before they bubble here. Observe the complete captured gesture route.
        _root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        _root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved), true);
        _root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => EndDrag(e, "release", consumeFinal: true)), true);
        _root.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, e) => EndDrag(e, "cancel")), true);
        _root.PointerExited += (_, e) =>
        {
            if (_resizable && _resizePointer is null)
                _root.SetResizeCursor(ResizeEdgesAt(e.GetCurrentPoint(_root).Position));
        };
        _root.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, e) =>
        {
            // A child relinquishing capture while the root acquires it is expected.
            if (ReferenceEquals(e.OriginalSource, _root)) EndDrag(e, "capture-lost", releaseCapture: false);
        }), true);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // Cloaking permits the bootstrap XamlRoot layout without displaying an empty HWND.
        var cloak = 1;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(hwnd, 13, ref cloak, sizeof(int)));
        // GWLP_HWNDPARENT establishes an owned top-level window, not a child HWND.
        Win32Api.SetWindowLongPtrW(hwnd, -8, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // Keep chrome removal in RemoveNativeFrame; the SDK title-bar teardown
            // can corrupt the Windows 10 heap while disconnecting its UIA providers.
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = owner.AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: true };
        }
        RemoveNativeFrame(hwnd);
        Activated += (_, args) =>
        {
            var active = args.WindowActivationState != WindowActivationState.Deactivated;
            if (_materialActive == active) return;
            _materialActive = active;
            UpdateMaterial();
        };
        AppWindow.Changed += (_, e) =>
        {
            if (_releasing) return;
            if (e.DidPositionChange || e.DidSizeChange)
            {
                CancelBackgroundCommit();
                UpdateMaterial();
            }
            if (e.DidPositionChange && !_fitting) QueueFit();
        };
        AppWindow.Closing += (_, e) =>
        {
            if (_releasing) return;
            e.Cancel = true;
            dismiss(); // ContentDialog.Closing retains its existing draft/discard policy.
        };
    }

    internal async Task InitializeAsync()
    {
        var work = WorkArea;
        var scale = (_owner.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1;
        // Constructors use XamlRoot.Size as their initial page budget. Keep that budget
        // independent of the final card size, including when a small exit prompt replaces it.
        var width = Math.Min(work.Width, (int)Math.Ceiling(800 * scale));
        var height = Math.Min(work.Height, (int)Math.Ceiling(780 * scale));
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2, width, height));
        // Bootstrap XAML without activating an invisible window or disabling the current
        // foreground owner. Transfer activation only once the fitted card is visible.
        AppWindow.Show(false);
        await _loaded.Task;
        _appearanceScale = Scale;
        _root.XamlRoot!.Changed += (_, _) =>
        {
            if (_releasing) return;
            if (_appearanceScale != Scale)
            {
                _appearanceScale = Scale;
                CancelBackgroundCommit();
                UpdateMaterial();
            }
            _root.InvalidateMeasure(); QueueFit();
        };
    }

    internal void ApplyAppearance(ElementTheme theme, WallSettings settings, ImageSource? image, Color? contrastBackground,
        ImageSource? snapshot = null, RectInt32? snapshotBounds = null)
    {
        if (_releasing) return;
        _backgroundMode = settings.BackgroundMode;
        _backgroundTransparency = settings.BackgroundTransparency;
        _highContrastBackground = contrastBackground;
        var imageChanged = !ReferenceEquals(_backgroundImage.Source, image);
        var snapshotChanged = !ReferenceEquals(_snapshotImage.Source, snapshot);
        if (imageChanged || snapshotChanged || !Nullable.Equals(_snapshotBounds, snapshotBounds)) CancelBackgroundCommit();
        if (imageChanged) _backgroundImage.Source = image;
        if (snapshotChanged) _snapshotImage.Source = snapshot;
        _snapshotBounds = snapshotBounds;
        ApplyTheme(theme);
        UpdateMaterial();
    }

    private void UpdateMaterial()
    {
        if (_releasing) return;
        var dark = _root.RequestedTheme == ElementTheme.Dark;
        var tile = MemberBackgroundRules.GetColor(DefaultTileVisual, dark);
        var cardColor = _highContrastBackground ?? Color.FromArgb(tile.A, tile.R, tile.G, tile.B);
        var cardOpacity = _highContrastBackground is not null ? 1 : tile.Opacity;
        if (_previewCardBackground.Color != cardColor) _previewCardBackground.Color = cardColor;
        if (_previewCardBackground.Opacity != cardOpacity) _previewCardBackground.Opacity = cardOpacity;
        if (_backdrop is null && !_pageChrome) return;
        var mode = _pageChrome ? _backgroundMode : WallBackgroundMode.Custom;
        var active = _materialActive || !_shown;
        if (_backdrop is null)
        {
            _nativeBackdropSurface = new NativeBackdropSurface(WinRT.Interop.WindowNative.GetWindowHandle(this));
            _backdrop = new WallBackdrop(DispatcherQueue, OnMaterialFallbackChanged);
            _backdrop.WaitForFallbackCommit = EnsureBackgroundCommittedAsync;
            _backdrop.HasOpaqueSnapshotCover = () => !_releasing && !_backgroundClosing && HasValidSnapshot &&
                _snapshotViewport.Visibility == Visibility.Visible && _backgroundLayer.Visibility == Visibility.Visible &&
                _backgroundLayer.Opacity == 1 && _snapshotImage.Opacity == 1 && _root.Opacity == 1;
            _backdrop.Configure(mode, _backgroundTransparency, active, dark, HasValidSnapshot);
            SystemBackdrop = _backdrop;
        }
        else _backdrop.Configure(mode, _backgroundTransparency, active, dark, HasValidSnapshot);
        UpdateBackground();
    }

    private void OnMaterialFallbackChanged(bool fallback, bool reduced)
    {
        _materialFallback = fallback;
        _materialReduced = reduced;
        UpdateBackground();
    }

    private void UpdateBackground()
    {
        if (_releasing) return;
        var reduced = _materialReduced || _highContrastBackground is not null;
        var custom = _backgroundMode == WallBackgroundMode.Custom;
        var showImage = _pageChrome && custom && !reduced && _backgroundImage.Source is not null;
        var showSnapshot = _pageChrome && !reduced && _backgroundMode == WallBackgroundMode.Acrylic &&
            HasValidSnapshot && (!_materialActive && _shown || _materialFallback);
        var showSolid = _pageChrome && !showImage && !showSnapshot && (custom || reduced || _materialFallback);
        if (showSolid)
        {
            var color = _highContrastBackground ?? WallBackdrop.SolidBackgroundColor(_root.RequestedTheme == ElementTheme.Dark);
            if (_solidBackground.Color != color) _solidBackground.Color = color;
        }
        var opacity = showSnapshot || reduced || showSolid && _backdrop?.NativeMaterialAttached == true
            ? 1 : WallBackdrop.LinearBackgroundOpacity(_backgroundTransparency);
        var imageVisibility = showImage ? Visibility.Visible : Visibility.Collapsed;
        var snapshotVisibility = showSnapshot ? Visibility.Visible : Visibility.Collapsed;
        var layerVisibility = showImage || showSnapshot || showSolid ? Visibility.Visible : Visibility.Collapsed;
        if (_backgroundLayer.Background != (showSolid ? _solidBackground : null) || _backgroundLayer.Opacity != opacity ||
            _backgroundImage.Visibility != imageVisibility || _snapshotViewport.Visibility != snapshotVisibility ||
            _backgroundLayer.Visibility != layerVisibility) CancelBackgroundCommit();
        if (HasValidSnapshot) UpdateSnapshotViewport();
        _backgroundLayer.Background = showSolid ? _solidBackground : null;
        _backgroundLayer.Opacity = opacity;
        _backgroundImage.Visibility = imageVisibility;
        _snapshotViewport.Visibility = snapshotVisibility;
        _backgroundLayer.Visibility = layerVisibility;
        if (layerVisibility == Visibility.Visible && (opacity == 1 || _backdrop?.NativeMaterialAttached == true))
            _ = EnsureBackgroundCommittedAsync();
    }

    private bool HasValidSnapshot
    {
        get
        {
            if (!_pageChrome || _root.RequestedTheme != _pendingTheme || _snapshotImage.Source is null ||
                _snapshotBounds is not { Width: > 0, Height: > 0 } bounds || Scale <= 0) return false;
            var position = AppWindow.Position; var size = AppWindow.Size;
            return position.X >= bounds.X && position.Y >= bounds.Y && size.Width > 0 && size.Height > 0 &&
                (long)position.X + size.Width <= (long)bounds.X + bounds.Width &&
                (long)position.Y + size.Height <= (long)bounds.Y + bounds.Height;
        }
    }

    private void UpdateSnapshotViewport()
    {
        if (_snapshotBounds is not { } bounds || Scale <= 0) return;
        var position = AppWindow.Position; var size = AppWindow.Size; var scale = Scale;
        // F is shared in physical screen pixels. Each HWND applies only its own
        // physical-to-DIP mapping; moving between scales never stretches it to fit.
        var width = bounds.Width / scale; var height = bounds.Height / scale;
        if (_snapshotImage.Width != width) _snapshotImage.Width = width;
        if (_snapshotImage.Height != height) _snapshotImage.Height = height;
        Canvas.SetLeft(_snapshotImage, (bounds.X - position.X) / scale);
        Canvas.SetTop(_snapshotImage, (bounds.Y - position.Y) / scale);
        var clip = new Rect(0, 0, size.Width / scale, size.Height / scale);
        if (_snapshotClip.Rect != clip) _snapshotClip.Rect = clip;
    }

    private Task<bool> EnsureBackgroundCommittedAsync()
    {
        if (_releasing || _backgroundClosing || !_shown) return Task.FromResult(true);
        if (_backgroundCommitWork is not null) return _backgroundCommitWork;
        _backgroundCommitRequest = new CancellationTokenSource();
        _backgroundCommitWork = CommitBackgroundAsync(_backgroundCommitRequest.Token);
        _backgroundWork.RemoveWhere(task => task.IsCompletedSuccessfully || task.IsCanceled);
        _backgroundWork.Add(_backgroundCommitWork);
        return _backgroundCommitWork;
    }

    private async Task<bool> CommitBackgroundAsync(CancellationToken token)
    {
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? rendering = null;
        EventHandler<RenderedEventArgs>? rendered = null;
        var renderingAttached = false; var renderedAttached = false;
        void Unsubscribe()
        {
            if (renderingAttached) { CompositionTarget.Rendering -= rendering; renderingAttached = false; }
            if (renderedAttached) { CompositionTarget.Rendered -= rendered; renderedAttached = false; }
        }
        rendered = (_, _) => { Unsubscribe(); frame.TrySetResult(); };
        rendering = (_, _) =>
        {
            if (renderedAttached) return;
            CompositionTarget.Rendered += rendered;
            renderedAttached = true;
        };
        try
        {
            CompositionTarget.Rendering += rendering;
            renderingAttached = true;
            await frame.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            await CompositionTarget.GetCompositorForCurrentThread().RequestCommitAsync().AsTask(token);
            token.ThrowIfCancellationRequested();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            AppLog.Warn("DialogBackdrop", $"Commit unavailable: {ex.GetType().Name}");
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

    private void ApplyTheme(ElementTheme theme)
    {
        _pendingTheme = theme;
        if (_releasing || _root.RequestedTheme == theme || _themeQueued) return;
        // A system-theme notification may arrive while a ComboBox popup is open.
        // Change its root only after the popup closes and the selection route unwinds.
        _themeQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ApplyPendingTheme);
    }

    private void ApplyPendingTheme()
    {
        _themeQueued = false;
        if (_releasing) return;
        var open = FindOpenCombo(_root);
        if (open is not null)
        {
            if (ReferenceEquals(_themeWaitCombo, open)) return;
            DetachThemeWait();
            _themeWaitCombo = open;
            open.DropDownClosed += OnThemeDropDownClosed;
            return;
        }
        DetachThemeWait();
        if (_root.RequestedTheme == _pendingTheme) return;
        _root.RequestedTheme = _pendingTheme;
        UpdateMaterial();
    }

    private void OnThemeDropDownClosed(object? sender, object args)
    {
        DetachThemeWait();
        ApplyTheme(_pendingTheme);
    }

    private void DetachThemeWait()
    {
        if (_themeWaitCombo is null) return;
        _themeWaitCombo.DropDownClosed -= OnThemeDropDownClosed;
        _themeWaitCombo = null;
    }

    private static ComboBox? FindOpenCombo(DependencyObject parent)
    {
        if (parent is ComboBox { IsDropDownOpen: true } combo) return combo;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindOpenCombo(VisualTreeHelper.GetChild(parent, i)) is { } open) return open;
        return null;
    }

    internal static Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (dialog.XamlRoot?.Content is not DialogRoot root)
            throw new InvalidOperationException("The dialog requires its owned modal root.");
        return root.Host.ShowDialogAsync(dialog);
    }

    internal static void ReleaseTextFocus(ContentDialog dialog)
    {
        // WinUI's text service can remain active after its page is detached and
        // later query input services during framework shutdown (#11653). Move
        // focus while the text control still belongs to its live XamlRoot.
        if (dialog.XamlRoot is not { } root) return;
        var focused = FocusManager.GetFocusedElement(root);
        var previous = focused?.GetType().Name ?? "none";
        if (focused is TextBox input)
        {
            for (var parent = VisualTreeHelper.GetParent(input); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is not NumberBox number) continue;
                // NumberBox's inner InputBox keeps keyboard edits locally. Its
                // public Text setter runs the control's own parser/validation and
                // synchronously publishes ValueChanged; moving focus alone need
                // not finish that work before the caller's close/save decision.
                var raw = input.Text;
                var oldValue = number.Value;
                number.Text = raw;
                if (Environment.GetEnvironmentVariable("TILEWALL_TRACE_INPUT") == "1")
                    AppLog.Warn("OwnedDialogInput", $"NumberCommit input={raw} old={oldValue} value={number.Value} text={number.Text}");
                break;
            }
        }
        dialog.IsTabStop = true;
        var moved = dialog.Focus(FocusState.Programmatic);
        if (Environment.GetEnvironmentVariable("TILEWALL_TRACE_INPUT") == "1")
            AppLog.Warn("OwnedDialogInput", $"CloseFocus previous={previous} moved={moved} current={FocusManager.GetFocusedElement(root)?.GetType().Name ?? "none"}");
    }

    internal static Size ContentBudget(Window owner)
    {
        var scale = (owner.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1;
        var work = owner is OwnedDialogWindow host ? host.WorkArea :
            DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        if (owner is MainWindow wall) work = wall.ConstrainDialogWorkArea(work);
        return new Size(Math.Max(400, work.Width / scale - 72), Math.Max(240, work.Height / scale - 180));
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        _backgroundClosing = false;
        // Dialogs and their controls have a fixed page geometry; only embedded
        // tile visuals consult the wall's Win10/Win11 preference.
        dialog.CornerRadius = new CornerRadius(0);
        _dialog = dialog; _resizable = ResizableDialogs.TryGetValue(dialog, out _); _userSized = false;
        dialog.HorizontalAlignment = HorizontalAlignment.Left;
        dialog.VerticalAlignment = VerticalAlignment.Top;
        dialog.Opened += OnDialogOpened;
        dialog.Closing += OnDialogClosing;
        var contentChanged = dialog.RegisterPropertyChangedCallback(ContentControl.ContentProperty, (_, _) => UpdatePageChrome());
        _root.Children.Add(dialog);
        try
        {
            UpdatePageChrome();
            // InPlace keeps the stock card and ContentDialog behavior without a popup
            // canvas or dimming background surrounding the independently modal HWND.
            var pending = dialog.ShowAsync(ContentDialogPlacement.InPlace);
            dialog.ApplyTemplate();
            RefreshDialogParts(dialog);
            _root.UpdateLayout();
            FitToContent();
            return await pending;
        }
        finally
        {
            // Cancel superseded fences as well as the current one, and keep this
            // HWND/XamlRoot alive until every accepted continuation has returned.
            _backgroundClosing = true;
            CancelBackgroundCommit();
            try { await Task.WhenAll(_backgroundWork); }
            catch (Exception ex) { AppLog.Warn("DialogBackdrop", $"Close drain: {ex.GetType().Name}"); }
            finally { _backgroundWork.Clear(); }
            ReleaseTextFocus(dialog);
            dialog.Opened -= OnDialogOpened;
            dialog.Closing -= OnDialogClosing;
            dialog.UnregisterPropertyChangedCallback(ContentControl.ContentProperty, contentChanged);
            if (_pageChrome) SetPageChrome(dialog, false);
            _dialog = null; _title = null; _contentPresenter = null; _card = null; _root.Children.Remove(dialog);
            _pageFrame = null; _layoutRoot = null;
            _stockCardShadow = null;
            _stockFrameBackground = _stockLayoutBackground = null;
        }
    }

    private static void OnDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!args.Cancel) ReleaseTextFocus(sender);
    }

    private void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _opened = true;
        _root.UpdateLayout();
        RefreshDialogParts(sender);
        FitToContent();
        if (BackdropTrace.Enabled && _pageFrame is { } frame)
        {
            for (DependencyObject? node = frame; node is FrameworkElement element; node = VisualTreeHelper.GetParent(node))
            {
                var brush = element switch { Panel panel => panel.Background, Border border => border.Background,
                    Control control => control.Background, _ => null };
                BackdropTrace.Write($"DialogLayer {element.GetType().Name}/{element.Name} size={element.ActualWidth}x{element.ActualHeight} " +
                    $"fill={(brush is SolidColorBrush solid ? solid.Color.ToString() : brush?.GetType().Name)} shadow={element.Shadow is not null} z={element.Translation.Z:R}");
            }
            BackdropTrace.Write($"DialogBackground mode={_backgroundMode} image={_backgroundImage.Visibility} " +
                $"plane={_backgroundLayer.Visibility} alpha={_backgroundLayer.Opacity:R} fallback={_materialFallback}");
        }
    }

    private void RefreshDialogParts(ContentDialog sender)
    {
        _title = FindPart(sender, "Title");
        _contentPresenter = FindPart(sender, "Content");
        var card = FindPart(sender, "BackgroundElement");
        if (!ReferenceEquals(card, _card))
        {
            _card = card;
            _stockCardShadow = null;
        }
        var layoutRoot = FindPart(sender, "LayoutRoot") as Panel;
        if (!ReferenceEquals(layoutRoot, _layoutRoot))
        {
            _layoutRoot = layoutRoot;
            _stockLayoutBackground = layoutRoot?.Background;
        }
        var frame = _contentPresenter is not null ? VisualTreeHelper.GetParent(_contentPresenter) as Grid : null;
        if (!ReferenceEquals(frame, _pageFrame))
        {
            _pageFrame = frame;
            _stockFrameBackground = frame?.Background;
            _stockFrameBorder = frame?.BorderThickness ?? default;
        }
        if (_pageChrome) ApplyPageTemplateParts();
    }

    private void UpdatePageChrome()
    {
        if (_dialog is not { } dialog) return;
        var enabled = PrimaryPages.TryGetValue(dialog, out var page) && ReferenceEquals(dialog.Content, page.Content);
        if (_pageChrome != enabled) SetPageChrome(dialog, enabled);
    }

    private void SetPageChrome(ContentDialog dialog, bool enabled)
    {
        _pageChrome = enabled;
        if (enabled)
        {
            _stockName = AutomationProperties.GetName(dialog);
            dialog.Title = null;
            UpdatePageName(dialog);
            dialog.Background = Transparent;
        }
        else
        {
            if (string.IsNullOrEmpty(_stockName)) dialog.ClearValue(AutomationProperties.NameProperty);
            else AutomationProperties.SetName(dialog, _stockName);
            dialog.ClearValue(Control.BackgroundProperty);
        }
        ApplyPageTemplateParts();
        UpdateMaterial();
        QueueFit();
    }

    private void UpdatePageName(ContentDialog dialog)
    {
        if (_pageChrome && ReferenceEquals(_dialog, dialog) && PrimaryPages.TryGetValue(dialog, out var page) &&
            page.Name is { } name)
            AutomationProperties.SetName(dialog, name);
    }

    private void ApplyPageTemplateParts()
    {
        // The template can tint LayoutRoot even in place. It also applies an
        // elevation shadow which shows through the transparent BackgroundElement.
        if (_layoutRoot is { } layout)
        {
            if (_pageChrome) layout.Background = Transparent;
            else
            {
                layout.ClearValue(Panel.BackgroundProperty);
                if (layout.Background is null) layout.Background = _stockLayoutBackground;
            }
        }
        if (_card is { } card)
        {
            if (_pageChrome && card.Shadow is { } shadow)
            {
                _stockCardShadow = shadow;
                card.Shadow = null;
            }
            else if (!_pageChrome && _stockCardShadow is { } stockShadow)
            {
                card.Shadow = stockShadow;
                _stockCardShadow = null;
            }
        }
        if (_pageFrame is not { } frame) return;
        // The WinUI template adds an independent top fill over the card. Remove it
        // on the page so it cannot cover the material, especially in light mode.
        if (_pageChrome)
        {
            frame.Background = Transparent;
            frame.BorderThickness = new Thickness(0);
        }
        else
        {
            frame.ClearValue(Panel.BackgroundProperty);
            if (frame.Background is null) frame.Background = _stockFrameBackground;
            frame.BorderThickness = _stockFrameBorder;
        }
    }

    private static void RemoveNativeFrame(IntPtr hwnd)
    {
        const long frameStyles = 0x00C00000L | 0x00040000L; // WS_CAPTION | WS_THICKFRAME
        var style = Win32Api.GetWindowLongPtrW(hwnd, -16).ToInt64();
        Win32Api.SetWindowLongPtrW(hwnd, -16, new IntPtr(style & ~frameStyles));
        // Keep the owned dialog out of the taskbar and Alt+Tab before its first Show.
        const long appWindowStyle = 0x00040000L, toolWindowStyle = 0x00000080L; // WS_EX_APPWINDOW, WS_EX_TOOLWINDOW
        var extendedStyle = Win32Api.GetWindowLongPtrW(hwnd, -20).ToInt64(); // GWL_EXSTYLE
        Win32Api.SetWindowLongPtrW(hwnd, -20, new IntPtr((extendedStyle & ~appWindowStyle) | toolWindowStyle));
        Win32Api.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0020 | 0x0001 | 0x0002 | 0x0004 | 0x0010);
        // Suppress DWM's independent frame; the app draws its own square dialog border.
        uint border = 0xFFFFFFFE;
        Win32Api.DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(uint));
    }

    private RectInt32 WorkArea => ConstrainToOwnerTaskbar(DisplayArea.GetFromWindowId(
        _shown ? AppWindow.Id : _owner.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea);

    private RectInt32 ConstrainToOwnerTaskbar(RectInt32 work) =>
        _owner is MainWindow wall ? wall.ConstrainDialogWorkArea(work) : work;

    internal void RefreshPlacementBounds() => QueueFit();

    private double Scale => _root.XamlRoot?.RasterizationScale ?? 1;

    private void QueueFit()
    {
        if (_releasing || _dialog is null || _fitQueued || _fitting) return;
        _fitQueued = true;
        DispatcherQueue.TryEnqueue(() => { _fitQueued = false; FitToContent(); });
    }

    private void FitToContent()
    {
        if (_releasing || _dialog is null || _fitting) return;
        if (_dragPointer is not null || _resizePointer is not null) return;
        if (_userSized && _owner is not MainWindow { HasTaskbarPresentationBounds: true })
        { ApplyCardRegion(AppWindow.Size.Width, AppWindow.Size.Height); return; }
        var desired = _userSized ? new Size(AppWindow.Size.Width / Scale, AppWindow.Size.Height / Scale) : _dialog.DesiredSize;
        if (desired.Width <= 0 || desired.Height <= 0) return;
        _fitting = true;
        try
        {
            var work = WorkArea;
            var width = Math.Clamp((int)Math.Ceiling(desired.Width * Scale), 1, work.Width);
            var height = Math.Clamp((int)Math.Ceiling(desired.Height * Scale), 1, work.Height);
            var position = AppWindow.Position;
            var x = _shown ? position.X : work.X + (work.Width - width) / 2;
            var y = _shown ? position.Y : work.Y + (work.Height - height) / 2;
            x = Math.Clamp(x, work.X, work.X + work.Width - width);
            y = Math.Clamp(y, work.Y, work.Y + work.Height - height);
            if (AppWindow.Size.Width != width || AppWindow.Size.Height != height || position.X != x || position.Y != y)
                AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            ApplyCardRegion(width, height);
            if (!_shown && _opened)
            {
                _root.UpdateLayout();
                var cloak = 0;
                Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 13, ref cloak, sizeof(int)));
                _shown = true;
                Activate();
                Win32Api.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
                Win32Api.EnableWindow(WinRT.Interop.WindowNative.GetWindowHandle(_owner), false);
                // Initial Opened handlers ran while cloaked. Reapply their selected XAML
                // focus after native activation so text input reaches the intended control.
                if (FocusManager.GetFocusedElement(_root.XamlRoot) is Control focused)
                    focused.Focus(FocusState.Programmatic);
            }
        }
        finally { _fitting = false; }
    }

    private void ApplyCardRegion(int width, int height)
    {
        // Keep the native region identical to the app card. The fixed page style is
        // square, including when its preview displays Win11 tiles.
        var radius = (int)Math.Ceiling((_dialog?.CornerRadius.TopLeft ?? 0) * Scale);
        if (_regionSize == (width, height, radius)) return;
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (region == IntPtr.Zero) return;
        if (SetWindowRgn(WinRT.Interop.WindowNative.GetWindowHandle(this), region, true) == 0)
            DeleteObject(region);
        else _regionSize = (width, height, radius);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer is not null || _resizePointer is not null)
        {
            if (_root.PointerCaptures is { Count: > 0 }) return;
            _dragPointer = _resizePointer = null;
            _root.SetResizeCursor(0);
        }
        var point = e.GetCurrentPoint(_root);
        if (_resizable && point.Properties.IsLeftButtonPressed)
        {
            var edges = ResizeEdgesAt(point.Position);
            if (edges != 0 && TryGetScreenPoint(point.Position, out var pressedAt))
            {
                var origin = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
                if (!_root.CapturePointer(e.Pointer))
                {
                    TraceDrag("begin-capture-rejected", e.Pointer.PointerId, "resize", edges, pressedAt);
                    return;
                }
                _dragStart = pressedAt;
                _resizePointer = e.Pointer.PointerId; _resizeEdges = edges;
                _root.SetResizeCursor(edges);
                _resizeOrigin = origin;
                // Preserve the stock card's padding/border while giving star columns
                // a finite content width. Auto width collapses the editor's preview.
                _resizeContentInset = _dialog?.Content is FrameworkElement content
                    ? Math.Max(0, origin.Width / Scale - content.ActualWidth) : 0;
                TraceDrag("begin", e.Pointer.PointerId, "resize", edges, pressedAt);
                e.Handled = true; return;
            }
        }
        if (!point.Properties.IsLeftButtonPressed ||
            !(_pageChrome ? IsBlankPageHit(e) : IsTitleBand(point.Position)) ||
            !TryGetScreenPoint(point.Position, out var pressedAtForMove)) return;
        var dragOrigin = AppWindow.Position;
        if (!_root.CapturePointer(e.Pointer))
        {
            TraceDrag("begin-capture-rejected", e.Pointer.PointerId, "move", 0, pressedAtForMove);
            return;
        }
        _dragStart = pressedAtForMove;
        _dragPointer = e.Pointer.PointerId;
        _dragOrigin = dragOrigin;
        TraceDrag("begin", e.Pointer.PointerId, "move", 0, pressedAtForMove);
        e.Handled = true;
    }

    private bool IsTitleBand(Point point)
    {
        if (_title is null) return false;
        var titleTop = _title.TransformToVisual(_root).TransformPoint(new Point());
        var titleBottom = titleTop.Y + _title.ActualHeight + _title.Margin.Bottom;
        if (_contentPresenter is not null)
            titleBottom = Math.Min(titleBottom, _contentPresenter.TransformToVisual(_root).TransformPoint(new Point()).Y);
        return point.Y >= 0 && point.Y < titleBottom;
    }

    private bool IsBlankPageHit(PointerRoutedEventArgs e)
    {
        if (_dialog is not { } dialog || e.OriginalSource is not DependencyObject source) return false;
        var mouse = e.Pointer.PointerDeviceType == PointerDeviceType.Mouse;
        for (DependencyObject? element = source; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, dialog) || ReferenceEquals(element, _root))
            {
                // WinUI can route a press on a disabled control to its container.
                // Check the arranged controls too, bounded by their parent viewports.
                return !HasControlAt(dialog, e.GetCurrentPoint(_root).Position, mouse);
            }
            if (element is Control control && IsDragControl(control, mouse) ||
                element is TextBlock { IsTextSelectionEnabled: true } or ClockMemberView) return false;
            if (element is MemberEditorPreview editor && editor.OwnsPointer(e.GetCurrentPoint(editor).Position)) return false;
        }
        return false;
    }

    private bool HasControlAt(DependencyObject parent, Point point, bool mouse)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            if (VisualTreeHelper.GetChild(parent, index) is not FrameworkElement element ||
                element.Visibility != Visibility.Visible || !element.IsHitTestVisible) continue;
            var local = _root.TransformToVisual(element).TransformPoint(point);
            if (local.X < 0 || local.Y < 0 || local.X >= element.ActualWidth || local.Y >= element.ActualHeight) continue;
            if (element is Control control && IsDragControl(control, mouse) || HasControlAt(element, point, mouse)) return true;
        }
        return false;
    }

    // Scrollbars and their buttons are controls in their own right. A mouse
    // press in the scroller's empty area can move the window; touch can still pan.
    private static bool IsDragControl(Control control, bool mouse) => control.GetType() != typeof(ContentControl) &&
        !(mouse && control is ScrollViewer);

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizePointer == e.Pointer.PointerId || _dragPointer == e.Pointer.PointerId)
        {
            var point = e.GetCurrentPoint(_root);
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed)
            {
                EndDrag(e, "button-up");
                return;
            }
            // Mouse events may have been queued before the last HWND move. Their
            // local coordinates must never be added to the new window origin.
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
            {
                if (GetCursorPos(out var cursor)) ApplyDragPoint(cursor);
            }
            else if (TryGetScreenPoint(point.Position, out var cursor)) ApplyDragPoint(cursor);
            e.Handled = true;
            return;
        }
        if (_resizable)
            _root.SetResizeCursor(ResizeEdgesAt(e.GetCurrentPoint(_root).Position));
    }

    private int ResizeEdgesAt(Point point)
    {
        // HWND geometry changes before XAML finishes its next layout. Never offer
        // a resize cursor outside the current client area (over the disabled wall).
        if (!Win32Api.GetClientRect(WinRT.Interop.WindowNative.GetWindowHandle(this), out var client)) return 0;
        var width = (client.Right - client.Left) / Scale;
        var height = (client.Bottom - client.Top) / Scale;
        if (point.X < 0 || point.Y < 0 || point.X >= width || point.Y >= height) return 0;
        const double edge = 7;
        return (point.X < edge ? 1 : point.X >= width - edge ? 2 : 0) |
            (point.Y < edge ? 4 : point.Y >= height - edge ? 8 : 0);
    }

    private void ApplyDragPoint(NativePoint cursor)
    {
        if (_resizePointer is not null)
        {
            var dx = cursor.X - _dragStart.X; var dy = cursor.Y - _dragStart.Y;
            var workArea = WorkArea; var r = _resizeOrigin;
            var left = r.X + ((_resizeEdges & 1) != 0 ? dx : 0); var top = r.Y + ((_resizeEdges & 4) != 0 ? dy : 0);
            var right = r.X + r.Width + ((_resizeEdges & 2) != 0 ? dx : 0); var bottom = r.Y + r.Height + ((_resizeEdges & 8) != 0 ? dy : 0);
            var contentMinimum = _dialog?.Content is FrameworkElement page ? page.MinWidth + _resizeContentInset : 0;
            var minWidth = (int)Math.Ceiling(Math.Max(480, contentMinimum) * Scale); var minHeight = (int)(320 * Scale);
            if (right - left < minWidth) { if ((_resizeEdges & 1) != 0) left = right - minWidth; else right = left + minWidth; }
            if (bottom - top < minHeight) { if ((_resizeEdges & 4) != 0) top = bottom - minHeight; else bottom = top + minHeight; }
            left = Math.Max(workArea.X, left); top = Math.Max(workArea.Y, top);
            right = Math.Min(workArea.X + workArea.Width, right); bottom = Math.Min(workArea.Y + workArea.Height, bottom);
            _userSized = true; _fitting = true;
            try
            {
                if (_dialog is { } dialog)
                {
                    dialog.Width = (right - left) / Scale; dialog.Height = (bottom - top) / Scale;
                    dialog.MaxWidth = dialog.Width; dialog.MaxHeight = dialog.Height;
                    if (dialog.Content is FrameworkElement content)
                        content.Width = Math.Max(content.MinWidth, dialog.Width - _resizeContentInset);
                    // The stock template centers BackgroundElement and does not bind
                    // its size to ContentDialog.Width/Height. Explicit user sizing owns
                    // that card as well as the outer HWND; initial auto-fit stays intact.
                    if (_card is { } card)
                    {
                        card.MaxWidth = card.Width = dialog.Width;
                        card.MaxHeight = card.Height = dialog.Height;
                        card.HorizontalAlignment = HorizontalAlignment.Left;
                        card.VerticalAlignment = VerticalAlignment.Top;
                    }
                }
                AppWindow.MoveAndResize(new RectInt32(left, top, right - left, bottom - top)); ApplyCardRegion(right - left, bottom - top);
                _root.InvalidateMeasure();
            }
            finally { _fitting = false; }
            return;
        }
        if (_dragPointer is null) return;
        var originalWork = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest).WorkArea;
        var work = ConstrainToOwnerTaskbar(originalWork);
        var size = AppWindow.Size;
        var width = work.Equals(originalWork) ? size.Width : Math.Min(size.Width, work.Width);
        var height = work.Equals(originalWork) ? size.Height : Math.Min(size.Height, work.Height);
        var x = Math.Clamp(_dragOrigin.X + cursor.X - _dragStart.X, work.X, work.X + Math.Max(0, work.Width - width));
        var y = Math.Clamp(_dragOrigin.Y + cursor.Y - _dragStart.Y, work.Y, work.Y + Math.Max(0, work.Height - height));
        if (width == size.Width && height == size.Height) AppWindow.Move(new PointInt32(x, y));
        else AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void EndDrag(PointerRoutedEventArgs e, string reason, bool consumeFinal = false, bool releaseCapture = true)
    {
        if (_resizePointer != e.Pointer.PointerId && _dragPointer != e.Pointer.PointerId) return;
        var kind = _resizePointer is not null ? "resize" : "move";
        var edges = _resizePointer is not null ? _resizeEdges : 0;
        NativePoint? finalPoint = null;
        try
        {
            // Freeze the release event before applying its rectangle or relinquishing
            // capture. Cancel/loss never substitutes the current cursor for that event.
            var haveReleasePoint = e.Pointer.PointerDeviceType == PointerDeviceType.Mouse
                ? GetCursorPos(out var releasedAt)
                : TryGetScreenPoint(e.GetCurrentPoint(_root).Position, out releasedAt);
            if (consumeFinal && haveReleasePoint)
            {
                finalPoint = releasedAt;
                ApplyDragPoint(releasedAt);
            }
        }
        finally
        {
            _resizePointer = null;
            _dragPointer = null;
            if (releaseCapture) _root.ReleasePointerCapture(e.Pointer);
            // PointerExited can occur during capture, with no second exit after
            // release. End the cursor lifetime with the gesture, not the next move.
            _root.SetResizeCursor(0);
            TraceDrag(reason, e.Pointer.PointerId, kind, edges, finalPoint);
        }
        e.Handled = true;
    }

    private bool TryGetScreenPoint(Point rootPoint, out NativePoint screenPoint)
    {
        screenPoint = default;
        var scale = Scale;
        if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(rootPoint.X) || !double.IsFinite(rootPoint.Y)) return false;
        // DialogRoot is this HWND's Content, so its DIP origin is the client origin.
        var origin = new NativePoint();
        if (!ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(this), ref origin)) return false;
        screenPoint = new NativePoint
        {
            X = origin.X + (int)Math.Round(rootPoint.X * scale),
            Y = origin.Y + (int)Math.Round(rootPoint.Y * scale),
        };
        return true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    private void TraceDrag(string phase, uint pointer, string kind, int edges, NativePoint? point)
    {
        if (!_traceInput) return;
        try
        {
            var position = AppWindow.Position;
            var size = AppWindow.Size;
            AppLog.Warn("OwnedDialogInput", $"{phase} pointer={pointer} kind={kind} edges={edges} " +
                $"point={(point is { } p ? $"{p.X},{p.Y}" : "none")} start={_dragStart.X},{_dragStart.Y} " +
                $"rect={position.X},{position.Y},{size.Width},{size.Height} root={_root.ActualWidth},{_root.ActualHeight} scale={Scale}");
        }
        catch (Exception) { /* Optional diagnostics cannot interrupt capture cleanup. */ }
    }

    private static FrameworkElement? FindPart(DependencyObject parent, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindPart(child, name) is { } found) return found;
        }
        return null;
    }

    internal void Release()
    {
        if (_releasing) return;
        _releasing = true;
        _backgroundClosing = true;
        CancelBackgroundCommit();
        _snapshotImage.Source = null;
        _snapshotBounds = null;
        _backgroundImage.Source = null;
        if (_backdrop is not null)
        {
            _backdrop.WaitForFallbackCommit = null;
            _backdrop.HasOpaqueSnapshotCover = null;
        }
        _root.SetResizeCursor(0);
        DetachThemeWait();
        try
        {
            if (_backdrop is not null) SystemBackdrop = null;
        }
        finally
        {
            _nativeBackdropSurface?.Dispose();
            Win32Api.EnableWindow(WinRT.Interop.WindowNative.GetWindowHandle(_owner), true);
            Close();
            if (_owner.AppWindow.IsVisible) _owner.Activate();
        }
    }

    /// <summary>Measure at the screen budget so a small host cannot lock out larger pages.</summary>
    private sealed class DialogRoot(OwnedDialogWindow host) : Panel
    {
        internal OwnedDialogWindow Host => host;
        private int _cursorEdges;
        internal void SetResizeCursor(int edges)
        {
            if (_cursorEdges == edges) return;
            _cursorEdges = edges;
            var previous = ProtectedCursor;
            ProtectedCursor = edges switch
            {
                1 or 2 => InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast),
                4 or 8 => InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth),
                5 or 10 => InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast),
                6 or 9 => InputSystemCursor.Create(InputSystemCursorShape.SizeNortheastSouthwest),
                _ => null,
            };
            previous?.Dispose();
            // A disabled owner does not refresh the cursor when capture ends
            // outside this HWND. Reset that stale resize cursor explicitly.
            if (edges == 0) SetCursor(LoadCursorW(IntPtr.Zero, new IntPtr(32512)));
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var work = host.WorkArea;
            var budget = new Size(work.Width / host.Scale, work.Height / host.Scale);
            var desired = new Size();
            foreach (var child in Children)
            {
                if (ReferenceEquals(child, host._backgroundLayer)) continue;
                child.Measure(host._userSized ? availableSize : budget);
                desired = child.DesiredSize;
            }
            host._backgroundLayer.Measure(availableSize);
            return new Size(Math.Min(availableSize.Width, desired.Width), Math.Min(availableSize.Height, desired.Height));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (var child in Children)
                child.Arrange(new Rect(new Point(), host._userSized || ReferenceEquals(child, host._backgroundLayer) ? finalSize : child.DesiredSize));
            return finalSize;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
