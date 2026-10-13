using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using TileWall.Core.Settings;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;
using Windows.Storage.Pickers;

namespace TileWall.Views;

internal sealed record SettingsDialogState(WallSettings Settings, string? RegisteredHotKey,
    bool? LoginStartupEnabled, string? Error = null);

/// <summary>Immediate settings edits. The host owns persistence and operating-system rollback.</summary>
internal sealed class SettingsDialog : IWallDialogSession
{
    private readonly Window _owner;
    private readonly Func<SettingsDialogState> _readState;
    private readonly Func<WallSettings, bool, Task<SettingsDialogState>> _apply;
    private readonly ContentDialog _dialog;
    private readonly Grid _form = new() { RowSpacing = 12, ColumnSpacing = 20 };
    private readonly Dictionary<string, TextBlock> _labels = [];
    private readonly ScrollViewer _formHost = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ComboBox _anchor = new(), _language = new(), _visualStyle = new(), _theme = new(), _background = new();
    private readonly ToggleSwitch _topmost = new(), _stayVisible = new(), _runAtLogin = new(), _showGrid = new();
    private readonly TextBox _hotKey = new() { IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
    private readonly Slider _transparency = new() { Minimum = 0, Maximum = 100, StepFrequency = 1, MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
    private readonly NumberBox _transparencyInput = new() { Minimum = 0, Maximum = 100, SmallChange = 1, Width = 76, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
    private readonly TextBlock _backgroundPath = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _reset = new();
    private readonly TextBlock _version = new(), _status = new() { TextWrapping = TextWrapping.Wrap };
    private SettingsDialogState _state;
    private Task _pending = Task.CompletedTask;
    private bool _refreshing, _busy, _closeRequested, _forceClose, _showFailureBeforeClose, _hotKeyInvalid;

    public SettingsDialog(Window owner, Func<SettingsDialogState> readState,
        Func<WallSettings, bool, Task<SettingsDialogState>> applyAsync)
    {
        _owner = owner; _readState = readState; _apply = applyAsync; _state = readState();
        _form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        _form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddRow("Settings.Theme", _theme);
        AddRow("Settings.VisualStyle", _visualStyle);
        AddRow("Settings.Language", _language);
        AddRow("Settings.ShowGrid", _showGrid);
        var backgroundRow = new Grid { ColumnSpacing = 12 };
        backgroundRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        backgroundRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        backgroundRow.Children.Add(_background); Grid.SetColumn(_backgroundPath, 1); backgroundRow.Children.Add(_backgroundPath);
        AddRow("Settings.Background", backgroundRow);
        var transparencyRow = new Grid { ColumnSpacing = 10 };
        transparencyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        transparencyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transparencyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transparencyRow.Children.Add(_transparency); Grid.SetColumn(_transparencyInput, 1); transparencyRow.Children.Add(_transparencyInput);
        var percent = new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(percent, 2); transparencyRow.Children.Add(percent); AddRow("Settings.Transparency", transparencyRow);
        AddSeparator();
        AddRow("Settings.Anchor", _anchor);
        AddRow("Settings.HotKey", _hotKey);
        AddRow("Settings.RunAtLogin", _runAtLogin);
        AddRow("Settings.Topmost", _topmost);
        AddRow("Settings.StayVisible", _stayVisible);
        AddRow("Settings.Reset", _reset);
        AddSeparator();
        AddRow("Settings.Product", new TextBlock { Text = "TileWall", VerticalAlignment = VerticalAlignment.Center });
        AddRow("Settings.VersionLabel", _version);
        AddRow("Settings.Provider", Link("GitRuozhi", "https://github.com/GitRuozhi"));
        AddRow("Settings.Website", Link("GitHub", "https://github.com/GitRuozhi/TileWall_for_Win11"));
        AddRow("Settings.Updates", Link("Releases", "https://github.com/GitRuozhi/TileWall_for_Win11/releases"));
        AddRow("Settings.Feedback", Link("Discussions", "https://github.com/GitRuozhi/TileWall_for_Win11/discussions"));
        AddRow("Settings.Chat", new TextBlock());
        var root = (owner.Content as FrameworkElement)?.XamlRoot;
        var body = new Grid { Width = Math.Min(620, Math.Max(400, (root?.Size.Width ?? 800) - 100)),
            MaxHeight = Math.Min(560, Math.Max(120, (root?.Size.Height ?? 780) - 210)) };
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _formHost.Content = _form; body.Children.Add(_formHost);
        var statusHost = new ScrollViewer { Content = _status, MaxHeight = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(statusHost, 1); body.Children.Add(statusHost);
        _dialog = new ContentDialog { XamlRoot = root, Content = body, DefaultButton = ContentDialogButton.None };
        _dialog.Resources["ContentDialogMaxWidth"] = 740d;
        Id(_dialog, "dialog"); Id(_anchor, "anchor"); Id(_topmost, "topmost"); Id(_stayVisible, "stay-visible");
        Id(_language, "language"); Id(_visualStyle, "visual-style"); Id(_theme, "theme"); Id(_background, "background");
        Id(_hotKey, "hotkey"); Id(_transparency, "transparency"); Id(_transparencyInput, "transparency-input");
        Id(_backgroundPath, "background-path"); Id(_showGrid, "show-grid"); Id(_runAtLogin, "run-at-login"); Id(_reset, "reset"); Id(_status, "status");
        RefreshStrings(); RestoreState();
        _anchor.SelectionChanged += (_, _) => { if (_anchor.SelectedIndex is >= 0 and < 4) BeginChoiceApply(_anchor, s => s with { AnchorCorner = (AnchorCorner)_anchor.SelectedIndex }); };
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
        _topmost.Toggled += (_, _) => BeginApply(s => s with { AlwaysOnTop = _topmost.IsOn });
        _stayVisible.Toggled += (_, _) => BeginApply(s => s with { StayVisibleOnFocusLoss = _stayVisible.IsOn });
        _showGrid.Toggled += (_, _) => BeginApply(s => s with { ShowGrid = _showGrid.IsOn });
        _runAtLogin.Toggled += (_, _) => BeginApply(s => s with { RunAtLogin = _runAtLogin.IsOn });
        _reset.Click += (_, _) =>
        {
            if (_refreshing || _busy) return;
            _hotKeyInvalid = false; _pending = ApplyAsync(new WallSettings(), restoreDefaults: true);
        };
        _hotKey.LostFocus += (_, _) => ApplyHotKey();
        _hotKey.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { ApplyHotKey(); e.Handled = true; } };
        _dialog.Closing += (_, e) =>
        {
            if (_forceClose) return;
            if (!_busy && !ApplyHotKey()) { _showFailureBeforeClose = false; e.Cancel = true; return; }
            if (_showFailureBeforeClose) { _showFailureBeforeClose = false; e.Cancel = true; return; }
            if (_busy) { e.Cancel = true; _closeRequested = true; }
        };
    }

    public async Task ShowAsync() { await OwnedDialogWindow.ShowAsync(_dialog); await _pending; }
    public bool HasUnsavedDraft => false;
    public Task WaitForPendingAsync() { if (!_busy) ApplyHotKey(); return _pending; }
    public Task<ExitDecision> ConfirmExitAsync() => Task.FromResult(ExitDecision.DiscardAndExit);
    public string? TrySaveNow() => _busy ? ProductStrings.Get("Settings.Saving") : null;
    public void DiscardDraft() { }
    public void RequestDismiss() => _dialog.Hide();
    public void Close() { if (_busy) _closeRequested = true; else { _forceClose = true; _dialog.Hide(); } }

    public void RefreshStrings()
    {
        _refreshing = true;
        _dialog.Title = ProductStrings.Get("Settings.Title"); _dialog.CloseButtonText = ProductStrings.Get("Common.Close");
        SetChoices(_anchor, "Settings.Anchor", ["Settings.BottomLeft", "Settings.BottomRight", "Settings.TopLeft", "Settings.TopRight", "Settings.FullScreen"]);
        ((ComboBoxItem)_anchor.Items[4]).IsEnabled = false;
        SetChoices(_theme, "Settings.Theme", ["Settings.SystemLanguage", "Settings.Light", "Settings.Dark"]);
        SetChoices(_language, "Settings.Language", ["Settings.SystemLanguage", "Settings.Chinese", "Settings.English"]);
        SetChoices(_visualStyle, "Settings.VisualStyle", ["Settings.Win10Style", "Settings.Win11Style"]);
        SetChoices(_background, "Settings.Background", ["Settings.Acrylic", "Settings.Mica", "Settings.Custom"]);
        foreach (var (key, label) in _labels) label.Text = ProductStrings.Get(key);
        foreach (var toggle in new[] { _topmost, _stayVisible, _showGrid, _runAtLogin }) { toggle.OnContent = string.Empty; toggle.OffContent = string.Empty; }
        _reset.Content = ProductStrings.Get("Settings.Reset");
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
        _anchor.SelectedIndex = (int)_state.Settings.AnchorCorner; _language.SelectedIndex = (int)_state.Settings.LanguageMode;
        _theme.SelectedIndex = (int)_state.Settings.ThemeMode; _visualStyle.SelectedIndex = (int)_state.Settings.VisualStyle;
        _background.SelectedIndex = (int)_state.Settings.BackgroundMode;
        _backgroundPath.Text = _state.Settings.BackgroundMode == WallBackgroundMode.Custom ? _state.Settings.BackgroundImagePath ?? string.Empty : string.Empty;
        ToolTipService.SetToolTip(_backgroundPath, _backgroundPath.Text);
        _transparency.Value = _state.Settings.BackgroundTransparency; _transparencyInput.Value = _state.Settings.BackgroundTransparency;
        _topmost.IsOn = _state.Settings.AlwaysOnTop; _stayVisible.IsOn = _state.Settings.StayVisibleOnFocusLoss;
        _showGrid.IsOn = _state.Settings.ShowGrid; _runAtLogin.IsOn = _state.Settings.RunAtLogin;
        if (!_hotKeyInvalid) _hotKey.Text = _state.Settings.HotKey ?? string.Empty;
        _status.Text = _state.Error is null ? string.Empty : string.Format(ProductStrings.Get("Settings.ApplyFailed"), _state.Error);
        _status.Visibility = _state.Error is null ? Visibility.Collapsed : Visibility.Visible;
        _refreshing = false;
    }

    private bool ApplyHotKey()
    {
        if (_refreshing || _busy) return true;
        if (_hotKey.Text == (_state.Settings.HotKey ?? string.Empty))
        {
            _hotKeyInvalid = false; ApplyTransparency(); return true;
        }
        if (!HotKeyGesture.TryParse(_hotKey.Text, out var gesture, out var error))
        {
            _hotKeyInvalid = true; _state = _readState() with { Error = error }; RestoreState(); return false;
        }
        _hotKeyInvalid = false; BeginApply(s => s with { HotKey = gesture!.ToConfigString(), BackgroundTransparency = (int)_transparency.Value });
        return _busy || _state.Error is null;
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
        _pending = ApplyChoiceAsync(box, candidate, pickImage);
    }

    private async Task ApplyChoiceAsync(ComboBox box, WallSettings candidate, bool pickImage)
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
            if (pickImage) await PickBackgroundAsync();
            else await ApplyAsync(candidate);
        }
        catch (Exception ex)
        {
            _state = _readState() with { Error = string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message) };
            _busy = false; _formHost.IsEnabled = true; RestoreState(); FinishPending();
        }
    }

    private async Task PickBackgroundAsync()
    {
        _busy = true; _formHost.IsEnabled = false;
        string? path = null;
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".bmp" }) picker.FileTypeFilter.Add(extension);
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                var decoded = await new ImageFrameDecoder().DecodeAsync(file.Path, 512, 512);
                if (decoded.Status != ImageDecodeStatus.Success) throw new InvalidDataException();
                path = file.Path;
            }
        }
        catch { _state = _readState() with { Error = ProductStrings.Get("Settings.BackgroundFailed") }; }
        finally { _busy = false; _formHost.IsEnabled = true; RestoreState(); }
        if (path is not null) await ApplyAsync(_state.Settings with { BackgroundMode = WallBackgroundMode.Custom, BackgroundImagePath = path });
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
        _busy = true; _formHost.IsEnabled = false; _status.Visibility = Visibility.Visible; _status.Text = ProductStrings.Get("Settings.Saving");
        try { _state = await _apply(candidate, restoreDefaults); }
        catch (Exception ex) { _state = _readState() with { Error = string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message) }; }
        finally { _busy = false; _formHost.IsEnabled = true; RestoreState(); FinishPending(); }
    }

    private void FinishPending()
    {
        _showFailureBeforeClose = _state.Error is not null;
        var closeAfterSuccess = _closeRequested && _state.Error is null;
        _closeRequested = false;
        if (closeAfterSuccess) _dialog.Hide(); // Closing checks any final hotkey/transparency input again.
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
        var separator = (Border)XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Height=\"1\" Margin=\"0,6,0,6\" Background=\"{ThemeResource DividerStrokeColorDefaultBrush}\" />");
        Grid.SetRow(separator, row); Grid.SetColumnSpan(separator, 2); _form.Children.Add(separator);
    }

    private static HyperlinkButton Link(string text, string url) => new() { Content = text, NavigateUri = new Uri(url), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0) };
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
    private static void Id(DependencyObject item, string id) => AutomationProperties.SetAutomationId(item, "settings-" + id);
}
