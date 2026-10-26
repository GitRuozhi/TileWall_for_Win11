using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Components;
using TileWall.Core.Grid;
using TileWall.Core.Wall;
using Windows.Foundation;

namespace TileWall.Views;

/// <summary>The wall and source preview share one clock body and the member title plane.</summary>
internal sealed class ClockMemberView : Grid
{
    private readonly ClockMember _member;
    private readonly WallMetrics _metrics;
    private readonly double _scale;
    private readonly WallVisualStyle _style;
    private readonly TextBlock _time = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _date = new() { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly PuzzleCanvasView _title;
    private readonly double _textWidth, _textHeight;
    private (string Time, string Date, string Culture)? _last;

    internal ClockMemberView(ClockMember member, WallMetrics metrics, double scale, WallVisualStyle style)
    {
        _member = member; _metrics = metrics; _scale = scale; _style = style;
        Width = metrics.WidthOf(member.LocalBounds.Width); Height = metrics.HeightOf(member.LocalBounds.Height);
        CornerRadius = WallCornerStyle.Corners(style);
        Clip = new RectangleGeometry { Rect = new Rect(0, 0, Width, Height) };
        // The shared member plane owns the same title viewport, hover and scrolling
        // behavior as tiles and puzzles. The clock text keeps its original wall fit.
        _title = WallMemberVisualFactory.Create(member with
        {
            Visual = member.Visual with { TitleMode = MemberTitleMode.Manual, BackgroundMode = MemberBackgroundMode.None },
        }, null, metrics, scale, style, interactive: false, externalInteraction: true);
        _title.IsHitTestVisible = false; Children.Add(_title);
        var titleSpace = member.Visual.ShowTitle && !string.IsNullOrWhiteSpace(member.Visual.Title) ? 23 : 0;
        _textWidth = Math.Max(1, Width - 8); _textHeight = Math.Max(1, Height - 8 - titleSpace);
        var stack = new StackPanel { Width = _textWidth, Margin = new Thickness(0, 0, 0, titleSpace),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 2, IsHitTestVisible = false };
        stack.Children.Add(_time); stack.Children.Add(_date); Children.Add(stack);
        ActualThemeChanged += (_, _) => RefreshAppearance();
        Unloaded += (_, _) => StopTransientFeedback();
        RefreshAppearance(); Refresh(DateTimeOffset.Now);
    }

    internal void RefreshAppearance()
    {
        var dark = ActualTheme == ElementTheme.Dark;
        var color = MemberBackgroundRules.GetColor(_member.Visual, dark);
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)Math.Round(color.A * color.Opacity), color.R, color.G, color.B));
        var light = _member.Visual.BackgroundMode == MemberBackgroundMode.Color
            ? color.R * 299 + color.G * 587 + color.B * 114 > 180_000 : !dark;
        _time.Foreground = _date.Foreground = new SolidColorBrush(light ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
    }

    internal void SetExternalHover(bool hover) => _title.SetExternalHover(hover);
    internal void StopTransientFeedback() => _title.StopTransientFeedback();
    internal ClockMemberView CloneForDrag()
    {
        var copy = new ClockMemberView(_member, _metrics, _scale, _style) { IsHitTestVisible = false };
        copy._title.RevealTitles = true; copy.SetExternalHover(false);
        return copy;
    }

    internal void Refresh(DateTimeOffset utcNow)
    {
        var lines = ClockTextFormatter.Format(utcNow, _member.TimeFormat, _member.DateFormat, CultureInfo.CurrentCulture);
        var key = (lines.TimeLine, lines.DateLine, CultureInfo.CurrentCulture.Name);
        if (_last == key) return;
        _last = key; _time.Text = lines.TimeLine; _date.Text = lines.DateLine;
        _time.FontSize = 19;
        _time.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var factor = Math.Min(1, Math.Min(_textWidth / Math.Max(1, _time.DesiredSize.Width),
            _textHeight * .45 / Math.Max(1, _time.DesiredSize.Height)));
        _time.FontSize = Math.Max(1, 19 * factor * .98);
        _time.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var dateHeight = Math.Max(1, _textHeight - _time.DesiredSize.Height - 2);
        _date.Width = _textWidth; _date.FontSize = 12;
        _date.Measure(new Size(_textWidth, double.PositiveInfinity));
        if (_date.DesiredSize.Height <= dateHeight) return;
        var lower = 1d; var upper = 12d;
        for (var step = 0; step < 8; step++)
        {
            var candidate = (lower + upper) / 2; _date.FontSize = candidate;
            _date.Measure(new Size(_textWidth, double.PositiveInfinity));
            if (_date.DesiredSize.Height <= dateHeight) lower = candidate; else upper = candidate;
        }
        _date.FontSize = lower;
    }
}
