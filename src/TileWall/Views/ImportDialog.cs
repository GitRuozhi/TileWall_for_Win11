using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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

/// <summary>Uncommitted selection over read-only source facts and one exact incremental wall proposal.</summary>
internal sealed class ImportDialog : IWallDialogSession
{
    private readonly Window _owner;
    private readonly ContentDialog _dialog;
    private readonly WallImageService _images;
    private readonly StartMenuImportCoordinator _catalog;
    private readonly WallPackageReader _reader;
    private readonly Func<CancellationToken, Task<ImportPageContext>> _readContext;
    private readonly Func<int, string?, CancellationToken, Task<ImportPageSource>> _readSource;
    private readonly Func<ImportPageRequest, Task<ImportPageResult>> _save;
    private readonly Func<ImportPageContext, bool> _contextCurrent;
    private readonly Func<WallMember, string?> _entryPath;
    private readonly Func<StartMenuImportCandidate, int, CancellationToken, Task<PuzzleFrameResult>>? _loadIcon;
    private readonly WallVisualStyle _style;
    private readonly WallMetrics _metrics = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _pendingOperations = [];
    private readonly Dictionary<string, string> _selected = new(StringComparer.Ordinal);
    private readonly List<PreviewRow> _rows = [];
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
    private readonly Canvas _scene = new();
    private readonly ScrollViewer _preview;
    private readonly Action _catalogChanged;
    private CancellationTokenSource? _pageRequest, _previewRequest, _catalogRequest, _pickerRequest;
    private Windows.Foundation.IAsyncOperation<Microsoft.Windows.Storage.Pickers.PickFileResult>? _pickerOperation;
    private Task _pageRead = Task.CompletedTask, _previewLoad = Task.CompletedTask, _addTask = Task.CompletedTask;
    private ImportPageContext? _context;
    private ImportPageSource? _sourceData;
    private ImportSourceProjection? _projection;
    private ImportSourcePlan? _plan;
    private string? _packagePath, _notice, _error;
    private bool _shown, _closed, _dismissRequested, _reading, _adding, _picking, _refreshingStrings, _readsSuspended;
    private int _sourceEpoch, _renderEpoch, _tileSize = 1, _catalogRevision;

    public ImportDialog(Window owner, WallImageService images, StartMenuImportCoordinator catalog, WallPackageReader reader,
        Func<CancellationToken, Task<ImportPageContext>> readContext,
        Func<int, string?, CancellationToken, Task<ImportPageSource>> readSource,
        Func<ImportPageRequest, Task<ImportPageResult>> save, Func<ImportPageContext, bool> contextCurrent,
        Func<WallMember, string?> entryPath, WallVisualStyle style,
        Func<StartMenuImportCandidate, int, CancellationToken, Task<PuzzleFrameResult>>? loadIcon = null)
    {
        _owner = owner; _images = images; _catalog = catalog; _reader = reader;
        _readContext = readContext; _readSource = readSource; _save = save;
        _contextCurrent = contextCurrent; _entryPath = entryPath; _style = style; _loadIcon = loadIcon;
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
        _preview = new ScrollViewer { Content = _scene, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Enabled };
        _preview.ViewChanged += (_, _) => QueuePreviewImages();
        _preview.SizeChanged += (_, _) => QueuePreviewImages();
        var left = new Grid(); left.Children.Add(_preview); left.Children.Add(_development);
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
        Id(_chooseFile, "choose-wall-file"); Id(_refresh, "refresh"); Id(_preview, "candidates"); Id(_status, "status"); Id(_changes, "planned-changes");
        _source.SelectionChanged += (_, _) =>
        {
            if (_refreshingStrings || !_shown || _adding) return;
            _selected.Clear(); _notice = _error = null; _context = null; _sourceData = null;
            _projection = null; _plan = null; ReleaseRows(); _scene.Children.Clear();
            BeginRead(resetSelection: true); UpdateAvailability();
        };
        _position.SelectionChanged += (_, _) => { if (!_refreshingStrings) Reproject(); };
        _autoFilter.Toggled += (_, _) => Reproject(); _recognizeGroups.Toggled += (_, _) => Reproject();
        _small.Click += (_, _) => SetSize(1); _medium.Click += (_, _) => SetSize(2);
        _selectAll.Click += (_, _) =>
        {
            if (_projection is null) return;
            var addable = _projection.Candidates.Where(candidate => candidate.CanAdd).ToArray();
            var clear = addable.All(IsSelected);
            foreach (var candidate in addable) Select(candidate, !clear);
            _error = _notice = null; Replan();
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
        try { await _catalog.RequestRefreshAsync(afterCurrent: true).WaitAsync(request.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) { _error = ex.Message; UpdateStatus(); } }
        finally { if (ReferenceEquals(_catalogRequest, request)) _catalogRequest = null; request.Dispose(); }
    }

    private void BeginRead(bool resetSelection = false)
    {
        if (_closed || _dismissRequested || _adding) return;
        _readsSuspended = false;
        _pageRequest?.Cancel(); CancelPreview();
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _pageRequest = request;
        var epoch = ++_sourceEpoch; var revision = _catalogRevision;
        if (resetSelection) _selected.Clear();
        _reading = true; UpdateAvailability(); UpdateStatus();
        _pageRead = ReadPageAsync(epoch, revision, request); Track(_pageRead);
    }

    private async Task ReadPageAsync(int epoch, int catalogRevision, CancellationTokenSource request)
    {
        try
        {
            // Switching sources waits for the previous bounded thumbnail batch
            // to release its package stream before admitting another one.
            await _previewLoad;
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
                UpdateAvailability(); UpdateStatus(); QueuePreviewImages();
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
            ReleaseRows(); _scene.Children.Clear(); BeginRead(resetSelection: true);
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

    private void SetSize(int size) { _tileSize = size; _small.IsChecked = size == 1; _medium.IsChecked = size == 2; _error = null; Replan(); }

    private void Reproject()
    {
        if (_adding || _context is null || _sourceData is null) return;
        _projection = ImportSourcePlanner.Project(_context.Document, _sourceData.Candidates,
            _recognizeGroups.IsOn, _autoFilter.IsOn, _context.ExistingIdentities);
        var migrated = ImportSourcePlanner.MigrateSelection(_selected, _projection);
        _selected.Clear(); foreach (var pair in migrated.Selected) _selected.Add(pair.Key, pair.Value);
        if (migrated.RemovedCount > 0) _notice = string.Format(ProductStrings.Get("Import.SelectionRemoved"), migrated.RemovedCount);
        Replan();
    }

    private void Replan()
    {
        if (_adding || _context is null || _projection is null) return;
        _plan = ImportSourcePlanner.TryPlan(_context.Document, _context.Document.Generation, _projection,
            _selected.Keys, new GridSize(_tileSize, _tileSize), _position.SelectedIndex == 1 ? ImportInsertionPosition.Tail : ImportInsertionPosition.Head,
            _metrics, _context.Budget);
        RebuildPreview(); UpdateAvailability(); UpdateStatus();
    }

    private bool IsSelected(ImportProjectedCandidate candidate) => candidate.CanAdd && candidate.SourceKeys
        .Any(key => _selected.GetValueOrDefault(key) == candidate.Source.SelectionIdentity);

    private void Select(ImportProjectedCandidate candidate, bool selected)
    {
        if (selected && candidate.CanAdd) _selected[candidate.SelectionKey] = candidate.Source.SelectionIdentity;
        else foreach (var key in candidate.SourceKeys) _selected.Remove(key);
    }

    private void RebuildPreview()
    {
        ReleaseRows(); _scene.Children.Clear(); ++_renderEpoch;
        if (_projection is null || _context is null) return;
        var y = 0d;
        var proposedGroups = _plan?.Proposal?.Document.Lanes.SelectMany(lane => lane.Groups).ToDictionary(group => group.Id, StringComparer.Ordinal);
        foreach (var group in _projection.Groups)
        {
            var visible = group.Candidates.Where(candidate => candidate.Visible).ToArray();
            if (visible.Length == 0) continue;
            var header = WallGroupVisualFactory.Label(group.Title, _metrics.LaneWidth);
            header.Height = _metrics.TitleInteractionHeight;
            Canvas.SetLeft(header, _metrics.Margin); Canvas.SetTop(header, y + _metrics.TitleHeight - _metrics.TitleInteractionHeight);
            _scene.Children.Add(header);
            y += _metrics.TitleHeight + _metrics.TitleToMembersGap;
            var accepted = _plan?.Accepted.Where(item => visible.Any(candidate => candidate.SelectionKey == item.SourceKey)).ToArray() ?? [];
            var destinationId = accepted.FirstOrDefault()?.GroupId ?? group.DestinationGroupId;
            var destination = destinationId is not null && proposedGroups is not null && proposedGroups.TryGetValue(destinationId, out var planned)
                ? planned : _context.Document.Lanes.SelectMany(lane => lane.Groups).FirstOrDefault(item => item.Id == destinationId);
            var bottom = 0;
            if (destination is not null)
                foreach (var member in destination.Members)
                {
                    var item = accepted.FirstOrDefault(item => item.MemberId == member.Id);
                    var candidate = item is null ? null : visible.First(candidate => candidate.SelectionKey == item.SourceKey);
                    AddMember(member, candidate, y, candidate is null ? ProductStrings.Get("Import.ExistingMember") : null);
                    bottom = Math.Max(bottom, member.LocalBounds.Bottom);
                }
            var browseY = y + (bottom == 0 ? 0 : _metrics.HeightOf(bottom) + 24);
            // Unselected rows remain browsable with their real source geometry.
            // This area is explicitly labelled as not included; it never enters
            // the proposal shown above it or any accepted entry/media closure.
            foreach (var block in visible.Where(candidate => !IsSelected(candidate) || _plan?.Status != ImportPlanStatus.Ready)
                         .GroupBy(candidate => candidate.Source.SourceGroup?.Key ?? "ungrouped", StringComparer.Ordinal))
            {
                // Keep this annotation outside individual tiles: even a 1x1
                // English candidate must visibly identify the browsing area.
                var browseTitle = Label(); browseTitle.Text = ProductStrings.Get("Import.NotAccepted");
                browseTitle.Width = _metrics.LaneWidth; browseTitle.FontSize = 12;
                Canvas.SetLeft(browseTitle, _metrics.Margin); Canvas.SetTop(browseTitle, browseY);
                _scene.Children.Add(browseTitle); browseY += 24;
                var next = 0; var blockBottom = 0;
                foreach (var candidate in block)
                {
                    var member = candidate.Source.Member;
                    if (!candidate.Source.HasRecordedSize) member = member with { LocalBounds = new(member.LocalBounds.Column, member.LocalBounds.Row, _tileSize, _tileSize) };
                    if (!candidate.Source.HasRecordedPosition)
                    {
                        var perRow = Math.Max(1, 8 / member.LocalBounds.Width);
                        member = member with { LocalBounds = new((next % perRow) * member.LocalBounds.Width,
                            (next / perRow) * member.LocalBounds.Height, member.LocalBounds.Width, member.LocalBounds.Height) };
                        next++;
                    }
                    // Invalid recorded geometry is a hard gate, never converted
                    // into a plausible default-sized candidate.
                    if (!MemberSizeRules.IsValid(member) || member.LocalBounds.Column < 0 || member.LocalBounds.Row < 0 || member.LocalBounds.Right > 8)
                    {
                        var invalid = Label(); invalid.Text = candidate.Source.DisplayName + " · " + ErrorText(candidate.ErrorCode ?? "INVALID_SOURCE_GEOMETRY");
                        invalid.Width = _metrics.LaneWidth; Canvas.SetLeft(invalid, _metrics.Margin); Canvas.SetTop(invalid, browseY);
                        _scene.Children.Add(invalid); browseY += 44; continue;
                    }
                    AddMember(member, candidate, browseY, ProductStrings.Get("Import.NotAccepted"));
                    blockBottom = Math.Max(blockBottom, member.LocalBounds.Bottom);
                }
                if (blockBottom > 0) browseY += _metrics.HeightOf(blockBottom) + 24;
            }
            y = Math.Max(browseY, y + (bottom == 0 ? 0 : _metrics.HeightOf(bottom))) + 20;
        }
        _scene.Width = _metrics.LaneWidth + _metrics.Margin * 2;
        _scene.Height = Math.Max(1, y);
        QueuePreviewImages();
    }

    private void AddMember(WallMember member, ImportProjectedCandidate? candidate, double originY, string? annotation)
    {
        var bounds = member.LocalBounds;
        var width = _metrics.WidthOf(bounds.Width); var height = _metrics.HeightOf(bounds.Height);
        var card = new Grid { Width = width, Height = height };
        var view = WallMemberVisualFactory.Create(member, null, _metrics, _dialog.XamlRoot?.RasterizationScale ?? 1,
            _style, interactive: false, externalInteraction: true);
        view.IsHitTestVisible = false; card.Children.Add(view);
        var selection = WallMemberVisualFactory.AddSelection(card, _style);
        var toggle = new ToggleButton { Content = card, Width = width, Height = height, Padding = new Thickness(0),
            BorderThickness = new Thickness(0), MinWidth = 0, MinHeight = 0, CornerRadius = WallCornerStyle.Corners(_style),
            IsEnabled = candidate?.CanAdd == true && !_adding, IsChecked = candidate is not null && IsSelected(candidate) };
        var selected = toggle.IsChecked == true;
        selection.Outline.Visibility = selection.Check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        var reset = WallMemberVisualFactory.ConfigureInteraction(toggle, card, toggle.CornerRadius, view.SetExternalHover);
        var dimensions = string.Format(ProductStrings.Get("Import.PreviewDimensions"), bounds.Width, bounds.Height, bounds.Column, bounds.Row);
        var tooltip = (candidate?.Source.DisplayName ?? member.Visual.Title ?? member.Id) + "\n" + dimensions;
        if (annotation is not null) tooltip += "\n" + annotation;
        if (candidate?.ErrorCode is { } error) tooltip += "\n" + ErrorText(error);
        if (candidate is { FilterReason: not ImportFilterReason.None }) tooltip += "\n" + FilterText(candidate.FilterReason);
        if (_sourceData?.StartMenuSources.TryGetValue(candidate?.SelectionKey ?? "", out var source) == true) tooltip += "\n" + source.Candidate.FullPath;
        ActionIcon.SetTip(toggle, tooltip); AutomationProperties.SetName(toggle, tooltip);
        Id(toggle, "candidate-" + (candidate?.SelectionKey ?? member.Id));
        toggle.Checked += Changed; toggle.Unchecked += Changed;
        void Changed(object sender, RoutedEventArgs args)
        {
            if (candidate is null || _adding) return;
            Select(candidate, toggle.IsChecked == true); _error = _notice = null; Replan();
        }
        var label = new TextBlock { Text = dimensions + (annotation is null ? "" : " · " + annotation), FontSize = 10,
            TextWrapping = TextWrapping.Wrap, MaxLines = 2, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(3), IsHitTestVisible = false,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
        var band = new Border { Child = label, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 0, 0, 0)),
            VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
        card.Children.Add(band);
        if (member is ClockMember clock)
        {
            var time = new TextBlock { Text = TileWall.Core.Components.ClockTextFormatter.Format(DateTimeOffset.Now,
                clock.TimeFormat, clock.DateFormat, System.Globalization.CultureInfo.CurrentCulture).TimeLine,
                FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            card.Children.Add(time); time.IsHitTestVisible = false;
        }
        var top = originY + bounds.Row * _metrics.Pitch;
        Canvas.SetLeft(toggle, _metrics.Margin + bounds.Column * _metrics.Pitch); Canvas.SetTop(toggle, top);
        _scene.Children.Add(toggle); _rows.Add(new(member, candidate, view, toggle, reset, top, height));
    }

    private void ReleaseRows()
    {
        CancelPreview();
        foreach (var row in _rows) { row.Reset(); row.View.StopTransientFeedback(); row.View.UpdateFrame(null); }
        _rows.Clear();
    }

    private void CancelPreview() => _previewRequest?.Cancel();

    private void QueuePreviewImages()
    {
        if (!_shown || _closed || _dismissRequested || _readsSuspended || _adding || _reading || _sourceData is null || _rows.Count == 0) return;
        CancelPreview();
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _previewRequest = request;
        var top = _preview.VerticalOffset - 128; var bottom = top + Math.Max(400, _preview.ViewportHeight) + 256;
        var visible = _rows.Where(row => row.Top + row.Height >= top && row.Top <= bottom).ToArray();
        foreach (var row in _rows.Except(visible)) row.View.UpdateFrame(null);
        var previous = _previewLoad; var source = _sourceData; var epoch = _renderEpoch;
        _previewLoad = LoadPreviewImagesAsync(previous, source, visible, epoch, request); Track(_previewLoad);
    }

    private async Task LoadPreviewImagesAsync(Task previous, ImportPageSource source, PreviewRow[] rows,
        int epoch, CancellationTokenSource request)
    {
        WallPackageImport? batch = null;
        try
        {
            await previous;
            request.Token.ThrowIfCancellationRequested();
            foreach (var row in rows)
            {
                request.Token.ThrowIfCancellationRequested();
                if (row.Member is ClockMember) continue;
                var pixels = Math.Clamp((int)Math.Ceiling(Math.Max(_metrics.WidthOf(row.Member.LocalBounds.Width),
                    _metrics.HeightOf(row.Member.LocalBounds.Height)) * (_dialog.XamlRoot?.RasterizationScale ?? 1)), 64, 256);
                var cached = row.Candidate is { } factCandidate ? source.StartMenuSources.GetValueOrDefault(factCandidate.SelectionKey) : null;
                var cacheKey = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Origin = row.Candidate is null ? "existing" : "candidate",
                    Package = row.Candidate is null ? null : source.Package?.PackageSha256,
                    SourceKey = row.Candidate?.SelectionKey, row.Member.Id, row.Member.Images,
                    row.Member.Visual.BackgroundMode, CurrentImage = row.Member.Carousel?.CurrentImageId,
                    Identity = row.Candidate?.Source.Identity,
                    EntryHash = row.Candidate is null ? _context?.ExistingEntrySha256.GetValueOrDefault(row.Member.Id) : cached?.SourceSha256,
                    IconKey = cached?.IconKey, Pixels = pixels,
                });
                if (!_frames.TryGetValue(cacheKey, out var frame))
                {
                    PuzzleFrameResult? result = null;
                    if (source.Package is { } package && row.Candidate is not null && row.Member.Images.Media is { } media)
                    {
                        batch ??= await _reader.OpenImportAsync(package, request.Token);
                        var asset = (await batch.PrepareMediaAsync([media], token: request.Token)).Single();
                        result = await _images.LoadPackageThumbnailAsync(asset, pixels,
                            row.Member.Visual.BackgroundMode == MemberBackgroundMode.Blur, request.Token);
                    }
                    else if (row.Candidate is { } candidate && source.StartMenuSources.TryGetValue(candidate.SelectionKey, out var original) && _loadIcon is not null)
                        result = await _loadIcon(original, pixels, request.Token);
                    if (result?.Frame is null)
                    {
                        var path = row.Candidate is null ? _entryPath(row.Member) : row.Candidate.Source.Identity?.Target ?? row.Member.TargetInput;
                        result = await _images.LoadMemberFrameAsync(row.Member, path, pixels, pixels, ct: request.Token, purpose: MemberImagePurpose.Thumbnail);
                    }
                    frame = result.Frame;
                    if (frame is not null)
                    {
                        _frames[cacheKey] = frame; _frameOrder.Enqueue(cacheKey);
                        while (_frames.Count > 32 && _frameOrder.TryDequeue(out var old)) _frames.Remove(old);
                    }
                }
                if (!_closed && !_dismissRequested && epoch == _renderEpoch && !request.IsCancellationRequested) row.View.UpdateFrame(frame);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && !_dismissRequested && epoch == _renderEpoch && !request.IsCancellationRequested) { _error = ex.Message; UpdateStatus(); } }
        finally { batch?.Dispose(); if (ReferenceEquals(_previewRequest, request)) _previewRequest = null; request.Dispose(); }
    }

    private void StartAdd()
    {
        if (_closed || _dismissRequested || _adding || _reading || _picking || _context is null || _projection is null ||
            _sourceData is null || _plan?.Status != ImportPlanStatus.Ready || !_projection.Candidates.Any(IsSelected)) return;
        if (!_contextCurrent(_context)) { _notice = ProductStrings.Get("Import.SourceChanged"); BeginRead(resetSelection: true); return; }
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
        _preview.Visibility = implemented ? Visibility.Visible : Visibility.Collapsed;
        _development.Visibility = implemented ? Visibility.Collapsed : Visibility.Visible;
        _autoFilter.IsEnabled = _recognizeGroups.IsEnabled = _small.IsEnabled = _medium.IsEnabled = _position.IsEnabled = implemented;
        _refresh.IsEnabled = implemented && !_reading;
        var addable = _projection?.Candidates.Where(candidate => candidate.CanAdd).ToArray() ?? [];
        var count = addable.Count(IsSelected);
        _selectAll.IsEnabled = implemented && !_reading && addable.Length > 0;
        _selectAll.IsChecked = count == 0 ? false : count == addable.Length ? true : null;
        _dialog.IsPrimaryButtonEnabled = !_closed && !_dismissRequested && !_adding && !_reading && !_picking &&
            implemented && count > 0 && _plan?.Status == ImportPlanStatus.Ready && _context is not null && _contextCurrent(_context);
        foreach (var row in _rows) row.Toggle.IsEnabled = !_adding && !_reading && row.Candidate?.CanAdd == true;
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
        _changes.Text = string.Format(ProductStrings.Get("Import.PlannedChanges"), _plan?.AddedCount ?? 0, _plan?.OverwrittenCount ?? 0);
        if (_plan?.OverwrittenCount > 0) _changes.Text += "\n" + ProductStrings.Get("Import.OverwriteNotice");
        var messages = new List<string>();
        if (_adding) messages.Add(ProductStrings.Get("Import.Adding"));
        else if (_reading || _sourceData?.Refreshing == true) messages.Add(ProductStrings.Get("Import.Updating"));
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
        _readsSuspended = true; CancelPicker(); _catalogRequest?.Cancel(); _pageRequest?.Cancel(); CancelPreview();
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
        _dismissRequested = true; CancelPicker(); _pageRequest?.Cancel(); CancelPreview(); UpdateAvailability();
        if (!_adding && !_picking) FinishDismiss();
    }
    private void FinishDismiss() { StopRequests(); _dialog.Hide(); }
    private void StopRequests()
    {
        if (_closed) return;
        _closed = true; CancelPicker(); _lifetime.Cancel(); _pageRequest?.Cancel(); CancelPreview();
        _catalog.Changed -= _catalogChanged; ReleaseRows(); _frames.Clear(); _frameOrder.Clear();
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
        if (_shown && !_adding) RebuildPreview();
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
    private sealed record PreviewRow(WallMember Member, ImportProjectedCandidate? Candidate, PuzzleCanvasView View,
        ToggleButton Toggle, Action Reset, double Top, double Height);
}
