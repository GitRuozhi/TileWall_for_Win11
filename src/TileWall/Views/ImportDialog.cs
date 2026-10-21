using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using TileWall.ApplicationModel;
using TileWall.Core.Application;
using TileWall.Core.Import;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;
using TileWall.Shell.Interop;

namespace TileWall.Views;

/// <summary>A source-keyed draft over the primary session's shared catalog.</summary>
internal sealed class ImportDialog : IWallDialogSession
{
    private readonly ContentDialog _dialog;
    private readonly DialogExitPrompt _exit;
    private readonly WallImageService _images;
    private readonly StartMenuImportCoordinator _catalog;
    private readonly Action _catalogChanged;
    private readonly Func<StartMenuImportCandidate, int, CancellationToken, Task<PuzzleFrameResult>>? _loadIcon;
    private readonly Func<IReadOnlyList<ImportBatchItem>, bool, string?> _save;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<CancellationTokenSource> _rowRequests = [];
    private readonly CheckBox _selectAll = new() { IsThreeState = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _source = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _autoTrim = new() { IsOn = true, OnContent = string.Empty, OffContent = string.Empty };
    private readonly ToggleSwitch _recognizeGroups = new() { IsOn = true, OnContent = string.Empty, OffContent = string.Empty };
    private readonly ToggleSwitch _preserveChanges = new() { IsOn = true, OnContent = string.Empty, OffContent = string.Empty };
    private readonly ToggleButton _small = new(), _medium = new();
    private readonly TextBlock _sourceLabel = Label(), _trimLabel = Label(), _groupsLabel = Label(), _sizeLabel = Label(), _preserveLabel = Label();
    private readonly TextBlock _scannedCount = Label(), _filteredCount = Label(), _candidateCount = Label(), _selectedCount = Label();
    private readonly TextBlock _development = new() { TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly ScrollViewer _candidateScroll;
    private readonly WallMetrics _metrics = new();
    private readonly WallVisualStyle _style;
    private readonly HashSet<CandidateRow> _rows = [];
    private readonly Canvas _groupOverlay = new() { IsHitTestVisible = false };
    private GridViewHeaderItem? _hoveredHeader;
    private readonly GridView _grid = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, string> _selected = new(StringComparer.Ordinal);
    private string? _projectionNotice;
    private readonly Dictionary<string, ImportBatchItem> _items = new(StringComparer.Ordinal);
    private IReadOnlyList<StartMenuImportCandidate> _candidates = [];
    private IReadOnlyList<StartMenuImportCandidate> _visibleCandidates = [];
    private Task _pending = Task.CompletedTask;
    private CancellationTokenSource? _pageRequest;
    private bool _saved, _closed, _allowDiscard, _discarded, _scanReceived, _updating, _initialSelectionApplied;
    private bool _synchronizingSelection, _refreshingStrings, _shown;
    private int _sourceEpoch, _tileSize = 1, _skipped, _scanned, _readErrors;

    public ImportDialog(Window owner, WallImageService images,
        StartMenuImportCoordinator catalog,
        Func<IReadOnlyList<ImportBatchItem>, bool, string?> save, WallVisualStyle style,
        Func<StartMenuImportCandidate, int, CancellationToken, Task<PuzzleFrameResult>>? loadIcon = null)
    {
        _images = images; _catalog = catalog; _save = save; _style = style; _loadIcon = loadIcon;
        _catalogChanged = () =>
        {
            var epoch = _sourceEpoch;
            owner.DispatcherQueue.TryEnqueue(() =>
            {
                if (!_closed && _source.SelectedIndex == 0 && _sourceEpoch == epoch) ApplySnapshot();
            });
        };
        RefreshItemTemplate();
        _grid.Width = _metrics.LaneWidth + _metrics.Gap + 16;
        _grid.Padding = new Thickness(0);
        _grid.ItemContainerStyle = (Style)XamlReader.Load("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="GridViewItem">
                <Setter Property="Padding" Value="0" /><Setter Property="Margin" Value="0" />
                <Setter Property="MinWidth" Value="0" /><Setter Property="MinHeight" Value="0" />
                <Setter Property="IsTabStop" Value="False" />
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
        AddRow(options, "Import.AutoFilter", _trimLabel, _autoTrim);
        AddRow(options, "Import.RecognizeGroups", _groupsLabel, _recognizeGroups);
        AddRow(options, "Import.PreserveChanges", _preserveLabel, _preserveChanges);
        var sizes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sizes.Children.Add(_small); sizes.Children.Add(_medium);
        AddRow(options, "Editor.TileSize", _sizeLabel, sizes);
        var counts = new StackPanel { Spacing = 6 };
        counts.Children.Add(_scannedCount); counts.Children.Add(_filteredCount);
        counts.Children.Add(_candidateCount); counts.Children.Add(_selectedCount);
        AddOptionRow(options, counts);
        AddOptionRow(options, _status);

        var rightCard = VisualStyles.Card(options);
        rightCard.Width = rightWidth;
        rightCard.HorizontalAlignment = HorizontalAlignment.Left;
        var rightScroll = new ScrollViewer { Content = rightCard, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var left = new Grid { RowSpacing = 8 };
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        // ItemsWrapGrid flattens grouped headers/items rather than creating GroupItem
        // ancestors. Put the shared outline beside that content so it scrolls with it.
        var candidateSurface = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        candidateSurface.Children.Add(_grid); candidateSurface.Children.Add(_groupOverlay);
        _candidateScroll = new ScrollViewer { Content = candidateSurface, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
        left.Children.Add(_candidateScroll); left.Children.Add(_development);

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
        Id(_preserveChanges, "preserve-changes");
        Id(_scannedCount, "scanned-count"); Id(_filteredCount, "filtered-count");
        Id(_candidateCount, "candidate-count"); Id(_selectedCount, "selected-count");
        Id(_grid, "candidates"); Id(_status, "status");

        _grid.ContainerContentChanging += OnContainerContentChanging;
        _selectAll.Click += (_, _) => ToggleVisibleSelection();
        _autoTrim.Toggled += (_, _) => RefreshProjection();
        _recognizeGroups.Toggled += (_, _) => RefreshProjection();
        _source.SelectionChanged += (_, _) => SourceChanged();
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
            try
            {
                if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.CancelExit); }
                else if (!_discarded && HasUnsavedDraft) { e.Cancel = true; OfferDiscard(); }
            }
            finally { if (!e.Cancel) StopRequests(); }
        };
        RefreshStrings();
    }

    public async Task ShowAsync()
    {
        _shown = true;
        _catalog.Changed += _catalogChanged;
        StartPageRequest();
        try { await OwnedDialogWindow.ShowAsync(_dialog); }
        finally { StopRequests(); _pageRequest?.Dispose(); _lifetime.Dispose(); }
    }

    private void StartPageRequest(int? epoch = null)
    {
        _pageRequest?.Cancel(); _pageRequest?.Dispose();
        _pageRequest = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _pending = LoadPageAsync(epoch ?? _sourceEpoch, _pageRequest.Token);
    }

    private async Task LoadPageAsync(int epoch, CancellationToken token)
    {
        try
        {
            await _catalog.ReadCachedAsync().WaitAsync(token);
            if (!IsCurrentRequest(epoch, token)) return;
            ApplySnapshot();
            await _catalog.RequestRefreshAsync().WaitAsync(token);
            if (IsCurrentRequest(epoch, token)) ApplySnapshot();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsCurrentRequest(epoch, token))
            {
                _status.Text = string.Format(ProductStrings.Get("Import.ScanFailed"), ex.Message);
            }
        }
    }

    private bool IsCurrentRequest(int epoch, CancellationToken token) =>
        !_closed && !token.IsCancellationRequested && _source.SelectedIndex == 0 && _sourceEpoch == epoch;

    private void ApplySnapshot()
    {
        var snapshot = _catalog.Snapshot;
        _scanReceived = snapshot.Sources.Count != 0 || snapshot.Roots.Count != 0;
        _updating = snapshot.Refreshing;
        _scanned = snapshot.ScannedCount; _skipped = snapshot.SkippedDirectories;
        _readErrors = snapshot.Sources.Count(source => source.ErrorCode is "InvalidTarget" or "SourceUnavailable" or
            "SourceMissing" or "SourceChanged" or "UnsafeSource" or "SourceTooLarge");
        _candidates = snapshot.Sources.Select(source => new StartMenuImportCandidate(source.Candidate,
            new("pending", string.Empty, 0), source.Identity,
            source.Identity is null ? StartMenuExclusion.None : StartMenuSelectionRules.Classify(source.Candidate.DisplayName, source.Identity),
            source.ErrorCode ?? (source.Identity?.IsUrl == true && !RegisteredUrlProtocols.IsSupported(source.Identity.Target)
                ? "UnsupportedProtocol" : null), source.Stale, source.Company, source.SourceSha256,
            _catalog.IconsDirectory(source.Candidate), source.IconKey)).ToArray();
        RefreshProjection();
    }

    private void SourceChanged()
    {
        if (!_shown || _refreshingStrings || _closed) return;
        var epoch = ++_sourceEpoch;
        var active = _source.SelectedIndex == 0;
        _pageRequest?.Cancel();
        foreach (var row in _rows.ToArray()) ReleaseRow(row);
        _grid.ItemsSource = null;
        _candidateScroll.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _development.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        _autoTrim.IsEnabled = active; _recognizeGroups.IsEnabled = active; _preserveChanges.IsEnabled = active;
        _small.IsEnabled = active; _medium.IsEnabled = active;
        if (active) StartPageRequest(epoch);
        SelectionChanged();
    }

    private void ToggleVisibleSelection()
    {
        var candidates = _visibleCandidates.Where(candidate => candidate.CanAdd).ToArray();
        if (candidates.Length == 0) return;
        var clear = candidates.All(IsSelected);
        foreach (var candidate in candidates)
        {
            SetSelection(candidate, !clear, refresh: false);
        }
        _projectionNotice = null;
        SelectionChanged();
    }

    private void RefreshProjection()
    {
        var previous = _visibleCandidates;
        _visibleCandidates = StartMenuImportProjection.Project(_candidates, _autoTrim.IsOn, _recognizeGroups.IsOn);
        var migration = StartMenuCandidateProjection.Migrate(_selected, previous, _visibleCandidates, _candidates);
        _selected.Clear();
        foreach (var pair in migration.Selected) _selected.Add(pair.Key, pair.Value);
        if (!_initialSelectionApplied && _visibleCandidates.Any(candidate => candidate.CanAdd))
        {
            _initialSelectionApplied = true;
            foreach (var candidate in _visibleCandidates.Where(candidate => candidate.CanAdd && candidate.Exclusion == StartMenuExclusion.None))
                SetSelection(candidate, true, refresh: false);
        }
        _projectionNotice = migration.RemovedCount > 0
            ? string.Format(ProductStrings.Get("Import.SelectionRemoved"), migration.RemovedCount) : null;
        foreach (var row in _rows.ToArray()) ReleaseRow(row);
        _grid.ItemsSource = null;
        var source = new CollectionViewSource { IsSourceGrouped = true,
            Source = _visibleCandidates.GroupBy(candidate => candidate.Bucket.Key)
                .Select(group => new CandidateGroup(group.First().Bucket.Title, group)).ToArray() };
        if (_source.SelectedIndex == 0) _grid.ItemsSource = source.View;
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

    private sealed record CandidateRow(CancellationTokenSource Request, StartMenuImportCandidate Candidate, PuzzleCanvasView View,
        ToggleButton Toggle, bool CanAdd, RoutedEventHandler Changed, Action ResetInteraction, Border Outline, Border Check);

    private void ReleaseRow(CandidateRow row)
    {
        if (!_rows.Remove(row)) return;
        ReleaseRowRequest(row.Request);
        row.Toggle.Checked -= row.Changed;
        row.Toggle.Unchecked -= row.Changed;
        row.ResetInteraction();
        row.View.StopTransientFeedback();
        row.Toggle.IsChecked = false;
    }

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
        if (y < _metrics.TitleHeight - _metrics.TitleInteractionHeight || y >= _metrics.TitleHeight)
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
        var top = origin.Y + _metrics.TitleHeight - _metrics.TitleInteractionHeight;
        var outline = WallGroupVisualFactory.Outline(_style, element.ActualTheme == ElementTheme.Dark);
        outline.Width = _metrics.LaneWidth; outline.Height = Math.Max(0, bottom - top);
        outline.Visibility = Visibility.Visible;
        Canvas.SetLeft(outline, origin.X); Canvas.SetTop(outline, top);
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
            ReleaseRow(old);
            e.ItemContainer.Tag = null;
        }
        if (e.ItemContainer.ContentTemplateRoot is not Grid host) return;
        host.Children.Clear(); e.Handled = true;
        if (_closed || e.InRecycleQueue || e.Item is not StartMenuImportCandidate item) return;
        var candidate = item.Candidate;
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _rowRequests.Add(request);
        var tile = new TileMember { Id = "candidate-" + candidate.FullPath,
            LocalBounds = new TileWall.Core.Grid.GridRect(0, 0, _tileSize, _tileSize),
            Visual = new MemberVisual { Title = candidate.DisplayName, TitleMode = MemberTitleMode.Manual }, TargetInput = candidate.FullPath };
        var size = _metrics.WidthOf(_tileSize);
        var card = new Grid { Width = size, Height = size,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var sourceText = ProductStrings.Get(candidate.Source == SourceKind.User ? "Import.UserSource" : "Import.CommonSource");
        var reason = item.ErrorCode is { } error ? ErrorText(error) : (item.Exclusion == StartMenuExclusion.None ? string.Empty
            : ProductStrings.Get("Import.Exclusion." + item.Exclusion));
        var tooltip = $"{sourceText} · {candidate.FullPath}";
        if (!string.IsNullOrWhiteSpace(candidate.DisplayName)) tooltip = candidate.DisplayName + "\n" + tooltip;
        if (item.Stale) tooltip += "\n" + Text("Import.Stale", "来源待核验");
        if (reason.Length != 0) tooltip += "\n" + reason;
        if (!candidate.IsImportable) tooltip += "\n" + string.Join("; ", candidate.NotImportableReasons);
        var view = WallMemberVisualFactory.Create(tile, null, _metrics, _dialog.XamlRoot?.RasterizationScale ?? 1,
            _style, interactive: false, externalInteraction: true);
        view.IsHitTestVisible = false;
        card.Children.Add(view);
        var visual = WallMemberVisualFactory.AddSelection(card, _style);
        var toggle = new ToggleButton
        {
            Content = card, Width = size, Height = size, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0), BorderThickness = new Thickness(0),
            CornerRadius = WallCornerStyle.Corners(_style), IsEnabled = item.CanAdd,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        ActionIcon.SetTip(toggle, tooltip);
        var accessibleName = string.IsNullOrWhiteSpace(candidate.DisplayName)
            ? Path.GetFileNameWithoutExtension(candidate.FullPath) ?? candidate.FullPath : candidate.DisplayName;
        AutomationProperties.SetName(toggle, accessibleName);
        AutomationProperties.SetHelpText(toggle, tooltip);
        AutomationProperties.SetAutomationId(toggle, "import-candidate-" + candidate.FullPath);
        // The default GridViewItem name is the data record's ToString(), which dumps
        // every candidate field into UIA and screen readers. Give the container the
        // same concise name as its interactive child.
        AutomationProperties.SetName(e.ItemContainer, accessibleName);
        AutomationProperties.SetHelpText(e.ItemContainer, tooltip);
        var reset = WallMemberVisualFactory.ConfigureInteraction(toggle, card, toggle.CornerRadius, view.SetExternalHover);
        RoutedEventHandler changed = (_, _) =>
        {
            if (!_synchronizingSelection && item.CanAdd) SetSelection(item, toggle.IsChecked == true);
        };
        toggle.Checked += changed; toggle.Unchecked += changed;
        var row = new CandidateRow(request, item, view, toggle, item.CanAdd, changed, reset, visual.Outline, visual.Check);
        _rows.Add(row);
        e.ItemContainer.Tag = row;
        host.Children.Add(toggle); UpdateSelectionFeedback();
        _ = LoadIconAsync();

        async Task LoadIconAsync()
        {
            try
            {
                var pixels = (int)Math.Ceiling(size * (_dialog.XamlRoot?.RasterizationScale ?? 1));
                var frame = _loadIcon is null ? null : await _loadIcon(item, pixels, request.Token);
                if (frame?.Frame is null && !request.IsCancellationRequested)
                    frame = await _images.LoadMemberFrameAsync(tile, candidate.FullPath, pixels, pixels, ct: request.Token);
                if (!request.IsCancellationRequested) view.UpdateFrame(frame?.Frame);
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
        var selectedCount = addable.Count(IsSelected);
        var active = _source.SelectedIndex == 0;
        _selectAll.IsEnabled = active && addable.Length != 0;
        _selectAll.IsChecked = selectedCount == 0 ? false : selectedCount == addable.Length ? true : null;
        _dialog.IsPrimaryButtonEnabled = active && selectedCount != 0;
        UpdateSelectionFeedback(); RefreshStrings(); UpdateStatus();
    }

    private bool IsSelected(StartMenuImportCandidate candidate) => candidate.CanAdd && candidate.SelectionSources
        .Any(source => _selected.GetValueOrDefault(source.SourceKey) == source.IdentityKey);

    private void SetSelection(StartMenuImportCandidate candidate, bool selected, bool refresh = true)
    {
        if (selected && candidate.Identity is { } identity) _selected[candidate.SelectionKey] = identity.Key;
        else foreach (var source in candidate.SelectionSources) _selected.Remove(source.SourceKey);
        if (!refresh) return;
        _projectionNotice = null;
        SelectionChanged();
    }

    private void UpdateSelectionFeedback()
    {
        _synchronizingSelection = true;
        try
        {
            foreach (var row in _rows)
            {
                var selected = row.CanAdd && IsSelected(row.Candidate);
                row.Toggle.IsChecked = selected;
                var visible = selected ? Visibility.Visible : Visibility.Collapsed;
                row.Outline.Visibility = visible; row.Check.Visibility = visible;
            }
        }
        finally { _synchronizingSelection = false; }
    }

    private void UpdateStatus()
    {
        var active = _source.SelectedIndex == 0;
        _scannedCount.Text = string.Format(Text("Import.ScannedCount", "已扫描 {0} 项"), active ? _scanned : 0);
        _filteredCount.Text = string.Format(Text("Import.FilteredCount", "已过滤 {0} 项"),
            active && _autoTrim.IsOn ? _candidates.Count(candidate => candidate.Exclusion != StartMenuExclusion.None) : 0);
        _candidateCount.Text = string.Format(Text("Import.CandidateCount", "候选 {0} 项"), active ? _visibleCandidates.Count : 0);
        _selectedCount.Text = string.Format(Text("Import.SelectedCount", "已选 {0} 项"), active ? _visibleCandidates.Count(IsSelected) : 0);
        if (!active) { _status.Text = Text("Import.Developing", "正在开发中"); return; }
        if (_projectionNotice is not null) { _status.Text = _projectionNotice; return; }
        var messages = new List<string>();
        if (_updating || !_scanReceived) messages.Add(Text("Import.Updating", "正在更新来源…"));
        if (_readErrors != 0 || _skipped != 0)
            messages.Add(string.Format(Text("Import.ReadErrors", "读取失败 {0} 项，未完整读取 {1} 个目录"), _readErrors, _skipped));
        if (_catalog.Snapshot.ErrorCode is { } code) messages.Add(ErrorText(code));
        _status.Text = string.Join("\n", messages);
    }

    private void ReleaseRowRequest(CancellationTokenSource request)
    {
        if (!_rowRequests.Remove(request)) return;
        request.Cancel(); request.Dispose();
    }

    private void StopRequests()
    {
        if (_closed) return;
        _closed = true; _lifetime.Cancel(); _pageRequest?.Cancel();
        _catalog.Changed -= _catalogChanged;
        foreach (var row in _rows.ToArray()) ReleaseRow(row);
        foreach (var request in _rowRequests.ToArray()) ReleaseRowRequest(request);
    }

    public bool HasUnsavedDraft => !_saved && _selected.Count != 0;
    public async Task WaitForPendingAsync()
    {
        // The page stops waiting. The primary lifetime drains the shared producer only after exit is confirmed.
        if (!_closed && !_pending.IsCompleted) _pageRequest?.Cancel();
        try { await _pending; }
        catch (OperationCanceledException) { }
    }
    public Task<ExitDecision> ConfirmExitAsync() => _exit.ConfirmAsync();
    public void RequestDismiss() => _dialog.Hide();
    public void Close() { _discarded = true; StopRequests(); _dialog.Hide(); }
    public void DiscardDraft()
    {
        _selected.Clear();
        UpdateSelectionFeedback(); _discarded = true;
    }

    private ImportBatchItem[] SelectedItems() => _visibleCandidates
        .Where(IsSelected).Select(candidate =>
        {
            var scanned = candidate.Candidate;
            if (!_items.TryGetValue(candidate.SelectionKey, out var item))
                _items[candidate.SelectionKey] = item = ImportBatchItem.Create(scanned.FullPath, scanned.DisplayName);
            var size = _tileSize;
            return item with
            {
                Member = item.Member with { LocalBounds = new TileWall.Core.Grid.GridRect(0, 0, size, size) },
                GroupBucketKey = candidate.Bucket.Key, GroupTitle = candidate.Bucket.Title, GroupOrder = candidate.Bucket.Order,
                SourceRoot = scanned.RootPath, RelativePath = scanned.RelativePath,
                CandidateSnapshot = scanned, ExpectedIdentityKey = candidate.Identity!.Key,
                ExpectedSourceSha256 = candidate.SourceSha256,
                IconDirectory = candidate.IconDirectory, IconKey = candidate.IconKey,
            };
        }).ToArray();

    public string? TrySaveNow()
    {
        if (_source.SelectedIndex != 0)
            return Fail(Text("Import.Developing", "正在开发中"));
        if (!_visibleCandidates.Any(IsSelected))
            return Fail(ProductStrings.Get("Import.SelectFirst"));
        try
        {
            var error = _save(SelectedItems(), _preserveChanges.IsOn);
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
        _trimLabel.Text = Text("Import.AutoFilter", "自动过滤");
        _groupsLabel.Text = ProductStrings.Get("Import.RecognizeGroups");
        _preserveLabel.Text = Text("Import.PreserveChanges", "保留已有改动");
        _sizeLabel.Text = ProductStrings.Get("Editor.TileSize");
        _development.Text = Text("Import.Developing", "正在开发中");
        _refreshingStrings = true;
        try
        {
            var selected = _source.SelectedIndex;
            var names = new[] { ProductStrings.Get("Import.Source.StartMenu"),
                Text("Import.SourceDesktop", "本机桌面"),
                Text("Import.Source.InstalledApplications", "已安装应用"),
                Text("Import.Source.CustomSettings", "自定义设置") };
            if (_source.Items.Count != names.Length)
            {
                _source.Items.Clear();
                foreach (var name in names) _source.Items.Add(new ComboBoxItem { Content = name });
            }
            else for (var index = 0; index < names.Length; index++) ((ComboBoxItem)_source.Items[index]).Content = names[index];
            _source.SelectedIndex = selected is >= 0 and <= 3 ? selected : 0;
        }
        finally { _refreshingStrings = false; }
        ToolTipService.SetToolTip(_source, ProductStrings.Get("Import.Source.Tooltip"));
        ActionIcon.SetTip(_selectAll, ProductStrings.Get("Import.SelectAll.Tooltip"));
        ToolTipService.SetToolTip(_autoTrim, Text("Import.AutoFilter.Tooltip", "过滤系统自带、卸载、维护及文档网址入口"));
        ToolTipService.SetToolTip(_preserveChanges, Text("Import.PreserveChanges.Tooltip", "保留已有磁贴的外观、尺寸和标题，并重新排列位置"));
        ToolTipService.SetToolTip(_recognizeGroups, ProductStrings.Get("Import.RecognizeGroups.Tooltip"));
        AutomationProperties.SetName(_source, ProductStrings.Get("Import.Source"));
        AutomationProperties.SetHelpText(_source, ProductStrings.Get("Import.Source.Tooltip"));
        AutomationProperties.SetName(_selectAll, ProductStrings.Get("Import.SelectAll"));
        AutomationProperties.SetHelpText(_selectAll, ProductStrings.Get("Import.SelectAll.Tooltip"));
        AutomationProperties.SetName(_autoTrim, Text("Import.AutoFilter", "自动过滤"));
        AutomationProperties.SetName(_recognizeGroups, ProductStrings.Get("Import.RecognizeGroups"));
        AutomationProperties.SetName(_preserveChanges, Text("Import.PreserveChanges", "保留已有改动"));
        AutomationProperties.SetLabeledBy(_source, _sourceLabel); AutomationProperties.SetLabeledBy(_autoTrim, _trimLabel);
        AutomationProperties.SetLabeledBy(_recognizeGroups, _groupsLabel);
        AutomationProperties.SetLabeledBy(_preserveChanges, _preserveLabel);
        ActionIcon.SetIcon(_small, ProductStrings.Get("Import.Small"), ActionIcon.PresetSmall);
        ActionIcon.SetIcon(_medium, ProductStrings.Get("Import.Medium"), ActionIcon.PresetMedium);
        _small.IsChecked = _tileSize == 1; _medium.IsChecked = _tileSize == 2;
        AutomationProperties.SetLabeledBy(_small, _sizeLabel); AutomationProperties.SetLabeledBy(_medium, _sizeLabel);
        ActionIcon.DecorateDialog(_dialog, ActionIcon.Import, ActionIcon.Delete, ActionIcon.Cancel);
    }

    private static string ErrorText(string code) => code switch
    {
        "InvalidTarget" => ProductStrings.Get("Import.InvalidTarget"),
        "UnsupportedProtocol" => ProductStrings.Get("Import.UnsupportedProtocol"),
        "SourceChanged" => Text("Import.SourceChanged", "来源已变化，请更新后重新确认"),
        "SourceTooLarge" => Text("Import.SourceTooLarge", "快捷方式文件过大，无法读取"),
        "UnsafeSource" => Text("Import.UnsafeSource", "来源位置不安全，无法导入"),
        "CacheInvalid" or "CacheWriteFailed" => Text("Import.CacheUnavailable", "来源缓存不可用，正在尝试从原位置更新"),
        "RefreshFailed" => Text("Import.RefreshFailed", "来源更新失败，可稍后重试"),
        _ => Text("Import.SourceUnavailable", "来源暂不可用，请检查原位置"),
    };

    private static string Text(string key, string fallback)
    {
        var value = ProductStrings.Get(key);
        return value == key ? fallback : value;
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
