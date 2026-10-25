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
using TileWall.Core.Exchange;
using TileWall.Core.Grid;
using TileWall.Core.Import;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;

namespace TileWall.Views;

/// <summary>A source-keyed selection over read-only facts; final wall planning occurs only on confirmation.</summary>
internal sealed class ImportDialog : IWallDialogSession
{
    private readonly Window _owner;
    private readonly ContentDialog _dialog;
    private readonly WallImageService _images;
    private readonly StartMenuImportCoordinator _catalog;
    private readonly WallPackageReader _reader;
    private readonly Func<CancellationToken, Task<ImportPageContext>> _readContext;
    private readonly Func<int, string?, CancellationToken, Task<ImportPageSource>> _readSource;
    private readonly Func<ImportPageContext, ImportPageSource, ImportSourceProjection, CancellationToken, Task<ImportPagePreparation>> _prepare;
    private readonly Func<ImportPageRequest, Task<ImportPageResult>> _save;
    private readonly Func<ImportPageContext, bool> _contextCurrent;
    private readonly Func<StartMenuImportCandidate, int, CancellationToken, Task<PuzzleFrameResult>>? _loadIcon;
    private readonly WallVisualStyle _style;
    private readonly WallMetrics _metrics = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _pendingOperations = [];
    private readonly Dictionary<string, string> _selected = new(StringComparer.Ordinal);
    private readonly HashSet<CandidateRow> _rows = [];
    private IReadOnlyList<ImportProjectedCandidate> _visibleCandidates = [];
    private readonly HashSet<Task> _previewOperations = [];
    private readonly SemaphoreSlim _packageReadGate = new(1, 1);
    private readonly SemaphoreSlim _packageMediaSlots = new(2, 2);
    private Task<WallPackageImport>? _packagePreview;
    private int _previewRowLoads;
    private IReadOnlyList<ImportSourceCandidate>? _projectedCandidates;
    private IReadOnlyDictionary<string, string>? _projectedIdentities;
    private readonly Dictionary<string, ImportProjectedCandidate> _candidateByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _groupTitles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _rowIconKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PuzzleFrame> _frames = new(StringComparer.Ordinal);
    private readonly Queue<string> _frameOrder = new();
    private readonly ComboBox _source = new(), _position = new();
    private readonly CheckBox _selectAll = new() { IsThreeState = true };
    private readonly ToggleSwitch _autoFilter = new() { IsOn = true, OnContent = "", OffContent = "" };
    private readonly ToggleSwitch _recognizeGroups = new() { IsOn = true, OnContent = "", OffContent = "" };
    private readonly ToggleButton _small = new(), _medium = new();
    private readonly Button _chooseFile = new(), _refresh = new();
    private readonly TextBlock _sourceLabel = Label(), _filterLabel = Label(), _groupsLabel = Label(), _sizeLabel = Label(), _positionLabel = Label();
    private readonly TextBlock _pathLabel = Label(), _counts = Label(), _status = Label(), _changes = Label();
    private readonly TextBlock _development = Label();
    private readonly Grid _options;
    private readonly ContentControl _optionsHost;
    private readonly GridView _grid = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false };
    private readonly Canvas _groupOverlay = new() { IsHitTestVisible = false };
    private GridViewHeaderItem? _hoveredHeader;
    private ScrollViewer? _gridScrollViewer;
    private EventHandler<ScrollViewerViewChangedEventArgs>? _gridViewChanged;
    private readonly Action _catalogChanged;
    private CancellationTokenSource? _pageRequest, _previewRequest, _catalogRequest, _pickerRequest, _preparationRequest;
    private Windows.Foundation.IAsyncOperation<Microsoft.Windows.Storage.Pickers.PickFileResult>? _pickerOperation;
    private Task _pageRead = Task.CompletedTask, _previewLoad = Task.CompletedTask, _addTask = Task.CompletedTask;
    private ImportPageContext? _context;
    private ImportPageSource? _sourceData;
    private ImportSourceProjection? _projection;
    private ImportSourcePlan? _plan;
    private string? _packagePath, _notice, _error;
    private bool _shown, _closed, _dismissRequested, _reading, _adding, _picking, _refreshingStrings, _readsSuspended, _preparing, _prepared, _synchronizingSelection;
    private int _sourceEpoch, _renderEpoch, _preparationEpoch, _tileSize = 1, _catalogRevision;

    public ImportDialog(Window owner, WallImageService images, StartMenuImportCoordinator catalog, WallPackageReader reader,
        Func<CancellationToken, Task<ImportPageContext>> readContext,
        Func<int, string?, CancellationToken, Task<ImportPageSource>> readSource,
        Func<ImportPageContext, ImportPageSource, ImportSourceProjection, CancellationToken, Task<ImportPagePreparation>> prepare,
        Func<ImportPageRequest, Task<ImportPageResult>> save, Func<ImportPageContext, bool> contextCurrent,
        WallVisualStyle style,
        Func<StartMenuImportCandidate, int, CancellationToken, Task<PuzzleFrameResult>>? loadIcon = null)
    {
        _owner = owner; _images = images; _catalog = catalog; _reader = reader;
        _readContext = readContext; _readSource = readSource; _prepare = prepare; _save = save;
        _contextCurrent = contextCurrent; _style = style; _loadIcon = loadIcon;
        _catalogChanged = () => owner.DispatcherQueue.TryEnqueue(() =>
        {
            _catalogRevision++;
            if (!_closed && !_dismissRequested && !_readsSuspended && !_reading && !_adding && _source.SelectedIndex == 0) BeginRead();
        });

        var budget = OwnedDialogWindow.ContentBudget(owner);
        var width = Math.Min(940, budget.Width); var height = Math.Min(640, budget.Height);
        var rightWidth = Math.Min(390, width * .46);
        _options = new Grid { RowSpacing = 10, ColumnSpacing = 10 };
        _options.ColumnDefinitions.Add(new() { Width = new GridLength(92) });
        _options.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        AddRow(_sourceLabel, _source); AddFull(_chooseFile); AddFull(_pathLabel); AddFull(_refresh);
        AddFull(_selectAll); AddRow(_filterLabel, _autoFilter); AddRow(_groupsLabel, _recognizeGroups);
        AddRow(_positionLabel, _position);
        var sizes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sizes.Children.Add(_small); sizes.Children.Add(_medium); AddRow(_sizeLabel, sizes);
        AddFull(_counts); AddFull(_changes); AddFull(_status);
        var optionsCard = VisualStyles.Card(_options);
        _optionsHost = new ContentControl { Content = optionsCard, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };
        var right = new ScrollViewer { Content = _optionsHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        ConfigureCandidateGrid();
        _grid.Height = height;
        _grid.Width = Math.Min(_metrics.LaneWidth + _metrics.Gap + 16, width - rightWidth - 20);
        var candidateSurface = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        candidateSurface.Children.Add(_grid); candidateSurface.Children.Add(_groupOverlay);
        var left = new Grid(); left.Children.Add(candidateSurface); left.Children.Add(_development);
        _development.HorizontalAlignment = HorizontalAlignment.Center; _development.VerticalAlignment = VerticalAlignment.Center;
        _development.Visibility = Visibility.Collapsed;
        var page = new Grid { Width = width, Height = height, ColumnSpacing = 20 };
        page.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        page.ColumnDefinitions.Add(new() { Width = new GridLength(rightWidth) });
        page.Children.Add(left); Grid.SetColumn(right, 1); page.Children.Add(right);
        _dialog = new ContentDialog { XamlRoot = (owner.Content as FrameworkElement)?.XamlRoot, Content = page,
            DefaultButton = ContentDialogButton.None, IsPrimaryButtonEnabled = false };
        _dialog.Resources["ContentDialogMaxWidth"] = budget.Width + 48;
        _dialog.Resources["ContentDialogMaxHeight"] = budget.Height + 180;
        Id(_dialog, "dialog"); Id(_source, "source"); Id(_position, "position"); Id(_selectAll, "select-all");
        Id(_autoFilter, "auto-trim"); Id(_recognizeGroups, "recognize-groups"); Id(_small, "small"); Id(_medium, "medium");
        Id(_chooseFile, "choose-wall-file"); Id(_refresh, "refresh"); Id(_grid, "candidates"); Id(_status, "status"); Id(_changes, "planned-changes");
        _source.SelectionChanged += (_, _) =>
        {
            if (_refreshingStrings || !_shown || _adding) return;
            _selected.Clear(); _notice = _error = null; _context = null; _sourceData = null;
            _projection = null; _plan = null; ClearCandidates();
            BeginRead(resetSelection: true); UpdateAvailability();
        };
        _position.SelectionChanged += (_, _) => { if (!_refreshingStrings) SelectionChanged(); };
        _autoFilter.Toggled += (_, _) => Reproject(); _recognizeGroups.Toggled += (_, _) => Reproject();
        _small.Click += (_, _) => SetSize(1); _medium.Click += (_, _) => SetSize(2);
        _selectAll.Click += (_, _) =>
        {
            if (_projection is null) return;
            var addable = _projection.Candidates.Where(candidate => candidate.CanAdd).ToArray();
            var clear = addable.All(IsSelected);
            foreach (var candidate in addable) Select(candidate, !clear);
            _error = _notice = null; SelectionChanged();
        };
        _chooseFile.Click += (_, _) =>
        {
            if (_picking || _adding) return;
            _picking = true; UpdateAvailability();
            Track(PickWallFileAsync());
        };
        _refresh.Click += (_, _) =>
        {
            if (_source.SelectedIndex == 0) Track(ObserveCatalogAsync());
            BeginRead();
        };
        _dialog.PrimaryButtonClick += (_, e) => { e.Cancel = true; StartAdd(); };
        _dialog.CloseButtonClick += (_, e) => { e.Cancel = true; RequestDismiss(); };
        _dialog.Closing += (_, e) =>
        {
            if (_adding || _picking) { e.Cancel = true; RequestDismiss(); }
            else StopRequests();
        };
        RefreshStrings();
    }

    public async Task ShowAsync()
    {
        _shown = true; _catalog.Changed += _catalogChanged;
        BeginRead(); Track(ObserveCatalogAsync());
        try { await OwnedDialogWindow.ShowAsync(_dialog); }
        finally
        {
            StopRequests();
            await DrainOperationsAsync();
            ClosePreviewSource(); _packageReadGate.Dispose(); _packageMediaSlots.Dispose();
            _lifetime.Dispose();
        }
    }

    private void Track(Task task)
    {
        _pendingOperations.RemoveWhere(operation => operation.IsCompletedSuccessfully || operation.IsCanceled);
        _pendingOperations.Add(task);
    }

    private async Task ObserveCatalogAsync()
    {
        _catalogRequest?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _catalogRequest = request;
        try { await _catalog.RequestRefreshAsync().WaitAsync(request.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) { _error = ex.Message; UpdateStatus(); } }
        finally { if (ReferenceEquals(_catalogRequest, request)) _catalogRequest = null; request.Dispose(); }
    }

    private void BeginRead(bool resetSelection = false)
    {
        if (_closed || _dismissRequested || _adding) return;
        _readsSuspended = false;
        _pageRequest?.Cancel(); _preparationRequest?.Cancel();
        var resetPreview = resetSelection || _source.SelectedIndex != 0;
        if (resetPreview) CancelPreview();
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _pageRequest = request;
        var epoch = ++_sourceEpoch; var revision = _catalogRevision;
        if (resetSelection) _selected.Clear();
        _reading = true; _prepared = false; UpdateAvailability(); UpdateStatus();
        _pageRead = ReadPageAsync(epoch, revision, resetPreview, request); Track(_pageRead);
    }

    private async Task ReadPageAsync(int epoch, int catalogRevision, bool resetPreview, CancellationTokenSource request)
    {
        try
        {
            // Changing sources drains protected package reads. Catalog progress
            // keeps independent visible-icon requests alive.
            if (resetPreview)
            {
                await _previewLoad;
                request.Token.ThrowIfCancellationRequested();
                ClosePreviewSource();
            }
            request.Token.ThrowIfCancellationRequested();
            var sourceIndex = _source.SelectedIndex;
            if (sourceIndex is 1 or 2 || sourceIndex == 3 && _packagePath is null) return;
            var context = _context is { } confirmed && _contextCurrent(confirmed)
                ? confirmed : await _readContext(request.Token);
            var source = await _readSource(sourceIndex, _packagePath, request.Token);
            if (!IsCurrent(epoch, request.Token)) return;
            if (_context is { } oldContext && (context.Document.Generation != oldContext.Document.Generation || context.Budget != oldContext.Budget) ||
                _sourceData?.Package is { } oldPackage && oldPackage.PackageSha256 != source.Package?.PackageSha256)
            { _selected.Clear(); _notice = ProductStrings.Get("Import.SourceChanged"); }
            if (_sourceData is { } previous)
                foreach (var key in _selected.Keys.ToArray())
                    if (previous.StartMenuSources.TryGetValue(key, out var old) &&
                        (!source.StartMenuSources.TryGetValue(key, out var updated) || old.SourceSha256 != updated.SourceSha256)) _selected.Remove(key);
            _context = context; _sourceData = source; _error = null;
            _previewRequest ??= CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            Reproject();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (IsCurrent(epoch, request.Token)) { _error = ex.Message; _plan = null; UpdateStatus(); } }
        finally
        {
            if (_sourceEpoch == epoch)
            {
                _reading = false;
                if (ReferenceEquals(_pageRequest, request)) _pageRequest = null;
                UpdateAvailability(); UpdateStatus();
                foreach (var row in _rows) StartRowImage(row);
                if (!_closed && !_dismissRequested && !_readsSuspended && _source.SelectedIndex == 0 && catalogRevision != _catalogRevision) BeginRead();
            }
            request.Dispose();
        }
    }

    private bool IsCurrent(int epoch, CancellationToken token) => !_closed && !_dismissRequested &&
        !token.IsCancellationRequested && epoch == _sourceEpoch;

    private async Task PickWallFileAsync()
    {
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _pickerRequest = request;
        var epoch = _sourceEpoch;
        try
        {
            if (_closed || _dismissRequested || _readsSuspended || _source.SelectedIndex != 3) return;
            request.Token.ThrowIfCancellationRequested();
            var owner = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(_owner));
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(owner);
            picker.FileTypeFilter.Add(".twpack");
            var operation = picker.PickSingleFileAsync();
            _pickerOperation = operation;
            var file = await operation.AsTask(request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (_closed || _dismissRequested || _readsSuspended || epoch != _sourceEpoch || _source.SelectedIndex != 3 || file is null) return;
            // Cancellation preserves a valid preview. Choosing a file invalidates
            // all old content before validation, including when the new file fails.
            _packagePath = file.Path; _selected.Clear(); _context = null; _sourceData = null;
            _projection = null; _plan = null; _notice = _error = null;
            ClearCandidates(); BeginRead(resetSelection: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && !_dismissRequested && !_readsSuspended && epoch == _sourceEpoch) { _error = ex.Message; UpdateStatus(); } }
        finally
        {
            _pickerOperation = null;
            if (ReferenceEquals(_pickerRequest, request)) _pickerRequest = null;
            request.Dispose(); _picking = false; UpdateAvailability();
            if (_dismissRequested) FinishDismiss();
        }
    }

    private void CancelPicker()
    {
        // Cancellation reaches IAsyncInfo; result gates reject late selections.
        // The native window follows the owner/SDK lifecycle; release requires GUI verification.
        _pickerRequest?.Cancel();
        if (_pickerOperation is { Status: Windows.Foundation.AsyncStatus.Started } operation) operation.Cancel();
    }

    private void ConfigureCandidateGrid()
    {
        RefreshItemTemplate();
        _grid.Padding = new Thickness(0);
        ScrollViewer.SetVerticalScrollBarVisibility(_grid, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollMode(_grid, ScrollMode.Enabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_grid, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(_grid, ScrollMode.Disabled);
        _grid.Loaded += OnCandidateGridLoaded;
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
                <ItemsWrapGrid Orientation="Horizontal" CacheLength="0.5" />
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

        _grid.ContainerContentChanging += OnContainerContentChanging;
    }

    private void ClearGroupOutline()
    {
        _hoveredHeader = null;
        _groupOverlay.Children.Clear();
    }

    private void OnCandidateGridLoaded(object sender, RoutedEventArgs e)
    {
        if (_closed || _gridScrollViewer is not null) return;
        _grid.ApplyTemplate();
        _grid.UpdateLayout();
        _gridScrollViewer = FindGridScrollViewer(_grid);
        if (_gridScrollViewer is null) return;
        _gridViewChanged = (_, _) => ClearGroupOutline();
        _gridScrollViewer.ViewChanged += _gridViewChanged;
    }

    private static ScrollViewer? FindGridScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scrollViewer) return scrollViewer;
            if (FindGridScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private void DetachGridScrollViewer()
    {
        if (_gridScrollViewer is { } viewer && _gridViewChanged is { } handler)
            viewer.ViewChanged -= handler;
        _gridScrollViewer = null;
        _gridViewChanged = null;
        _grid.Loaded -= OnCandidateGridLoaded;
        ClearGroupOutline();
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
        var top = origin.Y + _metrics.TitleHeight - _metrics.TitleInteractionHeight;
        var outline = WallGroupVisualFactory.TitleFeedback(_style, element.ActualTheme == ElementTheme.Dark);
        outline.Width = _metrics.LaneWidth; outline.Height = _metrics.TitleInteractionHeight;
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


    private void RefreshItemTemplate()
    {
        var slot = (_tileSize * _metrics.Pitch).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _grid.ItemTemplate = (DataTemplate)XamlReader.Load($"<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Grid Width=\"{slot}\" Height=\"{slot}\" /></DataTemplate>");
    }

    private void SetSize(int size)
    {
        if (_tileSize == size) return;
        _tileSize = size; _small.IsChecked = size == 1; _medium.IsChecked = size == 2;
        _error = null; _plan = null; RefreshItemTemplate(); RefreshCandidates(force: true); SelectionChanged();
    }

    private void Reproject(bool prepare = true)
    {
        if (_adding || _context is null || _sourceData is null) return;
        if (_projection is null || _projection.Generation != _context.Document.Generation ||
            _projection.AutoFilter != _autoFilter.IsOn || _projection.RecognizeGroups != _recognizeGroups.IsOn ||
            !ReferenceEquals(_projectedCandidates, _sourceData.Candidates) ||
            !ReferenceEquals(_projectedIdentities, _context.ExistingIdentities))
        {
            _projection = ImportSourcePlanner.Project(_context.Document, _sourceData.Candidates,
                _recognizeGroups.IsOn, _autoFilter.IsOn, _context.ExistingIdentities);
            _projectedCandidates = _sourceData.Candidates; _projectedIdentities = _context.ExistingIdentities;
            var migrated = ImportSourcePlanner.MigrateSelection(_selected, _projection);
            _selected.Clear(); foreach (var pair in migrated.Selected) _selected.Add(pair.Key, pair.Value);
            if (migrated.RemovedCount > 0) _notice = string.Format(ProductStrings.Get("Import.SelectionRemoved"), migrated.RemovedCount);
        }
        RefreshCandidates(); SelectionChanged();
        if (prepare) BeginPreparation();
    }

    private void BeginPreparation()
    {
        if (_closed || _dismissRequested || _readsSuspended || _adding || _context is null || _sourceData is null || _projection is null) return;
        _preparationRequest?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _preparationRequest = request;
        var epoch = ++_preparationEpoch; var sourceEpoch = _sourceEpoch;
        _preparing = true; _prepared = false; UpdateAvailability(); UpdateStatus();
        Track(PrepareAsync(_context, _sourceData, _projection, sourceEpoch, epoch, request));
    }

    private async Task PrepareAsync(ImportPageContext context, ImportPageSource source, ImportSourceProjection projection,
        int sourceEpoch, int epoch, CancellationTokenSource request)
    {
        try
        {
            // Yield cached candidates to the finite viewport before destination IO begins.
            await Task.Yield();
            request.Token.ThrowIfCancellationRequested();
            var result = await _prepare(context, source, projection, request.Token);
            if (!IsCurrent(sourceEpoch, request.Token) || epoch != _preparationEpoch || !ReferenceEquals(source, _sourceData)) return;
            if (!_contextCurrent(result.Context)) { _notice = ProductStrings.Get("Import.SourceChanged"); return; }
            _context = result.Context; _sourceData = result.Source;
            Reproject(prepare: false);
            _prepared = result.Source.AvailabilityPrepared && result.Context.Covers(_projection!);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (IsCurrent(sourceEpoch, request.Token) && epoch == _preparationEpoch) _error = ex.Message; }
        finally
        {
            if (epoch == _preparationEpoch)
            {
                _preparing = false;
                if (ReferenceEquals(_preparationRequest, request)) _preparationRequest = null;
                UpdateAvailability(); UpdateStatus();
            }
            request.Dispose();
        }
    }

    private void SelectionChanged()
    {
        _plan = null; UpdateSelectionFeedback(); UpdateAvailability(); UpdateStatus();
    }

    private bool IsSelected(ImportProjectedCandidate candidate) => candidate.CanAdd && candidate.SourceKeys
        .Any(key => _selected.GetValueOrDefault(key) == candidate.Source.SelectionIdentity);

    private void Select(ImportProjectedCandidate candidate, bool selected)
    {
        if (selected && candidate.CanAdd) _selected[candidate.SelectionKey] = candidate.Source.SelectionIdentity;
        else foreach (var key in candidate.SourceKeys) _selected.Remove(key);
    }

    private void RefreshCandidates(bool force = false)
    {
        var candidates = _projection?.Candidates.Where(candidate => candidate.Visible).ToArray() ?? [];
        _candidateByKey.Clear();
        foreach (var candidate in candidates) _candidateByKey[candidate.SelectionKey] = candidate;
        if (!force && _grid.ItemsSource is not null && SameRows(_visibleCandidates, candidates))
        {
            foreach (var row in _rows)
            {
                row.Candidate = _candidateByKey[row.Candidate.SelectionKey];
                UpdateRowTooltip(row);
                var changedIcon = _rowIconKeys.GetValueOrDefault(row.Candidate.SelectionKey) != IconStamp(row.Candidate);
                if (changedIcon) row.View.UpdateFrame(null);
                StartRowImage(row, replace: changedIcon);
            }
            foreach (var candidate in candidates) _rowIconKeys[candidate.SelectionKey] = IconStamp(candidate);
            _visibleCandidates = candidates; UpdateSelectionFeedback(); return;
        }
        ClearCandidates(); _visibleCandidates = candidates; ++_renderEpoch;
        if (_projection is null) return;
        var grouped = new CollectionViewSource { IsSourceGrouped = true,
            Source = _projection.Groups.Select(group => new CandidateGroup(group.Title,
                group.Candidates.Where(candidate => candidate.Visible))).Where(group => group.Count > 0).ToArray() };
        foreach (var candidate in candidates) _rowIconKeys[candidate.SelectionKey] = IconStamp(candidate);
        foreach (var group in _projection.Groups) _groupTitles[group.Key] = group.Title;
        _grid.ItemsSource = grouped.View;
    }

    private bool SameRows(IReadOnlyList<ImportProjectedCandidate> left, IReadOnlyList<ImportProjectedCandidate> right)
    {
        if (left.Count != right.Count || _projection!.Groups.Any(group => !_groupTitles.TryGetValue(group.Key, out var title) || title != group.Title)) return false;
        for (var index = 0; index < left.Count; index++)
        {
            var a = left[index]; var b = right[index];
            // Diagnostics and availability can update without replacing visual containers.
            if (a.SelectionKey != b.SelectionKey || a.GroupKey != b.GroupKey || a.Source.Member != b.Source.Member ||
                a.Source.DisplayName != b.Source.DisplayName || a.Source.Identity != b.Source.Identity ||
                !a.SourceKeys.SequenceEqual(b.SourceKeys, StringComparer.Ordinal)) return false;
        }
        return true;
    }

    private string IconStamp(ImportProjectedCandidate candidate) =>
        _sourceData?.StartMenuSources.TryGetValue(candidate.SelectionKey, out var source) == true
            ? source.SourceSha256 + "|" + source.IconKey : _sourceData?.Package?.PackageSha256 ?? "";

    private void ClearCandidates()
    {
        ReleaseRows(); _grid.ItemsSource = null; _visibleCandidates = []; _rowIconKeys.Clear(); _groupTitles.Clear(); ClearGroupOutline();
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (e.ItemContainer.Tag is CandidateRow old) { ReleaseRow(old); e.ItemContainer.Tag = null; }
        if (e.ItemContainer.ContentTemplateRoot is not Grid host) return;
        host.Children.Clear(); e.Handled = true;
        if (_closed || _dismissRequested || e.InRecycleQueue || e.Item is not ImportProjectedCandidate candidate) return;
        candidate = _candidateByKey.GetValueOrDefault(candidate.SelectionKey) ?? candidate;
        // Browsing uses uniform slots. Source geometry remains intact for confirmation.
        var original = candidate.Source.Member;
        var member = original with { LocalBounds = new(0, 0,
            candidate.Source.HasRecordedSize ? original.LocalBounds.Width : _tileSize,
            candidate.Source.HasRecordedSize ? original.LocalBounds.Height : _tileSize) };
        var size = _metrics.WidthOf(_tileSize);
        var card = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top };
        var view = WallMemberVisualFactory.Create(member, null, _metrics, _dialog.XamlRoot?.RasterizationScale ?? 1,
            _style, interactive: false, externalInteraction: true);
        view.IsHitTestVisible = false;
        card.Children.Add(new Viewbox { Child = view, Stretch = Stretch.Uniform });
        if (member is ClockMember clock)
        {
            card.Children.Add(new TextBlock { Text = TileWall.Core.Components.ClockTextFormatter.Format(DateTimeOffset.Now,
                clock.TimeFormat, clock.DateFormat, System.Globalization.CultureInfo.CurrentCulture).TimeLine,
                FontSize = _tileSize == 1 ? 12 : 20, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        }
        var selection = WallMemberVisualFactory.AddSelection(card, _style);
        var toggle = new ToggleButton { Content = card, Width = size, Height = size, Padding = new Thickness(0),
            BorderThickness = new Thickness(0), MinWidth = 0, MinHeight = 0, CornerRadius = WallCornerStyle.Corners(_style),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var reset = WallMemberVisualFactory.ConfigureInteraction(toggle, card, toggle.CornerRadius, view.SetExternalHover);
        CandidateRow? row = null;
        RoutedEventHandler changed = (_, _) =>
        {
            if (_synchronizingSelection || _adding || _reading || row is null || !row.Candidate.CanAdd) return;
            Select(row.Candidate, toggle.IsChecked == true); _error = _notice = null; SelectionChanged();
        };
        row = new(candidate, member, view, toggle, e.ItemContainer, changed, reset, selection.Outline, selection.Check);
        toggle.Checked += changed; toggle.Unchecked += changed;
        Id(toggle, "candidate-" + candidate.SelectionKey); _rows.Add(row); e.ItemContainer.Tag = row;
        host.Children.Add(toggle); UpdateRowTooltip(row); UpdateSelectionFeedback(); UpdateAvailability();
        StartRowImage(row);
    }

    private void UpdateRowTooltip(CandidateRow row)
    {
        var candidate = row.Candidate;
        var tooltip = candidate.Source.DisplayName;
        if (_sourceData?.StartMenuSources.TryGetValue(candidate.SelectionKey, out var source) == true)
        {
            tooltip += "\n" + source.Candidate.FullPath;
            if (source.Stale) tooltip += "\n" + ProductStrings.Get("Import.Stale");
        }
        if (candidate.ErrorCode is { } error) tooltip += "\n" + ErrorText(error);
        if (candidate.FilterReason != ImportFilterReason.None) tooltip += "\n" + FilterText(candidate.FilterReason);
        ActionIcon.SetTip(row.Toggle, tooltip);
        AutomationProperties.SetName(row.Toggle, candidate.Source.DisplayName);
        AutomationProperties.SetName(row.Container, candidate.Source.DisplayName);
        AutomationProperties.SetHelpText(row.Toggle, tooltip); AutomationProperties.SetHelpText(row.Container, tooltip);
    }

    private void UpdateSelectionFeedback()
    {
        _synchronizingSelection = true;
        try
        {
            foreach (var row in _rows)
            {
                row.Toggle.IsChecked = IsSelected(row.Candidate);
                row.Outline.Visibility = row.Check.Visibility = row.Toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        finally { _synchronizingSelection = false; }
    }

    private void ReleaseRow(CandidateRow row)
    {
        if (!_rows.Remove(row)) return;
        row.Request?.Cancel(); row.Toggle.Checked -= row.Changed; row.Toggle.Unchecked -= row.Changed;
        row.Reset(); row.View.StopTransientFeedback(); row.View.UpdateFrame(null);
    }

    private void ReleaseRows() { foreach (var row in _rows.ToArray()) ReleaseRow(row); }

    private void CancelPreview()
    {
        _previewRequest?.Cancel();
        foreach (var row in _rows) row.Request?.Cancel();
    }

    private void ClosePreviewSource()
    {
        // The caller drains all independent row operations before closing the package.
        if (_packagePreview?.IsCompletedSuccessfully == true) _packagePreview.Result.Dispose();
        _packagePreview = null; _previewRequest?.Dispose(); _previewRequest = null;
    }

    private void StartRowImage(CandidateRow row, bool replace = false)
    {
        if (_closed || _dismissRequested || _readsSuspended || _adding || _reading || _sourceData is null || row.Member is ClockMember) return;
        if (row.Request is not null && !replace) return;
        if (replace) row.Request?.Cancel();
        _previewRequest ??= CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (_previewRequest.IsCancellationRequested) return;
        var source = _sourceData; var epoch = _renderEpoch;
        var request = CancellationTokenSource.CreateLinkedTokenSource(_previewRequest.Token);
        row.Request = request;
        var pixels = Math.Clamp((int)Math.Ceiling(_metrics.WidthOf(_tileSize) * (_dialog.XamlRoot?.RasterizationScale ?? 1)), 64, 256);
        var cacheKey = System.Text.Json.JsonSerializer.Serialize(new { row.Candidate.SelectionKey, Stamp = IconStamp(row.Candidate),
            row.Member.Images, row.Member.Visual.BackgroundMode, CurrentImage = row.Member.Carousel?.CurrentImageId, Pixels = pixels });
        if (!replace && row.ImageKey == cacheKey) { row.Request = null; request.Dispose(); return; }
        row.ImageKey = cacheKey;
        if (_frames.TryGetValue(cacheKey, out var cached)) { row.View.UpdateFrame(cached); row.Request = null; request.Dispose(); return; }
        _previewRowLoads++;
        var task = LoadRowImageAsync(row, source, epoch, pixels, cacheKey, request);
        _previewOperations.RemoveWhere(operation => operation.IsCompleted);
        _previewOperations.Add(task); _previewLoad = Task.WhenAll(_previewOperations); Track(task);
    }

    private async Task LoadRowImageAsync(CandidateRow row, ImportPageSource source, int epoch, int pixels,
        string cacheKey, CancellationTokenSource request)
    {
        try
        {
            PuzzleFrameResult? result = null;
            if (source.Package is { } package && row.Member.Images.Media is { } media)
            {
                // Bound retained original bytes as well as decoded frames. ZIP shares a
                // stream; each ready original then decodes independently in the image service.
                await _packageMediaSlots.WaitAsync(request.Token);
                try
                {
                    _packagePreview ??= _reader.OpenImportAsync(package, _previewRequest!.Token);
                    var batch = await _packagePreview;
                    await _packageReadGate.WaitAsync(request.Token);
                    TileWall.Core.Storage.PreparedMediaAsset asset;
                    try { asset = (await batch.PrepareMediaAsync([media], token: request.Token)).Single(); }
                    finally { _packageReadGate.Release(); }
                    result = await _images.LoadPackageThumbnailAsync(asset, pixels,
                        row.Member.Visual.BackgroundMode == MemberBackgroundMode.Blur, request.Token);
                }
                finally { _packageMediaSlots.Release(); }
            }
            else if (source.StartMenuSources.TryGetValue(row.Candidate.SelectionKey, out var original) && _loadIcon is not null)
                result = await _loadIcon(original, pixels, request.Token);
            if (result?.Frame is null)
            {
                var path = source.StartMenuSources.TryGetValue(row.Candidate.SelectionKey, out var startMenu)
                    ? startMenu.Candidate.FullPath : row.Candidate.Source.Identity?.Target ?? row.Member.TargetInput;
                result = await _images.LoadMemberFrameAsync(row.Member, path, pixels, pixels,
                    ct: request.Token, purpose: MemberImagePurpose.Thumbnail);
            }
            if (_closed || _dismissRequested || request.IsCancellationRequested || epoch != _renderEpoch || !_rows.Contains(row)) return;
            if (result.Frame is { } frame)
            {
                _frames[cacheKey] = frame; _frameOrder.Enqueue(cacheKey);
                while (_frames.Count > 32 && _frameOrder.TryDequeue(out var old)) _frames.Remove(old);
            }
            row.View.UpdateFrame(result.Frame);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception) { /* A failed thumbnail keeps the shared placeholder. */ }
        finally
        {
            if (ReferenceEquals(row.Request, request))
            {
                row.Request = null;
                if (request.IsCancellationRequested) row.ImageKey = null;
            }
            request.Dispose();
            if (--_previewRowLoads == 0)
            {
                if (_packagePreview?.IsCompletedSuccessfully == true) _packagePreview.Result.Dispose();
                _packagePreview = null;
            }
        }
    }

    private void StartAdd()
    {
        if (_closed || _dismissRequested || _adding || _reading || _picking || _context is null || _projection is null ||
            _sourceData is null || !_prepared || _preparing || !_projection.Candidates.Any(IsSelected)) return;
        if (!_contextCurrent(_context)) { _notice = ProductStrings.Get("Import.SourceChanged"); BeginRead(resetSelection: true); return; }
        _plan = ImportSourcePlanner.TryPlan(_context.Document, _context.Document.Generation, _projection,
            _selected.Keys, new GridSize(_tileSize, _tileSize), _position.SelectedIndex == 1 ? ImportInsertionPosition.Tail : ImportInsertionPosition.Head,
            _metrics, _context.Budget);
        if (_plan.Status != ImportPlanStatus.Ready) { UpdateStatus(); return; }
        var epoch = _sourceEpoch; var plan = _plan; var source = _sourceData;
        var request = new ImportPageRequest(_context, source, _projection, plan,
            () => epoch == _sourceEpoch && ReferenceEquals(_plan, plan) && ReferenceEquals(_sourceData, source));
        _adding = true; _error = null; CancelPreview(); UpdateAvailability(); UpdateStatus();
        // Register the complete operation before its first await, including the
        // preview drain, source checks, media preparation, commit and UI acceptance.
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _addTask = completed.Task; Track(_addTask);
        _ = AddCoreAsync(request, completed);
    }

    private async Task AddCoreAsync(ImportPageRequest request, TaskCompletionSource completed)
    {
        try
        {
            await _previewLoad;
            ClosePreviewSource();
            var result = await _save(request);
            if (result.Error is null) _dismissRequested = true;
            else
            {
                _error = result.Error;
                if (result.RefreshRequired && !_dismissRequested)
                { _selected.Clear(); _context = null; _notice = ProductStrings.Get("Import.SourceChanged"); }
            }
            _adding = false;
            if (!_dismissRequested && !_readsSuspended && result.RefreshRequired) BeginRead(resetSelection: true);
            UpdateAvailability(); UpdateStatus();
        }
        catch (Exception ex) { _error = string.Format(ProductStrings.Get("Import.Failed"), ex.Message); _adding = false; UpdateAvailability(); UpdateStatus(); }
        finally
        {
            _adding = false;
            if (_dismissRequested) FinishDismiss();
            completed.TrySetResult();
        }
    }

    private void UpdateAvailability()
    {
        var implemented = _source.SelectedIndex is 0 or 3;
        _optionsHost.IsEnabled = !_closed && !_dismissRequested && !_adding && !_picking;
        _chooseFile.Visibility = _pathLabel.Visibility = _source.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        _pathLabel.Text = string.IsNullOrEmpty(_packagePath) ? "" : Path.GetFileName(_packagePath);
        ToolTipService.SetToolTip(_pathLabel, _packagePath);
        AutomationProperties.SetHelpText(_pathLabel, _packagePath ?? "");
        _grid.Visibility = implemented ? Visibility.Visible : Visibility.Collapsed;
        _development.Visibility = implemented ? Visibility.Collapsed : Visibility.Visible;
        _autoFilter.IsEnabled = _recognizeGroups.IsEnabled = _small.IsEnabled = _medium.IsEnabled = _position.IsEnabled = implemented;
        _refresh.IsEnabled = implemented && !_reading;
        var addable = _projection?.Candidates.Where(candidate => candidate.CanAdd).ToArray() ?? [];
        var count = addable.Count(IsSelected);
        _selectAll.IsEnabled = implemented && !_reading && addable.Length > 0;
        _selectAll.IsChecked = count == 0 ? false : count == addable.Length ? true : null;
        _dialog.IsPrimaryButtonEnabled = !_closed && !_dismissRequested && !_adding && !_reading && !_picking &&
            implemented && count > 0 && _prepared && !_preparing && _context is not null && _contextCurrent(_context);
        foreach (var row in _rows) row.Toggle.IsEnabled = !_adding && !_reading && row.Candidate.CanAdd;
    }

    private void UpdateStatus()
    {
        var projection = _projection;
        var selected = projection?.Candidates.Count(IsSelected) ?? 0;
        _counts.Text = string.Join("\n", string.Format(ProductStrings.Get("Import.ScannedCount"), _sourceData?.Scanned ?? 0),
            string.Format(ProductStrings.Get("Import.FilteredCount"), projection?.FilteredCount ?? 0),
            string.Format(ProductStrings.Get("Import.CandidateCount"), projection?.Candidates.Count(candidate => candidate.Visible) ?? 0),
            string.Format(ProductStrings.Get("Import.SelectedCount"), selected));
        if (projection is not null)
            foreach (var reason in projection.Candidates.Where(candidate => candidate.FilterReason != ImportFilterReason.None).GroupBy(candidate => candidate.FilterReason))
                _counts.Text += "\n" + FilterText(reason.Key) + ": " + reason.Count();
        var chosen = projection?.Candidates.Where(IsSelected).ToArray() ?? [];
        var overwritten = chosen.Count(candidate => candidate.ReplacedMemberId is not null);
        _changes.Text = string.Format(ProductStrings.Get("Import.PlannedChanges"), chosen.Length - overwritten, overwritten);
        if (overwritten > 0) _changes.Text += "\n" + ProductStrings.Get("Import.OverwriteNotice");
        var messages = new List<string>();
        if (_adding) messages.Add(ProductStrings.Get("Import.Adding"));
        else if (_reading || _preparing || _sourceData?.Refreshing == true) messages.Add(ProductStrings.Get("Import.Updating"));
        if (_notice is not null) messages.Add(_notice);
        if (_error is not null) messages.Add(_error);
        if (_sourceData is { } source && (source.ReadErrors != 0 || source.Skipped != 0))
            messages.Add(string.Format(ProductStrings.Get("Import.ReadErrors"), source.ReadErrors, source.Skipped));
        if (_sourceData?.ErrorCode is { } code) messages.Add(ErrorText(code));
        if (_plan?.Status == ImportPlanStatus.Rejected) messages.Add(ProductStrings.Get("Import.LayoutRejected"));
        if (_source.SelectedIndex is 1 or 2) messages.Add(ProductStrings.Get("Import.Developing"));
        _status.Text = string.Join("\n", messages);
    }

    public bool HasUnsavedDraft => false;
    public string? TrySaveNow() => ProductStrings.Get("Import.Confirm");
    public void DiscardDraft() { _selected.Clear(); }
    public Task<ExitDecision> ConfirmExitAsync() => Task.FromResult(ExitDecision.DiscardAndExit);
    public async Task WaitForPendingAsync()
    {
        // Never wait for modalClosed: the host closes the page only after this
        // first drain. Reading can stop; an already admitted add cannot cancel.
        _readsSuspended = true; CancelPicker(); _catalogRequest?.Cancel(); _pageRequest?.Cancel(); _preparationRequest?.Cancel(); CancelPreview();
        await _addTask;
        await DrainOperationsAsync();
    }
    private async Task DrainOperationsAsync()
    {
        while (true)
        {
            var pending = _pendingOperations.Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0) return;
            try { await Task.WhenAll(pending); } catch (OperationCanceledException) { }
        }
    }
    public void Close() => RequestDismiss();
    public void RequestDismiss()
    {
        if (_closed) return;
        _dismissRequested = true; CancelPicker(); _pageRequest?.Cancel(); _preparationRequest?.Cancel(); CancelPreview(); UpdateAvailability();
        if (!_adding && !_picking) FinishDismiss();
    }
    private void FinishDismiss() { StopRequests(); _dialog.Hide(); }
    private void StopRequests()
    {
        if (_closed) return;
        _closed = true; CancelPicker(); _lifetime.Cancel(); _pageRequest?.Cancel(); _preparationRequest?.Cancel(); CancelPreview();
        _catalog.Changed -= _catalogChanged; DetachGridScrollViewer(); ReleaseRows(); _frames.Clear(); _frameOrder.Clear();
    }

    public void RefreshStrings()
    {
        _refreshingStrings = true;
        try
        {
            var source = _source.SelectedIndex; var position = _position.SelectedIndex;
            _source.Items.Clear(); foreach (var key in new[] { "Import.Source.StartMenu", "Import.SourceDesktop", "Import.Source.InstalledApplications", "Import.Source.CustomSettings" }) _source.Items.Add(ProductStrings.Get(key));
            _source.SelectedIndex = source is >= 0 and <= 3 ? source : 0;
            _position.Items.Clear(); _position.Items.Add(ProductStrings.Get("Import.Position.Head")); _position.Items.Add(ProductStrings.Get("Import.Position.Tail"));
            _position.SelectedIndex = position == 1 ? 1 : 0;
        }
        finally { _refreshingStrings = false; }
        _dialog.Title = ProductStrings.Get("Import.Title"); _dialog.PrimaryButtonText = ProductStrings.Get("Import.Confirm");
        _dialog.CloseButtonText = ProductStrings.Get("Common.Cancel");
        _sourceLabel.Text = ProductStrings.Get("Import.Source"); _filterLabel.Text = ProductStrings.Get("Import.AutoFilter");
        _groupsLabel.Text = ProductStrings.Get("Import.RecognizeGroups"); _sizeLabel.Text = ProductStrings.Get("Editor.TileSize");
        _positionLabel.Text = ProductStrings.Get("Import.Position"); _selectAll.Content = ProductStrings.Get("Import.SelectAll");
        _development.Text = ProductStrings.Get("Import.Developing");
        ActionIcon.Set(_chooseFile, ProductStrings.Get("Import.ChooseWallFile"), ActionIcon.OpenFile);
        ActionIcon.Set(_refresh, ProductStrings.Get("Import.Refresh"), ActionIcon.Refresh);
        ActionIcon.SetIcon(_small, ProductStrings.Get("Import.Small"), ActionIcon.PresetSmall);
        ActionIcon.SetIcon(_medium, ProductStrings.Get("Import.Medium"), ActionIcon.PresetMedium);
        _small.IsChecked = _tileSize == 1; _medium.IsChecked = _tileSize == 2;
        ActionIcon.DecorateDialog(_dialog, ActionIcon.Import, ActionIcon.Delete, ActionIcon.Cancel);
        if (_shown && !_adding) RefreshCandidates(force: true);
        UpdateAvailability(); UpdateStatus();
    }

    private static string FilterText(ImportFilterReason reason) => ProductStrings.Get(reason switch
    {
        ImportFilterReason.AlreadyInGroup => "Import.FilterReason.AlreadyInGroup",
        ImportFilterReason.MissingTarget => "Import.FilterReason.MissingTarget",
        _ => "Import.Exclusion." + reason,
    });
    private static string ErrorText(string code) => ProductStrings.Get(code switch
    {
        "InvalidTarget" => "Import.InvalidTarget", "UnsupportedProtocol" => "Import.UnsupportedProtocol",
        "SourceChanged" => "Import.SourceChanged", "SourceTooLarge" => "Import.SourceTooLarge",
        "UnsafeSource" => "Import.UnsafeSource", "INVALID_SOURCE_GEOMETRY" or "MEMBER_SIZE_LIMIT" => "Import.LayoutRejected",
        "CacheInvalid" or "CacheWriteFailed" => "Import.CacheUnavailable", "RefreshFailed" => "Import.RefreshFailed", _ => "Import.SourceUnavailable",
    });
    private void AddRow(TextBlock label, FrameworkElement value)
    {
        var row = _options.RowDefinitions.Count; _options.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(label, row); Grid.SetRow(value, row); Grid.SetColumn(value, 1);
        AutomationProperties.SetLabeledBy(value, label); _options.Children.Add(label); _options.Children.Add(value);
    }
    private void AddFull(FrameworkElement value)
    {
        var row = _options.RowDefinitions.Count; _options.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(value, row); Grid.SetColumnSpan(value, 2); _options.Children.Add(value);
    }
    private static TextBlock Label() => new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static void Id(DependencyObject element, string id) => AutomationProperties.SetAutomationId(element, "import-" + id);
    private sealed class CandidateGroup(string? title, IEnumerable<ImportProjectedCandidate> items) : List<ImportProjectedCandidate>(items)
    {
        public string? Title { get; } = title;
    }
    private sealed class CandidateRow(ImportProjectedCandidate candidate, WallMember member, PuzzleCanvasView view,
        ToggleButton toggle, DependencyObject container, RoutedEventHandler changed, Action reset, Border outline, Border check)
    {
        public ImportProjectedCandidate Candidate { get; set; } = candidate;
        public WallMember Member { get; } = member;
        public PuzzleCanvasView View { get; } = view;
        public ToggleButton Toggle { get; } = toggle;
        public DependencyObject Container { get; } = container;
        public RoutedEventHandler Changed { get; } = changed;
        public Action Reset { get; } = reset;
        public Border Outline { get; } = outline;
        public Border Check { get; } = check;
        public CancellationTokenSource? Request { get; set; }
        public string? ImageKey { get; set; }
    }
}
