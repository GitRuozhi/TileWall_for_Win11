using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Specialized;
using TileWall.Core.Grid;
using Windows.Foundation;

namespace TileWall.Views;

/// <summary>Realizes existing shared geometry; makes no placement or sizing decisions.</summary>
internal sealed class ImportPreviewLayout : VirtualizingLayout
{
    private IReadOnlyList<DipRect> _bounds = [];
    private Size _extent;
    private readonly Dictionary<int, List<int>> _bands = [];
    private readonly Dictionary<int, UIElement> _realized = [];
    private VirtualizingLayoutContext? _context;
    private const double BandHeight = 256;

    internal Action<UIElement, int>? ElementRealized { get; set; }

    protected override void InitializeForContextCore(VirtualizingLayoutContext context) => _context = context;

    protected override void UninitializeForContextCore(VirtualizingLayoutContext context)
    {
        RecycleRealized(context);
        _context = null;
    }

    protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object source, NotifyCollectionChangedEventArgs args)
    {
        // ItemsRepeater recycles removed items. Preserve the surviving layout
        // references with the same index changes as its stable collection.
        var previous = _realized.ToArray();
        _realized.Clear();
        foreach (var (index, element) in previous)
        {
            var next = index;
            switch (args.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    if (index >= args.NewStartingIndex) next += args.NewItems!.Count;
                    break;
                case NotifyCollectionChangedAction.Remove:
                    if (index >= args.OldStartingIndex && index < args.OldStartingIndex + args.OldItems!.Count) continue;
                    if (index >= args.OldStartingIndex) next -= args.OldItems!.Count;
                    break;
                default:
                    continue;
            }
            _realized[next] = element;
        }
        InvalidateMeasure();
    }

    internal void SetScene(IReadOnlyList<DipRect> bounds, double width, double height, bool preserveElements = false)
    {
        // Recycle while the previous ItemsSource and its indices still exist.
        if (!preserveElements && _context is { } context) RecycleRealized(context);
        _bounds = bounds; _extent = new Size(Math.Max(1, width), Math.Max(1, height));
        _bands.Clear();
        for (var index = 0; index < bounds.Count; index++)
        {
            var box = bounds[index];
            for (var band = (int)Math.Floor(box.Y / BandHeight); band <= (int)Math.Floor((box.Y + box.Height) / BandHeight); band++)
            {
                if (!_bands.TryGetValue(band, out var items)) _bands.Add(band, items = []);
                items.Add(index);
            }
        }
        InvalidateMeasure();
    }

    private void RecycleRealized(VirtualizingLayoutContext context)
    {
        var elements = _realized.Values.ToArray();
        _realized.Clear();
        foreach (var element in elements) context.RecycleElement(element);
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var viewport = context.RealizationRect;
        var wanted = new HashSet<int>();
        if (double.IsFinite(viewport.Width) && double.IsFinite(viewport.Height) &&
            double.IsFinite(viewport.X) && double.IsFinite(viewport.Y))
        {
            var start = Math.Max(0, (int)Math.Floor(viewport.Y / BandHeight));
            var end = (int)Math.Floor((viewport.Y + viewport.Height) / BandHeight);
            for (var band = start; band <= end; band++)
                if (_bands.TryGetValue(band, out var items))
                    foreach (var index in items)
                    {
                        var box = _bounds[index];
                        if (index < context.ItemCount && box.X < viewport.Right && box.X + box.Width > viewport.Left &&
                            box.Y < viewport.Bottom && box.Y + box.Height > viewport.Top) wanted.Add(index);
                    }
        }
        foreach (var index in _realized.Keys.Where(index => !wanted.Contains(index)).ToArray())
        {
            context.RecycleElement(_realized[index]); _realized.Remove(index);
        }
        foreach (var index in wanted.Order())
        {
            var element = context.GetOrCreateElementAt(index);
            _realized[index] = element;
            // GetOrCreate may return an existing element without ElementPrepared.
            ElementRealized?.Invoke(element, index);
            var box = _bounds[index]; element.Measure(new Size(box.Width, box.Height));
        }
        return _extent;
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        foreach (var (index, element) in _realized)
        {
            if (index >= _bounds.Count || index >= context.ItemCount) continue;
            var box = _bounds[index]; element.Arrange(new Rect(box.X, box.Y, box.Width, box.Height));
        }
        return _extent;
    }
}
