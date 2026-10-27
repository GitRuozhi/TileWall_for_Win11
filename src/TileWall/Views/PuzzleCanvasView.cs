using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Grid;
using TileWall.Core.Imaging;
using TileWall.Core.Wall;
using TileWall.Graphics;
using Windows.Foundation;
using Windows.UI;

namespace TileWall.Views;

/// <summary>Fixed hit targets and clipped viewports onto the same complete member canvas.</summary>
public sealed class PuzzleCanvasView : Canvas
{
    private readonly List<Action> _resetInteractions = [];
    private readonly List<(Border Band, TextBlock Title, double ViewportWidth)> _externalTitles = [];
    private static readonly Windows.UI.ViewManagement.UISettings MotionSettings = new();
    private sealed class FramePlane(Canvas host, double x, double y)
    {
        public Canvas Host = host;
        public double X = x;
        public double Y = y;
        public Canvas? Previous;
        public Canvas? Next;
    }
    private readonly List<FramePlane> _planes = [];
    private PuzzleMember? _member;
    private MemberTransition? _transition;
    private DipRect[] _viewports = [];
    private double _rasterScale = 1;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _scrollTimer;
    private TextBlock? _scrollTitle;
    private double _scrollMaximum;
    private double _scrollOffset;
    private bool _scrollAtEnd;
    private DateTime _scrollStarts;
    public bool Interactive { get; set; }
    internal bool ExternalInteraction { get; set; }
    internal bool RevealTitles { get; set; }
    public event Action<Button, int>? PartitionCreated;

    internal void StopTransientFeedback()
    {
        StopScrolling();
        foreach (var title in _externalTitles)
            title.Band.Visibility = RevealTitles ? Visibility.Visible : Visibility.Collapsed;
        foreach (var reset in _resetInteractions) reset();
    }

    public PuzzleCanvasView()
    {
        Unloaded += (_, _) => StopTransientFeedback();
        ActualThemeChanged += (_, _) => RenderFrames();
    }

    public void SetScene(PuzzleMember member, PuzzleFrame? frame, WallMetrics metrics, double rasterScale,
        WallVisualStyle? visualStyle = null, bool? targetIsFolder = null)
    {
        StopTransientFeedback();
        Children.Clear();
        _resetInteractions.Clear();
        _externalTitles.Clear();
        _planes.Clear();
        _transition = null;
        _member = member;
        _rasterScale = rasterScale;
        Width = member.LocalBounds.Width * metrics.Pitch - metrics.Gap;
        Height = member.LocalBounds.Height * metrics.Pitch - metrics.Gap;
        _viewports = member.Partitions.Select(p => new DipRect(p.Column * metrics.Pitch,
            p.Row * metrics.Pitch, p.Width * metrics.Pitch - metrics.Gap,
            p.Height * metrics.Pitch - metrics.Gap)).ToArray();
        var title = MemberPresentationRules.ResolveTitle(member, targetIsFolder ?? Directory.Exists(member.TargetInput));
        for (var index = 0; index < _viewports.Length; index++)
        {
            var pixels = PuzzleSceneGeometry.ToPixelRect(_viewports[index], rasterScale);
            var width = pixels.Width / rasterScale;
            var height = pixels.Height / rasterScale;
            var x = pixels.X / rasterScale;
            var y = pixels.Y / rasterScale;
            var content = new Grid
            {
                Width = width, Height = height, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) },
                CornerRadius = WallCornerStyle.Corners(visualStyle ?? WallVisualStyle.Win10),
            };
            var plane = new Canvas { Width = width, Height = height, IsHitTestVisible = false };
            content.Children.Add(plane);
            _planes.Add(new FramePlane(plane, x, y));
            Border? band = null;
            TextBlock? label = null;
            if (member.Visual.ShowTitle && !string.IsNullOrWhiteSpace(title) && (Interactive || RevealTitles || ExternalInteraction))
            {
                label = new TextBlock { Text = title, FontSize = 11,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.None,
                    RenderTransform = new TranslateTransform(), VerticalAlignment = VerticalAlignment.Center };
                var titleViewport = new Canvas
                {
                    Width = Math.Max(0, width - 10), Height = 19,
                    Clip = new RectangleGeometry { Rect = new Rect(0, 0, Math.Max(0, width - 10), 19) },
                };
                SetTop(label, 2);
                titleViewport.Children.Add(label);
                band = new Border { Height = 23, VerticalAlignment = VerticalAlignment.Bottom,
                    Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)),
                    Visibility = RevealTitles ? Visibility.Visible : Visibility.Collapsed,
                    Padding = new Thickness(5, 2, 5, 2), IsHitTestVisible = false, Child = titleViewport };
                content.Children.Add(band);
                if (ExternalInteraction) _externalTitles.Add((band, label, width - 10));
            }
            FrameworkElement part = content;
            if (Interactive)
            {
                var button = new Button
                {
                    Content = content, Width = width, Height = height, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
                    BorderThickness = new Thickness(0), CornerRadius = content.CornerRadius,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Tag = $"member:{member.Id}",
                };
                AutomationProperties.SetAutomationId(button, $"member-{member.Id}-partition-{index}");
                AutomationProperties.SetName(button, title);
                ToolTipService.SetToolTip(button, string.IsNullOrWhiteSpace(title) ? null : title);
                _resetInteractions.Add(WallMemberVisualFactory.ConfigureInteraction(button, content, content.CornerRadius, hover =>
                {
                    if (band is not null) band.Visibility = hover ? Visibility.Visible : Visibility.Collapsed;
                    if (label is not null)
                    {
                        if (hover) StartScrolling(label, width - 10);
                        else StopScrolling(label);
                    }
                }));
                part = button;
                PartitionCreated?.Invoke(button, index);
            }
            SetLeft(part, x);
            SetTop(part, y);
            Children.Add(part);
        }
        UpdateFrame(frame);
    }

    public void UpdateFrame(PuzzleFrame? frame)
    {
        UpdateTransition(new MemberTransition(null, frame, MemberTransitionEffect.None, 1));
    }

    public void UpdateTransition(MemberTransition transition)
    {
        var rebuild = _transition is null ||
            !ReferenceEquals(_transition.PreviousFrame, transition.PreviousFrame) ||
            !ReferenceEquals(_transition.NextFrame, transition.NextFrame);
        _transition = transition;
        if (rebuild) RenderFrames();
        else ApplyTransition();
    }

    private void RenderFrames()
    {
        if (_member is not { } member) return;
        var appearance = MemberBackgroundRules.GetColor(member.Visual, ActualTheme == ElementTheme.Dark);
        var previousScene = SceneFor(_transition?.PreviousFrame);
        var nextScene = SceneFor(_transition?.NextFrame);
        foreach (var plane in _planes)
        {
            plane.Host.Children.Clear();
            if (member.Visual.BackgroundMode is MemberBackgroundMode.Default or MemberBackgroundMode.Color)
                plane.Host.Children.Add(new Border { Width = plane.Host.Width, Height = plane.Host.Height,
                    Background = new SolidColorBrush(Color.FromArgb(appearance.A, appearance.R, appearance.G, appearance.B)),
                    Opacity = appearance.Opacity });
            plane.Previous = AddFrame(plane, _transition?.PreviousFrame, previousScene, member, appearance.Opacity);
            plane.Next = AddFrame(plane, _transition?.NextFrame, nextScene, member, appearance.Opacity);
        }
        ApplyTransition();

        PuzzleSceneGeometry? SceneFor(PuzzleFrame? frame) => frame is null ? null
            : PuzzleSceneGeometry.Create(new DipSize(Width, Height), frame.SourcePixels,
                _viewports, member.ImageDisplay, _rasterScale);
    }

    private static Canvas? AddFrame(FramePlane plane, PuzzleFrame? frame, PuzzleSceneGeometry? scene,
        PuzzleMember member, double backgroundOpacity)
    {
        if (frame is null || scene is null) return null;
        // The crop always uses the complete member transform, but the animation uses
        // this fixed partition's local viewport. At rest all partitions line up again.
        var layer = new Canvas { Width = plane.Host.Width, Height = plane.Host.Height, IsHitTestVisible = false,
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, plane.Host.Width, plane.Host.Height) },
            RenderTransform = new CompositeTransform { CenterX = plane.Host.Width / 2, CenterY = plane.Host.Height / 2 } };
        if (member.Visual.BackgroundMode == MemberBackgroundMode.Blur && frame.BlurredUnderfill is { } underfill)
            AddImage(layer, underfill, frame.SourcePixels, scene.BackgroundTransform, plane.X, plane.Y, backgroundOpacity);
        AddImage(layer, frame.Sharp, frame.SourcePixels, scene.SharpTransform, plane.X, plane.Y, 1);
        plane.Host.Children.Add(layer);
        return layer;
    }

    private void ApplyTransition()
    {
        if (_transition is not { } transition) return;
        var effect = transition.PreviousFrame is null ? MemberTransitionEffect.None : transition.Effect;
        for (var index = 0; index < _planes.Count; index++)
        {
            var plane = _planes[index];
            var progress = transition.ProgressForPartition(index);
            ApplyLayer(plane.Previous, old: true, progress, plane.Host.Width, plane.Host.Height);
            ApplyLayer(plane.Next, old: false, progress, plane.Host.Width, plane.Host.Height);
        }

        void ApplyLayer(Canvas? layer, bool old, double p, double width, double height)
        {
            if (layer?.RenderTransform is not CompositeTransform transform) return;
            transform.ScaleX = transform.ScaleY = 1;
            transform.TranslateX = transform.TranslateY = 0;
            layer.Opacity = 1;
            layer.Visibility = Visibility.Visible;
            // A finished partition has one visible image plane even while later
            // partitions are still animating. This also restores transparent areas.
            if (effect == MemberTransitionEffect.None || p >= 1)
            {
                layer.Visibility = old ? Visibility.Collapsed : Visibility.Visible;
                return;
            }
            if (p <= 0)
            {
                layer.Visibility = old ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
            switch (effect)
            {
                case MemberTransitionEffect.None:
                    layer.Visibility = old ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case MemberTransitionEffect.Crossfade:
                    layer.Opacity = old ? 1 - p : p;
                    break;
                case MemberTransitionEffect.SlideLeft:
                case MemberTransitionEffect.SlideRight:
                    transform.TranslateX = (old ? -p : 1 - p) * width *
                        (effect == MemberTransitionEffect.SlideLeft ? 1 : -1);
                    break;
                case MemberTransitionEffect.SlideUp:
                case MemberTransitionEffect.SlideDown:
                    transform.TranslateY = (old ? -p : 1 - p) * height *
                        (effect == MemberTransitionEffect.SlideUp ? 1 : -1);
                    break;
                case MemberTransitionEffect.FlipHorizontal:
                case MemberTransitionEffect.FlipVertical:
                    layer.Visibility = old == (p < 0.5) ? Visibility.Visible : Visibility.Collapsed;
                    var scale = Math.Abs(Math.Cos(Math.PI * p));
                    if (effect == MemberTransitionEffect.FlipHorizontal) transform.ScaleX = scale;
                    else transform.ScaleY = scale;
                    break;
                case MemberTransitionEffect.ZoomFade:
                    layer.Opacity = old ? 1 - p : p;
                    if (!old)
                    {
                        transform.ScaleX = transform.ScaleY = 0.85 + 0.15 * p;
                    }
                    break;
            }
        }
    }

    private static void AddImage(Canvas plane, ImageSource source, PixelSize pixels,
        ImageTransform transform, double viewportX, double viewportY, double opacity)
    {
        var image = new Image { Source = source, Stretch = Stretch.Fill, Opacity = opacity,
            Width = pixels.Width * transform.Scale, Height = pixels.Height * transform.Scale };
        SetLeft(image, transform.OffsetX - viewportX);
        SetTop(image, transform.OffsetY - viewportY);
        plane.Children.Add(image);
    }

    private void StartScrolling(TextBlock title, double viewportWidth)
    {
        StopScrolling();
        if (!MotionSettings.AnimationsEnabled) return;
        title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _scrollMaximum = title.DesiredSize.Width - viewportWidth;
        if (_scrollMaximum <= 0 || viewportWidth <= 0) return;
        title.Width = title.DesiredSize.Width;
        _scrollTitle = title;
        _scrollOffset = 0;
        _scrollAtEnd = false;
        _scrollStarts = DateTime.UtcNow.AddMilliseconds(900);
        _scrollTimer ??= DispatcherQueue.CreateTimer();
        _scrollTimer.Interval = TimeSpan.FromMilliseconds(30);
        _scrollTimer.IsRepeating = true;
        _scrollTimer.Tick -= OnScrollTick;
        _scrollTimer.Tick += OnScrollTick;
        _scrollTimer.Start();
    }

    private void OnScrollTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!MotionSettings.AnimationsEnabled) { StopScrolling(); return; }
        if (_scrollTitle?.RenderTransform is not TranslateTransform transform || DateTime.UtcNow < _scrollStarts) return;
        if (_scrollAtEnd)
        {
            _scrollOffset = 0;
            _scrollAtEnd = false;
            _scrollStarts = DateTime.UtcNow.AddMilliseconds(900);
        }
        else
        {
            _scrollOffset = Math.Min(_scrollMaximum, _scrollOffset + 0.75);
            if (_scrollOffset >= _scrollMaximum)
            { _scrollAtEnd = true; _scrollStarts = DateTime.UtcNow.AddMilliseconds(900); }
        }
        transform.X = -_scrollOffset;
    }

    private void StopScrolling(TextBlock? requested = null)
    {
        if (requested is not null && !ReferenceEquals(requested, _scrollTitle)) return;
        _scrollTimer?.Stop();
        if (_scrollTitle?.RenderTransform is TranslateTransform transform) transform.X = 0;
        _scrollOffset = 0;
        _scrollMaximum = 0;
        _scrollTitle = null;
    }

    internal void SetExternalHover(bool hover)
    {
        foreach (var title in _externalTitles)
        {
            title.Band.Visibility = hover || RevealTitles ? Visibility.Visible : Visibility.Collapsed;
            if (hover) StartScrolling(title.Title, title.ViewportWidth);
            else StopScrolling(title.Title);
        }
    }
}
