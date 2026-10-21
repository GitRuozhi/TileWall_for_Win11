using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using TileWall.Core.Grid;
using TileWall.Core.Wall;
using TileWall.Graphics;
using Windows.UI;

namespace TileWall.Views;

/// <summary>Shared projection and presentation. The caller owns all activation and drag behavior.</summary>
internal static class WallMemberVisualFactory
{
    private static ControlTemplate? _memberButtonTemplate;
    private static ControlTemplate? _memberToggleTemplate;
    private static readonly Windows.UI.ViewManagement.UISettings MotionSettings = new();

    /// <summary>Shared pointer/focus states for wall, import and gallery buttons; hit targets never move.</summary>
    internal static Action ConfigureInteraction(ButtonBase button, Grid content, CornerRadius corners,
        Action<bool>? hoverChanged = null, bool drawHoverEdge = true)
    {
        // Stock button templates add theme-dependent fills over image content.
        // Keep its activation/accessibility behavior while drawing our shared states once.
        button.Template = button is ToggleButton
            ? (_memberToggleTemplate ??= CreateInteractionTemplate("ToggleButton"))
            : (_memberButtonTemplate ??= CreateInteractionTemplate("Button"));
        button.UseSystemFocusVisuals = false;
        static ControlTemplate CreateInteractionTemplate(string targetType) => (ControlTemplate)XamlReader.Load($$"""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="{{targetType}}">
                <ContentPresenter Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}"
                    HorizontalContentAlignment="{TemplateBinding HorizontalContentAlignment}"
                    VerticalContentAlignment="{TemplateBinding VerticalContentAlignment}"
                    Padding="{TemplateBinding Padding}" CornerRadius="{TemplateBinding CornerRadius}" Background="Transparent" />
            </ControlTemplate>
            """);
        var stateCue = new Border
        {
            BorderThickness = new Thickness(2), CornerRadius = corners,
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
            Opacity = 0, IsHitTestVisible = false,
        };
        var pressedCue = new Border
        {
            CornerRadius = corners, Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
            Opacity = 0, IsHitTestVisible = false,
        };
        content.Children.Add(pressedCue);
        content.Children.Add(stateCue);
        var hoverBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
        var focusBrush = new SolidColorBrush(WallSurface.SystemAccentColor());
        var hovering = false;
        var keyboardFocus = false;
        var pointerPressed = false;
        var keyPressed = false;
        var displayedHover = false;
        var cueAnimations = new Dictionary<Border, Storyboard>();
        var cueTargets = new Dictionary<Border, double>();

        void SetCue(Border cue, bool visible, bool immediate)
        {
            var target = visible ? 1d : 0d;
            if (!immediate && cueTargets.TryGetValue(cue, out var previousTarget) && previousTarget == target) return;
            cueTargets[cue] = target;
            // XAML owns both the base value and the animation. Never acquire a
            // CompositionVisual during initialization, reset, or window teardown.
            var current = cue.Opacity;
            if (cueAnimations.Remove(cue, out var previous)) previous.Stop();
            cue.Opacity = target;
            if (immediate || !button.IsLoaded || !MotionSettings.AnimationsEnabled || Math.Abs(current - target) < 0.001)
            {
                return;
            }
            var animation = new DoubleAnimation
            {
                From = current, To = target, Duration = new Duration(TimeSpan.FromMilliseconds(100)),
            };
            Storyboard.SetTarget(animation, cue);
            Storyboard.SetTargetProperty(animation, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Completed += (_, _) =>
            {
                if (!cueAnimations.TryGetValue(cue, out var active) || !ReferenceEquals(active, storyboard)) return;
                cueAnimations.Remove(cue);
                storyboard.Stop();
            };
            cueAnimations[cue] = storyboard;
            storyboard.Begin();
        }
        void Update(bool immediate = false)
        {
            var enabled = button.IsEnabled;
            var hover = enabled && hovering;
            stateCue.BorderBrush = keyboardFocus ? focusBrush : hoverBrush;
            SetCue(stateCue, enabled && (keyboardFocus || drawHoverEdge && hover), immediate);
            SetCue(pressedCue, enabled && (pointerPressed || keyPressed), immediate);
            content.Opacity = enabled ? 1 : 0.45;
            if (displayedHover == hover) return;
            displayedHover = hover;
            hoverChanged?.Invoke(hover);
        }
        void Reset()
        {
            hovering = keyboardFocus = pointerPressed = keyPressed = false;
            Update(immediate: true);
        }
        button.PointerEntered += (_, _) => { hovering = true; Update(); };
        button.PointerExited += (_, _) => { hovering = pointerPressed = false; Update(); };
        button.GotFocus += (_, _) => { keyboardFocus = button.FocusState == FocusState.Keyboard; Update(); };
        button.LostFocus += (_, _) => { keyboardFocus = keyPressed = false; Update(); };
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) pointerPressed = true;
            Update();
        }), true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) =>
        {
            pointerPressed = false;
            Update();
        }), true);
        button.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => Reset()), true);
        button.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, e) =>
        {
            pointerPressed = false;
            // Button normally releases capture after PointerReleased. Retain the
            // real hover in that case; an interrupted press clears its hover cue.
            if (e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) hovering = false;
            Update();
        }), true);
        button.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Space or Windows.System.VirtualKey.Enter)
            {
                keyPressed = true;
                Update();
            }
        }), true);
        button.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Space or Windows.System.VirtualKey.Enter)
            {
                keyPressed = false;
                Update();
            }
        }), true);
        button.IsEnabledChanged += (_, _) => Reset();
        button.Unloaded += (_, _) => Reset();
        button.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
        {
            if (button.Visibility != Visibility.Visible) Reset();
        });
        Update(immediate: true);
        return Reset;
    }

    internal static PuzzleMember Project(WallMember member) => member as PuzzleMember ?? new PuzzleMember
    {
        Id = member.Id, LocalBounds = member.LocalBounds, Visual = member.Visual, Entry = member.Entry,
        TargetInput = member.TargetInput, Images = member.Images, ImageDisplay = member.ImageDisplay,
        RotationInterval = member.RotationInterval, Carousel = member.Carousel,
        ImportSource = member.ImportSource, UserClaimed = member.UserClaimed,
        Partitions = [new GridRect(0, 0, member.LocalBounds.Width, member.LocalBounds.Height)],
    };

    internal static PuzzleCanvasView Create(WallMember member, PuzzleFrame? frame, WallMetrics metrics,
        double scale, WallVisualStyle style, Action<Button, int>? partitionCreated = null, bool interactive = true,
        bool externalInteraction = false)
    {
        var view = new PuzzleCanvasView { Interactive = interactive, ExternalInteraction = externalInteraction };
        if (partitionCreated is not null) view.PartitionCreated += partitionCreated;
        view.SetScene(Project(member), frame, metrics, scale, style);
        return view;
    }

    internal static (Border Outline, Border Check) AddSelection(Grid host, WallVisualStyle style)
    {
        var accent = new SolidColorBrush(WallSurface.SystemAccentColor());
        var outline = new Border { BorderThickness = new Thickness(2), BorderBrush = accent,
            CornerRadius = WallCornerStyle.Corners(style), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        var mark = ActionIcon.Glyph("\uE73E", 12);
        mark.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        var check = new Border { Width = 20, Height = 20, Background = accent, Child = mark,
            CornerRadius = new CornerRadius(WallCornerStyle.Radius(style), 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        host.Children.Add(outline); host.Children.Add(check);
        return (outline, check);
    }
}

internal static class WallGroupVisualFactory
{
    internal static TextBlock Label(string? title, double width, double height) => new()
    {
        Text = title ?? string.Empty, Width = Math.Max(0, width - 5), Height = height, FontSize = 14,
        VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    internal static DataTemplate HeaderTemplate(WallMetrics metrics, string? tag = null) => (DataTemplate)XamlReader.Load($$"""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <TextBlock Text="{Binding Title}" Tag="{{tag ?? string.Empty}}" Width="{{metrics.LaneWidth - 5}}" Height="{{metrics.TitleInteractionHeight}}" Margin="5,{{metrics.TitleHeight - metrics.TitleInteractionHeight}},0,{{metrics.TitleToMembersGap}}"
                FontSize="14" VerticalAlignment="Center" TextWrapping="NoWrap" TextTrimming="CharacterEllipsis" />
        </DataTemplate>
        """);

    internal static Border Outline(WallVisualStyle style, bool dark) => new()
    {
        BorderThickness = new Thickness(0), CornerRadius = WallCornerStyle.Corners(style),
        Background = new SolidColorBrush(dark ? Color.FromArgb(51, 255, 255, 255) : Color.FromArgb(51, 0, 0, 0)),
        IsHitTestVisible = false, Visibility = Visibility.Collapsed,
    };
}
