using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Settings;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;
using TileWall.Shell.Interop;

namespace TileWall.Views;

internal sealed record SettingsDialogState(WallSettings Settings, string? RegisteredHotKey,
    bool? LoginStartupEnabled, string? Error = null, bool StartReplacementActive = false,
    bool StartReplacementSuspended = false, string? StartReplacementError = null,
    string? RegisteredSecondaryHotKey = null, string? HotKeyError = null,
    string? SecondaryHotKeyError = null, bool TaskbarStartReplacementActive = false,
    bool TaskbarStartReplacementSuspended = false, string? TaskbarStartReplacementError = null,
    string? TaskbarActivationError = null);

/// <summary>Immediate settings edits. The host owns persistence and operating-system rollback.</summary>
internal sealed class SettingsDialog : IWallDialogSession
{
    private const string CommunityGroupUrl =
        "https://qm.qq.com/cgi-bin/qm/qr?k=iw0LxnFaHE8JdUr5z937pFuagFxOtFOo&jump_from=webapi&authKey=JvkcK/IIaFg5e1ymzhP41yxcAiTVURjvhrNtDziZZSGj3ZD2byZhqX2lj48L9jkT";
    private readonly Window _owner;
    private readonly ExplorerContextMenuController _contextMenuController;
    private readonly Func<SettingsDialogState> _readState;
    private readonly Func<WallSettings, bool, Task<SettingsDialogState>> _apply;
    private readonly Func<int, bool, Task>? _hotKeyRecordingChanged;
    private readonly Func<bool, Task<SettingsDialogState>>? _retryStartReplacement;
    private readonly Func<CancellationToken, Task<string?>> _initializeWall;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _startReplacementWatch;
    private readonly ContentDialog _dialog;
    private readonly Grid _settingsBody;
    private readonly StackPanel _form = new() { Spacing = 12 };
    private readonly ListView _groupNavigation = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        IsItemClickEnabled = true,
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0),
        VerticalAlignment = VerticalAlignment.Top,
    };
    private readonly List<(Border Card, ListViewItem Item, string LabelKey)> _groups = [];
    private readonly Border _groupHighlight = (Border)XamlReader.Load("""
        <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                Background="{ThemeResource LayerFillColorDefaultBrush}"
                BorderBrush="{ThemeResource ControlStrokeColorDefaultBrush}" BorderThickness="1"
                VerticalAlignment="Top" IsHitTestVisible="False" Visibility="Collapsed" />
        """);
    private readonly TranslateTransform _groupHighlightOffset = new();
    private readonly Button _close = new() { HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 8, 0), AllowFocusOnInteraction = false };
    private readonly Dictionary<string, TextBlock> _labels = [];
    private readonly ScrollViewer _formHost = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
    private readonly StackPanel _anchor = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ToggleButton[] _anchorButtons = Enumerable.Range(0, 4).Select(_ => new ToggleButton { Width = 32, Height = 32 }).ToArray();
    private readonly ToggleSwitch _fullScreen = new();
    private readonly ToggleSwitch _expandColumns = new(), _menuStart = new();
    private static readonly WallLanguageMode[] LanguageModes = [WallLanguageMode.System, .. WallLanguages.Supported];
    private readonly ComboBox _language = new(), _visualStyle = new(), _theme = new(), _background = new();
    private readonly ToggleSwitch _topmost = new(), _stayVisible = new(), _layoutLocked = new(), _activateTaskbar = new(), _runAtLogin = new(), _showGrid = new();
    private readonly HotKeyRecorder _hotKey = new(), _secondaryHotKey = new();
    private readonly Button _resetHotKey = new(), _resetSecondaryHotKey = new();
    private readonly TextBlock _hotKeyDiagnostic = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 180, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _secondaryHotKeyDiagnostic = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 180, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly ToggleSwitch _replaceStartMenu = new();
    private readonly TextBlock _startReplacementStatus = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Button _retryStart = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ToggleSwitch _redirectTaskbarStartButton = new();
    private readonly TextBlock _taskbarStartReplacementStatus = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Button _retryTaskbarStart = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _keyboardExperimental = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Opacity = .75 };
    private readonly TextBlock _taskbarExperimental = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Opacity = .75 };
    private readonly TextBlock _keyBehaviorKeyHeader = new(), _keyBehaviorActionHeader = new();
    private readonly TextBlock[] _keyBehaviorKeys = Enumerable.Range(0, 5).Select(_ => new TextBlock { TextWrapping = TextWrapping.Wrap }).ToArray();
    private readonly TextBlock[] _keyBehaviorActions = Enumerable.Range(0, 5).Select(_ => new TextBlock { TextWrapping = TextWrapping.Wrap }).ToArray();
    private readonly TextBlock _taskbarActivationStatus = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Visibility = Visibility.Collapsed };
    private readonly Slider _transparency = new() { Minimum = 0, Maximum = 100, StepFrequency = 1, Width = 180, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly NumberBox _transparencyInput = new() { Minimum = 0, Maximum = 100, SmallChange = 1, Width = 56, MinWidth = 0, Padding = new Thickness(8, 5, 8, 5), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
    private readonly TextBlock _backgroundPath = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _reset = new();
    private readonly Button _initialize = new();
    private readonly ToggleSwitch _contextMenu = new();
    private readonly TextBlock _contextMenuTransfer = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _contextMenuConfirmation = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly Button _confirmContextTransfer = new(), _cancelContextTransfer = new();
    private readonly List<(Button Button, string LabelKey)> _linkButtons = [];
    private readonly TextBlock _version = new();
    private readonly StatusOverlay _status = new();
    private SettingsDialogState _state;
    private Task _pending = Task.CompletedTask;
    private Task _contextPending = Task.CompletedTask;
    private CancellationTokenSource? _backgroundProbe;
    private CancellationTokenSource? _initialization;
    private TaskCompletionSource<bool>? _initializationConfirmation;
    private bool _refreshing, _busy, _closeRequested, _forceClose, _showFailureBeforeClose;
    private bool _contextPageOpen;
    private bool _syncingGroupSelection;
    private bool _contextRefreshing, _contextRefreshRequested;
    private bool _persistentStatusVisible;
    private long _contextMutationRevision;
    private string? _contextOutcomeKey, _contextOutcomeArgument, _progressKey;
    private string[]? _contextTransferTargets;
    private ExplorerContextMenuState? _contextState;
    private readonly bool _traceInput = Environment.GetEnvironmentVariable("TILEWALL_TRACE_SETTINGS") == "1" ||
        Environment.GetEnvironmentVariable("TILEWALL_TRACE_INPUT") == "1";
    private readonly HashSet<Thumb> _tracedThumbs = [];
    private int _inputSequence;

    public SettingsDialog(Window owner, ExplorerContextMenuController contextMenuController, Func<SettingsDialogState> readState,
        Func<WallSettings, bool, Task<SettingsDialogState>> applyAsync, Func<int, bool, Task>? hotKeyRecordingChanged = null,
        Func<bool, Task<SettingsDialogState>>? retryStartReplacement = null,
        Func<CancellationToken, Task<string?>>? initializeWall = null)
    {
        _contextMenuController = contextMenuController;
        _owner = owner; _readState = readState; _apply = applyAsync; _state = readState();
        _hotKeyRecordingChanged = hotKeyRecordingChanged;
        _retryStartReplacement = retryStartReplacement;
        _initializeWall = initializeWall ?? (_ => Task.FromResult<string?>(ProductStrings.Get("Wall.SettingsBlocked")));
        _startReplacementWatch = _form.DispatcherQueue.CreateTimer();
        _startReplacementWatch.Interval = TimeSpan.FromMilliseconds(500);
        _startReplacementWatch.IsRepeating = true;
        _startReplacementWatch.Tick += (_, _) => RefreshStartReplacementState();
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var key in new[] { "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver",
            "ListViewItemBackgroundSelectedPressed", "ListViewItemBackgroundSelectedDisabled" })
            _groupNavigation.Resources[key] = transparent;
        _groupNavigation.Resources["ListViewItemSelectionIndicatorVisualEnabled"] = false;
        _groupNavigation.SelectionChanged += (_, _) =>
        {
            if (!_syncingGroupSelection) NavigateToGroup(_groupNavigation.SelectedIndex);
        };
        _groupNavigation.ItemClick += (_, args) => NavigateToGroup(args.ClickedItem is ListViewItem item
            ? _groupNavigation.Items.IndexOf(item) : _groupNavigation.SelectedIndex);
        _groupHighlight.RenderTransform = _groupHighlightOffset;
        var budget = OwnedDialogWindow.ContentBudget(owner);
        const double navigationWidth = 148, columnGap = 16;
        var pageWidth = Math.Min(740, budget.Width);
        var compact = pageWidth - navigationWidth - columnGap < 570;
        var actionsOrientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        foreach (var box in new[] { _theme, _visualStyle, _language, _background })
        {
            box.Width = 180; box.HorizontalAlignment = HorizontalAlignment.Left;
        }
        for (var index = 0; index < _anchorButtons.Length; index++)
        {
            var anchor = (AnchorCorner)index;
            _anchor.Children.Add(_anchorButtons[index]);
            _anchorButtons[index].Click += (_, _) => BeginApply(s => s with { AnchorCorner = anchor });
        }
        _hotKey.RecordingChanged += recording => ChangeHotKeyRecordingAsync(1, _hotKey, _secondaryHotKey, recording);
        _secondaryHotKey.RecordingChanged += recording => ChangeHotKeyRecordingAsync(2, _secondaryHotKey, _hotKey, recording);
        _hotKey.GestureRecorded += gesture => BeginApply(s => s with { HotKey = gesture.ToConfigString() });
        _secondaryHotKey.GestureRecorded += gesture => BeginApply(s => s with { SecondaryHotKey = gesture.ToConfigString() });
        _hotKey.RecordingFailed += ShowHotKeyRecordingFailure;
        _secondaryHotKey.RecordingFailed += ShowHotKeyRecordingFailure;
        var appearance = AddGroup("Settings.Appearance", "appearance", compact);
        AddRow(appearance, "Settings.Language", _language);
        AddRow(appearance, "Settings.Theme", _theme);
        AddRow(appearance, "Settings.VisualStyle", _visualStyle);
        var backgroundRow = new Grid { ColumnSpacing = 12 };
        backgroundRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        backgroundRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        backgroundRow.Children.Add(_background); Grid.SetColumn(_backgroundPath, 1); backgroundRow.Children.Add(_backgroundPath);
        AddRow(appearance, "Settings.Background", backgroundRow);
        // The inner TextBox otherwise retains its theme's 64 DIP minimum.
        _transparencyInput.Resources["TextControlThemeMinWidth"] = 0d;
        _transparencyInput.Resources["NumberBoxMinWidth"] = 0d;
        var transparencyRow = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
        transparencyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transparencyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transparencyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transparencyRow.Children.Add(_transparencyInput);
        var percent = new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(percent, 1); transparencyRow.Children.Add(percent);
        Grid.SetColumn(_transparency, 2); transparencyRow.Children.Add(_transparency);
        AddRow(appearance, "Settings.Transparency", transparencyRow);
        AddRow(appearance, "Settings.ShowGrid", _showGrid);
        var invocation = AddGroup("Settings.Invocation", "invocation", compact);
        var taskbarStartReplacementRow = new StackPanel { Spacing = 8 };
        taskbarStartReplacementRow.Children.Add(ExperimentalToggleRow(_redirectTaskbarStartButton, _taskbarExperimental));
        taskbarStartReplacementRow.Children.Add(_taskbarStartReplacementStatus);
        taskbarStartReplacementRow.Children.Add(_retryTaskbarStart);
        AddRow(invocation, "Settings.RedirectTaskbarStartButton", taskbarStartReplacementRow);
        var startReplacementRow = new StackPanel { Spacing = 8 };
        startReplacementRow.Children.Add(ExperimentalToggleRow(_replaceStartMenu, _keyboardExperimental));
        startReplacementRow.Children.Add(_startReplacementStatus);
        startReplacementRow.Children.Add(_retryStart);
        AddRow(invocation, "Settings.ReplaceStartMenu", startReplacementRow);
        AddRow(invocation, "Settings.HotKey", HotKeyRow(_resetHotKey, _hotKey, _hotKeyDiagnostic, compact));
        AutomationProperties.SetLabeledBy(_hotKey, _labels["Settings.HotKey"]);
        Id(_resetHotKey, "reset-hot-key");
        AddRow(invocation, "Settings.SecondaryHotKey", HotKeyRow(_resetSecondaryHotKey, _secondaryHotKey, _secondaryHotKeyDiagnostic, compact));
        AutomationProperties.SetLabeledBy(_secondaryHotKey, _labels["Settings.SecondaryHotKey"]);
        Id(_resetSecondaryHotKey, "reset-secondary-hot-key");
        _resetHotKey.Click += (_, _) =>
        {
            if (_refreshing || _busy) return;
            CancelHotKeyRecordings();
            BeginApply(s => s with { HotKey = new WallSettings().HotKey });
        };
        _resetSecondaryHotKey.Click += (_, _) =>
        {
            if (_refreshing || _busy) return;
            CancelHotKeyRecordings();
            BeginApply(s => s with { SecondaryHotKey = new WallSettings().SecondaryHotKey });
        };
        AddRow(invocation, "Settings.KeyBehavior", KeyBehaviorTable());
        var general = AddGroup("Settings.Wall", "wall", compact);
        AddRow(general, "Settings.LayoutLocked", _layoutLocked);
        AddRow(general, "Settings.Anchor", _anchor);
        AddRow(general, "Settings.MenuStart", _menuStart);
        AddRow(general, "Settings.FullScreen", _fullScreen);
        AddRow(general, "Settings.ExpandColumns", _expandColumns);
        var integration = AddGroup("Settings.System", "system", compact);
        AddRow(integration, "Settings.Topmost", _topmost);
        AddRow(integration, "Settings.StayVisible", _stayVisible);
        var taskbarActivationRow = new StackPanel { Spacing = 8 };
        taskbarActivationRow.Children.Add(_activateTaskbar);
        taskbarActivationRow.Children.Add(_taskbarActivationStatus);
        AddRow(integration, "Settings.ActivateTaskbar", taskbarActivationRow);
        AddRow(integration, "Settings.RunAtLogin", _runAtLogin);
        var contextMenuRow = new StackPanel { Spacing = 8 };
        contextMenuRow.Children.Add(_contextMenu);
        _contextMenuConfirmation.Children.Add(_contextMenuTransfer);
        var contextActions = new StackPanel { Orientation = actionsOrientation, Spacing = 8 };
        contextActions.Children.Add(_confirmContextTransfer); contextActions.Children.Add(_cancelContextTransfer);
        _contextMenuConfirmation.Children.Add(contextActions);
        contextMenuRow.Children.Add(_contextMenuConfirmation);
        AddRow(integration, "Settings.ExplorerMenu", contextMenuRow);
        var initializationActions = new StackPanel { Orientation = actionsOrientation, Spacing = 8 };
        initializationActions.Children.Add(_reset); initializationActions.Children.Add(_initialize);
        AddRow(integration, "Settings.Initialization", initializationActions);
        var about = AddGroup("Settings.About", "about", compact);
        AddRow(about, "Settings.Product", new TextBlock { Text = "TileWall", VerticalAlignment = VerticalAlignment.Center });
        AddRow(about, "Settings.VersionLabel", _version);
        var visitLinks = new StackPanel { Orientation = actionsOrientation, Spacing = 8 };
        var storeLink = Link("Settings.MicrosoftStore", "https://apps.microsoft.com/detail/9NZZN35FDB0C");
        var sourceLink = Link("Settings.OpenSource", "https://github.com/GitRuozhi/TileWall_for_Win11");
        var developerLink = Link("Settings.Developer", "https://github.com/GitRuozhi");
        Id(storeLink, "visit-store"); Id(sourceLink, "visit-source"); Id(developerLink, "visit-developer");
        visitLinks.Children.Add(storeLink); visitLinks.Children.Add(sourceLink); visitLinks.Children.Add(developerLink);
        AddRow(about, "Settings.Visit", visitLinks);
        var feedbackLinks = new StackPanel { Orientation = actionsOrientation, Spacing = 8 };
        var discussionLink = Link("Settings.Discussion", "https://github.com/GitRuozhi/TileWall_for_Win11/discussions");
        var communityLink = Link("Settings.QQGroup", CommunityGroupUrl,
            "Settings.CommunityOpenFailed");
        Id(discussionLink, "feedback-discussion"); Id(communityLink, "community");
        feedbackLinks.Children.Add(discussionLink); feedbackLinks.Children.Add(communityLink);
        AddRow(about, "Settings.Feedback", feedbackLinks);
        var aboutCard = _groups[^1].Card;
        // Add the scroll extent below the card, where it leaves the material visible.
        _formHost.SizeChanged += (_, _) => UpdateEndPadding();
        aboutCard.SizeChanged += (_, _) => UpdateEndPadding();
        _form.Loaded += (_, _) => UpdateGroupHighlight();
        _groupNavigation.Loaded += (_, _) => UpdateGroupHighlight();
        _form.SizeChanged += (_, _) => UpdateGroupHighlight();
        _formHost.ViewChanged += (_, _) => UpdateGroupHighlight();

        void UpdateEndPadding()
        {
            var padding = Math.Max(0, _formHost.ActualHeight - aboutCard.ActualHeight);
            if (_form.Padding.Bottom != padding) _form.Padding = new Thickness(0, 0, 0, padding);
            UpdateGroupHighlight();
        }

        var root = (owner.Content as FrameworkElement)?.XamlRoot;
        var body = new Grid { Width = pageWidth, Height = Math.Min(590, budget.Height), ColumnSpacing = columnGap };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(navigationWidth) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var navigationPane = new Grid();
        navigationPane.Children.Add(_groupHighlight);
        navigationPane.Children.Add(_groupNavigation);
        var navigationCard = VisualStyles.Card(navigationPane);
        navigationCard.VerticalAlignment = VerticalAlignment.Top;
        body.Children.Add(navigationCard);
        _formHost.Content = _form;
        var formPane = new Grid();
        formPane.Children.Add(_formHost); formPane.Children.Add(_status);
        formPane.Children.Add(_close);
        Grid.SetColumn(formPane, 1); body.Children.Add(formPane);
        _dialog = new ContentDialog { XamlRoot = root, Content = body, DefaultButton = ContentDialogButton.None };
        _settingsBody = body;
        ConfigureActionFeedback(body);
        _status.TransientExpired += RefreshStatus;
        _dialog.Resources["ContentDialogMaxWidth"] = budget.Width + 48;
        _dialog.Resources["ContentDialogMaxHeight"] = budget.Height + 180;
        OwnedDialogWindow.EnablePageChrome(_dialog, body);
        _close.Click += (_, _) => RequestDismiss();
        Id(_close, "close");
        Id(_dialog, "dialog"); Id(_anchor, "anchor"); Id(_topmost, "topmost"); Id(_stayVisible, "stay-visible"); Id(_layoutLocked, "layout-locked");
        Id(_activateTaskbar, "activate-taskbar"); Id(_taskbarActivationStatus, "taskbar-activation-state");
        Id(_language, "language"); Id(_visualStyle, "visual-style"); Id(_theme, "theme"); Id(_background, "background");
        Id(_hotKey, "hotkey"); Id(_secondaryHotKey, "secondary-hotkey");
        Id(_hotKeyDiagnostic, "hotkey-diagnostic"); Id(_secondaryHotKeyDiagnostic, "secondary-hotkey-diagnostic");
        Id(_transparency, "transparency"); Id(_transparencyInput, "transparency-input");
        Id(_backgroundPath, "background-path"); Id(_showGrid, "show-grid"); Id(_runAtLogin, "run-at-login"); Id(_reset, "reset"); Id(_status, "status");
        Id(_contextMenu, "context-menu");
        Id(_groupNavigation, "group-navigation");
        Id(_initialize, "initialize-wall");
        _initialize.Click += (_, _) => BeginInitializeWall();
        Id(_replaceStartMenu, "replace-start-menu"); Id(_startReplacementStatus, "start-replacement-state");
        Id(_retryStart, "start-replacement-retry");
        Id(_redirectTaskbarStartButton, "redirect-taskbar-start-button");
        Id(_taskbarStartReplacementStatus, "taskbar-start-replacement-state");
        Id(_retryTaskbarStart, "taskbar-start-replacement-retry");
        Id(_confirmContextTransfer, "context-menu-confirm-transfer"); Id(_cancelContextTransfer, "context-menu-cancel-transfer");
        // A switch has no separate double-tap action. Treat two quick clicks as
        // two taps rather than letting the second click disappear into DoubleTapped.
        var toggles = new[] { _showGrid, _runAtLogin, _topmost, _stayVisible, _layoutLocked, _activateTaskbar, _menuStart,
            _fullScreen, _expandColumns, _replaceStartMenu, _contextMenu };
        foreach (var toggle in toggles)
        {
            toggle.IsDoubleTapEnabled = false;
            // Hit testing lands on template children, which own their gesture
            // flags independently of the ToggleSwitch itself.
            toggle.Loaded += (_, _) => DisableDoubleTap(toggle);
        }
        if (_traceInput)
        {
            body.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
                TraceSettings("BodyPressed", PointerDetail(body, e) +
                    $" enabled={_formHost.IsEnabled} scroll={_formHost.VerticalOffset:F2}")), true);
            body.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler((_, e) =>
                TraceSettings("DoubleTapped", $"source={e.OriginalSource?.GetType().Name}")), true);
            body.SizeChanged += (_, _) => TraceSettings("BodySize", $"size={body.ActualWidth:F2},{body.ActualHeight:F2}");
            foreach (var toggle in toggles)
            {
                toggle.Toggled += (_, _) => TraceSettings("Toggled",
                    $"{AutomationProperties.GetAutomationId(toggle)} isOn={toggle.IsOn}");
                toggle.Loaded += (_, _) => TraceThumbs(toggle, toggle);
                toggle.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
                {
                    _inputSequence++;
                    TraceSettings("PointerPressed", PointerDetail(toggle, e));
                }), true);
                toggle.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) =>
                    TraceSettings("PointerReleased", PointerDetail(toggle, e))), true);
                toggle.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, e) =>
                    TraceSettings("PointerCanceled", PointerDetail(toggle, e))), true);
                toggle.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, e) =>
                    TraceSettings("PointerCaptureLost", PointerDetail(toggle, e))), true);
                toggle.AddHandler(UIElement.TappedEvent, new TappedEventHandler((_, _) =>
                    TraceSettings("Tapped", AutomationProperties.GetAutomationId(toggle))), true);
            }
        }
        RefreshStrings(); RestoreState();
        _theme.SelectionChanged += (_, _) => BeginChoiceApply(_theme, s => s with { ThemeMode = (WallThemeMode)_theme.SelectedIndex });
        _language.SelectionChanged += (_, _) => BeginChoiceApply(_language, s => s with { LanguageMode = LanguageModes[_language.SelectedIndex] });
        _visualStyle.SelectionChanged += (_, _) => BeginChoiceApply(_visualStyle, s => s with { VisualStyle = (WallVisualStyle)_visualStyle.SelectedIndex });
        _background.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _busy) return;
            BeginChoiceApply(_background, s => s with { BackgroundMode = (WallBackgroundMode)_background.SelectedIndex },
                pickImage: _background.SelectedIndex == (int)WallBackgroundMode.Custom);
        };
        ((ComboBoxItem)_background.Items[(int)WallBackgroundMode.Custom]).AddHandler(UIElement.TappedEvent,
            new Microsoft.UI.Xaml.Input.TappedEventHandler((_, _) =>
            {
                if (_state.Settings.BackgroundMode == WallBackgroundMode.Custom &&
                    _background.SelectedIndex == (int)WallBackgroundMode.Custom)
                    BeginChoiceApply(_background, s => s, pickImage: true);
            }), true);
        _transparency.ValueChanged += (_, e) => SetTransparency(e.NewValue, apply: false);
        _transparency.PointerCaptureLost += (_, _) => ApplyTransparency();
        _transparency.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => ApplyTransparency()), true);
        _transparency.KeyUp += (_, _) => ApplyTransparency();
        _transparency.LostFocus += (_, _) => ApplyTransparency();
        _transparencyInput.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) SetTransparency(e.NewValue, apply: true); };
        _fullScreen.Toggled += (_, _) => BeginApply(s => s with { FullScreen = _fullScreen.IsOn });
        _expandColumns.Toggled += (_, _) => BeginApply(s => s with { ExpandColumns = _expandColumns.IsOn });
        _menuStart.Toggled += (_, _) => BeginApply(s => s with { MenuStart = _menuStart.IsOn });
        _topmost.Toggled += (_, _) => BeginApply(s => s with { AlwaysOnTop = _topmost.IsOn });
        _stayVisible.Toggled += (_, _) => BeginApply(s => s with { StayVisibleOnFocusLoss = _stayVisible.IsOn });
        _layoutLocked.Toggled += (_, _) => BeginApply(s => s with { LayoutLocked = _layoutLocked.IsOn });
        _activateTaskbar.Toggled += (_, _) => BeginApply(s => s with { ActivateTaskbar = _activateTaskbar.IsOn });
        _showGrid.Toggled += (_, _) => BeginApply(s => s with { ShowGrid = _showGrid.IsOn });
        _runAtLogin.Toggled += (_, _) => BeginApply(s => s with { RunAtLogin = _runAtLogin.IsOn });
        _replaceStartMenu.Toggled += (_, _) => BeginApply(s => s with { ReplaceStartMenu = _replaceStartMenu.IsOn });
        _redirectTaskbarStartButton.Toggled += (_, _) => BeginApply(s => s with { RedirectTaskbarStartButton = _redirectTaskbarStartButton.IsOn });
        _retryStart.Click += (_, _) =>
        {
            if (!_busy && _retryStartReplacement is not null) _pending = RetryStartReplacementAsync(taskbar: false);
        };
        _retryTaskbarStart.Click += (_, _) =>
        {
            if (!_busy && _retryStartReplacement is not null) _pending = RetryStartReplacementAsync(taskbar: true);
        };
        _reset.Click += (_, _) =>
        {
            if (_refreshing || _busy) return;
            CancelHotKeyRecordings(); _pending = ApplyAsync(new WallSettings(), restoreDefaults: true);
        };
        _contextMenu.Toggled += (_, _) =>
        {
            if (_refreshing || _busy || !_contextPageOpen) return;
            BeginContextMenuChange(_contextMenu.IsOn);
        };
        _confirmContextTransfer.Click += (_, _) =>
        {
            if (_contextTransferTargets is { } targets) BeginContextMenuChange(true, targets);
        };
        _cancelContextTransfer.Click += (_, _) =>
        {
            if (_busy) return;
            _contextTransferTargets = null;
            RefreshContextMenuPresentation();
        };
        _dialog.PrimaryButtonClick += (_, e) =>
        {
            if (_initializationConfirmation is not { } confirmation) return;
            e.Cancel = true; confirmation.TrySetResult(true);
        };
        _dialog.CloseButtonClick += (_, e) =>
        {
            if (_initializationConfirmation is not { } confirmation) return;
            e.Cancel = true; confirmation.TrySetResult(false);
        };
        _dialog.Closing += (_, e) =>
        {
            _backgroundProbe?.Cancel();
            // Esc cancels the warning in place; the owned host stays alive.
            if (_initializationConfirmation is { } confirmation)
            {
                e.Cancel = true; confirmation.TrySetResult(false); return;
            }
            _initialization?.Cancel();
            // Explicitly settle NumberBox's inner text before moving focus, so
            // its apply enters _pending before deciding whether this page closes.
            OwnedDialogWindow.ReleaseTextFocus(_dialog);
            CancelHotKeyRecordings();
            if (_forceClose && !_busy && !HasPendingHotKeyTransition) return;
            if (_showFailureBeforeClose) { _showFailureBeforeClose = false; e.Cancel = true; return; }
            if (_busy || HasPendingHotKeyTransition)
            {
                e.Cancel = true; _closeRequested = true;
                if (!_busy) _pending = FinishRecordingForCloseAsync();
            }
        };
    }

    public async Task ShowAsync()
    {
        _contextPageOpen = true;
        _startReplacementWatch.Start();
        _owner.Activated += OnOwnerActivated;
        BeginContextMenuRefresh();
        try { await OwnedDialogWindow.ShowAsync(_dialog); }
        finally
        {
            _contextPageOpen = false;
            _startReplacementWatch.Stop();
            _owner.Activated -= OnOwnerActivated;
            _backgroundProbe?.Cancel();
            try { await WaitForPendingAsync(); }
            finally { _status.Clear(); }
        }
    }
    public bool HasUnsavedDraft => false;
    public async Task WaitForPendingAsync()
    {
        _backgroundProbe?.Cancel();
        CancelInitialization();
        OwnedDialogWindow.ReleaseTextFocus(_dialog);
        await CancelAndWaitForHotKeysAsync();
        await AwaitPendingAsync();
        await WaitForHotKeysAsync();
    }
    public Task<ExitDecision> ConfirmExitAsync() => Task.FromResult(ExitDecision.DiscardAndExit);
    public string? TrySaveNow() => _busy ? ProductStrings.Get("Settings.Saving") : null;
    public void DiscardDraft() { }
    public void RequestDismiss() => _dialog.Hide();
    public void Close()
    {
        _backgroundProbe?.Cancel();
        CancelInitialization();
        if (_busy) _closeRequested = true;
        else { _forceClose = true; _dialog.Hide(); }
    }

    public void RefreshStrings()
    {
        if (_initializationConfirmation is not null) return;
        if (!_busy)
        {
            var current = _readState();
            _state = _state with { Settings = current.Settings, TaskbarActivationError = current.TaskbarActivationError };
        }
        _refreshing = true;
        OwnedDialogWindow.SetPageName(_dialog, ProductStrings.Get("Settings.Title"));
        ActionIcon.SetIcon(_close, ProductStrings.Get("Common.Close"), ActionIcon.Cancel);
        foreach (var (_, item, labelKey) in _groups)
        {
            var label = (TextBlock)item.Content;
            label.Text = ProductStrings.Get(labelKey);
            AutomationProperties.SetName(item, label.Text);
        }
        string[] anchorKeys = ["Settings.BottomLeft", "Settings.BottomRight", "Settings.TopLeft", "Settings.TopRight"];
        for (var index = 0; index < anchorKeys.Length; index++)
        {
            var label = ProductStrings.Get(anchorKeys[index]);
            ActionIcon.SetIcon(_anchorButtons[index], label, ActionIcon.PresetMedium);
            _anchorButtons[index].Content = AnchorIcon(index);
            Id(_anchorButtons[index], "anchor-" + index);
        }
        Id(_fullScreen, "full-screen");
        SetChoices(_theme, "Settings.Theme", ["Settings.SystemLanguage", "Settings.Light", "Settings.Dark"]);
        SetChoices(_language, "Settings.Language", LanguageModes.Select(mode => mode == WallLanguageMode.System
            ? ProductStrings.Get("Settings.SystemLanguage") : WallLanguages.NativeName(mode)).ToArray(), localized: false);
        SetChoices(_visualStyle, "Settings.VisualStyle", ["Settings.Win10Style", "Settings.Win11Style"]);
        SetChoices(_background, "Settings.Background", ["Settings.Acrylic", "Settings.Mica", "Settings.Custom"]);
        foreach (var (key, label) in _labels) label.Text = ProductStrings.Get(key);
        foreach (var (button, key) in _linkButtons) ActionIcon.Set(button, ProductStrings.Get(key), ActionIcon.Link);
        foreach (var (toggle, key) in new[] { (_topmost, "Settings.Topmost"), (_stayVisible, "Settings.StayVisible"), (_layoutLocked, "Settings.LayoutLocked"),
            (_activateTaskbar, "Settings.ActivateTaskbar"),
            (_showGrid, "Settings.ShowGrid"), (_runAtLogin, "Settings.RunAtLogin"), (_fullScreen, "Settings.FullScreen"),
            (_expandColumns, "Settings.ExpandColumns"), (_menuStart, "Settings.MenuStart"),
            (_replaceStartMenu, "Settings.ReplaceStartMenu"),
            (_redirectTaskbarStartButton, "Settings.RedirectTaskbarStartButton"),
            (_contextMenu, "Settings.ContextMenuToggle"),
        })
        {
            toggle.OnContent = string.Empty; toggle.OffContent = string.Empty;
            var name = ProductStrings.Get(key);
            AutomationProperties.SetName(toggle, name); ToolTipService.SetToolTip(toggle, name);
        }
        ActionIcon.Set(_reset, ProductStrings.Get("Settings.Reset"), ActionIcon.Reset);
        ActionIcon.Set(_initialize, ProductStrings.Get("Settings.InitializeWall"), ActionIcon.Reset);
        ActionIcon.SetIcon(_resetHotKey, ProductStrings.Get("Settings.ResetHotKey"), ActionIcon.Reset);
        ActionIcon.SetIcon(_resetSecondaryHotKey, ProductStrings.Get("Settings.ResetSecondaryHotKey"), ActionIcon.Reset);
        foreach (var (key, control, helpKey) in new (string, FrameworkElement, string)[]
        {
            ("Settings.ReplaceStartMenu", _replaceStartMenu, "Settings.StartReplacementHelp"),
            ("Settings.RedirectTaskbarStartButton", _redirectTaskbarStartButton, "Settings.TaskbarStartReplacementHelp"),
            ("Settings.RunAtLogin", _runAtLogin, "Settings.RunAtLoginHelp"),
            ("Settings.Topmost", _topmost, "Settings.TopmostHelp"),
            ("Settings.StayVisible", _stayVisible, "Settings.StayVisibleHelp"),
            ("Settings.LayoutLocked", _layoutLocked, "Settings.LayoutLockedHelp"),
            ("Settings.ActivateTaskbar", _activateTaskbar, "Settings.ActivateTaskbarHelp"),
            ("Settings.ShowGrid", _showGrid, "Settings.ShowGridHint"),
            ("Settings.Language", _language, "Settings.LanguageFallback"),
            ("Settings.MenuStart", _menuStart, "Settings.MenuStartHelp"),
            ("Settings.FullScreen", _fullScreen, "Settings.FullScreenHelp"),
            ("Settings.ExpandColumns", _expandColumns, "Settings.ExpandColumnsHelp"),
            ("Settings.Initialization", _reset, "Settings.ResetHelp"),
            ("Settings.ExplorerMenu", _contextMenu, _contextMenuController.HelpKey),
        }) SetHelp(key, control, ProductStrings.Get(helpKey));
        var initializeHelp = ProductStrings.Get("Settings.InitializeWallHelp") + "\n" + ProductStrings.Get("Settings.RecycleRecoveryHint");
        ToolTipService.SetToolTip(_initialize, initializeHelp);
        AutomationProperties.SetHelpText(_initialize, initializeHelp);
        _retryStart.Content = ProductStrings.Get("Settings.StartReplacementRetry");
        _retryTaskbarStart.Content = ProductStrings.Get("Settings.StartReplacementRetry");
        _keyboardExperimental.Text = _taskbarExperimental.Text = ProductStrings.Get("Settings.ExperimentalFeature");
        _keyBehaviorKeyHeader.Text = ProductStrings.Get("Settings.KeyBehaviorKey");
        _keyBehaviorActionHeader.Text = ProductStrings.Get("Settings.KeyBehaviorAction");
        _confirmContextTransfer.Content = ProductStrings.Get("Settings.ContextMenuConfirmTransfer");
        _cancelContextTransfer.Content = ProductStrings.Get("Common.Cancel");
        AutomationProperties.SetName(_transparency, ProductStrings.Get("Settings.Transparency"));
        AutomationProperties.SetName(_transparencyInput, ProductStrings.Get("Settings.Transparency"));
        _version.Text = ProductVersion.Display;
        _refreshing = false;
        // The host can refresh strings before _apply has returned its committed state.
        if (!_busy) RestoreState();
    }

    private void RestoreState()
    {
        TraceSettings("RestoreBefore");
        _refreshing = true;
        string[] anchorKeys = ["Settings.BottomLeft", "Settings.BottomRight", "Settings.TopLeft", "Settings.TopRight"];
        for (var index = 0; index < _anchorButtons.Length; index++)
        {
            _anchorButtons[index].IsChecked = index == (int)_state.Settings.AnchorCorner;
            _anchorButtons[index].IsEnabled = !_state.Settings.LayoutLocked;
            ActionIcon.SetTip(_anchorButtons[index], ProductStrings.Get(anchorKeys[index]));
        }
        _fullScreen.IsOn = _state.Settings.FullScreen;
        _expandColumns.IsOn = _state.Settings.ExpandColumns;
        _expandColumns.IsEnabled = !_state.Settings.LayoutLocked;
        _fullScreen.IsEnabled = _menuStart.IsEnabled = !_state.Settings.LayoutLocked;
        _menuStart.IsOn = _state.Settings.MenuStart;
        Id(_expandColumns, "expand-columns"); Id(_menuStart, "menu-start");
        ToolTipService.SetToolTip(_labels["Settings.Anchor"], ProductStrings.Get("Settings.Anchor"));
        _language.SelectedIndex = Array.IndexOf(LanguageModes, _state.Settings.LanguageMode);
        _theme.SelectedIndex = (int)_state.Settings.ThemeMode; _visualStyle.SelectedIndex = (int)_state.Settings.VisualStyle;
        _background.SelectedIndex = (int)_state.Settings.BackgroundMode;
        _backgroundPath.Text = _state.Settings.BackgroundMode == WallBackgroundMode.Custom ? _state.Settings.BackgroundImagePath ?? string.Empty : string.Empty;
        ToolTipService.SetToolTip(_backgroundPath, _backgroundPath.Text);
        _transparency.Value = _state.Settings.BackgroundTransparency; _transparencyInput.Value = _state.Settings.BackgroundTransparency;
        _topmost.IsOn = _state.Settings.AlwaysOnTop; _stayVisible.IsOn = _state.Settings.StayVisibleOnFocusLoss;
        _layoutLocked.IsOn = _state.Settings.LayoutLocked;
        RefreshTaskbarActivationPresentation();
        _showGrid.IsOn = _state.Settings.ShowGrid; _runAtLogin.IsOn = _state.Settings.RunAtLogin;
        _hotKey.SetConfigured(_state.Settings.HotKey);
        _secondaryHotKey.SetConfigured(_state.Settings.SecondaryHotKey);
        RefreshContextMenuPresentation();
        RefreshStartReplacementPresentation();
        _refreshing = false;
        TraceSettings("RestoreAfter");
    }

    private void RefreshTaskbarActivationPresentation()
    {
        _activateTaskbar.IsOn = _state.Settings.ActivateTaskbar;
        var error = _state.TaskbarActivationError;
        _taskbarActivationStatus.Text = error ?? string.Empty;
        _taskbarActivationStatus.Visibility = string.IsNullOrWhiteSpace(error) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(_taskbarActivationStatus, error ?? string.Empty);
    }

    private void RefreshStartReplacementState()
    {
        if (!_contextPageOpen || _busy) return;
        var current = _readState();
        _state = _state with { Settings = current.Settings, RegisteredHotKey = current.RegisteredHotKey,
            RegisteredSecondaryHotKey = current.RegisteredSecondaryHotKey,
            HotKeyError = current.HotKeyError, SecondaryHotKeyError = current.SecondaryHotKeyError,
            StartReplacementActive = current.StartReplacementActive,
            StartReplacementSuspended = current.StartReplacementSuspended,
            StartReplacementError = current.StartReplacementError,
            TaskbarStartReplacementActive = current.TaskbarStartReplacementActive,
            TaskbarStartReplacementSuspended = current.TaskbarStartReplacementSuspended,
            TaskbarStartReplacementError = current.TaskbarStartReplacementError,
            TaskbarActivationError = current.TaskbarActivationError };
        RefreshStartReplacementPresentation();
    }

    private void RefreshStartReplacementPresentation()
    {
        var wasRefreshing = _refreshing;
        _refreshing = true;
        try
        {
            _replaceStartMenu.IsOn = _state.Settings.ReplaceStartMenu;
            var key = !_state.Settings.ReplaceStartMenu ? "Settings.StartReplacementOff"
                : _state.StartReplacementSuspended ? "Settings.StartReplacementSuspended"
                : _state.StartReplacementActive ? "Settings.StartReplacementActive"
                : "Settings.StartReplacementInactive";
            _startReplacementStatus.Text = ProductStrings.Get(key);
            if (_state.StartReplacementError is { } error)
                _startReplacementStatus.Text += "\n" + error;
            var showStatus = _state.StartReplacementError is not null || _state.Settings.ReplaceStartMenu &&
                (!_state.StartReplacementActive || _state.StartReplacementSuspended);
            _startReplacementStatus.Visibility = showStatus ? Visibility.Visible : Visibility.Collapsed;
            _retryStart.Visibility = _state.Settings.ReplaceStartMenu && !_state.StartReplacementActive &&
                !_state.StartReplacementSuspended && _retryStartReplacement is not null ? Visibility.Visible : Visibility.Collapsed;
            _retryStart.IsEnabled = !_busy && !HasPendingHotKeyTransition;
            _redirectTaskbarStartButton.IsOn = _state.Settings.RedirectTaskbarStartButton;
            var taskbarKey = !_state.Settings.RedirectTaskbarStartButton ? "Settings.StartReplacementOff"
                : _state.TaskbarStartReplacementSuspended ? "Settings.StartReplacementSuspended"
                : _state.TaskbarStartReplacementActive ? "Settings.StartReplacementActive"
                : "Settings.StartReplacementInactive";
            _taskbarStartReplacementStatus.Text = ProductStrings.Get(taskbarKey);
            if (_state.TaskbarStartReplacementError is { } taskbarError)
                _taskbarStartReplacementStatus.Text += "\n" + taskbarError;
            var showTaskbarStatus = _state.TaskbarStartReplacementError is not null || _state.Settings.RedirectTaskbarStartButton &&
                (!_state.TaskbarStartReplacementActive || _state.TaskbarStartReplacementSuspended);
            _taskbarStartReplacementStatus.Visibility = showTaskbarStatus ? Visibility.Visible : Visibility.Collapsed;
            _retryTaskbarStart.Visibility = _state.Settings.RedirectTaskbarStartButton && !_state.TaskbarStartReplacementActive &&
                !_state.TaskbarStartReplacementSuspended && _retryStartReplacement is not null ? Visibility.Visible : Visibility.Collapsed;
            _retryTaskbarStart.IsEnabled = !_busy && !HasPendingHotKeyTransition;
            RefreshHotKeyRegistrationPresentation();
            RefreshKeyBehaviorTable();
        }
        finally { _refreshing = wasRefreshing; }
    }

    private bool HasPendingHotKeyTransition =>
        _hotKey.HasPendingTransition || _secondaryHotKey.HasPendingTransition;

    private void CancelHotKeyRecordings()
    {
        _hotKey.CancelRecording();
        _secondaryHotKey.CancelRecording();
    }

    private async Task CancelAndWaitForHotKeysAsync()
    {
        await Task.WhenAll(_hotKey.CancelRecordingAsync(), _secondaryHotKey.CancelRecordingAsync());
        await WaitForHotKeysAsync();
    }

    private Task WaitForHotKeysAsync() => Task.WhenAll(
        _hotKey.WaitForIdleAsync(), _secondaryHotKey.WaitForIdleAsync());

    private void ShowHotKeyRecordingFailure(string error)
    {
        ShowFeedback(error);
    }

    private async Task ChangeHotKeyRecordingAsync(int slot, HotKeyRecorder recorder,
        HotKeyRecorder other, bool recording)
    {
        var hostChange = _hotKeyRecordingChanged;
        if (!recording)
        {
            if (hostChange is not null) await hostChange(slot, false);
            return;
        }

        try
        {
            if (hostChange is not null) await hostChange(slot, true);
        }
        catch
        {
            // A host may fail after partially accepting admission. Slot release is
            // idempotent, and the recorder's paired cleanup will also report false.
            if (hostChange is not null)
            {
                try { await hostChange(slot, false); }
                catch { }
            }
            throw;
        }

        try
        {
            // Focus can move again while host admission is in flight. Let the
            // newer recorder own the switch instead of cancelling its request.
            if (!recorder.IsRecordingRequested || !recorder.CanReceiveRecording) return;
            if (other.IsRecordingRequested && other.CanReceiveRecording) return;

            // Await this cancellation's fixed teardown boundary only: it owns
            // paired key-up, unhook and slot release. Waiting for the other's
            // moving idle tail can include a newer focus request that is itself
            // waiting for this admission, creating a cross-recorder wait cycle.
            await other.CancelRecordingAsync();
            if (!recorder.IsRecordingRequested || !recorder.CanReceiveRecording) return;
        }
        catch
        {
            if (hostChange is not null)
            {
                try { await hostChange(slot, false); }
                catch { }
            }
            throw;
        }
    }

    private void RefreshHotKeyRegistrationPresentation()
    {
        RefreshHotKeyRegistrationPresentation(_hotKey, _hotKeyDiagnostic, "Settings.HotKey",
            _state.RegisteredHotKey, _state.HotKeyError);
        RefreshHotKeyRegistrationPresentation(_secondaryHotKey, _secondaryHotKeyDiagnostic,
            "Settings.SecondaryHotKey", _state.RegisteredSecondaryHotKey, _state.SecondaryHotKeyError);
    }

    private void RefreshHotKeyRegistrationPresentation(HotKeyRecorder recorder,
        TextBlock diagnostic, string rowKey, string? registered, string? error)
    {
        var registration = registered is null
            ? ProductStrings.Get("Settings.NotRegistered")
            : string.Format(ProductStrings.Get("Settings.RegisteredHotKey"), DisplayHotKey(registered));
        var actualState = error is null ? registration : registration + "\n" + error;
        diagnostic.Text = error ?? string.Empty;
        diagnostic.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(diagnostic, error);
        AutomationProperties.SetHelpText(diagnostic, actualState);

        var help = ProductStrings.Get(_state.StartReplacementActive
            ? "Settings.HotKeyOpensSystemStart" : "Settings.HotKeyOpensTileWall") + "\n" +
            ProductStrings.Get("Settings.HotKeyHint") + "\n" + actualState;
        SetHelp(rowKey, recorder, help);
    }

    private static string DisplayHotKey(string? configured) =>
        HotKeyGesture.TryParse(configured, out var gesture) ? gesture!.ToDisplayString() : configured ?? string.Empty;

    private void RefreshKeyBehaviorTable()
    {
        _keyBehaviorKeys[0].Text = ProductStrings.Get("Settings.TaskbarWinKey");
        _keyBehaviorKeys[1].Text = ProductStrings.Get("Settings.KeyboardWinKey");
        _keyBehaviorKeys[2].Text = DisplayHotKey(_state.Settings.HotKey);
        _keyBehaviorKeys[3].Text = DisplayHotKey(_state.Settings.SecondaryHotKey);
        _keyBehaviorKeys[4].Text = "Ctrl+Esc";
        var systemStart = ProductStrings.Get("Settings.NativeStartMenu");
        _keyBehaviorActions[0].Text = _state.TaskbarStartReplacementActive ? "TileWall" : systemStart;
        _keyBehaviorActions[1].Text = _state.StartReplacementActive ? "TileWall" : systemStart;
        _keyBehaviorActions[2].Text = _keyBehaviorActions[3].Text = _state.StartReplacementActive ? systemStart : "TileWall";
        _keyBehaviorActions[4].Text = systemStart;
        for (var row = 0; row < _keyBehaviorKeys.Length; row++)
        {
            var registered = row == 2 ? _state.RegisteredHotKey : _state.RegisteredSecondaryHotKey;
            var error = row == 2 ? _state.HotKeyError : _state.SecondaryHotKeyError;
            var available = row is < 2 or 4 || registered is not null;
            _keyBehaviorKeys[row].Opacity = _keyBehaviorActions[row].Opacity = available ? 1 : .5;
            var help = row == 4 ? systemStart
                : row < 2 ? ProductStrings.Get(row == 0 ? "Settings.TaskbarStartReplacementHelp" : "Settings.StartReplacementHelp")
                : (registered is null ? ProductStrings.Get("Settings.NotRegistered")
                    : string.Format(ProductStrings.Get("Settings.RegisteredHotKey"), DisplayHotKey(registered)))
                    + (error is null ? string.Empty : "\n" + error);
            foreach (var cell in new[] { _keyBehaviorKeys[row], _keyBehaviorActions[row] })
            {
                ToolTipService.SetToolTip(cell, help);
                AutomationProperties.SetHelpText(cell, help);
            }
        }
    }

    private async Task RetryStartReplacementAsync(bool taskbar)
    {
        _busy = true; SetFormEnabled(false);
        var completed = false;
        try
        {
            await CancelAndWaitForHotKeysAsync();
            _state = await _retryStartReplacement!(taskbar);
            completed = _state.Error is null && (taskbar
                ? _state.TaskbarStartReplacementActive && _state.TaskbarStartReplacementError is null
                : _state.StartReplacementActive && _state.StartReplacementError is null);
        }
        catch (Exception ex)
        {
            var error = string.Format(ProductStrings.Get("Settings.StartReplacementFailed"), ex.Message);
            _state = taskbar ? _readState() with { TaskbarStartReplacementError = error }
                : _readState() with { StartReplacementError = error };
        }
        finally
        {
            _busy = false; SetFormEnabled(true); RestoreState(); FinishPending();
            if (completed) ShowSettingUpdated(taskbar ? "Settings.RedirectTaskbarStartButton" : "Settings.ReplaceStartMenu");
        }
    }

    private async Task FinishRecordingForCloseAsync()
    {
        _busy = true; SetFormEnabled(false);
        try { await CancelAndWaitForHotKeysAsync(); }
        catch (Exception ex) { _state = _readState() with { Error = ex.Message }; RestoreState(); }
        finally { _busy = false; SetFormEnabled(true); FinishPending(); }
    }

    private void SetTransparency(double value, bool apply)
    {
        if (_refreshing || _busy || _initialization is not null) return;
        var percent = (int)Math.Clamp(Math.Round(value), 0, 100);
        _refreshing = true; _transparency.Value = percent; _transparencyInput.Value = percent; _refreshing = false;
        if (apply) ApplyTransparency();
    }

    private void ApplyTransparency() => BeginApply(s => s with { BackgroundTransparency = (int)_transparency.Value });

    private async Task AwaitPendingAsync()
    {
        // An activation refresh may be queued by the operation just finishing.
        Task observed, contextObserved;
        do
        {
            observed = _pending; contextObserved = _contextPending;
            await Task.WhenAll(observed, contextObserved);
        }
        while (!ReferenceEquals(observed, _pending) || !ReferenceEquals(contextObserved, _contextPending));
    }

    private void OnOwnerActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_contextPageOpen && args.WindowActivationState != WindowActivationState.Deactivated)
            BeginContextMenuRefresh();
    }

    private void BeginContextMenuRefresh()
    {
        if (!_contextPageOpen) return;
        if (_busy || _contextRefreshing) { _contextRefreshRequested = true; return; }
        _contextRefreshRequested = false;
        _contextPending = RefreshContextMenuAsync();
    }

    private async Task RefreshContextMenuAsync()
    {
        // Activation precedes the click that brought this window forward. A
        // read-only refresh must not disable the form and cancel that click.
        _contextRefreshing = true;
        RefreshContextMenuPresentation();
        var observedRevision = _contextMutationRevision;
        try
        {
            var snapshot = await _contextMenuController.ReadAsync();
            if (!_contextPageOpen) return;
            // A menu write may have started and finished while this read was running.
            if (_busy || observedRevision != _contextMutationRevision)
            {
                _contextRefreshRequested = true;
                return;
            }
            _contextState = snapshot;
            if (!snapshot.ReadSucceeded)
            {
                _contextOutcomeKey = "Settings.ContextMenuReadFailed";
                _contextOutcomeArgument = snapshot.Error;
            }
            else if (_contextOutcomeKey == "Settings.ContextMenuReadFailed")
            {
                _contextOutcomeKey = null; _contextOutcomeArgument = null;
            }
            // Keep transfer confirmation controls stable through activation clicks.
            // Confirmation performs its own fresh ownership check before writing.
        }
        finally
        {
            _contextRefreshing = false;
            if (_contextPageOpen)
            {
                RefreshContextMenuPresentation();
                QueueContextMenuRefresh();
            }
        }
    }

    private void BeginContextMenuChange(bool enable, string[]? confirmedTargets = null)
    {
        if (_refreshing || _busy || !_contextPageOpen) return;
        _contextMutationRevision++;
        CancelHotKeyRecordings();
        _contextTransferTargets = null;
        _contextOutcomeKey = null; _contextOutcomeArgument = null;
        _pending = ChangeContextMenuAsync(enable, confirmedTargets);
    }

    private async Task ChangeContextMenuAsync(bool enable, string[]? confirmedTargets)
    {
        _busy = true; SetFormEnabled(false);
        var failed = false;
        var completed = false;
        try
        {
            await CancelAndWaitForHotKeysAsync();
            RefreshContextMenuPresentation();
            var language = WallLanguages.ResolveTag(ProductStrings.Language);
            var result = await _contextMenuController.ApplyAsync(enable, language, confirmedTargets);
            _contextState = result.State;
            _contextRefreshRequested = false;
            _contextTransferTargets = result.TransferTargets;
            _contextOutcomeKey = result.OutcomeKey;
            _contextOutcomeArgument = result.Detail;
            failed = result.Failed;
            completed = !failed && result.TransferTargets is null && result.State.ReadSucceeded &&
                (enable ? result.State.Complete : !result.State.Enabled);
        }
        catch (Exception ex)
        {
            failed = true;
            _contextOutcomeKey = "Settings.ContextMenuOperationFailed";
            _contextOutcomeArgument = ex.Message;
            _contextState = await _contextMenuController.ReadAsync();
        }
        finally
        {
            _busy = false;
            if (_contextPageOpen)
            {
                SetFormEnabled(true);
                RefreshContextMenuPresentation();
                if (failed && _contextOutcomeKey is not null)
                    AppLog.Warn("ExplorerMenu", $"operation={(enable ? "enable" : "disable")} outcome={_contextOutcomeKey} detail={_contextOutcomeArgument}");
                FinishPending(failed);
                if (completed) ShowSettingUpdated("Settings.ExplorerMenu");
            }
        }
    }

    private void RefreshContextMenuPresentation()
    {
        var wasRefreshing = _refreshing;
        _refreshing = true;
        try
        {
            // A mixed state can still contain this copy's entries; On permits their exact removal.
            _contextMenu.IsOn = _contextState?.Enabled == true;
            _contextMenu.IsEnabled = !_busy && _contextState?.ReadSucceeded == true;
            _confirmContextTransfer.IsEnabled = _cancelContextTransfer.IsEnabled = !_busy;
            _contextMenuConfirmation.Visibility = _contextTransferTargets is null ? Visibility.Collapsed : Visibility.Visible;
            if (_contextTransferTargets is not null)
                _contextMenuTransfer.Text = string.Format(ProductStrings.Get("Settings.ContextMenuTransfer"),
                    string.Join("\n", _contextTransferTargets), Environment.ProcessPath);
            RefreshStatus();
        }
        finally { _refreshing = wasRefreshing; }
    }

    private void BeginChoiceApply(ComboBox box, Func<WallSettings, WallSettings> change, bool pickImage = false)
    {
        if (_refreshing || _busy || _initialization is not null) return;
        var candidate = change(_state.Settings);
        if (!pickImage && candidate == _state.Settings) return;
        // Publish cancellation ownership before the queued popup unwind. Exit
        // may start waiting before the picker/decoder has actually begun.
        var probe = pickImage ? _backgroundProbe = new CancellationTokenSource() : null;
        _pending = ApplyChoiceAsync(box, candidate, probe);
    }

    private async Task ApplyChoiceAsync(ComboBox box, WallSettings candidate, CancellationTokenSource? probe)
    {
        _busy = true;
        try
        {
            // Theme changes and localized item updates must run after the ComboBox has
            // finished its popup/selection event. Do not disable the open control first.
            if (box.IsDropDownOpen)
            {
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnClosed(object? sender, object args) => closed.TrySetResult();
                box.DropDownClosed += OnClosed;
                try { box.IsDropDownOpen = false; await closed.Task; }
                finally { box.DropDownClosed -= OnClosed; }
            }
            var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!box.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => dispatched.TrySetResult()))
                throw new InvalidOperationException(ProductStrings.Get("Wall.SettingsBlocked"));
            await dispatched.Task;
            if (probe is not null) await PickBackgroundAsync(probe.Token);
            else await ApplyAsync(candidate);
        }
        catch (Exception ex)
        {
            _state = _readState() with { Error = string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message) };
            _busy = false; SetFormEnabled(true); RestoreState(); FinishPending();
        }
        finally
        {
            if (ReferenceEquals(_backgroundProbe, probe)) _backgroundProbe = null;
            probe?.Dispose();
        }
    }

    private async Task PickBackgroundAsync(CancellationToken token)
    {
        _busy = true; SetFormEnabled(false);
        string? path = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var owner = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(_owner));
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(owner);
            TargetItemPicker.SetSuggestedFolder(picker, _state.Settings.BackgroundImagePath);
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".bmp" }) picker.FileTypeFilter.Add(extension);
            var file = await picker.PickSingleFileAsync();
            // The system picker itself keeps its native lifetime. A close/exit
            // request only cancels this uncommitted candidate probe.
            token.ThrowIfCancellationRequested();
            if (file is not null)
            {
                var decoded = await new ImageFrameDecoder().DecodeAsync(file.Path, 512, 512, token);
                token.ThrowIfCancellationRequested();
                if (decoded.Status != ImageDecodeStatus.Cancelled)
                {
                    if (decoded.Status != ImageDecodeStatus.Success) throw new InvalidDataException();
                    path = file.Path;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { _state = _readState() with { Error = ProductStrings.Get("Settings.BackgroundFailed") }; }
        finally { _busy = false; SetFormEnabled(true); RestoreState(); }
        if (path is not null) await ApplyAsync(_state.Settings with { BackgroundMode = WallBackgroundMode.Custom, BackgroundImagePath = path, BackgroundMedia = null });
        else FinishPending();
    }

    private void BeginInitializeWall()
    {
        if (_refreshing || _busy || !_contextPageOpen) return;
        // A NumberBox may start an ordinary settings apply when its text settles.
        // Let that operation finish before offering a destructive confirmation.
        OwnedDialogWindow.ReleaseTextFocus(_dialog);
        if (_busy || !_pending.IsCompleted) return;
        var cancellation = new CancellationTokenSource();
        _initialization = cancellation;
        _busy = true; SetFormEnabled(false);
        _pending = InitializeWallCoreAsync(cancellation);
    }

    private void CancelInitialization()
    {
        _initialization?.Cancel();
        _initializationConfirmation?.TrySetResult(false);
    }

    private async Task InitializeWallCoreAsync(CancellationTokenSource cancellation)
    {
        var completed = false;
        try
        {
            await CancelAndWaitForHotKeysAsync();
            await _contextPending;
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_contextPageOpen || _closeRequested) return;
            var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _initializationConfirmation = confirmation;
            try
            {
                _dialog.Title = ProductStrings.Get("Settings.InitializeWall");
                _dialog.Content = new TextBlock
                {
                    Text = ProductStrings.Get("Settings.InitializeWallWarning"), TextWrapping = TextWrapping.Wrap,
                    Width = Math.Min(460, _settingsBody.Width),
                };
                _dialog.PrimaryButtonText = ProductStrings.Get("Settings.InitializeWall");
                _dialog.CloseButtonText = ProductStrings.Get("Common.Cancel");
                _dialog.DefaultButton = ContentDialogButton.Close;
                ActionIcon.DecorateDialog(_dialog, ActionIcon.Reset, ActionIcon.Delete, ActionIcon.Cancel);
                // Refreshing DefaultButton updates Enter behavior; move actual
                // focus off the destructive command as well.
                _dialog.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!ReferenceEquals(_initializationConfirmation, confirmation)) return;
                    FocusInitializationCancel(_dialog);
                });
                if (!await confirmation.Task) return;
            }
            finally
            {
                _initializationConfirmation = null;
                _dialog.Content = _settingsBody;
                _dialog.PrimaryButtonText = string.Empty;
                _dialog.DefaultButton = ContentDialogButton.None;
                OwnedDialogWindow.SetPageName(_dialog, ProductStrings.Get("Settings.Title"));
                _dialog.CloseButtonText = string.Empty;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_contextPageOpen || _closeRequested) return;
            _progressKey = "Settings.InitializingWall";
            RefreshStatus();
            var error = await _initializeWall(cancellation.Token);
            _state = _readState() with { Error = error };
            completed = error is null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _state = _readState() with { Error = string.Format(ProductStrings.Get("Welcome.Failed"), ex.Message) };
        }
        finally
        {
            // Reattaching/enabling the form can raise control events. Restore
            // committed values before releasing initialization's input gate.
            try
            {
                _progressKey = null;
                RestoreState();
                SetFormEnabled(true);
            }
            finally
            {
                _busy = false;
                if (ReferenceEquals(_initialization, cancellation)) _initialization = null;
                cancellation.Dispose();
            }
            if (completed) ShowFeedback(ProductStrings.Get("Settings.InitializedWall"));
            FinishPending();
        }
    }

    private static bool FocusInitializationCancel(DependencyObject parent)
    {
        if (parent is Button { Name: "CloseButton" } cancel) return cancel.Focus(FocusState.Programmatic);
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FocusInitializationCancel(VisualTreeHelper.GetChild(parent, index))) return true;
        return false;
    }

    private void BeginApply(Func<WallSettings, WallSettings> change)
    {
        TraceSettings("BeginApply");
        if (_refreshing || _busy || _initialization is not null) { TraceSettings("IgnoredBusyOrRefresh"); return; }
        var candidate = change(_state.Settings);
        if (candidate == _state.Settings) { TraceSettings("IgnoredUnchanged"); return; }
        TraceSettings("Candidate", SettingsFlags(candidate));
        _pending = ApplyAsync(candidate);
    }

    private async Task ApplyAsync(WallSettings candidate, bool restoreDefaults = false)
    {
        TraceSettings("ApplyStart", SettingsFlags(candidate));
        var settingKey = ChangedSettingKey(_state.Settings, candidate);
        var completed = false;
        var restoreAnchorFocus = _anchor.FocusState != FocusState.Unfocused;
        _busy = true; SetFormEnabled(false);
        _progressKey = "Settings.Saving";
        RefreshStatus();
        try
        {
            await CancelAndWaitForHotKeysAsync();
            _state = await _apply(candidate, restoreDefaults);
            completed = _state.Error is null;
            TraceSettings("ApplyReturned", _state.Error ?? "success");
        }
        catch (Exception ex)
        {
            _state = _readState() with { Error = string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message) };
            TraceSettings("ApplyFailed", _state.Error);
        }
        finally
        {
            _progressKey = null;
            _busy = false;
            SetFormEnabled(true);
            RestoreState();
            if (restoreAnchorFocus) _anchor.Focus(FocusState.Programmatic);
            FinishPending();
            if (completed)
            {
                if (restoreDefaults) ShowFeedback(ProductStrings.Get("Settings.DefaultsRestored"));
                else ShowSettingUpdated(settingKey);
            }
        }
    }

    private static string ChangedSettingKey(WallSettings before, WallSettings after)
    {
        if (before.AnchorCorner != after.AnchorCorner) return "Settings.Anchor";
        if (before.FullScreen != after.FullScreen) return "Settings.FullScreen";
        if (before.ExpandColumns != after.ExpandColumns) return "Settings.ExpandColumns";
        if (before.MenuStart != after.MenuStart) return "Settings.MenuStart";
        if (before.AlwaysOnTop != after.AlwaysOnTop) return "Settings.Topmost";
        if (before.StayVisibleOnFocusLoss != after.StayVisibleOnFocusLoss) return "Settings.StayVisible";
        if (before.LayoutLocked != after.LayoutLocked) return "Settings.LayoutLocked";
        if (before.ActivateTaskbar != after.ActivateTaskbar) return "Settings.ActivateTaskbar";
        if (before.ShowGrid != after.ShowGrid) return "Settings.ShowGrid";
        if (before.RunAtLogin != after.RunAtLogin) return "Settings.RunAtLogin";
        if (before.ReplaceStartMenu != after.ReplaceStartMenu) return "Settings.ReplaceStartMenu";
        if (before.RedirectTaskbarStartButton != after.RedirectTaskbarStartButton) return "Settings.RedirectTaskbarStartButton";
        if (before.HotKey != after.HotKey) return "Settings.HotKey";
        if (before.SecondaryHotKey != after.SecondaryHotKey) return "Settings.SecondaryHotKey";
        if (before.LanguageMode != after.LanguageMode) return "Settings.Language";
        if (before.ThemeMode != after.ThemeMode) return "Settings.Theme";
        if (before.VisualStyle != after.VisualStyle) return "Settings.VisualStyle";
        if (before.BackgroundMode != after.BackgroundMode || before.BackgroundImagePath != after.BackgroundImagePath)
            return "Settings.Background";
        if (before.BackgroundTransparency != after.BackgroundTransparency) return "Settings.Transparency";
        return "Settings.Title";
    }

    private void ShowSettingUpdated(string key) =>
        ShowFeedback(ProductStrings.SettingUpdatedMessage(key, _state.Settings));

    private static string SettingsFlags(WallSettings settings) =>
        $"grid={settings.ShowGrid} login={settings.RunAtLogin} topmost={settings.AlwaysOnTop}";

    private void TraceSettings(string phase, string? detail = null)
    {
        if (!_traceInput) return;
        AppLog.Warn("SettingsInput", $"sequence={_inputSequence} phase={phase} busy={_busy} refreshing={_refreshing} " +
            $"ui=({_showGrid.IsOn},{_runAtLogin.IsOn},{_topmost.IsOn}) model=({SettingsFlags(_state.Settings)}) {detail}");
    }

    private static string PointerDetail(FrameworkElement toggle, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(toggle);
        return $"{AutomationProperties.GetAutomationId(toggle)} point={point.Position.X:F2},{point.Position.Y:F2} " +
            $"left={point.Properties.IsLeftButtonPressed} handled={args.Handled} source={args.OriginalSource?.GetType().Name}";
    }

    private static void DisableDoubleTap(DependencyObject node)
    {
        if (node is UIElement element) element.IsDoubleTapEnabled = false;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            DisableDoubleTap(VisualTreeHelper.GetChild(node, index));
    }

    private void TraceThumbs(DependencyObject node, ToggleSwitch toggle)
    {
        if (node is Thumb thumb && _tracedThumbs.Add(thumb))
        {
            var name = AutomationProperties.GetAutomationId(toggle);
            thumb.DragStarted += (_, _) => TraceSettings("DragStarted", name);
            thumb.DragDelta += (_, e) => TraceSettings("DragDelta", $"{name} delta={e.HorizontalChange:F2},{e.VerticalChange:F2}");
            thumb.DragCompleted += (_, e) => TraceSettings("DragCompleted", $"{name} canceled={e.Canceled} delta={e.HorizontalChange:F2},{e.VerticalChange:F2}");
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            TraceThumbs(VisualTreeHelper.GetChild(node, index), toggle);
    }

    private void FinishPending(bool contextOperationFailed = false)
    {
        _showFailureBeforeClose = _state.Error is not null || contextOperationFailed;
        var closeAfterSuccess = _closeRequested && _state.Error is null && !contextOperationFailed;
        _closeRequested = false;
        if (closeAfterSuccess) _dialog.Hide();
        else QueueContextMenuRefresh();
    }

    private void QueueContextMenuRefresh()
    {
        if (_contextRefreshRequested && _contextPageOpen && !_busy && !_contextRefreshing)
        {
            // Apply may complete synchronously before its caller assigns _pending. Start the
            // follow-up on the next UI turn so that assignment cannot hide this new operation.
            _dialog.DispatcherQueue.TryEnqueue(() =>
            {
                if (_contextRefreshRequested && _contextPageOpen && !_busy && !_contextRefreshing) BeginContextMenuRefresh();
            });
        }
    }

    private Grid AddGroup(string labelKey, string id, bool compact)
    {
        var group = new Grid { RowSpacing = compact ? 8 : 12, ColumnSpacing = 16, VerticalAlignment = VerticalAlignment.Top };
        if (!compact)
        {
            group.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
            group.ColumnDefinitions.Add(new ColumnDefinition());
        }
        var label = (TextBlock)XamlReader.Load("""
            <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                       Foreground="{ThemeResource TextFillColorPrimaryBrush}"
                       TextWrapping="Wrap" VerticalAlignment="Center" />
            """);
        var item = new ListViewItem { Content = label };
        Id(item, "group-" + id);
        item.SizeChanged += (_, _) => UpdateGroupHighlight();
        var card = VisualStyles.Card(group);
        _groups.Add((card, item, labelKey));
        _form.Children.Add(card);
        _groupNavigation.Items.Add(item);
        return group;
    }

    private void NavigateToGroup(int index)
    {
        if (!_groupNavigation.IsLoaded || _busy || !_formHost.IsEnabled || index < 0 || index >= _groups.Count) return;
        _formHost.ChangeView(null, GroupTop(index), null, disableAnimation: false);
    }

    private double GroupTop(int index) => _groups[index].Card.TransformToVisual(_form).TransformPoint(default).Y;

    private void UpdateGroupHighlight()
    {
        if (!_form.IsLoaded || !_groupNavigation.IsLoaded || _groups.Count == 0) return;
        var offset = _formHost.VerticalOffset;
        var first = 0;
        var last = _groups.Count - 1;
        while (first < last && GroupTop(first + 1) <= offset + .5) first++;
        var second = Math.Min(first + 1, last);
        var firstItem = _groups[first].Item;
        var secondItem = _groups[second].Item;
        if (firstItem.ActualHeight <= 0 || secondItem.ActualHeight <= 0) return;

        var firstOffset = GroupTop(first);
        var distance = GroupTop(second) - firstOffset;
        var progress = distance > 0 ? Math.Clamp((offset - firstOffset) / distance, 0, 1) : 0;
        var firstY = firstItem.TransformToVisual(_groupNavigation).TransformPoint(default).Y;
        var secondY = secondItem.TransformToVisual(_groupNavigation).TransformPoint(default).Y;
        _groupHighlightOffset.Y = firstY + (secondY - firstY) * progress;
        _groupHighlight.Height = firstItem.ActualHeight + (secondItem.ActualHeight - firstItem.ActualHeight) * progress;
        _groupHighlight.Visibility = Visibility.Visible;

        var index = progress < .5 ? first : second;
        if (_groupNavigation.SelectedIndex == index) return;
        _syncingGroupSelection = true;
        try { _groupNavigation.SelectedIndex = index; }
        finally { _syncingGroupSelection = false; }
    }

    private void AddRow(Grid group, string key, FrameworkElement value)
    {
        var row = group.RowDefinitions.Count;
        group.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _labels.Add(key, label); AutomationProperties.SetLabeledBy(value, label);
        Grid.SetRow(label, row); group.Children.Add(label);
        if (group.ColumnDefinitions.Count == 0)
        {
            group.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row++;
        }
        else Grid.SetColumn(value, 1);
        Grid.SetRow(value, row); group.Children.Add(value);
    }

    private static StackPanel HotKeyRow(Button reset, HotKeyRecorder recorder, TextBlock diagnostic, bool compact)
    {
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(reset);
        controls.Children.Add(recorder);
        if (!compact)
        {
            controls.Children.Add(diagnostic);
            return controls;
        }
        var row = new StackPanel { Spacing = 8 };
        row.Children.Add(controls);
        row.Children.Add(diagnostic);
        return row;
    }

    private static Grid ExperimentalToggleRow(ToggleSwitch toggle, TextBlock explanation)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toggle.MinWidth = 0;
        toggle.Width = 52;
        toggle.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(toggle);
        Grid.SetColumn(explanation, 1); row.Children.Add(explanation);
        return row;
    }

    private Border KeyBehaviorTable()
    {
        var grid = new Grid();
        Id(grid, "key-behavior-table");
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        for (var row = 0; row <= _keyBehaviorKeys.Length; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (row == 0)
            {
                var header = (Border)XamlReader.Load("""
                    <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        Background="{ThemeResource LayerFillColorDefaultBrush}" CornerRadius="6" />
                    """);
                Grid.SetColumnSpan(header, 2); grid.Children.Add(header);
            }
            else if (row < _keyBehaviorKeys.Length)
            {
                var divider = VisualStyles.InputFrame(transparent: true);
                divider.BorderThickness = new Thickness(0, 0, 0, 1);
                divider.Opacity = .25;
                divider.IsHitTestVisible = false;
                Grid.SetRow(divider, row); Grid.SetColumnSpan(divider, 2); grid.Children.Add(divider);
            }
            for (var column = 0; column < 2; column++)
            {
                var text = row == 0 ? (column == 0 ? _keyBehaviorKeyHeader : _keyBehaviorActionHeader)
                    : (column == 0 ? _keyBehaviorKeys[row - 1] : _keyBehaviorActions[row - 1]);
                text.VerticalAlignment = VerticalAlignment.Center;
                if (row == 0)
                {
                    text.FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 };
                    text.FontSize = 12;
                    text.Opacity = .75;
                }
                Id(text, row == 0 ? "key-behavior-header-" + column : "key-behavior-" + (row - 1) + "-" + column);
                var cell = new Border { Padding = new Thickness(12, row == 0 ? 8 : 10, 12, row == 0 ? 8 : 10) };
                if (row > 0 && column == 1)
                {
                    var action = (Border)XamlReader.Load("""
                        <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            Background="{ThemeResource LayerFillColorDefaultBrush}" CornerRadius="4"
                            Padding="8,3" HorizontalAlignment="Left" VerticalAlignment="Center" />
                        """);
                    action.Child = text;
                    cell.Child = action;
                }
                else cell.Child = text;
                Grid.SetRow(cell, row); Grid.SetColumn(cell, column); grid.Children.Add(cell);
            }
        }
        return new Border { Child = grid };
    }

    private void SetFormEnabled(bool enabled)
    {
        _formHost.IsEnabled = enabled;
        _groupNavigation.IsEnabled = enabled;
    }

    private void ConfigureActionFeedback(FrameworkElement root)
    {
        DialogActionFeedback.Attach(root, ShowFeedback, SettingsBlockReason);
        DialogActionFeedback.SetReason(_formHost, SettingsBlockReason);
        DialogActionFeedback.SetReason(_groupNavigation, SettingsBlockReason);
        foreach (var control in _anchorButtons.Cast<FrameworkElement>().Concat([_expandColumns, _fullScreen, _menuStart]))
            DialogActionFeedback.SetReason(control, () => SettingsBlockReason() ??
                (_state.Settings.LayoutLocked ? ProductStrings.Get("Settings.LayoutLockReason") : null));
        foreach (var control in new FrameworkElement[] { _reset, _initialize, _resetHotKey, _resetSecondaryHotKey,
            _confirmContextTransfer, _cancelContextTransfer })
            DialogActionFeedback.SetReason(control, SettingsBlockReason);
        foreach (var control in new FrameworkElement[] { _retryStart, _retryTaskbarStart })
            DialogActionFeedback.SetReason(control, () => SettingsBlockReason() ??
                (_retryStartReplacement is null ? ProductStrings.Get("Settings.StartReplacementUnavailable") : null));
        DialogActionFeedback.SetReason(_contextMenu, () => SettingsBlockReason() ??
            (_contextState?.ReadSucceeded != true ? ProductStrings.Get("Settings.ContextMenuReadFailed") : null));
    }

    private string? SettingsBlockReason()
    {
        if (_initialization is not null) return ProductStrings.Get("Settings.InitializingWall");
        if (_busy || !_pending.IsCompleted) return ProductStrings.Get("Settings.ControlsBusy");
        if (HasPendingHotKeyTransition) return ProductStrings.Get("Settings.RecordingInProgress");
        return null;
    }

    private void RefreshStatus()
    {
        var message = _progressKey is { } progress ? ProductStrings.Get(progress) : string.Join("\n", new[]
        {
            _state.Error is { } error ? string.Format(ProductStrings.Get("Settings.ApplyFailed"), error) : null,
            _contextOutcomeKey is { } outcome ? ProductStrings.Get(outcome) : null,
        }.Where(value => !string.IsNullOrEmpty(value)));
        if (message.Length != 0)
        {
            if (!_persistentStatusVisible || _status.Text != message) _status.Show(message);
        }
        else if (_persistentStatusVisible) _status.Clear();
        _persistentStatusVisible = message.Length != 0;
    }

    private void ShowFeedback(string message)
    {
        _status.Show(message, transient: true, renew: true);
    }

    private void SetHelp(string rowKey, FrameworkElement control, string text)
    {
        ToolTipService.SetToolTip(_labels[rowKey], text);
        AutomationProperties.SetHelpText(control, text);
        if (control is ButtonBase button) ActionIcon.SetTip(button, text);
        else ToolTipService.SetToolTip(control, text);
    }

    private Button Link(string labelKey, string url, string failureMessageKey = "Settings.OpenLinkFailed")
    {
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Left };
        _linkButtons.Add((button, labelKey));
        ActionIcon.Set(button, ProductStrings.Get(labelKey), ActionIcon.Link);
        button.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                ShowFeedback(string.Format(ProductStrings.Get("Common.Opened"), ProductStrings.Get(labelKey)));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or
                UnauthorizedAccessException or System.Runtime.InteropServices.COMException or
                ArgumentException or InvalidOperationException or System.Security.SecurityException)
            {
                ShowLinkOpenFailure(failureMessageKey, ex.Message);
            }
        };
        return button;
    }

    private void ShowLinkOpenFailure(string messageKey, string detail)
    {
        ShowFeedback(string.Format(ProductStrings.Get(messageKey), detail));
    }
    private static void SetChoices(ComboBox box, string header, string[] keys, bool localized = true)
    {
        box.Header = null; AutomationProperties.SetName(box, ProductStrings.Get(header));
        var selected = box.SelectedIndex;
        var refreshSelection = false;
        for (var index = 0; index < keys.Length; index++)
        {
            var text = localized ? ProductStrings.Get(keys[index]) : keys[index];
            if (index == box.Items.Count) box.Items.Add(new ComboBoxItem { Content = text });
            else if (box.Items[index] is ComboBoxItem item && !Equals(item.Content, text))
            {
                item.Content = text;
                refreshSelection |= index == selected;
            }
        }
        // ComboBox caches the selected presentation separately from its item Content.
        // RefreshStrings suppresses apply events while refreshing this existing container.
        if (refreshSelection) { box.SelectedIndex = -1; box.SelectedIndex = selected; }
    }
    private static UIElement AnchorIcon(int index)
    {
        return new FontIcon
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"),
            Glyph = ActionIcon.PresetMedium, FontSize = 16, RenderTransformOrigin = new Windows.Foundation.Point(.5, .5),
            RenderTransform = new Microsoft.UI.Xaml.Media.RotateTransform { Angle = index switch { 0 => 0, 1 => 270, 2 => 90, _ => 180 } },
        };
    }
    private static void Id(DependencyObject item, string id) => AutomationProperties.SetAutomationId(item, "settings-" + id);
}



