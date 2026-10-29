using System.Globalization;
using Microsoft.UI.Xaml;
using System.Numerics;
using Microsoft.UI.Xaml.Hosting;
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
    private const byte GridCellOpacity = 13;
    private readonly WallMetrics _metrics = new();
    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action> _memberInteractions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _cues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PuzzleCanvasView> _puzzles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PuzzleFrame?> _puzzleFrames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBox> _titleEditors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<bool, string?>> _titleEditFinishers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action> _titleEditReleases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _groupTitleFeedback = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrameworkElement> _titleViews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _groupTitles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WallMember> _memberModels = new(StringComparer.Ordinal);
    private (int Columns, int Rows, WallVisualStyle Style, ElementTheme Theme, bool Show, string Titles)? _gridKey;
    private double _renderScale;
    private WallLanguageMode? _renderLanguage;
    private bool _animatePlacement;
    private readonly Windows.UI.ViewManagement.UISettings _motionSettings = new();
    public long VisualCreationCount { get; private set; }
    public long GridCreationCount { get; private set; }
    private readonly HashSet<MenuFlyout> _openContextMenus = [];
    private Canvas? _gridBackground;
    private double _viewportMinimumWidth, _viewportMinimumHeight;
    private bool _automaticHeightPresentation;
    private double _heightPresentationMinimum;
    private double _heightGridProgress, _heightGridFromProgress;
    private readonly Dictionary<Canvas, double> _heightGridLayers = [];
    private readonly Dictionary<string, ClockMemberView> _clocks = new(StringComparer.Ordinal);
    private WallDocument? _document;
    private string? _dragMemberId;
    private string? _dragGroupId;
    private bool _dragAllowed;
    private Border? _groupDragVisual;
    private CornerRadius WallCorners => WallCornerStyle.Corners(_document!.Settings.VisualStyle);

    public Func<WallMember, string?>? ResolveEntryPath { get; set; }
    public Func<WallMember, string?>? ResolveLocationTarget { get; set; }
    public Func<string, PuzzleFrame?>? ResolvePuzzleFrame { get; set; }
    public Func<string, double>? ResolvePuzzleProgress { get; set; }
    public Func<string, MemberTransition?>? ResolvePuzzleTransition { get; set; }
    public bool ImagesActive { get; set; }
    public bool HasClocks => _clocks.Count > 0;

    public WallGeometry? Geometry { get; private set; }
    public event Action<string>? MemberInvoked;
    public event Action<string>? MemberEditRequested;
    public event Action<string>? MemberRemoveRequested;
    public event Action<string>? MemberAdminRequested;
    public event Action<string>? MemberLocationRequested;
    public event Func<string, string?, string?>? GroupRenameRequested;
    public event Action<string?, int>? NewTileRequested;
    public event Action<string?, int>? NewPuzzleRequested;
    public event Action? PuzzleNextPageRequested;
    public event Action<string?, int>? NewClockRequested;
    public event Action? SettingsRequested;
    public event Action<string?, int>? ImportRequested;
    public event Action? TitleEditFinished;
    public event Action<bool>? ContextMenuVisibilityChanged;
    public bool HasActiveTitleEdit => _titleEditors.Values.Any(editor => editor.Visibility == Visibility.Visible);

    public void FocusMember(string id)
    {
        if (_buttons.TryGetValue(id, out var button)) button.Focus(FocusState.Programmatic);
        else if (_puzzles.TryGetValue(id, out var canvas))
            canvas.Children.OfType<Button>().FirstOrDefault()?.Focus(FocusState.Programmatic);
    }

    public WallSurface()
    {
        Background = new SolidColorBrush(Colors.Transparent);
        XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
        AutomationProperties.SetAutomationId(this, "wall-surface");
        RightTapped += OnBackgroundRightTapped;
        AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(OnWallDoubleTapped), true);
        Unloaded += (_, _) => { StopClockFeedback(); ClearGroupFeedback(); EndAutomaticHeightPresentation(); };
        ActualThemeChanged += (_, _) =>
        {
            RefreshGridBackground();
            foreach (var clock in _clocks.Values) clock.RefreshAppearance();
            foreach (var feedback in _groupTitleFeedback.Values)
                feedback.Background = WallGroupVisualFactory.TitleFeedback(_document!.Settings.VisualStyle, ActualTheme == ElementTheme.Dark).Background;
            if (_dragGroupId is not null) ApplyDragCue(_dragMemberId, _dragAllowed, _dragGroupId);
        };
    }

    public void SetViewportMinimum(double width, double height)
    {
        width = Math.Max(0, double.IsFinite(width) ? width : 0);
        height = Math.Max(0, double.IsFinite(height) ? height : 0);
        if (_viewportMinimumWidth == width && _viewportMinimumHeight == height) return;
        _viewportMinimumWidth = width;
        _viewportMinimumHeight = height;
        if (Geometry is null) return;
        Width = Math.Max(Geometry.WallWidth, _viewportMinimumWidth);
        Height = Math.Max(Math.Max(Geometry.WallHeight, _viewportMinimumHeight), _heightPresentationMinimum);
        RefreshGridBackground();
    }

    // Presentation only: neither this envelope nor the fading grid layers supplies
    // geometry, hit targets, rows or capacity to the layout engine.
    public void BeginAutomaticHeightPresentation()
    {
        _automaticHeightPresentation = true;
        _heightGridProgress = _heightGridFromProgress = 0;
        _heightPresentationMinimum = Math.Max(_heightPresentationMinimum, Height);
        foreach (var layer in _heightGridLayers.Keys.ToArray())
        {
            if (!ReferenceEquals(layer, _gridBackground) && layer.Opacity < .002)
            { Children.Remove(layer); _heightGridLayers.Remove(layer); }
            else _heightGridLayers[layer] = layer.Opacity;
        }
        if (_gridBackground is { } grid) _heightGridLayers[grid] = grid.Opacity;
    }

    public void AdvanceAutomaticHeightPresentation(double progress)
    {
        if (!_automaticHeightPresentation) return;
        _heightGridProgress = progress;
        var segment = Math.Clamp((progress - _heightGridFromProgress) /
            Math.Max(.000001, 1 - _heightGridFromProgress), 0, 1);
        foreach (var (layer, opacity) in _heightGridLayers)
            if (!ReferenceEquals(layer, _gridBackground)) layer.Opacity = opacity * (1 - segment);
        if (_gridBackground is { } grid)
        {
            var from = _heightGridLayers.GetValueOrDefault(grid);
            grid.Opacity = from + (1 - from) * segment;
        }
    }

    public void EndAutomaticHeightPresentation()
    {
        if (!_automaticHeightPresentation) return;
        _automaticHeightPresentation = false;
        foreach (var layer in _heightGridLayers.Keys)
            if (!ReferenceEquals(layer, _gridBackground)) Children.Remove(layer);
        _heightGridLayers.Clear();
        _heightPresentationMinimum = 0;
        if (_gridBackground is { } grid) grid.Opacity = 1;
        if (Geometry is not null) Height = Math.Max(Geometry.WallHeight, _viewportMinimumHeight);
        RefreshGridBackground();
    }

    public void Render(WallDocument document, WallGeometry? measuredGeometry = null)
    {
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var resetAppearance = _document is not null && (_document.Settings.VisualStyle != document.Settings.VisualStyle ||
            _document.Settings.LanguageMode != document.Settings.LanguageMode ||
            _renderLanguage != ProductStrings.Language || _renderScale != scale);
        try { _animatePlacement = _document is not null && !resetAppearance && _motionSettings.AnimationsEnabled; }
        catch { _animatePlacement = _document is not null && !resetAppearance; }
        if (resetAppearance) ResetProjection();
        _renderScale = scale;
        _renderLanguage = ProductStrings.Language;
        var geometry = measuredGeometry ?? WallLayoutEngine.Measure(document, _metrics);
        var groups = document.Lanes.SelectMany(l => l.Groups).ToDictionary(g => g.Id, StringComparer.Ordinal);
        var members = groups.Values.SelectMany(g => g.Members).ToDictionary(m => m.Id, StringComparer.Ordinal);
        _document = document;
        Geometry = geometry;
        Width = Math.Max(geometry.WallWidth, _viewportMinimumWidth);
        Height = Math.Max(Math.Max(geometry.WallHeight, _viewportMinimumHeight), _heightPresentationMinimum);
        foreach (var id in _memberModels.Keys.Where(id => !members.ContainsKey(id)).ToArray()) RemoveMemberView(id);
        foreach (var id in _titleViews.Keys.Where(id => !groups.ContainsKey(id)).ToArray()) RemoveGroupView(id);
        RefreshGridBackground();
        foreach (var placed in geometry.Groups)
        {
            var group = groups[placed.GroupId];
            if (!_titleViews.TryGetValue(group.Id, out var title) || _groupTitles[group.Id] != group.Title)
            {
                RemoveGroupView(group.Id);
                _titleViews[group.Id] = title = CreateTitle(group, placed.TitleBounds);
                _groupTitles[group.Id] = group.Title;
                VisualCreationCount++;
            }
            AddAt(title, WallGroupVisualFactory.TitleControlBounds(placed.TitleBounds, _metrics.TitleInteractionHeight));
            foreach (var memberPlace in placed.Members)
            {
                var member = members[memberPlace.MemberId];
                // Placement and import-claim metadata do not change a member's visual scene.
                // Retaining this tree preserves image requests, transitions, menus and clock fits.
                var reusable = _memberModels.TryGetValue(member.Id, out var old) && SamePresentation(old, member);
                if (!reusable) RemoveMemberView(member.Id);
                if (member is TileMember or PuzzleMember)
                {
                    if (!_puzzles.TryGetValue(member.Id, out var view))
                    {
                        _puzzles.Add(member.Id, view = CreatePuzzle(member));
                        VisualCreationCount++;
                    }
                    AddAt(view, memberPlace.Bounds);
                    _memberModels[member.Id] = member;
                    continue;
                }
                if (!_buttons.TryGetValue(member.Id, out var button))
                {
                    _buttons.Add(member.Id, button = CreateMember(member, memberPlace.Bounds));
                    VisualCreationCount++;
                }
                AddAt(button, memberPlace.Bounds);
                _memberModels[member.Id] = member;
            }
            if (!_groupTitleFeedback.TryGetValue(placed.GroupId, out var feedback))
                _groupTitleFeedback.Add(placed.GroupId, feedback = WallGroupVisualFactory.TitleFeedback(document.Settings.VisualStyle, ActualTheme == ElementTheme.Dark));
            AddAt(feedback, WallGroupVisualFactory.TitleControlBounds(placed.TitleBounds, _metrics.TitleInteractionHeight));
            Canvas.SetZIndex(feedback, -1);
        }
        if (_dragMemberId is not null || _dragGroupId is not null)
            ApplyDragCue(_dragMemberId, _dragAllowed, _dragGroupId);
    }

    private static bool SamePresentation(WallMember old, WallMember member)
    {
        if (ReferenceEquals(old, member)) return true;
        if (old.GetType() != member.GetType() || old.LocalBounds.Size != member.LocalBounds.Size) return false;
        if (old is PuzzleMember a && member is PuzzleMember b)
        {
            if (!a.Partitions.SequenceEqual(b.Partitions)) return false;
            old = a with { Partitions = b.Partitions };
        }
        return (old with { LocalBounds = member.LocalBounds, UserClaimed = member.UserClaimed,
            ImportSource = member.ImportSource, Carousel = member.Carousel }) == member;
    }

    private void RemoveMemberView(string id)
    {
        if (_memberInteractions.Remove(id, out var reset)) reset();
        if (_buttons.Remove(id, out var button)) Children.Remove(button);
        if (_puzzles.Remove(id, out var puzzle)) { puzzle.StopTransientFeedback(); Children.Remove(puzzle); }
        _memberModels.Remove(id); _puzzleFrames.Remove(id); _cues.Remove(id); _clocks.Remove(id);
    }

    private void RemoveGroupView(string id)
    {
        // Rendering an accepted document may replace a title independently of a
        // rename. Release its text service while attached, without committing an
        // older editor draft back into the new document through LostFocus.
        if (_titleEditReleases.Remove(id, out var release)) release();
        if (_titleViews.Remove(id, out var title)) Children.Remove(title);
        if (_groupTitleFeedback.Remove(id, out var feedback)) Children.Remove(feedback);
        _titleEditors.Remove(id); _titleEditFinishers.Remove(id); _groupTitles.Remove(id);
    }

    public void ResetProjection()
    {
        EndAutomaticHeightPresentation();
        StopClockFeedback();
        foreach (var id in _memberModels.Keys.ToArray()) RemoveMemberView(id);
        foreach (var id in _titleViews.Keys.ToArray()) RemoveGroupView(id);
        if (_gridBackground is not null) Children.Remove(_gridBackground);
        _gridBackground = null; _gridKey = null;
    }

    private void RefreshGridBackground()
    {
        if (_document is null) return;
        var visibleColumns = WallViewportPolicy.CompleteColumns(_viewportMinimumWidth, _metrics);
        // A narrow empty wall still accepts creation and retains its scrollable tail,
        // but that empty tail must not paint a misleading partial grid lane.
        var columns = _document.Settings.ExpandColumns && Geometry?.Groups.Count == 0
            ? visibleColumns : Math.Max(_document.Columns, visibleColumns);
        // Rows is the same complete-cell budget used by placement. Row zero belongs to titles.
        var rows = _document.Rows;
        var titleRows = Geometry?.Groups.Select(group => (group.LaneIndex,
            Row: (int)Math.Round((group.TitleBounds.Y - _metrics.TopMargin) / _metrics.Pitch))).ToHashSet() ?? [];
        var titleMask = string.Join(";", titleRows.OrderBy(item => item.LaneIndex).ThenBy(item => item.Row));
        var key = (columns, rows, _document.Settings.VisualStyle, ActualTheme, _document.Settings.ShowGrid, titleMask);
        if (_gridKey == key)
        {
            if (_gridBackground is not null)
            {
                _gridBackground.Width = Width; _gridBackground.Height = Height;
                if (_gridBackground.Clip is RectangleGeometry clip) clip.Rect = new Rect(0, 0, Width, Height);
            }
            return;
        }
        _gridKey = key;
        if (_automaticHeightPresentation)
        {
            foreach (var layer in _heightGridLayers.Keys.ToArray()) _heightGridLayers[layer] = layer.Opacity;
            _heightGridFromProgress = _heightGridProgress;
        }
        if (_gridBackground is { } previous)
        {
            if (_automaticHeightPresentation) _heightGridLayers[previous] = previous.Opacity;
            else Children.Remove(previous);
        }
        _gridBackground = null;
        if (!_document.Settings.ShowGrid) return;
        GridCreationCount++;
        var background = new Canvas { Width = Width, Height = Height, IsHitTestVisible = false,
            Opacity = _automaticHeightPresentation ? 0 : 1,
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, Width, Height) } };
        // Fill each complete lane independently of its objects, preserving the lane gaps.
        var channel = ActualTheme == ElementTheme.Dark ? (byte)255 : (byte)0;
        var brush = new SolidColorBrush(Color.FromArgb(GridCellOpacity, channel, channel, channel));
        var radius = WallCornerStyle.Radius(_document!.Settings.VisualStyle);
        var cells = new GeometryGroup();
        var interiors = radius > 0 ? new GeometryGroup() : null;
        for (var lane = 0; lane < columns; lane++)
        for (var row = 1; row < rows; row++)
        for (var column = 0; column < 8; column++)
        {
            if (titleRows.Contains((lane, row))) continue;
            var x = _metrics.Margin + lane * (_metrics.LaneWidth + _metrics.ColumnGap) + column * _metrics.Pitch;
            var y = _metrics.TopMargin + row * _metrics.Pitch;
            cells.Children.Add(new RectangleGeometry
            {
                Rect = new Rect(x + radius, y + radius, _metrics.Cell - 2 * radius, _metrics.Cell - 2 * radius),
            });
            if (interiors is not null)
                interiors.Children.Add(new RectangleGeometry
                {
                    Rect = new Rect(x + 2 * radius, y + 2 * radius, _metrics.Cell - 4 * radius, _metrics.Cell - 4 * radius),
                });
        }
        var grid = new Microsoft.UI.Xaml.Shapes.Path { Data = cells, Fill = brush, IsHitTestVisible = false };
        if (interiors is not null)
        {
            // The 8 DIP rounded stroke of the inset 40 DIP rectangle has a 32 DIP
            // rectangular hole. Fill that exact hole in a separate path, so the two
            // Alpha=13 brushes do not paint the same area twice.
            grid.Fill = null;
            grid.Stroke = brush;
            grid.StrokeThickness = 2 * radius;
            grid.StrokeLineJoin = PenLineJoin.Round;
        }
        background.Children.Add(grid);
        if (interiors is not null)
            background.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = interiors, Fill = brush, Stroke = null, IsHitTestVisible = false });
        _gridBackground = background;
        Canvas.SetZIndex(background, -2);
        Children.Insert(0, background);
    }

    private void ClearGroupFeedback()
    {
        foreach (var feedback in _groupTitleFeedback.Values) feedback.Visibility = Visibility.Collapsed;
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
                var model = _memberModels[member.MemberId];
                if (model is PuzzleMember puzzle && !puzzle.Partitions.Any(p =>
                    ContainsPartition(p, member.Bounds, position)))
                    return (null, placement.GroupId, false);
                return (member.MemberId, placement.GroupId, false);
            }
            if (Contains(WallGroupVisualFactory.TitleControlBounds(placement.TitleBounds, _metrics.TitleInteractionHeight), position))
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

    public void ShowDragCue(string? memberId, bool allowed, string? groupId = null)
    {
        if (_dragMemberId == memberId && _dragGroupId == groupId && _dragAllowed == allowed &&
            !(memberId is null && groupId is null && _groupDragVisual is not null)) return;
        ApplyDragCue(memberId, allowed, groupId);
    }

    private void ApplyDragCue(string? memberId, bool allowed, string? groupId)
    {
        var wasDragging = _dragMemberId is not null || _dragGroupId is not null;
        var dragging = memberId is not null || groupId is not null;
        if (wasDragging != dragging)
        {
            StopClockFeedback();
            foreach (var reset in _memberInteractions.Values) reset();
            foreach (var puzzle in _puzzles.Values) puzzle.StopTransientFeedback();
        }
        ClearGroupFeedback();
        _dragMemberId = memberId;
        _dragGroupId = groupId;
        _dragAllowed = allowed;
        var groupMembers = groupId is null ? null : _document?.Lanes.SelectMany(l => l.Groups)
            .FirstOrDefault(g => g.Id == groupId)?.Members.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, button) in _buttons)
        {
            button.Opacity = !dragging ? 1 : id == memberId || groupMembers?.Contains(id) == true ? 0 : 0.42;
            _cues[id].BorderBrush = new SolidColorBrush(Colors.Transparent);
        }
        foreach (var (id, puzzle) in _puzzles)
        {
            puzzle.Opacity = !dragging ? 1 : id == memberId || groupMembers?.Contains(id) == true ? 0 : 0.42;
        }
        foreach (var (id, title) in _titleViews) title.Opacity = !dragging ? 1 : id == groupId ? 0 : 0.42;
        if (groupId is not null && _groupTitleFeedback.TryGetValue(groupId, out var target))
        {
            target.Background = new SolidColorBrush(allowed ? SystemAccentColor() : Colors.OrangeRed);
            Canvas.SetZIndex(target, 10);
            target.Visibility = Visibility.Visible;
        }
        if (_groupDragVisual is not null)
        {
            _groupDragVisual.BorderBrush = new SolidColorBrush(allowed ? SystemAccentColor() : Colors.OrangeRed);
            if (!dragging) _groupDragVisual = null;
        }
        if (!dragging)
            foreach (var feedback in _groupTitleFeedback.Values)
            {
                Canvas.SetZIndex(feedback, -1);
                feedback.Background = WallGroupVisualFactory.TitleFeedback(_document!.Settings.VisualStyle, ActualTheme == ElementTheme.Dark).Background;
            }
    }

    public void BeginGroupTitleEdit(string groupId)
    {
        if (!_titleEditors.TryGetValue(groupId, out var editor)) return;
        editor.Visibility = Visibility.Visible;
        editor.Focus(FocusState.Programmatic);
        editor.SelectAll();
    }

    public string? FinishActiveTitleEdit(bool save)
    {
        string? activeGroupId = null;
        foreach (var (groupId, editor) in _titleEditors)
        {
            if (editor.Visibility != Visibility.Visible) continue;
            activeGroupId = groupId;
            break;
        }
        return activeGroupId is not null && _titleEditFinishers.TryGetValue(activeGroupId, out var finish)
            ? finish(save)
            : null;
    }

    public void ClearTransientInteraction()
    {
        ClearGroupFeedback();
        StopClockFeedback();
        foreach (var reset in _memberInteractions.Values) reset();
        foreach (var puzzle in _puzzles.Values) puzzle.StopTransientFeedback();
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
        var accent = new SolidColorBrush(SystemAccentColor());
        var heading = WallGroupVisualFactory.Label(group.Title ?? ProductStrings.Get("Wall.UntitledGroup"), placed.Bounds.Width - 4);
        heading.Foreground = new SolidColorBrush(Colors.White);
        heading.Margin = new Thickness(5, 0, 0, 0);
        return _groupDragVisual = new Border
        {
            Width = placed.Bounds.Width, Height = _metrics.TitleInteractionHeight,
            CornerRadius = WallCorners, Background = accent,
            BorderThickness = new Thickness(2), BorderBrush = accent,
            Child = heading, IsHitTestVisible = false,
        };
    }

    private PuzzleCanvasView? ClonePuzzle(string id)
    {
        var source = _document?.Lanes.SelectMany(l => l.Groups).SelectMany(g => g.Members)
            .FirstOrDefault(p => p.Id == id);
        if (source is not (TileMember or PuzzleMember)) return null;
        var puzzle = WallMemberVisualFactory.Project(source);
        var copy = new PuzzleCanvasView { IsHitTestVisible = false, RevealTitles = true };
        copy.SetScene(puzzle, ResolvePuzzleFrame?.Invoke(id), _metrics, XamlRoot?.RasterizationScale ?? 1,
            _document!.Settings.VisualStyle);
        return copy;
    }

    private PuzzleCanvasView CreatePuzzle(WallMember member)
    {
        var frame = ResolvePuzzleFrame?.Invoke(member.Id);
        _puzzleFrames[member.Id] = frame;
        var view = WallMemberVisualFactory.Create(member, frame, _metrics, XamlRoot?.RasterizationScale ?? 1,
            _document!.Settings.VisualStyle, (button, _) =>
            {
                button.Click += (_, _) => MemberInvoked?.Invoke(member.Id);
                button.ContextFlyout = CreateMemberContextMenu(member);
            });
        AutomationProperties.SetAutomationId(view, $"puzzle-{member.Id}");
        if (ResolvePuzzleTransition?.Invoke(member.Id) is { } transition) view.UpdateTransition(transition);
        return view;
    }

    public void UpdatePuzzleFrame(string id, PuzzleFrame? frame, double progress)
    {
        if (!_puzzles.TryGetValue(id, out var view)) return;
        var transition = ResolvePuzzleTransition?.Invoke(id);
        if (frame is null && transition is null)
        {
            // Eviction clears both transition layers even if the cached visible frame was already null.
            view.UpdateFrame(null);
            _puzzleFrames[id] = null;
            return;
        }
        if (transition is not null)
        {
            view.UpdateTransition(transition);
            _puzzleFrames[id] = frame;
            return;
        }
        if (!_puzzleFrames.TryGetValue(id, out var previous) || !ReferenceEquals(previous, frame))
        {
            view.UpdateFrame(frame);
            _puzzleFrames[id] = frame;
        }
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
        foreach (var clock in _clocks.Values) clock.Refresh(utcNow);
    }

    private static FrameworkElement CloneMember(Button button, double width, double height)
    {
        if (button.Content is ClockMemberView clock) return clock.CloneForDrag();
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
        var lane = _document is null ? 0 : Math.Max(0,
            (int)Math.Floor((position.X - _metrics.Margin) /
                (_metrics.LaneWidth + _metrics.ColumnGap)));
        var menu = CreateContextMenu();
        var advancePuzzles = false;
        AddMenuItem(menu, WallMenuAction.PuzzleNextPage, () => advancePuzzles = true);
        menu.Closed += (_, _) => { if (advancePuzzles) PuzzleNextPageRequested?.Invoke(); };
        AddMenuItem(menu, WallMenuAction.NewTile, () => NewTileRequested?.Invoke(hit.GroupId, lane));
        AddMenuItem(menu, WallMenuAction.NewPuzzle, () => NewPuzzleRequested?.Invoke(hit.GroupId, lane));
        var componentLabel = ProductStrings.Get("Menu.AddComponent");
        var addComponent = new MenuFlyoutSubItem { Text = componentLabel, Icon = ActionIcon.Glyph(ActionIcon.Add) };
        AutomationProperties.SetName(addComponent, componentLabel); ToolTipService.SetToolTip(addComponent, componentLabel);
        var clockLabel = ProductStrings.Get("Wall.TimeAndDate");
        var newClock = new MenuFlyoutItem { Text = clockLabel, Icon = ActionIcon.Glyph(ActionIcon.Clock) };
        AutomationProperties.SetName(newClock, clockLabel); ToolTipService.SetToolTip(newClock, clockLabel);
        newClock.Click += (_, _) => NewClockRequested?.Invoke(hit.GroupId, lane);
        addComponent.Items.Add(newClock);
        menu.Items.Add(addComponent);
        AddMenuItem(menu, WallMenuAction.Import, () => ImportRequested?.Invoke(hit.GroupId, lane));
        AddMenuItem(menu, WallMenuAction.Settings, () => SettingsRequested?.Invoke());
        menu.ShowAt(this, e.GetPosition(this));
        e.Handled = true;
    }

    private FrameworkElement CreateTitle(StartGroup group, DipRect bounds)
    {
        var controlBounds = WallGroupVisualFactory.TitleControlBounds(bounds, _metrics.TitleInteractionHeight);
        var grid = new Grid { CornerRadius = WallCorners };
        var titleContent = WallGroupVisualFactory.TitleContent(group.Title, bounds.Width);
        AutomationProperties.SetAutomationId(grid, $"group-title-{group.Id}");
        var title = new Button
        {
            Content = titleContent,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Width = bounds.Width,
            Height = controlBounds.Height,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Bottom,
            VerticalContentAlignment = VerticalAlignment.Center,
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
        var hovering = false;
        var focused = false;
        void UpdateGroupFeedback()
        {
            if (_groupTitleFeedback.TryGetValue(group.Id, out var feedback))
                feedback.Visibility = _dragMemberId is null && _dragGroupId is null && (hovering || focused)
                    ? Visibility.Visible : Visibility.Collapsed;
        }
        WallMemberVisualFactory.ConfigureInteraction(title, titleContent, WallCorners,
            hover => { hovering = hover; UpdateGroupFeedback(); }, drawHoverEdge: false);
        title.GotFocus += (_, _) => { focused = title.FocusState == FocusState.Keyboard; UpdateGroupFeedback(); };
        title.LostFocus += (_, _) => { focused = false; UpdateGroupFeedback(); };
        title.PointerCanceled += (_, _) => { hovering = false; focused = false; UpdateGroupFeedback(); };
        title.PointerCaptureLost += (_, _) => { hovering = false; focused = false; UpdateGroupFeedback(); };
        grid.Children.Add(title);
        var editor = new TextBox
        {
            Text = group.Title ?? string.Empty,
            PlaceholderText = ProductStrings.Get("Wall.GroupName"),
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            Width = bounds.Width,
            Height = controlBounds.Height,
            MinHeight = 0,
            Padding = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
            CornerRadius = new CornerRadius(0),
            Tag = $"title:{group.Id}",
        };
        var original = editor.Text;
        var finishing = false;
        string? Finish(bool save)
        {
            if (finishing || editor.Visibility != Visibility.Visible) return null;
            finishing = true;
            try
            {
                // Move text focus while the editor still belongs to its live XamlRoot.
                // A successful synchronous rename can remove this entire title view.
                var hadTextFocus = editor.FocusState != FocusState.Unfocused;
                if (hadTextFocus) title.Focus(FocusState.Programmatic);
                if (!save || editor.Text == original)
                {
                    editor.Text = original;
                    editor.Visibility = Visibility.Collapsed;
                    TitleEditFinished?.Invoke();
                    return null;
                }

                // Keep the editor in place until the synchronous commit returns. A
                // successful commit may rebuild this title view; while that happens,
                // the guard prevents its detach-induced LostFocus from committing twice.
                var error = GroupRenameRequested?.Invoke(group.Id, editor.Text);
                if (error is null)
                {
                    // A no-op rename may keep the existing title view. Only touch the
                    // editor if it is still the live one; a successful changed rename
                    // has already replaced it during the synchronous view refresh.
                    if (_titleEditors.TryGetValue(group.Id, out var current) && ReferenceEquals(current, editor))
                    {
                        editor.Text = original;
                        editor.Visibility = Visibility.Collapsed;
                    }
                    TitleEditFinished?.Invoke();
                }
                else if (hadTextFocus && editor.XamlRoot is not null)
                    editor.Focus(FocusState.Programmatic);
                return error;
            }
            finally { finishing = false; }
        }
        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { Finish(true); e.Handled = true; }
            else if (e.Key == Windows.System.VirtualKey.Escape) { Finish(false); e.Handled = true; }
        };
        editor.LostFocus += (_, _) => Finish(true);
        _titleEditors[group.Id] = editor;
        _titleEditFinishers[group.Id] = Finish;
        _titleEditReleases[group.Id] = () =>
        {
            finishing = true;
            if (editor.FocusState != FocusState.Unfocused) title.Focus(FocusState.Programmatic);
        };
        grid.Children.Add(editor);
        return grid;
    }

    private Button CreateMember(WallMember member, DipRect bounds)
    {
        // Tile/puzzle content uses CreatePuzzle; clocks share the same body with previews.
        var content = new ClockMemberView((ClockMember)member, _metrics,
            XamlRoot?.RasterizationScale ?? 1, _document!.Settings.VisualStyle);
        _clocks[member.Id] = content;
        var cue = new Border { CornerRadius = WallCorners, BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Colors.Transparent), IsHitTestVisible = false };
        content.Children.Add(cue); _cues[member.Id] = cue;
        var button = new Button
        {
            Content = content, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = WallCorners,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent), Tag = $"member:{member.Id}",
        };
        AutomationProperties.SetAutomationId(button, $"member-{member.Id}");
        AutomationProperties.SetName(button, $"{ProductStrings.Get("Wall.Clock")}: {member.Visual.Title ?? member.Id}");
        button.Click += (_, _) => MemberInvoked?.Invoke(member.Id);
        var reset = WallMemberVisualFactory.ConfigureInteraction(button, content, WallCorners, content.SetExternalHover);
        _memberInteractions[member.Id] = () => { reset(); content.StopTransientFeedback(); };
        ToolTipService.SetToolTip(button, string.IsNullOrWhiteSpace(member.Visual.Title) ? null : member.Visual.Title);
        button.ContextFlyout = CreateMemberContextMenu(member);
        return button;
    }

    private void StopClockFeedback()
    {
        foreach (var clock in _clocks.Values) clock.StopTransientFeedback();
    }

    private void AddManagedEntryMenuItems(MenuFlyout menu, WallMember member)
    {
        var open = AddAction(WallMenuAction.Open, () => MemberInvoked?.Invoke(member.Id));
        var admin = AddAction(WallMenuAction.Admin, () => MemberAdminRequested?.Invoke(member.Id));
        var location = AddAction(WallMenuAction.Location, () => MemberLocationRequested?.Invoke(member.Id));
        menu.Opening += (_, _) => RefreshAvailability();
        RefreshAvailability();

        MenuFlyoutItem AddAction(WallMenuAction id, Action action)
        {
            var label = MenuLabel(id);
            var item = new MenuFlyoutItem { Text = label, Icon = ActionIcon.Glyph(MenuGlyph(id)) };
            ToolTipService.SetToolTip(item, label); AutomationProperties.SetName(item, label);
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
            Update(open, ProductStrings.Get("Wall.Open"), available);
            Update(admin, ProductStrings.Get("Wall.Admin"), available && member.Entry?.Kind == ManagedEntryKind.Lnk);
            Update(location, ProductStrings.Get("Wall.Location"), available && ResolveLocationTarget?.Invoke(member) is not null);

            void Update(MenuFlyoutItem item, string label, bool enabled)
            {
                item.Text = label;
                item.IsEnabled = enabled;
                AutomationProperties.SetName(item, item.Text);
                AutomationProperties.SetHelpText(item, reason);
                ToolTipService.SetToolTip(item, available ? label : reason);
            }
        }
    }

    private static void AddMenuItem(MenuFlyout menu, WallMenuAction id, Action action)
    {
        var label = MenuLabel(id);
        var item = new MenuFlyoutItem { Text = label, Icon = ActionIcon.Glyph(MenuGlyph(id)) };
        ToolTipService.SetToolTip(item, label); AutomationProperties.SetName(item, label);
        menu.Opening += (_, _) =>
        {
            var currentLabel = MenuLabel(id);
            item.Text = currentLabel;
            ToolTipService.SetToolTip(item, currentLabel);
            AutomationProperties.SetName(item, currentLabel);
        };
        item.Click += (_, _) =>
        {
            TileWall.Core.Shell.AppLog.Info("WallContextMenu", $"Click action={label}");
            action();
        };
        menu.Items.Add(item);
    }

    private MenuFlyout CreateContextMenu()
    {
        var menu = new MenuFlyout();
        menu.Opened += (_, _) =>
        {
            _openContextMenus.Add(menu);
            ContextMenuVisibilityChanged?.Invoke(true);
        };
        menu.Closed += (_, _) =>
        {
            _openContextMenus.Remove(menu);
            ContextMenuVisibilityChanged?.Invoke(_openContextMenus.Count != 0);
        };
        return menu;
    }

    internal MenuFlyout CreateMemberContextMenu(WallMember member)
    {
        var menu = CreateContextMenu();
        AddManagedEntryMenuItems(menu, member);
        AddMenuItem(menu, WallMenuAction.Edit, () => MemberEditRequested?.Invoke(member.Id));
        AddMenuItem(menu, WallMenuAction.Remove, () => MemberRemoveRequested?.Invoke(member.Id));
        return menu;
    }

    private enum WallMenuAction { Open, Edit, Remove, Settings, Import, PuzzleNextPage, Location, Admin, NewTile, NewPuzzle }
    private static string MenuLabel(WallMenuAction action) => ProductStrings.Get("Wall." + action);
    private static string MenuGlyph(WallMenuAction action) => action switch
    {
        WallMenuAction.Edit => ActionIcon.Edit, WallMenuAction.Remove => ActionIcon.Delete,
        WallMenuAction.Settings => ActionIcon.Settings, WallMenuAction.Import => ActionIcon.Share,
        WallMenuAction.PuzzleNextPage => ActionIcon.Next, WallMenuAction.Location => ActionIcon.Browse,
        WallMenuAction.Open => "\uE8E5",
        WallMenuAction.Admin => "\uE7EF", _ => ActionIcon.Add,
    };

    private void AddAt(FrameworkElement element, DipRect rect)
    {
        var retained = Children.Contains(element);
        var previousX = GetLeft(element); var previousY = GetTop(element);
        // Retaining the tree must also retain unchanged layout properties. Most wall
        // elements are unaffected by a new drop target; avoid invalidating their layout.
        if (element.Width != rect.Width) element.Width = rect.Width;
        if (element.Height != rect.Height) element.Height = rect.Height;
        if (previousX != rect.X) SetLeft(element, rect.X);
        if (previousY != rect.Y) SetTop(element, rect.Y);
        if (!retained) Children.Add(element);
        else if (_animatePlacement && double.IsFinite(previousX) && double.IsFinite(previousY) &&
            (previousX != rect.X || previousY != rect.Y))
        {
            // Final coordinates remain in layout; visual compensation redirects yielding
            // neighbors from the displayed position without easing the pointer ghost.
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
            animation.SetVector3Parameter("displacement", new Vector3((float)(previousX - rect.X), (float)(previousY - rect.Y), 0));
            animation.InsertExpressionKeyFrame(0, "this.StartingValue + displacement");
            animation.InsertKeyFrame(1, Vector3.Zero);
            animation.Duration = TimeSpan.FromMilliseconds(180);
            visual.StartAnimation("Translation", animation);
        }
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
