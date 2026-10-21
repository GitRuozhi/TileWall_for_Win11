using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using TileWall.Core.Shell;
using TileWall.Core.Storage;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;
using TileWall.Shell.Interop;

namespace TileWall.Views;

/// <summary>One property/layout draft and one save transaction for ordinary tiles and puzzles.</summary>
internal sealed class SharedMemberEditorDialog : IWallDialogSession
{
    private readonly Window _owner;
    private readonly WallMember _original;
    private WallMember _draft;
    private readonly WallImageService _images;
    private readonly string? _managedEntryPath, _originalInput;
    private readonly ILnkFileService _links;
    private readonly Func<WallMember, IReadOnlyList<EntryChange>, string?> _save;
    private readonly int _maxRows;
    private readonly WallVisualStyle _visualStyle;
    private readonly bool _traceImages = Environment.GetEnvironmentVariable("TILEWALL_TRACE_INPUT") == "1" ||
        Environment.GetEnvironmentVariable("TILEWALL_PERF") == "1" ||
        Environment.GetCommandLineArgs().Contains("--test-root", StringComparer.OrdinalIgnoreCase) ||
        Environment.GetCommandLineArgs().Contains("--trace-input", StringComparer.OrdinalIgnoreCase);
    private readonly ContentDialog _dialog;
    private readonly DialogExitPrompt _exit;
    private readonly MemberEditorPreview _preview = new();
    private readonly TextBox _title = Input(), _target = Input(), _sourcePath = Input();
    private readonly TextBox _imageRatio = Input();
    private readonly TextBlock _galleryCount = new() { Text = "0/0", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap }, _sourceStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _toolIntro = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 },
        _toolHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 },
        _scaleHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 },
        _operationText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
    private readonly Border _operationToast;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _operationTimer;
    private readonly IconSelector _source = new(ActionIcon.Reset, ActionIcon.Picture, ActionIcon.Folder),
        _background = new(ActionIcon.Reset, ActionIcon.Color, ActionIcon.BackgroundBlur, ActionIcon.Cancel);
    private readonly ComboBox _interval = new();
    private readonly IntegerStepper _width = new() { Minimum = 1, Maximum = 8 }, _height = new() { Minimum = 1, Maximum = 256 };
    private readonly NumberBox _transparencyInput = Number(0, 100);
    private readonly Slider _transparency = new() { Minimum = 0, Maximum = 100, StepFrequency = 1, Width = 140, MinWidth = 0 };
    private readonly Button _autoTitle = new(), _targetBrowse = new(), _targetConfirm = new(), _titleConfirm = new(), _sourceBrowse = new(), _colorButton = new(), _undo = new(), _redo = new();
    private readonly ColorPicker _color = new() { IsAlphaEnabled = false, IsMoreButtonVisible = false, Width = 240 };
    private readonly ToggleButton _draw = new(), _erase = new();
    private readonly List<(ToggleButton Button, string Key, string Glyph)> _actions = [];
    private readonly List<(ToggleButton Button, string Key, int Width, int Height)> _puzzlePresetActions = [];
    private readonly List<(ToggleButton Button, string Key, ImageDisplayMode Mode)> _imageActions = [];
    private readonly Dictionary<string, TextBlock> _labels = [];
    private readonly ObservableCollection<Thumbnail> _candidates = [];
    private readonly StackPanel _thumbnailStrip = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly ScrollViewer _thumbnails = new() { Height = 68, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollMode = ScrollMode.Disabled };
    private Thumbnail? _selectedThumbnail;
    private readonly ImageRotationInterval[] _intervals = Enum.GetValues<ImageRotationInterval>();
    private readonly PartitionEditSession? _layout;
    private PuzzleFrame? _frame;
    private string? _previewPath;
    private CancellationTokenSource? _request;
    private CancellationTokenSource? _thumbnailRequest;
    private bool _thumbnailCandidatesReady, _thumbnailPreviewPending = true, _enumeratingCandidates;
    private int _thumbnailEdge;
    private bool _thumbnailNeedsBlur;
    private long _generation;
    private long _candidateEnumerationGeneration = -1;
    private XamlRoot? _previewRoot;
    private double _previewRasterScale = 1;
    private bool _previewClosing;
    private readonly HashSet<Task> _pendingOperations = [];
    private Task _latestPending = Task.CompletedTask;
    private Task _pickerPending = Task.CompletedTask;
    private Task _pending
    {
        get => _latestPending;
        set { _latestPending = value; TrackOperation(value); }
    }
    private bool _refreshing, _saved, _closed, _opened, _allowDiscard, _discardConfirmed, _pickerBusy;
    private bool _titleDirty, _targetDirty, _sourceDirty, _ratioDirty;
    private string? _galleryVersion;
    private MemberImageSource? _galleryVersionSource;
    private (MemberImageSource Source, PuzzleCandidateResult Snapshot, long Revision)? _deferredGallerySnapshot;
    private readonly List<(ToggleButton Button, int Width, int Height, bool Puzzle)> _presets = [];
    private long _inputRevision, _targetRevision, _titleRevision, _sourceRevision;
    private readonly Grid _page;
    internal MemberImageSource? GallerySource => _opened && !_closed && !_previewClosing && !_sourceDirty &&
        _draft.Images.Kind == MemberImageSourceKind.Folder ? _draft.Images : null;
    internal event Action? GallerySourceChanged;
    internal long GallerySourceRevision => _sourceRevision;

    internal SharedMemberEditorDialog(Window owner, WallMember original, WallImageService images,
        string? managedEntryPath, ILnkFileService links, int maxRows,
        Func<WallMember, IReadOnlyList<EntryChange>, string?> save, WallVisualStyle visualStyle = WallVisualStyle.Win10)
    {
        if (original is not (TileMember or PuzzleMember)) throw new ArgumentException("A tile or puzzle is required.", nameof(original));
        _owner = owner; _original = original; _draft = original; _images = images;
        _managedEntryPath = managedEntryPath; _links = links; _save = save;
        _visualStyle = visualStyle;
        _maxRows = Math.Clamp(maxRows, 0, WallViewportPolicy.MaximumMemberRows);
        _originalInput = original.TargetInput ?? ReadManagedTarget();
        _draft = original with { TargetInput = _originalInput };
        if (original is PuzzleMember puzzle) _layout = new(puzzle.LocalBounds.Size, puzzle.Partitions);
        if (original is PuzzleMember)
        {
            _width.Minimum = MemberSizeRules.PuzzleMinimumColumns;
            _height.Minimum = MemberSizeRules.PuzzleMinimumRows;
        }
        else _height.Minimum = _width.Minimum = 1;
        _height.Maximum = Math.Min(_maxRows, original is PuzzleMember ? MemberSizeRules.PuzzleMaximumRows : MemberSizeRules.TileMaximumRows);
        _width.Maximum = original is PuzzleMember ? MemberSizeRules.PuzzleMaximumColumns : MemberSizeRules.TileMaximumColumns;
        _transparencyInput.Width = 56; _transparencyInput.Resources["TextControlThemeMinWidth"] = 0d;
        _transparencyInput.Resources["NumberBoxMinWidth"] = 0d;
        _sourceStatus.Visibility = Visibility.Collapsed;
        _sourceStatus.FontSize = 12;
        _sourceStatus.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
            _sourceStatus.Visibility = string.IsNullOrEmpty(_sourceStatus.Text) ? Visibility.Collapsed : Visibility.Visible);
        _operationToast = VisualStyles.Card(_operationText);
        _operationToast.IsHitTestVisible = false; _operationToast.Visibility = Visibility.Collapsed;
        _operationToast.HorizontalAlignment = HorizontalAlignment.Center; _operationToast.VerticalAlignment = VerticalAlignment.Center;
        _operationTimer = owner.DispatcherQueue.CreateTimer(); _operationTimer.IsRepeating = false;
        _operationTimer.Interval = TimeSpan.FromSeconds(2);
        _operationTimer.Tick += (_, _) => _operationToast.Visibility = Visibility.Collapsed;
        _autoTitle.Click += (_, _) => { _titleDirty = false; _draft = _draft with { Visual = _draft.Visual with { TitleMode = MemberTitleMode.Automatic, Title = null } }; Changed(); RefreshFields(); };
        _targetConfirm.Click += (_, _) => ConfirmTarget(); _titleConfirm.Click += (_, _) => ConfirmTitle();
        var titleValue = FieldRow(_autoTitle, _title, _titleConfirm);
        _targetBrowse.Click += (_, _) => _pending = PickTargetAsync();
        var targetValue = FieldRow(_targetBrowse, _target, _targetConfirm);
        var sourceValue = new Grid { ColumnSpacing = 8 };
        sourceValue.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sourceValue.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sourceValue.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sourceValue.Children.Add(_source); Grid.SetColumn(_sourcePath, 1); sourceValue.Children.Add(_sourcePath);
        Grid.SetColumn(_sourceBrowse, 2); sourceValue.Children.Add(_sourceBrowse); _sourcePath.VerticalAlignment = VerticalAlignment.Center;
        _sourceBrowse.Click += (_, _) => _pending = PickSourceAsync(_draft.Images.Kind);
        var colorFlyout = new Flyout { Content = _color }; _colorButton.Flyout = colorFlyout;
        var backgroundValue = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; backgroundValue.Children.Add(_background); backgroundValue.Children.Add(_colorButton);
        var transparency = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        transparency.Children.Add(_transparencyInput); transparency.Children.Add(new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center }); transparency.Children.Add(_transparency);
        var right = new Grid { RowSpacing = 8, ColumnSpacing = 12 };
        right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddRow(right, "Editor.Target", targetValue); AddRow(right, "Editor.Title", titleValue);
        AddRow(right, "Editor.Foreground", sourceValue); AddRow(right, "Tile.Background", backgroundValue);
        AddRow(right, "Editor.Opacity", transparency); AddRow(right, "Editor.Interval", _interval);

        var tools = Form();
        var shapeActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (_layout is not null)
        {
            _undo.Click += (_, _) => { _preview.FinishGesture(notifyCompleted: false); if (_layout.Undo()) LayoutChanged(); };
            _redo.Click += (_, _) => { _preview.FinishGesture(notifyCompleted: false); if (_layout.Redo()) LayoutChanged(); };
            shapeActions.Children.Add(_undo); shapeActions.Children.Add(_redo); shapeActions.Children.Add(_draw); shapeActions.Children.Add(_erase);
            _draw.Checked += (_, _) => { _preview.FinishGesture(notifyCompleted: false); _erase.IsChecked = false; _preview.Tool = EditTool.Draw; RefreshToolHint(); };
            _erase.Checked += (_, _) => { _preview.FinishGesture(notifyCompleted: false); _draw.IsChecked = false; _preview.Tool = EditTool.Erase; RefreshToolHint(); };
            _draw.Unchecked += (_, _) => { if (_erase.IsChecked != true) _preview.Tool = null; RefreshToolHint(); };
            _erase.Unchecked += (_, _) => { if (_draw.IsChecked != true) _preview.Tool = null; RefreshToolHint(); };
            _preview.StrokeStarted += _layout.BeginStroke;
            _preview.PreviewStroke = _layout.Preview;
            _preview.StrokeEdge += (edge, tool) => { if (_layout.Stroke(edge, tool) == StrokeOutcome.Applied) LayoutChanged(); };
            _preview.StrokeCompleted += () => { _layout.EndStroke(); RefreshFields(); };
            _preview.StrokeCanceled += () => { if (_layout.CancelStroke()) LayoutChanged(); };
            _preview.StrokeRejected += outcome => { if (outcome == StrokeOutcome.SizeLimit) ShowOperationReminder(T("Layout.PartitionSizeLimit")); };
            _preview.DescribeStroke = (tool, proposal) => string.Format(T("Layout.PointerActions"),
                T(tool == EditTool.Draw ? "Layout.Draw" : "Layout.Erase"), T(tool == EditTool.Draw ? "Layout.Erase" : "Layout.Draw"))
                + (proposal.Outcome == StrokeOutcome.SizeLimit ? "\n" + T("Layout.PartitionSizeLimit") : string.Empty);
        }
        _draw.IsEnabled = _erase.IsEnabled = _layout is not null;

        if (_layout is null) { shapeActions.Children.Add(_undo); shapeActions.Children.Add(_redo); shapeActions.Children.Add(_draw); shapeActions.Children.Add(_erase); }
        var toolContent = new StackPanel { Spacing = 4 }; toolContent.Children.Add(shapeActions);
        toolContent.Children.Add(_toolIntro); toolContent.Children.Add(_toolHint);
        AddRow(tools, "Editor.PuzzleTools", toolContent);
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (key, width, height, glyph) in new[] { ("Editor.PresetSmall", 1, 1, ActionIcon.PresetSmall), ("Editor.PresetMedium", 2, 2, ActionIcon.PresetMedium), ("Editor.PresetWide", 4, 2, ActionIcon.PresetWide), ("Editor.PresetLarge", 4, 4, ActionIcon.PresetLarge) })
        { var button = Preset(key, glyph, () => SetSize(width, height)); _presets.Add((button, width, height, false)); presets.Children.Add(button); }
        AddRow(tools, "Editor.TileSize", presets);
        var puzzlePresets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (key, width, height) in new[] { ("Editor.PuzzleDefault", 4, 4),
            ("Editor.PuzzleWide", 6, 4), ("Editor.PuzzleTall", 4, 6) })
        {
            var button = PuzzlePreset(key, width, height, () => SetPuzzlePreset(width, height));
            _presets.Add((button, width, height, true)); puzzlePresets.Children.Add(button);
        }
        AddRow(tools, "Editor.PuzzlePreset", puzzlePresets);
        _width.HorizontalAlignment = _height.HorizontalAlignment = HorizontalAlignment.Left;
        AddRow(tools, "Editor.Width", _width); AddRow(tools, "Editor.Height", _height);
        var imageActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (mode, key, glyph) in new[] { (ImageDisplayMode.Fill, "Puzzle.Fill", ActionIcon.Fill), (ImageDisplayMode.Fit, "Puzzle.Fit", ActionIcon.Fit),
            (ImageDisplayMode.Original, "Editor.Original", ActionIcon.Original), (ImageDisplayMode.Custom, "Editor.Custom", ActionIcon.Custom) })
        {
            var button = new ToggleButton(); ActionIcon.SetIcon(button, T(key), glyph);
            _imageActions.Add((button, key, mode)); button.Click += (_, _) => SetDisplayMode(mode); imageActions.Children.Add(button);
        }
        AddRow(tools, "Editor.ImageFit", imageActions);
        _imageRatio.Width = IntegerStepper.ControlWidth; _imageRatio.Height = 32; _imageRatio.MinHeight = 0;
        _imageRatio.Padding = new Thickness(4); _imageRatio.CornerRadius = new CornerRadius(0);
        _imageRatio.TextAlignment = TextAlignment.Center;
        var ratioRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; ratioRow.Children.Add(_imageRatio);
        ratioRow.Children.Add(new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center });
        var scaleContent = new StackPanel { Spacing = 4 }; scaleContent.Children.Add(ratioRow); scaleContent.Children.Add(_scaleHint);
        AddRow(tools, "Editor.ImageRatio", scaleContent);
        _thumbnails.Content = _thumbnailStrip;
        _thumbnails.SizeChanged += (_, _) => { _thumbnailStrip.Padding = new Thickness(Math.Max(0, (_thumbnails.ActualWidth - 48) / 2), 0, Math.Max(0, (_thumbnails.ActualWidth - 48) / 2), 0); CenterThumbnail(false); };
        var gallery = new StackPanel { Spacing = 4 }; gallery.Children.Add(_thumbnails); gallery.Children.Add(_galleryCount);
        var cards = new StackPanel { Spacing = 12 }; cards.Children.Add(VisualStyles.Card(right)); cards.Children.Add(VisualStyles.Card(tools));
        cards.Children.Add(VisualStyles.Card(gallery)); cards.Children.Add(_sourceStatus); cards.Children.Add(_error);
        var root = (owner.Content as FrameworkElement)?.XamlRoot;
        var budget = OwnedDialogWindow.ContentBudget(owner);
        var page = _page = new Grid { Width = Math.Min(900, budget.Width), MinWidth = Math.Min(680, budget.Width),
            MaxHeight = budget.Height, ColumnSpacing = 24, RowSpacing = 8 };
        // Retain a usable preview beside the fixed 420 DIP tools when the user narrows
        // the owned window. The host adds the stock card's padding to this minimum.
        page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = Math.Min(240, Math.Max(0, page.MinWidth - 444)) });
        page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        // Keep the column's finite width when measuring the gallery. Only the
        // thumbnail list scrolls horizontally; preview overflow is clipped in place.
        var leftScroll = new ScrollViewer { Content = _preview, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
        var rightScroll = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        page.Children.Add(leftScroll); Grid.SetColumn(rightScroll, 1); page.Children.Add(rightScroll);
        Grid.SetColumnSpan(_operationToast, 2); page.Children.Add(_operationToast); Canvas.SetZIndex(_operationToast, 1);
        page.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        { if (e.Key == Windows.System.VirtualKey.Escape && _preview.IsGestureActive) { _preview.CancelGesture(); e.Handled = true; } }), true);
        _dialog = new ContentDialog { XamlRoot = root, Content = page, DefaultButton = ContentDialogButton.None };
        OwnedDialogWindow.EnableUserResize(_dialog);
        _dialog.Resources["ContentDialogMaxWidth"] = budget.Width + 48;
        // WinUI caps the stock card at 756 DIP. Permit this editor's 1:1 preview
        // and tools to grow within the work area; OwnedDialogWindow fits its desired size.
        _dialog.Resources["ContentDialogMaxHeight"] = budget.Height + 180;
        _exit = new(_dialog);
        Id(_dialog, "dialog"); Id(_title, "title"); Id(_target, "target"); Id(_width, "width"); Id(_height, "height"); Id(_source, "source");
        Id(_background, "background"); Id(_transparencyInput, "transparency-input"); Id(_transparency, "transparency"); Id(_interval, "interval"); Id(_preview, "preview"); Id(_thumbnails, "thumbnails");
        Id(_autoTitle, "auto-title"); Id(_targetBrowse, "target-browse"); Id(_sourceBrowse, "source-browse"); Id(_undo, "undo"); Id(_redo, "redo"); Id(_draw, "draw"); Id(_erase, "erase");
        Id(_sourcePath, "source-path"); Id(_imageRatio, "image-ratio"); Id(_galleryCount, "gallery-count");
        Id(_targetConfirm, "target-confirm"); Id(_titleConfirm, "title-confirm");
        // TextChanged is asynchronous: a programmatic write can notify after
        // _refreshing has ended. Capture only draft state in synchronous
        // TextChanging; defer visual/subscription work to TextChanged.
        _sourcePath.TextChanging += (_, _) =>
        {
            if (_refreshing) return;
            _sourceDirty = true; _sourceRevision = ++_inputRevision;
            TraceImage("SourceInputChanged");
        };
        _sourcePath.TextChanged += (_, _) => { if (_sourceDirty) { Changed(); GallerySourceChanged?.Invoke(); } };
        _sourcePath.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { _pending = ConfirmSourceAsync(); e.Handled = true; } };
        _imageRatio.TextChanging += (_, _) => { if (!_refreshing) _ratioDirty = true; };
        _imageRatio.TextChanged += (_, _) => { if (_ratioDirty) Changed(); };
        _imageRatio.LostFocus += (_, _) => CommitRatioInput();
        _imageRatio.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { CommitRatioInput(); e.Handled = true; } };
        _title.TextChanging += (_, _) => { if (!_refreshing) { _titleDirty = true; _titleRevision = ++_inputRevision; } };
        _title.TextChanged += (_, _) => { if (_titleDirty) Changed(); };
        _target.TextChanging += (_, _) => { if (!_refreshing) { _targetDirty = true; _targetRevision = ++_inputRevision; } };
        _target.TextChanged += (_, _) => { if (_targetDirty) Changed(); };
        _title.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { ConfirmTitle(); e.Handled = true; } };
        _target.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { ConfirmTarget(); e.Handled = true; } };
        _width.ValueChanged += value => { if (!_refreshing) SetSize(value, _draft.LocalBounds.Height); };
        _height.ValueChanged += value => { if (!_refreshing) SetSize(_draft.LocalBounds.Width, value); };
        _source.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            var kind = (MemberImageSourceKind)_source.SelectedIndex; _sourceRevision = ++_inputRevision; CancelRequest();
            TraceImage($"SelectSource kind={kind}");
            CancelThumbnailRequests();
            if (kind == MemberImageSourceKind.Automatic) { _pending = PickSourceAsync(kind); return; }
            _draft = _draft with { Images = new MemberImageSource { Kind = kind, Path = string.Empty }, Carousel = null,
                Visual = _draft.Visual with { BackgroundMode = MemberBackgroundMode.Blur } };
            _sourceDirty = true; _frame = null; _previewPath = null;
            _candidates.Clear(); _thumbnailStrip.Children.Clear(); _selectedThumbnail = null;
            _sourceStatus.Text = string.Empty; RefreshThumbnailSelection(center: false);
            _thumbnails.ChangeView(0, null, null, true);
            _refreshing = true; _sourcePath.Text = string.Empty; _refreshing = false;
            Changed(); RefreshFields(); GallerySourceChanged?.Invoke();
            _pending = PickSourceAsync(kind);
        };
        _background.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            var neededBlur = _draft.Visual.BackgroundMode == MemberBackgroundMode.Blur;
            _draft = _draft with { Visual = _draft.Visual with { BackgroundMode = (MemberBackgroundMode)_background.SelectedIndex,
                BackgroundColor = _background.SelectedIndex == (int)MemberBackgroundMode.Color ? ColorText(_color.Color) : _draft.Visual.BackgroundColor } };
            var needsBlur = _draft.Visual.BackgroundMode == MemberBackgroundMode.Blur;
            Changed(); RefreshFields();
            // The first folder enumeration owns this generation. Cancelling it
            // drops the only candidate fill. The draft already has the new demand;
            // that enumeration loads the preview and thumbnails when it returns.
            if (neededBlur != needsBlur && !_enumeratingCandidates)
            {
                CancelRequest();
                _pending = LoadPreviewAsync();
                RestartThumbnailBatch();
            }
            if (_draft.Visual.BackgroundMode == MemberBackgroundMode.Color) colorFlyout.ShowAt(_colorButton);
        };
        _color.ColorChanged += (_, e) => { if (!_refreshing) { _draft = _draft with { Visual = _draft.Visual with { BackgroundColor = ColorText(e.NewColor) } }; Changed(); RefreshPreview(); } };
        _transparencyInput.ValueChanged += (_, e) => { if (!_refreshing && !double.IsNaN(e.NewValue)) SetTransparency(e.NewValue); };
        _transparency.ValueChanged += (_, e) => { if (!_refreshing) SetTransparency(e.NewValue); };
        _interval.SelectionChanged += (_, _) => { if (!_refreshing && _interval.SelectedIndex >= 0) { _draft = _draft with { RotationInterval = _intervals[_interval.SelectedIndex] }; Changed(); } };
        _preview.DisplayChanged += display => { _ratioDirty = false; _draft = _draft with { ImageDisplay = display }; Changed(); RefreshPreview(); EnsurePreviewQuality(); };
        _preview.GestureCompleted += EnsurePreviewQuality;
        _dialog.PrimaryButtonClick += async (_, e) =>
        {
            if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.SaveAndExit); return; }
            var deferral = e.GetDeferral();
            try
            {
                await WaitForPendingAsync();
                e.Cancel = _closed || _previewClosing || TrySaveNow() is not null;
            }
            catch (Exception ex)
            {
                e.Cancel = true;
                if (!_closed && !_previewClosing) ShowError(string.Format(T("Common.SaveFailed"), ex.Message));
            }
            finally { deferral.Complete(); }
        };
        _dialog.SecondaryButtonClick += (_, e) => { if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.DiscardAndExit); } };
        _dialog.CloseButtonClick += (_, e) => { if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.CancelExit); } };
        _dialog.Closing += (_, e) =>
        {
            // Settle NumberBox explicitly and stepper/ratio on blur before testing
            // the draft or marking the preview closed. These edits belong here.
            OwnedDialogWindow.ReleaseTextFocus(_dialog);
            _previewClosing = true;
            try
            {
                _preview.FinishGesture(notifyCompleted: false);
                if (_saved || _discardConfirmed) return;
                if (_pickerBusy) { e.Cancel = true; return; }
                if (_exit.IsActive) { e.Cancel = true; _exit.Resolve(ExitDecision.CancelExit); return; }
                if (!HasUnsavedDraft) return;
                if (_allowDiscard) return;
                e.Cancel = true; _allowDiscard = true; _error.Text = T("Common.UnsavedDraft"); RefreshStrings();
            }
            finally
            {
                _previewClosing = !e.Cancel;
                if (_previewClosing)
                {
                    CancelRequest(); CancelThumbnailRequests();
                    _operationTimer.Stop(); GallerySourceChanged?.Invoke();
                }
            }
        };
        _dialog.Opened += (_, _) =>
        {
            _opened = true;
            _previewRoot = _dialog.XamlRoot;
            _previewRasterScale = PreviewRasterizationScale;
            if (_previewRoot is not null) _previewRoot.Changed += OnPreviewRootChanged;
            if (_target.IsEnabled) _target.Focus(FocusState.Programmatic); else _dialog.Focus(FocusState.Programmatic);
            _pending = ReloadCandidatesAsync(); GallerySourceChanged?.Invoke();
        };
        RefreshStrings(); RefreshFields();
    }

    public async Task<bool> ShowAsync()
    {
        try { await OwnedDialogWindow.ShowAsync(_dialog); return _saved; }
        finally
        {
            _closed = true;
            if (_previewRoot is not null) _previewRoot.Changed -= OnPreviewRootChanged;
            _previewRoot = null;
            CancelRequest(); CancelThumbnailRequests(); _operationTimer.Stop(); GallerySourceChanged?.Invoke();
            // Replaced previews, source confirmations and thumbnail batches still
            // own UI continuations. Keep this page alive until all have returned.
            try { await Task.WhenAll(_pendingOperations.Append(_pickerPending)); }
            catch (OperationCanceledException) { }
        }
    }
    private void TrackOperation(Task operation)
    {
        _pendingOperations.RemoveWhere(task => task.IsCompletedSuccessfully || task.IsCanceled);
        _pendingOperations.Add(operation);
    }
    public bool HasUnsavedDraft => _sourceDirty || _titleDirty || _targetDirty || _ratioDirty || _width.HasPendingInput || _height.HasPendingInput || MemberPresentationRules.HasUserPropertyChanges(_original, _draft);
    public async Task WaitForPendingAsync()
    {
        if (_closed || _previewClosing) return;
        // App exit can enter here without a button taking focus first. Settle the
        // inner NumberBox text and blur-based edits before checking/saving drafts.
        OwnedDialogWindow.ReleaseTextFocus(_dialog);
        // Image quality/gallery callbacks may replace _pending while a native
        // picker pumps messages. Its separate completion still owns that work.
        await _pickerPending;
        await DrainPendingAsync();
        if (_closed || _previewClosing) return;
        if (_targetDirty)
        {
            var title = _title.Text; var applyTitle = _titleDirty && _titleRevision > _targetRevision;
            if (!ConfirmTarget()) return;
            await DrainPendingAsync();
            if (_closed || _previewClosing) return;
            if (applyTitle) { _refreshing = true; _title.Text = title; _refreshing = false; _titleDirty = true; }
        }
        if (_sourceDirty) await ConfirmSourceAsync();
    }
    private async Task DrainPendingAsync()
    {
        Task pending;
        do { pending = _pending; await pending; }
        while (!_closed && !_previewClosing && !ReferenceEquals(pending, _pending));
    }
    public Task<ExitDecision> ConfirmExitAsync() { _preview.FinishGesture(notifyCompleted: false); return _exit.ConfirmAsync(); }
    public void DiscardDraft() { _discardConfirmed = true; }
    public void RequestDismiss() => _dialog.Hide();
    public void Close() { _discardConfirmed = true; _preview.FinishGesture(notifyCompleted: false); _dialog.Hide(); }

    public void RefreshStrings()
    {
        if (_exit.IsActive) return;
        _refreshing = true;
        _dialog.Title = new Border { Height = 16, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        AutomationProperties.SetName(_dialog, T(_original is PuzzleMember ? "Puzzle.Edit" : "Tile.Edit"));
        _dialog.PrimaryButtonText = T("Common.Save"); _dialog.CloseButtonText = T(_allowDiscard ? "Common.Discard" : "Common.Cancel");
        ActionIcon.DecorateDialog(_dialog);
        foreach (var (key, label) in _labels) label.Text = T(key);
        foreach (var (button, key, glyph) in _actions)
            ActionIcon.SetIcon(button, T(key), glyph);
        foreach (var (button, key, width, height) in _puzzlePresetActions)
        {
            button.Content = PuzzlePresetIcon(width / 2, height / 2);
            ActionIcon.SetTip(button, T(key)); AutomationProperties.SetName(button, T(key));
        }
        foreach (var (button, key, _) in _imageActions) { ToolTipService.SetToolTip(button, T(key)); AutomationProperties.SetName(button, T(key)); }
        ActionIcon.SetIcon(_autoTitle, T("Editor.AutoTitle"), ActionIcon.Reset); ActionIcon.SetIcon(_targetBrowse, T("Entry.Browse"), ActionIcon.OpenFile);
        ActionIcon.SetIcon(_targetConfirm, T("Editor.ConfirmTarget"), ActionIcon.Confirm); ActionIcon.SetIcon(_titleConfirm, T("Editor.ConfirmTitle"), ActionIcon.Confirm);
        ActionIcon.SetIcon(_sourceBrowse, T("Entry.Browse"), ActionIcon.OpenFile); ActionIcon.SetIcon(_colorButton, T("Tile.CustomColor"), ActionIcon.Color);
        ActionIcon.SetIcon(_undo, T("Layout.Undo"), ActionIcon.Undo); ActionIcon.SetIcon(_redo, T("Layout.Redo"), ActionIcon.Redo);
        ActionIcon.SetIcon(_draw, T("Layout.Draw"), ActionIcon.Draw); ActionIcon.SetIcon(_erase, T("Layout.Erase"), ActionIcon.Erase);
        _source.SetItems(new[] { "Editor.Initial", "Editor.Picture", "Editor.Gallery" }.Select(T)); _background.SetItems(new[] { "Editor.Initial", "Tile.CustomColor", "Editor.Blur", "Editor.None" }.Select(T));
        _width.SetNames(T("Editor.Width"), T("Layout.RemoveColumn"), T("Layout.AddColumn"));
        _height.SetNames(T("Editor.Height"), T("Layout.RemoveRow"), T("Layout.AddRow"));
        _toolIntro.Text = T("Layout.GestureHint"); _scaleHint.Text = T("Editor.ImageScaleHint");
        AutomationProperties.SetName(_imageRatio, T("Editor.ImageRatio"));
        ToolTipService.SetToolTip(_imageRatio, T("Editor.IntegerRatio"));
        _interval.Items.Clear(); foreach (var value in new[] { "5s", "15s", "30s", "1min", "5min", "15min", "30min" }) _interval.Items.Add(value);
        _refreshing = false; RefreshFields();
    }

    private void RefreshFields()
    {
        _refreshing = true;
        if (!_titleDirty) _title.Text = MemberPresentationRules.ResolveTitle(_draft, Directory.Exists(_draft.TargetInput));
        if (!_targetDirty) _target.Text = _draft.TargetInput ?? string.Empty;
        _width.Value = _draft.LocalBounds.Width; _height.Value = _draft.LocalBounds.Height;
        _source.SelectedIndex = (int)_draft.Images.Kind; if (!_sourceDirty) _sourcePath.Text = _draft.Images.Path ?? string.Empty;
        ToolTipService.SetToolTip(_sourcePath, string.IsNullOrWhiteSpace(_sourcePath.Text) ? null : _sourcePath.Text); _sourceBrowse.IsEnabled = _sourcePath.IsEnabled = _draft.Images.Kind != MemberImageSourceKind.Automatic;
        ActionIcon.SetIcon(_sourceBrowse, T("Entry.Browse"), _draft.Images.Kind == MemberImageSourceKind.Folder ? ActionIcon.Folder : ActionIcon.Picture);
        ActionIcon.SetTip(_sourceBrowse, T(_sourceBrowse.IsEnabled ? "Entry.Browse" : "Editor.AutomaticSource"));
        _background.SelectedIndex = (int)_draft.Visual.BackgroundMode; _colorButton.IsEnabled = _draft.Visual.BackgroundMode == MemberBackgroundMode.Color;
        ActionIcon.SetTip(_colorButton, T(_colorButton.IsEnabled ? "Tile.CustomColor" : "Editor.RequiresColor"));
        _color.Color = WallSurface.ParseColor(_draft.Visual.BackgroundColor, Windows.UI.Color.FromArgb(255, 64, 64, 64));
        _transparencyInput.Value = _transparency.Value = _draft.Visual.BackgroundTransparency;
        _interval.SelectedIndex = Array.IndexOf(_intervals, _draft.RotationInterval); _interval.IsEnabled = _draft.Images.Kind == MemberImageSourceKind.Folder;
        _thumbnails.IsEnabled = _draft.Images.Kind == MemberImageSourceKind.Folder;
        foreach (var (button, width, height, puzzle) in _presets)
        {
            button.IsEnabled = puzzle == (_layout is not null) && height <= _maxRows;
            button.IsChecked = puzzle == (_layout is not null) && _draft.LocalBounds.Width == width && _draft.LocalBounds.Height == height &&
                (!puzzle || IsPuzzlePreset(width, height));
            var key = puzzle ? _puzzlePresetActions.First(a => a.Button == button).Key : _actions.First(a => a.Button == button).Key;
            ActionIcon.SetTip(button, T(button.IsEnabled ? key : puzzle != (_layout is not null)
                ? puzzle ? "Editor.RequiresPuzzle" : "Editor.RequiresTile" : "Editor.HeightLimit").Replace("{0}", _maxRows.ToString(CultureInfo.CurrentCulture)));
        }
        _undo.IsEnabled = _layout?.CanUndo == true; _redo.IsEnabled = _layout?.CanRedo == true;
        ActionIcon.SetTip(_undo, T(_undo.IsEnabled ? "Layout.Undo" : "Editor.NoUndo")); ActionIcon.SetTip(_redo, T(_redo.IsEnabled ? "Layout.Redo" : "Editor.NoRedo"));
        ActionIcon.SetTip(_draw, T(_layout is null ? "Editor.RequiresPuzzle" : "Layout.Draw")); ActionIcon.SetTip(_erase, T(_layout is null ? "Editor.RequiresPuzzle" : "Layout.Erase"));
        ToolTipService.SetToolTip(_interval, T(_interval.IsEnabled ? "Editor.Interval" : "Editor.RequiresGallery"));
        ToolTipService.SetToolTip(_thumbnails, T(_thumbnails.IsEnabled ? "Editor.Gallery" : "Editor.RequiresGallery"));
        RefreshToolHint(); _refreshing = false; RefreshPreview();
    }

    private void RefreshPreview()
    {
        var budget = OwnedDialogWindow.ContentBudget(_owner); var metrics = new WallMetrics();
        var tileWidth = metrics.WidthOf(_draft.LocalBounds.Width); var tileHeight = metrics.HeightOf(_draft.LocalBounds.Height);
        // The initial viewport follows the real 1:1 object, with room for editing
        // handles. A small tile no longer forces a 640 DIP empty preview column.
        _preview.MinWidth = 0; _preview.Width = double.NaN; _preview.Height = Math.Max(400, tileHeight + 24);
        _page.MaxHeight = budget.Height;
        foreach (var (button, key, mode) in _imageActions) { button.IsChecked = _draft.ImageDisplay.Mode == mode; button.IsEnabled = _frame is not null; ActionIcon.SetTip(button, T(button.IsEnabled ? key : "Editor.NoImage")); }
        _refreshing = true; _imageRatio.IsEnabled = _frame is not null;
        if (!_ratioDirty && _frame is { } image)
        {
            var placement = MemberImageGeometry.Calculate(tileWidth, tileHeight, image.SourcePixels.Width, image.SourcePixels.Height,
                PreviewRasterizationScale, _draft.ImageDisplay);
            var percent = 100 * MemberImageGeometry.CustomScaleFromImageScale(placement.Scale, tileWidth, tileHeight, image.SourcePixels.Width, image.SourcePixels.Height);
            // Preset geometry may lie outside the editable custom interval. Still
            // display its exact fill-relative percentage; custom edits use the shared gate.
            _imageRatio.Text = percent.ToString("0.########", CultureInfo.CurrentCulture);
        }
        else if (!_ratioDirty) _imageRatio.Text = string.Empty;
        _refreshing = false;
        _preview.SetScene(_draft, _frame, PreviewRasterizationScale, _visualStyle);
    }
    private double PreviewRasterizationScale => _dialog.XamlRoot?.RasterizationScale ?? 1;

    private void OnPreviewRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_closed || _previewClosing || _previewRasterScale == sender.RasterizationScale) return;
        _previewRasterScale = sender.RasterizationScale;
        RefreshPreview();
        // An initial preview owns its first request. Its completion compares
        // requestedScale with the latest root before releasing thumbnail work.
        if (!_thumbnailPreviewPending) EnsurePreviewQuality();
        RestartThumbnailBatch();
    }

    private void EnsurePreviewQuality()
    {
        if (!_preview.IsGestureActive && _deferredGallerySnapshot is { } deferred)
        {
            _deferredGallerySnapshot = null;
            _pending = RefreshGalleryCandidatesAsync(deferred.Source, deferred.Snapshot, deferred.Revision);
            return;
        }
        if (_closed || _previewClosing || _saved || _discardConfirmed || _sourceDirty || _targetDirty ||
            _preview.IsGestureActive || _generation == _candidateEnumerationGeneration || _frame is not { } frame)
            return;
        if (_previewPath is not null && !StringComparer.OrdinalIgnoreCase.Equals(_previewPath, frame.CanonicalPath)) return;
        var source = frame.SourcePixels;
        var metrics = new WallMetrics();
        var placement = MemberImageGeometry.Calculate(metrics.WidthOf(_draft.LocalBounds.Width),
            metrics.HeightOf(_draft.LocalBounds.Height), source.Width, source.Height,
            PreviewRasterizationScale, _draft.ImageDisplay);
        // Compare the complete sharp plane with the physical display requirement,
        // retaining the body's source, longest-edge and decoded-pixel ceilings.
        var scale = Math.Min(1, placement.Scale * PreviewRasterizationScale);
        scale = Math.Min(scale, 4096d / Math.Max(source.Width, source.Height));
        scale = Math.Min(scale, Math.Sqrt(16_000_000d / ((long)source.Width * source.Height)));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        // One pixel of rounding tolerance avoids retrying an already capped plane.
        var blurMissing = _draft.Visual.BackgroundMode == MemberBackgroundMode.Blur && frame.BlurredUnderfill is null;
        if (!blurMissing && frame.SharpDecodedPixels.Width + 1 >= width && frame.SharpDecodedPixels.Height + 1 >= height) return;
        _pending = LoadPreviewAsync();
    }
    private void Changed()
    {
        if (_allowDiscard) { _dialog.CloseButtonText = T("Common.Cancel"); ActionIcon.DecorateDialog(_dialog); }
        _allowDiscard = false; _discardConfirmed = false; _error.Text = string.Empty;
    }

    private void SetSize(int columns, int rows)
    {
        _preview.FinishGesture(notifyCompleted: false);
        if (!(_original is PuzzleMember ? MemberSizeRules.IsPuzzleSize(columns, rows) : MemberSizeRules.IsTileSize(columns, rows)) || rows > _maxRows)
        { ShowError(T("Common.CheckInput")); return; }
        if (_layout is not null)
        {
            var resized = _layout.Current;
            while (resized.Size.Columns < columns) resized = PartitionOps.AddColumn(resized).Layout!;
            while (resized.Size.Columns > columns) resized = PartitionOps.RemoveColumn(resized).Layout!;
            while (resized.Size.Rows < rows) resized = PartitionOps.AddRow(resized).Layout!;
            while (resized.Size.Rows > rows) resized = PartitionOps.RemoveRow(resized).Layout!;
            _layout.ReplaceLayout(resized.Size, resized.Partitions);
            LayoutChanged();
        }
        else { _draft = _draft with { LocalBounds = _draft.LocalBounds with { Width = columns, Height = rows } }; Changed(); RefreshFields(); EnsurePreviewQuality(); }
    }
    private void SetPuzzlePreset(int columns, int rows)
    {
        _preview.FinishGesture(notifyCompleted: false);
        if (_layout is null) return;
        if (rows > _maxRows) { RefreshFields(); ShowOperationReminder(string.Format(T("Editor.HeightLimit"), _maxRows)); return; }
        _layout.ReplaceLayout(new GridSize(columns, rows), PuzzlePresetPartitions(columns, rows));
        LayoutChanged();
    }
    private bool IsPuzzlePreset(int columns, int rows) => _layout is not null &&
        _layout.Current.Partitions.ToHashSet().SetEquals(PuzzlePresetPartitions(columns, rows));
    private static IReadOnlyList<GridRect> PuzzlePresetPartitions(int columns, int rows)
    {
        var partitions = new List<GridRect>();
        for (var row = 0; row < rows; row += 2)
            for (var column = 0; column < columns; column += 2)
                partitions.Add(new GridRect(column, row, 2, 2));
        return partitions;
    }
    private void RefreshToolHint() => _toolHint.Text = T(_layout is null ? "Editor.RequiresPuzzle"
        : _preview.Tool == EditTool.Draw ? "Layout.DrawHint" : _preview.Tool == EditTool.Erase ? "Layout.EraseHint" : "Layout.ToolRequired");
    private bool CommitRatioInput()
    {
        if (!_ratioDirty) return true;
        if (!int.TryParse(_imageRatio.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var percent) || percent is < 1 or > 10000)
        { ShowError(T("Editor.IntegerRatio")); return false; }
        _preview.FinishGesture(notifyCompleted: false); _ratioDirty = false;
        _draft = _draft with { ImageDisplay = new MemberImageDisplay { Mode = ImageDisplayMode.Custom, CustomScale = percent / 100d } };
        Changed(); RefreshPreview(); EnsurePreviewQuality(); return true;
    }
    private void LayoutChanged()
    {
        if (_layout is null || _draft is not PuzzleMember puzzle) return;
        _draft = puzzle with { LocalBounds = puzzle.LocalBounds with { Width = _layout.Current.Size.Columns, Height = _layout.Current.Size.Rows }, Partitions = _layout.Current.Partitions.ToArray() };
        Changed(); RefreshFields(); EnsurePreviewQuality();
    }
    private void SetTransparency(double value)
    {
        var percent = (int)Math.Clamp(Math.Round(value), 0, 100);
        _draft = _draft with { Visual = _draft.Visual with { BackgroundTransparency = percent } }; Changed();
        _refreshing = true; _transparencyInput.Value = _transparency.Value = percent; _refreshing = false; RefreshPreview();
    }
    private void SetDisplayMode(ImageDisplayMode mode)
    {
        _preview.FinishGesture(notifyCompleted: false);
        _ratioDirty = false;
        var display = _draft.ImageDisplay;
        if (mode == ImageDisplayMode.Custom && display.Mode != ImageDisplayMode.Custom && _frame is not null)
        {
            var metrics = new WallMetrics(); var width = metrics.WidthOf(_draft.LocalBounds.Width); var height = metrics.HeightOf(_draft.LocalBounds.Height);
            var placement = MemberImageGeometry.Calculate(width, height, _frame.SourcePixels.Width, _frame.SourcePixels.Height,
                PreviewRasterizationScale, display);
            var percent = Math.Round(100 * MemberImageGeometry.CustomScaleFromImageScale(placement.Scale, width, height,
                _frame.SourcePixels.Width, _frame.SourcePixels.Height), MidpointRounding.AwayFromZero);
            display = display with { CustomScale = Math.Clamp(percent / 100, MemberImageGeometry.MinimumCustomScale, MemberImageGeometry.MaximumCustomScale) };
        }
        _draft = _draft with { ImageDisplay = display with { Mode = mode } }; Changed(); RefreshPreview(); EnsurePreviewQuality();
    }

    private async Task PickTargetAsync()
    {
        if (_closed || _previewClosing || _pickerBusy) return;
        _pickerBusy = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pickerPending = completion.Task;
        try
        {
            // IFileDialog.Show runs a nested native message loop. Publish this
            // operation to _pending before that loop can receive an exit request.
            await Task.Yield();
            if (_closed || _previewClosing) return;
            var path = TargetItemPicker.Pick(WinRT.Interop.WindowNative.GetWindowHandle(_owner), T("Editor.SelectCurrentFolder"), GetTargetPickerInitialFolder());
            if (!_closed && !_previewClosing && path is not null) { _target.Text = path; if (ConfirmTarget()) await _pending; }
        }
        catch (Exception ex) { if (!_closed && !_previewClosing) _error.Text = string.Format(T("Entry.PickerFailed"), ex.Message); }
        finally { _pickerBusy = false; completion.TrySetResult(); }
    }

    private async Task PickSourceAsync(MemberImageSourceKind kind)
    {
        if (_closed || _previewClosing) return;
        if (_pickerBusy) { RefreshFields(); return; }
        if (kind == MemberImageSourceKind.Automatic)
        {
            _sourceDirty = false; _draft = _draft with { Images = new(), Carousel = null, Visual = _draft.Visual with { BackgroundMode = MemberBackgroundMode.Default, BackgroundTransparency = 50 } };
            _previewPath = null; Changed(); RefreshFields(); GallerySourceChanged?.Invoke(); await ReloadCandidatesAsync(); return;
        }
        _pickerBusy = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pickerPending = completion.Task;
        try
        {
            string? path;
            var owner = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(_owner));
            if (kind == MemberImageSourceKind.Folder)
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(owner);
                TargetItemPicker.SetSuggestedFolder(picker, _sourcePath.Text);
                path = (await picker.PickSingleFolderAsync())?.Path;
            }
            else
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(owner);
                TargetItemPicker.SetSuggestedFolder(picker, _sourcePath.Text);
                picker.FileTypeFilter.Add("*");
                path = (await picker.PickSingleFileAsync())?.Path;
            }
            if (!_closed && !_previewClosing && path is not null)
            {
                // Browsing and typing share the same readable-image gate and the
                // same cached candidate list, including for empty/broken folders.
                _refreshing = true; _sourcePath.Text = path; _refreshing = false;
                _sourceDirty = true; _sourceRevision = ++_inputRevision;
                await ConfirmSourceAsync();
            }
        }
        catch (Exception ex) { if (!_closed && !_previewClosing) ShowError(string.Format(T("Puzzle.PickerFailed"), ex.Message)); }
        finally
        {
            _pickerBusy = false;
            completion.TrySetResult();
            if (!_closed && !_previewClosing) RefreshFields();
        }
    }

    private async Task ReloadCandidatesAsync(PuzzleCandidateResult? cached = null)
    {
        if (_closed || _previewClosing) return;
        var source = _draft.Images; var revision = _sourceRevision;
        var request = BeginRequest(); _sourceStatus.Text = T("Puzzle.ReadingSource");
        TraceImage($"CandidatesBegin request={request.Generation} prepared={cached is not null}");
        _candidateEnumerationGeneration = request.Generation;
        CancelThumbnailRequests();
        var enumerating = cached is null;
        // This request owns the state, including when it replaces a still-running
        // enumeration with prepared candidates. An older finally cannot clear it.
        _enumeratingCandidates = enumerating;
        try
        {
            var result = cached ?? await _images.EnumerateMemberCandidatesAsync(source, request.Token);
            TraceImage($"CandidatesResult request={request.Generation} status={result.Status} complete={result.IsComplete} count={result.Paths.Count} active={Active(request.Generation)}");
            if (enumerating && _candidateEnumerationGeneration == request.Generation)
                _enumeratingCandidates = false;
            if (!Active(request.Generation) || revision != _sourceRevision || source != _draft.Images || _sourceDirty) return;
            var preferred = _previewPath ?? _draft.Carousel?.CurrentImageId;
            var existing = _candidates.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
            var previousPath = _previewPath;
            _refreshing = true; _candidates.Clear(); _thumbnailStrip.Children.Clear();
            if (_draft.Images.Kind == MemberImageSourceKind.Folder)
                foreach (var path in result.Paths)
                {
                    var item = existing.GetValueOrDefault(path) ?? new Thumbnail(path);
                    if (result.ChangedPaths.Contains(path)) { item.BudgetEdge = 0; }
                    _candidates.Add(item); AddThumbnail(item);
                }
            var selection = _candidates.FirstOrDefault(i => StringComparer.OrdinalIgnoreCase.Equals(i.Path, preferred)) ?? _candidates.FirstOrDefault();
            _selectedThumbnail = selection; _previewPath = selection?.Path ?? (_draft.Images.Kind == MemberImageSourceKind.Single ? result.Paths.FirstOrDefault() : null);
            _refreshing = false; RefreshThumbnailSelection();
            _thumbnailCandidatesReady = true;
            _galleryVersion = result.SourceVersion;
            _galleryVersionSource = _draft.Images;
            TraceImage($"CandidatesAccepted request={request.Generation} count={_candidates.Count} selected={_previewPath}");
            _sourceStatus.Text = _draft.Images.Kind == MemberImageSourceKind.Automatic ? string.Empty
                : WallImageService.DescribeCandidateDiagnostic(result) ?? (result.Paths.Count > 0 ? string.Empty : T("Puzzle.NoCandidates"));
            // The main request starts and completes before any thumbnails enter
            // the shared two-slot service. Its current completion opens the batch
            // with the latest DPI, even when the root changes during this await.
            if (_frame is not null && StringComparer.OrdinalIgnoreCase.Equals(previousPath, _previewPath) &&
                _previewPath is not null && !result.ChangedPaths.Contains(_previewPath))
            { _thumbnailPreviewPending = false; RestartThumbnailBatch(); }
            else await LoadPreviewAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            TraceImage($"CandidatesFailed request={request.Generation} error={ex}");
            if (Active(request.Generation)) _sourceStatus.Text = string.Format(T("Puzzle.SourceFailed"), ex.Message);
        }
        finally
        {
            if (enumerating && _candidateEnumerationGeneration == request.Generation)
                _enumeratingCandidates = false;
        }
    }

    private async Task LoadPreviewAsync()
    {
        if (_closed || _previewClosing) return;
        var request = BeginRequest();
        TraceImage($"PreviewBegin request={request.Generation} selected={_previewPath}");
        try
        {
            var target = _draft.TargetInput is null ? null : string.Equals(_draft.TargetInput, _originalInput, StringComparison.Ordinal) ? _managedEntryPath ?? _draft.TargetInput : _draft.TargetInput;
            var requestedMember = _draft;
            var requestedScale = PreviewRasterizationScale;
            var metrics = new WallMetrics();
            var width = Math.Clamp((int)Math.Ceiling(metrics.WidthOf(requestedMember.LocalBounds.Width) * requestedScale), 1, 4096);
            var height = Math.Clamp((int)Math.Ceiling(metrics.HeightOf(requestedMember.LocalBounds.Height) * requestedScale), 1, 4096);
            var result = await _images.LoadMemberFrameAsync(requestedMember, target, width, height, _previewPath, request.Token);
            TraceImage($"PreviewResult request={request.Generation} status={result.Status} frame={result.Frame is not null} active={Active(request.Generation)} diagnostic={result.Diagnostic}");
            if (!Active(request.Generation)) return;
            if (result.Frame is not null || result.Status == WallImageStatus.None)
            {
                _frame = result.Frame;
                if (_draft.Images.Kind == MemberImageSourceKind.Automatic) _sourceStatus.Text = string.Empty;
                RefreshPreview();
                // A mode, size, DPI, or blur-demand edit can land while this frame
                // was still decoding. Check that requirement once here; rendering
                // and SetScene never initiate image requests.
                var blurDemandChanged = (requestedMember.Visual.BackgroundMode == MemberBackgroundMode.Blur)
                    != (_draft.Visual.BackgroundMode == MemberBackgroundMode.Blur);
                if (blurDemandChanged || requestedMember.ImageDisplay != _draft.ImageDisplay ||
                    requestedMember.LocalBounds.Size != _draft.LocalBounds.Size || requestedScale != PreviewRasterizationScale)
                    EnsurePreviewQuality();
            }
            else _sourceStatus.Text = (result.Diagnostic ?? T("Puzzle.UnavailableImage")) + (_frame is not null ? T("Puzzle.KeepPreview") : string.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Active(request.Generation)) _sourceStatus.Text = string.Format(T("Puzzle.LoadFailed"), ex.Message); }
        finally
        {
            if (Active(request.Generation) && _thumbnailPreviewPending)
            {
                _thumbnailPreviewPending = false;
                RestartThumbnailBatch();
            }
        }
    }

    private void RestartThumbnailBatch()
    {
        var edge = (int)Math.Clamp(Math.Ceiling(48 * PreviewRasterizationScale), 64, 256);
        var needsBlur = _draft.Visual.BackgroundMode == MemberBackgroundMode.Blur;
        if (_thumbnailRequest is not null && _thumbnailEdge == edge && _thumbnailNeedsBlur == needsBlur) return;
        CancelThumbnailRequests(resetCandidates: false);
        if (_closed || _previewClosing || _saved || _discardConfirmed || _sourceDirty || _targetDirty ||
            !_thumbnailCandidatesReady || _thumbnailPreviewPending || _candidates.Count == 0) return;
        var batch = _thumbnailRequest = new CancellationTokenSource();
        var token = batch.Token;
        _thumbnailEdge = edge;
        _thumbnailNeedsBlur = needsBlur;
        TrackOperation(LoadThumbnailsAsync(_candidates.ToArray(), _draft, edge, batch, token));
    }
    private async Task LoadThumbnailsAsync(IReadOnlyList<Thumbnail> candidates, WallMember member, int edge,
        CancellationTokenSource batch, CancellationToken token)
    {
        try
        {
            foreach (var item in candidates)
            {
                if (!ThumbnailBatchActive(batch, token)) return;
                if (item.Visual is not null && item.BudgetEdge == edge && item.NeedsBlur == (member.Visual.BackgroundMode == MemberBackgroundMode.Blur)) continue;
                // Await each result before submitting the next request; a 99-image
                // gallery must never prequeue 99 decodes ahead of a new selection.
                var result = await _images.LoadMemberFrameAsync(member, null, edge, edge, item.Path, token,
                    MemberImagePurpose.Thumbnail);
                if (!ThumbnailBatchActive(batch, token)) return;
                if (_candidates.Contains(item) && item.Host is { } host && result.Frame is { } frame)
                {
                    var tile = new TileMember { Id = "thumbnail", LocalBounds = new GridRect(0, 0, 1, 1), Visual = member.Visual,
                        Images = new MemberImageSource { Kind = MemberImageSourceKind.Single, Path = item.Path }, ImageDisplay = new MemberImageDisplay { Mode = ImageDisplayMode.Fill } };
                    var visual = WallMemberVisualFactory.Create(tile, frame, new WallMetrics(), 1, _visualStyle, interactive: false);
                    if (item.Visual is { } previous) host.Children.Remove(previous);
                    item.Visual = visual;
                    item.BudgetEdge = edge; item.NeedsBlur = member.Visual.BackgroundMode == MemberBackgroundMode.Blur;
                    host.Children.Insert(0, visual);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (ThumbnailBatchActive(batch, token)) _sourceStatus.Text = string.Format(T("Puzzle.LoadFailed"), ex.Message); }
    }

    /// <summary>The visible editor consumes the same completed directory snapshots as the wall.</summary>
    internal Task RefreshGalleryCandidatesAsync(MemberImageSource expectedSource, PuzzleCandidateResult snapshot, long? sourceRevision = null)
    {
        if (!_opened || _closed || _previewClosing || _sourceDirty || _draft.Images.Kind != MemberImageSourceKind.Folder ||
            _draft.Images != expectedSource || sourceRevision is { } revision && revision != _sourceRevision) return Task.CompletedTask;
        if (_galleryVersionSource == expectedSource && snapshot.SourceVersion is not null && snapshot.SourceVersion == _galleryVersion &&
            _candidates.Select(item => item.Path).SequenceEqual(snapshot.Paths, StringComparer.OrdinalIgnoreCase))
        { _sourceStatus.Text = WallImageService.DescribeCandidateDiagnostic(snapshot) ?? (snapshot.Paths.Count == 0 ? T("Puzzle.NoCandidates") : string.Empty); return Task.CompletedTask; }
        // A held pointer owns its frame and topology until release. The prepared
        // candidate result can wait without starting a second directory read.
        if (_preview.IsGestureActive)
        { _deferredGallerySnapshot = (expectedSource, snapshot, _sourceRevision); return Task.CompletedTask; }
        _pending = ReloadCandidatesAsync(snapshot); return _pending;
    }

    public string? TrySaveNow()
    {
        _preview.FinishGesture(notifyCompleted: false);
        if (_pickerBusy) return ShowError(T("Settings.Saving"));
        // Save/exit can be invoked without moving keyboard focus. Confirm both
        // complete number drafts explicitly rather than relying on a blur event.
        if (!_width.CommitPendingInput() || !_height.CommitPendingInput()) return ShowError(T("Common.CheckInput"));
        if (!CommitRatioInput()) return _error.Text;
        if (!MemberSizeRules.IsValid(_draft)) return ShowError(T("Common.CheckInput"));
        if (_sourceDirty) return ShowError(T("Puzzle.UnavailableImage"));
        if (_draft.LocalBounds.Height > _maxRows)
            return ShowError(string.Format(T("Editor.HeightLimit"), _maxRows));
        if (!_width.IsInputValid || !_height.IsInputValid || !double.IsFinite(_transparencyInput.Value))
            return ShowError(T("Common.CheckInput"));
        var pendingTitle = _title.Text;
        var applyPendingTitle = _titleDirty && (!_targetDirty || _titleRevision > _targetRevision);
        if (_targetDirty && !ConfirmTarget()) return _error.Text;
        if (applyPendingTitle) { _refreshing = true; _title.Text = pendingTitle; _refreshing = false; _titleDirty = true; }
        if (_titleDirty && !ConfirmTitle()) return _error.Text;
        try
        {
            var target = NormalizeTarget(_target.Text.Trim()) ?? string.Empty;
            var unchanged = string.Equals(target, _originalInput ?? string.Empty, StringComparison.Ordinal);
            var changes = new List<EntryChange>();
            var isUrl = !Path.IsPathFullyQualified(target) && Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
                !uri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase);
            if (target.Length == 0) { if (_original.Entry is not null) changes.Add(new EntryChange.Remove(_draft.Id)); }
            else if (!unchanged || _original.Entry is null)
            {
                if (isUrl)
                {
                    if (!RegisteredUrlProtocols.IsSupported(target)) return ShowError(T("Entry.InvalidUrl"));
                    changes.Add(_original.Entry?.Kind == ManagedEntryKind.Url ? new EntryChange.ChangeTarget(_draft.Id, target) : new EntryChange.CreateUrl(_draft.Id, target));
                }
                else
                {
                    if (!Path.IsPathFullyQualified(target) || !File.Exists(target) && !Directory.Exists(target)) return ShowError(T("Editor.InvalidTarget"));
                    target = LaunchIdentityService.NormalizePath(target);
                    var shortcut = new[] { ".lnk", ".url" }.Contains(Path.GetExtension(target), StringComparer.OrdinalIgnoreCase);
                    if (shortcut)
                    {
                        if (Path.GetExtension(target).Equals(".url", StringComparison.OrdinalIgnoreCase))
                        {
                            var shortcutUrl = UrlShortcut.ReadUrlLine(File.ReadAllBytes(target));
                            if (!RegisteredUrlProtocols.IsSupported(shortcutUrl)) return ShowError(T("Entry.InvalidUrl"));
                        }

                        changes.Add(new EntryChange.CopyShortcut(_draft.Id, target));
                    }
                    else if (_original.Entry?.Kind == ManagedEntryKind.Lnk)
                    {
                        if (_managedEntryPath is not null && _links.Read(_managedEntryPath).HasIdList) return ShowError(T("Entry.IdListTarget"));
                        changes.Add(new EntryChange.ChangeTarget(_draft.Id, target));
                    }
                    else changes.Add(new EntryChange.CreateLink(_draft.Id, target));
                }
            }
            if (_draft.Visual.TitleMode == MemberTitleMode.Manual && !string.IsNullOrEmpty(_draft.Visual.Title) && target.Length > 0 && EntryNames.Validate(_draft.Visual.Title).Count != 0)
                return ShowError(T("Puzzle.InvalidTitle"));
            var replaced = changes.Any(c => c is EntryChange.CopyShortcut or EntryChange.CreateLink or EntryChange.CreateUrl);
            if (_original.Entry is not null && target.Length > 0 && !replaced && _draft.Visual.TitleMode == MemberTitleMode.Manual &&
                !string.IsNullOrEmpty(_draft.Visual.Title) && _draft.Visual.Title != _original.Visual.Title) changes.Add(new EntryChange.Rename(_draft.Id));
            if (_draft.Images.Kind == MemberImageSourceKind.Single && _draft.Images.Media is null && !File.Exists(_draft.Images.Path) ||
                _draft.Images.Kind == MemberImageSourceKind.Folder && !Directory.Exists(_draft.Images.Path)) return ShowError(T("Puzzle.SourceRequired"));
            if (_draft is PuzzleMember puzzle && !new PartitionLayout(puzzle.LocalBounds.Size, puzzle.Partitions).Check().IsValid) return ShowError(T("Puzzle.InvalidPartition"));
            var candidate = _draft with { TargetInput = target.Length == 0 ? null : target, Entry = target.Length == 0 ? null : _original.Entry };
            if (MemberPresentationRules.Validate(candidate) is { } validation) return ShowError(validation);
            var error = _save(candidate, changes);
            if (error is not null) return ShowError(error);
            _saved = true; _draft = candidate; return null;
        }
        catch (Exception ex) { return ShowError(string.Format(T("Common.SaveFailed"), ex.Message)); }
    }

    private string? ReadManagedTarget()
    {
        if (_managedEntryPath is null || _original.Entry is null) return null;
        try { return _original.Entry.Kind == ManagedEntryKind.Lnk ? _links.Read(_managedEntryPath).TargetPath : UrlShortcut.ReadUrlLine(File.ReadAllBytes(_managedEntryPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException) { return null; }
    }
    private static string? NormalizeTarget(string input)
    {
        if (input.Length == 0) return null;
        var expanded = Environment.ExpandEnvironmentVariables(input);
        if (Path.IsPathFullyQualified(expanded))
        {
            try { return LaunchIdentityService.NormalizePath(expanded); }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return input; }
        }
        if (RegisteredUrlProtocols.IsSupported(input) && Uri.TryCreate(input, UriKind.Absolute, out var registeredUri))
            return registeredUri.AbsoluteUri;
        if (!input.Any(char.IsWhiteSpace) && !input.Contains('\\') && !input.Contains("://", StringComparison.Ordinal) &&
            input.Split('/')[0].Contains('.') && Uri.TryCreate("https://" + input, UriKind.Absolute, out var uri) &&
            uri.HostNameType != UriHostNameType.Unknown) return uri.AbsoluteUri;
        return input;
    }
    private (long Generation, CancellationToken Token) BeginRequest() { CancelRequest(); _request = new(); return (_generation, _request.Token); }
    private void CancelRequest()
    {
        _generation++; _request?.Cancel(); _request?.Dispose(); _request = null;
        TraceImage("CancelRequest");
    }
    private void CancelThumbnailRequests(bool resetCandidates = true)
    {
        _thumbnailRequest?.Cancel(); _thumbnailRequest?.Dispose(); _thumbnailRequest = null;
        if (resetCandidates) { _thumbnailCandidatesReady = false; _thumbnailPreviewPending = true; }
    }
    private bool ThumbnailBatchActive(CancellationTokenSource batch, CancellationToken token) =>
        !_closed && !_previewClosing && !token.IsCancellationRequested && ReferenceEquals(batch, _thumbnailRequest);
    private bool Active(long generation) => !_closed && !_previewClosing && generation == _generation;
    private string ShowError(string message) { TraceImage("Error " + message); _error.Text = message; return message; }
    private void TraceImage(string message)
    {
        if (_traceImages) AppLog.Warn("MemberEditorImage", $"{message} generation={_generation} revision={_sourceRevision} kind={_draft.Images.Kind} dirty={_sourceDirty} candidates={_candidates.Count} frame={_frame is not null}");
    }
    private void ShowOperationReminder(string message)
    {
        ShowError(message); _operationText.Text = message; _operationToast.Visibility = Visibility.Visible;
        _operationTimer.Stop(); _operationTimer.Start();
    }
    private string? GetTargetPickerInitialFolder()
    {
        var input = (_targetDirty ? _target.Text : _draft.TargetInput ?? _target.Text).Trim();
        var fallback = TargetItemPicker.GetInitialFolderForPath(input);
        try
        {
            var linkPath = Environment.ExpandEnvironmentVariables(input);
            if (!Path.IsPathFullyQualified(linkPath) || !File.Exists(linkPath) ||
                !Path.GetExtension(linkPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return fallback;

            var link = _links.Read(linkPath);
            var target = link.TargetPath;
            if (string.IsNullOrWhiteSpace(target)) return fallback;
            target = Environment.ExpandEnvironmentVariables(target);
            if (!Path.IsPathFullyQualified(target))
            {
                var workingDirectory = Environment.ExpandEnvironmentVariables(link.WorkingDirectory);
                var baseFolder = Path.IsPathFullyQualified(workingDirectory) && Directory.Exists(workingDirectory)
                    ? workingDirectory
                    : Path.GetDirectoryName(linkPath);
                if (string.IsNullOrWhiteSpace(baseFolder)) return fallback;
                target = Path.GetFullPath(target, baseFolder);
            }
            return TargetItemPicker.GetInitialFolderForPath(target) ?? fallback;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException or NotSupportedException)
        {
            return fallback;
        }
    }
    private static string ColorText(Windows.UI.Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private static string T(string key) => ProductStrings.Get(key);
    private static void Id(DependencyObject item, string id) => AutomationProperties.SetAutomationId(item, "member-editor-" + id);
    private static TextBox Input() => new() { IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, MinWidth = 0 };
    private static NumberBox Number(int min, int max) => new() { Minimum = min, Maximum = max, Value = min, SmallChange = 1,
        Width = 42, MinWidth = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, Padding = new Thickness(4) };
    private ToggleButton Preset(string key, string glyph, Action action)
    {
        var button = new ToggleButton(); ActionIcon.SetIcon(button, T(key), glyph); _actions.Add((button, key, glyph));
        button.Click += (_, _) => action(); return button;
    }
    private ToggleButton PuzzlePreset(string key, int width, int height, Action action)
    {
        var button = new ToggleButton { Content = PuzzlePresetIcon(width / 2, height / 2), Width = 32, Height = 32, Padding = new Thickness(7) };
        ActionIcon.SetTip(button, T(key)); AutomationProperties.SetName(button, T(key));
        _puzzlePresetActions.Add((button, key, width, height));
        button.Click += (_, _) => action(); return button;
    }
    private static Grid PuzzlePresetIcon(int columns, int rows)
    {
        var icon = new Grid
        {
            Width = 18d * columns / Math.Max(columns, rows), Height = 18d * rows / Math.Max(columns, rows), ColumnSpacing = 1, RowSpacing = 1,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        for (var column = 0; column < columns; column++)
            icon.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < rows; row++)
            icon.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                var partition = (Border)XamlReader.Load("""
                    <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        Background="{ThemeResource TileWallSecondaryTextBrush}" />
                    """);
                Grid.SetRow(partition, row); Grid.SetColumn(partition, column); icon.Children.Add(partition);
            }
        return icon;
    }
    private void AddRow(Grid form, string key, FrameworkElement value)
    {
        var row = form.RowDefinitions.Count; form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { Text = T(key), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _labels.Add(key, label); AutomationProperties.SetLabeledBy(value, label); Grid.SetRow(label, row); form.Children.Add(label);
        Grid.SetRow(value, row); Grid.SetColumn(value, 1); form.Children.Add(value);
    }

    private static Grid FieldRow(Button leading, TextBox input, Button confirm)
    {
        var row = new Grid { ColumnSpacing = 4 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(leading); Grid.SetColumn(input, 1); row.Children.Add(input); Grid.SetColumn(confirm, 2); row.Children.Add(confirm); return row;
    }

    private bool ConfirmTarget()
    {
        var target = NormalizeTarget(_target.Text.Trim());
        if (!string.IsNullOrEmpty(target))
        {
            var local = Path.IsPathFullyQualified(target);
            if (local ? !File.Exists(target) && !Directory.Exists(target) : !RegisteredUrlProtocols.IsSupported(target))
            { ShowError(T(local ? "Editor.InvalidTarget" : "Entry.InvalidUrl")); _target.Focus(FocusState.Programmatic); return false; }
            if (local)
            {
                try
                {
                    if (Directory.Exists(target)) _ = Directory.EnumerateFileSystemEntries(target).Take(1).ToArray();
                    else { using var readable = File.Open(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { ShowError(ex.Message); _target.Focus(FocusState.Programmatic); return false; }
            }
            if (local && Path.GetExtension(target).Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                try { if (!RegisteredUrlProtocols.IsSupported(UrlShortcut.ReadUrlLine(File.ReadAllBytes(target)))) { ShowError(T("Entry.InvalidUrl")); _target.Focus(FocusState.Programmatic); return false; } }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex.Message); _target.Focus(FocusState.Programmatic); return false; }
            }
        }
        CancelRequest();
        CancelThumbnailRequests();
        // A new confirmation supersedes all earlier inference, even when the
        // path is unchanged. Old folder callbacks must not replace its save wait.
        _sourceRevision = ++_inputRevision;
        _targetDirty = _titleDirty = false;
        _draft = _draft with { TargetInput = target, Visual = _draft.Visual with { TitleMode = MemberTitleMode.Automatic, Title = null } };
        Changed(); RefreshFields(); GallerySourceChanged?.Invoke();
        _pending = ApplyTargetSourceAsync(target);
        return true;
    }

    private bool ConfirmTitle()
    {
        if (!string.IsNullOrWhiteSpace(_title.Text) && !string.IsNullOrWhiteSpace(_draft.TargetInput) && EntryNames.Validate(_title.Text).Count != 0)
        { ShowError(T("Puzzle.InvalidTitle")); _title.Focus(FocusState.Programmatic); return false; }
        _draft = _draft with { Visual = _draft.Visual with { TitleMode = MemberTitleMode.Manual, Title = _title.Text } };
        _titleDirty = false; Changed(); RefreshFields(); return true;
    }

    private static Grid Form()
    {
        var form = new Grid { RowSpacing = 8, ColumnSpacing = 12 };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); return form;
    }
    private void AddThumbnail(Thumbnail item)
    {
        if (item.Button is { } retained) { _thumbnailStrip.Children.Add(retained); return; }
        var host = item.Host = new Grid { Width = 48, Height = 48 }; var selection = WallMemberVisualFactory.AddSelection(host, _visualStyle);
        item.Outline = selection.Outline; item.Check = selection.Check;
        var button = item.Button = new Button { Width = 48, Height = 48, MinWidth = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = WallCornerStyle.Corners(_visualStyle), Content = host };
        WallMemberVisualFactory.ConfigureInteraction(button, host, WallCornerStyle.Corners(_visualStyle));
        AutomationProperties.SetName(button, item.Name); ActionIcon.SetTip(button, item.Name);
        button.Click += (_, _) => { _selectedThumbnail = item; _previewPath = item.Path; RefreshThumbnailSelection(center: false); _pending = LoadPreviewAsync(); CenterThumbnail(true); };
        _thumbnailStrip.Children.Add(button);
    }
    private void RefreshThumbnailSelection(bool center = true)
    {
        foreach (var item in _candidates) { var visible = item == _selectedThumbnail ? Visibility.Visible : Visibility.Collapsed; if (item.Outline is { } outline) outline.Visibility = visible; if (item.Check is { } check) check.Visibility = visible; }
        _galleryCount.Text = $"{(_selectedThumbnail is null ? 0 : _candidates.IndexOf(_selectedThumbnail) + 1)}/{_candidates.Count}";
        if (center) CenterThumbnail(false);
    }
    private void CenterThumbnail(bool animate)
    {
        if (_selectedThumbnail is null) return;
        var index = _candidates.IndexOf(_selectedThumbnail);
        _thumbnails.UpdateLayout();
        _thumbnails.ChangeView(Math.Max(0, index * 52), null, null, !animate);
    }
    private async Task<bool> ConfirmSourceAsync()
    {
        if (_closed || _previewClosing) return false;
        var kind = _draft.Images.Kind; if (kind == MemberImageSourceKind.Automatic) { _sourceDirty = false; return true; }
        CancelThumbnailRequests();
        var path = _sourcePath.Text.Trim(); var revision = _sourceRevision;
        var request = BeginRequest();
        _sourceStatus.Text = T("Puzzle.ReadingSource");
        TraceImage($"ConfirmSourceBegin requestedRevision={revision} path={path}");
        PuzzleCandidateResult result;
        try { result = await _images.EnumerateMemberCandidatesAsync(new MemberImageSource { Kind = kind, Path = path }, request.Token); }
        catch (OperationCanceledException) { return false; }
        var current = Active(request.Generation) && revision == _sourceRevision && kind == _draft.Images.Kind;
        TraceImage($"ConfirmSourceResult requestedRevision={revision} status={result.Status} complete={result.IsComplete} count={result.Paths.Count} current={current}");
        if (!current) return false;
        var emptyGallery = kind == MemberImageSourceKind.Folder && result.IsComplete && result.Status == WallImageStatus.Loaded;
        if (result.Paths.Count == 0 && !emptyGallery)
        {
            _sourceStatus.Text = WallImageService.DescribeCandidateDiagnostic(result) ?? T("Puzzle.UnavailableImage");
            ShowError(T("Puzzle.UnavailableImage")); return false;
        }
        _sourceDirty = false; _draft = _draft with { Images = new MemberImageSource { Kind = kind, Path = path }, Carousel = null,
            Visual = _draft.Visual with { BackgroundMode = MemberBackgroundMode.Blur } };
        TraceImage("ConfirmSourceAccepted");
        _previewPath = null; Changed(); RefreshFields(); GallerySourceChanged?.Invoke(); await ReloadCandidatesAsync(result); return true;
    }
    private async Task ApplyTargetSourceAsync(string? target)
    {
        if (_closed || _previewClosing) return;
        var source = new MemberImageSource(); var sourceRevision = _sourceRevision;
        var request = BeginRequest();
        PuzzleCandidateResult? candidates = null;
        try
        {
            if (Directory.Exists(target))
            {
                var folder = new MemberImageSource { Kind = MemberImageSourceKind.Folder, Path = target };
                candidates = await _images.EnumerateMemberCandidatesAsync(folder, request.Token);
                if (candidates.Paths.Count > 0) source = folder;
            }
            else if (File.Exists(target) && !Path.GetExtension(target).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var file = new MemberImageSource { Kind = MemberImageSourceKind.Single, Path = target };
                candidates = await _images.EnumerateMemberCandidatesAsync(file, request.Token);
                if (candidates.Paths.Count > 0) source = file;
            }
        }
        catch (OperationCanceledException) { return; }
        if (!Active(request.Generation) || sourceRevision != _sourceRevision || !string.Equals(target, _draft.TargetInput, StringComparison.Ordinal)) return;
        _sourceDirty = false; _previewPath = null; _frame = null;
        _draft = _draft with { Images = source, Carousel = null, Visual = _draft.Visual with { BackgroundMode = source.Kind == MemberImageSourceKind.Automatic ? MemberBackgroundMode.Default : MemberBackgroundMode.Blur, BackgroundTransparency = source.Kind == MemberImageSourceKind.Automatic ? 50 : _draft.Visual.BackgroundTransparency } };
        RefreshFields(); GallerySourceChanged?.Invoke(); await ReloadCandidatesAsync(source.Kind == MemberImageSourceKind.Automatic ? null : candidates);
    }

    private sealed class Thumbnail(string path)
    {
        public Grid? Host { get; set; }
        public PuzzleCanvasView? Visual { get; set; }
        public Border? Outline { get; set; }
        public Border? Check { get; set; }
        public Button? Button { get; set; }
        public int BudgetEdge { get; set; }
        public bool NeedsBlur { get; set; }
        public string Path { get; } = path;
        public string Name => System.IO.Path.GetFileName(Path);
    }
}
