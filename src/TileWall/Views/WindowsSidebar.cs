using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TileWall.Core.Wall;
using TileWall.Resources;
using TileWall.Shell;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TileWall.Views;

/// <summary>A fixed Windows destination rail; expanded text overlays the wall.</summary>
internal sealed class WindowsSidebar : Grid
{
    internal const double ExpandedWidth = WallViewportPolicy.MenuWidthDip;
    private const double CollapsedWidth = 48;
    private sealed record Row(string Key, WindowsShortcut? Shortcut, Button Button, Grid Content, TextBlock Label);
    private readonly List<Row> _rows = [];
    private readonly StackPanel _items = new();
    private readonly Grid _itemsHost = new();
    private readonly ScrollViewer _scroll;
    private readonly Action<string> _status;
    private readonly Action<bool> _menuVisibility;
    private readonly AccessibilitySettings _accessibility = new();
    private readonly UISettings _colors = new();
    private UIElement? _pointerTrackingRoot;
    private PointerEventHandler? _pointerMovedHandler;
    private AnchorCorner? _anchor;
    private WallLanguageMode? _language;
    private bool _expanded, _pointerInside, _menuOpen;

    internal WindowsSidebar(Action<string> status, Action<bool> menuVisibility)
    {
        _status = status; _menuVisibility = menuVisibility;
        Width = CollapsedWidth;
        AutomationProperties.SetAutomationId(this, "windows-sidebar");
        _itemsHost.Children.Add(_items);
        _scroll = new ScrollViewer
        {
            Content = _itemsHost, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch, ZoomMode = ZoomMode.Disabled,
        };
        AutomationProperties.SetAutomationId(_scroll, "windows-sidebar-scroll");
        _scroll.SizeChanged += (_, args) =>
        {
            // A vertical ScrollViewer measures its content with unlimited height. Give
            // the arranging Grid a viewport floor so Bottom aligns the actual rows;
            // the floor never caps a taller list, which keeps short-window scrolling.
            _itemsHost.MinHeight = args.NewSize.Height;
            ScrollToAnchor();
        };
        _itemsHost.SizeChanged += (_, _) => DispatcherQueue.TryEnqueue(ScrollToAnchor);
        _items.SizeChanged += (_, _) => ScrollToAnchor();
        // Standard Button visual states provide pointer, pressed, disabled and keyboard
        // focus feedback. Only the rail's transparent resting fill is overridden.
        Resources["ButtonBackground"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Resources["ButtonBorderBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Add("Power", "\uE7E8", null);
        Add("Settings", "\uE713", WindowsShortcut.Settings);
        Add("Explorer", "\uEC50", WindowsShortcut.Explorer);
        Add("Profile", "\uEC25", WindowsShortcut.Profile);
        Add("Network", "\uEC27", WindowsShortcut.Network);
        Add("Videos", "\uE714", WindowsShortcut.Videos);
        Add("Pictures", "\uEB9F", WindowsShortcut.Pictures);
        Add("Music", "\uEC4F", WindowsShortcut.Music);
        Add("Downloads", "\uE896", WindowsShortcut.Downloads);
        Add("Documents", "\uE7C3", WindowsShortcut.Documents);
        Add("User", "\uE77B", null);
        Add("TileWall", string.Empty, null);
        var rail = new Grid();
        rail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rail.Children.Add(_rows.Single(row => row.Key == "TileWall").Button);
        Grid.SetRow(_scroll, 1); rail.Children.Add(_scroll);
        Children.Add(rail);
        PointerEntered += (_, _) => { _pointerInside = true; SetExpanded(true); };
        PointerExited += (_, args) =>
        {
            var point = args.GetCurrentPoint(this).Position;
            if (point.X >= 0 && point.Y >= 0 && point.X < ActualWidth && point.Y < ActualHeight) return;
            _pointerInside = false;
            if (!_menuOpen) SetExpanded(false);
        };
        GotFocus += (_, _) => SetExpanded(true);
        LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_pointerInside || _menuOpen || XamlRoot is null) return;
            var current = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            while (current is not null)
            {
                if (ReferenceEquals(current, this)) return;
                current = VisualTreeHelper.GetParent(current);
            }
            SetExpanded(false);
        });
        KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Escape || _menuOpen) return;
            SetExpanded(false); args.Handled = true;
        };
        ActualThemeChanged += (_, _) => RefreshBrush();
        Loaded += (_, _) => RefreshBrush();
        Unloaded += (_, _) =>
        {
            foreach (var row in _rows) row.Button.Flyout?.Hide();
            StopPointerTracking();
            _pointerInside = false; SetExpanded(false);
        };
    }

    private void Add(string key, string glyph, WindowsShortcut? shortcut)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CollapsedWidth) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FrameworkElement icon = key == "User" ? CreateUserIcon() : key == "TileWall" ?
            new Image { Width = 16, Height = 16, Source = BrandSource() } : ActionIcon.Glyph(glyph);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(icon);
        var label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
            TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed,
        };
        Grid.SetColumn(label, 1); content.Children.Add(label);
        var button = new Button
        {
            Content = content, Height = 48, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(button, "windows-sidebar-" + key.ToLowerInvariant());
        if (key == "TileWall") { button.IsTabStop = false; button.AllowFocusOnInteraction = false; }
        if (shortcut is { } action) button.Click += async (_, _) => await InvokeAsync(action);
        _rows.Add(new Row(key, shortcut, button, content, label));
    }

    private static PersonPicture CreateUserIcon()
    {
        var icon = new PersonPicture
        {
            Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        icon.Resources["SymbolThemeFontFamily"] = new FontFamily("Segoe MDL2 Assets");

        var path = LocalAccountPicture.FindSmallPicturePath();
        if (path is null) return icon;

        try
        {
            icon.ProfilePicture = new BitmapImage { UriSource = new Uri(path, UriKind.Absolute) };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or
            System.Security.SecurityException or System.Runtime.InteropServices.COMException)
        {
            // PersonPicture shows its built-in MDL2 person glyph when the photo is unavailable.
        }
        return icon;
    }

    internal void Configure(AnchorCorner anchor, bool fullScreen)
    {
        // The caller owns all viewport geometry. In both modes this is an overlay host;
        // only the caller budgets the collapsed 48 DIP rail into windowed wall sizing.
        Tag = fullScreen ? "fullscreen" : "windowed";
        if (_anchor != anchor)
        {
            _anchor = anchor;
            var right = anchor is AnchorCorner.TopRight or AnchorCorner.BottomRight;
            var bottom = anchor is AnchorCorner.BottomLeft or AnchorCorner.BottomRight;
            _items.VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            _items.Children.Clear();
            var entries = _rows.Where(row => row.Key != "TileWall");
            var ordered = bottom ? entries.Reverse() : entries;
            foreach (var row in ordered) _items.Children.Add(row.Button);
            foreach (var row in _rows)
            {
                row.Content.ColumnDefinitions[0].Width = right ? new GridLength(1, GridUnitType.Star) : new GridLength(CollapsedWidth);
                row.Content.ColumnDefinitions[1].Width = right ? new GridLength(CollapsedWidth) : new GridLength(1, GridUnitType.Star);
                Grid.SetColumn((FrameworkElement)row.Content.Children[0], right ? 1 : 0);
                Grid.SetColumn(row.Label, right ? 0 : 1);
                row.Label.Margin = right ? new Thickness(12, 0, 0, 0) : new Thickness(0, 0, 12, 0);
                if (row.Button.Flyout is { } flyout) flyout.Hide();
            }
            // For short windows, begin at the anchored end of the independently scrolling rail.
            DispatcherQueue.TryEnqueue(ScrollToAnchor);
        }
        if (_language != ProductStrings.Language)
        {
            _language = ProductStrings.Language;
            foreach (var row in _rows)
            {
                var text = row.Key == "TileWall" ? "TileWall" : ProductStrings.Get("Sidebar." + row.Key);
                row.Label.Text = text;
                AutomationProperties.SetName(row.Button, text);
                ActionIcon.SetTip(row.Button, text);
                if (row.Shortcut is null && row.Key != "TileWall")
                {
                    row.Button.Flyout?.Hide();
                    row.Button.Flyout = CreateMenu(row.Key == "Power");
                }
            }
        }
    }

    private MenuFlyout CreateMenu(bool power)
    {
        // Auto is rejected by WinUI's native Flyout placement validator when assigned directly.
        var presenterStyle = (Style)XamlReader.Load($$"""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="MenuFlyoutPresenter">
                <Setter Property="MinWidth" Value="{{ExpandedWidth}}" />
                <Setter Property="Width" Value="{{ExpandedWidth}}" />
                <Setter Property="MaxWidth" Value="{{ExpandedWidth}}" />
            </Style>
            """);
        // Keep the shared MenuFlyoutPresenter theme while pinning all size constraints.
        if (Application.Current.Resources.TryGetValue(typeof(MenuFlyoutPresenter), out var sharedStyle)
            && sharedStyle is Style baseStyle)
            presenterStyle.BasedOn = baseStyle;
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Top, MenuFlyoutPresenterStyle = presenterStyle };
        var actions = power
            ? new[] { WindowsShortcut.Sleep, WindowsShortcut.ShutDown, WindowsShortcut.Restart }
            : [WindowsShortcut.AccountSettings, WindowsShortcut.Lock, WindowsShortcut.SignOut];
        foreach (var action in actions)
        {
            var key = action.ToString();
            var item = new MenuFlyoutItem { Text = ProductStrings.Get("Sidebar." + key), Icon = ActionIcon.Glyph(action switch
            {
                WindowsShortcut.Sleep => "\uE708", WindowsShortcut.ShutDown => "\uE7E8", WindowsShortcut.Restart => "\uE72C",
                WindowsShortcut.AccountSettings => "\uE713", WindowsShortcut.Lock => "\uE72E", _ => "\uF3B1",
            }) };
            AutomationProperties.SetAutomationId(item, "windows-sidebar-" + key.ToLowerInvariant());
            AutomationProperties.SetName(item, item.Text);
            item.Click += async (_, _) => await InvokeAsync(action);
            menu.Items.Add(item);
        }
        menu.Opening += (_, _) =>
        {
            for (var i = 0; i < actions.Length; i++)
            {
                var item = (MenuFlyoutItem)menu.Items[i];
                item.IsEnabled = WindowsShortcutLauncher.IsAvailable(actions[i]);
                var tip = item.IsEnabled ? item.Text : ProductStrings.Get("Sidebar.Unavailable");
                ToolTipService.SetToolTip(item, tip);
                AutomationProperties.SetHelpText(item, item.IsEnabled ? "" : tip);
            }
            _menuOpen = true; SetExpanded(true); _menuVisibility(true);
        };
        menu.Closed += (_, _) =>
        {
            _menuOpen = false; _menuVisibility(false);
            if (!_pointerInside) SetExpanded(false);
        };
        return menu;
    }

    private async Task InvokeAsync(WindowsShortcut action)
    {
        var error = await WindowsShortcutLauncher.OpenAsync(action);
        if (error is not null) _status(string.Format(ProductStrings.Get("Sidebar.OpenFailed"), error));
    }

    private void ScrollToAnchor() => _scroll.ChangeView(null,
        _anchor is AnchorCorner.BottomLeft or AnchorCorner.BottomRight ? _scroll.ScrollableHeight : 0, null, true);

    internal void Collapse()
    {
        foreach (var row in _rows) row.Button.Flyout?.Hide();
        _pointerInside = false;
        SetExpanded(false);
    }

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded)
        {
            if (expanded) StartPointerTracking();
            return;
        }
        _expanded = expanded; Width = expanded ? ExpandedWidth : CollapsedWidth;
        foreach (var row in _rows) row.Label.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (expanded) StartPointerTracking(); else StopPointerTracking();
        RefreshBrush();
    }

    private void StartPointerTracking()
    {
        if (_pointerTrackingRoot is not null || XamlRoot?.Content is not UIElement root) return;
        _pointerTrackingRoot = root;
        _pointerMovedHandler = TrackPointerPosition;
        root.AddHandler(UIElement.PointerMovedEvent, _pointerMovedHandler, true);
    }

    private void StopPointerTracking()
    {
        if (_pointerTrackingRoot is { } root && _pointerMovedHandler is { } handler)
            root.RemoveHandler(UIElement.PointerMovedEvent, handler);
        _pointerTrackingRoot = null;
        _pointerMovedHandler = null;
    }

    private void TrackPointerPosition(object sender, PointerRoutedEventArgs args)
    {
        if (!_expanded || _pointerTrackingRoot is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var point = args.GetCurrentPoint(this).Position;
        var inside = point.X >= 0 && point.Y >= 0 && point.X < ActualWidth && point.Y < ActualHeight;
        _pointerInside = inside;
        if (!inside && !_menuOpen) SetExpanded(false);
    }

    private void RefreshBrush()
    {
        // ActualTheme inherits the wall's explicit/system theme. Native Button styles
        // retain their own high-contrast hover, pressed and keyboard focus resources.
        var contrast = _accessibility.HighContrast;
        var color = ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
        Background = contrast ? new SolidColorBrush(_colors.GetColorValue(UIColorType.Background))
            : !_expanded ? new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            : !_colors.AdvancedEffectsEnabled ? new SolidColorBrush(color)
            : new AcrylicBrush { TintColor = color, TintOpacity = .65, FallbackColor = color };
        foreach (var row in _rows.Where(row => row.Key == "TileWall"))
            if (row.Content.Children[0] is Image brand) brand.Source = BrandSource();
    }

    private SvgImageSource BrandSource() => new(new Uri(ActualTheme == ElementTheme.Dark
        ? "ms-appx:///Assets/TileWallMonochromeLight.svg" : "ms-appx:///Assets/TileWallMonochrome.svg"));
}
