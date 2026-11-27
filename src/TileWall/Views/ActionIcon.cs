using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using System.Runtime.CompilerServices;

namespace TileWall.Views;

/// <summary>One Win10-compatible vector font and size for application actions.</summary>
internal static class ActionIcon
{
    private sealed class DialogIcons { internal string Primary = Save, Secondary = Delete, Close = Cancel; }
    private static readonly ConditionalWeakTable<ContentDialog, DialogIcons> DecoratedDialogs = new();
    internal const string Save = "\uE74E", Cancel = "\uE711", Browse = "\uE8B7", Reset = "\uE777",
        Register = "\uE710", Link = "\uE71B", Settings = "\uE713", Import = "\uE8B5",
        Edit = "\uE70F", Undo = "\uE7A7", Redo = "\uE7A6", Add = "\uE710", Remove = "\uE738",
        Delete = "\uE74D", Previous = "\uE76B", Next = "\uE76C", Refresh = "\uE72C",
        OpenFile = "\uE8E5", Folder = "\uE8B7", Picture = "\uE8B9", Confirm = "\uE73E",
        PresetSmall = "\uE743", PresetMedium = "\uE744", PresetWide = "\uE745", PresetLarge = "\uE747", Tiles = "\uECA5",
        Fit = "\uE799", Fill = "\uE9A6", Original = "\uE91B", Custom = "\uE932",
        Photo2 = "\uEB9F", GallerySet = "\uF5ED", Checkbox = "\uE739", CheckedBox = "\uE73A",
        OpenFolderHorizontal = "\uED25", Share = "\uE72D",
        ImportAllMirrored = "\uEA53", PreviousItem = "\uE892", NextItem = "\uE893",
        BackgroundBlur = "\uED5B", Color = "\uE790", Draw = "\uEC87", Erase = "\uE75C", Clock = "\uE121";

    internal static FontIcon Glyph(string glyph, double size = 16) => new()
    {
        Glyph = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = size,
        IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
    };

    internal static StackPanel Label(string text, string glyph)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(Glyph(glyph));
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return content;
    }

    internal static void Set(ButtonBase button, string text, string glyph)
    {
        button.Content = Label(text, glyph);
        AutomationProperties.SetName(button, text);
        SetTip(button, text);
    }

    internal static void SetIcon(ButtonBase button, string text, string glyph, string? overlayGlyph = null)
    {
        var icon = Glyph(glyph);
        if (overlayGlyph is null) button.Content = icon;
        else
        {
            var overlay = Glyph(overlayGlyph, 10);
            overlay.HorizontalAlignment = HorizontalAlignment.Center;
            var layers = new Grid { IsHitTestVisible = false };
            layers.Children.Add(icon);
            layers.Children.Add(overlay);
            button.Content = layers;
        }
        button.MinWidth = 32; button.Padding = new Thickness(7);
        SetTip(button, text);
        AutomationProperties.SetName(button, text);
    }

    private sealed class Registration
    {
        internal readonly ToolTip Tip = new();
        internal HoverTips? Root;
        internal bool DisabledOnly;
        internal string? InlineHelp;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, Registration> RegisteredTips = new();
    private static readonly ConditionalWeakTable<UIElement, HoverTips> RootTips = new();

    internal static void SetTip(FrameworkElement owner, string? text, bool disabledOnly = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            if (RegisteredTips.TryGetValue(owner, out var previous))
            {
                previous.Tip.Content = null;
                previous.Root?.Clear(owner);
                SetInlineHelp(owner, previous, null);
            }
            ToolTipService.SetToolTip(owner, null);
            return;
        }

        if (!RegisteredTips.TryGetValue(owner, out var registration))
        {
            registration = new Registration();
            RegisteredTips.Add(owner, registration);
            owner.Loaded += (_, _) => AttachTip(owner, registration);
            owner.Unloaded += (_, _) =>
            {
                registration.Root?.Clear(owner);
                registration.Root = null;
                if (registration.Tip.IsOpen) registration.Tip.IsOpen = false;
            };
            owner.GotFocus += (_, _) =>
            {
                if (owner is Control { FocusState: FocusState.Keyboard })
                    registration.Root?.ShowForKeyboard(owner, registration);
            };
            owner.LostFocus += (_, _) => registration.Root?.ClearKeyboard(owner);
        }

        var changed = !Equals(registration.Tip.Content, text);
        registration.Tip.Content = text;
        registration.DisabledOnly = disabledOnly;
        if (owner.IsLoaded) AttachTip(owner, registration);
        else SetServiceTip(owner, registration, inline: false);
        if (changed) registration.Root?.Update(owner, registration);
    }

    private static void AttachTip(FrameworkElement owner, Registration registration)
    {
        if (owner.XamlRoot is not { Content: UIElement root } xamlRoot) return;
        var manager = RootTips.GetValue(root, static element => new HoverTips(element));
        if (!ReferenceEquals(registration.Root, manager))
        {
            registration.Root?.Clear(owner);
            if (registration.Tip.IsOpen) registration.Tip.IsOpen = false;
            registration.Root = manager;
        }

        var inline = OwnedDialogWindow.InlineToolTipsFor(xamlRoot) is not null;
        if (!inline && registration.Tip.XamlRoot != xamlRoot) registration.Tip.XamlRoot = xamlRoot;
        SetServiceTip(owner, registration, inline);
        manager.Add(owner);
    }

    private static void SetServiceTip(FrameworkElement owner, Registration registration, bool inline)
    {
        var serviceTip = inline || registration.DisabledOnly || registration.Tip.Content is null ? null : registration.Tip;
        if (!ReferenceEquals(ToolTipService.GetToolTip(owner), serviceTip))
        {
            if (registration.Tip.IsOpen) registration.Tip.IsOpen = false;
            ToolTipService.SetToolTip(owner, serviceTip);
        }
        SetInlineHelp(owner, registration, inline ? registration.Tip.Content as string : null);
    }

    private static void SetInlineHelp(FrameworkElement owner, Registration registration, string? text)
    {
        if (text == registration.InlineHelp) return;
        var current = AutomationProperties.GetHelpText(owner);
        if (string.IsNullOrEmpty(current) || current == registration.InlineHelp)
            AutomationProperties.SetHelpText(owner, text ?? string.Empty);
        registration.InlineHelp = text;
    }

    internal static void ObservePointer(UIElement root, Point? position)
    {
        if (RootTips.TryGetValue(root, out var manager)) manager.SetPointer(position);
    }

    internal static void DismissTips(UIElement root)
    {
        if (RootTips.TryGetValue(root, out var manager)) manager.Stop();
    }

    private sealed class HoverTips
    {
        private readonly UIElement _root;
        private readonly List<WeakReference<FrameworkElement>> _owners = [];
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
        private ToolTip? _activeTip;
        private FrameworkElement? _activeOwner;
        private Point? _point;
        private bool _keyboard;

        internal HoverTips(UIElement root)
        {
            _root = root;
            _timer = root.DispatcherQueue.CreateTimer();
            _timer.IsRepeating = false;
            _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Tick += (_, _) => Show();
            root.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(Move), true);
            root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Move), true);
            root.PointerExited += (_, _) =>
            {
                if (OwnedDialogWindow.InlineToolTipsFor(root.XamlRoot) is null) Stop();
            };
            root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => DismissCurrent()), true);
            root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => DismissCurrent()), true);
            root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => DismissCurrent()), true);
            if (root is FrameworkElement lifetime) lifetime.Unloaded += (_, _) => Stop();
        }

        private void Move(object sender, PointerRoutedEventArgs args)
        {
            if (args.Pointer.PointerDeviceType != PointerDeviceType.Mouse ||
                OwnedDialogWindow.InlineToolTipsFor(_root.XamlRoot) is not null) return;
            var point = args.GetCurrentPoint(_root);
            if (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed) return;
            SetPointer(point.Position);
        }

        internal void SetPointer(Point? position)
        {
            // A popup or layout can re-hit-test the stationary pointer.
            if (_point == position) return;
            if (!_keyboard && _activeOwner is { IsLoaded: true } active &&
                position is { } next && Contains(active, next))
            {
                _point = next;
                return;
            }
            Stop();
            _point = position;
            if (position.HasValue) _timer.Start();
        }

        private void DismissCurrent()
        {
            var position = _point;
            Stop();
            _point = position;
        }

        internal void Add(FrameworkElement owner)
        {
            if (!_owners.Any(reference => reference.TryGetTarget(out var existing) && ReferenceEquals(existing, owner)))
                _owners.Add(new WeakReference<FrameworkElement>(owner));
        }

        internal void Clear(FrameworkElement owner)
        {
            if (ReferenceEquals(_activeOwner, owner)) Stop();
        }

        internal void ClearKeyboard(FrameworkElement owner)
        {
            if (_keyboard) Clear(owner);
        }

        internal void Stop()
        {
            _timer.Stop();
            _point = null;
            _keyboard = false;
            _activeOwner = null;
            var activeTip = _activeTip;
            _activeTip = null;
            if (activeTip is not null) activeTip.IsOpen = false;
            OwnedDialogWindow.InlineToolTipsFor(_root.XamlRoot)?.HideControlTip();
        }

        internal void Update(FrameworkElement owner, Registration registration)
        {
            if (ReferenceEquals(_activeOwner, owner) && registration.Tip.Content is string text)
                OwnedDialogWindow.InlineToolTipsFor(_root.XamlRoot)?.ShowControlTip(owner, text);
        }

        internal void ShowForKeyboard(FrameworkElement owner, Registration registration)
        {
            if (registration.DisabledOnly || registration.Tip.Content is not string text ||
                OwnedDialogWindow.InlineToolTipsFor(_root.XamlRoot) is not { } dialog) return;
            DismissCurrent();
            _activeOwner = owner;
            _keyboard = true;
            dialog.ShowControlTip(owner, text);
        }

        private void Show()
        {
            if (_point is not { } point || _root.XamlRoot is not { } xamlRoot ||
                _root is FrameworkElement { IsLoaded: false }) return;
            var inline = OwnedDialogWindow.InlineToolTipsFor(xamlRoot);
            for (var index = _owners.Count - 1; index >= 0; index--)
            {
                if (!_owners[index].TryGetTarget(out var owner)) { _owners.RemoveAt(index); continue; }
                if (!owner.IsLoaded || owner.XamlRoot != xamlRoot ||
                    !RegisteredTips.TryGetValue(owner, out var registration) || registration.Tip.Content is not string text)
                    continue;
                var enabled = owner is not Control { IsEnabled: false };
                if (enabled && (registration.DisabledOnly || inline is null) || !Contains(owner, point)) continue;

                if (inline is not null)
                {
                    if (!OwnedDialogWindow.IsPointerWithin(owner)) continue;
                    _activeOwner = owner;
                    inline.ShowControlTip(owner, text);
                    return;
                }

                var tip = registration.Tip;
                if (!registration.DisabledOnly && !ReferenceEquals(ToolTipService.GetToolTip(owner), tip)) continue;
                if (tip.XamlRoot != xamlRoot) tip.XamlRoot = xamlRoot;
                tip.PlacementTarget = owner;
                _activeOwner = owner;
                _activeTip = tip;
                tip.IsOpen = true;
                return;
            }
        }

        private bool Contains(FrameworkElement owner, Point point)
        {
            for (DependencyObject? node = owner; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, _root)) return true;
                if (node is not FrameworkElement element) continue;
                if (element.Visibility != Visibility.Visible || !element.IsHitTestVisible) return false;
                var local = _root.TransformToVisual(element).TransformPoint(point);
                if (local.X < 0 || local.Y < 0 || local.X >= element.ActualWidth || local.Y >= element.ActualHeight) return false;
            }
            return false;
        }
    }

    internal static (string Primary, string Secondary, string Close) GetDialogIcons(ContentDialog dialog)
    {
        var icons = DecoratedDialogs.GetValue(dialog, static _ => new DialogIcons());
        return (icons.Primary, icons.Secondary, icons.Close);
    }

    internal static void DecorateDialog(ContentDialog dialog, string primary, string secondary, string close)
    {
        DecorateDialog(dialog);
        var icons = DecoratedDialogs.GetValue(dialog, static _ => new DialogIcons());
        icons.Primary = primary; icons.Secondary = secondary; icons.Close = close;
        DecorateButtons(dialog, dialog);
    }

    /// <summary>Keeps standard dialog command buttons, adding icons after the template exists.</summary>
    internal static void DecorateDialog(ContentDialog dialog)
    {
        if (!DecoratedDialogs.TryGetValue(dialog, out _))
        {
            DecoratedDialogs.Add(dialog, new DialogIcons());
            dialog.Opened += (_, _) => DecorateButtons(dialog, dialog);
        }
        DecorateButtons(dialog, dialog);
    }

    private static void DecorateButtons(DependencyObject parent, ContentDialog dialog)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ButtonBase button && VisualTreeHelper.GetParent(button) is FrameworkElement { Name: "CommandSpace" })
            {
                var text = button.Name switch
                {
                    "PrimaryButton" => dialog.PrimaryButtonText,
                    "SecondaryButton" => dialog.SecondaryButtonText,
                    "CloseButton" => dialog.CloseButtonText,
                    _ => null,
                };
                var icons = DecoratedDialogs.GetValue(dialog, static _ => new DialogIcons());
                var glyph = button.Name switch { "PrimaryButton" => icons.Primary, "SecondaryButton" => icons.Secondary, _ => icons.Close };
                if (!string.IsNullOrWhiteSpace(text)) Set(button, text, glyph);
            }
            DecorateButtons(child, dialog);
        }
    }

}
