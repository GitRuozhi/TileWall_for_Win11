using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Application;
using TileWall.Core.Configuration;
using TileWall.Core.Shell;
using TileWall.Core.Storage;
using TileWall.Core.Imaging;
using TileWall.Core.Wall;
using TileWall.Graphics;
using TileWall.Resources;
using TileWall.Views;
using Windows.Foundation;

namespace TileWall;

public sealed partial class MainWindow
{
    private readonly ScrollViewer _menuStart = new()
    {
        Width = WallViewportPolicy.MenuWidthDip, HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 8, 0, 8),
        Visibility = Visibility.Collapsed,
    };
    private MenuGroupExpansion _menuExpansion = new();
    private MenuStartStateStore? _menuStateStore;
    private Task? _menuStateWrite;
    private readonly Dictionary<string, List<Image>> _menuIcons = new(StringComparer.Ordinal);
    private bool _menuImagesActive;
    private sealed class MenuImageRequest(PuzzleFrame frame, int edge)
    {
        public PuzzleFrame Frame { get; } = frame;
        public int Edge { get; } = edge;
        public ImageSource? Display;
    }
    private readonly Dictionary<string, MenuImageRequest> _menuImageRequests = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _menuImageLoads = [];
    private CancellationTokenSource _menuImageCancellation = new();

    private void InitializeMenuStart()
    {
        AutomationProperties.SetAutomationId(_menuStart, "menu-start-list");
        WallRoot.Children.Insert(1, _menuStart);
        _puzzles.FrameChanged += UpdateMenuFrame;
        _menuStateStore = new(new LocalFileStore(), _session.DataRoot);
        try { _menuExpansion = _menuStateStore.Read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { AppLog.Warn("MenuStart", $"UI preference could not be read: {ex.GetType().Name}"); }
    }

    private void RenderMenuStart(WallDocument document)
    {
        _menuStart.Visibility = document.Settings.MenuStart ? Visibility.Visible : Visibility.Collapsed;
        _menuImageCancellation.Cancel(); _menuImageCancellation.Dispose();
        _menuImageCancellation = new();
        _menuImageRequests.Clear();
        _menuIcons.Clear();
        if (_menuExpansion.Retain(document.Lanes.SelectMany(lane => lane.Groups).Select(group => group.Id))) SaveMenuExpansion();
        if (!document.Settings.MenuStart || !_menuImagesActive) { _menuStart.Content = null; return; }
        var panel = new StackPanel { Spacing = 0 };
        var updateExpansions = new List<Action>();
        var unnamedIndex = 0;
        foreach (var group in document.Lanes.SelectMany(lane => lane.Groups))
        {
            var entries = MemberLayoutOrder.Order(group.Members).Where(member => member.Entry is not null).ToArray();
            if (entries.Length == 0) continue;
            var groupTitle = string.IsNullOrWhiteSpace(group.Title)
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    ProductStrings.Get("MenuStart.Unnamed"), ++unnamedIndex)
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
                if (_windowClosing || ShutdownStarted) return;
                _menuExpansion.Toggle(group.Id);
                SaveMenuExpansion();
                var offset = _menuStart.VerticalOffset;
                // Keep the pressed/focused header in the live tree. Replacing the
                // whole menu here can relocate focus into the adjacent rail.
                foreach (var update in updateExpansions) update();
                _menuStart.UpdateLayout();
                _menuStart.ChangeView(null, Math.Min(offset, _menuStart.ScrollableHeight), null, true);
            };
            panel.Children.Add(toggle);
            var entryPanel = new StackPanel { Spacing = 0 };
            panel.Children.Add(entryPanel);
            bool? displayedExpanded = null;
            void UpdateExpansion()
            {
                var expanded = _menuExpansion.Contains(group.Id);
                if (displayedExpanded == expanded) return;
                displayedExpanded = expanded;
                chevron.Glyph = expanded ? "\uE70E" : "\uE70D";
                var action = ProductStrings.Get(expanded ? "MenuStart.Collapse" : "MenuStart.Expand");
                AutomationProperties.SetName(toggle, action + " " + groupTitle);
                ToolTipService.SetToolTip(toggle, action);
                foreach (var member in entries) _menuIcons.Remove(member.Id);
                entryPanel.Children.Clear();
                if (!expanded) return;
                foreach (var member in entries)
                {
                    var content = new Grid { Margin = new Thickness(16, 0, 0, 0) };
                    content.ColumnDefinitions.Add(new() { Width = new GridLength(44) });
                    content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                    var icon = new Image { Width = 24, Height = 24, Stretch = Stretch.Uniform,
                        Source = _puzzles.FrameFor(member.Id)?.Sharp };
                    content.Children.Add(icon);
                    if (!_menuIcons.TryGetValue(member.Id, out var icons)) _menuIcons[member.Id] = icons = [];
                    icons.Add(icon);
                    UpdateMenuFrame(member.Id, _puzzles.FrameFor(member.Id), 1);
                    var label = new TextBlock { Text = WallMemberVisualFactory.ResolveTitle(member),
                        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(0, 0, 12, 0) };
                    Grid.SetColumn(label, 1); content.Children.Add(label);
                    var row = new Button { Content = content, Height = 36, Padding = new Thickness(0),
                        BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
                        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), ContextFlyout = Surface.CreateMemberContextMenu(member) };
                    var id = member.Id;
                    row.Click += (_, _) => LaunchMember(id);
                    AutomationProperties.SetAutomationId(row, "menu-member-" + id);
                    AutomationProperties.SetName(row, label.Text);
                    WallMemberVisualFactory.SetTargetTooltip(row, member);
                    entryPanel.Children.Add(row);
                }
            }
            updateExpansions.Add(UpdateExpansion);
            UpdateExpansion();
        }
        _menuStart.Content = panel;
        PositionMenuStart(_scrollRegion);
    }

    private void UpdateMenuFrame(string memberId, PuzzleFrame? frame, double progress)
    {
        if (_windowClosing || ShutdownStarted || !_menuIcons.TryGetValue(memberId, out var icons)) return;
        if (frame is null) { foreach (var icon in icons) icon.Source = null; _menuImageRequests.Remove(memberId); return; }
        var edge = Math.Max(1, checked((int)Math.Ceiling(24 * Scale)));
        if (_menuImageRequests.TryGetValue(memberId, out var existing) && ReferenceEquals(existing.Frame, frame) && existing.Edge == edge)
        {
            if (existing.Display is { } display) foreach (var icon in icons) icon.Source = display;
            return;
        }
        var request = new MenuImageRequest(frame, edge);
        _menuImageRequests[memberId] = request;
        var work = PrepareMenuImageAsync(memberId, request, _menuImageCancellation.Token);
        _menuImageLoads.Add(work);
        _ = work.ContinueWith(_ => DispatcherQueue.TryEnqueue(() => _menuImageLoads.Remove(work)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task PrepareMenuImageAsync(string memberId, MenuImageRequest request, CancellationToken token)
    {
        try
        {
            var result = await _images.PrepareDisplayFrameAsync(request.Frame, request.Edge, request.Edge,
                new() { Mode = ImageDisplayMode.Fit }, token);
            if (token.IsCancellationRequested || _windowClosing || ShutdownStarted ||
                !_menuImageRequests.TryGetValue(memberId, out var current) || !ReferenceEquals(current, request)) return;
            request.Display = result.Sharp;
            if (_menuIcons.TryGetValue(memberId, out var icons)) foreach (var icon in icons) icon.Source = result.Sharp;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn("MenuImages", ex.Message); }
    }

    private void SaveMenuExpansion()
    {
        if (_storageExecutor is not { } executor || _menuStateStore is not { } store) return;
        var ids = _menuExpansion.Expanded;
        _menuStateWrite = executor.ExecuteAsync(() =>
        {
            try { store.Write(ids); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { AppLog.Warn("MenuStart", $"UI preference could not be saved: {ex.GetType().Name}"); }
            return true;
        });
    }

    private async Task ShutdownMenuStartAsync()
    {
        _puzzles.FrameChanged -= UpdateMenuFrame;
        _menuImageCancellation.Cancel();
        if (_menuImageLoads.Count != 0) await Task.WhenAll(_menuImageLoads.ToArray());
        _menuImageLoads.Clear(); _menuImageRequests.Clear();
        _menuImageCancellation.Dispose();
        if (_menuStateWrite is { } write)
            try { await write; }
            catch (Exception ex) { AppLog.Warn("MenuStart", $"UI preference write did not finish: {ex.GetType().Name}"); }
        _menuIcons.Clear(); _menuStart.Content = null;
    }

    private void SetMenuImagesActive(bool active)
    {
        if (_menuImagesActive == active) return;
        _menuImagesActive = active;
        if (_hasDocument) RenderMenuStart(Document);
    }

    private void PositionMenuStart(Rect region)
    {
        if (!_hasDocument || !PresentedViewportSettings.MenuStart) return;
        var right = _sidebarAnchor is AnchorCorner.TopRight or AnchorCorner.BottomRight;
        var width = Math.Max(1, (AppWindow.Size.Width - _frameWidth) / Scale);
        var clientHeight = Math.Max(1, (AppWindow.Size.Height - _frameHeight) / Scale);
        _menuStart.Height = clientHeight;
        _menuStart.Margin = new Thickness(right ? Math.Max(0, width - WallViewportPolicy.FixedWidth(true))
            : WallViewportPolicy.SidebarWidthDip, 0, 0, 0);
    }
}
