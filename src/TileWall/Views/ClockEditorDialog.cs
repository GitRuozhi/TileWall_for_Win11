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
    private readonly WallMetrics _metrics;
    private readonly WallVisualStyle _style;
    private readonly double _rasterScale;
    private readonly Func<ClockMember, string?> _save;
    private readonly ContentDialog _dialog;
    private readonly DialogExitPrompt _exit;
    private readonly TextBox _title = new() { IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
    private readonly CheckBox _showTitle = new();
    private readonly Viewbox _preview = new() { Height = 150, Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _width = new(), _height = new(), _timeFormat = new(), _dateFormat = new();
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed) };
    private readonly Dictionary<string, TextBlock> _labels = [];
    private bool _saved, _dirty, _allowDiscard, _discarded, _titleEdited;

    public ClockEditorDialog(Window owner, ClockMember original, int maxHeight, IClock clock,
        WallMetrics metrics, WallVisualStyle style, double rasterScale, Func<ClockMember, string?> save)
    {
        _draft = original;
        _maxHeight = Math.Max(_draft.LocalBounds.Height, Math.Clamp(maxHeight, 1, WallLayoutEngine.MaxRows));
        _clock = clock; _metrics = metrics; _style = style; _rasterScale = Math.Max(0.1, rasterScale); _save = save;
        var initialTitle = WallMemberVisualFactory.ResolveTitle(_draft);
        _title.Text = initialTitle; _showTitle.IsChecked = _draft.Visual.ShowTitle;
        for (var i = 1; i <= WallLayoutEngine.CellsPerLane; i++) _width.Items.Add(i.ToString());
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
        _preview.Margin = new Thickness(0, 12, 0, 0);
        var previewRow = form.RowDefinitions.Count; form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_preview, previewRow); Grid.SetColumnSpan(_preview, 2); form.Children.Add(_preview);
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
        Id(_timeFormat, "time-format"); Id(_dateFormat, "date-format"); Id(_preview, "clock-preview"); Id(_error, "error");
        RefreshStrings(); RefreshPreview();
        _title.TextChanged += (_, _) =>
        {
            _titleEdited = !string.Equals(_title.Text, initialTitle, StringComparison.Ordinal);
            Changed();
        };
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
        if (_width.SelectedIndex < 0 || _width.SelectedIndex >= WallLayoutEngine.CellsPerLane ||
            _height.SelectedIndex < 0 || _height.SelectedIndex >= _maxHeight)
            return Error(ProductStrings.Get("Clock.SizeInvalid"));
        var visual = _draft.Visual with { ShowTitle = _showTitle.IsChecked == true };
        if (_titleEdited)
            visual = visual with { TitleMode = MemberTitleMode.Manual, Title = _title.Text };
        var value = _draft with { LocalBounds = _draft.LocalBounds with { Width = _width.SelectedIndex + 1, Height = _height.SelectedIndex + 1 },
            Visual = visual,
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
        var visual = _draft.Visual with { ShowTitle = _showTitle.IsChecked == true };
        if (_titleEdited) visual = visual with { TitleMode = MemberTitleMode.Manual, Title = _title.Text };
        var member = _draft with
        {
            LocalBounds = _draft.LocalBounds with
            {
                Width = _width.SelectedIndex >= 0 ? _width.SelectedIndex + 1 : _draft.LocalBounds.Width,
                Height = _height.SelectedIndex >= 0 ? _height.SelectedIndex + 1 : _draft.LocalBounds.Height,
            },
            Visual = visual,
            TimeFormat = _timeFormat.SelectedIndex >= 0
                ? ClockFormatOptions.SupportedTimeFormats[_timeFormat.SelectedIndex] : _draft.TimeFormat,
            DateFormat = _dateFormat.SelectedIndex >= 0
                ? ClockFormatOptions.SupportedDateFormats[_dateFormat.SelectedIndex] : _draft.DateFormat,
        };
        var clock = new ClockMemberView(member, _metrics, _rasterScale, _style,
            revealTitles: true, initialUtcNow: _clock.UtcNow);
        Id(clock.TimeText, "time-preview"); Id(clock.DateText, "date-preview");
        _preview.Child = clock;
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
