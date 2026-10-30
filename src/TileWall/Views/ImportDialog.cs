using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.Globalization;
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

/// <summary>One source selection session; shared Core owns all grouping and source geometry.</summary>
internal sealed class ImportDialog : IWallDialogSession
{
    private static readonly ImportPageSourceKind[] SourceKinds = [ImportPageSourceKind.StartMenu, ImportPageSourceKind.Desktop,
        ImportPageSourceKind.InstalledApplications, ImportPageSourceKind.SystemFeatures, ImportPageSourceKind.WallPackage,
        ImportPageSourceKind.CurrentWall, ImportPageSourceKind.RecycleBin];
    private static readonly string[] SourceLabels = ["Import.Source.StartMenu", "Import.SourceDesktop", "Import.Source.InstalledApplications",
        "Import.Source.SystemFeatures", "Import.Source.CustomSettings", "Import.Source.CurrentWall", "Import.Source.RecycleBin"];
    private static readonly string[] StatisticLabels = ["Import.Stats.Acquired", "Import.Stats.Filtered", "Import.Stats.Candidates", "Import.Stats.Selected",
        "Import.Stats.SystemFeature", "Import.Stats.Program", "Import.Stats.File", "Import.Stats.Folder", "Import.Stats.WebLink", "Import.Stats.ProtocolLink",
        "Import.Stats.Component", "Import.Stats.DuplicateTarget", "Import.Stats.MissingTarget", "Import.Stats.NonPrimaryEntry"];
    private readonly Window _owner;
    private readonly ContentDialog _dialog;
    private readonly WallImageService _images;
    private readonly StartMenuImportCoordinator _catalog;
    private readonly WallPackageReader _reader;
    private readonly Func<CancellationToken, Task<ImportPageContext>> _readContext;
    private readonly Func<ImportPageSourceKind, string?, CancellationToken, Task<ImportPageSource>> _readSource;
    private readonly Func<ImportPageContext, ImportPageSource, ImportSourceProjection, CancellationToken, Task<ImportPagePreparation>> _prepare;
    private readonly Func<ImportPageRequest, Task<ImportPageResult>> _save;
    private readonly Func<ImportPageExportRequest, Task<ImportPageResult>> _saveExport;
    private readonly Func<ImportPageContext, bool> _contextCurrent;
    private readonly Func<ImportSourceCandidate, int, int, CancellationToken, Task<PuzzleFrameResult>>? _loadIcon;
    private readonly string? _initialExportDirectory;
    private readonly string _exportFileTimestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    private readonly Func<IReadOnlyList<string>, long, CancellationToken, Task<string?>>? _restoreRecycled;
    private readonly WallVisualStyle _style;
    private readonly WallMetrics _metrics = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _pendingOperations = [], _previewOperations = [];
    private readonly Dictionary<string, string> _selected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImportProjectedCandidate> _candidateByKey = new(StringComparer.Ordinal);
    private readonly HashSet<CandidateRow> _rows = [];
    private readonly Dictionary<string, PuzzleFrame> _frames = new(StringComparer.Ordinal);
    private const long PreviewFrameLimitBytes = 64L * 1024 * 1024;
    private readonly LinkedList<string> _frameOrder = new();
    private readonly Dictionary<string, LinkedListNode<string>> _frameNodes = new(StringComparer.Ordinal);
    private long _frameBytes;
    private readonly Dictionary<string, ImportTargetRecognition> _recognizedTargets = new(StringComparer.Ordinal);
    private ImportSourceProjection? _recognitionProjection;
    private ImportPageContext? _recognitionContext;
    private IReadOnlyDictionary<string, string>? _recognitionFacts;
    private bool _recognitionReady;
    private readonly SemaphoreSlim _packageMediaSlots = new(2, 2);
    private PackagePreviewSource? _packageSource;
    private IReadOnlyList<ImportSourceCandidate>? _projectedCandidates;
    private IReadOnlyDictionary<string, string>? _projectedIdentities;
    private readonly ComboBox _source = new();
    private readonly IconSelector _mode = new(ActionIcon.ImportAllMirrored, ActionIcon.Share),
        _position = new(ActionIcon.PreviousItem, ActionIcon.NextItem);
    private readonly CheckBox _selectAll = new() { IsThreeState = true };
    private readonly TextBox _filter = new() { MinWidth = 0, FontSize = 12 };
    private readonly ToggleSwitch _autoFilter = new() { IsOn = true, OnContent = "", OffContent = "" },
        _recognizeGroups = new() { IsOn = true, OnContent = "", OffContent = "" };
    private readonly ToggleButton _small = new(), _medium = new();
    private readonly Button _chooseFile = new() { MinWidth = 32, Width = 32, Padding = new Thickness(0) };
    private readonly TextBox _path = new() { IsReadOnly = true, MinWidth = 0, FontSize = 12 };
    private readonly TextBlock[] _optionLabels = Enumerable.Range(0, 9).Select(_ => Label()).ToArray();
    private readonly TextBlock[] _numbers = Enumerable.Range(0, 14).Select(_ => new TextBlock { Text = "0", FontSize = 14,
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.NoWrap }).ToArray();
    private readonly TextBlock[] _numberLabels = Enumerable.Range(0, 14).Select(_ => new TextBlock { FontSize = 10,
        TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Center }).ToArray();
    private readonly Border[] _statisticCells = new Border[14];
    private readonly Grid[] _statisticRows = Enumerable.Range(0, 3).Select(_ => new Grid
        { ColumnSpacing = 4, RowSpacing = 4, HorizontalAlignment = HorizontalAlignment.Left }).ToArray();
    private readonly StackPanel _statistics = new() { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _statisticsCard;
    private readonly Grid _page;
    private readonly ColumnDefinition _statisticsColumn;
    private readonly TextBlock _status = Label();
    private readonly Grid _options = new() { RowSpacing = 6, ColumnSpacing = 8 };
    private readonly ContentControl _optionsHost;
    private readonly ItemsRepeater _repeater = new() { HorizontalCacheLength = .5, VerticalCacheLength = .5 };
    private readonly ImportPreviewLayout _previewLayout = new();
    private readonly Dictionary<UIElement, (int Epoch, int Index, PreviewItem Item)> _preparedElements = [];
    private readonly ObservableCollection<string> _previewKeys = [];
    private readonly ScrollViewer _preview, _statusScroll;
    private IReadOnlyList<PreviewItem> _previewItems = [];
    private readonly Action _catalogChanged;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clockTimer;
    private CancellationTokenSource? _pageRequest, _previewRequest, _catalogRequest, _pickerRequest, _preparationRequest;
    private Windows.Foundation.IAsyncOperation<Microsoft.Windows.Storage.Pickers.PickFileResult>? _pickerOperation;
    private Task _previewLoad = Task.CompletedTask, _addTask = Task.CompletedTask;
    private ImportPageContext? _context;
    private ImportPageSource? _sourceData;
    private ImportSourceProjection? _projection;
    private ImportSourceProjection? _previewProjection;
    private string _previewFilter = "";
    private int _previewTileSize;
    private double _previewHeight, _previewScale;
    private XamlRoot? _previewRoot;
    private ImportSourcePlan? _plan;
    private string? _packagePath, _exportPath, _notice, _error;
    private string? _statisticLayoutKey;
    private readonly double _statisticsMaximumColumnWidth, _statisticsPreviewWidth;
    private bool _shown, _closed, _dismissRequested, _reading, _adding, _picking, _refreshingStrings, _readsSuspended,
        _preparing, _prepared, _synchronizingSelection, _exportPathPicked, _updatingPreviewItems;
    private int _sourceEpoch, _renderEpoch, _preparationEpoch, _tileSize = 1, _catalogRevision, _lastImportSource;
    private ImportPageMode Mode => _mode.SelectedIndex == 1 ? ImportPageMode.Export : ImportPageMode.Import;
    private ImportPageSourceKind SourceKind => SourceKinds[Math.Clamp(_source.SelectedIndex, 0, SourceKinds.Length - 1)];
    private bool IsRecycle => Mode == ImportPageMode.Import && SourceKind == ImportPageSourceKind.RecycleBin;
    private bool Attributed => Mode == ImportPageMode.Export || SourceKind is ImportPageSourceKind.WallPackage or ImportPageSourceKind.RecycleBin;

    public ImportDialog(Window owner, WallImageService images, StartMenuImportCoordinator catalog, WallPackageReader reader,
        Func<CancellationToken, Task<ImportPageContext>> readContext,
        Func<ImportPageSourceKind, string?, CancellationToken, Task<ImportPageSource>> readSource,
        Func<ImportPageContext, ImportPageSource, ImportSourceProjection, CancellationToken, Task<ImportPagePreparation>> prepare,
        Func<ImportPageRequest, Task<ImportPageResult>> save, Func<ImportPageExportRequest, Task<ImportPageResult>> saveExport,
        Func<ImportPageContext, bool> contextCurrent, WallVisualStyle style,
        Func<ImportSourceCandidate, int, int, CancellationToken, Task<PuzzleFrameResult>>? loadIcon = null,
        string? initialExportDirectory = null,
        Func<IReadOnlyList<string>, long, CancellationToken, Task<string?>>? restoreRecycled = null)
    {
        _owner = owner; _images = images; _catalog = catalog; _reader = reader;
        _readContext = readContext; _readSource = readSource; _prepare = prepare; _save = save; _saveExport = saveExport;
        _contextCurrent = contextCurrent; _style = style; _loadIcon = loadIcon; _initialExportDirectory = initialExportDirectory;
        _restoreRecycled = restoreRecycled;
        if (!string.IsNullOrWhiteSpace(initialExportDirectory)) _exportPath = Path.Combine(initialExportDirectory, DefaultExportFileName());
        _catalogChanged = () => owner.DispatcherQueue.TryEnqueue(() => SourceChanged(ImportPageSourceKind.StartMenu));
        _clockTimer = owner.DispatcherQueue.CreateTimer(); _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, _) => { foreach (var row in _rows) row.Clock?.Refresh(DateTimeOffset.Now); };
        var budget = OwnedDialogWindow.ContentBudget(owner);
        const double rightWidth = 748, columnGap = 16;
        var previewWidth = _metrics.WallWidth(1);
        _statisticsPreviewWidth = previewWidth;
        _statisticsMaximumColumnWidth = Math.Max(0, budget.Width - previewWidth - columnGap);
        _options.ColumnDefinitions.Add(new() { Width = new GridLength(92) });
        _options.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var pathRow = new Grid { ColumnSpacing = 6 };
        pathRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        pathRow.Children.Add(_path); Grid.SetColumn(_chooseFile, 1); pathRow.Children.Add(_chooseFile);
        var sizes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sizes.Children.Add(_small); sizes.Children.Add(_medium);
        FrameworkElement[] values = [_mode, _source, pathRow, _autoFilter, _recognizeGroups, sizes, _position, _selectAll, _filter];
        for (var index = 0; index < values.Length; index++) AddOption(index, values[index]);
        _optionsHost = new ContentControl { Content = VisualStyles.Card(_options), HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };
        foreach (var row in _statisticRows)
        {
            for (var column = 0; column < 4; column++) row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            _statistics.Children.Add(row);
        }
        for (var statisticIndex = 0; statisticIndex < StatisticLabels.Length; statisticIndex++)
        {
            var box = new Grid { ColumnSpacing = 4, Margin = new Thickness(2) };
            box.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            box.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            box.Children.Add(_numbers[statisticIndex]); Grid.SetColumn(_numberLabels[statisticIndex], 1); box.Children.Add(_numberLabels[statisticIndex]);
            var cell = VisualStyles.InputFrame(); cell.Child = box;
            _statisticCells[statisticIndex] = cell;
            var rowIndex = statisticIndex < 4 ? 0 : statisticIndex < 11 ? 1 : 2;
            var indexInRow = statisticIndex < 4 ? statisticIndex : statisticIndex < 11 ? statisticIndex - 4 : statisticIndex - 11;
            Grid.SetRow(cell, 0); Grid.SetColumn(cell, indexInRow); _statisticRows[rowIndex].Children.Add(cell);
            Id(_numbers[statisticIndex], "stat-" + StatisticLabels[statisticIndex][13..].ToLowerInvariant());
        }
        var rightBody = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        rightBody.Children.Add(_optionsHost);
        _statisticsCard = VisualStyles.Card(_statistics);
        rightBody.Children.Add(_statisticsCard);
        _status.MaxHeight = 70;
        _statusScroll = new ScrollViewer { Content = _status, MaxHeight = 70, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        rightBody.Children.Add(_statusScroll);
        var right = new ScrollViewer { Content = rightBody, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _repeater.Layout = _previewLayout;
        _previewLayout.ElementRealized = EnsureElementPrepared;
        _repeater.ItemTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Grid /></DataTemplate>");
        _repeater.ItemsSource = _previewKeys;
        _repeater.ElementPrepared += (_, e) => PrepareElement(e.Element, e.Index);
        _repeater.ElementIndexChanged += (_, e) => PrepareElement(e.Element, e.NewIndex);
        _repeater.ElementClearing += (_, e) => ClearElement(e.Element);
        _repeater.Loaded += (_, _) =>
        {
            if (_previewRoot is not null || _repeater.XamlRoot is not { } root) return;
            _previewRoot = root; _previewScale = root.RasterizationScale; root.Changed += OnPreviewRootChanged;
        };
        _preview = new ScrollViewer { Content = _repeater, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollMode = ScrollMode.Enabled };
        _preview.SizeChanged += (_, _) => RefreshPreview();
        var page = new Grid { Width = previewWidth + columnGap + rightWidth, Height = Math.Min(520, budget.Height), ColumnSpacing = columnGap };
        _page = page;
        _statisticsColumn = new() { Width = new GridLength(rightWidth) };
        page.ColumnDefinitions.Add(new() { Width = new GridLength(previewWidth) }); page.ColumnDefinitions.Add(_statisticsColumn);
        page.Children.Add(_preview); Grid.SetColumn(right, 1); page.Children.Add(right);
        rightBody.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Height > 0) page.Height = Math.Min(budget.Height, Math.Ceiling(e.NewSize.Height));
        };
        _dialog = new ContentDialog { XamlRoot = (owner.Content as FrameworkElement)?.XamlRoot, Content = page, DefaultButton = ContentDialogButton.None };
        _dialog.Resources["ContentDialogMaxWidth"] = budget.Width + 48; _dialog.Resources["ContentDialogMaxHeight"] = budget.Height + 180;
        Id(_dialog, "dialog"); Id(_mode, "mode"); Id(_source, "source"); Id(_path, "path"); Id(_chooseFile, "choose-wall-file");
        Id(_autoFilter, "auto-trim"); Id(_recognizeGroups, "recognize-groups"); Id(_small, "small"); Id(_medium, "medium");
        Id(_position, "position"); Id(_selectAll, "select-all"); Id(_filter, "filter"); Id(_repeater, "candidates"); Id(_status, "status");
        _mode.SelectionChanged += (_, _) =>
        {
            if (_refreshingStrings || !_shown || _adding) return;
            _refreshingStrings = true;
            _source.SelectedIndex = Mode == ImportPageMode.Export ? 5 : _lastImportSource;
            _refreshingStrings = false; ResetSource();
        };
        _source.SelectionChanged += (_, _) =>
        {
            if (_refreshingStrings || !_shown || _adding) return;
            if (Mode == ImportPageMode.Import && SourceKind != ImportPageSourceKind.CurrentWall) _lastImportSource = _source.SelectedIndex;
            ResetSource();
        };
        _autoFilter.Toggled += (_, _) => { if (!_refreshingStrings) Reproject(); };
        _recognizeGroups.Toggled += (_, _) => { if (!_refreshingStrings) Reproject(); };
        _position.SelectionChanged += (_, _) => { if (!_refreshingStrings) SelectionChanged(); };
        _small.Click += (_, _) => SetSize(1); _medium.Click += (_, _) => SetSize(2);
        _filter.TextChanged += (_, _) =>
        {
            if (_refreshingStrings || _adding) return;
            RemoveFilteredSelections();
            _plan = null;
            RefreshPreview();
            SelectionChanged();
        };
        _selectAll.Click += (_, _) =>
        {
            if (_projection is null || _adding) return;
            var addable = FilteredAddableCandidates(); var clear = addable.All(IsSelected);
            foreach (var candidate in addable) Select(candidate, !clear);
            _error = _notice = null; SelectionChanged();
        };
        _chooseFile.Click += (_, _) =>
        {
            if (_picking || _adding) return;
            _picking = true; UpdateAvailability(); Track(PickPathAsync());
        };
        _dialog.PrimaryButtonClick += (_, e) => { e.Cancel = true; StartOperation(); };
        _dialog.CloseButtonClick += (_, e) => { e.Cancel = true; RequestDismiss(); };
        _dialog.Closing += (_, e) => { if (_adding || _picking) { e.Cancel = true; RequestDismiss(); } else StopRequests(); };
        RefreshStrings();
    }

    public async Task ShowAsync()
    {
        _shown = true; _catalog.Changed += _catalogChanged; _clockTimer.Start();
        BeginRead(resetSelection: true);
        if (SourceKind == ImportPageSourceKind.StartMenu) Track(ObserveCatalogAsync());
        try { await OwnedDialogWindow.ShowAsync(_dialog); }
        finally
        {
            StopRequests(); await DrainOperationsAsync(); ClosePreviewSource();
            _pendingOperations.Clear(); _previewOperations.Clear(); _previewLoad = Task.CompletedTask;
            _packageMediaSlots.Dispose(); _lifetime.Dispose();
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
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _catalogRequest = request;
        try { await _catalog.RequestRefreshAsync().WaitAsync(request.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && !_dismissRequested) { _error = ex.Message; UpdateStatus(); } }
        finally { if (ReferenceEquals(_catalogRequest, request)) _catalogRequest = null; request.Dispose(); }
    }

    internal void SourceChanged(ImportPageSourceKind kind)
    {
        if (_closed || _dismissRequested || _readsSuspended || _adding || SourceKind != kind) return;
        _catalogRevision++;
        if (!_reading) BeginRead();
    }

    private void ResetSource()
    {
        var wasRefreshingStrings = _refreshingStrings;
        _refreshingStrings = true;
        try { _filter.Text = ""; }
        finally { _refreshingStrings = wasRefreshingStrings; }
        _recognizedTargets.Clear();
        _selected.Clear(); _notice = _error = null; _context = null; _sourceData = null; _projection = null; _plan = null;
        _projectedCandidates = null; _projectedIdentities = null; _previewProjection = null;
        _recognitionProjection = null; _recognitionContext = null; _recognitionFacts = null;
        _candidateByKey.Clear(); ClearFrameCache();
        ClearPreview(); BeginRead(resetSelection: true);
        if (SourceKind == ImportPageSourceKind.StartMenu) Track(ObserveCatalogAsync());
    }

    private void BeginRead(bool resetSelection = false)
    {
        if (_closed || _dismissRequested || _adding) return;
        _readsSuspended = false; _pageRequest?.Cancel(); _preparationRequest?.Cancel();
        var resetPreview = resetSelection;
        if (resetPreview)
        {
            CancelPreview();
            foreach (var row in _rows)
                if (row.Member.Images.Kind == MemberImageSourceKind.Automatic) row.ImageKey = null;
        }
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _pageRequest = request;
        var epoch = ++_sourceEpoch; var revision = _catalogRevision;
        if (resetSelection) _selected.Clear();
        _reading = true;
        if (resetSelection) _prepared = false;
        UpdateAvailability(); UpdateStatus();
        Track(ReadPageAsync(epoch, revision, resetPreview, request));
    }

    private async Task ReadPageAsync(int epoch, int revision, bool resetPreview, CancellationTokenSource request)
    {
        try
        {
            if (resetPreview) { request.Token.ThrowIfCancellationRequested(); ClosePreviewSource(); }
            request.Token.ThrowIfCancellationRequested();
            var kind = SourceKind;
            if (kind == ImportPageSourceKind.WallPackage && _packagePath is null) return;
            var context = _context is { } cached && _contextCurrent(cached) ? cached : await _readContext(request.Token);
            var source = await _readSource(kind, _packagePath, request.Token);
            if (!IsCurrent(epoch, request.Token)) return;
            var previousSource = _sourceData;
            var keepPreparation = _prepared && previousSource is not null &&
                ReferenceEquals(previousSource.Candidates, source.Candidates) &&
                _context is { } preparedContext && ReferenceEquals(preparedContext, context) &&
                preparedContext.Document.Generation == context.Document.Generation &&
                preparedContext.Budget == context.Budget &&
                previousSource.AvailabilityFacts.Count == source.AvailabilityFacts.Count &&
                previousSource.AvailabilityFacts.All(pair => source.AvailabilityFacts.GetValueOrDefault(pair.Key) == pair.Value);
            var nativeRefreshCompleted = kind is ImportPageSourceKind.Desktop or ImportPageSourceKind.InstalledApplications or ImportPageSourceKind.SystemFeatures &&
                previousSource?.Refreshing == true && !source.Refreshing;
            if (previousSource?.Package is { } oldPackage && oldPackage.PackageSha256 != source.Package?.PackageSha256)
            { CancelPreview(); ClosePreviewSource(); }
            if (_context is { } previousContext && (context.Document.Generation != previousContext.Document.Generation || context.Budget != previousContext.Budget) ||
                _sourceData?.Package is { } previousPackage && previousPackage.PackageSha256 != source.Package?.PackageSha256)
            { _selected.Clear(); _notice = ProductStrings.Get("Import.SourceChanged"); }
            if (_sourceData is { } previous)
                foreach (var key in _selected.Keys.ToArray())
                    if (previous.StartMenuSources.TryGetValue(key, out var old) &&
                        (!source.StartMenuSources.TryGetValue(key, out var current) || old.SourceSha256 != current.SourceSha256)) _selected.Remove(key);
            _context = context; _sourceData = source; _error = null;
            if (keepPreparation) _sourceData = source with { AvailabilityPrepared = previousSource!.AvailabilityPrepared };
            _previewRequest ??= CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            Reproject(prepare: !keepPreparation);
            if (nativeRefreshCompleted)
                foreach (var row in _rows.Where(row => row.Member.Images.Kind == MemberImageSourceKind.Automatic))
                { row.ImageKey = null; row.Request?.Cancel(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (IsCurrent(epoch, request.Token)) { _error = ex.Message; _plan = null; } }
        finally
        {
            if (_sourceEpoch == epoch)
            {
                _reading = false; if (ReferenceEquals(_pageRequest, request)) _pageRequest = null;
                UpdateAvailability(); UpdateStatus(); foreach (var row in _rows) StartRowImage(row);
                if (!_closed && !_dismissRequested && !_readsSuspended && revision != _catalogRevision) BeginRead();
            }
            request.Dispose();
        }
    }

    private bool IsCurrent(int epoch, CancellationToken token) => !_closed && !_dismissRequested && !_readsSuspended &&
        !token.IsCancellationRequested && epoch == _sourceEpoch;

    private async Task PickPathAsync()
    {
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _pickerRequest = request;
        var epoch = _sourceEpoch; var mode = Mode;
        try
        {
            if (_closed || _dismissRequested || _readsSuspended) return;
            var owner = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(_owner));
            Windows.Foundation.IAsyncOperation<Microsoft.Windows.Storage.Pickers.PickFileResult> operation;
            if (mode == ImportPageMode.Export)
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(owner)
                {
                    SuggestedFileName = _exportPathPicked && _exportPath is not null ? Path.GetFileName(_exportPath) : DefaultExportFileName(),
                    DefaultFileExtension = ".zip", ShowOverwritePrompt = true,
                };
                picker.FileTypeChoices.Add(ProductStrings.Get("Import.WallFileType"), new List<string> { ".zip" });
                var directory = _exportPath is null ? _initialExportDirectory : Path.GetDirectoryName(_exportPath);
                if (!string.IsNullOrWhiteSpace(directory)) { picker.SuggestedFolder = directory; picker.SuggestedStartFolder = directory; }
                operation = picker.PickSaveFileAsync();
            }
            else
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(owner);
                picker.FileTypeFilter.Add(".zip"); picker.FileTypeFilter.Add(".twpack");
                TargetItemPicker.SetSuggestedFolder(picker, _packagePath);
                operation = picker.PickSingleFileAsync();
            }
            _pickerOperation = operation;
            var file = await operation.AsTask(request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(epoch, request.Token) || mode != Mode || file is null) return;
            if (mode == ImportPageMode.Export) { _exportPath = file.Path; _exportPathPicked = true; }
            else
            {
                _packagePath = file.Path; ResetSource();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (IsCurrent(epoch, request.Token)) _error = ex.Message; }
        finally
        {
            _pickerOperation = null; if (ReferenceEquals(_pickerRequest, request)) _pickerRequest = null;
            request.Dispose(); _picking = false; UpdateAvailability(); UpdateStatus();
            if (_dismissRequested) FinishDismiss();
        }
    }

    private void CancelPicker()
    {
        _pickerRequest?.Cancel();
        if (_pickerOperation is { Status: Windows.Foundation.AsyncStatus.Started } operation) operation.Cancel();
    }

    private void SetSize(int size)
    {
        if (_tileSize == size || _adding) return;
        _tileSize = size; _small.IsChecked = size == 1; _medium.IsChecked = size == 2;
        _error = null; _plan = null; RefreshPreview(); SelectionChanged();
    }

    private void Reproject(bool prepare = true)
    {
        if (_adding || _context is null || _sourceData is null) return;
        var filter = !Attributed && _autoFilter.IsOn; var recognize = !Attributed && _recognizeGroups.IsOn;
        if (_projection is null || _projection.Generation != _context.Document.Generation || _projection.AutoFilter != filter ||
            _projection.RecognizeGroups != recognize || !ReferenceEquals(_projectedCandidates, _sourceData.Candidates) ||
            !ReferenceEquals(_projectedIdentities, _context.ExistingIdentities))
        {
            _projection = ImportSourcePlanner.Project(_context.Document, _sourceData.Candidates, recognize, filter, _context.ExistingIdentities);
            _projectedCandidates = _sourceData.Candidates; _projectedIdentities = _context.ExistingIdentities;
            var migration = ImportSourcePlanner.MigrateSelection(_selected, _projection);
            _selected.Clear(); foreach (var pair in migration.Selected) _selected.Add(pair.Key, pair.Value);
            RemoveFilteredSelections();
            if (migration.RemovedCount > 0) _notice = string.Format(ProductStrings.Get("Import.SelectionRemoved"), migration.RemovedCount);
        }
        RefreshPreview(); SelectionChanged();
        if (Mode == ImportPageMode.Export) { _prepared = _contextCurrent(_context); UpdateAvailability(); }
        else if (prepare) BeginPreparation();
    }

    private void BeginPreparation()
    {
        if (_closed || _dismissRequested || _readsSuspended || _adding || _context is null || _sourceData is null || _projection is null) return;
        _preparationRequest?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _preparationRequest = request;
        var epoch = ++_preparationEpoch; var sourceEpoch = _sourceEpoch;
        _preparing = true; _prepared = false; UpdateAvailability(); UpdateStatus();
        Track(PrepareAsync(_context, _sourceData, _projection, sourceEpoch, epoch, request));
    }

    private async Task PrepareAsync(ImportPageContext context, ImportPageSource source, ImportSourceProjection projection,
        int sourceEpoch, int epoch, CancellationTokenSource request)
    {
        try
        {
            await Task.Yield(); request.Token.ThrowIfCancellationRequested();
            var result = await _prepare(context, source, projection, request.Token);
            if (!IsCurrent(sourceEpoch, request.Token) || epoch != _preparationEpoch || !ReferenceEquals(source, _sourceData)) return;
            if (!_contextCurrent(result.Context)) { _notice = ProductStrings.Get("Import.SourceChanged"); return; }
            _context = result.Context; _sourceData = result.Source; Reproject(prepare: false);
            _prepared = result.Source.AvailabilityPrepared && result.Context.Covers(_projection!);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (IsCurrent(sourceEpoch, request.Token) && epoch == _preparationEpoch) _error = ex.Message; }
        finally
        {
            if (epoch == _preparationEpoch)
            {
                _preparing = false; if (ReferenceEquals(_preparationRequest, request)) _preparationRequest = null;
                UpdateAvailability(); UpdateStatus();
            }
            request.Dispose();
        }
    }

    private string FilterQuery => _filter.Text.Trim();
    private bool MatchesFilter(ImportProjectedCandidate candidate)
    {
        var query = FilterQuery;
        return candidate.Visible && (query.Length == 0 || candidate.Source.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
    private ImportProjectedCandidate[] FilteredAddableCandidates() => _projection?.Candidates
        .Where(candidate => candidate.CanAdd && MatchesFilter(candidate)).ToArray() ?? [];
    private string[] FilteredSelectedSourceKeys()
    {
        var keys = FilteredAddableCandidates().SelectMany(candidate => candidate.SourceKeys).ToHashSet(StringComparer.Ordinal);
        return _selected.Keys.Where(keys.Contains).ToArray();
    }
    private void RemoveFilteredSelections()
    {
        var keys = FilteredAddableCandidates().SelectMany(candidate => candidate.SourceKeys).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _selected.Keys.Where(key => !keys.Contains(key)).ToArray()) _selected.Remove(key);
    }
    private bool IsSelected(ImportProjectedCandidate candidate) => candidate.CanAdd && MatchesFilter(candidate) && candidate.SourceKeys
        .Any(key => _selected.GetValueOrDefault(key) == candidate.Source.SelectionIdentity);
    private void Select(ImportProjectedCandidate candidate, bool selected)
    {
        if (selected && IsSelected(candidate)) return;
        if (selected && candidate.CanAdd && MatchesFilter(candidate)) _selected[candidate.SelectionKey] = candidate.Source.SelectionIdentity;
        else foreach (var key in candidate.SourceKeys) _selected.Remove(key);
    }
    private void SelectionChanged() { _plan = null; UpdateSelectionFeedback(); UpdateAvailability(); UpdateStatus(); }

    private void RefreshRecognitionSelection()
    {
        // A same-source progress read does not invalidate a completed inference.
        // Its new facts are compared when that read/preparation has settled.
        if (_closed || _dismissRequested || _readsSuspended || _reading || _adding || _preparing) return;
        var ready = !Attributed && !_reading && !_adding && _prepared && !_preparing &&
            _sourceData?.Refreshing == false && _context is { } currentContext && _projection is { } currentProjection && currentContext.Covers(currentProjection);
        if (ReferenceEquals(_recognitionProjection, _projection) && ReferenceEquals(_recognitionContext, _context) &&
            ReferenceEquals(_recognitionFacts, _sourceData?.AvailabilityFacts) && _recognitionReady == ready) return;
        var becameReady = !_recognitionReady && ready;
        _recognitionProjection = _projection; _recognitionContext = _context; _recognitionFacts = _sourceData?.AvailabilityFacts;
        _recognitionReady = ready;
        var changed = false;
        if (_sourceData is { Refreshing: false } source)
        {
            foreach (var key in _recognizedTargets.Keys.ToArray())
            {
                var candidate = source.Candidates.FirstOrDefault(candidate => candidate.SourceKey == key);
                if (candidate is null || !_recognizedTargets[key].Matches(source, candidate))
                { _recognizedTargets.Remove(key); changed = true; }
            }
        }
        // Preview inference applies to visible realized candidates, independent of
        // selection and current placement capacity. Rebind earlier shell-only frames
        // when the page becomes ready, and discard presentations whose facts changed.
        if (changed || becameReady) RefreshRecognizedRows();
    }

    private void RefreshRecognizedRows(string? sourceKey = null)
    {
        foreach (var row in _rows.ToArray().Where(row => sourceKey is null || row.Candidate.Source.SourceKey == sourceKey))
        { row.Request?.Cancel(); row.ImageKey = null; }
        foreach (var element in _preparedElements.Keys.ToArray())
            if (sourceKey is null || _preparedElements[element].Item.Candidate?.Source.SourceKey == sourceKey)
                PrepareElement(element, _repeater.GetElementIndex(element));
        foreach (var row in _rows.ToArray().Where(row => sourceKey is null || row.Candidate.Source.SourceKey == sourceKey))
            StartRowImage(row);
    }

    private void RefreshPreview(bool force = false)
    {
        if (_closed || _projection is null || _adding) return;
        var height = _preview.ViewportHeight; var query = FilterQuery;
        if (!force && ReferenceEquals(_previewProjection, _projection) && _previewTileSize == _tileSize && _previewHeight == height && _previewFilter == query)
        {
            RefreshRealizedMetadata(); return;
        }
        _previewProjection = _projection; _previewTileSize = _tileSize; _previewHeight = height; _previewFilter = query;
        var previewProjection = CreateFilteredPreviewProjection(_projection);
        var preview = ImportSourcePlanner.BuildSourcePreview(previewProjection, new GridSize(_tileSize, _tileSize), _metrics,
            singleColumn: true);
        if (preview.ErrorCode is { } error) _error = ErrorText(error);
        var groups = preview.Lanes.SelectMany(lane => lane.Groups).ToDictionary(group => group.Id, StringComparer.Ordinal);
        var members = groups.Values.SelectMany(group => group.Members).ToDictionary(member => member.Id, StringComparer.Ordinal);
        var items = new List<PreviewItem>(); _candidateByKey.Clear();
        foreach (var candidate in previewProjection.Candidates) _candidateByKey[candidate.SelectionKey] = candidate;
        foreach (var group in preview.Geometry.Groups)
        {
            var model = groups[group.GroupId];
            items.Add(new("group:" + group.GroupId, WallGroupVisualFactory.TitleControlBounds(group.TitleBounds, _metrics.TitleInteractionHeight), model, null, null));
            foreach (var placed in group.Members)
                if (preview.CandidatesByMemberId.TryGetValue(placed.MemberId, out var candidate))
                    items.Add(new("member:" + placed.MemberId, placed.Bounds, null, members[placed.MemberId], candidate));
        }
        if (force) ClearPreview();
        _previewFilter = query;
        _previewItems = items;
        // Catalog progress and availability preparation can change a single row.
        // Keep the source and surviving containers stable through those updates.
        _updatingPreviewItems = true;
        try
        {
            var keys = items.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            for (var index = _previewKeys.Count - 1; index >= 0; index--)
                if (!keys.Contains(_previewKeys[index])) _previewKeys.RemoveAt(index);
            var existingKeys = _previewKeys.ToHashSet(StringComparer.Ordinal);
            for (var index = 0; index < items.Count; index++)
            {
                var key = items[index].Key;
                if (index < _previewKeys.Count && _previewKeys[index] == key) continue;
                var previous = existingKeys.Contains(key) ? _previewKeys.IndexOf(key) : -1;
                if (previous >= 0) _previewKeys.RemoveAt(previous);
                _previewKeys.Insert(index, key);
            }
        }
        finally { _updatingPreviewItems = false; }
        _previewLayout.SetScene(items.Select(item => item.Bounds).ToArray(), preview.Geometry.WallWidth, preview.Geometry.WallHeight,
            preserveElements: true);
        foreach (var element in _preparedElements.Keys.ToArray()) PrepareElement(element, _repeater.GetElementIndex(element));
        RefreshRealizedMetadata();
    }

    private ImportSourceProjection CreateFilteredPreviewProjection(ImportSourceProjection projection)
    {
        var compactRecordedPositions = FilterQuery.Length != 0;
        var groups = projection.Groups.Select(group => group with
        {
            Candidates = group.Candidates.Where(MatchesFilter).Select(candidate =>
            {
                if (!compactRecordedPositions || !candidate.Source.HasRecordedPosition) return candidate;
                // Search results form a temporary, compact preview. Preserve recorded size and
                // every source fact; only the preview copy ignores recorded position.
                return candidate with { Source = candidate.Source with { HasRecordedPosition = false } };
            }).ToArray(),
        }).Where(group => group.Candidates.Count != 0).ToArray();
        return projection with { Groups = groups, Candidates = groups.SelectMany(group => group.Candidates).ToArray() };
    }

    private void RefreshRealizedMetadata()
    {
        foreach (var row in _rows)
        {
            row.Candidate = _candidateByKey.GetValueOrDefault(row.Candidate.SelectionKey) ?? row.Candidate;
            UpdateRowTooltip(row);
            var stamp = IconStamp(row.Candidate);
            if (row.IconStamp != stamp)
            {
                row.IconStamp = stamp; row.Request?.Cancel(); row.ImageKey = null;
                StartRowImage(row, replace: true);
            }
        }
    }

    private void OnPreviewRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_closed || _dismissRequested) return;
        if (_previewScale != sender.RasterizationScale)
        { _previewScale = sender.RasterizationScale; RefreshPreview(force: true); }
        UpdateStatisticLayout();
    }

    private static bool SamePresentation(PreviewItem left, PreviewItem right)
    {
        if (left.Key != right.Key || left.Bounds.Width != right.Bounds.Width || left.Bounds.Height != right.Bounds.Height ||
            left.Group?.Title != right.Group?.Title) return false;
        if (left.Member is not { } member) return right.Member is null;
        if (right.Member is not { } other || member.GetType() != other.GetType() || member.TargetInput != other.TargetInput || member.Visual != other.Visual ||
            member.Images != other.Images || member.ImageDisplay != other.ImageDisplay ||
            member.Carousel?.CurrentImageId != other.Carousel?.CurrentImageId) return false;
        return (member, other) switch
        {
            (PuzzleMember puzzle, PuzzleMember next) => puzzle.Partitions.SequenceEqual(next.Partitions),
            (ClockMember clock, ClockMember next) => clock.TimeFormat == next.TimeFormat && clock.DateFormat == next.DateFormat,
            _ => true,
        };
    }

    private void EnsureElementPrepared(UIElement element, int index)
    {
        if (!_preparedElements.TryGetValue(element, out var binding) || binding.Epoch != _renderEpoch || binding.Index != index)
            PrepareElement(element, index);
    }

    private void PrepareElement(UIElement element, int index)
    {
        if (_updatingPreviewItems || element is not Grid host) return;
        if (index < 0 || index >= _previewItems.Count) return;
        var item = _previewItems[index];
        if (item.Candidate is { } sourceCandidate && item.Member is { } sourceMember && _sourceData is { } source &&
            _recognizedTargets.TryGetValue(sourceCandidate.Source.SourceKey, out var recognition) && recognition.Matches(source, sourceCandidate.Source))
            item = item with { Member = recognition.Result.ApplyTo(sourceMember) };
        if (_preparedElements.TryGetValue(host, out var binding) && binding.Epoch == _renderEpoch && SamePresentation(binding.Item, item))
        {
            _preparedElements[host] = (_renderEpoch, index, item);
            if (host.Tag is CandidateRow existing && item.Candidate is { } currentCandidate && item.Member is { } currentMember)
            { existing.Candidate = currentCandidate; existing.Member = currentMember; }
            return;
        }
        ClearElement(host);
        _preparedElements[host] = (_renderEpoch, index, item);
        if (item.Group is { } group)
        {
            var feedback = WallGroupVisualFactory.TitleFeedback(_style, host.ActualTheme == ElementTheme.Dark);
            feedback.Visibility = Visibility.Collapsed; host.Children.Add(feedback);
            var content = WallGroupVisualFactory.TitleContent(group.Title, item.Bounds.Width); content.Margin = new Thickness(5, 0, 0, 0);
            host.Children.Add(content);
            host.PointerEntered += OnEnter; host.PointerExited += OnExit;
            host.Tag = (Action)(() => { host.PointerEntered -= OnEnter; host.PointerExited -= OnExit; });
            void OnEnter(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => feedback.Visibility = Visibility.Visible;
            void OnExit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => feedback.Visibility = Visibility.Collapsed;
            return;
        }
        if (item.Member is not { } member || item.Candidate is not { } oldCandidate) return;
        var candidate = _candidateByKey.GetValueOrDefault(oldCandidate.SelectionKey) ?? oldCandidate;
        var card = new Grid { Width = item.Bounds.Width, Height = item.Bounds.Height };
        PuzzleCanvasView? view = null; ClockMemberView? clock = null;
        if (member is ClockMember clockMember) { clock = new ClockMemberView(clockMember, _metrics, _dialog.XamlRoot?.RasterizationScale ?? 1, _style); card.Children.Add(clock); }
        else { view = WallMemberVisualFactory.Create(member, null, _metrics, _dialog.XamlRoot?.RasterizationScale ?? 1, _style,
            interactive: false, externalInteraction: true, targetIsFolder: candidate.Source.TargetKind == ImportTargetKind.Folder);
            view.IsHitTestVisible = false; card.Children.Add(view); }
        var selection = WallMemberVisualFactory.AddSelection(card, _style);
        var toggle = new ToggleButton { Content = card, Width = item.Bounds.Width, Height = item.Bounds.Height,
            Padding = new Thickness(0), BorderThickness = new Thickness(0), MinWidth = 0, MinHeight = 0,
            CornerRadius = WallCornerStyle.Corners(_style), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch };
        var reset = WallMemberVisualFactory.ConfigureInteraction(toggle, card, toggle.CornerRadius,
            hover => { view?.SetExternalHover(hover); clock?.SetExternalHover(hover); }, deferHoverExit: true);
        CandidateRow? row = null;
        RoutedEventHandler changed = (_, _) =>
        {
            if (_synchronizingSelection || _adding || _reading || row is null || !row.Candidate.CanAdd) return;
            Select(row.Candidate, toggle.IsChecked == true); _error = _notice = null; SelectionChanged();
        };
        row = new(candidate, member, view, clock, toggle, changed, reset, selection.Outline, selection.Check) { IconStamp = IconStamp(candidate) };
        toggle.Checked += changed; toggle.Unchecked += changed;
        Id(toggle, "candidate-" + candidate.SelectionKey); _rows.Add(row); host.Tag = row; host.Children.Add(toggle);
        UpdateRowTooltip(row); UpdateSelectionFeedback(); UpdateAvailability(); StartRowImage(row);
    }

    private void ClearElement(UIElement element)
    {
        _preparedElements.Remove(element);
        if (element is not Grid host) return;
        if (host.Tag is CandidateRow row) ReleaseRow(row); else if (host.Tag is Action reset) reset();
        host.Tag = null; host.Children.Clear();
    }
    private void ClearPreview()
    {
        // Clear complete host subtrees, including group handlers, before a pool
        // can return them for another source without raising ElementPrepared.
        foreach (var element in _preparedElements.Keys.ToArray()) ClearElement(element);
        _previewLayout.SetScene([], 1, 1);
        _previewKeys.Clear(); _previewItems = []; ++_renderEpoch;
        _previewFilter = "";
    }
    private void UpdateRowTooltip(CandidateRow row)
    {
        var candidate = row.Candidate;
        var displayName = candidate.Source.DisplayName == candidate.Source.Member.Id
            ? ProductStrings.Get(candidate.Source.Member switch
            {
                ClockMember => "Wall.Clock", PuzzleMember => "Wall.Puzzle", _ => "Wall.Tile",
            }) : candidate.Source.DisplayName;
        var tooltip = displayName;
        if (_sourceData?.StartMenuSources.TryGetValue(candidate.SelectionKey, out var source) == true)
        {
            tooltip += "\n" + source.Candidate.FullPath;
            if (source.Stale) tooltip += "\n" + ProductStrings.Get("Import.Stale");
        }
        if (candidate.ErrorCode is { } error) tooltip += "\n" + ErrorText(error);
        if (candidate.FilterReason != ImportFilterReason.None) tooltip += "\n" + FilterText(candidate.FilterReason);
        ActionIcon.SetTip(row.Toggle, tooltip); AutomationProperties.SetName(row.Toggle, displayName);
        AutomationProperties.SetHelpText(row.Toggle, tooltip);
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
    private string IconStamp(ImportProjectedCandidate candidate) =>
        _sourceData?.StartMenuSources.TryGetValue(candidate.SelectionKey, out var source) == true
            ? source.SourceSha256 + "|" + source.IconKey
            : (_sourceData?.Package?.PackageSha256 ?? _sourceData?.AvailabilityFacts.GetValueOrDefault(candidate.SelectionKey) ?? "") + candidate.Source.SelectionIdentity;
    private void ReleaseRow(CandidateRow row)
    {
        if (!_rows.Remove(row)) return;
        row.Request?.Cancel(); row.Toggle.Checked -= row.Changed; row.Toggle.Unchecked -= row.Changed;
        row.Reset(); row.View?.StopTransientFeedback(); row.View?.UpdateFrame(null); row.Clock?.StopTransientFeedback();
    }
    private void CancelPreview() { _previewRequest?.Cancel(); foreach (var row in _rows) row.Request?.Cancel(); }
    private void ClosePreviewSource()
    {
        _packageSource?.Retire(); _packageSource = null;
        _previewRequest?.Dispose(); _previewRequest = null;
    }


    private static long FrameBytes(PuzzleFrame frame) => 4L *
        ((long)frame.SharpDecodedPixels.Width * (long)frame.SharpDecodedPixels.Height +
         (long)frame.BlurDecodedPixels.Width * (long)frame.BlurDecodedPixels.Height);

    private bool TryGetFrame(string key, out PuzzleFrame frame)
    {
        if (!_frames.TryGetValue(key, out frame!)) return false;
        var node = _frameNodes[key];
        _frameOrder.Remove(node); _frameOrder.AddLast(node);
        return true;
    }

    private void RememberFrame(string key, PuzzleFrame frame)
    {
        var bytes = FrameBytes(frame);
        if (bytes <= 0 || bytes > PreviewFrameLimitBytes) return;
        ForgetFrame(key);
        _frames[key] = frame;
        _frameNodes[key] = _frameOrder.AddLast(key);
        _frameBytes += bytes;
        while (_frameBytes > PreviewFrameLimitBytes && _frameOrder.First is { } oldest)
            ForgetFrame(oldest.Value);
    }

    private void ForgetFrame(string key)
    {
        if (_frames.Remove(key, out var frame)) _frameBytes -= FrameBytes(frame);
        if (_frameNodes.Remove(key, out var node)) _frameOrder.Remove(node);
    }

    private void ClearFrameCache()
    {
        _frames.Clear(); _frameNodes.Clear(); _frameOrder.Clear(); _frameBytes = 0;
    }

    private void StartRowImage(CandidateRow row, bool replace = false)
    {
        if (_closed || _dismissRequested || _readsSuspended || _adding || _reading || _sourceData is null || row.Member is ClockMember) return;
        if (row.Request is { IsCancellationRequested: false } && !replace) return;
        if (replace) row.Request?.Cancel();
        _previewRequest ??= CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (_previewRequest.IsCancellationRequested) return;
        var source = _sourceData; var epoch = _renderEpoch;
        var request = CancellationTokenSource.CreateLinkedTokenSource(_previewRequest.Token);
        row.Request = request;
        var scale = _dialog.XamlRoot?.RasterizationScale ?? 1;
        var width = Math.Clamp((int)Math.Ceiling(_metrics.WidthOf(row.Member.LocalBounds.Width) * scale), 1, 4096);
        var height = Math.Clamp((int)Math.Ceiling(_metrics.HeightOf(row.Member.LocalBounds.Height) * scale), 1, 4096);
        var cacheKey = System.Text.Json.JsonSerializer.Serialize(new { row.Candidate.SelectionKey, row.Candidate.Source.SourceKind, Stamp = IconStamp(row.Candidate),
            row.Member.Images, row.Member.ImageDisplay, row.Member.Visual.BackgroundMode,
            CurrentImage = row.Member.Carousel?.CurrentImageId, Width = width, Height = height });
        var automaticCachePath = source.Package is null && row.Member.Images.Kind == MemberImageSourceKind.Automatic
            ? AutomaticFrameValidationPath(row.Candidate.Source) : null;
        PuzzleFrame? cachedAutomaticFrame = null;
        if (!replace && automaticCachePath is not null && TryGetFrame(cacheKey, out var cachedAutomatic) &&
            cachedAutomatic.MemoryCacheAllowed && cachedAutomatic.AutomaticStamp is not null)
            cachedAutomaticFrame = cachedAutomatic;
        var hasAutomaticCache = cachedAutomaticFrame is not null;
        if (!replace && row.ImageKey == cacheKey && !hasAutomaticCache)
        { row.Request = null; request.Dispose(); return; }
        row.RequestKey = cacheKey;
        if (hasAutomaticCache)
        {
            // Keep a previously loaded frame visible while its lightweight source
            // stamp is checked and, if stale, a replacement is prepared.
            row.View!.UpdateFrame(cachedAutomaticFrame!);
        }
        if (row.Member.Images.Kind != MemberImageSourceKind.Automatic && TryGetFrame(cacheKey, out var cached))
        { row.View!.UpdateFrame(cached); row.ImageKey = cacheKey; row.RequestKey = null; row.Request = null; request.Dispose(); return; }
        PackagePreviewSource? packageSource = null;
        if (source.Package is { } package)
        {
            _packageSource ??= new PackagePreviewSource(_reader, package, _previewRequest.Token);
            packageSource = _packageSource; packageSource.Enter();
        }
        var task = LoadRowImageAsync(row, source, epoch, width, height, cacheKey, request, packageSource,
            cachedAutomaticFrame, automaticCachePath);
        _previewOperations.RemoveWhere(operation => operation.IsCompleted);
        _previewOperations.Add(task); _previewLoad = Task.WhenAll(_previewOperations); Track(task);
    }

    private async Task LoadRowImageAsync(CandidateRow row, ImportPageSource source, int epoch, int width, int height,
        string cacheKey, CancellationTokenSource request, PackagePreviewSource? packageSource,
        PuzzleFrame? cachedAutomaticFrame, string? automaticCachePath)
    {
        try
        {
            var candidateKey = row.Candidate.Source.SourceKey;
            if (_sourceData is { Refreshing: false } recognitionSource &&
                recognitionSource.Candidates.FirstOrDefault(item => item.SourceKey == candidateKey) is { } recognitionCandidate &&
                CanInferVisibleTarget(row, recognitionSource, recognitionCandidate) &&
                (!_recognizedTargets.TryGetValue(recognitionCandidate.SourceKey, out var recognized) || !recognized.Matches(recognitionSource, recognitionCandidate)))
            {
                // Probe only visible, realized filesystem targets. Selection and
                // placement capacity affect Save, not what the preview can recognize.
                var fact = ImportTargetRecognition.FactFor(recognitionSource, recognitionCandidate);
                TargetImageSourceResult? inferred = null;
                try { inferred = await _images.ResolveTargetSourceAsync(recognitionCandidate.Member.TargetInput, request.Token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                    InvalidOperationException or OverflowException or System.Runtime.InteropServices.COMException or NotSupportedException)
                {
                    // A preview-only read failure should preserve the ordinary Shell
                    // icon path below. Do not record a successful inference; Save
                    // performs its own authoritative read and validation.
                }
                if (request.IsCancellationRequested) return;
                if (inferred is not null)
                {
                    if (_closed || _dismissRequested || _readsSuspended || _reading || epoch != _renderEpoch || !_rows.Contains(row) ||
                        !ReferenceEquals(row.Request, request) || !_recognitionReady || _preparing || _adding ||
                        _sourceData is not { Refreshing: false } currentSource ||
                        currentSource.Candidates.FirstOrDefault(item => item.SourceKey == candidateKey) is not { } currentCandidate ||
                        fact != ImportTargetRecognition.FactFor(currentSource, currentCandidate) ||
                        !CanInferVisibleTarget(row, currentSource, currentCandidate)) return;
                    _recognizedTargets[currentCandidate.SourceKey] = new(fact, inferred);
                    // Rebuild only changed realized presentations; source geometry stays intact.
                    row.Request = null; row.ImageKey = null;
                    RefreshRecognizedRows(currentCandidate.SourceKey);
                    return;
                }
            }
            if (cachedAutomaticFrame is { } cachedFrame && automaticCachePath is not null)
            {
                var current = await _images.IsAutomaticFrameCurrentAsync(cachedFrame, automaticCachePath, request.Token);
                if (_closed || _dismissRequested || request.IsCancellationRequested || epoch != _renderEpoch || !_rows.Contains(row) ||
                    !ReferenceEquals(row.Request, request) || row.RequestKey != cacheKey) return;
                if (current)
                {
                    row.ImageKey = cacheKey;
                    return;
                }
                if (_frames.TryGetValue(cacheKey, out var stillCached) && ReferenceEquals(stillCached, cachedFrame))
                    ForgetFrame(cacheKey);
            }
            var candidate = row.Candidate.Source;
            PuzzleFrameResult? result = null;
            var reserve = row.Member.ImageDisplay.Mode == TileWall.Core.Imaging.ImageDisplayMode.Custom
                ? Math.Max(1, row.Member.ImageDisplay.CustomScale) : 1;
            var contentWidth = (int)Math.Clamp(Math.Ceiling(width * reserve), 1, 4096);
            var contentHeight = (int)Math.Clamp(Math.Ceiling(height * reserve), 1, 4096);
            if (packageSource is not null && (row.Member.Images.Media is not null || row.Member.Images.ManagedGallery is not null))
            {
                // Bound retained original bytes as well as decoded frames. ZIP shares a
                // stream; each ready original then decodes independently in the image service.
                await _packageMediaSlots.WaitAsync(request.Token);
                try
                {
                    var batch = await packageSource.OpenAsync();
                    await packageSource.ReadGate.WaitAsync(request.Token);
                    TileWall.Core.Storage.PreparedMediaAsset asset;
                    try
                    {
                        asset = row.Member.Images.ManagedGallery is { } gallery
                            ? await batch.ReadGalleryImageAsync(gallery.ContentId, request.Token)
                            : (await batch.PrepareMediaAsync([row.Member.Images.Media!], token: request.Token)).Single();
                    }
                    finally { packageSource.ReadGate.Release(); }
                    result = await _images.LoadPackageFrameAsync(asset, contentWidth, contentHeight,
                        row.Member.Visual.BackgroundMode == MemberBackgroundMode.Blur, request.Token);
                }
                finally { _packageMediaSlots.Release(); }
            }
            // Unconfirmed packages never delegate target/icon/source paths to Shell.
            // Keep the shared first-party placeholder when no bundled media exists.
            else if (packageSource is null && row.Member.Images.Kind == MemberImageSourceKind.Automatic && _loadIcon is not null)
            {
                result = await _loadIcon(row.Candidate.Source, contentWidth, contentHeight, request.Token);
            }
            var cacheAllowed = result?.CacheAllowed != false;
            if (result?.Frame is null && source.Package is null)
            {
                var path = source.StartMenuSources.TryGetValue(row.Candidate.SelectionKey, out var startMenu)
                    ? startMenu.Candidate.FullPath : row.Member.SourceShortcutPath ?? row.Candidate.Source.Identity?.Target ?? row.Member.TargetInput;
                result = await _images.LoadMemberFrameAsync(row.Member, path, width, height,
                    ct: request.Token, purpose: MemberImagePurpose.Content);
            }
            if (_closed || _dismissRequested || request.IsCancellationRequested || epoch != _renderEpoch || !_rows.Contains(row) ||
                !ReferenceEquals(row.Request, request) || row.RequestKey != cacheKey) return;
            if (row.Member.Images.Kind != MemberImageSourceKind.Automatic && cacheAllowed && result is { CacheAllowed: true, Frame: { } frame })
            {
                RememberFrame(cacheKey, frame);
            }
            if (row.Member.Images.Kind == MemberImageSourceKind.Automatic && source.Package is null && automaticCachePath is not null &&
                result is { MemoryCacheAllowed: true, Frame: { } automaticFrame } && automaticFrame.MemoryCacheAllowed && automaticFrame.AutomaticStamp is not null)
            {
                RememberFrame(cacheKey, automaticFrame);
            }
            if (result?.Frame is { } success)
            {
                var canReuseAutomatic = row.Member.Images.Kind == MemberImageSourceKind.Automatic && source.Package is null &&
                    automaticCachePath is not null && result is { MemoryCacheAllowed: true } && success.MemoryCacheAllowed && success.AutomaticStamp is not null;
                row.ImageKey = row.Member.Images.Kind == MemberImageSourceKind.Automatic
                    ? canReuseAutomatic ? cacheKey : null
                    : cacheAllowed && result.CacheAllowed ? cacheKey : null;
                row.View!.UpdateFrame(success);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception) { /* A failed thumbnail keeps the shared placeholder. */ }
        finally
        {
            if (ReferenceEquals(row.Request, request))
            {
                row.Request = null;
                row.RequestKey = null;
            }
            request.Dispose();
            packageSource?.Release();
        }
    }

    private bool CanInferVisibleTarget(CandidateRow row, ImportPageSource source, ImportSourceCandidate candidate)
    {
        if (!_recognitionReady || _closed || _dismissRequested || _readsSuspended || _reading || _preparing || _adding ||
            source.Refreshing || !ReferenceEquals(_sourceData, source) || !ImportTargetRecognition.AppliesTo(candidate)) return false;
        var projected = _candidateByKey.GetValueOrDefault(row.Candidate.SelectionKey);
        if (projected is null || !MatchesFilter(projected) || projected.Source.SourceKey != candidate.SourceKey) return false;
        if (source.StartMenuSources.TryGetValue(projected.SelectionKey, out var startMenu))
        {
            var sourceExtension = Path.GetExtension(startMenu.Candidate.FullPath);
            if (sourceExtension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                sourceExtension.Equals(".url", StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (candidate.Member.SourceShortcutPath is not null) return false;
        var target = candidate.Member.TargetInput;
        // ResolveTargetSourceAsync treats .lnk as opaque and never follows it; the
        // exact path check also excludes URI targets without guessing a link target.
        return target is not null && Path.IsPathFullyQualified(target);
    }

    private static string? AutomaticFrameValidationPath(ImportSourceCandidate candidate) =>
        candidate.SourceKind is ImportPageSourceKind.CurrentWall or ImportPageSourceKind.RecycleBin or ImportPageSourceKind.WallPackage
            ? null : candidate.Member.SourceShortcutPath ?? candidate.Member.TargetInput ?? candidate.Identity?.Target;

    private void StartOperation()
    {
        if (_closed || _dismissRequested || _adding || _reading || _picking || _context is null || _projection is null ||
            _sourceData is null || _sourceData.Refreshing || !_prepared || _preparing || !_projection.Candidates.Any(IsSelected)) return;
        if (!_contextCurrent(_context)) { _notice = ProductStrings.Get("Import.SourceChanged"); BeginRead(resetSelection: true); return; }
        var epoch = _sourceEpoch; var source = _sourceData; var projection = _projection;
        Func<Task<ImportPageResult>> operation;
        if (IsRecycle)
        {
            if (_restoreRecycled is null) return;
            var ids = projection.Candidates.Where(IsSelected).Select(candidate => candidate.Source.Member.Id).ToArray();
            var generation = _context.Document.Generation;
            operation = async () => new ImportPageResult(await _restoreRecycled(ids, generation, CancellationToken.None));
        }
        else if (Mode == ImportPageMode.Export)
        {
            if (string.IsNullOrWhiteSpace(_exportPath)) return;
            if (!_exportPathPicked && File.Exists(_exportPath))
            {
                _picking = true; UpdateAvailability(); Track(ConfirmDefaultExportPathAsync()); return;
            }
            var selected = projection.Candidates.Where(IsSelected).Select(candidate => candidate.Source.Member.Id).ToArray();
            var request = new ImportPageExportRequest(_context, source, selected, _exportPath,
                () => epoch == _sourceEpoch && ReferenceEquals(_sourceData, source) && ReferenceEquals(_projection, projection));
            operation = () => _saveExport(request);
        }
        else
        {
            _plan = ImportSourcePlanner.TryPlan(_context.Document, _context.Document.Generation, projection,
                FilteredSelectedSourceKeys(), new GridSize(_tileSize, _tileSize), _position.SelectedIndex == 1 ? ImportInsertionPosition.Tail : ImportInsertionPosition.Head,
                _metrics, _context.Budget);
            if (_plan.Status != ImportPlanStatus.Ready) { UpdateStatus(); return; }
            var plan = _plan;
            var request = new ImportPageRequest(_context, source, projection, plan,
                () => epoch == _sourceEpoch && ReferenceEquals(_plan, plan) && ReferenceEquals(_sourceData, source))
            { RecognizedTargets = new Dictionary<string, ImportTargetRecognition>(_recognizedTargets, StringComparer.Ordinal) };
            operation = () => _save(request);
        }
        _adding = true; _error = null; CancelPreview(); UpdateAvailability(); UpdateStatus();
        // Registration precedes preview drain, snapshot/commit or export publication.
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _addTask = completed.Task; Track(_addTask); _ = ExecuteOperationAsync(operation, completed);
    }

    private async Task ConfirmDefaultExportPathAsync()
    {
        await PickPathAsync();
        if (_exportPathPicked) StartOperation();
    }

    private async Task ExecuteOperationAsync(Func<Task<ImportPageResult>> operation, TaskCompletionSource completed)
    {
        try
        {
            await _previewLoad; ClosePreviewSource();
            var result = await operation();
            if (result.Error is null) _dismissRequested = true;
            else
            {
                _error = result.Error;
                if (result.RefreshRequired && !_dismissRequested) { _selected.Clear(); _context = null; _notice = ProductStrings.Get("Import.SourceChanged"); }
            }
            _adding = false;
            if (!_dismissRequested && !_readsSuspended && result.RefreshRequired) BeginRead(resetSelection: true);
            UpdateAvailability(); UpdateStatus();
        }
        catch (Exception ex) { _error = ex.Message; _adding = false; UpdateAvailability(); UpdateStatus(); }
        finally { _adding = false; if (_dismissRequested) FinishDismiss(); completed.TrySetResult(); }
    }

    private void UpdateAvailability()
    {
        var idle = !_closed && !_dismissRequested && !_adding && !_picking;
        var export = Mode == ImportPageMode.Export;
        _optionsHost.IsEnabled = idle;
        for (var index = 0; index < _source.Items.Count; index++)
            if (_source.Items[index] is ComboBoxItem item) item.IsEnabled = export == (SourceKinds[index] == ImportPageSourceKind.CurrentWall);
        _source.IsEnabled = idle && !export;
        var pathEnabled = export || SourceKind == ImportPageSourceKind.WallPackage;
        _path.IsEnabled = pathEnabled; _chooseFile.IsEnabled = idle && pathEnabled;
        var path = export ? _exportPath : _packagePath;
        _path.Text = pathEnabled ? path is null ? ProductStrings.Get(export ? "Import.Path.ExportEmpty" : "Import.Path.Empty") : export ? path : Path.GetFileName(path)
            : ProductStrings.Get("Import.Path.System");
        ToolTipService.SetToolTip(_path, pathEnabled ? path : ProductStrings.Get("Import.Path.System"));
        AutomationProperties.SetHelpText(_path, pathEnabled ? path ?? "" : ProductStrings.Get("Import.Path.System"));
        _autoFilter.IsEnabled = !Attributed;
        _recognizeGroups.IsEnabled = !Attributed && SourceKind != ImportPageSourceKind.SystemFeatures;
        _small.IsEnabled = _medium.IsEnabled = !Attributed && (_sourceData?.Candidates.Any(candidate => !candidate.HasRecordedSize) ?? true);
        _position.SetEnabled(!export && !IsRecycle);
        var reason = ProductStrings.Get(export ? "Import.Option.ImportOnly" : "Import.Option.RecordedAttributes");
        AutomationProperties.SetHelpText(_autoFilter, _autoFilter.IsEnabled ? "" : reason);
        AutomationProperties.SetHelpText(_recognizeGroups, _recognizeGroups.IsEnabled ? "" :
            !Attributed && SourceKind == ImportPageSourceKind.SystemFeatures ? ProductStrings.Get("Import.Option.SystemGroups") : reason);
        AutomationProperties.SetHelpText(_small, _small.IsEnabled ? "" : reason);
        AutomationProperties.SetHelpText(_medium, _medium.IsEnabled ? "" : reason);
        AutomationProperties.SetHelpText(_position, export || IsRecycle ? reason : "");
        foreach (var button in _position.Children.OfType<ToggleButton>())
            AutomationProperties.SetHelpText(button, export || IsRecycle ? reason : "");
        var addable = FilteredAddableCandidates();
        var count = addable.Count(IsSelected);
        _selectAll.IsEnabled = idle && !_reading && addable.Length > 0;
        _selectAll.IsChecked = count == 0 ? false : count == addable.Length ? true : null;
        _dialog.IsPrimaryButtonEnabled = idle && !_reading && !_preparing && _sourceData?.Refreshing == false && _prepared && count > 0 &&
            (!export || !string.IsNullOrWhiteSpace(_exportPath)) && (!IsRecycle || _restoreRecycled is not null) && _context is not null && _contextCurrent(_context);
        foreach (var row in _rows) row.Toggle.IsEnabled = idle && !_reading && row.Candidate.CanAdd && MatchesFilter(row.Candidate);
        var primaryText = ProductStrings.Get(export ? "Import.ExportConfirm" : IsRecycle ? "Recycle.Restore" : "Import.Confirm");
        if (_dialog.PrimaryButtonText != primaryText)
        {
            _dialog.PrimaryButtonText = primaryText;
            ActionIcon.DecorateDialog(_dialog, export ? ActionIcon.Save : IsRecycle ? ActionIcon.Undo : ActionIcon.Import, ActionIcon.Delete, ActionIcon.Cancel);
        }
        if (!_adding) RefreshRecognitionSelection();
    }

    private void UpdateStatus()
    {
        var stats = _projection?.Statistics;
        var counts = new int[14];
        counts[0] = stats?.AcquiredCount ?? _sourceData?.Scanned ?? 0;
        counts[1] = stats?.FilteredCount ?? 0; counts[2] = stats?.CandidateCount ?? 0;
        counts[3] = _projection?.Candidates.Count(IsSelected) ?? 0;
        ImportTargetKind[] kinds = [ImportTargetKind.SystemFeature, ImportTargetKind.Program, ImportTargetKind.File, ImportTargetKind.Folder,
            ImportTargetKind.WebLink, ImportTargetKind.ProtocolLink, ImportTargetKind.Component];
        for (var index = 0; index < kinds.Length; index++) counts[index + 4] = stats?.ContentCounts.GetValueOrDefault(kinds[index]) ?? 0;
        ImportFilterReason[] reasons = [ImportFilterReason.DuplicateTarget, ImportFilterReason.MissingTarget, ImportFilterReason.NonPrimaryEntry];
        for (var index = 0; index < reasons.Length; index++) counts[index + 11] = stats?.PrimaryFilterCounts.GetValueOrDefault(reasons[index]) ?? 0;
        for (var index = 0; index < counts.Length; index++)
        {
            _numbers[index].Text = counts[index].ToString(System.Globalization.CultureInfo.CurrentCulture);
            AutomationProperties.SetName(_numbers[index], ProductStrings.Get(StatisticLabels[index]) + ": " + _numbers[index].Text);
        }
        UpdateStatisticLayout();
        var messages = new List<string>();
        if (_adding) messages.Add(ProductStrings.Get(IsRecycle ? "Settings.Saving" : Mode == ImportPageMode.Export ? "Import.Exporting" : "Import.Adding"));
        else if (_reading || _preparing || _sourceData?.Refreshing == true) messages.Add(ProductStrings.Get("Import.Updating"));
        else if (_sourceData?.IconPreparing == true) messages.Add(ProductStrings.Get("Import.PreparingImages"));
        if (_notice is not null) messages.Add(_notice); if (_error is not null) messages.Add(_error);
        if (_sourceData is { } source && (source.ReadErrors != 0 || source.Skipped != 0)) messages.Add(string.Format(ProductStrings.Get("Import.ReadErrors"), source.ReadErrors, source.Skipped));
        if (_sourceData?.ErrorCode is { } error) messages.Add(ErrorText(error));
        if (IsRecycle && !_reading && !_preparing && _sourceData?.Candidates.Count == 0) messages.Add(ProductStrings.Get("Recycle.Empty"));
        if (_plan?.Status == ImportPlanStatus.Rejected) messages.Add(ProductStrings.Get("Import.LayoutRejected"));
        _status.Text = string.Join("\n", messages);
        _statusScroll.Visibility = messages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public bool HasUnsavedDraft => false;
    public string? TrySaveNow() => ProductStrings.Get("Import.Confirm");
    public void DiscardDraft() { _selected.Clear(); UpdateSelectionFeedback(); }
    public Task<ExitDecision> ConfirmExitAsync() => Task.FromResult(ExitDecision.DiscardAndExit);
    public async Task WaitForPendingAsync()
    {
        _readsSuspended = true; CancelPicker(); _catalogRequest?.Cancel(); _pageRequest?.Cancel(); _preparationRequest?.Cancel(); CancelPreview();
        await _addTask; await DrainOperationsAsync();
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
        _closed = true; _clockTimer.Stop(); CancelPicker(); _lifetime.Cancel(); _pageRequest?.Cancel(); _preparationRequest?.Cancel(); CancelPreview();
        if (_previewRoot is not null) _previewRoot.Changed -= OnPreviewRootChanged;
        _catalog.Changed -= _catalogChanged; ClearPreview(); ClearFrameCache();
        _sourceData = null; _projection = _previewProjection = null; _projectedCandidates = null;
        _context = null; _plan = null; _projectedIdentities = null;
        _candidateByKey.Clear(); _selected.Clear(); _recognizedTargets.Clear();
        _recognitionProjection = null; _recognitionContext = null; _recognitionFacts = null;
    }

    public void RefreshStrings()
    {
        if (!_exportPathPicked && !string.IsNullOrWhiteSpace(_initialExportDirectory))
            _exportPath = Path.Combine(_initialExportDirectory, DefaultExportFileName());
        _refreshingStrings = true;
        try
        {
            var mode = _mode.SelectedIndex; var source = _source.SelectedIndex; var position = _position.SelectedIndex;
            _mode.SetItems([ProductStrings.Get("Import.Mode.Import"), ProductStrings.Get("Import.Mode.Export")],
                showLabels: true, notifyOnReselect: false);
            Id(_mode.Children[0], "mode-import"); Id(_mode.Children[1], "mode-export");
            _mode.SelectedIndex = mode < 0 || mode == 1 ? 1 : 0;
            _source.Items.Clear(); foreach (var key in SourceLabels) _source.Items.Add(new ComboBoxItem { Content = ProductStrings.Get(key) });
            _source.SelectedIndex = source >= 0 && source < SourceKinds.Length ? source : Mode == ImportPageMode.Export ? 5 : 0;
            _position.SetItems([ProductStrings.Get("Import.Position.Head"), ProductStrings.Get("Import.Position.Tail")],
                showLabels: true, notifyOnReselect: false);
            Id(_position.Children[0], "position-head"); Id(_position.Children[1], "position-tail");
            _position.SelectedIndex = position == 1 ? 1 : 0;
        }
        finally { _refreshingStrings = false; }
        string[] labels = ["Import.Mode", "Import.Source", "Import.Path", "Import.AutoFilter", "Import.RecognizeGroups", "Import.DefaultTileSize", "Import.Position", "Import.SelectAll", "Import.Filter"];
        for (var index = 0; index < labels.Length; index++) _optionLabels[index].Text = ProductStrings.Get(labels[index]);
        for (var index = 0; index < StatisticLabels.Length; index++) _numberLabels[index].Text = ProductStrings.Get(StatisticLabels[index]);
        _filter.PlaceholderText = ProductStrings.Get("Import.Filter.Placeholder");
        _selectAll.Content = "";
        _dialog.Title = ProductStrings.Get("Import.Title"); _dialog.CloseButtonText = ProductStrings.Get("Common.Cancel");
        ActionIcon.SetIcon(_chooseFile, ProductStrings.Get("Import.ChoosePath"), ActionIcon.OpenFolderHorizontal);
        ActionIcon.SetIcon(_small, ProductStrings.Get("Import.Small"), ActionIcon.PresetSmall);
        ActionIcon.SetIcon(_medium, ProductStrings.Get("Import.Medium"), ActionIcon.PresetMedium);
        _small.IsChecked = _tileSize == 1; _medium.IsChecked = _tileSize == 2;
        ActionIcon.DecorateDialog(_dialog, ActionIcon.Import, ActionIcon.Delete, ActionIcon.Cancel);
        if (_shown && !_adding) RefreshPreview(force: true);
        UpdateAvailability(); UpdateStatus();
    }

    private string DefaultExportFileName() => string.Format(CultureInfo.InvariantCulture,
        ProductStrings.Get("Import.ExportFileNameFormat"), _exportFileTimestamp) + ".zip";

    private static string FilterText(ImportFilterReason reason) => ProductStrings.Get("Import.FilterReason." + reason);
    private static string ErrorText(string code) => ProductStrings.Get(code switch
    {
        "InvalidTarget" => "Import.InvalidTarget", "UnsupportedProtocol" => "Import.UnsupportedProtocol",
        "SourceNotApplicable" => "Import.SourceNotApplicable",
        "SourceChanged" => "Import.SourceChanged", "SourceTooLarge" => "Import.SourceTooLarge", "UnsafeSource" => "Import.UnsafeSource",
        "INVALID_SOURCE_GEOMETRY" or "MEMBER_SIZE_LIMIT" or "SOURCE_LAYOUT_LIMIT" => "Import.LayoutRejected",
        "CacheInvalid" or "CacheWriteFailed" => "Import.CacheUnavailable", "RefreshFailed" => "Import.RefreshFailed", _ => "Import.SourceUnavailable",
    });
    private void AddOption(int index, FrameworkElement value)
    {
        _options.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var label = _optionLabels[index]; Grid.SetRow(label, index); Grid.SetRow(value, index); Grid.SetColumn(value, 1);
        AutomationProperties.SetLabeledBy(value, label); _options.Children.Add(label); _options.Children.Add(value);
    }
    private static TextBlock Label() => new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private void UpdateStatisticLayout()
    {
        const double numberFontSize = 14, cardGap = 4, cardBorderAndMargin = 6;
        var numberSample = _numbers[0];
        var numberText = numberSample.Text;
        numberSample.Text = "000";
        numberSample.FontSize = numberFontSize;
        numberSample.Width = double.NaN;
        numberSample.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var numberWidth = Math.Ceiling(numberSample.DesiredSize.Width);
        var numberHeight = Math.Ceiling(numberSample.DesiredSize.Height);
        numberSample.Text = numberText;

        var labelSample = _numberLabels[0];
        var labelText = labelSample.Text;
        // Ten half-width characters are approximately five Chinese characters.
        labelSample.Text = "中中中中中";
        labelSample.Width = double.NaN;
        labelSample.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelWidth = Math.Ceiling(labelSample.DesiredSize.Width);
        labelSample.Text = labelText;
        var labelHeight = 0d;
        foreach (var label in _numberLabels)
        {
            label.Width = labelWidth;
            label.Measure(new Windows.Foundation.Size(labelWidth, double.PositiveInfinity));
            labelHeight = Math.Max(labelHeight, Math.Ceiling(label.DesiredSize.Height));
        }

        var cardWidth = numberWidth + labelWidth + 4 + 4 + 2;
        var cardHeight = Math.Ceiling(Math.Max(numberHeight, labelHeight) + cardBorderAndMargin);
        foreach (var number in _numbers)
        {
            number.Width = double.NaN;
            number.FontSize = numberFontSize;
            number.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            if (number.Text.Length > 3 && number.DesiredSize.Width > 0)
            {
                var size = Math.Max(6, numberFontSize * Math.Min(3d / number.Text.Length, numberWidth / number.DesiredSize.Width));
                number.FontSize = size;
                number.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                while (number.DesiredSize.Width > numberWidth && number.FontSize > 6)
                {
                    number.FontSize = Math.Max(6, number.FontSize - .5);
                    number.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                }
            }
            number.Width = numberWidth;
        }
        foreach (var label in _numberLabels) label.Width = labelWidth;

        // The requested default viewport shows exactly four whole cards.
        // On a narrower work area, use only whole columns and wrap within each of the three groups.
        var desiredContentWidth = cardWidth * 4 + cardGap * 3;
        var availableColumnWidth = Math.Max(cardWidth + 24, _statisticsMaximumColumnWidth);
        var columnWidth = Math.Min(desiredContentWidth + 24, availableColumnWidth);
        var contentWidth = Math.Max(cardWidth, columnWidth - 24);
        var key = string.Join("\n", _numberLabels.Select(label => label.Text)) +
            $"\n{numberWidth}:{numberHeight}:{labelWidth}:{labelHeight}:{contentWidth}:{cardHeight}";
        if (_statisticLayoutKey == key) return;
        _statisticLayoutKey = key;
        foreach (var cell in _statisticCells) { cell.Width = cardWidth; cell.Height = cardHeight; }
        _statistics.Width = contentWidth;
        _statisticsCard.Width = columnWidth;
        var columns = Math.Clamp((int)Math.Floor((contentWidth + cardGap) / (cardWidth + cardGap)), 1, 4);
        int[] groupCounts = [4, 7, 3];
        int[] starts = [0, 4, 11];
        for (var groupIndex = 0; groupIndex < _statisticRows.Length; groupIndex++)
        {
            var rowCount = (groupCounts[groupIndex] + columns - 1) / columns;
            _statisticRows[groupIndex].RowDefinitions.Clear();
            for (var row = 0; row < rowCount; row++)
                _statisticRows[groupIndex].RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (var index = 0; index < groupCounts[groupIndex]; index++)
            {
                Grid.SetRow(_statisticCells[starts[groupIndex] + index], index / columns);
                Grid.SetColumn(_statisticCells[starts[groupIndex] + index], index % columns);
            }
        }
        _statisticsColumn.Width = new GridLength(columnWidth);
        _page.Width = _statisticsPreviewWidth + 16 + columnWidth;
    }

    private sealed class PackagePreviewSource(WallPackageReader reader, WallPackagePreview preview, CancellationToken parent)
    {
        private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(parent);
        private Task<WallPackageImport>? _import;
        private int _users;
        private bool _retired, _disposed;
        internal SemaphoreSlim ReadGate { get; } = new(1, 1);
        internal void Enter() => _users++;
        internal Task<WallPackageImport> OpenAsync()
        {
            if (_import is { IsFaulted: true } or { IsCanceled: true }) _import = null;
            return _import ??= reader.OpenImportAsync(preview, _lifetime.Token);
        }
        internal void Release() { _users--; TryDispose(); }
        internal void Retire() { _retired = true; _lifetime.Cancel(); TryDispose(); }
        private void TryDispose()
        {
            if (!_retired || _users != 0 || _disposed) return;
            _disposed = true;
            if (_import?.IsCompletedSuccessfully == true) _import.Result.Dispose();
            ReadGate.Dispose(); _lifetime.Dispose();
        }
    }
    private static void Id(DependencyObject element, string id) => AutomationProperties.SetAutomationId(element, "import-" + id);
    private sealed record PreviewItem(string Key, DipRect Bounds, StartGroup? Group, WallMember? Member, ImportProjectedCandidate? Candidate);
    private sealed class CandidateRow(ImportProjectedCandidate candidate, WallMember member, PuzzleCanvasView? view, ClockMemberView? clock,
        ToggleButton toggle, RoutedEventHandler changed, Action reset, Border outline, Border check)
    {
        public ImportProjectedCandidate Candidate { get; set; } = candidate;
        public WallMember Member { get; set; } = member;
        public PuzzleCanvasView? View { get; } = view;
        public ClockMemberView? Clock { get; } = clock;
        public ToggleButton Toggle { get; } = toggle;
        public RoutedEventHandler Changed { get; } = changed;
        public Action Reset { get; } = reset;
        public Border Outline { get; } = outline;
        public Border Check { get; } = check;
        public CancellationTokenSource? Request { get; set; }
        public string? ImageKey { get; set; }
        public string? RequestKey { get; set; }
        public string IconStamp { get; set; } = "";
    }
}
