using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Carousel;
using TileWall.Core.Components;
using TileWall.Core.Grid;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Resources;

namespace TileWall.Views;

internal sealed class ClockEditorDialog : IWallDialogSession
{
    private readonly ClockMember _draft;
    private readonly bool _creating;
    private readonly int _maxHeight;
    private readonly IClock _clock;
    private readonly Func<ClockMember, string?> _save;
    private readonly ContentDialog _dialog;
    private readonly DialogExitPrompt _exit;
    private readonly TextBox _title = new() { IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
    private readonly CheckBox _showTitle = new();
    private readonly ComboBox _width = new(), _height = new(), _timeFormat = new(), _dateFormat = new();
    private readonly TextBlock _time = new() { FontSize = 34 }, _date = new() { TextWrapping = TextWrapping.Wrap },
        _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed) };
    private bool _saved, _dirty, _allowDiscard, _discarded;

    public ClockEditorDialog(Window owner, ClockMember? original, string memberId, int maxHeight,
        IClock clock, Func<ClockMember, string?> save)
    {
        _creating = original is null;
        _draft = original ?? new ClockMember { Id = memberId, LocalBounds = new GridRect(0, 0, 2, 2) };
        _maxHeight = Math.Clamp(maxHeight, _draft.LocalBounds.Height, 256); _clock = clock; _save = save;
        _title.Text = _draft.Visual.Title ?? string.Empty; _showTitle.IsChecked = _draft.Visual.ShowTitle;
        for (var i = 1; i <= 8; i++) _width.Items.Add(i.ToString());
        for (var i = 1; i <= _maxHeight; i++) _height.Items.Add(i.ToString());
        foreach (var key in new[] { "Clock.Time24", "Clock.Time24Seconds", "Clock.Time12", "Clock.Time12Seconds" }) _timeFormat.Items.Add(ProductStrings.Get(key));
        foreach (var key in new[] { "Clock.DateFull", "Clock.DateNumeric", "Clock.DateShort", "Clock.DateChinese" }) _dateFormat.Items.Add(ProductStrings.Get(key));
        _width.SelectedIndex = _draft.LocalBounds.Width - 1; _height.SelectedIndex = _draft.LocalBounds.Height - 1;
        _timeFormat.SelectedIndex = ClockFormatOptions.SupportedTimeFormats.ToList().IndexOf(_draft.TimeFormat);
        _dateFormat.SelectedIndex = ClockFormatOptions.SupportedDateFormats.ToList().IndexOf(_draft.DateFormat);
        var form = new StackPanel { Spacing = 10 };
        foreach (var item in new UIElement[] { _title, _showTitle, _width, _height, _timeFormat, _dateFormat, _time, _date }) form.Children.Add(item);
        var root = (owner.Content as FrameworkElement)?.XamlRoot;
        var body = new Grid { Width = Math.Min(400, Math.Max(260, (root?.Size.Width ?? 480) - 80)),
            MaxHeight = Math.Min(520, Math.Max(120, (root?.Size.Height ?? 730) - 210)) };
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var errorHost = new ScrollViewer { Content = _error, MaxHeight = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(errorHost, 1); body.Children.Add(errorHost);
        _dialog = new ContentDialog { XamlRoot = root, Content = body, DefaultButton = ContentDialogButton.None };
        _exit = new(_dialog);
        Id(_dialog, "editor"); Id(_title, "title"); Id(_showTitle, "show-title"); Id(_width, "width"); Id(_height, "height");
        Id(_timeFormat, "time-format"); Id(_dateFormat, "date-format"); Id(_time, "time-preview"); Id(_date, "date-preview"); Id(_error, "error");
        RefreshStrings(); RefreshPreview();
        _title.TextChanged += (_, _) => Changed();
        _showTitle.Checked += (_, _) => Changed(); _showTitle.Unchecked += (_, _) => Changed();
        foreach (var box in new[] { _width, _height, _timeFormat, _dateFormat }) box.SelectionChanged += (_, _) => Changed();
        _dialog.Opened += (_, _) => { _title.Focus(FocusState.Programmatic); RefreshPreview(); };
        _dialog.PrimaryButtonClick += (_, e) =>
        {
            if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.SaveAndExit); return; }
            e.Cancel = TrySaveNow() is not null;
        };
        _dialog.SecondaryButtonClick += (_, e) => { if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.DiscardAndExit); } };
        _dialog.CloseButtonClick += (_, e) =>
        {
            if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.CancelExit); return; }
            if (_dirty && !_allowDiscard) { e.Cancel = true; OfferDiscard(); } else _discarded = true;
        };
        _dialog.Closing += (_, e) =>
        {
            if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.CancelExit); }
            else if (!_saved && !_discarded && _dirty) { e.Cancel = true; OfferDiscard(); }
        };
    }

    public async Task<bool> ShowAsync() { await _dialog.ShowAsync(); return _saved; }
    public bool HasUnsavedDraft => _dirty;
    public Task WaitForPendingAsync() => Task.CompletedTask;
    public Task<ExitDecision> ConfirmExitAsync() => _exit.ConfirmAsync();
    public void DiscardDraft() { _discarded = true; _dirty = false; }
    public void Close() { _discarded = true; _dialog.Hide(); }

    public string? TrySaveNow()
    {
        var time = _timeFormat.SelectedIndex >= 0 ? ClockFormatOptions.SupportedTimeFormats[_timeFormat.SelectedIndex] : null;
        var date = _dateFormat.SelectedIndex >= 0 ? ClockFormatOptions.SupportedDateFormats[_dateFormat.SelectedIndex] : null;
        if (!ClockFormatOptions.IsValid(time, date)) return Error(ProductStrings.Get("Clock.FormatInvalid"));
        if (_width.SelectedIndex is < 0 or > 7 || _height.SelectedIndex < 0 || _height.SelectedIndex >= _maxHeight)
            return Error(ProductStrings.Get("Clock.SizeInvalid"));
        var value = _draft with { LocalBounds = _draft.LocalBounds with { Width = _width.SelectedIndex + 1, Height = _height.SelectedIndex + 1 },
            Visual = _draft.Visual with { Title = _title.Text, ShowTitle = _showTitle.IsChecked == true },
            TimeFormat = time!, DateFormat = date!, Entry = null };
        try
        {
            if (_save(value) is { } error) return Error(error);
            _saved = true; _dirty = false; return null;
        }
        catch (Exception ex) { return Error(string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message)); }
    }

    public void RefreshStrings()
    {
        if (_exit.IsActive) return;
        _dialog.Title = ProductStrings.Get(_creating ? "Clock.Create" : "Clock.Edit");
        _dialog.PrimaryButtonText = ProductStrings.Get("Common.Save");
        _dialog.CloseButtonText = ProductStrings.Get(_allowDiscard ? "Common.Discard" : "Common.Cancel");
        _title.Header = ProductStrings.Get("Common.Title"); _showTitle.Content = ProductStrings.Get("Common.ShowTitle");
        _width.Header = ProductStrings.Get("Common.Width"); _height.Header = ProductStrings.Get("Common.Height");
        _timeFormat.Header = ProductStrings.Get("Clock.TimeFormat"); _dateFormat.Header = ProductStrings.Get("Clock.DateFormat");
    }
    private void Changed() { _dirty = true; _allowDiscard = false; _discarded = false; RefreshStrings(); RefreshPreview(); }
    private void OfferDiscard() { _allowDiscard = true; _error.Text = ProductStrings.Get("Common.UnsavedDraft"); RefreshStrings(); }
    private string Error(string error) { _error.Text = error; return error; }
    private void RefreshPreview()
    {
        var value = ClockTextFormatter.Format(_clock.UtcNow,
            _timeFormat.SelectedIndex >= 0 ? ClockFormatOptions.SupportedTimeFormats[_timeFormat.SelectedIndex] : null,
            _dateFormat.SelectedIndex >= 0 ? ClockFormatOptions.SupportedDateFormats[_dateFormat.SelectedIndex] : null);
        _time.Text = value.TimeLine; _date.Text = value.DateLine;
    }
    private static void Id(DependencyObject item, string id) => AutomationProperties.SetAutomationId(item, "clock-" + id);
}
