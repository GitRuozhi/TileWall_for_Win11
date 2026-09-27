using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using TileWall.ApplicationModel;
using TileWall.Core.Import;
using TileWall.Core.Shell;
using TileWall.Graphics;
using TileWall.Resources;

namespace TileWall.Views;

/// <summary>A cancellable candidate session; only the host's batch commit changes the wall.</summary>
internal sealed class ImportDialog : IWallDialogSession
{
    private readonly ContentDialog _dialog;
    private readonly DialogExitPrompt _exit;
    private readonly WallImageService _images;
    private readonly Func<IReadOnlyList<ImportBatchItem>, ImportPreview> _plan;
    private readonly Func<IReadOnlyList<ImportBatchItem>, string?> _save;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<CancellationTokenSource> _rowRequests = [];
    private readonly TextBox _search = new();
    private readonly Button _selectAll = new(), _clear = new();
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.None };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _previewText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ScrollViewer _previewHost;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImportBatchItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<StartMenuCandidate> _candidates = [];
    private ImportPreview? _preview;
    private Task _pending = Task.CompletedTask;
    private bool _saved, _closed, _allowDiscard, _discarded;
    private int _skipped;

    public ImportDialog(Window owner, WallImageService images,
        Func<IReadOnlyList<ImportBatchItem>, ImportPreview> plan,
        Func<IReadOnlyList<ImportBatchItem>, string?> save)
    {
        _images = images; _plan = plan; _save = save;
        // The virtualizing panel measures the template before ContainerContentChanging.
        // Populate its existing root rather than replacing the container's content after that measure.
        _list.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Grid MinHeight="32" />
            </DataTemplate>
            """);
        var root = (owner.Content as FrameworkElement)?.XamlRoot;
        var body = new Grid { RowSpacing = 8,
            Width = Math.Min(500, Math.Max(200, (root?.Size.Width ?? 760) - 100)),
            Height = Math.Min(540, Math.Max(180, (root?.Size.Height ?? 700) - 200)) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            body.RowDefinitions.Add(new RowDefinition { Height = height });
        body.Children.Add(_search);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_selectAll); actions.Children.Add(_clear); Grid.SetRow(actions, 1); body.Children.Add(actions);
        Grid.SetRow(_list, 2); body.Children.Add(_list);
        _previewHost = new ScrollViewer { Content = _previewText, MaxHeight = Math.Min(130, body.Height * 0.3),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
        Grid.SetRow(_previewHost, 3); body.Children.Add(_previewHost);
        var statusHost = new ScrollViewer { Content = _status, MaxHeight = 60, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(statusHost, 4); body.Children.Add(statusHost);
        _dialog = new ContentDialog { XamlRoot = root,
            Content = body, DefaultButton = ContentDialogButton.None, IsPrimaryButtonEnabled = false };
        _exit = new DialogExitPrompt(_dialog);
        Id(_dialog, "dialog"); Id(_search, "search"); Id(_selectAll, "select-all"); Id(_clear, "clear");
        Id(_list, "candidates"); Id(_previewText, "preview"); Id(_status, "status");
        _search.TextChanged += (_, _) => Filter();
        _list.ContainerContentChanging += OnContainerContentChanging;
        _selectAll.Click += (_, _) =>
        {
            foreach (var item in Filtered().Where(c => c.IsImportable)) _selected.Add(item.FullPath);
            SelectionChanged(); Filter();
        };
        _clear.Click += (_, _) => { _selected.Clear(); SelectionChanged(); Filter(); };
        _dialog.PrimaryButtonClick += (_, e) =>
        {
            if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.SaveAndExit); return; }
            if (TryConfirmSelection() is not null) e.Cancel = true;
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
        try { await _dialog.ShowAsync(); }
        finally { StopRequests(); _lifetime.Dispose(); }
    }

    private async Task ScanAsync()
    {
        _status.Text = ProductStrings.Get("Import.Scanning");
        try
        {
            var result = await StartMenuImportScan.RunAsync(_lifetime.Token);
            if (_closed || _lifetime.IsCancellationRequested) return;
            _candidates = result.Candidates.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
            _skipped = result.SkippedDirectories;
            Filter(); UpdateStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) _status.Text = string.Format(ProductStrings.Get("Import.ScanFailed"), ex.Message); }
    }

    private IEnumerable<StartMenuCandidate> Filtered() => _candidates.Where(c =>
        string.IsNullOrWhiteSpace(_search.Text) || c.DisplayName.Contains(_search.Text, StringComparison.CurrentCultureIgnoreCase)
        || c.FullPath.Contains(_search.Text, StringComparison.CurrentCultureIgnoreCase));

    private void Filter() => _list.ItemsSource = Filtered().ToArray();

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (e.ItemContainer.Tag is CancellationTokenSource old) { ReleaseRowRequest(old); e.ItemContainer.Tag = null; }
        if (e.ItemContainer.ContentTemplateRoot is not Grid host) return;
        host.Children.Clear();
        e.Handled = true;
        if (_closed || e.InRecycleQueue || e.Item is not StartMenuCandidate candidate) return;
        var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _rowRequests.Add(tokenSource);
        e.ItemContainer.Tag = tokenSource;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var icon = new Image { Width = 32, Height = 32 };
        var placeholder = new TextBlock { Text = "▣", FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center };
        var iconHost = new Grid { Width = 32, Height = 32 };
        iconHost.Children.Add(placeholder); iconHost.Children.Add(icon); row.Children.Add(iconHost);
        var labels = new StackPanel();
        labels.Children.Add(new TextBlock { Text = candidate.DisplayName, TextWrapping = TextWrapping.Wrap });
        var source = ProductStrings.Get(candidate.Source == SourceKind.User ? "Import.UserSource" : "Import.CommonSource");
        labels.Children.Add(new TextBlock { Text = $"{source} · {candidate.FullPath}", FontSize = 11,
            MaxWidth = 380, TextWrapping = TextWrapping.Wrap });
        if (!candidate.IsImportable) labels.Children.Add(new TextBlock
        { Text = string.Format(ProductStrings.Get("Import.InvalidName"), string.Join("; ", candidate.NotImportableReasons)),
            MaxWidth = 380, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(labels);
        var check = new CheckBox { Content = row, IsChecked = _selected.Contains(candidate.FullPath), IsEnabled = candidate.IsImportable };
        AutomationProperties.SetName(check, $"{candidate.DisplayName} · {source} · {candidate.FullPath}");
        AutomationProperties.SetAutomationId(check, "import-candidate-" + candidate.FullPath);
        check.Checked += (_, _) => { _selected.Add(candidate.FullPath); SelectionChanged(); };
        check.Unchecked += (_, _) => { _selected.Remove(candidate.FullPath); SelectionChanged(); };
        host.Children.Add(check);
        _ = LoadIconAsync();

        async Task LoadIconAsync()
        {
            try
            {
                var tile = new TileWall.Core.Wall.TileMember
                { Id = "candidate", LocalBounds = new TileWall.Core.Grid.GridRect(0, 0, 1, 1) };
                var pixels = (int)Math.Ceiling(32 * (_dialog.XamlRoot?.RasterizationScale ?? 1));
                var result = await _images.LoadForegroundAsync(tile, candidate.FullPath, pixels, pixels, tokenSource.Token);
                if (tokenSource.IsCancellationRequested) return;
                icon.Source = result.Source;
                placeholder.Visibility = result.Source is null ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            finally { ReleaseRowRequest(tokenSource); }
        }
    }

    private void ReleaseRowRequest(CancellationTokenSource request)
    {
        if (!_rowRequests.Remove(request)) return;
        request.Cancel();
        request.Dispose();
    }

    private void StopRequests()
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        foreach (var request in _rowRequests.ToArray()) ReleaseRowRequest(request);
    }

    private void SelectionChanged()
    {
        _preview = null; _previewHost.Visibility = Visibility.Collapsed;
        _allowDiscard = false; _discarded = false;
        RefreshStrings(); UpdateStatus();
    }

    private void UpdateStatus() => _status.Text = string.Format(ProductStrings.Get("Import.Count"), _candidates.Count, _selected.Count, _skipped);
    public bool HasUnsavedDraft => !_saved && _selected.Count != 0;
    public async Task WaitForPendingAsync()
    {
        // This wait precedes a cancellable exit decision, so it must not cancel the live draft's work.
        try { await _pending.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
    }
    public Task<ExitDecision> ConfirmExitAsync() => _exit.ConfirmAsync();
    public void Close() { _discarded = true; StopRequests(); _dialog.Hide(); }
    public void DiscardDraft() { _selected.Clear(); _discarded = true; }

    private ImportBatchItem[] SelectedItems() => _candidates.Where(c => _selected.Contains(c.FullPath)).Select(c =>
    {
        if (!_items.TryGetValue(c.FullPath, out var item))
            _items[c.FullPath] = item = ImportBatchItem.Create(c.FullPath, c.DisplayName);
        return item;
    }).ToArray();

    private string? TryConfirmSelection()
    {
        if (_selected.Count == 0) return Fail(ProductStrings.Get("Import.SelectFirst"));
        try
        {
            var latest = _plan(SelectedItems());
            if (latest.Error is not null) return Fail(latest.Error);
            if (_preview is null || _preview.Text != latest.Text)
            {
                _preview = latest; _previewText.Text = latest.Text; _previewHost.Visibility = Visibility.Visible;
                RefreshStrings(); return Fail(ProductStrings.Get("Import.Review"));
            }
            return TrySaveNow();
        }
        catch (Exception ex) { return Fail(string.Format(ProductStrings.Get("Import.Failed"), ex.Message)); }
    }

    public string? TrySaveNow()
    {
        if (_selected.Count == 0) return Fail(ProductStrings.Get("Import.SelectFirst"));
        try
        {
            // The host replans against current capacity and commits once. Save-and-exit is itself confirmation.
            var error = _save(SelectedItems());
            if (error is not null) return Fail(error);
            _saved = true;
            return null;
        }
        catch (Exception ex) { return Fail(string.Format(ProductStrings.Get("Import.Failed"), ex.Message)); }
    }

    private string Fail(string error) { _status.Text = error; return error; }
    private void OfferDiscard() { _allowDiscard = true; RefreshStrings(); _status.Text = ProductStrings.Get("Common.UnsavedDraft"); }
    public void RefreshStrings()
    {
        if (_exit.IsActive) return;
        _dialog.Title = ProductStrings.Get("Import.Title");
        _dialog.PrimaryButtonText = ProductStrings.Get(_preview is null ? "Import.Preview" : "Import.Confirm");
        _dialog.IsPrimaryButtonEnabled = _selected.Count != 0;
        _dialog.CloseButtonText = ProductStrings.Get(_allowDiscard ? "Common.Discard" : "Common.Cancel");
        _search.PlaceholderText = ProductStrings.Get("Import.Search");
        AutomationProperties.SetName(_search, ProductStrings.Get("Import.Search"));
        _selectAll.Content = ProductStrings.Get("Import.SelectAll"); _clear.Content = ProductStrings.Get("Import.Clear");
    }
    private static void Id(DependencyObject element, string id) => AutomationProperties.SetAutomationId(element, "import-" + id);
}
