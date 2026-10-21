using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TileWall.Core.Settings;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;
using TileWall.Shell.Interop;

namespace TileWall.Views;

internal sealed record SettingsDialogState(WallSettings Settings, string? RegisteredHotKey,
    bool? LoginStartupEnabled, string? Error = null);

/// <summary>Immediate settings edits. The host owns persistence and operating-system rollback.</summary>
internal sealed class SettingsDialog : IWallDialogSession
{
    private const string CommunityGroupUrl =
        "https://qm.qq.com/cgi-bin/qm/qr?k=iw0LxnFaHE8JdUr5z937pFuagFxOtFOo&jump_from=webapi&authKey=JvkcK/IIaFg5e1ymzhP41yxcAiTVURjvhrNtDziZZSGj3ZD2byZhqX2lj48L9jkT";
    private readonly Window _owner;
    private readonly Func<SettingsDialogState> _readState;
    private readonly Func<WallSettings, bool, Task<SettingsDialogState>> _apply;
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
    private readonly Slider _transparency = new() { Minimum = 0, Maximum = 100, StepFrequency = 1, Width = 180, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly NumberBox _transparencyInput = new() { Minimum = 0, Maximum = 100, SmallChange = 1, Width = 56, MinWidth = 0, Padding = new Thickness(8, 5, 8, 5), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
    private readonly TextBlock _backgroundPath = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _reset = new(), _registerContextMenu = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _communityButton;
    private readonly TextBlock _version = new(), _status = new() { TextWrapping = TextWrapping.Wrap };
    private SettingsDialogState _state;
    private Task _pending = Task.CompletedTask;
    private CancellationTokenSource? _backgroundProbe;
    private bool _refreshing, _busy, _closeRequested, _forceClose, _showFailureBeforeClose;

    public SettingsDialog(Window owner, Func<SettingsDialogState> readState,
        Func<WallSettings, bool, Task<SettingsDialogState>> applyAsync, Action<bool>? hotKeyRecordingChanged = null)
    {
        _owner = owner; _readState = readState; _apply = applyAsync; _state = readState();
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
        _hotKey.RecordingChanged += recording => hotKeyRecordingChanged?.Invoke(recording);
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
        AddRow("Settings.ExplorerMenu", _registerContextMenu);
        AddSeparator();
        AddRow("Settings.Product", new TextBlock { Text = "TileWall", VerticalAlignment = VerticalAlignment.Center });
        AddRow("Settings.VersionLabel", _version);
        AddRow("Settings.Provider", Link("GitRuozhi", "https://github.com/GitRuozhi"));
        AddRow("Settings.Website", Link("GitHub", "https://github.com/GitRuozhi/TileWall_for_Win11"));
        AddRow("Settings.Updates", Link("Releases", "https://github.com/GitRuozhi/TileWall_for_Win11/releases"));
        AddRow("Settings.Feedback", Link("Discussions", "https://github.com/GitRuozhi/TileWall_for_Win11/discussions"));
        _communityButton = Link(ProductStrings.Get("Settings.JoinQQGroup"), CommunityGroupUrl,
            "Settings.CommunityOpenFailed");
        Id(_communityButton, "community");
        AddRow("Settings.Chat", _communityButton);
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
        Id(_backgroundPath, "background-path"); Id(_showGrid, "show-grid"); Id(_runAtLogin, "run-at-login"); Id(_reset, "reset"); Id(_status, "status"); Id(_registerContextMenu, "register-context-menu");
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
        _reset.Click += (_, _) =>
        {
            if (_refreshing || _busy) return;
            _hotKey.CancelRecording(); _pending = ApplyAsync(new WallSettings(), restoreDefaults: true);
        };
        _registerContextMenu.Click += (_, _) =>
        {
            if (_refreshing || _busy) return;
            var error = ShortcutContextMenuRegistration.Register(ProductStrings.Get("Explorer.AddToTileWall"));
            _status.Text = error is null ? ProductStrings.Get("Settings.ContextMenuRegistered")
                : string.Format(ProductStrings.Get("Settings.ContextMenuRegistrationFailed"), error);
            _status.Visibility = Visibility.Visible;
        };
        _dialog.Closing += (_, e) =>
        {
            _hotKey.CancelRecording();
            _backgroundProbe?.Cancel();
            // Explicitly settle NumberBox's inner text before moving focus, so
            // its apply enters _pending before deciding whether this page closes.
            OwnedDialogWindow.ReleaseTextFocus(_dialog);
            if (_forceClose && !_busy) return;
            if (_showFailureBeforeClose) { _showFailureBeforeClose = false; e.Cancel = true; return; }
            if (_busy) { e.Cancel = true; _closeRequested = true; }
        };
    }

    public async Task ShowAsync()
    {
        try { await OwnedDialogWindow.ShowAsync(_dialog); }
        finally
        {
            _backgroundProbe?.Cancel();
            try { await _pending; }
            finally { _hotKey.CancelRecording(); }
        }
    }
    public bool HasUnsavedDraft => false;
    public Task WaitForPendingAsync()
    {
        _hotKey.CancelRecording();
        _backgroundProbe?.Cancel();
        OwnedDialogWindow.ReleaseTextFocus(_dialog);
        return _pending;
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
        ActionIcon.Set(_communityButton, ProductStrings.Get("Settings.JoinQQGroup"), ActionIcon.Link);
        foreach (var (toggle, key) in new[] { (_topmost, "Settings.Topmost"), (_stayVisible, "Settings.StayVisible"),
            (_showGrid, "Settings.ShowGrid"), (_runAtLogin, "Settings.RunAtLogin"), (_fullScreen, "Settings.FullScreen"),
            (_expandColumns, "Settings.ExpandColumns"), (_menuStart, "Settings.MenuStart") })
        {
            toggle.OnContent = string.Empty; toggle.OffContent = string.Empty;
            var name = ProductStrings.Get(key);
            AutomationProperties.SetName(toggle, name); ToolTipService.SetToolTip(toggle, name);
        }
        ActionIcon.Set(_reset, ProductStrings.Get("Settings.Reset"), ActionIcon.Reset);
        ActionIcon.SetIcon(_resetHotKey, ProductStrings.Get("Settings.ResetHotKey"), ActionIcon.Reset);
        ActionIcon.Set(_registerContextMenu, ProductStrings.Get("Settings.RegisterContextMenu"), ActionIcon.Register);
        AutomationProperties.SetName(_transparency, ProductStrings.Get("Settings.Transparency"));
        AutomationProperties.SetName(_transparencyInput, ProductStrings.Get("Settings.Transparency"));
        _version.Text = typeof(SettingsDialog).Assembly.GetName().Version?.ToString(3);
        _refreshing = false;
        // The host can refresh strings before _apply has returned its committed state.
        if (!_busy) RestoreState();
    }

    private void RestoreState()
    {
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
        ToolTipService.SetToolTip(_expandColumns, ProductStrings.Get("Settings.ExpandColumns"));
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
        _refreshing = false;
    }

    private void SetTransparency(double value, bool apply)
    {
        if (_refreshing || _busy) return;
        var percent = (int)Math.Clamp(Math.Round(value), 0, 100);
        _refreshing = true; _transparency.Value = percent; _transparencyInput.Value = percent; _refreshing = false;
        if (apply) ApplyTransparency();
    }

    private void ApplyTransparency() => BeginApply(s => s with { BackgroundTransparency = (int)_transparency.Value });

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
        if (_refreshing || _busy) return;
        var candidate = change(_state.Settings);
        if (candidate == _state.Settings) return;
        _pending = ApplyAsync(candidate);
    }

    private async Task ApplyAsync(WallSettings candidate, bool restoreDefaults = false)
    {
        var restoreAnchorFocus = _anchor.FocusState != FocusState.Unfocused;
        _busy = true; _formHost.IsEnabled = false; _status.Visibility = Visibility.Visible; _status.Text = ProductStrings.Get("Settings.Saving");
        try { _state = await _apply(candidate, restoreDefaults); }
        catch (Exception ex) { _state = _readState() with { Error = string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message) }; }
        finally { _busy = false; _formHost.IsEnabled = true; RestoreState(); if (restoreAnchorFocus) _anchor.Focus(FocusState.Programmatic); FinishPending(); }
    }

    private void FinishPending()
    {
        _showFailureBeforeClose = _state.Error is not null;
        var closeAfterSuccess = _closeRequested && _state.Error is null;
        _closeRequested = false;
        if (closeAfterSuccess) _dialog.Hide();
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

    private Button Link(string text, string url, string failureMessageKey = "Settings.OpenLinkFailed")
    {
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Left };
        ActionIcon.Set(button, text, ActionIcon.Link);
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



