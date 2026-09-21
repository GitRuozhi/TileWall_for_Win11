using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Configuration;
using TileWall.Core.Grid;
using TileWall.Core.Shell;

namespace TileWall.Dialogs;

/// <summary>调整布局会话上下文（MainWindow 注入）。</summary>
public sealed record AdaptationAdjustWindowContext(
    TileWallConfig OriginalConfig,
    WallGrid OriginalWall,
    GridMetrics Metrics,
    double WorkWidthDip,
    double WorkHeightDip,
    Func<IReadOnlyList<string>, TileWallConfig, string?> ConfirmHandler); // (移除 Id 集, 最终配置) → null=成功

/// <summary>
/// 「调整布局」草稿会话（M8 设计 §6.4）：占用模态单槽（打开期间阻塞主墙、与提示窗互斥）。
/// 草稿期零提交：所有编辑只动内存草稿（对象行列 NumberBox 锚定左上角缩放 + 取消固定标记）；
/// 草稿墙 = WallSizing.MinShrink(原墙, 剩余对象)——移除/缩小对象后墙自动收敛到恰好容纳；
/// 实时状态行给出草稿墙 n×m 与 fit 结论（不可达组合持续说明，不做自动拆组）。
/// 确认仅在 fit ∧ ConfigValidator 通过时可用；点击经 ConfirmHandler 走 CommitAdaptation 单事务。
/// 不提供移动拖拽（适配是异常处理，取最小可用操作集：缩/删即可达成容纳）。
/// F-A2：窗打开中分辨率再变 → UpdateWorkArea 按新容量刷新判定；原配置始终未动。
/// </summary>
public sealed class AdaptationAdjustWindow : Window, IExitParticipant
{
    private const int GwlHwndParent = -8;

    private readonly AdaptationAdjustWindowContext _context;
    private readonly Grid _rootGrid;
    private readonly StackPanel _objectList;
    private readonly TextBlock _status;
    private readonly Button _confirm;
    private readonly List<DraftRow> _rows = [];
    private double _workWidthDip;
    private double _workHeightDip;
    private bool _forceClose;

    /// <summary>单行草稿状态（锚定原点缩放 + 取消固定标记；全部纯内存，确认前零提交）。</summary>
    private sealed class DraftRow
    {
        public required LayoutObject Original { get; init; }

        public required NumberBox Cols { get; init; }

        public required NumberBox Rows { get; init; }

        public required CheckBox Unpin { get; init; }

        public required TextBlock Error { get; init; }

        public bool Removed => Unpin.IsChecked == true;

        public string? ValidationError { get; set; }

        public GridRect CurrentRect => new(
            Original.Bounds.Column,
            Original.Bounds.Row,
            Math.Max(1, (int)Cols.Value),
            Math.Max(1, (int)Rows.Value));

        public string DisplayName => Original.Entry is not null
            ? System.IO.Path.GetFileNameWithoutExtension(Original.Entry.RelativePath)
            : Original.Visual.TitleText ?? Original.Id;
    }

    public AdaptationAdjustWindow(AdaptationAdjustWindowContext context, IntPtr ownerHwnd)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _workWidthDip = context.WorkWidthDip;
        _workHeightDip = context.WorkHeightDip;
        Title = "调整布局以适配显示";

        _objectList = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };
        AutomationProperties.SetAutomationId(_objectList, "adapt-object-list");

        _status = new TextBlock { Text = string.Empty, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(_status, "adapt-status");

        _confirm = new Button { Content = "确认并应用", IsEnabled = false };
        AutomationProperties.SetAutomationId(_confirm, "adapt-confirm");
        _confirm.Click += (_, _) => _ = ((IExitParticipant)this).TrySaveNow();

        var cancelButton = new Button { Content = "取消" };
        AutomationProperties.SetAutomationId(cancelButton, "adapt-cancel");
        cancelButton.Click += (_, _) => Close();

        Content = _rootGrid = BuildLayout(cancelButton);
        BuildRows();
        RecomputeDraft();
        ConfigureWindowShape(ownerHwnd);
        AppWindow.Closing += OnAppWindowClosing;
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
        const int width = 720;
        const int height = 620;
        AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2),
            width,
            height));

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetOwnerWindow(hwnd, ownerHwnd); // owned window：恒在墙前（§3.3 同款）
    }

    private Grid BuildLayout(Button cancelButton)
    {
        var scroll = new ScrollViewer { Content = _objectList, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var grid = new Grid { Padding = new Thickness(16), RowSpacing = 10 };
        for (var i = 0; i < 6; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var header = new TextBlock { Text = "调整布局以适配显示", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(header, "adapt-adjust-window");
        AutomationProperties.SetName(header, "调整布局以适配显示");
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var intro = new TextBlock
        {
            Text = "缩小或取消固定对象，使布局能完整放入当前屏幕；确认前不会改动任何配置。不能移动对象位置。",
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(intro, 1);
        grid.Children.Add(intro);

        Grid.SetRow(scroll, 2);
        grid.Children.Add(scroll);

        Grid.SetRow(_status, 3);
        grid.Children.Add(_status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_confirm);
        buttons.Children.Add(cancelButton);
        Grid.SetRow(buttons, 4);
        grid.Children.Add(buttons);

        var note = new TextBlock
        {
            Text = "恢复原分辨率/缩放后取消本窗即可按原布局显示（本窗的改动以确认提交为准）。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
        };
        Grid.SetRow(note, 5);
        grid.Children.Add(note);
        return grid;
    }

    private void BuildRows()
    {
        foreach (var o in _context.OriginalConfig.Objects)
        {
            var cols = new NumberBox
            {
                Value = o.Bounds.Width,
                Minimum = 1,
                Maximum = WallGrid.TileMaxCells,
                SmallChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Width = 84,
            };
            var rows = new NumberBox
            {
                Value = o.Bounds.Height,
                Minimum = 1,
                Maximum = Math.Max(_context.OriginalWall.Rows, 1),
                SmallChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Width = 84,
            };
            var unpin = new CheckBox { Content = "取消固定" };
            var error = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed) };

            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            var name = new TextBlock { Text = DisplayNameOf(o), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 0);
            row.Children.Add(name);
            var colsHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            colsHost.Children.Add(new TextBlock { Text = "列", VerticalAlignment = VerticalAlignment.Center });
            colsHost.Children.Add(cols);
            Grid.SetColumn(colsHost, 1);
            row.Children.Add(colsHost);
            var rowsHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            rowsHost.Children.Add(new TextBlock { Text = "行", VerticalAlignment = VerticalAlignment.Center });
            rowsHost.Children.Add(rows);
            Grid.SetColumn(rowsHost, 2);
            row.Children.Add(rowsHost);
            Grid.SetColumn(unpin, 3);
            row.Children.Add(unpin);
            Grid.SetColumn(error, 4);
            row.Children.Add(error);

            var draftRow = new DraftRow { Original = o, Cols = cols, Rows = rows, Unpin = unpin, Error = error };
            cols.ValueChanged += (_, _) => RecomputeDraft();
            rows.ValueChanged += (_, _) => RecomputeDraft();
            unpin.Checked += (_, _) => RecomputeDraft();
            unpin.Unchecked += (_, _) => RecomputeDraft();
            _rows.Add(draftRow);
            _objectList.Children.Add(row);
        }

        if (_rows.Count == 0)
        {
            _objectList.Children.Add(new TextBlock { Text = "当前布局没有对象（无需调整，直接恢复显示即可）。" });
        }
    }

    private static string DisplayNameOf(LayoutObject o) => o switch
    {
        GroupObject => "磁贴组（" + o.Id[..Math.Min(6, o.Id.Length)] + "…）",
        ClockObject => o.Visual.TitleText is { Length: > 0 } t ? t : "时间日期",
        _ => o.Entry is not null
            ? System.IO.Path.GetFileNameWithoutExtension(o.Entry.RelativePath)
            : o.Visual.TitleText ?? "磁贴",
    };

    /// <summary>F-A2：调整窗打开中分辨率再变 → 按新容量刷新判定（草稿不动，原配置始终未动）。</summary>
    public void UpdateWorkArea(double workWidthDip, double workHeightDip)
    {
        _workWidthDip = workWidthDip;
        _workHeightDip = workHeightDip;
        RecomputeDraft();
    }

    // ————————————————————————————— 草稿推导（零提交） —————————————————————————————

    private void RecomputeDraft()
    {
        // 1) 逐行锚定缩放校验（ComponentDraftValidator 同款规则：宽 ≤ 8、原点不动、不重叠）
        foreach (var row in _rows)
        {
            row.ValidationError = null;
            if (row.Removed)
            {
                continue;
            }

            var rect = row.CurrentRect;
            if (rect.Width < 1 || rect.Height < 1)
            {
                row.ValidationError = "尺寸非法";
                continue;
            }

            if (rect.Width > WallGrid.TileMaxCells)
            {
                row.ValidationError = $"宽 {rect.Width} 超过一栏八格";
                continue;
            }

            if (!_context.OriginalWall.Contains(rect))
            {
                row.ValidationError = "越出原墙范围";
            }
        }

        var active = _rows.Where(r => !r.Removed).ToList();
        for (var i = 0; i < active.Count; i++)
        {
            for (var j = i + 1; j < active.Count; j++)
            {
                if (active[i].CurrentRect.Intersects(active[j].CurrentRect))
                {
                    active[i].ValidationError ??= "与其他对象重叠";
                    active[j].ValidationError ??= "与其他对象重叠";
                }
            }
        }

        foreach (var row in _rows)
        {
            row.Error.Text = row.ValidationError ?? string.Empty;
        }

        // 2) 草稿墙收敛 + 草稿配置
        var validActive = active.Where(r => r.ValidationError is null).ToList();
        var rects = validActive.Select(r => r.CurrentRect).ToList();
        var draftWall = rects.Count > 0
            ? WallSizing.MinShrink(_context.OriginalWall, rects)
            : new WallGrid(1, 1); // 全部取消固定 → 空墙（1×1 最小合法形）
        var draftConfig = new TileWallConfig
        {
            Wall = new WallState(draftWall.Columns, draftWall.Rows),
            Settings = _context.OriginalConfig.Settings,
            Objects = [.. validActive.Select(r => r.Original with { Bounds = r.CurrentRect })],
        };

        // 3) 实时状态行：草稿墙 n×m、fit 结论、仍差多少
        var fits = AdaptationEvaluator.Fits(draftWall, _workWidthDip, _workHeightDip, _context.Metrics);
        var violations = ConfigValidator.Validate(draftConfig);
        var invalidCount = active.Count(r => r.ValidationError is not null);
        var lines = new List<string>
        {
            $"草稿墙：{draftWall.Columns} 栏 × {draftWall.Rows} 行（对象 {validActive.Count} 个）",
        };
        if (fits && violations.Count == 0 && invalidCount == 0)
        {
            lines.Add("✓ 草稿可完整放入当前屏幕；确认后将按此布局显示。");
            _status.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Green);
        }
        else
        {
            var (capacityColumns, capacityRows) = AdaptationEvaluator.CapacityFor(_workWidthDip, _workHeightDip, _context.Metrics);
            if (!fits)
            {
                var overColumns = Math.Max(0, draftWall.Columns - capacityColumns);
                var overRows = Math.Max(0, draftWall.Rows - capacityRows);
                lines.Add(overColumns > 0
                    ? $"✗ 仍放不下：当前还超 {overColumns} 栏（整屏至多 {capacityColumns} 栏 × {capacityRows} 行）"
                    : $"✗ 仍放不下：当前还超 {overRows} 行（整屏至多 {capacityColumns} 栏 × {capacityRows} 行）");
            }

            if (invalidCount > 0)
            {
                lines.Add($"✗ {invalidCount} 个对象的尺寸不合法（见行内说明）。");
            }

            if (violations.Count > 0)
            {
                lines.Add($"✗ 配置校验未通过：{string.Join("；", violations.Select(v => v.Code))}");
            }

            _status.Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
        }

        _status.Text = string.Join(Environment.NewLine, lines);
        AutomationProperties.SetName(_status, _status.Text);
        _confirm.IsEnabled = fits && violations.Count == 0 && invalidCount == 0;
    }

    /// <summary>是否存在草稿改动（相对原配置）：任一行取消固定或尺寸变化。</summary>
    private bool IsDirty()
    {
        foreach (var row in _rows)
        {
            if (row.Removed)
            {
                return true;
            }

            if (row.CurrentRect != row.Original.Bounds)
            {
                return true;
            }
        }

        return false;
    }

    private (IReadOnlyList<string> RemovedIds, TileWallConfig FinalConfig)? BuildConfirmation()
    {
        RecomputeDraft();
        if (!_confirm.IsEnabled)
        {
            return null;
        }

        var removedIds = _rows.Where(r => r.Removed).Select(r => r.Original.Id).ToList();
        var active = _rows.Where(r => !r.Removed && r.ValidationError is null).ToList();
        var rects = active.Select(r => r.CurrentRect).ToList();
        var draftWall = rects.Count > 0
            ? WallSizing.MinShrink(_context.OriginalWall, rects)
            : new WallGrid(1, 1);
        var finalConfig = new TileWallConfig
        {
            Wall = new WallState(draftWall.Columns, draftWall.Rows),
            Settings = _context.OriginalConfig.Settings,
            Objects = [.. active.Select(r => r.Original with { Bounds = r.CurrentRect })],
        };
        return (removedIds, finalConfig);
    }

    // ————————————————————————————— IExitParticipant（草稿三分支） —————————————————————————————

    bool IExitParticipant.HasUnsavedDraft => !_forceClose && IsDirty();

    string? IExitParticipant.TrySaveNow()
    {
        var confirmation = BuildConfirmation();
        if (confirmation is null)
        {
            return "草稿尚未能完整容纳，无法确认（可缩小/取消固定对象，或取消退出）";
        }

        var error = _context.ConfirmHandler(confirmation.Value.RemovedIds, confirmation.Value.FinalConfig);
        if (error is null)
        {
            _forceClose = true;
            Close();
            return null;
        }

        _status.Text = "提交失败：" + error; // 就地显示（F-A1：提交失败全回滚，原布局配置不动）
        _status.Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
        return error;
    }

    void IExitParticipant.DiscardDraft() => _forceClose = true;

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose || !IsDirty())
        {
            return; // 取消 → 草稿丢弃，配置零改动（§17.1「修改前保留上一有效状态」）
        }

        args.Cancel = true;
        _ = ConfirmDiscardAsync();
    }

    private async Task ConfirmDiscardAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _rootGrid.XamlRoot,
            Title = "放弃调整？",
            Content = "调整草稿尚未提交，关闭将放弃草稿（当前布局保持不变）。",
            PrimaryButtonText = "放弃调整",
            CloseButtonText = "继续调整",
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "adapt-discard-dialog");
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _forceClose = true;
            Close();
        }
    }

    // ————————————————————————————— owned window（§3.3 同款） —————————————————————————————

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
