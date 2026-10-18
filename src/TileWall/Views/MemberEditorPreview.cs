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
    private readonly WallMetrics _metrics = new();
    private WallMember? _member;
    private PuzzleFrame? _frame;
    private PuzzleFrame? _latestFrame;
    private double _rasterScale = 1, _cameraScale = 1, _cameraX, _cameraY, _unionX, _unionY;
    private MemberImagePlacement? _placement;
    private bool _scaling, _stroke;
    private WallVisualStyle _visualStyle;
    private double _startRadius, _startImageScale;
    private uint _scalePointerId;
    private int _scaleMoves;
    private readonly bool _traceScale = Environment.GetEnvironmentVariable("TILEWALL_PERF") == "1" ||
        Environment.GetEnvironmentVariable("TILEWALL_TRACE_INPUT") == "1";
    private Point? _previousStroke;
    private readonly HashSet<WallEdge> _strokeAffected = [];

    internal EditTool? Tool { get; set; }
    internal event Action<MemberImageDisplay>? DisplayChanged;
    internal event Action? GestureCompleted;
    internal bool IsGestureActive => _scaling || _stroke;
    internal event Action? StrokeStarted;
    internal event Action<WallEdge>? StrokeEdge;
    internal event Action? StrokeCompleted;
    internal Func<WallEdge, EditTool, StrokePreview>? PreviewStroke { get; set; }

    internal MemberEditorPreview()
    {
        Height = 640; MinHeight = 180; Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _viewport.Children.Add(_scene); Children.Add(_viewport);
        SizeChanged += (_, _) => Refresh(refitCamera: !_scaling && !_stroke);
        PointerWheelChanged += OnWheel;
        PointerPressed += OnPressed; PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCanceled += (_, _) => FinishGesture(); PointerCaptureLost += (_, _) => FinishGesture();
        PointerExited += (_, _) => { if (!_scaling && !_stroke) { _hint.Children.Clear(); SetEdgeCursor(false); } };
    }

    internal void SetScene(WallMember member, PuzzleFrame? frame, double rasterScale, WallVisualStyle visualStyle)
    {
        _member = member; _latestFrame = frame;
        if (!_scaling && !_stroke) _frame = frame;
        _rasterScale = Math.Max(0.1, rasterScale);
        _visualStyle = visualStyle;
        Refresh(refitCamera: !_scaling && !_stroke);
    }

    internal void FinishGesture(bool notifyCompleted = true)
    {
        var wasActive = IsGestureActive;
        if (_stroke) { _stroke = false; _previousStroke = null; _strokeAffected.Clear(); StrokeCompleted?.Invoke(); }
        if (_scaling) _scaling = false;
        _scalePointerId = 0;
        _frame = _latestFrame;
        SetEdgeCursor(false); Refresh(refitCamera: true);
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
            TargetInput = member.TargetInput, RotationInterval = member.RotationInterval, Carousel = member.Carousel,
        };
        _formal.SetScene(projected, _frame, _metrics, _rasterScale, _visualStyle);
        Canvas.SetLeft(_formal, -_unionX); Canvas.SetTop(_formal, -_unionY); _scene.Children.Add(_formal);
        _guides.Children.Clear(); _hint.Children.Clear();
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
        var atEdge = AtImageEdge(point.Position);
        var captured = false;
        try
        {
            if (!point.Properties.IsLeftButtonPressed || _member is null) return;
            if (Tool is not null && EdgeAt(point.Position) is { } edge)
            {
                if (!(captured = CapturePointer(e.Pointer))) return;
                _stroke = true; _strokeAffected.Clear(); _previousStroke = point.Position; StrokeStarted?.Invoke();
                var proposal = PreviewStroke?.Invoke(edge, Tool.Value);
                StrokeEdge?.Invoke(edge); if (proposal is not null) ShowAffected(proposal); e.Handled = true;
            }
            else if (Tool is null && atEdge && _placement is { } image)
            {
                if (!(captured = CapturePointer(e.Pointer))) return;
                _scaling = true; _scalePointerId = e.Pointer.PointerId; _scaleMoves = 0;
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
        var point = e.GetCurrentPoint(this).Position;
        var scaling = _scaling && e.Pointer.PointerId == _scalePointerId;
        // A normal release owns a final input sample. Cancel/loss only FinishGesture.
        if (scaling && _frame is not null)
            ChangeScale(_startImageScale * Radius(point) / _startRadius);
        if (_traceScale)
        {
            var ratio = _member is { } member && _frame is { } frame && _placement is { } placement
                ? 100 * MemberImageGeometry.CustomScaleFromImageScale(placement.Scale,
                    _metrics.WidthOf(member.LocalBounds.Width), _metrics.HeightOf(member.LocalBounds.Height),
                    frame.SourcePixels.Width, frame.SourcePixels.Height) : double.NaN;
            AppLog.Info("EditorImageGesture", $"Release pointer={e.Pointer.PointerId} scaling={scaling} moves={_scaleMoves} ratio={ratio:F4} end={point.X:F2},{point.Y:F2}");
        }
        FinishGesture(); ReleasePointerCapture(e.Pointer); e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Position;
        if (_stroke)
        {
            var previous = _previousStroke ?? point;
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(point.X - previous.X), Math.Abs(point.Y - previous.Y)) / 3));
            for (var index = 1; index <= steps; index++)
                if (EdgeAt(new Point(previous.X + (point.X - previous.X) * index / steps, previous.Y + (point.Y - previous.Y) * index / steps)) is { } edge)
                {
                    var dragProposal = Tool is { } tool ? PreviewStroke?.Invoke(edge, tool) : null;
                    StrokeEdge?.Invoke(edge);
                    if (dragProposal is not null) ShowAffected(dragProposal);
                }
            _previousStroke = point; e.Handled = true; return;
        }
        if (_scaling && _frame is not null && _member is { } member)
        {
            if (e.Pointer.PointerId != _scalePointerId) return;
            _scaleMoves++;
            ChangeScale(_startImageScale * Radius(point) / _startRadius);
            e.Handled = true; return;
        }
        _hint.Children.Clear();
        if (Tool is { } activeTool && EdgeAt(point) is { } previewEdge && PreviewStroke?.Invoke(previewEdge, activeTool) is { } proposal)
            ShowAffected(proposal);
        SetEdgeCursor(Tool is null && AtImageEdge(point));
        if (Tool is { } cursorTool && EdgeAt(point) is { } cursorEdge && PreviewStroke?.Invoke(cursorEdge, cursorTool) is { Outcome: StrokeOutcome.SizeLimit })
        { var previous = ProtectedCursor; ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.UniversalNo); previous?.Dispose(); }
    }

    private void SetEdgeCursor(bool show)
    {
        var previous = ProtectedCursor;
        ProtectedCursor = show ? InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast) : null;
        previous?.Dispose();
    }

    private void ShowAffected(StrokePreview proposal)
    {
        _hint.Children.Clear();
        if (_stroke) _strokeAffected.UnionWith(proposal.AffectedEdges);
        IEnumerable<WallEdge> edges = _stroke ? _strokeAffected : proposal.AffectedEdges;
        foreach (var edge in edges) AddEdge(_hint, edge, Microsoft.UI.Colors.Orange, dashed: false);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (Tool is not null || _frame is null || _member is null || _placement is not { } placement) return;
        var factor = Math.Pow(1.1, e.GetCurrentPoint(this).Properties.MouseWheelDelta / 120d);
        ChangeScale(placement.Scale * factor, wheel: true); e.Handled = true;
    }
    private void ChangeScale(double scale, bool wheel = false)
    {
        if (_member is not { } member || _frame is not { } frame) return;
        var width = _metrics.WidthOf(member.LocalBounds.Width); var height = _metrics.HeightOf(member.LocalBounds.Height);
        var fill = MemberImageGeometry.Calculate(width, height, frame.SourcePixels.Width, frame.SourcePixels.Height, _rasterScale,
            new MemberImageDisplay { Mode = ImageDisplayMode.Fill }).Scale;
        scale = Math.Clamp(scale, fill * MemberImageGeometry.MinimumCustomScale, fill * MemberImageGeometry.MaximumCustomScale);
        var fit = MemberImageGeometry.Calculate(width, height, frame.SourcePixels.Width, frame.SourcePixels.Height, _rasterScale, new()).Scale;
        foreach (var snap in new[] { fit, fill, 1 / _rasterScale })
            if (wheel ? Math.Abs(scale / snap - 1) <= 0.025 : Math.Abs(scale - snap) * Math.Max(frame.SourcePixels.Width, frame.SourcePixels.Height) * _rasterScale / 2 <= 7) { scale = snap; break; }
        // A meaningful preset can lie outside the editable custom interval
        // (for example Original for a 1px image in an 8x12 puzzle).
        scale = Math.Clamp(scale, fill * MemberImageGeometry.MinimumCustomScale, fill * MemberImageGeometry.MaximumCustomScale);
        DisplayChanged?.Invoke(new MemberImageDisplay { Mode = ImageDisplayMode.Custom,
            CustomScale = MemberImageGeometry.CustomScaleFromImageScale(scale, width, height, frame.SourcePixels.Width, frame.SourcePixels.Height) });
    }
    private void AddEdge(Canvas target, WallEdge edge, Windows.UI.Color color, bool dashed)
    {
        var x = edge.IsVertical ? edge.X * _metrics.Pitch - _metrics.Gap / 2 : edge.X * _metrics.Pitch;
        var y = edge.IsVertical ? edge.Y * _metrics.Pitch : edge.Y * _metrics.Pitch - _metrics.Gap / 2;
        Line Create(Windows.UI.Color stroke, double thickness) => new() { X1 = x, Y1 = y, X2 = edge.IsVertical ? x : x + _metrics.Cell,
            Y2 = edge.IsVertical ? y + _metrics.Cell : y, Stroke = new SolidColorBrush(stroke), StrokeThickness = thickness };
        if (dashed)
        {
            target.Children.Add(Create(Windows.UI.Color.FromArgb(128, 0, 0, 0), 8));
            var white = Create(Microsoft.UI.Colors.White, 4);
            // XAML dash units are stroke-width multiples: 3 * 4 DIP = 12 DIP.
            white.StrokeDashArray = new DoubleCollection { 3, 3 }; target.Children.Add(white);
        }
        else target.Children.Add(Create(color, 4));
    }
}
