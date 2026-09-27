using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Storage;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;
using Windows.Storage.Pickers;
using Windows.UI;

namespace TileWall.Views;

internal sealed record TileEditSubmission(TileMember Tile, IReadOnlyList<EntryChange> EntryChanges);

/// <summary>Native modal draft. Nothing reaches WallSession until Save succeeds.</summary>
internal sealed class TileEditorDialog : IWallDialogSession
{
    private readonly Window _owner;
    private readonly TileMember? _original;
    private readonly string _id;
    private readonly string? _originalTarget;
    private readonly bool _idList;
    private readonly bool _opaqueEntry;
    private readonly int _maxHeight;
    private readonly Func<TileEditSubmission, string?> _save;
    private readonly WallImageService _images;
    private readonly string? _entryPath;
    private readonly ContentDialog _dialog;
    private readonly DialogExitPrompt _exit;
    private readonly TextBox _title;
    private readonly CheckBox _showTitle;
    private readonly ComboBox _width;
    private readonly ComboBox _height;
    private readonly ComboBox _entryKind;
    private readonly TextBox _target;
    private readonly ComboBox _foreground;
    private readonly TextBox _customIcon;
    private readonly ComboBox _backgroundKind;
    private readonly ColorPicker _backgroundColor;
    private readonly TextBox _backgroundImage;
    private readonly Border _preview;
    private readonly TextBlock _previewText;
    private readonly TextBlock _previewTitleText;
    private readonly Border _previewTitleBand;
    private readonly Image _previewBackgroundImage;
    private readonly Image _previewForegroundImage;
    private readonly TextBlock _error;
    private readonly ScrollViewer _errorHost;
    private readonly Button _browseTarget;
    private readonly Button _browseIcon;
    private readonly Button _browseBackground;
    private bool _saved;
    private bool _allowDiscard;
    private CancellationTokenSource? _previewCancellation;
    private long _previewGeneration;
    private Task _pickerTask = Task.CompletedTask;

    public TileEditorDialog(Window owner, TileMember? original, string memberId,
        string? originalTarget, bool idList, bool opaqueEntry, string? entryDiagnostic,
        int maxHeight, WallImageService images, string? entryPath,
        Func<TileEditSubmission, string?> save)
    {
        _owner = owner;
        _original = original;
        _id = memberId;
        _originalTarget = originalTarget;
        _idList = idList;
        _opaqueEntry = opaqueEntry;
        _maxHeight = Math.Clamp(maxHeight, original?.LocalBounds.Height ?? 1, 256);
        _save = save;
        _images = images;
        _entryPath = entryPath;

        _title = new TextBox { Header = ProductStrings.Get("Entry.Title"), Text = original?.Visual.Title ?? string.Empty,
            PlaceholderText = ProductStrings.Get("Entry.EmptyTitleHint"),
            IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
        _showTitle = new CheckBox { Content = ProductStrings.Get("Tile.ShowTitle"), IsChecked = original?.Visual.ShowTitle ?? true };
        _width = Choice(ProductStrings.Get("Common.Width"), 1, 8, original?.LocalBounds.Width ?? 1);
        _height = Choice(ProductStrings.Get("Common.Height"), 1, _maxHeight, original?.LocalBounds.Height ?? 1);
        _entryKind = Choice(ProductStrings.Get("Entry.Kind"), [ProductStrings.Get("Entry.NoneTile"), ProductStrings.Get("Entry.Path"), ProductStrings.Get("Entry.Copy"), ProductStrings.Get("Entry.Url")], 0);
        _target = new TextBox { Header = ProductStrings.Get("Entry.Target"), Text = originalTarget ?? string.Empty,
            PlaceholderText = ProductStrings.Get("Entry.TargetHint"),
            IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
        _browseTarget = new Button { Content = ProductStrings.Get("Entry.Browse") };
        _browseTarget.Click += (_, _) => _pickerTask = PickIntoAsync(_target);
        _foreground = Choice(ProductStrings.Get("Tile.Foreground"), [ProductStrings.Get("Tile.AutoIcon"), ProductStrings.Get("Tile.CustomImage"), ProductStrings.Get("Tile.NoForeground")],
            original?.Foreground switch { CustomImageIcon => 1, NoForegroundIcon => 2, _ => 0 });
        _customIcon = new TextBox { Header = ProductStrings.Get("Tile.CustomImagePath"),
            Text = (original?.Foreground as CustomImageIcon)?.SourcePath ?? string.Empty,
            IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
        _browseIcon = new Button { Content = ProductStrings.Get("Tile.BrowseForeground") };
        _browseIcon.Click += (_, _) => _pickerTask = PickIntoAsync(_customIcon);
        _backgroundKind = Choice(ProductStrings.Get("Tile.Background"), [ProductStrings.Get("Tile.SystemColor"), ProductStrings.Get("Tile.CustomColor"), ProductStrings.Get("Tile.Image")],
            original?.Visual.BackgroundImagePath is not null ? 2
                : original?.Visual.BackgroundColor is not null ? 1 : 0);
        _backgroundColor = new ColorPicker
        {
            IsAlphaEnabled = false, IsMoreButtonVisible = false,
            Color = WallSurface.ParseColor(original?.Visual.BackgroundColor,
                WallSurface.SystemAccentColor()),
        };
        _backgroundImage = new TextBox { Header = ProductStrings.Get("Tile.BackgroundPath"),
            Text = original?.Visual.BackgroundImagePath ?? string.Empty,
            IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
        _browseBackground = new Button { Content = ProductStrings.Get("Tile.BrowseBackground") };
        _browseBackground.Click += (_, _) => _pickerTask = PickIntoAsync(_backgroundImage);
        _previewText = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, FontSize = 26,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
        _previewTitleText = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
        _previewTitleBand = new Border { Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)),
            VerticalAlignment = VerticalAlignment.Bottom, Height = 23,
            Padding = new Thickness(5, 2, 5, 2), Child = _previewTitleText };
        _previewBackgroundImage = new Image { Stretch = Stretch.UniformToFill };
        _previewForegroundImage = new Image { Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var previewLayers = new Grid();
        previewLayers.Children.Add(_previewBackgroundImage);
        previewLayers.Children.Add(_previewForegroundImage);
        previewLayers.Children.Add(_previewText);
        previewLayers.Children.Add(_previewTitleBand);
        _preview = new Border { Width = 100, Height = 100, Child = previewLayers };
        _error = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            TextWrapping = TextWrapping.Wrap };
        _errorHost = new ScrollViewer
        {
            Content = _error,
            MaxHeight = 96,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed,
        };

        var content = new StackPanel { Spacing = 9, MinWidth = 390 };
        content.Children.Add(_title);
        content.Children.Add(_showTitle);
        var dimensions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        dimensions.Children.Add(_width);
        dimensions.Children.Add(_height);
        content.Children.Add(dimensions);
        content.Children.Add(_entryKind);
        content.Children.Add(_target);
        content.Children.Add(_browseTarget);
        content.Children.Add(_foreground);
        content.Children.Add(_customIcon);
        content.Children.Add(_browseIcon);
        content.Children.Add(_backgroundKind);
        content.Children.Add(_backgroundColor);
        content.Children.Add(_backgroundImage);
        content.Children.Add(_browseBackground);
        content.Children.Add(new TextBlock { Text = ProductStrings.Get("Common.Preview") });
        content.Children.Add(_preview);

        var dialogContent = new Grid { MaxHeight = 540, MinWidth = 390 };
        dialogContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        dialogContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var formScroll = new ScrollViewer { Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(formScroll, 0);
        Grid.SetRow(_errorHost, 1);
        dialogContent.Children.Add(formScroll);
        dialogContent.Children.Add(_errorHost);

        _dialog = new ContentDialog
        {
            XamlRoot = (owner.Content as FrameworkElement)?.XamlRoot,
            Title = original is null ? ProductStrings.Get("Tile.Create") : ProductStrings.Get("Tile.Edit"),
            Content = dialogContent,
            PrimaryButtonText = ProductStrings.Get("Common.Save"), CloseButtonText = ProductStrings.Get("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        _exit = new(_dialog);
        AutomationProperties.SetAutomationId(_dialog, "tile-editor");
        AutomationProperties.SetAutomationId(_title, "tile-title");
        AutomationProperties.SetAutomationId(_showTitle, "tile-show-title");
        AutomationProperties.SetAutomationId(_width, "tile-width");
        AutomationProperties.SetAutomationId(_height, "tile-height");
        AutomationProperties.SetAutomationId(_entryKind, "tile-entry-kind");
        AutomationProperties.SetAutomationId(_target, "tile-target");
        AutomationProperties.SetAutomationId(_browseTarget, "tile-target-browse");
        AutomationProperties.SetAutomationId(_foreground, "tile-foreground");
        AutomationProperties.SetAutomationId(_customIcon, "tile-custom-image");
        AutomationProperties.SetAutomationId(_browseIcon, "tile-custom-browse");
        AutomationProperties.SetAutomationId(_backgroundKind, "tile-background-kind");
        AutomationProperties.SetAutomationId(_backgroundColor, "tile-background-color");
        AutomationProperties.SetAutomationId(_backgroundImage, "tile-background-image");
        AutomationProperties.SetAutomationId(_browseBackground, "tile-background-browse");
        _dialog.PrimaryButtonClick += OnSave;
        _dialog.SecondaryButtonClick += (_, e) => { if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.DiscardAndExit); } };
        _dialog.CloseButtonClick += (_, e) => { if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.CancelExit); } };
        _dialog.Closing += OnClosing;
        _dialog.Opened += (_, _) => { _title.Focus(FocusState.Programmatic); RefreshPreview(); };
        _title.TextChanged += (_, _) => { RefreshPreview(); DraftChanged(); };
        _showTitle.Checked += (_, _) => { RefreshPreview(); DraftChanged(); };
        _showTitle.Unchecked += (_, _) => { RefreshPreview(); DraftChanged(); };
        _backgroundColor.ColorChanged += (_, _) => { RefreshPreview(); DraftChanged(); };
        _width.SelectionChanged += (_, _) => { RefreshPreview(); DraftChanged(); };
        _height.SelectionChanged += (_, _) => { RefreshPreview(); DraftChanged(); };
        _entryKind.SelectionChanged += (_, _) => RefreshVisibility();
        _foreground.SelectionChanged += (_, _) => RefreshVisibility();
        _backgroundKind.SelectionChanged += (_, _) => RefreshVisibility();
        _target.TextChanged += (_, _) => { DraftChanged(); RefreshPreview(); };
        _customIcon.TextChanged += (_, _) => { DraftChanged(); RefreshPreview(); };
        _backgroundImage.TextChanged += (_, _) => { DraftChanged(); RefreshPreview(); };
        InitializeEntryKind();
        RefreshVisibility();
        RefreshPreview();
        if (!string.IsNullOrWhiteSpace(entryDiagnostic)) ShowError(entryDiagnostic);
    }

    public async Task<bool> ShowAsync()
    {
        await _dialog.ShowAsync();
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        return _saved;
    }

    public bool HasUnsavedDraft => IsDirty();
    public Task WaitForPendingAsync() => _pickerTask;
    public Task<ExitDecision> ConfirmExitAsync() => _exit.ConfirmAsync();
    public void DiscardDraft() => _allowDiscard = true;
    public void Close() { _allowDiscard = true; _dialog.Hide(); }
    public void RefreshStrings()
    {
        if (_exit.IsActive) return;
        _dialog.Title = ProductStrings.Get(_original is null ? "Tile.Create" : "Tile.Edit");
        _dialog.PrimaryButtonText = ProductStrings.Get("Common.Save");
        _dialog.CloseButtonText = ProductStrings.Get(_allowDiscard ? "Common.Discard" : "Common.Cancel");
    }

    private void InitializeEntryKind()
    {
        _entryKind.SelectedIndex = _original?.Entry?.Kind switch
        {
            ManagedEntryKind.Url => 3,
            ManagedEntryKind.Lnk => 1,
            _ => 0,
        };
        _target.IsReadOnly = _idList && _entryKind.SelectedIndex == 1;
    }

    private void RefreshVisibility()
    {
        _target.Visibility = _entryKind.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
        _target.IsReadOnly = _idList && _entryKind.SelectedIndex == 1;
        _browseTarget.Visibility = _entryKind.SelectedIndex is 1 or 2 ? Visibility.Visible : Visibility.Collapsed;
        _customIcon.Visibility = _foreground.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        _browseIcon.Visibility = _customIcon.Visibility;
        _backgroundColor.Visibility = _backgroundKind.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        _backgroundImage.Visibility = _backgroundKind.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        _browseBackground.Visibility = _backgroundImage.Visibility;
        DraftChanged();
        RefreshPreview();
    }

    private void DraftChanged()
    {
        if (!_allowDiscard) return;
        _allowDiscard = false;
        _dialog.CloseButtonText = ProductStrings.Get("Common.Cancel");
    }

    private void RefreshPreview()
    {
        if (_preview is null || _backgroundColor is null) return;
        _preview.Background = new SolidColorBrush(_backgroundKind.SelectedIndex == 1
            ? _backgroundColor.Color : WallSurface.SystemAccentColor());
        _preview.Width = 48 * Math.Max(1, _width.SelectedIndex + 1) + 4 * Math.Max(0, _width.SelectedIndex);
        _preview.Height = 48 * Math.Max(1, _height.SelectedIndex + 1) + 4 * Math.Max(0, _height.SelectedIndex);
        _previewTitleText.Text = _title.Text;
        _previewTitleBand.Visibility = _showTitle.IsChecked == true && !string.IsNullOrWhiteSpace(_title.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        _previewText.Text = _foreground.SelectedIndex == 2 ? string.Empty : _entryKind.SelectedIndex == 0 ? "◇" : "▣";
        _previewText.Visibility = Visibility.Visible;
        _previewForegroundImage.Width = Math.Min(_preview.Width, _preview.Height) * 0.62;
        _previewForegroundImage.Height = _previewForegroundImage.Width;
        _ = RefreshImagePreviewAsync();
    }

    private async Task RefreshImagePreviewAsync()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        var generation = ++_previewGeneration;
        _previewBackgroundImage.Source = null;
        _previewForegroundImage.Source = null;
        try
        {
            var scale = (_owner.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1;
            if (_backgroundKind.SelectedIndex == 2 && !string.IsNullOrWhiteSpace(_backgroundImage.Text))
            {
                var result = await _images.LoadBackgroundAsync(_backgroundImage.Text.Trim(),
                    Math.Max(1, (int)Math.Ceiling(_preview.Width * scale)),
                    Math.Max(1, (int)Math.Ceiling(_preview.Height * scale)), cancellation.Token);
                if (generation == _previewGeneration && !cancellation.IsCancellationRequested)
                    _previewBackgroundImage.Source = result.Source;
            }
            if (_foreground.SelectedIndex == 2) return;
            ForegroundChoice foreground = _foreground.SelectedIndex == 1
                ? new CustomImageIcon(_customIcon.Text.Trim()) : new AutoEntryIcon();
            var tile = new TileMember
            {
                Id = _id, LocalBounds = new GridRect(0, 0, 1, 1), Foreground = foreground,
            };
            var path = _entryKind.SelectedIndex == 0 ? null
                : _entryKind.SelectedIndex == 1 && _original?.Entry is not null &&
                _target.Text == (_originalTarget ?? string.Empty) ? _entryPath : _target.Text.Trim();
            var size = Math.Max(1, (int)Math.Ceiling(_previewForegroundImage.Width * scale));
            var icon = await _images.LoadForegroundAsync(tile, path, size, size, cancellation.Token);
            if (generation == _previewGeneration && !cancellation.IsCancellationRequested)
            {
                _previewForegroundImage.Source = icon.Source;
                _previewText.Visibility = icon.Source is null ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Preview failure never changes the draft. */ }
    }

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_exit.IsActive) { args.Cancel = true; _exit.Resolve(ExitDecision.SaveAndExit); return; }
        args.Cancel = TrySaveNow() is not null;
    }

    public string? TrySaveNow()
    {
        var validation = BuildSubmission();
        if (validation.Submission is null)
        {
            var validationError = validation.Error ?? ProductStrings.Get("Common.CheckInput");
            ShowError(validationError);
            return validationError;
        }
        string? error;
        try { error = _save(validation.Submission); }
        catch (Exception ex) { error = string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message); }
        if (error is not null)
        {
            ShowError(error);
            return error;
        }
        _saved = true;
        return null;
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_exit.IsActive) { args.Cancel = true; _exit.Resolve(ExitDecision.CancelExit); return; }
        if (_saved || _allowDiscard || args.Result == ContentDialogResult.Primary) return;
        if (!IsDirty()) return;
        args.Cancel = true;
        _allowDiscard = true;
        _dialog.CloseButtonText = ProductStrings.Get("Common.Discard");
        ShowError(ProductStrings.Get("Tile.UnsavedDraft"));
    }

    private bool IsDirty() => _title.Text != (_original?.Visual.Title ?? string.Empty)
        || _showTitle.IsChecked != (_original?.Visual.ShowTitle ?? true)
        || _width.SelectedIndex + 1 != (_original?.LocalBounds.Width ?? 1)
        || _height.SelectedIndex + 1 != (_original?.LocalBounds.Height ?? 1)
        || _entryKind.SelectedIndex != (_original?.Entry?.Kind switch
            { ManagedEntryKind.Url => 3, ManagedEntryKind.Lnk => 1, _ => 0 })
        || _foreground.SelectedIndex != (_original?.Foreground switch
            { CustomImageIcon => 1, NoForegroundIcon => 2, _ => 0 })
        || _backgroundKind.SelectedIndex != (_original?.Visual.BackgroundImagePath is not null ? 2
            : _original?.Visual.BackgroundColor is not null ? 1 : 0)
        || _target.Text != (_originalTarget ?? string.Empty)
        || _customIcon.Text != ((_original?.Foreground as CustomImageIcon)?.SourcePath ?? string.Empty)
        || _backgroundImage.Text != (_original?.Visual.BackgroundImagePath ?? string.Empty)
        || _backgroundColor.Color != WallSurface.ParseColor(_original?.Visual.BackgroundColor,
            WallSurface.SystemAccentColor());

    private (TileEditSubmission? Submission, string? Error) BuildSubmission()
    {
        var target = _target.Text.Trim();
        var mode = _entryKind.SelectedIndex;
        var unchangedExisting = _original?.Entry is not null &&
            mode == (_original.Entry.Kind == ManagedEntryKind.Lnk ? 1 : 3) &&
            target == (_originalTarget ?? string.Empty);
        var unchangedOpaque = _opaqueEntry && unchangedExisting;
        if (mode != 0 && target.Length == 0 && !unchangedOpaque)
            return (null, ProductStrings.Get("Entry.EmptyTileTarget"));
        if (mode is 1 or 2 && !unchangedExisting && target.Length > 0 &&
            !Path.IsPathFullyQualified(target))
            return (null, ProductStrings.Get("Entry.FullPathRequired"));
        if (mode == 2 && !new[] { ".lnk", ".url" }.Contains(Path.GetExtension(target), StringComparer.OrdinalIgnoreCase))
            return (null, ProductStrings.Get("Entry.ShortcutOnly"));
        if (mode == 3 && !unchangedExisting && UrlShortcut.ValidateUrl(target).Count != 0)
            return (null, ProductStrings.Get("Entry.InvalidUrl"));
        if (mode != 0 && !string.IsNullOrEmpty(_title.Text) && EntryNames.Validate(_title.Text).Count != 0)
            return (null, ProductStrings.Get("Entry.InvalidTitle"));
        if (_foreground.SelectedIndex == 1 && string.IsNullOrWhiteSpace(_customIcon.Text))
            return (null, ProductStrings.Get("Tile.ForegroundRequired"));
        if (_backgroundKind.SelectedIndex == 2 && string.IsNullOrWhiteSpace(_backgroundImage.Text))
            return (null, ProductStrings.Get("Tile.BackgroundRequired"));

        ForegroundChoice foreground = _foreground.SelectedIndex switch
        {
            1 => new CustomImageIcon(_customIcon.Text.Trim()),
            2 => new NoForegroundIcon(),
            _ => new AutoEntryIcon(),
        };
        var color = _backgroundColor.Color;
        var tile = new TileMember
        {
            Id = _id,
            LocalBounds = new GridRect(_original?.LocalBounds.Column ?? 0,
                _original?.LocalBounds.Row ?? 0, _width.SelectedIndex + 1, _height.SelectedIndex + 1),
            Visual = new MemberVisual
            {
                Title = _title.Text,
                ShowTitle = _showTitle.IsChecked == true,
                BackgroundColor = _backgroundKind.SelectedIndex == 1
                    ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : null,
                BackgroundImagePath = _backgroundKind.SelectedIndex == 2 ? _backgroundImage.Text.Trim() : null,
            },
            Foreground = foreground,
            Entry = mode == 0 ? null : _original?.Entry,
        };
        var changes = new List<EntryChange>();
        if (mode == 0)
        {
            if (_original?.Entry is not null) changes.Add(new EntryChange.Remove(_id));
        }
        else if (_original?.Entry is null || mode == 2 ||
            mode == 3 && _original.Entry.Kind != ManagedEntryKind.Url ||
            mode == 1 && _original.Entry.Kind != ManagedEntryKind.Lnk)
        {
            changes.Add(mode switch
            {
                2 => new EntryChange.CopyShortcut(_id, target),
                3 => new EntryChange.CreateUrl(_id, target),
                _ => new EntryChange.CreateLink(_id, target),
            });
        }
        else if (target != (_originalTarget ?? string.Empty))
        {
            if (_idList) return (null, ProductStrings.Get("Entry.IdListTarget"));
            changes.Add(new EntryChange.ChangeTarget(_id, target));
        }
        if (_original?.Entry is not null && mode != 0 && mode != 2 &&
            !changes.Any(c => c is EntryChange.CreateLink or EntryChange.CreateUrl) &&
            _title.Text != _original.Visual.Title && !string.IsNullOrEmpty(_title.Text))
            changes.Add(new EntryChange.Rename(_id));
        return (new TileEditSubmission(tile, changes), null);
    }

    private void ShowError(string value)
    {
        _error.Text = value;
        _errorHost.Visibility = Visibility.Visible;
        if (_errorHost.IsLoaded) _errorHost.ChangeView(null, 0, null);
    }

    private async Task PickIntoAsync(TextBox box)
    {
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));
            picker.FileTypeFilter.Add("*");
            var selected = await picker.PickSingleFileAsync();
            if (selected is not null) { box.Text = selected.Path; box.Focus(FocusState.Programmatic); }
        }
        catch (Exception ex) { ShowError(string.Format(ProductStrings.Get("Entry.PickerFailed"), ex.Message)); }
    }

    private static ComboBox Choice(string header, int first, int last, int selected)
    {
        var box = new ComboBox { Header = header, MinWidth = 115 };
        for (var value = first; value <= last; value++) box.Items.Add(value.ToString());
        box.SelectedIndex = Math.Clamp(selected - first, 0, last - first);
        return box;
    }

    private static ComboBox Choice(string header, string[] values, int selected)
    {
        var box = new ComboBox { Header = header, MinWidth = 230 };
        foreach (var value in values) box.Items.Add(value);
        box.SelectedIndex = selected;
        return box;
    }
}

