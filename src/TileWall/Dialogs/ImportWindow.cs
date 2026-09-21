using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Import;
using TileWall.Core.Shell;

namespace TileWall.Dialogs;

/// <summary>导入窗运行上下文（MainWindow 注入）：来源根、容量基线、图标提取与导入回调。</summary>
public sealed record ImportWindowContext(
    IReadOnlyList<(string Path, SourceKind Source)> Roots, // 来源查询失败的根已被 MainWindow 剔除
    WallGrid Wall,
    IReadOnlyList<GridRect> ExistingRects,
    IconCache Icons,
    IShortcutIconExtractor IconExtractor,
    Func<IReadOnlyList<StartMenuCandidate>, string?> ImportHandler); // 返回 null = 成功；否则错误文本

/// <summary>
/// 开始菜单导入窗（M8 设计 §5.3，P1 §3.5 线框）：搜索 / 来源复选 / 全选 / 候选列表 / 计数 / 容量预检 / 导入·取消。
/// 模态单槽会话之一（打开即阻塞主墙、重复请求只聚焦）；确认前零 IO（INV-I1）——取消零清理成本。
/// 扫描在后台线程（首屏 200 条先行、后续增量追加），窗关闭即取消（CancellationTokenSource）。
/// 实现 IExitParticipant：勾选集即草稿，托盘退出走既有三分支。
/// </summary>
public sealed class ImportWindow : Window, IExitParticipant
{
    private const int GwlHwndParent = -8;

    /// <summary>R-4：候选计数提示阈值（超过显示「结果过多」，不静默截断）。</summary>
    private const int ManyResultsThreshold = 5000;

    /// <summary>首屏先行条数（§5.2：首屏 200 条先行，后续增量追加）。</summary>
    private const int FirstBatchSize = 200;

    private readonly ImportWindowContext _context;
    private readonly Grid _rootGrid;
    private readonly TextBox _search;
    private readonly CheckBox _userSource;
    private readonly CheckBox _commonSource;
    private readonly CheckBox _selectAll;
    private readonly ListView _list;
    private readonly TextBlock _count;
    private readonly TextBlock _capacity;
    private readonly TextBlock _status;
    private readonly InfoBar _validation;
    private readonly Button _confirm;
    private readonly Button _cancel;

    private readonly Dictionary<string, StartMenuCandidate> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<StartMenuCandidate> _ordered = new(); // 扫描序（first-fit 确定性的输入顺序）
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private StartMenuScanResult? _scanResult;
    private CancellationTokenSource? _scanCancellation;
    private bool _forceClose;
    private bool _importInProgress;
    private bool _syncSelectAll; // 程序化同步全选框视觉时抑制事件（避免误清用户勾选）

    public ImportWindow(ImportWindowContext context, IntPtr ownerHwnd)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        Title = "从开始菜单导入";

        _search = new TextBox { PlaceholderText = "按名称筛选…", Width = 240 };
        AutomationProperties.SetAutomationId(_search, "import-search");

        _userSource = new CheckBox { Content = "当前用户", IsChecked = true, MinWidth = 96 };
        AutomationProperties.SetAutomationId(_userSource, "import-source-user");
        _commonSource = new CheckBox { Content = "所有用户", IsChecked = true, MinWidth = 96 };
        AutomationProperties.SetAutomationId(_commonSource, "import-source-common");

        _selectAll = new CheckBox { Content = "全选（当前可见）", MinWidth = 140 };
        AutomationProperties.SetAutomationId(_selectAll, "import-select-all");
        _selectAll.Checked += (_, _) => SelectAllVisible(true);
        _selectAll.Unchecked += (_, _) => SelectAllVisible(false);

        _list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            MaxHeight = 320,
        };
        AutomationProperties.SetAutomationId(_list, "import-list");

        _count = new TextBlock { Text = "已选 0 / 0" };
        AutomationProperties.SetAutomationId(_count, "import-count");

        _capacity = new TextBlock { Text = string.Empty, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(_capacity, "import-capacity");

        _status = new TextBlock { Text = "正在扫描…", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray) };
        AutomationProperties.SetAutomationId(_status, "import-status");

        _validation = new InfoBar { IsOpen = false, Severity = InfoBarSeverity.Warning, IsClosable = false };
        AutomationProperties.SetAutomationId(_validation, "import-validation");

        _confirm = new Button { Content = "导入", IsEnabled = false };
        AutomationProperties.SetAutomationId(_confirm, "import-confirm");
        _confirm.Click += (_, _) => _ = ((IExitParticipant)this).TrySaveNow();
        _cancel = new Button { Content = "取消" };
        AutomationProperties.SetAutomationId(_cancel, "import-cancel");
        _cancel.Click += (_, _) => Close();

        Content = _rootGrid = BuildLayout();

        _search.TextChanged += (_, _) => RebuildList();
        _userSource.Checked += (_, _) => RebuildList();
        _userSource.Unchecked += (_, _) => RebuildList();
        _commonSource.Checked += (_, _) => RebuildList();
        _commonSource.Unchecked += (_, _) => RebuildList();

        var hasUser = context.Roots.Any(r => r.Source == SourceKind.User);
        var hasCommon = context.Roots.Any(r => r.Source == SourceKind.Common);
        _userSource.Visibility = hasUser ? Visibility.Visible : Visibility.Collapsed; // 来源查询失败 → 来源项隐藏（§5.1）
        _commonSource.Visibility = hasCommon ? Visibility.Visible : Visibility.Collapsed;

        StartScan();
        ConfigureWindowShape(ownerHwnd);
        Closed += (_, _) => _scanCancellation?.Cancel();
        AppWindow.Closing += OnAppWindowClosing;
    }

    private Grid BuildLayout()
    {
        var grid = new Grid { Padding = new Thickness(16), RowSpacing = 10 };
        for (var i = 0; i < 7; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var header = new TextBlock { Text = "从开始菜单导入", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(header, "import-window");
        AutomationProperties.SetName(header, "从开始菜单导入");
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var filterRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        filterRow.Children.Add(_search);
        filterRow.Children.Add(_userSource);
        filterRow.Children.Add(_commonSource);
        filterRow.Children.Add(_selectAll);
        Grid.SetRow(filterRow, 1);
        grid.Children.Add(filterRow);

        Grid.SetRow(_list, 2);
        grid.Children.Add(_list);

        var countRow = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };
        countRow.Children.Add(_count);
        countRow.Children.Add(_capacity);
        countRow.Children.Add(_status);
        Grid.SetRow(countRow, 3);
        grid.Children.Add(countRow);

        Grid.SetRow(_validation, 4);
        grid.Children.Add(_validation);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_confirm);
        buttons.Children.Add(_cancel);
        Grid.SetRow(buttons, 5);
        grid.Children.Add(buttons);

        var note = new TextBlock
        {
            Text = "导入会为每个所选快捷方式创建托管副本（原快捷方式不受影响）；候选不会被执行。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
        };
        Grid.SetRow(note, 6);
        grid.Children.Add(note);
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
        const int width = 760;
        const int height = 640;
        AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2),
            width,
            height));

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetOwnerWindow(hwnd, ownerHwnd);
    }

    // ————————————————————————————— 扫描（后台线程，§5.2） —————————————————————————————

    private void StartScan()
    {
        _scanCancellation = new CancellationTokenSource();
        var token = _scanCancellation.Token;
        var roots = _context.Roots;
        var icons = _context.Icons;
        var extractor = _context.IconExtractor;
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(() =>
        {
            StartMenuScanResult result;
            try
            {
                result = StartMenuScanner.Enumerate(new TileWall.Core.Configuration.LocalFileStore(), roots);
            }
            catch (Exception)
            {
                _ = dispatcher.TryEnqueue(() => ShowStatus("扫描失败：无法枚举开始菜单目录。"));
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            _ = dispatcher.TryEnqueue(() =>
            {
                _scanResult = result;
                foreach (var candidate in result.Candidates)
                {
                    if (_candidates.TryAdd(candidate.FullPath, candidate))
                    {
                        _ordered.Add(candidate);
                    }
                }

                var suffix = result.SkippedDirectories > 0
                    ? $"（{result.SkippedDirectories} 个目录无法读取已跳过）"
                    : string.Empty;
                ShowStatus($"扫描完成：{result.Candidates.Count} 个候选{suffix}"
                           + (result.Candidates.Count > ManyResultsThreshold ? "；结果过多，请用搜索缩小范围" : string.Empty));
                RebuildList();
            });

            // 图标增量提取（尽力而为；命中缓存跳过）。逐批提取 → 逐批投递，避免 UI 首屏等待。
            var candidates = result.Candidates;
            for (var offset = 0; offset < candidates.Count; offset += FirstBatchSize)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                var batch = candidates.Skip(offset).Take(FirstBatchSize).ToList();
                var iconBatch = new List<(StartMenuCandidate Candidate, byte[]? Png)>(batch.Count);
                foreach (var candidate in batch)
                {
                    var png = icons.TryRead(candidate.FullPath)
                              ?? extractor.Extract(candidate.FullPath, candidate.Kind);
                    if (png is not null)
                    {
                        icons.Write(candidate.FullPath, png); // 写入失败静默（缓存不权威）
                    }

                    iconBatch.Add((candidate, png));
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                _ = dispatcher.TryEnqueue(() =>
                {
                    foreach (var (candidate, png) in iconBatch)
                    {
                        _icons[candidate.FullPath] = png;
                    }

                    RefreshIconsInList(iconBatch.Select(pair => pair.Candidate.FullPath));
                });
            }
        }, token);
    }

    private void ShowStatus(string message)
    {
        _status.Text = message;
        if (!message.Contains("正在扫描", StringComparison.Ordinal))
        {
            UpdateConfirmState(); // 扫描结束 → 导入按钮按选择状态放行
        }
    }

    // ————————————————————————————— 列表与选择（§5.3） —————————————————————————————

    /// <summary>当前可见集合 = 扫描全集 ∩ 来源勾选 ∩ 名称子串（OrdinalIgnoreCase）。</summary>
    private IReadOnlyList<StartMenuCandidate> VisibleCandidates()
    {
        var needle = _search.Text.Trim();
        var includeUser = _userSource.Visibility == Visibility.Collapsed || _userSource.IsChecked == true;
        var includeCommon = _commonSource.Visibility == Visibility.Collapsed || _commonSource.IsChecked == true;
        return [.. _ordered
            .Where(c => c.Source switch
            {
                SourceKind.User => includeUser,
                SourceKind.Common => includeCommon,
                _ => false,
            })
            .Where(c => needle.Length == 0 || c.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase))];
    }

    private void RebuildList()
    {
        var visible = VisibleCandidates();
        _list.Items.Clear();
        foreach (var candidate in visible)
        {
            _list.Items.Add(BuildRow(candidate));
        }

        var anyVisible = visible.Count > 0;
        _selectAll.IsEnabled = anyVisible;
        SyncSelectAllVisual();
        UpdateCountsAndCapacity();
    }

    private FrameworkElement BuildRow(StartMenuCandidate candidate)
    {
        var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(2), Tag = candidate.FullPath };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = BuildIcon(candidate.FullPath);
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var checkBox = new CheckBox { IsEnabled = candidate.IsImportable, MinWidth = 32 };
        checkBox.IsChecked = candidate.IsImportable && _selected.Contains(candidate.FullPath);
        checkBox.Checked += (_, _) => ToggleSelection(candidate, true);
        checkBox.Unchecked += (_, _) => ToggleSelection(candidate, false);
        Grid.SetColumn(checkBox, 1);
        row.Children.Add(checkBox);

        var nameStack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 0 };
        var name = new TextBlock { Text = candidate.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis };
        var source = new TextBlock
        {
            Text = SourceLabel(candidate),
            FontSize = 11,
            Opacity = 0.7,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        nameStack.Children.Add(name);
        nameStack.Children.Add(source);
        if (!candidate.IsImportable)
        {
            var reason = new TextBlock
            {
                Text = "不可导入：" + string.Join("；", candidate.NotImportableReasons),
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            };
            nameStack.Children.Add(reason); // 保留显示、就地说明——不静默剔除（§5.2）
            ToolTipService.SetToolTip(nameStack, string.Join("；", candidate.NotImportableReasons));
        }

        Grid.SetColumn(nameStack, 2);
        row.Children.Add(nameStack);

        var kindLabel = new TextBlock
        {
            Text = candidate.Kind == EntryKind.Lnk ? ".lnk" : ".url",
            FontSize = 11,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(kindLabel, 3);
        row.Children.Add(kindLabel);
        return row;
    }

    private FrameworkElement BuildIcon(string fullPath)
    {
        if (_icons.TryGetValue(fullPath, out var png) && png is not null)
        {
            try
            {
                var image = new BitmapImage();
                using var stream = new System.IO.MemoryStream(png);
                _ = image.SetSourceAsync(stream.AsRandomAccessStream());
                return new Border { Width = 24, Height = 24, Child = new Image { Source = image } };
            }
            catch (Exception)
            {
                // 位图构造失败 → 占位字形（缓存非真值）
            }
        }

        return new FontIcon { Glyph = "\uE7C3", FontSize = 16, Width = 24 }; // 占位字形（§5.4）
    }

    private void RefreshIconsInList(IEnumerable<string> fullPaths)
    {
        var wanted = fullPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _list.Items.OfType<Grid>().Where(row => row.Tag is string path && wanted.Contains(path)))
        {
            var iconHost = row.Children.OfType<FrameworkElement>().First(); // 列 0
            var replaced = BuildIcon((string)row.Tag);
            Grid.SetColumn(replaced, 0);
            row.Children.Remove(iconHost);
            row.Children.Insert(0, replaced);
        }
    }

    /// <summary>来源列（R-8：来源根缩写 + 相对目录，信息不丢即可；线框截断形式）。</summary>
    private static string SourceLabel(StartMenuCandidate candidate) =>
        (candidate.Source == SourceKind.User ? "当前用户" : "所有用户")
        + (candidate.RelativeDir.Length == 0 ? string.Empty : $"\\{candidate.RelativeDir}");

    private void ToggleSelection(StartMenuCandidate candidate, bool select)
    {
        if (!candidate.IsImportable)
        {
            return; // NotImportable 行复选禁用（防御）
        }

        if (select)
        {
            _selected.Add(candidate.FullPath);
        }
        else
        {
            _selected.Remove(candidate.FullPath);
        }

        SyncSelectAllVisual();
        UpdateCountsAndCapacity();
    }

    private void SelectAllVisible(bool select)
    {
        if (_syncSelectAll)
        {
            return; // 程序化同步视觉触发的事件：不改变勾选集（避免改筛选条件误清用户勾选）
        }

        foreach (var candidate in VisibleCandidates())
        {
            if (!candidate.IsImportable)
            {
                continue;
            }

            if (select)
            {
                _selected.Add(candidate.FullPath);
            }
            else
            {
                _selected.Remove(candidate.FullPath);
            }
        }

        RebuildList(); // 复选框状态与集合对齐（行内 IsChecked 先设后挂事件，无回环）
    }

    private void SyncSelectAllVisual()
    {
        var visible = VisibleCandidates();
        var importable = visible.Where(c => c.IsImportable).ToList();
        var allSelected = importable.Count > 0 && importable.All(c => _selected.Contains(c.FullPath));
        _syncSelectAll = true;
        try
        {
            _selectAll.IsChecked = allSelected;
        }
        finally
        {
            _syncSelectAll = false;
        }
    }

    /// <summary>计数与容量预检行（每次勾选变化即时重算；判定以 CanPlaceBatch 为准，§5.5）。</summary>
    private void UpdateCountsAndCapacity()
    {
        var importableTotal = _candidates.Values.Count(c => c.IsImportable);
        _count.Text = $"已选 {_selected.Count} / {importableTotal}";

        var selectedCandidates = OrderedSelectedCandidates().ToList();
        if (selectedCandidates.Count == 0)
        {
            _capacity.Text = "勾选候选后此处显示容量预检结果。";
            _capacity.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray);
            UpdateConfirmState();
            return;
        }

        var fits = GridPlacement.CanPlaceBatch(
            _context.Wall, _context.ExistingRects, selectedCandidates.Select(_ => new GridSize(1, 1)).ToList());
        var freeCells = FreeCellCount();
        if (fits)
        {
            _capacity.Text = $"✓ 所选 {selectedCandidates.Count} 项均能以 1×1 放入当前墙面";
            _capacity.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Green);
        }
        else
        {
            _capacity.Text = $"✗ 需 {selectedCandidates.Count} 格，当前墙面连续可用空位 {freeCells} 格；请减少选择或整理墙面";
            _capacity.Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
        }

        UpdateConfirmState();
    }

    /// <summary>空闲格计数 K 仅用于提示文案；碎片化时 K 足够但连续区域不足，判定仍以 CanPlaceBatch 为准（§5.5）。</summary>
    private int FreeCellCount()
    {
        var occupied = _context.ExistingRects.Sum(r => (long)r.Width * r.Height);
        var total = (long)_context.Wall.CellColumns * _context.Wall.Rows;
        return (int)Math.Max(0, total - occupied);
    }

    /// <summary>已选项按扫描序展开（顺序 first-fit 的确定性输入）。</summary>
    private IEnumerable<StartMenuCandidate> OrderedSelectedCandidates() =>
        _ordered.Where(c => _selected.Contains(c.FullPath));

    private void UpdateConfirmState()
    {
        var selected = OrderedSelectedCandidates().Count();
        var fits = selected > 0
                   && _scanResult is not null
                   && !_importInProgress
                   && GridPlacement.CanPlaceBatch(
                       _context.Wall, _context.ExistingRects, Enumerable.Repeat(new GridSize(1, 1), selected).ToList());
        _confirm.IsEnabled = fits;
    }

    private void ShowValidation(string? message)
    {
        if (message is null)
        {
            _validation.IsOpen = false;
            _validation.Visibility = Visibility.Collapsed;
            return;
        }

        _validation.Message = message;
        _validation.Visibility = Visibility.Visible;
        _validation.IsOpen = true;
        AutomationProperties.SetName(_validation, message);
    }

    // ————————————————————————————— IExitParticipant（勾选集即草稿） —————————————————————————————

    bool IExitParticipant.HasUnsavedDraft => _selected.Count > 0 && !_forceClose;

    string? IExitParticipant.TrySaveNow()
    {
        if (_importInProgress)
        {
            return "正在提交导入，请稍候";
        }

        var selected = OrderedSelectedCandidates().ToList();
        if (selected.Count == 0)
        {
            return "尚未勾选任何可导入候选";
        }

        _importInProgress = true;
        UpdateConfirmState();
        try
        {
            var error = _context.ImportHandler(selected); // MainWindow：CommitBatch 一次协议执行（§7.1）
            if (error is null)
            {
                _forceClose = true;
                Close();
                return null;
            }

            ShowValidation(error); // 就地报错，窗不关、勾选保留可重试（§7.1）
            return error;
        }
        finally
        {
            _importInProgress = false;
            UpdateConfirmState();
        }
    }

    void IExitParticipant.DiscardDraft() => _forceClose = true;

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose || _selected.Count == 0)
        {
            return; // 确认前零 IO → 关窗零清理成本（§5.3）
        }

        args.Cancel = true;
        _ = ConfirmDiscardAsync();
    }

    private async Task ConfirmDiscardAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _rootGrid.XamlRoot,
            Title = "放弃导入？",
            Content = "已勾选的候选尚未导入，关闭将放弃本次选择（不会产生任何文件）。",
            PrimaryButtonText = "放弃选择",
            CloseButtonText = "继续编辑",
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "import-discard-dialog");
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
