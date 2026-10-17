using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using TileWall.ApplicationModel;
using TileWall.Core.Import;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;

namespace TileWall.Views;

/// <summary>A projected candidate draft. The host keeps the existing single atomic replacement commit.</summary>
internal sealed class ImportDialog : IWallDialogSession
{
    private readonly ContentDialog _dialog;
    private readonly DialogExitPrompt _exit;
    private readonly WallImageService _images;
    private readonly Func<StartMenuScanResult, IReadOnlyList<StartMenuImportCandidate>> _prepare;
    private readonly Func<IReadOnlyList<ImportBatchItem>, bool, string?> _save;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<CancellationTokenSource> _rowRequests = [];
    private readonly CheckBox _selectAll = new() { IsThreeState = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _source = new() { IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _autoTrim = new() { IsOn = true, OnContent = string.Empty, OffContent = string.Empty };
    private readonly ToggleSwitch _recognizeGroups = new() { IsOn = true, OnContent = string.Empty, OffContent = string.Empty };
    private readonly ToggleButton _small = new(), _medium = new();
    private readonly TextBlock _sourceLabel = Label(), _trimLabel = Label(), _groupsLabel = Label(), _sizeLabel = Label();
    private readonly WallMetrics _metrics = new();
    private readonly WallVisualStyle _style;
    private readonly Dictionary<string, (Border Outline, Border Check)> _selectionVisuals = new(StringComparer.Ordinal);
    private readonly Canvas _groupOverlay = new() { IsHitTestVisible = false };
    private GridViewHeaderItem? _hoveredHeader;
    private readonly GridView _grid = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImportBatchItem> _items = new(StringComparer.Ordinal);
    private IReadOnlyList<StartMenuImportCandidate> _candidates = [];
    private IReadOnlyList<StartMenuImportCandidate> _visibleCandidates = [];
    private Task _pending = Task.CompletedTask;
    private bool _saved, _closed, _allowDiscard, _discarded, _scanComplete, _scanReceived, _scanFailed;
    private int _tileSize = 2, _skipped;

    public ImportDialog(Window owner, WallImageService images,
        Func<StartMenuScanResult, IReadOnlyList<StartMenuImportCandidate>> prepare,
        Func<IReadOnlyList<ImportBatchItem>, bool, string?> save, WallVisualStyle style)
    {
        _images = images; _prepare = prepare; _save = save; _style = style;
        RefreshItemTemplate();
        _grid.Width = _metrics.LaneWidth + _metrics.Gap + 16;
        _grid.Padding = new Thickness(0);
        _grid.ItemContainerStyle = (Style)XamlReader.Load("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="GridViewItem">
                <Setter Property="Padding" Value="0" /><Setter Property="Margin" Value="0" />
                <Setter Property="MinWidth" Value="0" /><Setter Property="MinHeight" Value="0" />
                <Setter Property="HorizontalContentAlignment" Value="Left" />
                <Setter Property="VerticalContentAlignment" Value="Top" />
            </Style>
            """);
        foreach (var key in new[] { "GridViewItemBackgroundPointerOver", "GridViewItemBackgroundPressed",
                     "GridViewItemBackgroundSelected", "GridViewItemBorderBrushPointerOver" })
            _grid.Resources[key] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _grid.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("""
            <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <ItemsWrapGrid Orientation="Horizontal" />
            </ItemsPanelTemplate>
            """);
        // The stock GridViewHeaderItem adds 8 DIP top padding, an 8+1 DIP
        // divider and 4 DIP bottom margin. The shared wall header already owns
        // its complete 48 DIP title reservation plus 4 DIP member gap.
        var headerContainerStyle = (Style)XamlReader.Load("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="GridViewHeaderItem">
                <Setter Property="Padding" Value="0" /><Setter Property="Margin" Value="0" />
                <Setter Property="MinWidth" Value="0" /><Setter Property="MinHeight" Value="0" />
                <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                <Setter Property="VerticalContentAlignment" Value="Top" />
                <Setter Property="IsTabStop" Value="False" />
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="GridViewHeaderItem">
                            <ContentPresenter x:Name="ContentPresenter"
                                Content="{TemplateBinding Content}"
                                ContentTemplate="{TemplateBinding ContentTemplate}"
                                ContentTransitions="{TemplateBinding ContentTransitions}"
                                HorizontalContentAlignment="{TemplateBinding HorizontalContentAlignment}"
                                VerticalContentAlignment="{TemplateBinding VerticalContentAlignment}" />
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
            """);
        _grid.GroupStyle.Add(new GroupStyle
        {
            HeaderTemplate = WallGroupVisualFactory.HeaderTemplate(_metrics),
            HeaderContainerStyle = headerContainerStyle,
        });
        _grid.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnCandidatePointerMoved), true);
        _grid.PointerExited += (_, _) => ClearGroupOutline();

        var budget = OwnedDialogWindow.ContentBudget(owner);
        var width = Math.Min(900, budget.Width);
        var height = Math.Min(620, budget.Height);
        var rightWidth = Math.Min(420, width * 0.48);
        _grid.Width = Math.Min(_grid.Width, width - rightWidth - 24);

        var options = new Grid { RowSpacing = 12, ColumnSpacing = 12 };
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddRow(options, "Import.Source", _sourceLabel, _source);
        AddOptionRow(options, _selectAll);
        _selectAll.Content = ProductStrings.Get("Import.SelectAll");
        AddRow(options, "Import.AutoTrim", _trimLabel, _autoTrim);
        AddRow(options, "Import.RecognizeGroups", _groupsLabel, _recognizeGroups);
        var sizes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sizes.Children.Add(_small); sizes.Children.Add(_medium);
        AddRow(options, "Editor.TileSize", _sizeLabel, sizes);

        var rightCard = VisualStyles.Card(options);
        rightCard.Width = rightWidth;
        rightCard.HorizontalAlignment = HorizontalAlignment.Left;
        var rightScroll = new ScrollViewer { Content = rightCard, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var left = new Grid { RowSpacing = 8 };
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // ItemsWrapGrid flattens grouped headers/items rather than creating GroupItem
        // ancestors. Put the shared outline beside that content so it scrolls with it.
        var candidateSurface = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        candidateSurface.Children.Add(_grid); candidateSurface.Children.Add(_groupOverlay);
        var candidatesScroll = new ScrollViewer { Content = candidateSurface, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
        left.Children.Add(candidatesScroll);
        Grid.SetRow(_status, 1); left.Children.Add(_status);

        var page = new Grid { Width = width, Height = height, ColumnSpacing = 24, RowSpacing = 8 };
        page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(rightWidth) });
        page.Children.Add(left); Grid.SetColumn(rightScroll, 1); page.Children.Add(rightScroll);

        var root = (owner.Content as FrameworkElement)?.XamlRoot;
        _dialog = new ContentDialog { XamlRoot = root, Content = page, DefaultButton = ContentDialogButton.None,
            IsPrimaryButtonEnabled = false };
        _dialog.Resources["ContentDialogMaxWidth"] = budget.Width + 48;
        _dialog.Resources["ContentDialogMaxHeight"] = budget.Height + 180;
        _exit = new DialogExitPrompt(_dialog);
        AutomationProperties.SetName(_dialog, ProductStrings.Get("Import.Title"));
        Id(_dialog, "dialog"); Id(_selectAll, "select-all"); Id(_source, "source"); Id(_autoTrim, "auto-trim");
        Id(_recognizeGroups, "recognize-groups"); Id(_small, "small"); Id(_medium, "medium");
        Id(_grid, "candidates"); Id(_status, "status");

        _grid.ContainerContentChanging += OnContainerContentChanging;
        _selectAll.Click += (_, _) => ToggleVisibleSelection();
        _autoTrim.Toggled += (_, _) => RefreshProjection();
        _recognizeGroups.Toggled += (_, _) => RefreshProjection();
        _small.Click += (_, _) => SetSize(1);
        _medium.Click += (_, _) => SetSize(2);
        _dialog.PrimaryButtonClick += (_, e) =>
        {
            if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.SaveAndExit); return; }
            if (TrySaveNow() is not null) e.Cancel = true;
        };
        _dialog.SecondaryButtonClick += (_, e) =>
        { if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.DiscardAndExit); } };
        _dialog.CloseButtonClick += (_, e) =>
        {
            if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.CancelExit); return; }
            if (!HasUnsavedDraft) return;
            if (_allowDiscard) { _discarded = true; return; }
            e.Cancel = true; OfferDiscard();
        };
        _dialog.Closing += (_, e) =>
        {
            if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.CancelExit); }
            else if (!_discarded && HasUnsavedDraft) { e.Cancel = true; OfferDiscard(); }
        };
        RefreshStrings();
    }

    public async Task ShowAsync()
    {
        _pending = ScanAsync();
        try { await OwnedDialogWindow.ShowAsync(_dialog); }
        finally { StopRequests(); _lifetime.Dispose(); }
    }

    private async Task ScanAsync()
    {
        _status.Text = ProductStrings.Get("Import.Scanning");
        try
        {
            var result = await StartMenuImportScan.RunAsync(_lifetime.Token);
            if (_closed || _lifetime.IsCancellationRequested) return;
            _scanReceived = true;
            _candidates = _prepare(result);
            _skipped = result.SkippedDirectories;
            _scanComplete = _skipped == 0 && _candidates.All(candidate => !candidate.ReadFailed);

            // Keep the former recommended default without exposing a separate preset action.
            foreach (var candidate in _candidates.Where(candidate => candidate.CanAdd && candidate.Exclusion == StartMenuExclusion.None))
                _selected.Add(candidate.SelectionKey);
            RefreshProjection();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _scanFailed = true;
                _status.Text = string.Format(ProductStrings.Get("Import.ScanFailed"), ex.Message);
            }
        }
    }

    private void ToggleVisibleSelection()
    {
        var candidates = _visibleCandidates.Where(candidate => candidate.CanAdd).ToArray();
        if (candidates.Length == 0) return;
        var clear = candidates.All(candidate => _selected.Contains(candidate.SelectionKey));
        foreach (var candidate in candidates)
        {
            if (clear) _selected.Remove(candidate.SelectionKey);
            else _selected.Add(candidate.SelectionKey);
        }
        SelectionChanged();
    }

    private void RefreshProjection()
    {
        _visibleCandidates = StartMenuImportProjection.Project(_candidates, _autoTrim.IsOn, _recognizeGroups.IsOn);
        foreach (var request in _rowRequests.ToArray()) ReleaseRowRequest(request);
        _selectionVisuals.Clear();
        _grid.ItemsSource = null;
        var source = new CollectionViewSource { IsSourceGrouped = true,
            Source = _visibleCandidates.GroupBy(candidate => candidate.Bucket.Key)
                .Select(group => new CandidateGroup(group.First().Bucket.Title, group)).ToArray() };
        _grid.ItemsSource = source.View;
        SelectionChanged();
    }

    private void RefreshItemTemplate()
    {
        var slot = _tileSize * _metrics.Pitch;
        _grid.ItemTemplate = (DataTemplate)XamlReader.Load($"<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Grid Width=\"{slot}\" Height=\"{slot}\" /></DataTemplate>");
    }

    private void SetSize(int size)
    {
        _tileSize = size;
        _small.IsChecked = size == 1; _medium.IsChecked = size == 2;
        RefreshItemTemplate(); RefreshProjection();
    }

    private sealed class CandidateGroup(string title, IEnumerable<StartMenuImportCandidate> items) : List<StartMenuImportCandidate>(items)
    {
        public string Title { get; } = title;
    }

    private sealed record CandidateRow(CancellationTokenSource Request, string SelectionKey, PuzzleCanvasView View,
        Border Outline, Border Check);

    private void ClearGroupOutline()
    {
        _hoveredHeader = null;
        _groupOverlay.Children.Clear();
    }

    private void OnCandidatePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var header = FindAncestor<GridViewHeaderItem>(source);
        if (header is null) { ClearGroupOutline(); return; }
        var y = e.GetCurrentPoint(header).Position.Y;
        if (y < _metrics.TitleHeight - 24 || y >= _metrics.TitleHeight)
        { ClearGroupOutline(); return; }
        if (ReferenceEquals(header, _hoveredHeader)) return;
        _hoveredHeader = header;
        SetGroupOutline(source, true);
    }

    private void SetGroupOutline(DependencyObject? source, bool visible)
    {
        _groupOverlay.Children.Clear();
        if (!visible || source is not FrameworkElement element ||
            FindAncestor<GridViewHeaderItem>(source) is not { } header) return;
        var group = element.DataContext as CandidateGroup ?? header.DataContext as CandidateGroup ??
            header.Content as CandidateGroup ?? (header.Content as ICollectionViewGroup)?.Group as CandidateGroup;
        if (group is null) return;
        var origin = header.TransformToVisual(_groupOverlay).TransformPoint(new Windows.Foundation.Point());
        var bottom = origin.Y + _metrics.TitleHeight;
        foreach (var candidate in group)
        {
            if (_grid.ContainerFromItem(candidate) is not FrameworkElement item || !item.IsLoaded) continue;
            var point = item.TransformToVisual(_groupOverlay).TransformPoint(new Windows.Foundation.Point());
            bottom = Math.Max(bottom, point.Y + _metrics.WidthOf(_tileSize));
        }
        var outline = WallGroupVisualFactory.Outline(_style, element.ActualTheme == ElementTheme.Dark);
        outline.Width = _metrics.LaneWidth; outline.Height = Math.Max(0, bottom - origin.Y);
        outline.Visibility = Visibility.Visible;
        Canvas.SetLeft(outline, origin.X); Canvas.SetTop(outline, origin.Y);
        _groupOverlay.Children.Add(outline);
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (e.ItemContainer.Tag is CandidateRow old)
        {
            ReleaseRowRequest(old.Request); old.View.StopTransientFeedback();
            if (_selectionVisuals.TryGetValue(old.SelectionKey, out var selection) && ReferenceEquals(selection.Outline, old.Outline))
                _selectionVisuals.Remove(old.SelectionKey);
            e.ItemContainer.Tag = null;
        }
        if (e.ItemContainer.ContentTemplateRoot is not Grid host) return;
        host.Children.Clear(); e.Handled = true;
        if (_closed || e.InRecycleQueue || e.Item is not StartMenuImportCandidate item) return;
        var candidate = item.Candidate;
        var selectionKey = item.SelectionKey;
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _rowRequests.Add(request);
        var tile = new TileMember { Id = "candidate-" + candidate.FullPath,
            LocalBounds = new TileWall.Core.Grid.GridRect(0, 0, _tileSize, _tileSize),
            Visual = new MemberVisual { Title = candidate.DisplayName, TitleMode = MemberTitleMode.Manual }, TargetInput = candidate.FullPath };
        var size = _metrics.WidthOf(_tileSize);
        var card = new Grid { Width = size, Height = size,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var sourceText = ProductStrings.Get(candidate.Source == SourceKind.User ? "Import.UserSource" : "Import.CommonSource");
        var reason = item.Error ?? (item.Exclusion == StartMenuExclusion.None ? string.Empty
            : ProductStrings.Get("Import.Exclusion." + item.Exclusion));
        var tooltip = $"{sourceText} · {candidate.FullPath}";
        if (!string.IsNullOrWhiteSpace(candidate.DisplayName)) tooltip = candidate.DisplayName + "\n" + tooltip;
        if (item.Replacing) tooltip += "\n" + ProductStrings.Get("Import.Replacing");
        if (reason.Length != 0) tooltip += "\n" + reason;
        if (!candidate.IsImportable) tooltip += "\n" + string.Join("; ", candidate.NotImportableReasons);
        var view = WallMemberVisualFactory.Create(tile, null, _metrics, _dialog.XamlRoot?.RasterizationScale ?? 1,
            _style, (button, _) =>
            {
                button.IsEnabled = item.CanAdd;
                ActionIcon.SetTip(button, tooltip);
                var accessibleName = string.IsNullOrWhiteSpace(candidate.DisplayName)
                    ? Path.GetFileNameWithoutExtension(candidate.FullPath) ?? candidate.FullPath : candidate.DisplayName;
                AutomationProperties.SetName(button, accessibleName);
                AutomationProperties.SetHelpText(button, tooltip);
                AutomationProperties.SetAutomationId(button, "import-candidate-" + candidate.FullPath);
                button.Click += (_, _) =>
                {
                    if (!_selected.Add(selectionKey)) _selected.Remove(selectionKey);
                    SelectionChanged();
                };
            });
        card.Children.Add(view);
        var visual = WallMemberVisualFactory.AddSelection(card, _style);
        _selectionVisuals[selectionKey] = visual;
        e.ItemContainer.Tag = new CandidateRow(request, selectionKey, view, visual.Outline, visual.Check);
        host.Children.Add(card); UpdateSelectionFeedback();
        _ = LoadIconAsync();

        async Task LoadIconAsync()
        {
            try
            {
                var pixels = (int)Math.Ceiling(size * (_dialog.XamlRoot?.RasterizationScale ?? 1));
                var frame = await _images.LoadMemberFrameAsync(tile, candidate.FullPath, pixels, pixels, ct: request.Token);
                if (!request.IsCancellationRequested) view.UpdateFrame(frame.Frame);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            finally { ReleaseRowRequest(request); }
        }
    }

    private void SelectionChanged()
    {
        _allowDiscard = false; _discarded = false;
        var addable = _visibleCandidates.Where(candidate => candidate.CanAdd).ToArray();
        var selectedCount = addable.Count(candidate => _selected.Contains(candidate.SelectionKey));
        _selectAll.IsEnabled = addable.Length != 0;
        _selectAll.IsChecked = selectedCount == 0 ? false : selectedCount == addable.Length ? true : null;
        _dialog.IsPrimaryButtonEnabled = selectedCount != 0;
        UpdateSelectionFeedback(); RefreshStrings(); UpdateStatus();
    }

    private void UpdateSelectionFeedback()
    {
        foreach (var (key, visual) in _selectionVisuals)
        {
            var visible = _selected.Contains(key) ? Visibility.Visible : Visibility.Collapsed;
            visual.Outline.Visibility = visible; visual.Check.Visibility = visible;
        }
    }

    private void UpdateStatus()
    {
        if (!_scanReceived || _scanFailed) return;
        var selectedCount = _visibleCandidates.Count(candidate => candidate.CanAdd && _selected.Contains(candidate.SelectionKey));
        _status.Text = string.Format(ProductStrings.Get("Import.Count"), _visibleCandidates.Count, selectedCount, _skipped);
    }

    private void ReleaseRowRequest(CancellationTokenSource request)
    {
        if (!_rowRequests.Remove(request)) return;
        request.Cancel(); request.Dispose();
    }

    private void StopRequests()
    {
        if (_closed) return;
        _closed = true; _lifetime.Cancel();
        foreach (var request in _rowRequests.ToArray()) ReleaseRowRequest(request);
    }

    public bool HasUnsavedDraft => !_saved && _selected.Count != 0;
    public async Task WaitForPendingAsync()
    {
        try { await _pending.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
    }
    public Task<ExitDecision> ConfirmExitAsync() => _exit.ConfirmAsync();
    public void RequestDismiss() => _dialog.Hide();
    public void Close() { _discarded = true; StopRequests(); _dialog.Hide(); }
    public void DiscardDraft() { _selected.Clear(); _discarded = true; }

    private ImportBatchItem[] SelectedItems() => _visibleCandidates
        .Where(candidate => candidate.CanAdd && _selected.Contains(candidate.SelectionKey)).Select(candidate =>
        {
            var scanned = candidate.Candidate;
            if (!_items.TryGetValue(candidate.SelectionKey, out var item))
                _items[candidate.SelectionKey] = item = ImportBatchItem.Create(scanned.FullPath, scanned.DisplayName);
            var root = Path.GetDirectoryName(scanned.FullPath)!;
            foreach (var _ in scanned.RelativeDir.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)) root = Path.GetDirectoryName(root)!;
            var size = _tileSize;
            return item with
            {
                Member = item.Member with { LocalBounds = new TileWall.Core.Grid.GridRect(0, 0, size, size) },
                GroupBucketKey = candidate.Bucket.Key, GroupTitle = candidate.Bucket.Title, GroupOrder = candidate.Bucket.Order,
                SourceRoot = root, RelativePath = Path.GetRelativePath(root, scanned.FullPath),
            };
        }).ToArray();

    public string? TrySaveNow()
    {
        if (!_visibleCandidates.Any(candidate => candidate.CanAdd && _selected.Contains(candidate.SelectionKey)))
            return Fail(ProductStrings.Get("Import.SelectFirst"));
        try
        {
            var error = _save(SelectedItems(), _scanComplete);
            if (error is not null) return Fail(error);
            _saved = true; return null;
        }
        catch (Exception ex) { return Fail(string.Format(ProductStrings.Get("Import.Failed"), ex.Message)); }
    }

    private string Fail(string error) { _status.Text = error; return error; }
    private void OfferDiscard() { _allowDiscard = true; RefreshStrings(); _status.Text = ProductStrings.Get("Common.UnsavedDraft"); }

    public void RefreshStrings()
    {
        if (_exit.IsActive) return;
        _dialog.PrimaryButtonText = ProductStrings.Get("Import.Confirm");
        _dialog.CloseButtonText = ProductStrings.Get(_allowDiscard ? "Common.Discard" : "Common.Cancel");
        _selectAll.Content = ProductStrings.Get("Import.SelectAll");
        _sourceLabel.Text = ProductStrings.Get("Import.Source");
        _trimLabel.Text = ProductStrings.Get("Import.AutoTrim");
        _groupsLabel.Text = ProductStrings.Get("Import.RecognizeGroups");
        _sizeLabel.Text = ProductStrings.Get("Editor.TileSize");
        _source.Items.Clear();
        _source.Items.Add(new ComboBoxItem { Content = ProductStrings.Get("Import.Source.StartMenu") });
        _source.SelectedIndex = 0;
        ToolTipService.SetToolTip(_source, ProductStrings.Get("Import.Source.Tooltip"));
        ActionIcon.SetTip(_selectAll, ProductStrings.Get("Import.SelectAll.Tooltip"));
        ToolTipService.SetToolTip(_autoTrim, ProductStrings.Get("Import.AutoTrim.Tooltip"));
        ToolTipService.SetToolTip(_recognizeGroups, ProductStrings.Get("Import.RecognizeGroups.Tooltip"));
        AutomationProperties.SetName(_source, ProductStrings.Get("Import.Source"));
        AutomationProperties.SetHelpText(_source, ProductStrings.Get("Import.Source.Tooltip"));
        AutomationProperties.SetName(_selectAll, ProductStrings.Get("Import.SelectAll"));
        AutomationProperties.SetHelpText(_selectAll, ProductStrings.Get("Import.SelectAll.Tooltip"));
        AutomationProperties.SetName(_autoTrim, ProductStrings.Get("Import.AutoTrim"));
        AutomationProperties.SetName(_recognizeGroups, ProductStrings.Get("Import.RecognizeGroups"));
        AutomationProperties.SetLabeledBy(_source, _sourceLabel); AutomationProperties.SetLabeledBy(_autoTrim, _trimLabel);
        AutomationProperties.SetLabeledBy(_recognizeGroups, _groupsLabel);
        ActionIcon.SetIcon(_small, ProductStrings.Get("Import.Small"), ActionIcon.PresetSmall);
        ActionIcon.SetIcon(_medium, ProductStrings.Get("Import.Medium"), ActionIcon.PresetMedium);
        _small.IsChecked = _tileSize == 1; _medium.IsChecked = _tileSize == 2;
        AutomationProperties.SetLabeledBy(_small, _sizeLabel); AutomationProperties.SetLabeledBy(_medium, _sizeLabel);
        ActionIcon.DecorateDialog(_dialog, ActionIcon.Import, ActionIcon.Delete, ActionIcon.Cancel);
    }

    private static void AddOptionRow(Grid form, FrameworkElement value)
    {
        var row = form.RowDefinitions.Count;
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumnSpan(value, 2); Grid.SetRow(value, row); form.Children.Add(value);
    }

    private static void AddRow(Grid form, string key, TextBlock label, FrameworkElement value)
    {
        var row = form.RowDefinitions.Count;
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        label.Text = ProductStrings.Get(key);
        AutomationProperties.SetLabeledBy(value, label);
        Grid.SetRow(label, row); form.Children.Add(label);
        Grid.SetRow(value, row); Grid.SetColumn(value, 1); form.Children.Add(value);
    }


    private static TextBlock Label() => new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static void Id(DependencyObject element, string id) => AutomationProperties.SetAutomationId(element, "import-" + id);
}
