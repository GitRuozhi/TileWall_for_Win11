using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
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
    bool StartReplacementSuspended = false, string? StartReplacementError = null);

/// <summary>Immediate settings edits. The host owns persistence and operating-system rollback.</summary>
internal sealed class SettingsDialog : IWallDialogSession
{
    private const string CommunityGroupUrl =
        "https://qm.qq.com/cgi-bin/qm/qr?k=iw0LxnFaHE8JdUr5z937pFuagFxOtFOo&jump_from=webapi&authKey=JvkcK/IIaFg5e1ymzhP41yxcAiTVURjvhrNtDziZZSGj3ZD2byZhqX2lj48L9jkT";
    private readonly Window _owner;
    private readonly Func<SettingsDialogState> _readState;
    private readonly Func<WallSettings, bool, Task<SettingsDialogState>> _apply;
    private readonly Func<Task<SettingsDialogState>>? _retryStartReplacement;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _startReplacementWatch;
    private readonly ContentDialog _dialog;
    private readonly Grid _form = new() { RowSpacing = 12, ColumnSpacing = 16 };
    private readonly Dictionary<string, TextBlock> _labels = [];
    private readonly ScrollViewer _formHost = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _anchor = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ToggleButton[] _anchorButtons = Enumerable.Range(0, 4).Select(_ => new ToggleButton { Width = 32, Height = 32 }).ToArray();
    private readonly ToggleSwitch _fullScreen = new();
    private readonly ToggleSwitch _expandColumns = new(), _menuStart = new();
    private readonly ComboBox _language = new(), _visualStyle = new(), _theme = new(), _background = new();
    private readonly ToggleSwitch _topmost = new(), _stayVisible = new(), _runAtLogin = new(), _showGrid = new();
    private readonly HotKeyRecorder _hotKey = new();
    private readonly Button _resetHotKey = new();
    private readonly ToggleSwitch _replaceStartMenu = new();
    private readonly TextBlock _startReplacementStatus = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Button _retryStart = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Slider _transparency = new() { Minimum = 0, Maximum = 100, StepFrequency = 1, Width = 180, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly NumberBox _transparencyInput = new() { Minimum = 0, Maximum = 100, SmallChange = 1, Width = 56, MinWidth = 0, Padding = new Thickness(8, 5, 8, 5), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
    private readonly TextBlock _backgroundPath = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _reset = new();
    private readonly ToggleSwitch _contextMenu = new();
    private readonly TextBlock _contextMenuTransfer = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _contextMenuConfirmation = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly Button _confirmContextTransfer = new(), _cancelContextTransfer = new();
    private readonly List<(Button Button, string LabelKey)> _linkButtons = [];
    private readonly TextBlock _version = new(), _status = new() { TextWrapping = TextWrapping.Wrap };
    private SettingsDialogState _state;
    private Task _pending = Task.CompletedTask;
    private Task _contextPending = Task.CompletedTask;
    private CancellationTokenSource? _backgroundProbe;
    private bool _refreshing, _busy, _closeRequested, _forceClose, _showFailureBeforeClose;
    private bool _contextPageOpen, _contextRefreshing, _contextRefreshRequested;
    private long _contextMutationRevision;
    private string? _contextOutcomeKey, _contextOutcomeArgument;
    private string[]? _contextTransferTargets;
    private MachineContextMenuState? _contextMachineState;
    private readonly bool _traceInput = Environment.GetEnvironmentVariable("TILEWALL_TRACE_SETTINGS") == "1" ||
        Environment.GetEnvironmentVariable("TILEWALL_TRACE_INPUT") == "1";
    private readonly HashSet<Thumb> _tracedThumbs = [];
    private int _inputSequence;

    public SettingsDialog(Window owner, Func<SettingsDialogState> readState,
        Func<WallSettings, bool, Task<SettingsDialogState>> applyAsync, Func<bool, Task>? hotKeyRecordingChanged = null,
        Func<Task<SettingsDialogState>>? retryStartReplacement = null)
    {
        _owner = owner; _readState = readState; _apply = applyAsync; _state = readState();
        _retryStartReplacement = retryStartReplacement;
        _startReplacementWatch = _form.DispatcherQueue.CreateTimer();
        _startReplacementWatch.Interval = TimeSpan.FromMilliseconds(500);
        _startReplacementWatch.IsRepeating = true;
        _startReplacementWatch.Tick += (_, _) => RefreshStartReplacementState();
        _form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        _form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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
        _hotKey.RecordingChanged += recording => hotKeyRecordingChanged?.Invoke(recording) ?? Task.CompletedTask;
        _hotKey.GestureRecorded += gesture => BeginApply(s => s with { HotKey = gesture.ToConfigString() });
        _hotKey.RecordingFailed += error =>
        {
            _status.Text = error; _status.Visibility = Visibility.Visible;
        };
        AddRow("Settings.Theme", _theme);
        AddRow("Settings.VisualStyle", _visualStyle);
        AddRow("Settings.Language", _language);
        var backgroundRow = new Grid { ColumnSpacing = 12 };
        backgroundRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        backgroundRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        backgroundRow.Children.Add(_background); Grid.SetColumn(_backgroundPath, 1); backgroundRow.Children.Add(_backgroundPath);
        AddRow("Settings.Background", backgroundRow);
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
        Grid.SetColumn(_transparency, 2); transparencyRow.Children.Add(_transparency); AddRow("Settings.Transparency", transparencyRow);
        AddRow("Settings.ShowGrid", _showGrid);
        AddSeparator();
        var startReplacementRow = new StackPanel { Spacing = 8 };
        startReplacementRow.Children.Add(_replaceStartMenu);
        startReplacementRow.Children.Add(_startReplacementStatus);
        startReplacementRow.Children.Add(_retryStart);
        AddRow("Settings.ReplaceStartMenu", startReplacementRow);
        var hotKeyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        hotKeyRow.Children.Add(_resetHotKey); hotKeyRow.Children.Add(_hotKey);
        AddRow("Settings.HotKey", hotKeyRow);
        Id(_resetHotKey, "reset-hot-key");
        _resetHotKey.Click += (_, _) =>
        {
            if (_refreshing || _busy) return;
            _hotKey.CancelRecording();
            BeginApply(s => s with { HotKey = new WallSettings().HotKey });
        };
        AddRow("Settings.RunAtLogin", _runAtLogin);
        AddRow("Settings.Topmost", _topmost);
        AddRow("Settings.StayVisible", _stayVisible);
        AddRow("Settings.Anchor", _anchor);
        AddRow("Settings.MenuStart", _menuStart);
        AddRow("Settings.FullScreen", _fullScreen);
        AddRow("Settings.ExpandColumns", _expandColumns);
        AddRow("Settings.Reset", _reset);
        var contextMenuRow = new StackPanel { Spacing = 8 };
        contextMenuRow.Children.Add(_contextMenu);
        _contextMenuConfirmation.Children.Add(_contextMenuTransfer);
        var contextActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        contextActions.Children.Add(_confirmContextTransfer); contextActions.Children.Add(_cancelContextTransfer);
        _contextMenuConfirmation.Children.Add(contextActions);
        contextMenuRow.Children.Add(_contextMenuConfirmation);
        AddRow("Settings.ExplorerMenu", contextMenuRow);
        AddSeparator();
        AddRow("Settings.Product", new TextBlock { Text = "TileWall", VerticalAlignment = VerticalAlignment.Center });
        AddRow("Settings.VersionLabel", _version);
        var visitLinks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var storeLink = Link("Settings.MicrosoftStore", "https://apps.microsoft.com/");
        var sourceLink = Link("Settings.OpenSource", "https://github.com/GitRuozhi/TileWall_for_Win11");
        var developerLink = Link("Settings.Developer", "https://github.com/GitRuozhi");
        Id(storeLink, "visit-store"); Id(sourceLink, "visit-source"); Id(developerLink, "visit-developer");
        visitLinks.Children.Add(storeLink); visitLinks.Children.Add(sourceLink); visitLinks.Children.Add(developerLink);
        AddRow("Settings.Visit", visitLinks);
        var feedbackLinks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var storeFeedbackLink = Link("Settings.MicrosoftStore", "https://apps.microsoft.com/");
        var discussionLink = Link("Settings.Discussion", "https://github.com/GitRuozhi/TileWall_for_Win11/discussions");
        var communityLink = Link("Settings.QQGroup", CommunityGroupUrl,
            "Settings.CommunityOpenFailed");
        Id(storeFeedbackLink, "feedback-store"); Id(discussionLink, "feedback-discussion"); Id(communityLink, "community");
        feedbackLinks.Children.Add(storeFeedbackLink); feedbackLinks.Children.Add(discussionLink); feedbackLinks.Children.Add(communityLink);
        AddRow("Settings.Feedback", feedbackLinks);
        var root = (owner.Content as FrameworkElement)?.XamlRoot;
        var body = new Grid { Width = Math.Min(600, Math.Max(400, (root?.Size.Width ?? 800) - 100)),
            MaxHeight = Math.Min(560, Math.Max(120, (root?.Size.Height ?? 780) - 210)) };
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _formHost.Content = _form; body.Children.Add(_formHost);
        var statusHost = new ScrollViewer { Content = _status, MaxHeight = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(statusHost, 1); body.Children.Add(statusHost);
        _dialog = new ContentDialog { XamlRoot = root, Content = body, DefaultButton = ContentDialogButton.None };
        _dialog.Resources["ContentDialogMaxWidth"] = 700d;
        Id(_dialog, "dialog"); Id(_anchor, "anchor"); Id(_topmost, "topmost"); Id(_stayVisible, "stay-visible");
        Id(_language, "language"); Id(_visualStyle, "visual-style"); Id(_theme, "theme"); Id(_background, "background");
        Id(_hotKey, "hotkey"); Id(_transparency, "transparency"); Id(_transparencyInput, "transparency-input");
        Id(_backgroundPath, "background-path"); Id(_showGrid, "show-grid"); Id(_runAtLogin, "run-at-login"); Id(_reset, "reset"); Id(_status, "status");
        Id(_contextMenu, "context-menu");
        Id(_replaceStartMenu, "replace-start-menu"); Id(_startReplacementStatus, "start-replacement-state");
        Id(_retryStart, "start-replacement-retry");
        Id(_confirmContextTransfer, "context-menu-confirm-transfer"); Id(_cancelContextTransfer, "context-menu-cancel-transfer");
        if (_traceInput)
        {
            body.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
                TraceSettings("BodyPressed", PointerDetail(body, e) +
                    $" enabled={_formHost.IsEnabled} scroll={_formHost.VerticalOffset:F2}")), true);
            body.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler((_, e) =>
                TraceSettings("DoubleTapped", $"source={e.OriginalSource?.GetType().Name}")), true);
            body.SizeChanged += (_, _) => TraceSettings("BodySize", $"size={body.ActualWidth:F2},{body.ActualHeight:F2}");
            foreach (var toggle in new[] { _showGrid, _runAtLogin, _topmost, _stayVisible, _menuStart,
                _fullScreen, _expandColumns, _replaceStartMenu, _contextMenu })
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
        _language.SelectionChanged += (_, _) => BeginChoiceApply(_language, s => s with { LanguageMode = (WallLanguageMode)_language.SelectedIndex });
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
        _showGrid.Toggled += (_, _) => BeginApply(s => s with { ShowGrid = _showGrid.IsOn });
        _runAtLogin.Toggled += (_, _) => BeginApply(s => s with { RunAtLogin = _runAtLogin.IsOn });
        _replaceStartMenu.Toggled += (_, _) => BeginApply(s => s with { ReplaceStartMenu = _replaceStartMenu.IsOn });
        _retryStart.Click += (_, _) =>
        {
            if (!_busy && _retryStartReplacement is not null) _pending = RetryStartReplacementAsync();
        };
        _reset.Click += (_, _) =>
        {
            if (_refreshing || _busy) return;
            _hotKey.CancelRecording(); _pending = ApplyAsync(new WallSettings(), restoreDefaults: true);
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
        _dialog.Closing += (_, e) =>
        {
            _backgroundProbe?.Cancel();
            // Explicitly settle NumberBox's inner text before moving focus, so
            // its apply enters _pending before deciding whether this page closes.
            OwnedDialogWindow.ReleaseTextFocus(_dialog);
            _hotKey.CancelRecording();
            if (_forceClose && !_busy && !_hotKey.HasPendingTransition) return;
            if (_showFailureBeforeClose) { _showFailureBeforeClose = false; e.Cancel = true; return; }
            if (_busy || _hotKey.HasPendingTransition)
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
            await WaitForPendingAsync();
        }
    }
    public bool HasUnsavedDraft => false;
    public async Task WaitForPendingAsync()
    {
        _backgroundProbe?.Cancel();
        OwnedDialogWindow.ReleaseTextFocus(_dialog);
        await _hotKey.CancelRecordingAsync();
        await _hotKey.WaitForIdleAsync();
        await AwaitPendingAsync();
        await _hotKey.WaitForIdleAsync();
    }
    public Task<ExitDecision> ConfirmExitAsync() => Task.FromResult(ExitDecision.DiscardAndExit);
    public string? TrySaveNow() => _busy ? ProductStrings.Get("Settings.Saving") : null;
    public void DiscardDraft() { }
    public void RequestDismiss() => _dialog.Hide();
    public void Close()
    {
        _backgroundProbe?.Cancel();
        if (_busy) _closeRequested = true;
        else { _forceClose = true; _dialog.Hide(); }
    }

    public void RefreshStrings()
    {
        _refreshing = true;
        _dialog.Title = ProductStrings.Get("Settings.Title"); _dialog.CloseButtonText = ProductStrings.Get("Common.Close");
        ActionIcon.DecorateDialog(_dialog);
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
        SetChoices(_language, "Settings.Language", ["Settings.SystemLanguage", "Settings.Chinese", "Settings.English"]);
        SetChoices(_visualStyle, "Settings.VisualStyle", ["Settings.Win10Style", "Settings.Win11Style"]);
        SetChoices(_background, "Settings.Background", ["Settings.Acrylic", "Settings.Mica", "Settings.Custom"]);
        foreach (var (key, label) in _labels) label.Text = ProductStrings.Get(key);
        foreach (var (button, key) in _linkButtons) ActionIcon.Set(button, ProductStrings.Get(key), ActionIcon.Link);
        foreach (var (toggle, key) in new[] { (_topmost, "Settings.Topmost"), (_stayVisible, "Settings.StayVisible"),
            (_showGrid, "Settings.ShowGrid"), (_runAtLogin, "Settings.RunAtLogin"), (_fullScreen, "Settings.FullScreen"),
            (_expandColumns, "Settings.ExpandColumns"), (_menuStart, "Settings.MenuStart"),
            (_replaceStartMenu, "Settings.ReplaceStartMenu"),
            (_contextMenu, "Settings.ContextMenuToggle") })
        {
            toggle.OnContent = string.Empty; toggle.OffContent = string.Empty;
            var name = ProductStrings.Get(key);
            AutomationProperties.SetName(toggle, name); ToolTipService.SetToolTip(toggle, name);
        }
        ActionIcon.Set(_reset, ProductStrings.Get("Settings.Reset"), ActionIcon.Reset);
        ActionIcon.SetIcon(_resetHotKey, ProductStrings.Get("Settings.ResetHotKey"), ActionIcon.Reset);
        foreach (var (key, control, helpKey) in new (string, FrameworkElement, string)[]
        {
            ("Settings.ReplaceStartMenu", _replaceStartMenu, "Settings.StartReplacementHelp"),
            ("Settings.RunAtLogin", _runAtLogin, "Settings.RunAtLoginHelp"),
            ("Settings.Topmost", _topmost, "Settings.TopmostHelp"),
            ("Settings.StayVisible", _stayVisible, "Settings.StayVisibleHelp"),
            ("Settings.ShowGrid", _showGrid, "Settings.ShowGridHint"),
            ("Settings.Language", _language, "Settings.LanguageFallback"),
            ("Settings.MenuStart", _menuStart, "Settings.MenuStartHelp"),
            ("Settings.FullScreen", _fullScreen, "Settings.FullScreenHelp"),
            ("Settings.ExpandColumns", _expandColumns, "Settings.ExpandColumnsHelp"),
            ("Settings.Reset", _reset, "Settings.ResetHelp"),
            ("Settings.ExplorerMenu", _contextMenu, "Settings.ContextMenuHelp"),
        }) SetHelp(key, control, ProductStrings.Get(helpKey));
        _retryStart.Content = ProductStrings.Get("Settings.StartReplacementRetry");
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
            _anchorButtons[index].IsEnabled = true;
            ActionIcon.SetTip(_anchorButtons[index], ProductStrings.Get(anchorKeys[index]));
        }
        _fullScreen.IsOn = _state.Settings.FullScreen;
        _expandColumns.IsOn = _state.Settings.ExpandColumns;
        _expandColumns.IsEnabled = true;
        _menuStart.IsOn = _state.Settings.MenuStart;
        Id(_expandColumns, "expand-columns"); Id(_menuStart, "menu-start");
        ToolTipService.SetToolTip(_labels["Settings.Anchor"], ProductStrings.Get("Settings.Anchor"));
        _language.SelectedIndex = (int)_state.Settings.LanguageMode;
        _theme.SelectedIndex = (int)_state.Settings.ThemeMode; _visualStyle.SelectedIndex = (int)_state.Settings.VisualStyle;
        _background.SelectedIndex = (int)_state.Settings.BackgroundMode;
        _backgroundPath.Text = _state.Settings.BackgroundMode == WallBackgroundMode.Custom ? _state.Settings.BackgroundImagePath ?? string.Empty : string.Empty;
        ToolTipService.SetToolTip(_backgroundPath, _backgroundPath.Text);
        _transparency.Value = _state.Settings.BackgroundTransparency; _transparencyInput.Value = _state.Settings.BackgroundTransparency;
        _topmost.IsOn = _state.Settings.AlwaysOnTop; _stayVisible.IsOn = _state.Settings.StayVisibleOnFocusLoss;
        _showGrid.IsOn = _state.Settings.ShowGrid; _runAtLogin.IsOn = _state.Settings.RunAtLogin;
        _hotKey.SetConfigured(_state.Settings.HotKey);
        _status.Text = _state.Error is null ? string.Empty : string.Format(ProductStrings.Get("Settings.ApplyFailed"), _state.Error);
        _status.Visibility = _state.Error is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshContextMenuPresentation();
        RefreshStartReplacementPresentation();
        _refreshing = false;
        TraceSettings("RestoreAfter");
    }

    private void RefreshStartReplacementState()
    {
        if (!_contextPageOpen || _busy) return;
        var current = _readState();
        _state = _state with { Settings = current.Settings, RegisteredHotKey = current.RegisteredHotKey,
            StartReplacementActive = current.StartReplacementActive,
            StartReplacementSuspended = current.StartReplacementSuspended,
            StartReplacementError = current.StartReplacementError };
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
            _retryStart.IsEnabled = !_busy && !_hotKey.HasPendingTransition;
            SetHelp("Settings.HotKey", _hotKey, ProductStrings.Get(_state.StartReplacementActive
                ? "Settings.HotKeyOpensStart" : "Settings.HotKeyOpensTileWall"));
        }
        finally { _refreshing = wasRefreshing; }
    }

    private async Task RetryStartReplacementAsync()
    {
        _busy = true; _formHost.IsEnabled = false;
        try
        {
            await _hotKey.CancelRecordingAsync();
            _state = await _retryStartReplacement!();
        }
        catch (Exception ex)
        {
            _state = _readState() with { StartReplacementError =
                string.Format(ProductStrings.Get("Settings.StartReplacementFailed"), ex.Message) };
        }
        finally { _busy = false; _formHost.IsEnabled = true; RestoreState(); FinishPending(); }
    }

    private async Task FinishRecordingForCloseAsync()
    {
        _busy = true; _formHost.IsEnabled = false;
        try { await _hotKey.CancelRecordingAsync(); await _hotKey.WaitForIdleAsync(); }
        catch (Exception ex) { _state = _readState() with { Error = ex.Message }; RestoreState(); }
        finally { _busy = false; _formHost.IsEnabled = true; FinishPending(); }
    }

    private void SetTransparency(double value, bool apply)
    {
        if (_refreshing || _busy) return;
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
            var snapshot = await Task.Run(ExplorerContextMenuRegistration.ReadMachineState);
            if (!_contextPageOpen) return;
            // A menu write may have started and finished while this read was running.
            if (_busy || observedRevision != _contextMutationRevision)
            {
                _contextRefreshRequested = true;
                return;
            }
            _contextMachineState = snapshot;
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

    private static bool IsCurrentContextTarget(string target) => Environment.ProcessPath is { } executable &&
        string.Equals(Path.GetFullPath(target), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);

    private string[] OtherContextTargets() => _contextMachineState?.Targets.Where(target => !IsCurrentContextTarget(target))
        .Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];

    private bool HasCurrentContextEntries => _contextMachineState?.Targets.Any(IsCurrentContextTarget) == true;

    private void BeginContextMenuChange(bool enable, string[]? confirmedTargets = null)
    {
        if (_refreshing || _busy || !_contextPageOpen) return;
        _contextMutationRevision++;
        _hotKey.CancelRecording();
        _contextTransferTargets = null;
        _contextOutcomeKey = null; _contextOutcomeArgument = null;
        _pending = ChangeContextMenuAsync(enable, confirmedTargets);
    }

    private async Task ChangeContextMenuAsync(bool enable, string[]? confirmedTargets)
    {
        _busy = true; _formHost.IsEnabled = false;
        var failed = false;
        try
        {
            await _hotKey.CancelRecordingAsync();
            // Fresh ownership before either a transfer confirmation or UAC.
            _contextMachineState = await Task.Run(ExplorerContextMenuRegistration.ReadMachineState);
            var machine = _contextMachineState!;
            if (machine.Status == MachineContextMenuStatus.ReadFailed || enable && machine.HasUnrecognizedConflict)
            {
                failed = true;
                _contextOutcomeKey = machine.Status == MachineContextMenuStatus.ReadFailed
                    ? "Settings.ContextMenuReadFailed" : "Settings.ContextMenuConflict";
                _contextOutcomeArgument = machine.Error;
                return;
            }
            var otherTargets = OtherContextTargets();
            if (enable && otherTargets.Length != 0 && (confirmedTargets is null ||
                !otherTargets.SequenceEqual(confirmedTargets, StringComparer.OrdinalIgnoreCase)))
            {
                _contextTransferTargets = otherTargets;
                return;
            }

            RefreshContextMenuPresentation();
            var language = ProductStrings.Language == WallLanguageMode.Chinese ||
                ProductStrings.Language == WallLanguageMode.System &&
                System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                ? "zh" : "en";
            var result = await ContextMenuElevation.ApplyAsync(enable, language);
            _contextMachineState = result.State;
            _contextRefreshRequested = false;
            var actual = result.State;
            var matchesRequest = actual.Status != MachineContextMenuStatus.ReadFailed &&
                (enable ? actual.IsCurrentExecutableComplete : !HasCurrentContextEntries);
            failed = result.Status != ContextMenuElevationStatus.Completed || !matchesRequest;
            _contextOutcomeKey = result.Status switch
            {
                ContextMenuElevationStatus.Cancelled => "Settings.ContextMenuCancelled",
                ContextMenuElevationStatus.LaunchFailed => "Settings.ContextMenuLaunchFailed",
                ContextMenuElevationStatus.HelperFailed when result.ExitCode == ApplicationModel.ContextMenuAdminCommand.RollbackIncomplete
                    => "Settings.ContextMenuRollbackIncomplete",
                ContextMenuElevationStatus.HelperFailed => "Settings.ContextMenuOperationFailed",
                _ => matchesRequest ? null : "Settings.ContextMenuChangedElsewhere"
            };
            _contextOutcomeArgument = result.Status == ContextMenuElevationStatus.HelperFailed
                ? result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) : result.Error;
        }
        catch (Exception ex)
        {
            failed = true;
            _contextOutcomeKey = "Settings.ContextMenuLaunchFailed";
            _contextOutcomeArgument = ex.Message;
            _contextMachineState = await Task.Run(ExplorerContextMenuRegistration.ReadMachineState);
        }
        finally
        {
            _busy = false;
            if (_contextPageOpen)
            {
                _formHost.IsEnabled = true;
                RefreshContextMenuPresentation();
                if (failed && _contextOutcomeKey is not null)
                {
                    AppLog.Warn("ExplorerMenu", $"operation={(enable ? "enable" : "disable")} outcome={_contextOutcomeKey} detail={_contextOutcomeArgument}");
                    _status.Text = ProductStrings.Get(_contextOutcomeKey);
                    _status.Visibility = Visibility.Visible;
                }
                else
                {
                    _status.Text = _state.Error is null ? string.Empty : string.Format(ProductStrings.Get("Settings.ApplyFailed"), _state.Error);
                    _status.Visibility = _state.Error is null ? Visibility.Collapsed : Visibility.Visible;
                }
                FinishPending(failed);
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
            _contextMenu.IsOn = HasCurrentContextEntries;
            _contextMenu.IsEnabled = !_busy && _contextMachineState is not null;
            _confirmContextTransfer.IsEnabled = _cancelContextTransfer.IsEnabled = !_busy;
            _contextMenuConfirmation.Visibility = _contextTransferTargets is null ? Visibility.Collapsed : Visibility.Visible;
            if (_contextTransferTargets is not null)
                _contextMenuTransfer.Text = string.Format(ProductStrings.Get("Settings.ContextMenuTransfer"),
                    string.Join("\n", _contextTransferTargets), Environment.ProcessPath);
        }
        finally { _refreshing = wasRefreshing; }
    }

    private void BeginChoiceApply(ComboBox box, Func<WallSettings, WallSettings> change, bool pickImage = false)
    {
        if (_refreshing || _busy) return;
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
            _busy = false; _formHost.IsEnabled = true; RestoreState(); FinishPending();
        }
        finally
        {
            if (ReferenceEquals(_backgroundProbe, probe)) _backgroundProbe = null;
            probe?.Dispose();
        }
    }

    private async Task PickBackgroundAsync(CancellationToken token)
    {
        _busy = true; _formHost.IsEnabled = false;
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
        finally { _busy = false; _formHost.IsEnabled = true; RestoreState(); }
        if (path is not null) await ApplyAsync(_state.Settings with { BackgroundMode = WallBackgroundMode.Custom, BackgroundImagePath = path, BackgroundMedia = null });
        else FinishPending();
    }

    private void BeginApply(Func<WallSettings, WallSettings> change)
    {
        TraceSettings("BeginApply");
        if (_refreshing || _busy) { TraceSettings("IgnoredBusyOrRefresh"); return; }
        var candidate = change(_state.Settings);
        if (candidate == _state.Settings) { TraceSettings("IgnoredUnchanged"); return; }
        TraceSettings("Candidate", SettingsFlags(candidate));
        _pending = ApplyAsync(candidate);
    }

    private async Task ApplyAsync(WallSettings candidate, bool restoreDefaults = false)
    {
        TraceSettings("ApplyStart", SettingsFlags(candidate));
        var restoreAnchorFocus = _anchor.FocusState != FocusState.Unfocused;
        _busy = true; _formHost.IsEnabled = false; _status.Visibility = Visibility.Visible; _status.Text = ProductStrings.Get("Settings.Saving");
        try { await _hotKey.CancelRecordingAsync(); _state = await _apply(candidate, restoreDefaults); TraceSettings("ApplyReturned", _state.Error ?? "success"); }
        catch (Exception ex)
        {
            _state = _readState() with { Error = string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message) };
            TraceSettings("ApplyFailed", _state.Error);
        }
        finally { _busy = false; _formHost.IsEnabled = true; RestoreState(); if (restoreAnchorFocus) _anchor.Focus(FocusState.Programmatic); FinishPending(); }
    }

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

    private void AddRow(string key, FrameworkElement value)
    {
        var row = _form.RowDefinitions.Count;
        _form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _labels.Add(key, label); AutomationProperties.SetLabeledBy(value, label);
        Grid.SetRow(label, row); _form.Children.Add(label);
        Grid.SetRow(value, row); Grid.SetColumn(value, 1); _form.Children.Add(value);
    }

    private void AddSeparator()
    {
        var row = _form.RowDefinitions.Count;
        _form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var separator = new Border { Height = 8 };
        Grid.SetRow(separator, row); Grid.SetColumnSpan(separator, 2); _form.Children.Add(separator);
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
        _status.Text = string.Format(ProductStrings.Get(messageKey), detail);
        _status.Visibility = Visibility.Visible;
    }
    private static void SetChoices(ComboBox box, string header, string[] keys)
    {
        box.Header = null; AutomationProperties.SetName(box, ProductStrings.Get(header));
        var selected = box.SelectedIndex;
        var refreshSelection = false;
        for (var index = 0; index < keys.Length; index++)
        {
            var text = ProductStrings.Get(keys[index]);
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



