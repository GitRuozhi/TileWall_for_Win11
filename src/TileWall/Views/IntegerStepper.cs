using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using TileWall.Resources;

namespace TileWall.Views;

/// <summary>One integer input, including its keyboard and adjacent decrement/increment actions.</summary>
internal sealed partial class IntegerStepper : UserControl
{
    internal const double ControlWidth = 100;
    private readonly TextBox _input = new() { Width = 38, Height = 30, MinHeight = 0, MinWidth = 0, CornerRadius = new CornerRadius(0), Padding = new Thickness(2, 4, 2, 4),
        TextAlignment = TextAlignment.Center, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), IsSpellCheckEnabled = false };
    private readonly RepeatButton _minus = new() { Width = 30, Height = 30, MinWidth = 0, Padding = new Thickness(4), Delay = 350, Interval = 65, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) },
        _plus = new() { Width = 30, Height = 30, MinWidth = 0, Padding = new Thickness(4), Delay = 350, Interval = 65, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
    private readonly Border _frame = VisualStyles.InputFrame();
    private int _value = 1, _minimum = 1, _maximum = 256;
    private bool _sync;
    private string _decrease = string.Empty, _increase = string.Empty;
    internal event Action<int>? ValueChanged;
    internal int Minimum { get => _minimum; set { _minimum = value; Sync(); } }
    internal int Maximum { get => _maximum; set { _maximum = value; Sync(); } }
    internal int Value { get => _value; set { if (value == _value && HasPendingInput) return; _value = value; Sync(); } }
    internal bool IsInputValid => int.TryParse(_input.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) && value >= Minimum && value <= Maximum;
    internal bool HasPendingInput => !int.TryParse(_input.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) || value != _value;

    internal IntegerStepper()
    {
        Width = _frame.Width = ControlWidth;
        _frame.Height = 32;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _minus.Content = ActionIcon.Glyph(ActionIcon.Remove, 12); _plus.Content = ActionIcon.Glyph(ActionIcon.Add, 12);
        row.Children.Add(_minus); row.Children.Add(_input); row.Children.Add(_plus); _frame.Child = row; Content = _frame;
        _minus.Click += (_, _) => Step(-1); _plus.Click += (_, _) => Step(1);
        _input.TextChanged += (_, _) =>
        {
            if (_sync) return;
            // A multi-digit edit is one pending value. Publishing its first digit
            // would destructively resize a puzzle before the user finishes typing.
            UpdateButtons();
            _input.BorderThickness = IsInputValid ? new Thickness(0) : new Thickness(1);
            _input.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
        };
        _input.LostFocus += (_, _) => CommitPendingInput();
        // TextBox can handle navigation keys before its normal KeyDown subscribers.
        // Observe the handled route as well so both arrows reach the same range gate.
        _input.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key is VirtualKey.Up or VirtualKey.Down) { Step(e.Key == VirtualKey.Up ? 1 : -1); e.Handled = true; }
            else if (e.Key == VirtualKey.Enter) { CommitPendingInput(); e.Handled = true; }
        }), true);
        Sync();
    }
    internal void SetNames(string name, string decrease, string increase)
    {
        _decrease = decrease; _increase = increase;
        AutomationProperties.SetName(_input, name); AutomationProperties.SetName(_minus, decrease); AutomationProperties.SetName(_plus, increase);
        ToolTipService.SetToolTip(_input, $"{name} ({Minimum}–{Maximum})");
        UpdateButtons();
    }
    internal bool CommitPendingInput()
    {
        if (!IsInputValid || !int.TryParse(_input.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value)) return false;
        Apply(value); return true;
    }
    private void Step(int delta) { if (CommitPendingInput()) Apply(_value + delta); }
    private void Apply(int value)
    {
        if (value < Minimum || value > Maximum) return;
        var changed = _value != value; _value = value; Sync();
        if (changed) ValueChanged?.Invoke(value);
    }
    private void Sync()
    {
        _sync = true; _input.Text = _value.ToString(CultureInfo.CurrentCulture); _sync = false;
        _input.BorderThickness = IsInputValid ? new Thickness(0) : new Thickness(1);
        _input.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        var valid = int.TryParse(_input.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) && value >= Minimum && value <= Maximum;
        _minus.IsEnabled = valid && value > Minimum; _plus.IsEnabled = valid && value < Maximum;
        ActionIcon.SetTip(_minus, !valid ? ProductStrings.Get("Common.CheckInput") : _minus.IsEnabled ? _decrease : string.Format(ProductStrings.Get("Common.MinimumReached"), Minimum));
        ActionIcon.SetTip(_plus, !valid ? ProductStrings.Get("Common.CheckInput") : _plus.IsEnabled ? _increase : string.Format(ProductStrings.Get("Common.MaximumReached"), Maximum));
    }
}
