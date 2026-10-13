using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using TileWall.Core.Storage;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;
using Windows.Foundation;
using Windows.Storage.Pickers;

namespace TileWall.Views;

internal sealed record PuzzleEditSubmission(PuzzleMember Puzzle, IReadOnlyList<EntryChange> EntryChanges);

/// <summary>Owns the complete parent draft; the save callback is the only commit boundary.</summary>
internal sealed class PuzzleEditorDialog : IWallDialogSession
{
    private readonly Window _owner;
    private readonly PuzzleMember _original;
    private readonly string? _originalTarget;
    private readonly bool _idList, _opaqueEntry;
    private readonly bool _trace;
    private readonly WallImageService _images;
    private readonly Func<PuzzleEditSubmission, string?> _save;
    private readonly WallMetrics _metrics = new();
    private readonly ContentDialog _dialog;
    private readonly DialogExitPrompt _exit;
    private readonly Grid _page;
    private readonly TextBox _title, _target, _paths;
    private readonly CheckBox _showTitle;
    private readonly ComboBox _entryKind, _sourceKind, _candidates;
    private readonly Button _targetBrowse, _sourceBrowse;
    private readonly TextBlock _summary = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed) };
    private readonly PuzzleCanvasView _preview = new();
    private readonly Dictionary<string, PuzzleImageTransform> _transforms = new(StringComparer.OrdinalIgnoreCase);
    private PuzzleMember _draft;
    private PuzzleFrame? _frame;
    private PuzzleLayoutEditorDialog? _layout;
    private CancellationTokenSource? _request;
    private long _generation;
    private bool _saved, _closed, _fillingCandidates, _dirty, _allowDiscard, _discardConfirmed;
    private Point? _panPoint;
    private Task _pickerTask = Task.CompletedTask;

    public PuzzleEditorDialog(Window owner, PuzzleMember original,
        string? originalTarget, bool idList, bool opaqueEntry, string? entryDiagnostic,
        int maxHeight, WallImageService images, string? entryPath, Func<PuzzleEditSubmission, string?> save, bool trace = false)
    {
        _owner = owner; _original = original; _originalTarget = originalTarget;
        _idList = idList; _opaqueEntry = opaqueEntry; _images = images; _save = save; _trace = trace;
        _draft = original;
        foreach (var transform in _draft.Images.Transforms)
        {
            var id = CanonicalImagePath(transform.ImageId);
            if (!_transforms.TryAdd(id, transform with { ImageId = id }))
                throw new InvalidDataException(ProductStrings.Get("Puzzle.DuplicateTransformOpen"));
        }
        _title = Text(ProductStrings.Get("Entry.Title"), "title", _draft.Visual.Title ?? string.Empty);
        _showTitle = new CheckBox { Content = ProductStrings.Get("Common.ShowTitle"), IsChecked = _draft.Visual.ShowTitle };
        Id(_showTitle, "show-title");
        _entryKind = Choice(ProductStrings.Get("Puzzle.Target"), "entry-kind", [ProductStrings.Get("Puzzle.NoTarget"), ProductStrings.Get("Entry.Path"), ProductStrings.Get("Entry.Copy"), ProductStrings.Get("Entry.Url")],
            original.Entry?.Kind switch { ManagedEntryKind.Lnk => 1, ManagedEntryKind.Url => 3, _ => 0 });
        _target = Text(ProductStrings.Get("Entry.Target"), "target", originalTarget ?? string.Empty);
        _targetBrowse = Action(ProductStrings.Get("Entry.Browse"), "target-browse", () => _pickerTask = PickTargetAsync());
        _sourceKind = Choice(ProductStrings.Get("Puzzle.Source"), "source-kind", [ProductStrings.Get("Puzzle.NoImage"), ProductStrings.Get("Puzzle.SingleImage"), ProductStrings.Get("Puzzle.MultipleImages"), ProductStrings.Get("Puzzle.Folder")], (int)_draft.Images.Kind);
        _paths = Text(ProductStrings.Get("Puzzle.Paths"), "source-paths", string.Empty);
        _paths.AcceptsReturn = true; _paths.TextWrapping = TextWrapping.Wrap; _paths.MaxHeight = 110;
        _paths.Text = _draft.Images.Kind == PuzzleImageSourceKind.Folder
            ? _draft.Images.FolderPath ?? string.Empty : string.Join(Environment.NewLine, _draft.Images.ImagePaths);
        _sourceBrowse = Action(ProductStrings.Get("Puzzle.Browse"), "source-browse", () => _pickerTask = PickSourceAsync());
        _candidates = Choice(ProductStrings.Get("Puzzle.CurrentImage"), "candidates", [], -1);
        _candidates.MaxWidth = 290;
        Id(_summary, "layout-summary"); Id(_status, "image-status"); Id(_error, "editor-error");
        var form = new StackPanel { Spacing = 9 };
        foreach (var item in new UIElement[] { _title, _showTitle, _summary,
            Action(ProductStrings.Get("Puzzle.EditLayout"), "layout", OpenLayout), _entryKind, _target, _targetBrowse,
            _sourceKind, _paths, _sourceBrowse, _candidates, _status }) form.Children.Add(item);
        var rootSize = (owner.Content as FrameworkElement)?.XamlRoot?.Size ?? new Size(740, 760);
        var pageWidth = Math.Min(640, Math.Max(260, rootSize.Width - 80));
        var pageHeight = Math.Min(560, Math.Max(120, rootSize.Height - 210));
        var narrow = pageWidth < 640;
        var picture = new StackPanel { Spacing = 10, Margin = narrow ? new Thickness(0, 18, 0, 0) : new Thickness(18, 0, 0, 0) };
        picture.Children.Add(new TextBlock { Text = ProductStrings.Get("Puzzle.Preview"), FontSize = 18 });
        picture.Children.Add(new TextBlock { Text = ProductStrings.Get("Puzzle.GestureHint"), TextWrapping = TextWrapping.Wrap });
        Id(_preview, "preview");
        picture.Children.Add(new Viewbox { Child = _preview, Stretch = Stretch.Uniform, Width = Math.Min(300, pageWidth), Height = 320 });
        var fit = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        fit.Children.Add(Action(ProductStrings.Get("Puzzle.Fill"), "fill", () => Reset(FitMode.CoverFill)));
        fit.Children.Add(Action(ProductStrings.Get("Puzzle.Fit"), "fit", () => Reset(FitMode.FitAll)));
        fit.Children.Add(Action(ProductStrings.Get("Puzzle.Reset"), "reset", () => Reset(CurrentTransform()?.Fit ?? FitMode.CoverFill)));
        picture.Children.Add(fit);
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        zoom.Children.Add(Action(ProductStrings.Get("Puzzle.ZoomOut"), "zoom-out", () => Zoom(1 / 1.1, Center)));
        zoom.Children.Add(Action(ProductStrings.Get("Puzzle.ZoomIn"), "zoom-in", () => Zoom(1.1, Center)));
        var pan = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        pan.Children.Add(Action("←", "pan-left", () => Pan(-10, 0)));
        pan.Children.Add(Action("→", "pan-right", () => Pan(10, 0)));
        pan.Children.Add(Action("↑", "pan-up", () => Pan(0, -10)));
        pan.Children.Add(Action("↓", "pan-down", () => Pan(0, 10)));
        picture.Children.Add(zoom);
        picture.Children.Add(pan);
        _page = new Grid { MaxHeight = pageHeight, Width = pageWidth };
        _page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (narrow)
        {
            // A single-lane wall is narrower than the wide two-column editor.
            // Keep native controls reachable without shrinking text or clipping the error footer.
            var combined = new StackPanel(); combined.Children.Add(form); combined.Children.Add(picture);
            _page.Children.Add(new ScrollViewer { Content = combined, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }
        else
        {
            _page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            _page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            _page.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var previewScroll = new ScrollViewer { Content = picture, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(previewScroll, 1); _page.Children.Add(previewScroll);
        }
        var errorHost = new ScrollViewer { Content = _error, MaxHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(errorHost, 1); Grid.SetColumnSpan(errorHost, 2); _page.Children.Add(errorHost);
        _dialog = new ContentDialog { XamlRoot = (owner.Content as FrameworkElement)?.XamlRoot,
            Title = ProductStrings.Get("Puzzle.Edit"), Content = _page,
            PrimaryButtonText = ProductStrings.Get("Common.Save"), CloseButtonText = ProductStrings.Get("Common.Cancel"), DefaultButton = ContentDialogButton.None };
        _dialog.Resources["ContentDialogMaxWidth"] = 740d;
        _exit = new(_dialog);
        Id(_dialog, "editor");
        _dialog.PrimaryButtonClick += OnSave;
        _dialog.SecondaryButtonClick += (_, e) => { if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.DiscardAndExit); } };
        _dialog.CloseButtonClick += (_, e) =>
        {
            if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.CancelExit); return; }
            if (_layout is not null) { e.Cancel = true; CloseLayout(false); return; }
            if (_dirty && !_allowDiscard) { e.Cancel = true; OfferDiscard(); }
            else _discardConfirmed = true;
        };
        _dialog.Closing += (_, e) =>
        {
            if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.CancelExit); return; }
            if (_layout is not null) { e.Cancel = true; CloseLayout(false); return; }
            if (!_saved && _dirty && !_discardConfirmed) { e.Cancel = true; OfferDiscard(); }
        };
        _dialog.Opened += (_, _) => { _title.Focus(FocusState.Programmatic); _ = ReloadSourceAsync(); };
        _title.TextChanged += (_, _) => { MarkDirty(); RefreshPreview(); };
        _showTitle.Checked += (_, _) => { MarkDirty(); RefreshPreview(); }; _showTitle.Unchecked += (_, _) => { MarkDirty(); RefreshPreview(); };
        _entryKind.SelectionChanged += (_, _) => { MarkDirty(); RefreshVisibility(); };
        _target.TextChanged += (_, _) => MarkDirty();
        _sourceKind.SelectionChanged += (_, _) => { MarkDirty(); RefreshVisibility(); _ = ReloadSourceAsync(); };
        _paths.TextChanged += (_, _) => { MarkDirty(); _ = ReloadSourceAsync(); };
        _candidates.SelectionChanged += (_, _) => { if (!_fillingCandidates) { MarkDirty(); _ = LoadSelectionAsync(); } };
        _preview.PointerPressed += OnPanPressed;
        _preview.PointerMoved += OnPanMoved;
        _preview.PointerReleased += (_, e) => { _panPoint = null; _preview.ReleasePointerCapture(e.Pointer); };
        _preview.PointerCaptureLost += (_, _) => _panPoint = null;
        _preview.PointerCanceled += (_, _) => _panPoint = null;
        _preview.PointerWheelChanged += (_, e) =>
        {
            var p = e.GetCurrentPoint(_preview);
            Zoom(Math.Pow(1.1, p.Properties.MouseWheelDelta / 120d), new(p.Position.X, p.Position.Y)); e.Handled = true;
        };
        RefreshVisibility(); RefreshPreview();
        if (!string.IsNullOrWhiteSpace(entryDiagnostic)) _error.Text = entryDiagnostic;
    }

    public async Task<bool> ShowAsync()
    {
        try { await OwnedDialogWindow.ShowAsync(_dialog); return _saved; }
        finally { _closed = true; CancelRequest(); }
    }

    public bool HasUnsavedDraft => _dirty || _layout is not null &&
        (_layout.Current.Size != _draft.LocalBounds.Size || !_layout.Current.Partitions.SequenceEqual(_draft.Partitions));
    public Task WaitForPendingAsync() => _pickerTask;
    public Task<ExitDecision> ConfirmExitAsync()
    {
        _panPoint = null; _layout?.FinishStroke(); return _exit.ConfirmAsync();
    }
    public void DiscardDraft() { _discardConfirmed = true; _dirty = false; }
    public void RequestDismiss() => _dialog.Hide();
    public void Close() { _discardConfirmed = true; _layout = null; _dialog.Hide(); }
    public void RefreshStrings()
    {
        if (_exit.IsActive) return;
        _dialog.Title = ProductStrings.Get(_layout is not null ? "Puzzle.LayoutTitle" : "Puzzle.Edit");
        _dialog.PrimaryButtonText = ProductStrings.Get(_layout is not null ? "Puzzle.AcceptLayout" : "Common.Save");
        _dialog.CloseButtonText = ProductStrings.Get(_layout is not null ? "Puzzle.CancelLayout" : _allowDiscard ? "Common.Discard" : "Common.Cancel");
    }

    private void RefreshVisibility()
    {
        _target.Visibility = _entryKind.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
        _target.IsReadOnly = _idList && _entryKind.SelectedIndex == 1;
        _targetBrowse.Visibility = _entryKind.SelectedIndex is 1 or 2 ? Visibility.Visible : Visibility.Collapsed;
        _paths.Visibility = _sourceBrowse.Visibility = _sourceKind.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MarkDirty()
    {
        _dirty = true; _discardConfirmed = false;
        if (!_allowDiscard) return;
        _allowDiscard = false; _dialog.CloseButtonText = ProductStrings.Get("Common.Cancel"); _error.Text = string.Empty;
    }

    private void OfferDiscard()
    {
        _allowDiscard = true; _dialog.CloseButtonText = ProductStrings.Get("Common.Discard");
        _error.Text = ProductStrings.Get("Common.UnsavedDraft");
    }

    private PuzzleImageSource ReadSource() => new()
    {
        Kind = (PuzzleImageSourceKind)_sourceKind.SelectedIndex,
        ImagePaths = _sourceKind.SelectedIndex is 1 or 2 ? _paths.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(_sourceKind.SelectedIndex == 1 ? 1 : int.MaxValue).ToArray() : [],
        FolderPath = _sourceKind.SelectedIndex == 3 ? _paths.Text.Trim() : null,
        Transforms = _transforms.Values.ToArray(),
    };

    private (long Generation, CancellationToken Token) BeginRequest()
    {
        CancelRequest(); _request = new(); return (_generation, _request.Token);
    }
    private void CancelRequest() { ++_generation; _request?.Cancel(); _request?.Dispose(); _request = null; }
    private bool Active(long generation) => !_closed && generation == _generation;

    private async Task ReloadSourceAsync()
    {
        var request = BeginRequest();
        _panPoint = null; _frame = null;
        _fillingCandidates = true; _candidates.Items.Clear(); _fillingCandidates = false;
        _draft = _draft with { Images = ReadSource() }; RefreshPreview();
        Trace($"SourceChanged generation={request.Generation} kind={_draft.Images.Kind} paths={string.Join('|', _draft.Images.ImagePaths)} folder={_draft.Images.FolderPath}");
        _status.Text = ProductStrings.Get("Puzzle.ReadingSource");
        try
        {
            var result = await _images.EnumeratePuzzleCandidatesAsync(_draft.Images, request.Token);
            if (!Active(request.Generation)) { Trace($"CandidatesRejected generation={request.Generation}"); return; }
            _fillingCandidates = true;
            foreach (var path in result.Paths) _candidates.Items.Add(path);
            var preferred = result.Paths.ToList().FindIndex(p => string.Equals(p, _draft.Carousel?.CurrentImageId, StringComparison.OrdinalIgnoreCase));
            _candidates.SelectedIndex = preferred >= 0 ? preferred : result.Paths.Count > 0 ? 0 : -1;
            _fillingCandidates = false;
            _status.Text = result.Paths.Count > 0 ? string.Format(ProductStrings.Get("Puzzle.Count"), result.Paths.Count) : result.Diagnostic ?? ProductStrings.Get("Puzzle.NoCandidates");
            if (_candidates.SelectedIndex >= 0) await LoadSelectionAsync();
        }
        catch (OperationCanceledException) { Trace($"CandidatesCancelled generation={request.Generation}"); }
        catch (Exception ex) { if (Active(request.Generation)) _status.Text = string.Format(ProductStrings.Get("Puzzle.SourceFailed"), ex.Message); }
    }

    private async Task LoadSelectionAsync()
    {
        var request = BeginRequest();
        _panPoint = null;
        if (_candidates.SelectedItem is not string path) return;
        Trace($"PreviewRequested generation={request.Generation} image={path}");
        try
        {
            var dpi = (_owner.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1;
            var result = await _images.LoadPuzzleFrameAsync(path, (int)Math.Ceiling(CanvasSize.Width * dpi), (int)Math.Ceiling(CanvasSize.Height * dpi), request.Token);
            if (!Active(request.Generation)) { Trace($"PreviewRejected generation={request.Generation} image={path}"); return; }
            if (result.Frame is { } frame)
            {
                _frame = frame;
                Trace($"PreviewApplied generation={request.Generation} image={frame.CanonicalPath} sourcePixels={frame.SourcePixels.Width}x{frame.SourcePixels.Height}");
                var unchanged = string.Equals(_original.Carousel?.CurrentImageId, frame.CanonicalPath, StringComparison.OrdinalIgnoreCase);
                _draft = _draft with { Carousel = new(frame.CanonicalPath, unchanged ? _original.Carousel?.LastSwitchUtc : null) };
                _status.Text = string.Format(ProductStrings.Get("Puzzle.ImageSize"), _candidates.Items.Count, frame.SourcePixels.Width, frame.SourcePixels.Height);
                RefreshPreview();
            }
            else _status.Text = (result.Diagnostic ?? ProductStrings.Get("Puzzle.UnavailableImage")) + (_frame is null ? string.Empty : ProductStrings.Get("Puzzle.KeepPreview"));
        }
        catch (OperationCanceledException) { Trace($"PreviewCancelled generation={request.Generation} image={path}"); }
        catch (Exception ex) { if (Active(request.Generation)) _status.Text = string.Format(ProductStrings.Get("Puzzle.LoadFailed"), ex.Message); }
    }

    private DipSize CanvasSize => new(_metrics.WidthOf(_draft.LocalBounds.Width), _metrics.HeightOf(_draft.LocalBounds.Height));
    private DipPoint Center => new(CanvasSize.Width / 2, CanvasSize.Height / 2);
    private PuzzleImageTransform? CurrentTransform() => _frame is null ? null : _transforms.GetValueOrDefault(_frame.CanonicalPath)
        ?? new(_frame.CanonicalPath, FitMode.CoverFill, SharedCanvasTransform.DefaultTransform(_frame.SourcePixels, CanvasSize, FitMode.CoverFill));
    private void SetTransform(PuzzleImageTransform value) { _transforms[value.ImageId] = value; MarkDirty(); RefreshPreview(); }
    private void Reset(FitMode fit)
    {
        if (_frame is not null) SetTransform(new(_frame.CanonicalPath, fit, SharedCanvasTransform.Reset(_frame.SourcePixels, CanvasSize, fit)));
    }
    private void Zoom(double factor, DipPoint anchor)
    {
        if (_frame is not null && CurrentTransform() is { } current)
            SetTransform(current with { Transform = SharedCanvasTransform.ZoomAt(current.Transform, factor, anchor, SharedCanvasTransform.CoverFillScale(_frame.SourcePixels, CanvasSize)) });
    }
    private void Pan(double dx, double dy)
    {
        if (CurrentTransform() is { } current) SetTransform(current with { Transform = SharedCanvasTransform.Translate(current.Transform, dx, dy) });
    }
    private void OnPanPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_preview);
        if (_frame is null || !point.Properties.IsLeftButtonPressed) return;
        if (!_preview.CapturePointer(e.Pointer)) return;
        _panPoint = point.Position; e.Handled = true;
    }
    private void OnPanMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_panPoint is not { } previous) return;
        var point = e.GetCurrentPoint(_preview).Position;
        _panPoint = point; Pan(point.X - previous.X, point.Y - previous.Y); e.Handled = true;
    }

    private void RefreshPreview()
    {
        _draft = _draft with { Visual = _draft.Visual with { Title = _title.Text, ShowTitle = _showTitle.IsChecked == true },
            Images = _draft.Images with { Transforms = _transforms.Values.ToArray() } };
        _summary.Text = string.Format(ProductStrings.Get("Puzzle.LayoutSummary"), _draft.LocalBounds.Width, _draft.LocalBounds.Height, _draft.Partitions.Count);
        _preview.SetScene(_draft, _frame, _metrics, (_owner.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1);
    }
    private void OpenLayout()
    {
        CancelRequest(); _panPoint = null;
        _allowDiscard = false; _discardConfirmed = false;
        _layout = new(_draft, _frame, _page.Width, _page.MaxHeight); _dialog.Content = _layout.Content;
        _dialog.Title = ProductStrings.Get("Puzzle.LayoutTitle"); _dialog.PrimaryButtonText = ProductStrings.Get("Puzzle.AcceptLayout"); _dialog.CloseButtonText = ProductStrings.Get("Puzzle.CancelLayout");
    }
    private void CloseLayout(bool accept)
    {
        if (_layout is null) return;
        _layout.FinishStroke();
        if (accept)
        {
            var layout = _layout.Current;
            if (layout.Size != _draft.LocalBounds.Size || !layout.Partitions.SequenceEqual(_draft.Partitions)) MarkDirty();
            _draft = _draft with { LocalBounds = _draft.LocalBounds with { Width = layout.Size.Columns, Height = layout.Size.Rows }, Partitions = layout.Partitions.ToArray() };
        }
        _layout = null; _dialog.Content = _page; _dialog.Title = ProductStrings.Get("Puzzle.Edit");
        _dialog.PrimaryButtonText = ProductStrings.Get("Common.Save"); _dialog.CloseButtonText = ProductStrings.Get("Common.Cancel"); RefreshPreview();
        if (_candidates.SelectedItem is string selected)
        {
            if (accept || !string.Equals(selected, _frame?.CanonicalPath, StringComparison.OrdinalIgnoreCase))
                _ = LoadSelectionAsync();
        }
        else if (_sourceKind.SelectedIndex != 0) _ = ReloadSourceAsync();
    }

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_exit.IsActive) { args.Cancel = true; _exit.Resolve(ExitDecision.SaveAndExit); return; }
        if (_layout is not null) { args.Cancel = true; CloseLayout(true); return; }
        args.Cancel = TrySaveNow() is not null;
    }

    public string? TrySaveNow()
    {
        var before = _draft;
        if (_layout is not null)
        {
            _layout.FinishStroke();
            var child = _layout.Current;
            _draft = _draft with { LocalBounds = _draft.LocalBounds with { Width = child.Size.Columns, Height = child.Size.Rows }, Partitions = child.Partitions.ToArray() };
        }
        var (submission, error) = BuildSubmission();
        if (submission is not null)
        {
            try { error = _save(submission); }
            catch (Exception ex) { error = string.Format(ProductStrings.Get("Common.SaveFailed"), ex.Message); }
        }
        else error ??= ProductStrings.Get("Common.CheckInput");
        if (error is not null)
        {
            _draft = before; RefreshPreview(); _error.Text = error; _layout?.ShowError(error); return error;
        }
        _saved = true; _dirty = false; return null;
    }

    private (PuzzleEditSubmission? Submission, string? Error) BuildSubmission()
    {
        RefreshPreview();
        if (_draft.LocalBounds.Width is < 2 or > 8 || _draft.LocalBounds.Height is < 2 or > 25)
            return (null, ProductStrings.Get("Puzzle.InvalidSize"));
        if (!new PartitionLayout(_draft.LocalBounds.Size, _draft.Partitions).Check().IsValid) return (null, ProductStrings.Get("Puzzle.InvalidPartition"));
        var source = ReadSource();
        if (source.Kind is PuzzleImageSourceKind.Single or PuzzleImageSourceKind.Multiple && source.ImagePaths.Count == 0 ||
            source.Kind == PuzzleImageSourceKind.Folder && string.IsNullOrWhiteSpace(source.FolderPath)) return (null, ProductStrings.Get("Puzzle.SourceRequired"));
        try
        {
            var normalized = new Dictionary<string, PuzzleImageTransform>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in source.Transforms)
            {
                if (!double.IsFinite(value.Transform.Scale) || value.Transform.Scale <= 0 ||
                    !double.IsFinite(value.Transform.OffsetX) || !double.IsFinite(value.Transform.OffsetY))
                    return (null, ProductStrings.Get("Puzzle.InvalidTransform"));
                var id = CanonicalImagePath(value.ImageId);
                if (!normalized.TryAdd(id, value with { ImageId = id })) return (null, ProductStrings.Get("Puzzle.DuplicateTransform"));
            }
            source = source with { ImagePaths = source.ImagePaths.Select(CanonicalImagePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                FolderPath = source.FolderPath is null ? null : CanonicalImagePath(source.FolderPath), Transforms = normalized.Values.ToArray() };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return (null, string.Format(ProductStrings.Get("Puzzle.PathInvalid"), ex.Message)); }
        var target = _target.Text.Trim(); var mode = _entryKind.SelectedIndex;
        var unchanged = _original.Entry is not null && mode == (_original.Entry.Kind == ManagedEntryKind.Lnk ? 1 : 3) && target == (_originalTarget ?? string.Empty);
        if (mode != 0 && target.Length == 0 && !(_opaqueEntry && unchanged)) return (null, ProductStrings.Get("Puzzle.EmptyTarget"));
        if (mode is 1 or 2 && !unchanged && !Path.IsPathFullyQualified(target)) return (null, ProductStrings.Get("Entry.FullPathRequired"));
        if (mode == 2 && !new[] { ".lnk", ".url" }.Contains(Path.GetExtension(target), StringComparer.OrdinalIgnoreCase)) return (null, ProductStrings.Get("Entry.ShortcutOnly"));
        if (mode == 3 && !unchanged && UrlShortcut.ValidateUrl(target).Count != 0) return (null, ProductStrings.Get("Entry.InvalidUrl"));
        if (mode != 0 && !string.IsNullOrEmpty(_title.Text) && EntryNames.Validate(_title.Text).Count != 0) return (null, ProductStrings.Get("Puzzle.InvalidTitle"));
        var changes = new List<EntryChange>();
        if (mode == 0) { if (_original.Entry is not null) changes.Add(new EntryChange.Remove(_draft.Id)); }
        else if (_original.Entry is null || mode == 2 || mode == 3 && _original.Entry.Kind != ManagedEntryKind.Url || mode == 1 && _original.Entry.Kind != ManagedEntryKind.Lnk)
            changes.Add(mode switch { 2 => new EntryChange.CopyShortcut(_draft.Id, target), 3 => new EntryChange.CreateUrl(_draft.Id, target), _ => new EntryChange.CreateLink(_draft.Id, target) });
        else if (target != (_originalTarget ?? string.Empty))
        {
            if (_idList) return (null, ProductStrings.Get("Entry.IdListTarget"));
            changes.Add(new EntryChange.ChangeTarget(_draft.Id, target));
        }
        if (_original.Entry is not null && mode != 0 && mode != 2 && !changes.Any(c => c is EntryChange.CreateLink or EntryChange.CreateUrl)
            && _title.Text != _original.Visual.Title && !string.IsNullOrEmpty(_title.Text)) changes.Add(new EntryChange.Rename(_draft.Id));
        var carousel = source.Kind == PuzzleImageSourceKind.None ? null : _draft.Carousel;
        return (new(_draft with { Images = source, Carousel = carousel, Entry = mode == 0 ? null : _original.Entry }, changes), null);
    }

    private async Task PickTargetAsync()
    {
        try
        {
            var picker = new FileOpenPicker(); InitializePicker(picker); picker.FileTypeFilter.Add("*");
            var file = await picker.PickSingleFileAsync(); if (file is not null) _target.Text = file.Path;
        }
        catch (Exception ex) { _error.Text = string.Format(ProductStrings.Get("Entry.PickerFailed"), ex.Message); }
    }
    private async Task PickSourceAsync()
    {
        try
        {
            if (_sourceKind.SelectedIndex == 3)
            {
                var picker = new FolderPicker(); InitializePicker(picker); picker.FileTypeFilter.Add("*");
                var folder = await picker.PickSingleFolderAsync(); if (folder is not null) _paths.Text = folder.Path;
            }
            else
            {
                var picker = new FileOpenPicker(); InitializePicker(picker);
                foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp" }) picker.FileTypeFilter.Add(ext);
                if (_sourceKind.SelectedIndex == 2)
                {
                    var files = await picker.PickMultipleFilesAsync(); if (files.Count > 0) _paths.Text = string.Join(Environment.NewLine, files.Select(f => f.Path));
                }
                else { var file = await picker.PickSingleFileAsync(); if (file is not null) _paths.Text = file.Path; }
            }
        }
        catch (Exception ex) { _error.Text = string.Format(ProductStrings.Get("Puzzle.PickerFailed"), ex.Message); }
    }
    private void InitializePicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));
    private static string CanonicalImagePath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException(ProductStrings.Get("Puzzle.FullPathRequired"));
        return Path.GetFullPath(path);
    }
    private void Trace(string message)
    {
        if (!_trace) return;
        AppLog.Warn("Phase3Editor", message); AppLog.Flush();
    }
    private static void Id(DependencyObject item, string id) => AutomationProperties.SetAutomationId(item, "puzzle-" + id);
    private static TextBox Text(string header, string id, string value)
    {
        var box = new TextBox { Header = header, Text = value, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
        Id(box, id); return box;
    }
    private static ComboBox Choice(string header, string id, string[] values, int selected)
    {
        var box = new ComboBox { Header = header, MinWidth = 220 }; foreach (var value in values) box.Items.Add(value);
        box.SelectedIndex = selected; Id(box, id); return box;
    }
    private static Button Action(string title, string id, Action action)
    {
        var button = new Button { Content = title }; Id(button, id); button.Click += (_, _) => action(); return button;
    }
}

