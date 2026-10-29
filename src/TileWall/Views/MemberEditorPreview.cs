using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Graphics;
using Windows.Foundation;

namespace TileWall.Views;

/// <summary>Editor camera around the same shared image plane and clipped member view as the wall.</summary>
internal sealed partial class MemberEditorPreview : Grid
{
    private readonly Canvas _viewport = new();
    private readonly Canvas _scene = new() { IsHitTestVisible = false };
    private readonly PuzzleCanvasView _formal = new();
    private readonly Canvas _guides = new(), _hint = new();
    private readonly TextBlock _pointerText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, MaxWidth = 300 };
    private readonly Border _pointerTip;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _wheelTimer;
    private readonly WallMetrics _metrics = new();
    private WallMember? _member;
    private PuzzleFrame? _frame;
    private PuzzleFrame? _latestFrame;
    private double _rasterScale = 1, _cameraScale = 1, _cameraX, _cameraY, _unionX, _unionY;
    private MemberImagePlacement? _placement;
    private bool _scaling, _stroke, _wheelZooming;
    private WallVisualStyle _visualStyle;
    private double _startRadius, _startImageScale;
    private uint _gesturePointerId;
    private EditTool _gestureTool;
    private MemberImageDisplay? _gestureDisplay;
    private int _scaleMoves;
    private readonly bool _traceScale = Environment.GetEnvironmentVariable("TILEWALL_PERF") == "1" ||
        Environment.GetEnvironmentVariable("TILEWALL_TRACE_INPUT") == "1";
    private readonly bool _traceHover = Environment.GetEnvironmentVariable("TILEWALL_TRACE_INPUT") == "1";
    private Point? _previousStroke;
    private Point? _hoverPoint;
    private EditTool? _tool;
    private string? _hoverTraceState;
    private readonly HashSet<WallEdge> _strokeAffected = [];
    private readonly HashSet<WallEdge> _rejectedEdges = [];

    internal EditTool? Tool
    {
        get => _tool;
        set { if (_tool == value) return; _tool = value; RefreshHover("tool"); }
    }
    internal event Action<MemberImageDisplay>? DisplayChanged;
    internal event Action? GestureCompleted;
    internal bool IsGestureActive => _scaling || _stroke || _wheelZooming;
    internal event Action? StrokeStarted;
    internal event Action<WallEdge, EditTool>? StrokeEdge;
    internal event Action? StrokeCompleted;
    internal event Action? StrokeCanceled;
    internal event Action<StrokeOutcome>? StrokeRejected;
    internal Func<WallEdge, EditTool, StrokePreview>? PreviewStroke { get; set; }
    internal Func<EditTool, StrokePreview, string>? DescribeStroke { get; set; }

    internal MemberEditorPreview()
    {
        Height = 640; MinHeight = 180; Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _viewport.Children.Add(_scene); Children.Add(_viewport);
        _pointerTip = VisualStyles.Card(_pointerText); _pointerTip.Padding = new Thickness(8, 5, 8, 5);
        _pointerTip.IsHitTestVisible = false; _pointerTip.Visibility = Visibility.Collapsed;
        _viewport.Children.Add(_pointerTip);
        _wheelTimer = DispatcherQueue.CreateTimer(); _wheelTimer.IsRepeating = false;
        _wheelTimer.Interval = TimeSpan.FromMilliseconds(160);
        _wheelTimer.Tick += (_, _) => FinishGesture();
        SizeChanged += (_, _) => Refresh(refitCamera: !IsGestureActive);
        ActualThemeChanged += (_, _) => Refresh(refitCamera: false);
        PointerWheelChanged += OnWheel;
        PointerPressed += OnPressed; PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCanceled += (_, _) => CancelGesture();
        // Our normal release has already completed the gesture before releasing
        // capture. Only an unexpected loss should cancel it and forget hover.
        PointerCaptureLost += (_, _) => { if (IsGestureActive) CancelGesture(); };
        PointerExited += (_, _) => ForgetHover("exit");
        Unloaded += (_, _) => ForgetHover("unloaded");
    }

    internal void SetScene(WallMember member, PuzzleFrame? frame, double rasterScale, WallVisualStyle visualStyle)
    {
        _member = member; _latestFrame = frame;
        if (!IsGestureActive) _frame = frame;
        _rasterScale = Math.Max(0.1, rasterScale);
        _visualStyle = visualStyle;
        Refresh(refitCamera: !IsGestureActive);
    }

    internal void FinishGesture(bool notifyCompleted = true)
        => CompleteGesture(cancel: false, notifyCompleted);

    internal void CancelGesture() => CompleteGesture(cancel: true, notifyCompleted: true);

    private void CompleteGesture(bool cancel, bool notifyCompleted)
    {
        var wasActive = IsGestureActive;
        if (cancel) ForgetHover("cancel");
        var stroke = _stroke; var scaling = _scaling || _wheelZooming; var previousDisplay = _gestureDisplay;
        _stroke = _scaling = _wheelZooming = false; _wheelTimer.Stop();
        _previousStroke = null; _strokeAffected.Clear(); _rejectedEdges.Clear();
        _gesturePointerId = 0; _gestureDisplay = null;
        if (stroke) { if (cancel) StrokeCanceled?.Invoke(); else StrokeCompleted?.Invoke(); }
        if (scaling && cancel && previousDisplay is not null) DisplayChanged?.Invoke(previousDisplay);
        _frame = _latestFrame;
        ClearHover(); Refresh(refitCamera: true);
        ReleasePointerCaptures();
        if (wasActive && notifyCompleted) GestureCompleted?.Invoke();
    }

    private void Refresh(bool refitCamera)
    {
        if (_member is not { } member) return;
        var width = _metrics.WidthOf(member.LocalBounds.Width); var height = _metrics.HeightOf(member.LocalBounds.Height);
        _placement = _frame is null ? null : MemberImageGeometry.Calculate(width, height,
            _frame.SourcePixels.Width, _frame.SourcePixels.Height, _rasterScale, member.ImageDisplay);
        if (refitCamera)
        {
            _unionX = _unionY = 0; _cameraScale = 1;
            _cameraX = (ActualWidth - width) / 2; _cameraY = (ActualHeight - height) / 2;
        }
        _scene.Children.Clear(); _scene.RenderTransform = new ScaleTransform { ScaleX = _cameraScale, ScaleY = _cameraScale };
        Canvas.SetLeft(_scene, _cameraX); Canvas.SetTop(_scene, _cameraY);
        _viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)) };
        if (_frame is not null && _placement is { } placement)
        {
            var image = placement.Bounds;
            var middleLeft = Math.Max(0, image.X); var middleRight = Math.Min(width, image.X + image.Width);
            AddOutsideRegion(new DipRect(image.X, image.Y, Math.Max(0, -image.X), image.Height), image);
            AddOutsideRegion(new DipRect(width, image.Y, Math.Max(0, image.X + image.Width - width), image.Height), image);
            AddOutsideRegion(new DipRect(middleLeft, image.Y, Math.Max(0, middleRight - middleLeft), Math.Max(0, -image.Y)), image);
            AddOutsideRegion(new DipRect(middleLeft, height, Math.Max(0, middleRight - middleLeft), Math.Max(0, image.Y + image.Height - height)), image);
        }
        var projected = member as PuzzleMember ?? new PuzzleMember
        {
            Id = member.Id, LocalBounds = member.LocalBounds, Partitions = [new GridRect(0, 0, member.LocalBounds.Width, member.LocalBounds.Height)],
            Visual = member.Visual, Images = member.Images, ImageDisplay = member.ImageDisplay, Entry = member.Entry,
            TargetInput = member.TargetInput, SourceShortcutPath = member.SourceShortcutPath,
            TargetDisplayName = member.TargetDisplayName, ResourceOrigin = member.ResourceOrigin,
            RotationInterval = member.RotationInterval, Carousel = member.Carousel,
        };
        _formal.SetScene(projected, _frame, _metrics, _rasterScale, _visualStyle);
        Canvas.SetLeft(_formal, -_unionX); Canvas.SetTop(_formal, -_unionY); _scene.Children.Add(_formal);
        _guides.Children.Clear();
        if (member is PuzzleMember puzzle)
        {
            var layout = new PartitionLayout(puzzle.LocalBounds.Size, puzzle.Partitions);
            for (var y = 0; y < layout.Size.Rows; y++)
                for (var x = 1; x < layout.Size.Columns; x++)
                    if (!layout.IsSolid(new WallEdge(true, x, y))) AddEdge(_guides, new WallEdge(true, x, y), Microsoft.UI.Colors.White, dashed: true);
            for (var y = 1; y < layout.Size.Rows; y++)
                for (var x = 0; x < layout.Size.Columns; x++)
                    if (!layout.IsSolid(new WallEdge(false, x, y))) AddEdge(_guides, new WallEdge(false, x, y), Microsoft.UI.Colors.White, dashed: true);
        }
        foreach (var layer in new[] { _guides, _hint })
        {
            Canvas.SetLeft(layer, -_unionX); Canvas.SetTop(layer, -_unionY); _scene.Children.Add(layer);
        }
        // Rebuild feedback against the new scene, including when the pointer
        // stayed still while an image, topology, theme, or viewport changed.
        RefreshHover("refresh");
    }

    private void AddOutsideRegion(DipRect region, DipRect image)
    {
        if (region.Width <= 0 || region.Height <= 0 || _frame is null) return;
        var layer = new Canvas { Width = region.Width, Height = region.Height,
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, region.Width, region.Height) } };
        var sharp = new Image { Source = _frame.Sharp, Width = image.Width, Height = image.Height, Stretch = Stretch.Fill };
        Canvas.SetLeft(sharp, image.X - region.X); Canvas.SetTop(sharp, image.Y - region.Y); layer.Children.Add(sharp);
        layer.Children.Add(new Rectangle { Width = region.Width, Height = region.Height, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(170, 0, 0, 0)) });
        Canvas.SetLeft(layer, region.X - _unionX); Canvas.SetTop(layer, region.Y - _unionY); _scene.Children.Add(layer);
    }

    private Point ObjectPoint(Point point) => new((point.X - _cameraX) / _cameraScale + _unionX,
        (point.Y - _cameraY) / _cameraScale + _unionY);

    private WallEdge? EdgeAt(Point point)
    {
        if (_member is not PuzzleMember puzzle) return null;
        var p = ObjectPoint(point); var size = puzzle.LocalBounds.Size;
        if (p.X < 0 || p.Y < 0 || p.X >= _metrics.WidthOf(size.Columns) || p.Y >= _metrics.HeightOf(size.Rows)) return null;
        var x = (int)Math.Round((p.X + _metrics.Gap / 2) / _metrics.Pitch);
        var y = (int)Math.Round((p.Y + _metrics.Gap / 2) / _metrics.Pitch);
        var dx = Math.Abs(p.X - (x * _metrics.Pitch - _metrics.Gap / 2));
        var dy = Math.Abs(p.Y - (y * _metrics.Pitch - _metrics.Gap / 2));
        var tolerance = 7 / (_cameraScale * _rasterScale);
        if (x > 0 && x < size.Columns && dx <= tolerance && (dx <= dy || y <= 0 || y >= size.Rows))
            return new(true, x, Math.Min(size.Rows - 1, (int)(p.Y / _metrics.Pitch)));
        if (y > 0 && y < size.Rows && dy <= tolerance)
            return new(false, Math.Min(size.Columns - 1, (int)(p.X / _metrics.Pitch)), y);
        return null;
    }

    private bool AtImageEdge(Point point)
    {
        if (_placement is not { } image) return false;
        var p = ObjectPoint(point); var b = image.Bounds; var tolerance = 8 / (_cameraScale * _rasterScale);
        return p.X >= b.X - tolerance && p.X <= b.X + b.Width + tolerance && p.Y >= b.Y - tolerance && p.Y <= b.Y + b.Height + tolerance &&
            (Math.Abs(p.X - b.X) <= tolerance || Math.Abs(p.X - b.X - b.Width) <= tolerance || Math.Abs(p.Y - b.Y) <= tolerance || Math.Abs(p.Y - b.Y - b.Height) <= tolerance);
    }

    private double Radius(Point point)
    {
        var p = ObjectPoint(point);
        var x = p.X - _metrics.WidthOf(_member!.LocalBounds.Width) / 2; var y = p.Y - _metrics.HeightOf(_member.LocalBounds.Height) / 2;
        return Math.Max(0.1, Math.Sqrt(x * x + y * y));
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        RememberPointer(point.Position);
        var atEdge = AtImageEdge(point.Position);
        var captured = false;
        try
        {
            if (_wheelZooming) FinishGesture(notifyCompleted: false);
            if (IsGestureActive || _member is null || !point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed) return;
            if (Tool is { } tool && EdgeAt(point.Position) is { } edge)
            {
                if (!(captured = CapturePointer(e.Pointer))) return;
                _gesturePointerId = e.Pointer.PointerId;
                _gestureTool = point.Properties.IsRightButtonPressed ? Opposite(tool) : tool;
                _stroke = true; _strokeAffected.Clear(); _rejectedEdges.Clear(); _previousStroke = point.Position;
                StrokeStarted?.Invoke(); ApplyEdge(edge); e.Handled = true;
            }
            else if (point.Properties.IsLeftButtonPressed && atEdge && _placement is { } image)
            {
                if (!(captured = CapturePointer(e.Pointer))) return;
                _scaling = true; _gesturePointerId = e.Pointer.PointerId; _scaleMoves = 0;
                _gestureDisplay = _member.ImageDisplay; ClearHover();
                _startRadius = Radius(point.Position); _startImageScale = image.Scale; e.Handled = true;
            }
        }
        finally
        {
            if (_traceScale)
            {
                var root = e.GetCurrentPoint(null).Position;
                AppLog.Info("EditorImageGesture", $"Press pointer={e.Pointer.PointerId} local={point.Position.X:F2},{point.Position.Y:F2} root={root.X:F2},{root.Y:F2} viewport={ActualWidth:F2},{ActualHeight:F2} placement={_placement?.Bounds} imageScale={_placement?.Scale:F8} camera={_cameraX:F2},{_cameraY:F2},{_cameraScale:F4} union={_unionX:F2},{_unionY:F2} raster={_rasterScale:F4} tool={Tool?.ToString() ?? "none"} edge={atEdge} capture={captured} scaling={_scaling}");
            }
        }
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!IsGestureActive || e.Pointer.PointerId != _gesturePointerId) return;
        var point = e.GetCurrentPoint(this).Position;
        RememberPointer(point);
        var scaling = _scaling;
        // A normal release owns a final input sample. Cancel/loss only FinishGesture.
        if (scaling && _frame is not null)
            ChangeScale(_startImageScale * Radius(point) / _startRadius);
        else if (_stroke) ContinueStroke(point);
        if (_traceScale)
        {
            var ratio = _member is { } member && _frame is { } frame && _placement is { } placement
                ? 100 * MemberImageGeometry.CustomScaleFromImageScale(placement.Scale,
                    _metrics.WidthOf(member.LocalBounds.Width), _metrics.HeightOf(member.LocalBounds.Height),
                    frame.SourcePixels.Width, frame.SourcePixels.Height) : double.NaN;
            AppLog.Info("EditorImageGesture", $"Release pointer={e.Pointer.PointerId} scaling={scaling} moves={_scaleMoves} ratio={ratio:F4} end={point.X:F2},{point.Y:F2}");
        }
        FinishGesture(); e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Position;
        if ((_stroke || _scaling) && e.Pointer.PointerId != _gesturePointerId) return;
        RememberPointer(point);
        if (_stroke)
        {
            ContinueStroke(point); e.Handled = true; return;
        }
        if (_scaling && _frame is not null && _member is { } member)
        {
            _scaleMoves++;
            ChangeScale(_startImageScale * Radius(point) / _startRadius);
            e.Handled = true; return;
        }
        RefreshHover("move");
    }

    private void RememberPointer(Point point)
        => _hoverPoint = point.X >= 0 && point.Y >= 0 && point.X < ActualWidth && point.Y < ActualHeight ? point : null;

    private void RefreshHover(string reason)
    {
        ClearHover();
        if (IsGestureActive || _hoverPoint is not { } point)
        { TraceHover(reason, null); return; }
        // A resize can move the remembered local point outside the viewport.
        RememberPointer(point);
        if (_hoverPoint is null) { TraceHover(reason, null); return; }
        var edge = EdgeAt(point);
        if (Tool is { } activeTool && edge is { } previewEdge && PreviewStroke?.Invoke(previewEdge, activeTool) is { } proposal)
        {
            // Either mouse button can act here. Show the actual split/merge extent
            // even when the selected button's action is already satisfied.
            var opposite = PreviewStroke?.Invoke(previewEdge, Opposite(activeTool)) ?? proposal;
            var feedback = proposal.AffectedEdges.Count > 0 ? proposal : opposite;
            ShowAffected(feedback); ShowPointerHint(point, activeTool, feedback);
            if (proposal.Outcome == StrokeOutcome.SizeLimit)
                SetCursor(InputSystemCursorShape.UniversalNo);
            TraceHover(reason, previewEdge);
            return;
        }
        SetEdgeCursor(AtImageEdge(point));
        TraceHover(reason, edge);
    }

    private void ContinueStroke(Point point)
    {
        var previous = _previousStroke ?? point;
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(point.X - previous.X), Math.Abs(point.Y - previous.Y)) / 3));
        for (var index = 1; index <= steps; index++)
            if (EdgeAt(new Point(previous.X + (point.X - previous.X) * index / steps,
                previous.Y + (point.Y - previous.Y) * index / steps)) is { } edge) ApplyEdge(edge);
        _previousStroke = point;
    }

    private void ApplyEdge(WallEdge edge)
    {
        var proposal = PreviewStroke?.Invoke(edge, _gestureTool);
        StrokeEdge?.Invoke(edge, _gestureTool);
        if (proposal is null) return;
        ShowAffected(proposal);
        if (proposal.Outcome == StrokeOutcome.SizeLimit && _rejectedEdges.Add(edge))
            StrokeRejected?.Invoke(proposal.Outcome);
    }

    private static EditTool Opposite(EditTool tool) => tool == EditTool.Draw ? EditTool.Erase : EditTool.Draw;

    private void ClearHover()
    {
        _hint.Children.Clear(); _pointerTip.Visibility = Visibility.Collapsed; SetEdgeCursor(false);
    }

    private void ForgetHover(string reason)
    {
        _hoverPoint = null; ClearHover(); TraceHover(reason, null);
    }

    private void TraceHover(string reason, WallEdge? edge)
    {
        if (!_traceHover) return;
        var state = $"{Tool}:{edge}:{IsGestureActive}:{_hoverPoint is not null}:{_hint.Children.Count}:{_pointerTip.Visibility}";
        if (_hoverTraceState == state) return;
        _hoverTraceState = state;
        AppLog.Warn("EditorHover", $"{reason} point={_hoverPoint} tool={Tool} edge={edge} gesture={IsGestureActive} lines={_hint.Children.Count} tip={_pointerTip.Visibility}");
    }

    private void ShowPointerHint(Point point, EditTool tool, StrokePreview proposal)
    {
        if (DescribeStroke?.Invoke(tool, proposal) is not { Length: > 0 } description) return;
        _pointerText.Text = description;
        _pointerTip.Visibility = Visibility.Visible;
        _pointerTip.Measure(new Size(Math.Max(0, Math.Min(320, ActualWidth)), double.PositiveInfinity));
        Canvas.SetLeft(_pointerTip, Math.Max(0, Math.Min(point.X + 16, ActualWidth - _pointerTip.DesiredSize.Width)));
        Canvas.SetTop(_pointerTip, Math.Max(0, Math.Min(point.Y + 20, ActualHeight - _pointerTip.DesiredSize.Height)));
    }

    private void SetCursor(InputSystemCursorShape? shape)
    {
        var previous = ProtectedCursor;
        ProtectedCursor = shape is { } cursor ? InputSystemCursor.Create(cursor) : null;
        previous?.Dispose();
    }

    private void SetEdgeCursor(bool show)
    {
        SetCursor(show ? InputSystemCursorShape.SizeNorthwestSoutheast : null);
    }

    private void ShowAffected(StrokePreview proposal)
    {
        _hint.Children.Clear();
        if (_stroke) _strokeAffected.UnionWith(proposal.AffectedEdges);
        IEnumerable<WallEdge> edges = _stroke ? _strokeAffected : proposal.AffectedEdges;
        foreach (var edge in edges) AddEdge(_hint, edge, WallSurface.SystemAccentColor(), dashed: false);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_stroke || _scaling || _frame is null || _member is null || _placement is not { } placement) return;
        RememberPointer(e.GetCurrentPoint(this).Position);
        if (!_wheelZooming) _gestureDisplay = _member.ImageDisplay;
        _wheelZooming = true;
        var factor = Math.Pow(1.1, e.GetCurrentPoint(this).Properties.MouseWheelDelta / 120d);
        ChangeScale(placement.Scale * factor, wheel: true); _wheelTimer.Stop(); _wheelTimer.Start(); e.Handled = true;
    }
    private void ChangeScale(double scale, bool wheel = false)
    {
        if (_member is not { } member || _frame is not { } frame) return;
        var width = _metrics.WidthOf(member.LocalBounds.Width); var height = _metrics.HeightOf(member.LocalBounds.Height);
        DisplayChanged?.Invoke(MemberImageGeometry.InteractiveDisplay(scale, width, height,
            frame.SourcePixels.Width, frame.SourcePixels.Height, _rasterScale, wheel));
    }
    private void AddEdge(Canvas target, WallEdge edge, Windows.UI.Color color, bool dashed)
    {
        var x = edge.IsVertical ? edge.X * _metrics.Pitch - _metrics.Gap / 2 : edge.X * _metrics.Pitch;
        var y = edge.IsVertical ? edge.Y * _metrics.Pitch : edge.Y * _metrics.Pitch - _metrics.Gap / 2;
        Line Create(Windows.UI.Color stroke, double thickness) => new() { X1 = x, Y1 = y, X2 = edge.IsVertical ? x : x + _metrics.Cell,
            Y2 = edge.IsVertical ? y + _metrics.Cell : y, Stroke = new SolidColorBrush(stroke), StrokeThickness = thickness };
        if (dashed)
        {
            target.Children.Add(Create(Windows.UI.Color.FromArgb(51, 0, 0, 0), 4));
            var white = Create(Windows.UI.Color.FromArgb(51, 255, 255, 255), 2);
            // XAML dash units are stroke-width multiples: 6 * 2 DIP = 12 DIP.
            white.StrokeDashArray = new DoubleCollection { 6, 6 }; target.Children.Add(white);
        }
        else target.Children.Add(Create(color, 4));
    }
}
