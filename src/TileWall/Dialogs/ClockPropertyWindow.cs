using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Shell;

namespace TileWall.Dialogs;

/// <summary>组件属性窗运行上下文（MainWindow 注入；窗口只做「绑定显示 → 草稿 → 回调」，§2.3）。</summary>
public sealed record ClockPropertyWindowContext(
    WallGrid Wall,
    IReadOnlyList<GridRect> OtherRects,
    ClockObject? Target, // null = 创建模式
    Func<GridRect, ClockDraft, string?> SaveHandler); // 返回 null = 成功（关窗）；否则错误文本就地显示

/// <summary>
/// 时间日期组件属性窗（M8 设计 §4；创建/编辑共用同一表单，P1 §3.2 模式）：
/// 仅两字段——大小（行列 NumberBox，与磁贴同规则，无自由拉伸）与可选标题。
/// 校验经 ComponentDraftValidator（firstFit/锚定缩放/重叠/标题名），满墙无位 → SIZE_NO_FIT 就地提示、窗口不关、零写入。
/// 保存回调由 MainWindow 走纯配置路径（LayoutCommitService.Commit，零入口文件操作）。
/// </summary>
public sealed class ClockPropertyWindow : Window, IExitParticipant
{
    private const int GwlHwndParent = -8;

    private readonly ClockPropertyWindowContext _context;
    private readonly Grid _rootGrid;
    private readonly NumberBox _colsBox;
    private readonly NumberBox _rowsBox;
    private readonly TextBox _titleBox;
    private readonly InfoBar _validation;
    private readonly Button _saveButton;
    private bool _forceClose;

    public ClockPropertyWindow(ClockPropertyWindowContext context, IntPtr ownerHwnd)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        var isCreateMode = context.Target is null;
        Title = isCreateMode ? "添加时间日期" : "编辑时间日期";

        _colsBox = new NumberBox
        {
            Minimum = 1,
            Maximum = WallGrid.TileMaxCells,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 88,
        };
        _rowsBox = new NumberBox
        {
            Minimum = 1,
            Maximum = Math.Max(context.Wall.Rows, 1),
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 88,
        };
        AutomationProperties.SetAutomationId(_colsBox, "clock-size-cols");
        AutomationProperties.SetAutomationId(_rowsBox, "clock-size-rows");

        _titleBox = new TextBox { PlaceholderText = "留空 = 隐藏标题", Width = 430 };
        AutomationProperties.SetAutomationId(_titleBox, "clock-title");

        _validation = new InfoBar { IsOpen = false, Severity = InfoBarSeverity.Warning, IsClosable = false };
        AutomationProperties.SetAutomationId(_validation, "clock-validation");

        _saveButton = new Button { Content = "保存" };
        AutomationProperties.SetAutomationId(_saveButton, "clock-save");
        _saveButton.Click += (_, _) => _ = ((IExitParticipant)this).TrySaveNow();
        var cancelButton = new Button { Content = "取消" };
        AutomationProperties.SetAutomationId(cancelButton, "clock-cancel");
        cancelButton.Click += (_, _) => Close();

        Content = _rootGrid = BuildLayout(isCreateMode, cancelButton);

        _colsBox.ValueChanged += (_, _) => ValidateForm();
        _rowsBox.ValueChanged += (_, _) => ValidateForm();
        _titleBox.TextChanged += (_, _) => ValidateForm();

        var target = context.Target;
        _colsBox.Value = target?.Bounds.Width ?? 2; // 创建默认 2×2（§3.2：一行大字一行小字的可读最小形）
        _rowsBox.Value = target?.Bounds.Height ?? 2;
        _titleBox.Text = target?.Visual.TitleText ?? string.Empty;
        ValidateForm();

        ConfigureWindowShape(ownerHwnd);
        AppWindow.Closing += OnAppWindowClosing;
    }

    private Grid BuildLayout(bool isCreateMode, Button cancelButton)
    {
        var grid = new Grid { Padding = new Thickness(16), RowSpacing = 10 };
        for (var i = 0; i < 5; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        // §7.3 契约：窗根 AutomationId 挂在标题上（TextBlock 带 peer，恒可被 UIA 断言）
        var header = new TextBlock
        {
            Text = isCreateMode ? "添加时间日期" : "编辑时间日期",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        AutomationProperties.SetAutomationId(header, "clock-window");
        AutomationProperties.SetName(header, isCreateMode ? "添加时间日期" : "编辑时间日期");
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sizeRow.Children.Add(new TextBlock { Text = "大小", VerticalAlignment = VerticalAlignment.Center, Width = 48 });
        sizeRow.Children.Add(new TextBlock { Text = "列", VerticalAlignment = VerticalAlignment.Center });
        sizeRow.Children.Add(_colsBox);
        sizeRow.Children.Add(new TextBlock { Text = "×  行", VerticalAlignment = VerticalAlignment.Center });
        sizeRow.Children.Add(_rowsBox);
        Grid.SetRow(sizeRow, 1);
        grid.Children.Add(sizeRow);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock { Text = "标题", VerticalAlignment = VerticalAlignment.Center, Width = 48 });
        titleRow.Children.Add(_titleBox);
        Grid.SetRow(titleRow, 2);
        grid.Children.Add(titleRow);

        var noteRow = new TextBlock
        {
            Text = "组件默认显示当前时间与日期（24 小时制 时:分 + 年/月/日 星期），无链接、点击无动作。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
        };
        Grid.SetRow(noteRow, 3);
        grid.Children.Add(noteRow);

        var bottomRow = new StackPanel { Orientation = Orientation.Vertical, Spacing = 10 };
        bottomRow.Children.Add(_validation);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_saveButton);
        buttons.Children.Add(cancelButton);
        bottomRow.Children.Add(buttons);
        Grid.SetRow(bottomRow, 4);
        grid.Children.Add(bottomRow);
        return grid;
    }

    private void ConfigureWindowShape(IntPtr ownerHwnd)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var workArea = DisplayArea.Primary.WorkArea;
        const int width = 560;
        const int height = 320;
        AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2),
            width,
            height));

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetOwnerWindow(hwnd, ownerHwnd); // owned window：恒在墙前（§3.3 同款）
    }

    // ————————————————————————————— 草稿与校验 —————————————————————————————

    private ClockDraft BuildDraft() => new()
    {
        Size = new GridSize((int)_colsBox.Value, (int)_rowsBox.Value),
        TitleText = _titleBox.Text,
    };

    private void ValidateForm()
    {
        var current = _context.Target;
        var otherRects = _context.OtherRects;
        var validation = ComponentDraftValidator.Validate(BuildDraft(), current, _context.Wall, otherRects);
        ShowValidation(validation.IsValid ? null : string.Join("；", validation.Errors));
    }

    private void ShowValidation(string? message)
    {
        if (message is null)
        {
            _validation.IsOpen = false;
            _validation.Visibility = Visibility.Collapsed;
            _saveButton.IsEnabled = true;
            return;
        }

        _validation.Message = message;
        _validation.Visibility = Visibility.Visible;
        _validation.IsOpen = true;
        AutomationProperties.SetName(_validation, message); // §7.3：Name = 文本（UIA 断言）
        _saveButton.IsEnabled = false;
    }

    /// <summary>保存边界计算：创建 = 校验 firstFit 建议位；编辑 = 原点锚定（ComponentDraftValidator 已验通过前提）。</summary>
    private GridRect? BuildBounds()
    {
        var current = _context.Target;
        var validation = ComponentDraftValidator.Validate(BuildDraft(), current, _context.Wall, _context.OtherRects);
        if (!validation.IsValid)
        {
            return null;
        }

        return current is null
            ? validation.SuggestedRect
            : new GridRect(current.Bounds.Column, current.Bounds.Row, BuildDraft().Size.Columns, BuildDraft().Size.Rows);
    }

    private bool IsDirty()
    {
        var target = _context.Target;
        if (target is null)
        {
            return true; // 创建模式：打开即视为草稿（与磁贴属性窗同判）
        }

        var draft = BuildDraft();
        return draft.Size != new GridSize(target.Bounds.Width, target.Bounds.Height)
               || !string.Equals(draft.TitleText ?? string.Empty, target.Visual.TitleText ?? string.Empty, StringComparison.Ordinal);
    }

    // ————————————————————————————— IExitParticipant（M7 §9 同款） —————————————————————————————

    bool IExitParticipant.HasUnsavedDraft => IsDirty();

    string? IExitParticipant.TrySaveNow()
    {
        ValidateForm();
        if (!_saveButton.IsEnabled)
        {
            return "表单尚未通过校验，无法保存";
        }

        var bounds = BuildBounds();
        if (bounds is null)
        {
            return "尺寸无法放置（容量或重叠问题）";
        }

        var error = _context.SaveHandler(bounds.Value, BuildDraft()); // MainWindow 纯配置路径提交
        if (error is null)
        {
            _forceClose = true;
            Close();
        }
        else
        {
            ShowValidation(error);
        }

        return error;
    }

    void IExitParticipant.DiscardDraft() => _forceClose = true;

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose || !IsDirty())
        {
            return;
        }

        args.Cancel = true;
        _ = ConfirmDiscardAsync();
    }

    private async Task ConfirmDiscardAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _rootGrid.XamlRoot,
            Title = "放弃更改？",
            Content = "时间日期属性有改动，关闭将放弃未保存的更改。",
            PrimaryButtonText = "放弃更改",
            CloseButtonText = "继续编辑",
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "clock-discard-dialog");
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _forceClose = true;
            Close();
        }
    }

    // ————————————————————————————— owned window（§3.3，TilePropertyWindow 同款） —————————————————————————————

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static void SetOwnerWindow(IntPtr hwnd, IntPtr ownerHwnd)
    {
        if (hwnd == IntPtr.Zero || ownerHwnd == IntPtr.Zero)
        {
            return;
        }

        if (Environment.Is64BitProcess)
        {
            _ = SetWindowLongPtr64(hwnd, GwlHwndParent, ownerHwnd);
        }
        else
        {
            _ = SetWindowLong32(hwnd, GwlHwndParent, ownerHwnd.ToInt32());
        }
    }
}
