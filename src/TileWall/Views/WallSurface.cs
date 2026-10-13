using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TileWall.Core.Grid;
using TileWall.Core.Wall;
using TileWall.Core.Components;
using TileWall.Resources;
using TileWall.Graphics;
using Windows.Foundation;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace TileWall.Views;

/// <summary>Projection of the committed document or an explicit layout proposal.</summary>
public sealed class WallSurface : Canvas
{
    private const byte GridCellOpacity = 26;
    private readonly WallMetrics _metrics = new();
    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _cues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PuzzleCanvasView> _puzzles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PuzzleFrame?> _puzzleFrames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBox> _titleEditors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ClockMember Member, TextBlock Time, TextBlock Date, double Width, double Height)> _clocks = new(StringComparer.Ordinal);
    private WallDocument? _document;
    private string? _dragMemberId;
    private bool _dragAllowed;
    private CancellationTokenSource? _imageCancellation;
    private long _renderVersion;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _titleScrollTimer;
    private TextBlock? _scrollingTitle;
    private Border? _scrollingTitleBand;
    private double _titleScrollOffset;
    private double _titleScrollMaximum;
    private double _titleScrollViewportWidth;
    private int _titleScrollDirection = 1;
    private DateTime _titleScrollPauseUntil;

    private CornerRadius WallCorners => WallCornerStyle.Corners(_document!.Settings.VisualStyle);

    public WallImageService? Images { get; set; }
    public Func<WallMember, string?>? ResolveEntryPath { get; set; }
    public Func<string, PuzzleFrame?>? ResolvePuzzleFrame { get; set; }
    public Func<string, double>? ResolvePuzzleProgress { get; set; }
    public bool ImagesActive { get; set; }
    public bool HasClocks => _clocks.Count > 0;

    public WallGeometry? Geometry { get; private set; }
    public event Action<string>? MemberInvoked;
    public event Action<string>? MemberEditRequested;
    public event Action<string>? MemberRemoveRequested;
    public event Action<string>? MemberAdminRequested;
    public event Action<string>? MemberLocationRequested;
    public event Action<string, string?>? GroupRenameRequested;
    public event Action<string?, int>? NewTileRequested;
    public event Action<string?, int>? NewPuzzleRequested;
    public event Action<string?, int>? NewClockRequested;
    public event Action? SettingsRequested;
    public event Action<string?, int>? ImportRequested;
    public event Action? TitleEditFinished;
    public bool HasActiveTitleEdit => _titleEditors.Values.Any(editor => editor.Visibility == Visibility.Visible);

    public WallSurface()
    {
        Background = new SolidColorBrush(Colors.Transparent);
        XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
        AutomationProperties.SetAutomationId(this, "wall-surface");
        RightTapped += OnBackgroundRightTapped;
        AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(OnWallDoubleTapped), true);
        Unloaded += (_, _) => StopTitleScrolling();
    }

    public void Render(WallDocument document)
    {
        StopTitleScrolling();
        _imageCancellation?.Cancel();
        _imageCancellation?.Dispose();
        _imageCancellation = new CancellationTokenSource();
        var renderVersion = ++_renderVersion;
        var geometry = WallLayoutEngine.Measure(document, _metrics);
        var groups = document.Lanes.SelectMany(l => l.Groups).ToDictionary(g => g.Id, StringComparer.Ordinal);
        var members = groups.Values.SelectMany(g => g.Members).ToDictionary(m => m.Id, StringComparer.Ordinal);
        _document = document;
        Geometry = geometry;
        Width = geometry.WallWidth;
        Height = geometry.WallHeight;
        Children.Clear();
        _buttons.Clear();
        _puzzles.Clear();
        _puzzleFrames.Clear();
        _cues.Clear();
        _titleEditors.Clear();
        _clocks.Clear();
        if (document.Settings.ShowGrid) AddGridBackground(document.Columns, document.Rows);
        foreach (var placed in geometry.Groups)
        {
            AddAt(CreateTitle(groups[placed.GroupId], placed.TitleBounds), placed.TitleBounds);
            foreach (var memberPlace in placed.Members)
            {
                var member = members[memberPlace.MemberId];
                if (member is PuzzleMember puzzle)
                {
                    var view = CreatePuzzle(puzzle);
                    _puzzles.Add(member.Id, view);
                    AddAt(view, memberPlace.Bounds);
                    continue;
                }
                var button = CreateMember(member, memberPlace.Bounds, renderVersion,
                    _imageCancellation.Token);
                _buttons.Add(member.Id, button);
                AddAt(button, memberPlace.Bounds);
            }
        }
        if (_dragMemberId is not null) ShowDragCue(_dragMemberId, _dragAllowed);
    }

    private void AddGridBackground(int columns, int rows)
    {
        // Fill each complete lane independently of its objects, preserving the lane gaps.
        var brush = new SolidColorBrush(Color.FromArgb(GridCellOpacity, 255, 255, 255));
        var radius = WallCornerStyle.Radius(_document!.Settings.VisualStyle);
        for (var lane = 0; lane < columns; lane++)
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < 8; column++)
        {
            var bounds = new DipRect(
                _metrics.Margin + lane * (_metrics.LaneWidth + _metrics.ColumnGap) + column * _metrics.Pitch,
                _metrics.TopMargin + row * _metrics.Pitch,
                _metrics.Cell,
                _metrics.Cell);
            AddAt(new Rectangle { Fill = brush, RadiusX = radius, RadiusY = radius,
                IsHitTestVisible = false }, bounds);
        }
    }

    private void OnWallDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, this);
            node = VisualTreeHelper.GetParent(node))
            if (node is TextBox) return;
        var hit = Hit(e.GetPosition(this));
        if (hit.MemberId is not null || !hit.GroupHandle || hit.GroupId is null) return;
        BeginGroupTitleEdit(hit.GroupId);
        e.Handled = true;
    }

    public (string? MemberId, string? GroupId, bool GroupHandle) Hit(Point position)
    {
        if (Geometry is null || _document is null) return (null, null, false);
        foreach (var placement in Geometry.Groups)
        {
            foreach (var member in placement.Members)
            {
                if (!Contains(member.Bounds, position)) continue;
                var model = _document.Lanes.SelectMany(l => l.Groups).SelectMany(g => g.Members)
                    .First(m => m.Id == member.MemberId);
                if (model is PuzzleMember puzzle && !puzzle.Partitions.Any(p =>
                    ContainsPartition(p, member.Bounds, position)))
                    return (null, placement.GroupId, false);
                return (member.MemberId, placement.GroupId, false);
            }
            if (Contains(placement.TitleBounds, position))
            {
                var editing = _titleEditors.TryGetValue(placement.GroupId, out var editor) &&
                    editor.Visibility == Visibility.Visible;
                return (null, placement.GroupId, !editing);
            }
            if (Contains(placement.Bounds, position)) return (null, placement.GroupId, false);
        }
        return (null, null, false);
    }

    private bool ContainsPartition(GridRect partition, DipRect memberBounds, Point position)
    {
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var pixels = PuzzleSceneGeometry.ToPixelRect(new DipRect(partition.Column * _metrics.Pitch,
            partition.Row * _metrics.Pitch, partition.Width * _metrics.Pitch - _metrics.Gap,
            partition.Height * _metrics.Pitch - _metrics.Gap), scale);
        return Contains(new DipRect(memberBounds.X + pixels.X / scale, memberBounds.Y + pixels.Y / scale,
            pixels.Width / scale, pixels.Height / scale), position);
    }

    public void ShowDragCue(string? memberId, bool allowed)
    {
        _dragMemberId = memberId;
        _dragAllowed = allowed;
        foreach (var (id, button) in _buttons)
        {
            button.Opacity = id == memberId ? 0.72 : 1;
            _cues[id].BorderBrush = id == memberId
                ? new SolidColorBrush(allowed ? Colors.LimeGreen : Colors.OrangeRed)
                : new SolidColorBrush(Colors.Transparent);
        }
        foreach (var (id, puzzle) in _puzzles) puzzle.SetDragCue(id == memberId, allowed);
    }

    public void BeginGroupTitleEdit(string groupId)
    {
        if (!_titleEditors.TryGetValue(groupId, out var editor)) return;
        editor.Visibility = Visibility.Visible;
        editor.Focus(FocusState.Programmatic);
        editor.SelectAll();
    }

    public void CancelImageRequests()
    {
        StopTitleScrolling();
        _imageCancellation?.Cancel();
        _imageCancellation?.Dispose();
        _imageCancellation = null;
        _renderVersion++;
    }

    public FrameworkElement? CreateDragVisual(string? memberId, string? groupId)
    {
        if (Geometry is null || _document is null) return null;
        if (memberId is not null && _puzzles.ContainsKey(memberId)) return ClonePuzzle(memberId);
        if (memberId is not null && _buttons.TryGetValue(memberId, out var button))
        {
            var placement = Geometry.Groups.SelectMany(g => g.Members)
                .FirstOrDefault(m => m.MemberId == memberId);
            return placement is null ? null : CloneMember(button, placement.Bounds.Width, placement.Bounds.Height);
        }
        if (groupId is null) return null;
        var placed = Geometry.Groups.FirstOrDefault(g => g.GroupId == groupId);
        var group = _document.Lanes.SelectMany(l => l.Groups).FirstOrDefault(g => g.Id == groupId);
        if (placed is null || group is null) return null;
        var canvas = new Canvas
        {
            Width = placed.Bounds.Width,
            Height = placed.Bounds.Height,
            Background = new SolidColorBrush(Color.FromArgb(130, 28, 47, 62)),
        };
        var heading = new TextBlock
        {
            Text = group.Title ?? ProductStrings.Get("Wall.UntitledGroup"),
            Foreground = new SolidColorBrush(Colors.White),
            Width = placed.TitleBounds.Width,
            Height = placed.TitleBounds.Height,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 0, 0),
        };
        canvas.Children.Add(heading);
        foreach (var item in placed.Members)
        {
            FrameworkElement? copy;
            if (_puzzles.ContainsKey(item.MemberId)) copy = ClonePuzzle(item.MemberId);
            else if (_buttons.TryGetValue(item.MemberId, out var source))
                copy = CloneMember(source, item.Bounds.Width, item.Bounds.Height);
            else continue;
            if (copy is null) continue;
            SetLeft(copy, item.Bounds.X - placed.Bounds.X);
            SetTop(copy, item.Bounds.Y - placed.Bounds.Y);
            canvas.Children.Add(copy);
        }
        return new Border
        {
            Width = placed.Bounds.Width, Height = placed.Bounds.Height,
            CornerRadius = WallCorners, Child = canvas, IsHitTestVisible = false,
        };
    }

    private PuzzleCanvasView? ClonePuzzle(string id)
    {
        var puzzle = _document?.Lanes.SelectMany(l => l.Groups).SelectMany(g => g.Members)
            .OfType<PuzzleMember>().FirstOrDefault(p => p.Id == id);
        if (puzzle is null) return null;
        var copy = new PuzzleCanvasView { IsHitTestVisible = false };
        copy.SetScene(puzzle, ResolvePuzzleFrame?.Invoke(id), _metrics, XamlRoot?.RasterizationScale ?? 1,
            _document!.Settings.VisualStyle);
        return copy;
    }

    private PuzzleCanvasView CreatePuzzle(PuzzleMember member)
    {
        var view = new PuzzleCanvasView { Interactive = true };
        AutomationProperties.SetAutomationId(view, $"puzzle-{member.Id}");
        view.PartitionCreated += (button, _) =>
        {
            button.Click += (_, _) => MemberInvoked?.Invoke(member.Id);
            var menu = new MenuFlyout();
            AddMenuItem(menu, ProductStrings.Get("Wall.Edit"), () => MemberEditRequested?.Invoke(member.Id));
            AddMenuItem(menu, ProductStrings.Get("Wall.Remove"), () => MemberRemoveRequested?.Invoke(member.Id));
            AddManagedEntryMenuItems(menu, member);
            button.ContextFlyout = menu;
        };
        var frame = ResolvePuzzleFrame?.Invoke(member.Id);
        _puzzleFrames[member.Id] = frame;
        view.SetScene(member, frame, _metrics, XamlRoot?.RasterizationScale ?? 1,
            _document!.Settings.VisualStyle);
        view.SetFlipProgress(ResolvePuzzleProgress?.Invoke(member.Id) ?? 1);
        return view;
    }

    public void UpdatePuzzleFrame(string id, PuzzleFrame? frame, double progress)
    {
        if (!_puzzles.TryGetValue(id, out var view)) return;
        if (!_puzzleFrames.TryGetValue(id, out var previous) || !ReferenceEquals(previous, frame))
        {
            view.UpdateFrame(frame);
            _puzzleFrames[id] = frame;
        }
        view.SetFlipProgress(progress);
    }

    public void RefreshPuzzlePresentation(WallDocument document)
    {
        // Used only for a carousel-only commit; geometry, controls and editing focus stay intact.
        _document = document;
        foreach (var id in _puzzles.Keys)
            UpdatePuzzleFrame(id, ResolvePuzzleFrame?.Invoke(id), ResolvePuzzleProgress?.Invoke(id) ?? 1);
    }

    public void RefreshClocks(DateTimeOffset utcNow)
    {
        foreach (var (_, (member, time, date, width, height)) in _clocks)
        {
            var lines = ClockTextFormatter.Format(utcNow, member.TimeFormat, member.DateFormat, CultureInfo.CurrentCulture);
            time.Text = lines.TimeLine;
            date.Text = lines.DateLine;
            FitClockText(time, date, width, height);
        }
    }

    private static void FitClockText(TextBlock time, TextBlock date, double width, double height)
    {
        // Measure the complete current strings. Time stays on one line; the full date may wrap.
        time.FontSize = 19;
        time.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var factor = Math.Min(1, Math.Min(width / Math.Max(1, time.DesiredSize.Width),
            height * 0.45 / Math.Max(1, time.DesiredSize.Height)));
        time.FontSize = Math.Max(1, 19 * factor * 0.98);
        time.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var dateHeight = Math.Max(1, height - time.DesiredSize.Height - 2);
        date.Width = width;
        date.FontSize = 12;
        date.Measure(new Size(width, double.PositiveInfinity));
        if (date.DesiredSize.Height <= dateHeight) return;
        var lower = 1d;
        var upper = 12d;
        // A fixed bounded search keeps even the longest supported format inside a legal 1×1 tile.
        for (var step = 0; step < 8; step++)
        {
            var candidate = (lower + upper) / 2;
            date.FontSize = candidate;
            date.Measure(new Size(width, double.PositiveInfinity));
            if (date.DesiredSize.Height <= dateHeight) lower = candidate;
            else upper = candidate;
        }
        date.FontSize = lower;
    }

    private static FrameworkElement CloneMember(Button button, double width, double height)
    {
        var content = (Grid)button.Content;
        var clone = new Grid
        {
            Width = width, Height = height,
            Background = content.Background,
            CornerRadius = content.CornerRadius,
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) },
        };
        foreach (var child in content.Children)
        {
            if (child is Image image)
            {
                clone.Children.Add(new Image
                {
                    Source = image.Source, Stretch = image.Stretch,
                    Width = image.Width, Height = image.Height,
                    HorizontalAlignment = image.HorizontalAlignment,
                    VerticalAlignment = image.VerticalAlignment,
                });
            }
            else if (child is TextBlock text)
            {
                clone.Children.Add(new TextBlock
                {
                    Text = text.Text, FontSize = text.FontSize,
                    Foreground = text.Foreground,
                    Visibility = text.Visibility,
                    TextTrimming = text.TextTrimming,
                    Margin = text.Margin,
                    HorizontalAlignment = text.HorizontalAlignment,
                    VerticalAlignment = text.VerticalAlignment,
                });
            }
            else if (child is StackPanel stack)
            {
                var copy = new StackPanel { Spacing = stack.Spacing, Width = stack.Width, Margin = stack.Margin,
                    HorizontalAlignment = stack.HorizontalAlignment, VerticalAlignment = stack.VerticalAlignment };
                foreach (var line in stack.Children.OfType<TextBlock>())
                    copy.Children.Add(new TextBlock { Text = line.Text, FontSize = line.FontSize,
                        Foreground = line.Foreground, TextAlignment = line.TextAlignment,
                        TextWrapping = line.TextWrapping, Width = line.Width, HorizontalAlignment = line.HorizontalAlignment });
                clone.Children.Add(copy);
            }
            else if (child is Border { Child: TextBlock label } band)
            {
                clone.Children.Add(new Border
                {
                    Background = band.Background,
                    Height = band.Height,
                    Padding = band.Padding,
                    VerticalAlignment = band.VerticalAlignment,
                    Visibility = Visibility.Visible,
                    Child = new TextBlock
                    {
                        Text = label.Text,
                        FontSize = label.FontSize,
                        Foreground = label.Foreground,
                        Width = Math.Max(0, width - band.Padding.Left - band.Padding.Right),
                        TextWrapping = TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                });
            }
        }
        return clone;
    }

    private void OnBackgroundRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null;
            node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { Tag: string tag } &&
                tag.StartsWith("member:", StringComparison.Ordinal)) return;
            if (ReferenceEquals(node, this)) break;
        }
        var hit = Hit(e.GetPosition(this));
        var position = e.GetPosition(this);
        var lane = _document is null ? 0 : Math.Clamp(
            (int)Math.Floor((position.X - _metrics.Margin) /
                (_metrics.LaneWidth + _metrics.ColumnGap)), 0, _document.Columns - 1);
        var menu = new MenuFlyout();
        AddMenuItem(menu, ProductStrings.Get("Wall.NewTile"), () => NewTileRequested?.Invoke(hit.GroupId, lane));
        AddMenuItem(menu, ProductStrings.Get("Wall.NewPuzzle"), () => NewPuzzleRequested?.Invoke(hit.GroupId, lane));
        var addComponent = new MenuFlyoutSubItem { Text = ProductStrings.Get("Menu.AddComponent") };
        var newClock = new MenuFlyoutItem { Text = ProductStrings.Get("Wall.NewClock") };
        newClock.Click += (_, _) => NewClockRequested?.Invoke(hit.GroupId, lane);
        addComponent.Items.Add(newClock);
        menu.Items.Add(addComponent);
        AddMenuItem(menu, ProductStrings.Get("Wall.Import"), () => ImportRequested?.Invoke(hit.GroupId, lane));
        AddMenuItem(menu, ProductStrings.Get("Wall.Settings"), () => SettingsRequested?.Invoke());
        menu.ShowAt(this, e.GetPosition(this));
        e.Handled = true;
    }

    private FrameworkElement CreateTitle(StartGroup group, DipRect bounds)
    {
        var grid = new Grid { Background = new SolidColorBrush(Colors.Transparent), CornerRadius = WallCorners };
        var hoverBrush = new SolidColorBrush(Color.FromArgb(52, 0, 0, 0));
        var clearBrush = new SolidColorBrush(Colors.Transparent);
        grid.PointerEntered += (_, _) => grid.Background = hoverBrush;
        grid.PointerExited += (_, _) => grid.Background = clearBrush;
        AutomationProperties.SetAutomationId(grid, $"group-title-{group.Id}");
        var title = new Button
        {
            Content = new TextBlock
            {
                Text = group.Title ?? string.Empty,
                Width = Math.Max(0, bounds.Width - 5),
                Height = Math.Min(24, bounds.Height),
                VerticalAlignment = VerticalAlignment.Bottom,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Width = bounds.Width,
            Height = bounds.Height,
            VerticalContentAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(5, 0, 0, 0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = WallCorners,
            Tag = $"title:{group.Id}",
        };
        AutomationProperties.SetName(title, string.IsNullOrEmpty(group.Title) ? ProductStrings.Get("Wall.EditUntitledGroup") : string.Format(ProductStrings.Get("Wall.EditGroup"), group.Title));
        ToolTipService.SetToolTip(title, ProductStrings.Get("Wall.GroupTitleHint"));
        AutomationProperties.SetHelpText(title, ProductStrings.Get("Wall.GroupTitleHint"));
        var pointerActivation = false;
        void ClearPointerActivationLater()
        {
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher is null) { pointerActivation = false; return; }
            if (!dispatcher.TryEnqueue(() => pointerActivation = false)) pointerActivation = false;
        }
        title.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.GetCurrentPoint(title).Properties.IsLeftButtonPressed) pointerActivation = true;
        }), true);
        title.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => ClearPointerActivationLater()), true);
        title.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => ClearPointerActivationLater()), true);
        title.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => ClearPointerActivationLater()), true);
        // Keep Button keyboard and automation activation as a rename command. Pointer clicks
        // are reserved for the group drag gesture; DoubleTapped on the surface renames instead.
        title.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
            {
                BeginGroupTitleEdit(group.Id);
                e.Handled = true;
            }
        }), true);
        title.Click += (_, _) => { if (!pointerActivation) BeginGroupTitleEdit(group.Id); };
        title.GotFocus += (_, _) => grid.Background = hoverBrush;
        title.LostFocus += (_, _) => grid.Background = clearBrush;
        grid.Children.Add(title);
        var editor = new TextBox
        {
            Text = group.Title ?? string.Empty,
            PlaceholderText = ProductStrings.Get("Wall.GroupName"),
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            Width = bounds.Width,
            Height = Math.Min(24, bounds.Height),
            MinHeight = 0,
            Padding = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
            CornerRadius = WallCorners,
            Tag = $"title:{group.Id}",
        };
        var original = editor.Text;
        void Finish(bool save)
        {
            if (editor.Visibility != Visibility.Visible) return;
            editor.Visibility = Visibility.Collapsed;
            if (save && editor.Text != original) GroupRenameRequested?.Invoke(group.Id, editor.Text);
            else editor.Text = original;
            TitleEditFinished?.Invoke();
        }
        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { Finish(true); e.Handled = true; }
            else if (e.Key == Windows.System.VirtualKey.Escape) { Finish(false); e.Handled = true; }
        };
        editor.LostFocus += (_, _) => Finish(true);
        _titleEditors[group.Id] = editor;
        grid.Children.Add(editor);
        return grid;
    }

    private Button CreateMember(WallMember member, DipRect bounds, long renderVersion,
        CancellationToken cancellation)
    {
        var color = ParseColor(member.Visual.BackgroundColor, member switch
        {
            PuzzleMember => Color.FromArgb(255, 55, 90, 145),
            ClockMember => Color.FromArgb(255, 71, 77, 90),
            _ => SystemAccentColor(),
        });
        // WinUI clips Grid children to its CornerRadius, including asynchronously loaded images.
        // Keep the rectangular viewport fixed so title scrolling never expands the tile bounds.
        var content = new Grid
        {
            Background = new SolidColorBrush(color), CornerRadius = WallCorners,
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, bounds.Width, bounds.Height) },
        };
        var main = new TextBlock
        {
            Text = member is ClockMember
                ? string.Empty
                : member is PuzzleMember ? ProductStrings.Get("Wall.Puzzle")
                : member is TileMember { Foreground: NoForegroundIcon } ? string.Empty
                : member.Entry is null ? "◇" : "▣",
            FontSize = member is ClockMember ? 19 : 26,
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(main);
        if (member is ClockMember clock)
        {
            content.Children.Remove(main);
            var date = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Colors.White),
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var clockWidth = Math.Max(1, bounds.Width - 8);
            var titleSpace = member.Visual.ShowTitle && !string.IsNullOrWhiteSpace(member.Visual.Title) ? 23 : 0;
            var clockHeight = Math.Max(1, bounds.Height - 8 - titleSpace);
            var stack = new StackPanel { Width = clockWidth, Margin = new Thickness(0, 0, 0, titleSpace),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2, IsHitTestVisible = false };
            stack.Children.Add(main);
            stack.Children.Add(date);
            content.Children.Add(stack);
            _clocks[member.Id] = (clock, main, date, clockWidth, clockHeight);
        }
        if (Images is not null && ImagesActive)
        {
            if (member.Visual.BackgroundImagePath is not null)
                _ = LoadBackgroundAsync(member.Visual.BackgroundImagePath, content, bounds,
                    renderVersion, cancellation);
            if (member is TileMember tile && tile.Foreground is not NoForegroundIcon)
                _ = LoadForegroundAsync(tile, content, main, bounds, renderVersion, cancellation);
        }
        Border? titleBand = null;
        if (member.Visual.ShowTitle && !string.IsNullOrWhiteSpace(member.Visual.Title))
        {
            titleBand = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)),
                VerticalAlignment = VerticalAlignment.Bottom,
                Height = 23,
                Visibility = Visibility.Collapsed,
                Padding = new Thickness(5, 2, 5, 2),
                Clip = new RectangleGeometry { Rect = new Rect(0, 0, bounds.Width, 23) },
                Child = new TextBlock
                {
                    Text = member.Visual.Title, FontSize = 11,
                    Foreground = new SolidColorBrush(Colors.White),
                    Width = Math.Max(0, bounds.Width - 10),
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransform = new TranslateTransform(),
                },
            };
            content.Children.Add(titleBand);
        }
        var cue = new Border
        {
            CornerRadius = WallCorners,
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            IsHitTestVisible = false,
        };
        content.Children.Add(cue);
        _cues[member.Id] = cue;
        var button = new Button
        {
            Content = content, Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = WallCorners,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent),
            Tag = $"member:{member.Id}",
        };
        AutomationProperties.SetAutomationId(button, $"member-{member.Id}");
        AutomationProperties.SetName(button, $"{member switch { TileMember => ProductStrings.Get("Wall.Tile"), PuzzleMember => ProductStrings.Get("Wall.Puzzle"), _ => ProductStrings.Get("Wall.Clock") }}：{member.Visual.Title ?? member.Id}");
        button.Click += (_, _) => MemberInvoked?.Invoke(member.Id);
        if (titleBand is not null)
        {
            var band = titleBand;
            var title = (TextBlock)band.Child;
            var titleViewportWidth = Math.Max(0, bounds.Width - band.Padding.Left - band.Padding.Right);
            var hovering = false;
            var focused = false;
            void UpdateBand() => band.Visibility = hovering || focused
                ? Visibility.Visible : Visibility.Collapsed;
            button.PointerEntered += (_, _) =>
            {
                hovering = true;
                UpdateBand();
                StartTitleScrolling(title, band, titleViewportWidth);
            };
            button.PointerExited += (_, _) =>
            {
                hovering = false;
                StopTitleScrolling(title);
                UpdateBand();
            };
            button.GotFocus += (_, _) => { focused = true; UpdateBand(); };
            button.LostFocus += (_, _) => { focused = false; UpdateBand(); };
        }
        ToolTipService.SetToolTip(button, member.Visual.Title ?? ProductStrings.Get("Wall.Tile"));
        if (member is TileMember or ClockMember)
        {
            var menu = new MenuFlyout();
            AddMenuItem(menu, ProductStrings.Get("Wall.Edit"), () => MemberEditRequested?.Invoke(member.Id));
            AddMenuItem(menu, ProductStrings.Get("Wall.Remove"), () => MemberRemoveRequested?.Invoke(member.Id));
            AddManagedEntryMenuItems(menu, member);
            button.ContextFlyout = menu;
        }
        return button;
    }

    private void StartTitleScrolling(TextBlock title, Border band, double viewportWidth)
    {
        StopTitleScrolling();
        if (viewportWidth <= 0) return;

        // Measure the untrimmed text while it is hovered, then expand the label inside a
        // clipped band. Short titles keep their normal ellipsis layout and never animate.
        title.TextTrimming = TextTrimming.None;
        title.Width = double.NaN;
        title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var textWidth = title.DesiredSize.Width;
        if (!double.IsFinite(textWidth) || textWidth <= viewportWidth + 1)
        {
            title.Width = viewportWidth;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            if (title.RenderTransform is TranslateTransform shortTransform) shortTransform.X = 0;
            return;
        }

        EnsureTitleScrollTimer();
        if (_titleScrollTimer is null)
        {
            title.Width = viewportWidth;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            if (title.RenderTransform is TranslateTransform fallbackTransform) fallbackTransform.X = 0;
            return;
        }

        title.Width = textWidth;
        // Border arranges its child inside its own content width, which is normally just
        // the tile title viewport. Grow the band with the text so the TextBlock gets a
        // finite full-width layout slot; the fixed Clip still limits what is displayed.
        band.Width = textWidth + band.Padding.Left + band.Padding.Right;
        band.HorizontalAlignment = HorizontalAlignment.Left;
        _scrollingTitle = title;
        _scrollingTitleBand = band;
        _titleScrollViewportWidth = viewportWidth;
        _titleScrollMaximum = textWidth - viewportWidth;
        _titleScrollOffset = 0;
        _titleScrollDirection = 1;
        _titleScrollPauseUntil = DateTime.UtcNow.AddMilliseconds(600);
        if (title.RenderTransform is TranslateTransform transform) transform.X = 0;
        _titleScrollTimer.Start();
    }

    private void StopTitleScrolling(TextBlock? expectedTitle = null)
    {
        if (_scrollingTitle is null || expectedTitle is not null &&
            !ReferenceEquals(expectedTitle, _scrollingTitle)) return;

        _titleScrollTimer?.Stop();
        var title = _scrollingTitle;
        var band = _scrollingTitleBand;
        _scrollingTitle = null;
        _scrollingTitleBand = null;
        if (title.RenderTransform is TranslateTransform transform) transform.X = 0;
        title.Width = _titleScrollViewportWidth;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        if (band is not null)
        {
            band.Width = double.NaN;
            band.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        _titleScrollOffset = 0;
        _titleScrollMaximum = 0;
        _titleScrollViewportWidth = 0;
        _titleScrollDirection = 1;
    }

    private void EnsureTitleScrollTimer()
    {
        if (_titleScrollTimer is not null) return;
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null) return;
        _titleScrollTimer = dispatcher.CreateTimer();
        _titleScrollTimer.Interval = TimeSpan.FromMilliseconds(30);
        _titleScrollTimer.IsRepeating = true;
        _titleScrollTimer.Tick += (_, _) => AdvanceTitleScrolling();
    }

    private void AdvanceTitleScrolling()
    {
        if (_scrollingTitle is not { } title ||
            _scrollingTitleBand?.Visibility != Visibility.Visible || Visibility != Visibility.Visible)
        {
            StopTitleScrolling();
            return;
        }
        if (DateTime.UtcNow < _titleScrollPauseUntil) return;

        _titleScrollOffset += _titleScrollDirection * 1.8;
        if (_titleScrollOffset >= _titleScrollMaximum)
        {
            _titleScrollOffset = _titleScrollMaximum;
            _titleScrollDirection = -1;
            _titleScrollPauseUntil = DateTime.UtcNow.AddMilliseconds(600);
        }
        else if (_titleScrollOffset <= 0)
        {
            _titleScrollOffset = 0;
            _titleScrollDirection = 1;
            _titleScrollPauseUntil = DateTime.UtcNow.AddMilliseconds(600);
        }
        if (title.RenderTransform is TranslateTransform transform)
            transform.X = -_titleScrollOffset;
    }

    private async Task LoadBackgroundAsync(string path, Grid target, DipRect bounds,
        long version, CancellationToken cancellation)
    {
        try
        {
            var scale = XamlRoot?.RasterizationScale ?? 1;
            var result = await Images!.LoadBackgroundAsync(path,
                Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(bounds.Height * scale)), cancellation);
            if (version != _renderVersion || cancellation.IsCancellationRequested || result.Source is null) return;
            target.Children.Insert(0, new Image { Source = result.Source, Stretch = Stretch.UniformToFill });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Bad image leaves the color fallback; source remains unchanged. */ }
    }

    private async Task LoadForegroundAsync(TileMember tile, Grid target, TextBlock fallback, DipRect bounds,
        long version, CancellationToken cancellation)
    {
        try
        {
            var scale = XamlRoot?.RasterizationScale ?? 1;
            var size = Math.Max(1, (int)Math.Ceiling(Math.Min(bounds.Width, bounds.Height) * 0.62 * scale));
            var result = await Images!.LoadForegroundAsync(tile, ResolveEntryPath?.Invoke(tile),
                size, size, cancellation);
            if (version != _renderVersion || cancellation.IsCancellationRequested || result.Source is null) return;
            fallback.Visibility = Visibility.Collapsed;
            target.Children.Insert(Math.Min(1, target.Children.Count), new Image
            {
                Source = result.Source,
                Width = size / scale,
                Height = size / scale,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Missing custom art never edits the saved source path. */ }
    }

    private void AddManagedEntryMenuItems(MenuFlyout menu, WallMember member)
    {
        var admin = AddAction(ProductStrings.Get("Wall.Admin"), () => MemberAdminRequested?.Invoke(member.Id));
        var location = AddAction(ProductStrings.Get("Wall.Location"), () => MemberLocationRequested?.Invoke(member.Id));
        menu.Opening += (_, _) => RefreshAvailability();
        RefreshAvailability();

        MenuFlyoutItem AddAction(string label, Action action)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }

        void RefreshAvailability()
        {
            var entryPath = member.Entry is null ? null : ResolveEntryPath?.Invoke(member);
            var available = entryPath is not null && File.Exists(entryPath);
            var reason = available ? string.Empty : ProductStrings.Get(member.Entry is null
                ? "Wall.NoManagedEntry" : "Wall.EntryMissing");
            var suffix = available ? string.Empty : ProductStrings.Get(member.Entry is null
                ? "Wall.NoManagedEntrySuffix" : "Wall.EntryMissingSuffix");
            Update(admin, ProductStrings.Get("Wall.Admin"));
            Update(location, ProductStrings.Get("Wall.Location"));

            void Update(MenuFlyoutItem item, string label)
            {
                item.Text = label + suffix;
                item.IsEnabled = available;
                AutomationProperties.SetHelpText(item, reason);
            }
        }
    }

    private static void AddMenuItem(MenuFlyout menu, string label, Action action)
    {
        var item = new MenuFlyoutItem { Text = label };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void AddAt(FrameworkElement element, DipRect rect)
    {
        element.Width = rect.Width;
        element.Height = rect.Height;
        SetLeft(element, rect.X);
        SetTop(element, rect.Y);
        Children.Add(element);
    }

    private static bool Contains(DipRect r, Point p) =>
        p.X >= r.X && p.Y >= r.Y && p.X < r.X + r.Width && p.Y < r.Y + r.Height;

    public static Color ParseColor(string? value, Color fallback)
    {
        if (value is null || !value.StartsWith('#')) return fallback;
        var hex = value.AsSpan(1);
        if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        return fallback;
    }

    public static Color SystemAccentColor()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings()
                .GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        }
        catch (Exception)
        {
            return Color.FromArgb(255, 32, 114, 154);
        }
    }
}
