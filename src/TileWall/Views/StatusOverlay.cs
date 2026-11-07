using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Windows.Foundation;

namespace TileWall.Views;

/// <summary>A local notice that overlays its host without contributing to its desired size.</summary>
internal sealed class StatusOverlay : Panel
{
    private const double Inset = 8;
    private const int DisplayMilliseconds = 4000;
    private readonly Border _card;
    private readonly TextBlock _text;
    private readonly DispatcherQueueTimer _timer;
    private readonly bool _top;
    private readonly FrameworkElement? _actions;
    private string? _lastTransient;
    private long _expiresAt;

    internal StatusOverlay(VerticalAlignment alignment = VerticalAlignment.Bottom, FrameworkElement? actions = null)
    {
        _top = alignment == VerticalAlignment.Top;
        _actions = actions;
        _card = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                Padding="8,5" Background="{ThemeResource TileWallNoticeBackgroundBrush}" Visibility="Collapsed" />
            """);
        _text = (TextBlock)XamlReader.Load("""
            <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                Foreground="{ThemeResource TileWallNoticeTextBrush}" TextWrapping="Wrap" />
            """);
        if (actions is null)
        {
            _text.MaxLines = _top ? 6 : 4;
            _text.TextTrimming = TextTrimming.CharacterEllipsis;
            _card.Child = _text;
        }
        else
        {
            var content = new StackPanel { Spacing = 4 };
            content.Children.Add(new ScrollViewer { Content = _text, MaxHeight = 180,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            content.Children.Add(actions);
            _card.Child = content;
        }
        _card.IsHitTestVisible = false;
        Children.Add(_card);
        _timer = DispatcherQueue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += OnTick;
        Unloaded += (_, _) => { if (HasTransient) Clear(); };
    }

    internal string Text => _text.Text;
    internal bool HasTransient => _expiresAt != 0;
    internal event Action? TransientExpired;

    internal void SetActionsVisible(bool visible)
    {
        if (_actions is null) return;
        _actions.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _card.IsHitTestVisible = visible;
    }

    internal void Show(string? message, bool transient = false, bool renew = false)
    {
        if (string.IsNullOrEmpty(message)) { Clear(); return; }
        if (transient && !renew && _lastTransient == message) return;
        _timer.Stop();
        _lastTransient = transient ? message : null;
        _expiresAt = transient ? Environment.TickCount64 + DisplayMilliseconds : 0;
        _text.Text = message;
        _card.Visibility = Visibility.Visible;
        if (transient)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(DisplayMilliseconds);
            _timer.Start();
        }
    }

    internal void Clear()
    {
        _lastTransient = null;
        Dismiss();
    }

    private void Dismiss()
    {
        _timer.Stop();
        _expiresAt = 0;
        _text.Text = string.Empty;
        _card.Visibility = Visibility.Collapsed;
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (!HasTransient) return;
        var remaining = _expiresAt - Environment.TickCount64;
        if (remaining > 0)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(remaining);
            _timer.Start();
            return;
        }
        Dismiss();
        TransientExpired?.Invoke();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _card.Measure(new Size(Math.Min(560, Math.Max(0, availableSize.Width - 2 * Inset)),
            Math.Max(0, availableSize.Height - 2 * Inset)));
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = Math.Min(_card.DesiredSize.Width, Math.Max(0, finalSize.Width - 2 * Inset));
        var height = Math.Min(_card.DesiredSize.Height, Math.Max(0, finalSize.Height - 2 * Inset));
        _card.Arrange(new Rect(_top ? Inset : (finalSize.Width - width) / 2,
            _top ? Inset : Math.Max(0, finalSize.Height - height - Inset), width, height));
        return finalSize;
    }
}
