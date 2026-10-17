using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Carousel;
using TileWall.Core.Components;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Resources;

namespace TileWall.Views;

internal sealed class ClockEditorDialog : IWallDialogSession
{
    private readonly ClockMember _draft;
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
    private readonly Dictionary<string, TextBlock> _labels = [];
    private bool _saved, _dirty, _allowDiscard, _discarded;

    public ClockEditorDialog(Window owner, ClockMember original, int maxHeight,
        IClock clock, Func<ClockMember, string?> save)
    {
        _draft = original;
        _maxHeight = Math.Clamp(maxHeight, _draft.LocalBounds.Height, 256); _clock = clock; _save = save;
        _title.Text = _draft.Visual.Title ?? string.Empty; _showTitle.IsChecked = _draft.Visual.ShowTitle;
        for (var i = 1; i <= 8; i++) _width.Items.Add(i.ToString());
        for (var i = 1; i <= _maxHeight; i++) _height.Items.Add(i.ToString());
        foreach (var key in new[] { "Clock.Time24", "Clock.Time24Seconds", "Clock.Time12", "Clock.Time12Seconds" }) _timeFormat.Items.Add(ProductStrings.Get(key));
        foreach (var key in new[] { "Clock.DateFull", "Clock.DateNumeric", "Clock.DateShort", "Clock.DateChinese" }) _dateFormat.Items.Add(ProductStrings.Get(key));
        _width.SelectedIndex = _draft.LocalBounds.Width - 1; _height.SelectedIndex = _draft.LocalBounds.Height - 1;
        _timeFormat.SelectedIndex = ClockFormatOptions.SupportedTimeFormats.ToList().IndexOf(_draft.TimeFormat);
        _dateFormat.SelectedIndex = ClockFormatOptions.SupportedDateFormats.ToList().IndexOf(_draft.DateFormat);
        var form = new Grid { RowSpacing = 12, ColumnSpacing = 16 };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddRow(form, "Common.Title", _title); AddRow(form, "Common.ShowTitle", _showTitle);
        AddRow(form, "Common.Width", _width); AddRow(form, "Common.Height", _height);
        AddRow(form, "Clock.TimeFormat", _timeFormat); AddRow(form, "Clock.DateFormat", _dateFormat);
        var preview = new StackPanel { Spacing = 4, Margin = new Thickness(0, 12, 0, 0) };
        preview.Children.Add(_time); preview.Children.Add(_date);
        var previewRow = form.RowDefinitions.Count; form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(preview, previewRow); Grid.SetColumnSpan(preview, 2); form.Children.Add(preview);
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

    public async Task<bool> ShowAsync() { await OwnedDialogWindow.ShowAsync(_dialog); return _saved; }
    public bool HasUnsavedDraft => _dirty;
    public Task WaitForPendingAsync() => Task.CompletedTask;
    public Task<ExitDecision> ConfirmExitAsync() => _exit.ConfirmAsync();
    public void DiscardDraft() { _discarded = true; _dirty = false; }
    public void RequestDismiss() => _dialog.Hide();
    public void Close() { _discarded = true; _dialog.Hide(); }

    public string? TrySaveNow()
    {
        var time = _timeFormat.SelectedIndex >= 0 ? ClockFormatOptions.SupportedTimeFormats[_timeFormat.SelectedIndex] : null;
        var date = _dateFormat.SelectedIndex >= 0 ? ClockFormatOptions.SupportedDateFormats[_dateFormat.SelectedIndex] : null;
        if (!ClockFormatOptions.IsValid(time, date)) return Error(ProductStrings.Get("Clock.FormatInvalid"));
        if (_width.SelectedIndex is < 0 or > 7 || _height.SelectedIndex < 0 || _height.SelectedIndex >= _maxHeight)
            return Error(ProductStrings.Get("Clock.SizeInvalid"));
        var value = _draft with { LocalBounds = _draft.LocalBounds with { Width = _width.SelectedIndex + 1, Height = _height.SelectedIndex + 1 },
            Visual = _draft.Visual with { TitleMode = MemberTitleMode.Manual, Title = _title.Text,
                ShowTitle = _showTitle.IsChecked == true },
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
        _dialog.Title = ProductStrings.Get("Clock.Edit");
        _dialog.PrimaryButtonText = ProductStrings.Get("Common.Save");
        _dialog.CloseButtonText = ProductStrings.Get(_allowDiscard ? "Common.Discard" : "Common.Cancel");
        foreach (var (key, label) in _labels) label.Text = ProductStrings.Get(key);
        AutomationProperties.SetName(_showTitle, ProductStrings.Get("Common.ShowTitle"));
        ActionIcon.SetTip(_showTitle, ProductStrings.Get("Common.ShowTitle"));
        ActionIcon.DecorateDialog(_dialog);
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
    private void AddRow(Grid form, string key, FrameworkElement value)
    {
        var row = form.RowDefinitions.Count; form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _labels.Add(key, label); AutomationProperties.SetLabeledBy(value, label);
        Grid.SetRow(label, row); form.Children.Add(label);
        value.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetRow(value, row); Grid.SetColumn(value, 1); form.Children.Add(value);
    }
    private static void Id(DependencyObject item, string id) => AutomationProperties.SetAutomationId(item, "clock-" + id);
}
