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

/// <summary>Stateless viewports onto one shared image plane. The gaps are not hit targets.</summary>
public sealed class PuzzleCanvasView : Canvas
{
    private readonly List<FrameworkElement> _parts = [];
    private readonly List<Border> _cues = [];
    private readonly List<(Canvas Plane, double X, double Y)> _planes = [];
    private PuzzleMember? _member;
    private DipRect[] _viewports = [];
    public bool Interactive { get; set; }
    public event Action<Button, int>? PartitionCreated;

    public void SetScene(PuzzleMember member, PuzzleFrame? frame, WallMetrics metrics, double rasterScale)
    {
        Children.Clear();
        _parts.Clear();
        _cues.Clear();
        _planes.Clear();
        _member = member;
        Width = member.LocalBounds.Width * metrics.Pitch - metrics.Gap;
        Height = member.LocalBounds.Height * metrics.Pitch - metrics.Gap;
        var viewports = member.Partitions.Select(p => new DipRect(p.Column * metrics.Pitch,
            p.Row * metrics.Pitch, p.Width * metrics.Pitch - metrics.Gap,
            p.Height * metrics.Pitch - metrics.Gap)).ToArray();
        _viewports = viewports;
        for (var index = 0; index < viewports.Length; index++)
        {
            var viewport = viewports[index];
            var pixels = PuzzleSceneGeometry.ToPixelRect(viewport, rasterScale);
            var width = pixels.Width / rasterScale;
            var height = pixels.Height / rasterScale;
            var x = pixels.X / rasterScale;
            var y = pixels.Y / rasterScale;
            var content = new Grid
            {
                Width = width, Height = height,
                Background = new SolidColorBrush(WallSurface.ParseColor(member.Visual.BackgroundColor,
                    WallSurface.SystemAccentColor())),
                Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) },
            };
            var plane = new Canvas { Width = width, Height = height, IsHitTestVisible = false };
            content.Children.Add(plane);
            _planes.Add((plane, x, y));
            FrameworkElement part = content;
            if (Interactive)
            {
                var button = new Button
                {
                    Content = content, Width = width, Height = height, Padding = new Thickness(0),
                    BorderThickness = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    Tag = $"member:{member.Id}",
                };
                AutomationProperties.SetAutomationId(button, $"member-{member.Id}-partition-{index}");
                AutomationProperties.SetName(button, $"拼图：{member.Visual.Title ?? member.Id}，分块 {index + 1}");
                if (member.Visual.ShowTitle && !string.IsNullOrWhiteSpace(member.Visual.Title))
                {
                    var band = new Border
                    {
                        Height = 23, VerticalAlignment = VerticalAlignment.Bottom,
                        Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)),
                        Visibility = Visibility.Collapsed, Padding = new Thickness(5, 2, 5, 2),
                        IsHitTestVisible = false,
                        Child = new TextBlock { Text = member.Visual.Title, FontSize = 11,
                            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                            TextTrimming = TextTrimming.CharacterEllipsis },
                    };
                    content.Children.Add(band);
                    var hover = false;
                    var focus = false;
                    void Update() => band.Visibility = hover || focus ? Visibility.Visible : Visibility.Collapsed;
                    button.PointerEntered += (_, _) => { hover = true; Update(); };
                    button.PointerExited += (_, _) => { hover = false; Update(); };
                    button.GotFocus += (_, _) => { focus = true; Update(); };
                    button.LostFocus += (_, _) => { focus = false; Update(); };
                }
                part = button;
                PartitionCreated?.Invoke(button, index);
            }
            var cue = new Border { BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent), IsHitTestVisible = false };
            content.Children.Add(cue);
            _cues.Add(cue);
            SetLeft(part, x);
            SetTop(part, y);
            Children.Add(part);
            // Keep the actual Button hit rectangle fixed while its image plane flips.
            _parts.Add(content);
        }
        UpdateFrame(frame);
    }

    /// <summary>Replace only image layers so keyboard focus and fixed hit targets survive a flip.</summary>
    public void UpdateFrame(PuzzleFrame? frame)
    {
        if (_member is not { } member) return;
        PuzzleSceneGeometry? scene = null;
        if (frame is not null)
        {
            var canvas = new DipSize(Width, Height);
            var transform = member.Images.Transforms.FirstOrDefault(t =>
                StringComparer.OrdinalIgnoreCase.Equals(t.ImageId, frame.CanonicalPath))?.Transform
                ?? SharedCanvasTransform.DefaultTransform(frame.SourcePixels, canvas, FitMode.CoverFill);
            scene = PuzzleSceneGeometry.Create(canvas, frame.SourcePixels, _viewports, transform);
        }
        foreach (var (plane, x, y) in _planes)
        {
            plane.Children.Clear();
            if (frame is null || scene is null) continue;
            AddImage(plane, frame.BlurredUnderfill, frame.SourcePixels, scene.BackgroundTransform, x, y);
            AddImage(plane, frame.Sharp, frame.SourcePixels, scene.SharpTransform, x, y);
        }
    }

    public void SetDragCue(bool active, bool allowed)
    {
        Opacity = active ? 0.72 : 1;
        foreach (var cue in _cues)
            cue.BorderBrush = new SolidColorBrush(!active ? Microsoft.UI.Colors.Transparent
                : allowed ? Microsoft.UI.Colors.LimeGreen : Microsoft.UI.Colors.OrangeRed);
    }

    public void SetFlipProgress(double progress)
    {
        var scale = Math.Abs(Math.Cos(Math.PI * Math.Clamp(progress, 0, 1)));
        foreach (var part in _parts)
        {
            part.RenderTransformOrigin = new Point(0.5, 0.5);
            part.RenderTransform = new ScaleTransform { ScaleX = scale };
        }
    }

    private static void AddImage(Canvas plane, ImageSource source, PixelSize pixels,
        ImageTransform transform, double viewportX, double viewportY)
    {
        var image = new Image { Source = source, Stretch = Stretch.Fill,
            Width = pixels.Width * transform.Scale, Height = pixels.Height * transform.Scale };
        SetLeft(image, transform.OffsetX - viewportX);
        SetTop(image, transform.OffsetY - viewportY);
        plane.Children.Add(image);
    }
}
