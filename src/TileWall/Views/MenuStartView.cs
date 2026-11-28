using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Application;
using TileWall.Core.Imaging;
using TileWall.Core.Shell;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;

namespace TileWall.Views;

internal sealed class MenuStartView
{
    private readonly WallSurface _surface;
    private readonly WallImageService _images;
    private readonly Action<string> _launch;
    private readonly Func<double> _scale;
    private readonly Func<bool> _canInteract;
    private readonly MenuGroupExpansion _menuExpansion;
    private readonly Action<IReadOnlyList<string>>? _expansionChanged;
    private readonly Dictionary<string, List<Image>> _menuIcons = new(StringComparer.Ordinal);
    private readonly List<Action> _menuStringRefreshers = [];
    private sealed class MenuImageRequest(PuzzleFrame frame, int edge)
    {
        public PuzzleFrame Frame { get; } = frame;
        public int Edge { get; } = edge;
        public ImageSource? Display;
    }
    private readonly Dictionary<string, MenuImageRequest> _menuImageRequests = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _menuImageLoads = [];
    private CancellationTokenSource _menuImageCancellation = new();
    private double _verticalOffset;
    private bool _stopped;

    internal MenuStartView(WallSurface surface, WallImageService images, Action<string> launch,
        Func<double> scale, Func<bool> canInteract, MenuGroupExpansion expansion,
        Action<IReadOnlyList<string>>? expansionChanged = null)
    {
        _surface = surface;
        _images = images;
        _launch = launch;
        _scale = scale;
        _canInteract = canInteract;
        _menuExpansion = expansion;
        _expansionChanged = expansionChanged;
        AutomationProperties.SetAutomationId(Element, "menu-start-list");
    }

    internal ScrollViewer Element { get; } = new()
    {
        Width = WallViewportPolicy.MenuWidthDip, HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Stretch, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 8, 0, 8),
        Visibility = Visibility.Collapsed,
    };
    internal IReadOnlyList<string> ExpandedGroups => _menuExpansion.Expanded;
    internal event Action? RowsChanged;

    internal void Position(AnchorCorner anchor, double width = WallViewportPolicy.MenuWidthDip)
    {
        var right = anchor is AnchorCorner.TopRight or AnchorCorner.BottomRight;
        Element.Width = width;
        Element.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Element.Margin = right ? new Thickness(0, 0, WallViewportPolicy.SidebarWidthDip, 0)
            : new Thickness(WallViewportPolicy.SidebarWidthDip, 0, 0, 0);
    }

    internal void Render(WallDocument document, bool active)
    {
        if (_stopped) return;
        var preserveOffset = _surface.IsSecondaryView;
        if (preserveOffset && Element.Content is not null) _verticalOffset = Element.VerticalOffset;
        Element.Visibility = document.Settings.MenuStart ? Visibility.Visible : Visibility.Collapsed;
        _menuImageCancellation.Cancel(); _menuImageCancellation.Dispose();
        _menuImageCancellation = new();
        _menuImageRequests.Clear();
        _menuIcons.Clear();
        _menuStringRefreshers.Clear();
        if (_menuExpansion.Retain(document.Lanes.SelectMany(lane => lane.Groups).Select(group => group.Id))) _expansionChanged?.Invoke(_menuExpansion.Expanded);
        if (!document.Settings.MenuStart || !active) { Element.Content = null; return; }
        var panel = new StackPanel { Spacing = 0 };
        var updateExpansions = new List<Action>();
        var unnamedIndex = 0;
        foreach (var group in document.Lanes.SelectMany(lane => lane.Groups))
        {
            var entries = MemberLayoutOrder.Order(group.Members).Where(member => member.Entry is not null).ToArray();
            if (entries.Length == 0) continue;
            var unnamedNumber = string.IsNullOrWhiteSpace(group.Title) ? ++unnamedIndex : 0;
            var groupTitle = unnamedNumber != 0
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    ProductStrings.Get("MenuStart.Unnamed"), unnamedNumber)
                : group.Title;
            var header = new Grid { Height = 32 };
            header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new() { Width = new GridLength(32) });
            var title = new TextBlock
            {
                Text = groupTitle, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            AutomationProperties.SetAutomationId(title, "menu-group-title-" + group.Id);
            header.Children.Add(title);
            var chevron = new FontIcon { FontSize = 12 };
            Grid.SetColumn(chevron, 1); header.Children.Add(chevron);
            var toggle = new Button
            {
                Content = header, Padding = new Thickness(0), BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            AutomationProperties.SetAutomationId(toggle, "menu-group-toggle-" + group.Id);
            toggle.Click += (_, _) =>
            {
                if (!_canInteract()) return;
                _menuExpansion.Toggle(group.Id);
                _expansionChanged?.Invoke(_menuExpansion.Expanded);
                var offset = Element.VerticalOffset;
                // Keep the pressed/focused header in the live tree. Replacing the
                // whole menu here can relocate focus into the adjacent rail.
                foreach (var update in updateExpansions) update();
                Element.UpdateLayout();
                Element.ChangeView(null, Math.Min(offset, Element.ScrollableHeight), null, true);
                RowsChanged?.Invoke();
            };
            panel.Children.Add(toggle);
            var entryPanel = new StackPanel { Spacing = 0 };
            panel.Children.Add(entryPanel);
            bool? displayedExpanded = null;
            void RefreshExpansionText()
            {
                var action = ProductStrings.Get(_menuExpansion.Contains(group.Id) ? "MenuStart.Collapse" : "MenuStart.Expand");
                AutomationProperties.SetName(toggle, action + " " + groupTitle);
                ToolTipService.SetToolTip(toggle, action);
            }
            _menuStringRefreshers.Add(() =>
            {
                // Only generated group names are translated; saved user titles stay intact.
                if (unnamedNumber != 0)
                    title.Text = groupTitle = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        ProductStrings.Get("MenuStart.Unnamed"), unnamedNumber);
                RefreshExpansionText();
            });
            void UpdateExpansion()
            {
                var expanded = _menuExpansion.Contains(group.Id);
                if (displayedExpanded == expanded) return;
                displayedExpanded = expanded;
                chevron.Glyph = expanded ? "\uE70E" : "\uE70D";
                RefreshExpansionText();
                foreach (var member in entries) _menuIcons.Remove(member.Id);
                entryPanel.Children.Clear();
                if (!expanded) return;
                foreach (var member in entries)
                {
                    var content = new Grid { Margin = new Thickness(16, 0, 0, 0) };
                    content.ColumnDefinitions.Add(new() { Width = new GridLength(44) });
                    content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                    var icon = new Image { Width = 24, Height = 24, Stretch = Stretch.Uniform,
                        Source = _surface.ResolvePuzzleFrame?.Invoke(member.Id)?.Sharp };
                    content.Children.Add(icon);
                    if (!_menuIcons.TryGetValue(member.Id, out var icons)) _menuIcons[member.Id] = icons = [];
                    icons.Add(icon);
                    UpdateFrame(member.Id, _surface.ResolvePuzzleFrame?.Invoke(member.Id));
                    var label = new TextBlock { Text = WallMemberVisualFactory.ResolveTitle(member),
                        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(0, 0, 12, 0) };
                    Grid.SetColumn(label, 1); content.Children.Add(label);
                    var row = new Button { Content = content, Height = 36, Padding = new Thickness(0),
                        BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
                        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
                    if (!_surface.IsSecondaryView) _surface.AttachMemberContextMenu(row, member);
                    var id = member.Id;
                    row.Click += (_, _) => { if (_canInteract()) _launch(id); };
                    AutomationProperties.SetAutomationId(row, "menu-member-" + id);
                    AutomationProperties.SetName(row, label.Text);
                    WallMemberVisualFactory.SetTargetTooltip(row, member, command: _surface.ResolveTargetCommand?.Invoke(member.Id));
                    entryPanel.Children.Add(row);
                }
            }
            updateExpansions.Add(UpdateExpansion);
            UpdateExpansion();
        }
        Element.Content = panel;
        RowsChanged?.Invoke();
        if (preserveOffset && _verticalOffset > 0)
            Element.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!ReferenceEquals(Element.Content, panel)) return;
                Element.UpdateLayout();
                Element.ChangeView(null, Math.Min(_verticalOffset, Element.ScrollableHeight), null, true);
            });
    }

    internal void RefreshStrings()
    {
        foreach (var refresh in _menuStringRefreshers) refresh();
    }

    internal void AddVisibleMembers(HashSet<string> members)
    {
        if (Element.Visibility != Visibility.Visible || Element.ViewportHeight <= 0) return;
        const double approachMargin = 36; // One shortcut row.
        foreach (var (id, icons) in _menuIcons)
        {
            foreach (var icon in icons)
            {
                if (!icon.IsLoaded || icon.ActualHeight <= 0) continue;
                var y = icon.TransformToVisual(Element).TransformPoint(default).Y;
                if (y >= Element.ViewportHeight + approachMargin || y + icon.ActualHeight <= -approachMargin) continue;
                members.Add(id);
                break;
            }
        }
    }

    internal void UpdateFrame(string memberId, PuzzleFrame? frame)
    {
        if (_stopped || !_menuIcons.TryGetValue(memberId, out var icons)) return;
        if (frame is null) { foreach (var icon in icons) icon.Source = null; _menuImageRequests.Remove(memberId); return; }
        var edge = Math.Max(1, checked((int)Math.Ceiling(24 * _scale())));
        if (_menuImageRequests.TryGetValue(memberId, out var existing) && ReferenceEquals(existing.Frame, frame) && existing.Edge == edge)
        {
            if (existing.Display is { } display) foreach (var icon in icons) icon.Source = display;
            return;
        }
        var request = new MenuImageRequest(frame, edge);
        _menuImageRequests[memberId] = request;
        var work = PrepareMenuImageAsync(memberId, request, _menuImageCancellation.Token);
        _menuImageLoads.Add(work);
        _ = work.ContinueWith(_ => Element.DispatcherQueue.TryEnqueue(() => _menuImageLoads.Remove(work)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task PrepareMenuImageAsync(string memberId, MenuImageRequest request, CancellationToken token)
    {
        try
        {
            var result = await _images.PrepareDisplayFrameAsync(request.Frame, request.Edge, request.Edge,
                new() { Mode = ImageDisplayMode.Fit }, token);
            if (token.IsCancellationRequested || _stopped ||
                !_menuImageRequests.TryGetValue(memberId, out var current) || !ReferenceEquals(current, request)) return;
            request.Display = result.Sharp;
            if (_menuIcons.TryGetValue(memberId, out var icons)) foreach (var icon in icons) icon.Source = result.Sharp;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn("MenuImages", ex.Message); }
    }

    internal async Task ShutdownAsync()
    {
        _stopped = true;
        _menuImageCancellation.Cancel();
        _menuImageCancellation.Dispose();
        Element.Content = null;
        _menuIcons.Clear();
        _menuStringRefreshers.Clear();
        _menuImageRequests.Clear();
        if (_menuImageLoads.Count != 0) await Task.WhenAll(_menuImageLoads.ToArray());
        _menuImageLoads.Clear();
    }
}
