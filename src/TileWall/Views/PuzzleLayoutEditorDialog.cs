using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TileWall.Core.Grid;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;
using Windows.Foundation;

namespace TileWall.Views;

/// <summary>A child draft page hosted by the parent's one ContentDialog.</summary>
internal sealed class PuzzleLayoutEditorDialog
{
    private readonly PartitionEditSession _session;
    private readonly PuzzleMember _parent;
    private readonly PuzzleFrame? _frame;
    private readonly WallMetrics _metrics = new();
    private readonly PuzzleCanvasView _image = new();
    private readonly Canvas _edges = new();
    private readonly Canvas _hint = new() { IsHitTestVisible = false };
    private readonly Grid _canvas = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly ComboBox _tool;
    private readonly ComboBox _direction;
    private readonly ComboBox _column;
    private readonly ComboBox _row;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _undo, _redo, _addColumn, _removeColumn, _addRow, _removeRow;
    private bool _stroke;
    private Point? _last;

    public FrameworkElement Content { get; }
    public PartitionLayout Current => _session.Current;
    public void ShowError(string message) => _status.Text = message;

    public PuzzleLayoutEditorDialog(PuzzleMember parent, PuzzleFrame? frame, double availableWidth = 640, double availableHeight = 560)
    {
        _parent = parent;
        _frame = frame;
        _session = new(parent.LocalBounds.Size, parent.Partitions);
        AutomationProperties.SetAutomationId(_status, "puzzle-layout-status");
        _tool = Choice(ProductStrings.Get("Layout.Tool"), "puzzle-layout-tool", [ProductStrings.Get("Layout.SelectTool"), ProductStrings.Get("Layout.Draw"), ProductStrings.Get("Layout.Erase")]);
        _direction = Choice(ProductStrings.Get("Layout.KeyboardEdge"), "puzzle-layout-direction", [ProductStrings.Get("Layout.Vertical"), ProductStrings.Get("Layout.Horizontal")]);
        _column = Choice(ProductStrings.Get("Layout.Column"), "puzzle-layout-column", Enumerable.Range(0, 8).Select(x => x.ToString()).ToArray());
        _row = Choice(ProductStrings.Get("Layout.Row"), "puzzle-layout-row", Enumerable.Range(0, 25).Select(x => x.ToString()).ToArray());
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(_tool);
        _undo = Button(ProductStrings.Get("Layout.Undo"), "undo", () => _session.Undo());
        _redo = Button(ProductStrings.Get("Layout.Redo"), "redo", () => _session.Redo());
        tools.Children.Add(_undo); tools.Children.Add(_redo);
        var size = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _removeColumn = Button(ProductStrings.Get("Layout.RemoveColumn"), "remove-column", () => _session.RemoveColumn());
        _addColumn = Button(ProductStrings.Get("Layout.AddColumn"), "add-column", () => _session.AddColumn());
        _removeRow = Button(ProductStrings.Get("Layout.RemoveRow"), "remove-row", () => _session.RemoveRow());
        _addRow = Button(ProductStrings.Get("Layout.AddRow"), "add-row", () => _session.AddRow());
        foreach (var b in new[] { _removeColumn, _addColumn, _removeRow, _addRow }) size.Children.Add(b);
        var keyboard = new StackPanel { Spacing = 6 };
        keyboard.Children.Add(_direction);
        var coordinates = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        coordinates.Children.Add(_column); coordinates.Children.Add(_row); keyboard.Children.Add(coordinates);
        var apply = new Button { Content = ProductStrings.Get("Layout.ApplyEdge"), VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetAutomationId(apply, "puzzle-layout-apply-edge");
        apply.Click += (_, _) =>
        {
            FinishStroke();
            if (Tool is not { } tool) { _status.Text = ProductStrings.Get("Layout.ToolRequired"); return; }
            _session.BeginStroke();
            var outcome = _session.Stroke(new(_direction.SelectedIndex == 0, _column.SelectedIndex, _row.SelectedIndex), tool);
            _session.EndStroke(); Refresh();
            if (outcome == StrokeOutcome.OutOfRange) _status.Text = ProductStrings.Get("Layout.InternalEdge");
        };
        keyboard.Children.Add(apply);
        _canvas.Children.Add(_image); _canvas.Children.Add(_edges); _canvas.Children.Add(_hint);
        AutomationProperties.SetAutomationId(_canvas, "puzzle-layout-canvas");
        _canvas.PointerPressed += OnPressed;
        _canvas.PointerMoved += OnMoved;
        _canvas.PointerReleased += (_, e) => { FinishStroke(); _canvas.ReleasePointerCapture(e.Pointer); e.Handled = true; };
        _canvas.PointerCaptureLost += (_, _) => FinishStroke();
        _canvas.PointerCanceled += (_, _) => FinishStroke();
        _canvas.PointerExited += (_, _) => { if (!_stroke) _hint.Children.Clear(); };
        _tool.SelectionChanged += (_, _) => { FinishStroke(); _hint.Children.Clear(); };
        var body = new StackPanel { Spacing = 10, Width = availableWidth };
        body.Children.Add(tools); body.Children.Add(size); body.Children.Add(_status);
        body.Children.Add(new ScrollViewer { Content = _canvas, MaxHeight = Math.Min(330, Math.Max(100, availableHeight - 190)),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(keyboard);
        Content = new ScrollViewer { Content = body, Width = availableWidth, MaxHeight = availableHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Refresh();
    }

    public void FinishStroke()
    {
        if (!_stroke) return;
        _stroke = false; _last = null;
        _session.EndStroke(); Refresh();
    }

    private EditTool? Tool => _tool.SelectedIndex switch { 1 => EditTool.Draw, 2 => EditTool.Erase, _ => null };

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas);
        if (Tool is null || !p.Properties.IsLeftButtonPressed) return;
        if (!_canvas.CapturePointer(e.Pointer)) return;
        _stroke = true; _session.BeginStroke(); _last = p.Position;
        ApplyAt(p.Position); e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas).Position;
        if (_stroke)
        {
            // Sample the whole pointer segment so fast movement cannot skip grid boundaries.
            var previous = _last ?? p;
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(p.X - previous.X), Math.Abs(p.Y - previous.Y)) / 3));
            for (var n = 1; n <= steps; n++) ApplyAt(new(previous.X + (p.X - previous.X) * n / steps, previous.Y + (p.Y - previous.Y) * n / steps));
            _last = p; e.Handled = true;
        }
        else PreviewAt(p);
    }

    private WallEdge? EdgeAt(Point p)
    {
        var size = Current.Size;
        if (p.X < 0 || p.Y < 0 || p.X >= _canvas.Width || p.Y >= _canvas.Height) return null;
        var vx = (int)Math.Round((p.X + _metrics.Gap / 2) / _metrics.Pitch);
        var hy = (int)Math.Round((p.Y + _metrics.Gap / 2) / _metrics.Pitch);
        var dx = Math.Abs(p.X - (vx * _metrics.Pitch - _metrics.Gap / 2));
        var dy = Math.Abs(p.Y - (hy * _metrics.Pitch - _metrics.Gap / 2));
        if (vx > 0 && vx < size.Columns && dx <= 7 && (dx <= dy || hy <= 0 || hy >= size.Rows))
            return new(true, vx, Math.Min(size.Rows - 1, (int)(p.Y / _metrics.Pitch)));
        if (hy > 0 && hy < size.Rows && dy <= 7)
            return new(false, Math.Min(size.Columns - 1, (int)(p.X / _metrics.Pitch)), hy);
        return null;
    }

    private void ApplyAt(Point p)
    {
        if (Tool is { } tool && EdgeAt(p) is { } edge && _session.Stroke(edge, tool) == StrokeOutcome.Applied) Refresh();
    }

    private void PreviewAt(Point p)
    {
        _hint.Children.Clear();
        if (Tool is not { } tool || EdgeAt(p) is not { } edge) return;
        var preview = _session.Preview(edge, tool);
        if (!preview.Applicable) return;
        if (preview.MergeExtent is { } rect)
        {
            var outline = new Rectangle { Width = _metrics.WidthOf(rect.Width), Height = _metrics.HeightOf(rect.Height),
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.Orange), StrokeThickness = 4,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(65, 255, 165, 0)) };
            Canvas.SetLeft(outline, rect.Column * _metrics.Pitch); Canvas.SetTop(outline, rect.Row * _metrics.Pitch);
            _hint.Children.Add(outline);
        }
        else if (preview.NewLineExtent is { } line)
        {
            var x = edge.IsVertical ? line.Column * _metrics.Pitch - _metrics.Gap / 2 : line.Column * _metrics.Pitch;
            var y = edge.IsVertical ? line.Row * _metrics.Pitch : line.Row * _metrics.Pitch - _metrics.Gap / 2;
            AddLine(_hint, x, y, edge.IsVertical ? x : x + _metrics.WidthOf(line.Width),
                edge.IsVertical ? y + _metrics.HeightOf(line.Height) : y, true, true);
        }
    }

    private void Refresh()
    {
        var size = Current.Size;
        _canvas.Width = _metrics.WidthOf(size.Columns); _canvas.Height = _metrics.HeightOf(size.Rows);
        _image.SetScene(_parent with { LocalBounds = new(0, 0, size.Columns, size.Rows), Partitions = Current.Partitions }, _frame, _metrics, 1);
        _edges.Children.Clear(); _hint.Children.Clear();
        for (var x = 1; x < size.Columns; x++)
            for (var y = 0; y < size.Rows; y++)
                AddLine(_edges, x * _metrics.Pitch - 2, y * _metrics.Pitch, x * _metrics.Pitch - 2, y * _metrics.Pitch + _metrics.Cell, Current.IsSolid(new(true, x, y)));
        for (var y = 1; y < size.Rows; y++)
            for (var x = 0; x < size.Columns; x++)
                AddLine(_edges, x * _metrics.Pitch, y * _metrics.Pitch - 2, x * _metrics.Pitch + _metrics.Cell, y * _metrics.Pitch - 2, Current.IsSolid(new(false, x, y)));
        var border = new Rectangle { Width = _canvas.Width, Height = _canvas.Height, StrokeThickness = 2, Stroke = new SolidColorBrush(Microsoft.UI.Colors.White), IsHitTestVisible = false };
        _edges.Children.Add(border);
        _status.Text = string.Format(ProductStrings.Get("Layout.Status"), size.Columns, size.Rows, Current.Partitions.Count);
        _undo.IsEnabled = _session.CanUndo; _redo.IsEnabled = _session.CanRedo;
        _removeColumn.IsEnabled = size.Columns > 2; _addColumn.IsEnabled = size.Columns < 8;
        _removeRow.IsEnabled = size.Rows > 2; _addRow.IsEnabled = size.Rows < 25;
    }

    private Button Button(string title, string id, Func<bool> action)
    {
        var button = new Button { Content = title };
        AutomationProperties.SetAutomationId(button, "puzzle-layout-" + id);
        button.Click += (_, _) => { FinishStroke(); action(); Refresh(); };
        return button;
    }

    private static ComboBox Choice(string header, string id, string[] values)
    {
        var box = new ComboBox { Header = header, MinWidth = 90 };
        foreach (var value in values) box.Items.Add(value);
        box.SelectedIndex = 0; AutomationProperties.SetAutomationId(box, id); return box;
    }

    private static void AddLine(Canvas canvas, double x1, double y1, double x2, double y2, bool solid, bool hint = false)
    {
        var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = hint ? 4 : 2,
            Stroke = new SolidColorBrush(hint ? Microsoft.UI.Colors.Orange : Microsoft.UI.Colors.White), IsHitTestVisible = false };
        if (!solid) line.StrokeDashArray = new DoubleCollection { 2, 3 };
        canvas.Children.Add(line);
    }
}

