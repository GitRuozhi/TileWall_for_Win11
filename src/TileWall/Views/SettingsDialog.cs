using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Settings;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Resources;

namespace TileWall.Views;

internal sealed record SettingsDialogState(WallSettings Settings, string? RegisteredHotKey,
    bool? LoginStartupEnabled, string? Error = null);

/// <summary>Immediate settings edits. The host owns persistence and operating-system rollback.</summary>
internal sealed class SettingsDialog : IWallDialogSession
{
    private readonly Func<SettingsDialogState> _readState;
    private readonly Func<WallSettings, Task<SettingsDialogState>> _apply;
    private readonly ContentDialog _dialog;
    private readonly StackPanel _form = new() { Spacing = 12 };
    private readonly ScrollViewer _formHost = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ComboBox _anchor = new(), _language = new();
    private readonly CheckBox _topmost = new(), _stayVisible = new(), _runAtLogin = new();
    private readonly TextBox _hotKey = new() { IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
    private readonly TextBlock _registered = new() { TextWrapping = TextWrapping.Wrap }, _startup = new(),
        _hint = new() { TextWrapping = TextWrapping.Wrap }, _languageHint = new() { TextWrapping = TextWrapping.Wrap },
        _about = new() { FontSize = 18 }, _version = new(), _description = new() { TextWrapping = TextWrapping.Wrap },
        _status = new() { TextWrapping = TextWrapping.Wrap };
    private SettingsDialogState _state;
    private Task _pending = Task.CompletedTask;
    private bool _refreshing, _busy, _closeRequested, _forceClose, _showFailureBeforeClose;

    public SettingsDialog(Window owner, Func<SettingsDialogState> readState,
        Func<WallSettings, Task<SettingsDialogState>> applyAsync)
    {
        _readState = readState; _apply = applyAsync; _state = readState();
        foreach (var item in new UIElement[] { _anchor, _topmost, _stayVisible, _language, _languageHint,
            _hotKey, _hint, _registered, _runAtLogin, _startup, _about, _version, _description }) _form.Children.Add(item);
        var root = (owner.Content as FrameworkElement)?.XamlRoot;
        var body = new Grid { Width = Math.Min(430, Math.Max(260, (root?.Size.Width ?? 510) - 80)),
            MaxHeight = Math.Min(540, Math.Max(120, (root?.Size.Height ?? 750) - 210)) };
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _formHost.Content = _form;
        body.Children.Add(_formHost);
        var statusHost = new ScrollViewer { Content = _status, MaxHeight = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(statusHost, 1); body.Children.Add(statusHost);
        _dialog = new ContentDialog { XamlRoot = root, Content = body, DefaultButton = ContentDialogButton.None };
        Id(_dialog, "dialog"); Id(_anchor, "anchor"); Id(_topmost, "topmost"); Id(_stayVisible, "stay-visible");
        Id(_language, "language"); Id(_hotKey, "hotkey"); Id(_registered, "registered-hotkey");
        Id(_runAtLogin, "run-at-login"); Id(_startup, "startup-status"); Id(_status, "status");
        RefreshStrings(); RestoreState();
        _anchor.SelectionChanged += (_, _) => BeginApply(s => s with { AnchorCorner = (AnchorCorner)_anchor.SelectedIndex });
        _language.SelectionChanged += (_, _) => BeginApply(s => s with { LanguageMode = (WallLanguageMode)_language.SelectedIndex });
        _topmost.Checked += (_, _) => BeginApply(s => s with { AlwaysOnTop = true });
        _topmost.Unchecked += (_, _) => BeginApply(s => s with { AlwaysOnTop = false });
        _stayVisible.Checked += (_, _) => BeginApply(s => s with { StayVisibleOnFocusLoss = true });
        _stayVisible.Unchecked += (_, _) => BeginApply(s => s with { StayVisibleOnFocusLoss = false });
        _runAtLogin.Checked += (_, _) => BeginApply(s => s with { RunAtLogin = true });
        _runAtLogin.Unchecked += (_, _) => BeginApply(s => s with { RunAtLogin = false });
        _hotKey.LostFocus += (_, _) => ApplyHotKey();
        _hotKey.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { ApplyHotKey(); e.Handled = true; } };
        _dialog.Closing += (_, e) =>
        {
            if (_forceClose) return;
            // Closing can precede TextBox.LostFocus. Apply the final valid input before allowing dismissal.
            if (!_busy && !ApplyHotKey()) { _showFailureBeforeClose = false; e.Cancel = true; return; }
            if (_showFailureBeforeClose) { _showFailureBeforeClose = false; e.Cancel = true; return; }
            if (_busy) { e.Cancel = true; _closeRequested = true; }
        };
    }

    public async Task ShowAsync() { await _dialog.ShowAsync(); await _pending; }
    public bool HasUnsavedDraft => false;
    public Task WaitForPendingAsync() { if (!_busy) ApplyHotKey(); return _pending; }
    public Task<ExitDecision> ConfirmExitAsync() => Task.FromResult(ExitDecision.DiscardAndExit);
    public string? TrySaveNow() => _busy ? ProductStrings.Get("Settings.Saving") : null;
    public void DiscardDraft() { }
    public void Close() { if (_busy) _closeRequested = true; else { _forceClose = true; _dialog.Hide(); } }

    public void RefreshStrings()
    {
        _refreshing = true;
        _dialog.Title = ProductStrings.Get("Settings.Title"); _dialog.CloseButtonText = ProductStrings.Get("Common.Close");
        SetChoices(_anchor, "Settings.Anchor", ["Settings.BottomLeft", "Settings.BottomRight", "Settings.TopLeft", "Settings.TopRight"]);
        SetChoices(_language, "Settings.Language", ["Settings.SystemLanguage", "Settings.Chinese"]);
        _topmost.Content = ProductStrings.Get("Settings.Topmost"); _stayVisible.Content = ProductStrings.Get("Settings.StayVisible");
        _runAtLogin.Content = ProductStrings.Get("Settings.RunAtLogin"); _hotKey.Header = ProductStrings.Get("Settings.HotKey");
        _hint.Text = ProductStrings.Get("Settings.HotKeyHint"); _languageHint.Text = ProductStrings.Get("Settings.LanguageFallback");
        _about.Text = ProductStrings.Get("Settings.About");
        _version.Text = string.Format(ProductStrings.Get("Settings.Version"), typeof(SettingsDialog).Assembly.GetName().Version?.ToString(3));
        _description.Text = ProductStrings.Get("Settings.Description");
        _refreshing = false; RestoreState();
    }

    private void RestoreState()
    {
        _refreshing = true;
        _anchor.SelectedIndex = (int)_state.Settings.AnchorCorner; _language.SelectedIndex = (int)_state.Settings.LanguageMode;
        _topmost.IsChecked = _state.Settings.AlwaysOnTop; _stayVisible.IsChecked = _state.Settings.StayVisibleOnFocusLoss;
        _runAtLogin.IsChecked = _state.Settings.RunAtLogin; _hotKey.Text = _state.Settings.HotKey ?? string.Empty;
        _registered.Text = string.Format(ProductStrings.Get("Settings.RegisteredHotKey"), _state.RegisteredHotKey ?? ProductStrings.Get("Settings.NotRegistered"));
        _startup.Text = ProductStrings.Get(_state.LoginStartupEnabled switch { true => "Settings.StartupEnabled", false => "Settings.StartupDisabled", _ => "Settings.StartupUnknown" });
        _status.Text = _state.Error is null ? ProductStrings.Get("Settings.AutoSave")
            : string.Format(ProductStrings.Get("Settings.ApplyFailed"), _state.Error);
        if (_state.Error is null) _status.ClearValue(TextBlock.ForegroundProperty);
        else _status.Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
        _refreshing = false;
    }

    private bool ApplyHotKey()
    {
        if (_refreshing || _busy || _hotKey.Text == (_state.Settings.HotKey ?? string.Empty)) return true;
        if (!HotKeyGesture.TryParse(_hotKey.Text, out var gesture, out var error))
        {
            _state = _readState() with { Error = error }; RestoreState(); return false;
        }
        BeginApply(s => s with { HotKey = gesture!.ToConfigString() });
        return _busy || _state.Error is null;
    }

    private void BeginApply(Func<WallSettings, WallSettings> change)
    {
        if (_refreshing || _busy) return;
        var candidate = change(_state.Settings);
        if (candidate == _state.Settings) return;
        _pending = ApplyAsync(candidate);
    }

    private async Task ApplyAsync(WallSettings candidate)
    {
        _busy = true; _formHost.IsEnabled = false; _status.Text = ProductStrings.Get("Settings.Saving");
        try { _state = await _apply(candidate); }
        catch (Exception ex) { _state = _readState() with { Error = string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message) }; }
        finally
        {
            _busy = false; _formHost.IsEnabled = true; RestoreState();
            _showFailureBeforeClose = _state.Error is not null;
            var closeAfterSuccess = _closeRequested && _state.Error is null;
            _closeRequested = false;
            if (closeAfterSuccess) { _forceClose = true; _dialog.Hide(); }
        }
    }

    private static void SetChoices(ComboBox box, string header, string[] keys)
    {
        box.Header = ProductStrings.Get(header); box.Items.Clear(); foreach (var key in keys) box.Items.Add(ProductStrings.Get(key));
    }
    private static void Id(DependencyObject item, string id) => AutomationProperties.SetAutomationId(item, "settings-" + id);
}
