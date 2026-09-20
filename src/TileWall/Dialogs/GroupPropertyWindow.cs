using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Carousel;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Groups;
using TileWall.Core.Imaging;
using TileWall.Shell.Imaging;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml.Media.Imaging;

namespace TileWall.Dialogs;

/// <summary>组属性窗运行上下文（MainWindow 注入；窗口只做「绑定显示 → 草稿 → 回调」）。</summary>
public sealed record GroupPropertyWindowContext(
    WallGrid Wall,
    IReadOnlyList<GridRect> OtherRects,
    GroupObject? Target,               // null = 创建模式（稳定标识在保存时生成）
    string? CurrentEntryFullPath,
    string? CurrentEntryDisplay,
    ILnkFileService LinkFiles,
    IFileStore Files,                  // M6：图片候选枚举（ImageCatalog，文件夹来源）
    Func<GroupEditDraft, string?> SaveHandler); // 返回 null = 成功（关窗）；否则错误文本就地显示

/// <summary>
/// 组属性窗（M5 设计 §10；P1 §3.3 线框）：主页与布局子页为同一 Window 内两个 Panel 翻转 Visibility
/// （同一窗口 = 同一模态会话，主墙全程阻塞）。两级草稿严格按 §9.2 状态机：
/// 进入画笔 = 子草稿会话；确认 = 接受回主页；取消编辑 = 回进画笔前；保存 = 整体提交；取消/有改动关窗 = 全弃
/// ——「文字取消了，墙也不会被拆」（B08）。主墙在保存回调成功前零变化（INV-P12/B07）。
/// 窗口形态复用 M4 模式：不可调、GWLP_HWNDPARENT 设为墙 HWND 的 owned window。
/// </summary>
public sealed class GroupPropertyWindow : Window
{
    private const int GwlHwndParent = -8;

    // 布局子页画布几何（纯视觉缩放，全部经 GridMetrics 常量换算——UI 不自造几何，§15.1）
    private static readonly GridMetrics CanvasMetrics = new() { CellCore = 20, Gap = 4, Margin = 10 };
    private const double PreviewCell = 12;

    private readonly GroupPropertyWindowContext _context;
    private readonly Grid _rootGrid;
    private readonly FrameworkElement _mainPage;
    private readonly Grid _layoutPage;

    // —— 主页控件 ——
    private readonly TextBlock _sizeSummary;
    private readonly Canvas _previewCanvas;
    private readonly Canvas _imagePreviewCanvas;
    private readonly ContentControl _imagePreviewHost;
    private readonly ComboBox _imageCurrent;
    private readonly Button _imageZoomIn;
    private readonly Button _imageZoomOut;
    private readonly Button _imageFitCover;
    private readonly Button _imageFitAll;
    private readonly Button _imageReset;
    private readonly TextBlock _imageStatus;
    private readonly RadioButton _imageSingle;
    private readonly RadioButton _imageMultiple;
    private readonly RadioButton _imageFolder;
    private readonly TextBox _imagePathsBox;
    private readonly TextBox _folderPathBox;
    private readonly ComboBox _backdrop;
    private readonly TextBox _backdropColorBox;
    private readonly StackPanel _backdropColorRow;
    private readonly TextBox _titleBox;
    private readonly TextBox _linkDisplay;
    private readonly TextBox _linkEdit;
    private readonly InfoBar _validation;
    private readonly Button _saveButton;
    private readonly MenuFlyout _linkBrowseMenu;

    // —— 布局子页控件 ——
    private readonly RadioButton _toolDraw;
    private readonly RadioButton _toolErase;
    private readonly Button _colPlus;
    private readonly Button _colMinus;
    private readonly Button _rowPlus;
    private readonly Button _rowMinus;
    private readonly Button _undoButton;
    private readonly Button _redoButton;
    private readonly Canvas _layoutCanvas;
    private readonly TextBlock _layoutStatus;

    private GroupEditDraft _draft;           // 已确认的组草稿（布局子草稿仅在确认时并入）
    private GroupEditDraft _initialDraft;
    private EntryDraft? _pickedEntryDraft;   // 浏览产生的草稿（CopyFromFile / CreateForPath）
    private LinkSource _linkSource = LinkSource.Untouched;
    private PartitionEditSession? _session;  // null = 主页；非 null = 布局子页会话
    private bool _strokeActive;
    private bool _forceClose;

    // —— M6 图片编辑状态（§10；预览与主墙共用 SharedCanvasTransform，禁止第二套裁剪数学） ——
    private IReadOnlyList<string> _candidates = [];
    private string? _editImageId;
    private PixelSize? _editPixels;
    private ImageTransform? _editTransform;   // null = 尚未加载出可编辑对象
    private FitMode _editFit = FitMode.CoverFill;

    /// <summary>
    /// 会话内逐图编辑草稿（当前图之外的其他图也活在草稿里，保存=整体提交、取消=全弃，
    /// M6 设计 §10 两级草稿语义）——「当前图」下拉切换不丢任何一张未保存的平移/缩放编辑。
    /// </summary>
    private readonly Dictionary<string, (FitMode Fit, ImageTransform Transform, PixelSize Pixels)> _sessionEdits
        = new(StringComparer.OrdinalIgnoreCase);
    private ImageSource? _editPreviewSource;
    private bool _imageDragActive;
    private Windows.Foundation.Point _imageDragLast;

    private enum LinkSource
    {
        Untouched,
        Text,
        Picked,
        Cleared,
    }

    public GroupPropertyWindow(GroupPropertyWindowContext context, IntPtr ownerHwnd)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        var isCreateMode = context.Target is null;
        Title = isCreateMode ? "新建磁贴组" : "编辑磁贴组";

        _draft = BuildInitialDraft(context.Target);

        _linkBrowseMenu = BuildLinkBrowseMenu();

        // ——— 主页 ———
        _sizeSummary = new TextBlock();
        AutomationProperties.SetAutomationId(_sizeSummary, "grp-size-summary");

        _previewCanvas = new Canvas();
        // 纯 Canvas 无 UIA peer：断言 Id 挂在有 peer 的宿主上（§7.3 契约）
        var previewHost = new ContentControl
        {
            Content = _previewCanvas,
            IsTabStop = false,
        };
        AutomationProperties.SetAutomationId(previewHost, "grp-preview");

        // —— M6 图片编辑（§10）：变换预览画布 + 当前图下拉 + 平移/缩放/两档适配/重置 ——
        _imagePreviewCanvas = new Canvas
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), // 命中面（拖动平移）
        };
        _imagePreviewHost = new ContentControl { Content = _imagePreviewCanvas, IsTabStop = false };
        AutomationProperties.SetAutomationId(_imagePreviewHost, "grp-image-preview");
        _imageCurrent = new ComboBox { MinWidth = 220, PlaceholderText = "（无候选）" };
        AutomationProperties.SetAutomationId(_imageCurrent, "grp-image-current");
        _imageCurrent.SelectionChanged += (_, _) =>
        {
            if (_imageCurrent.SelectedItem is ComboBoxItem item && item.Tag is string path)
            {
                _ = OnEditImageChangedAsync(path);
            }
        };
        _imageZoomIn = new Button { Content = "＋" };
        AutomationProperties.SetAutomationId(_imageZoomIn, "grp-image-zoom-in");
        _imageZoomIn.Click += (_, _) => ZoomEdit(1.25);
        _imageZoomOut = new Button { Content = "−" };
        AutomationProperties.SetAutomationId(_imageZoomOut, "grp-image-zoom-out");
        _imageZoomOut.Click += (_, _) => ZoomEdit(0.8);
        _imageFitCover = new Button { Content = "居中填满" };
        AutomationProperties.SetAutomationId(_imageFitCover, "grp-image-fit-cover");
        _imageFitCover.Click += (_, _) => FitEdit(FitMode.CoverFill);
        _imageFitAll = new Button { Content = "完整适应" };
        AutomationProperties.SetAutomationId(_imageFitAll, "grp-image-fit-all");
        _imageFitAll.Click += (_, _) => FitEdit(FitMode.FitAll);
        _imageReset = new Button { Content = "重置" };
        AutomationProperties.SetAutomationId(_imageReset, "grp-image-reset");
        _imageReset.Click += (_, _) => ResetEdit();
        _imageStatus = new TextBlock { Opacity = 0.7 };
        AutomationProperties.SetAutomationId(_imageStatus, "grp-image-status");
        _imagePreviewCanvas.PointerPressed += OnImagePreviewPointerPressed;
        _imagePreviewCanvas.PointerMoved += OnImagePreviewPointerMoved;
        _imagePreviewCanvas.PointerReleased += OnImagePreviewPointerReleased;
        _imagePreviewCanvas.PointerCanceled += OnImagePreviewPointerLost;
        _imagePreviewCanvas.PointerCaptureLost += OnImagePreviewPointerLost;
        _imagePreviewCanvas.PointerWheelChanged += OnImagePreviewWheel;

        _imageSingle = new RadioButton { Content = "单张图片", GroupName = "grp-image-source" };
        AutomationProperties.SetAutomationId(_imageSingle, "grp-image-source-single");
        _imageMultiple = new RadioButton { Content = "多张图片", GroupName = "grp-image-source" };
        AutomationProperties.SetAutomationId(_imageMultiple, "grp-image-source-multi");
        _imageFolder = new RadioButton { Content = "本机文件夹", GroupName = "grp-image-source" };
        AutomationProperties.SetAutomationId(_imageFolder, "grp-image-source-folder");
        _imagePathsBox = new TextBox { IsReadOnly = true, AcceptsReturn = true, Height = 56, PlaceholderText = "（M5 仅记录路径；共享画布渲染属 M6）" };
        AutomationProperties.SetAutomationId(_imagePathsBox, "grp-image-paths");
        var imageBrowse = new Button { Content = "浏览" };
        AutomationProperties.SetAutomationId(imageBrowse, "grp-image-browse");
        imageBrowse.Click += async (_, _) => await PickImagesAsync();
        _folderPathBox = new TextBox { IsReadOnly = true, PlaceholderText = "（文件夹来源：M6 切换时枚举候选）" };
        AutomationProperties.SetAutomationId(_folderPathBox, "grp-folder-path");
        var folderBrowse = new Button { Content = "浏览" };
        AutomationProperties.SetAutomationId(folderBrowse, "grp-folder-browse");
        folderBrowse.Click += async (_, _) => await PickFolderAsync();

        _backdrop = new ComboBox { MinWidth = 140 };
        _backdrop.Items.Add("模糊补底");
        _backdrop.Items.Add("纯色");
        _backdrop.Items.Add("透明");
        AutomationProperties.SetAutomationId(_backdrop, "grp-backdrop");
        _backdropColorBox = new TextBox { PlaceholderText = "#RRGGBB" };
        AutomationProperties.SetAutomationId(_backdropColorBox, "grp-backdrop-color");
        _backdropColorRow = FormRow("颜色", _backdropColorBox);

        _titleBox = new TextBox { PlaceholderText = "留空 = 隐藏标题" };
        AutomationProperties.SetAutomationId(_titleBox, "grp-title");

        _linkDisplay = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.NoWrap };
        AutomationProperties.SetAutomationId(_linkDisplay, "grp-link");
        _linkEdit = new TextBox { PlaceholderText = "可直接粘贴本地路径或 http(s) 网址" };
        AutomationProperties.SetAutomationId(_linkEdit, "grp-link-edit");
        var linkBrowse = new Button { Content = "浏览", Flyout = _linkBrowseMenu };
        AutomationProperties.SetAutomationId(linkBrowse, "grp-link-browse");
        var linkClear = new Button { Content = "清空链接" };
        AutomationProperties.SetAutomationId(linkClear, "grp-link-clear");
        linkClear.Click += (_, _) =>
        {
            _linkSource = LinkSource.Cleared;
            _pickedEntryDraft = null;
            _linkEdit.Text = string.Empty;
            _linkDisplay.Text = "（清空后保存 → 撤销绑定）";
            ValidateForm();
        };

        _validation = new InfoBar { IsOpen = false, Severity = InfoBarSeverity.Warning, IsClosable = false };
        AutomationProperties.SetAutomationId(_validation, "grp-validation");

        _saveButton = new Button { Content = "保存" };
        AutomationProperties.SetAutomationId(_saveButton, "grp-save");
        _saveButton.Click += (_, _) => Save();
        var cancelButton = new Button { Content = "取消" };
        AutomationProperties.SetAutomationId(cancelButton, "grp-cancel");
        // 程序化 Window.Close() 不触发 AppWindow.Closing（WinUI 3 已知行为）→ 取消钮显式走确认
        cancelButton.Click += (_, _) => RequestClose();
        var enterLayout = new Button { Content = "✎ 进入布局编辑" };
        AutomationProperties.SetAutomationId(enterLayout, "grp-enter-layout");
        enterLayout.Click += (_, _) => EnterLayoutPage();

        _mainPage = BuildMainPage(enterLayout, previewHost, imageBrowse, folderBrowse, linkBrowse, linkClear, cancelButton, _imagePreviewHost, _imageCurrent, _imageZoomOut, _imageZoomIn, _imageFitCover, _imageFitAll, _imageReset, _imageStatus);

        // ——— 布局子页 ———
        _toolDraw = new RadioButton { Content = "画墙", GroupName = "grp-tool" };
        AutomationProperties.SetAutomationId(_toolDraw, "grp-tool-draw");
        _toolErase = new RadioButton { Content = "拆墙", GroupName = "grp-tool" };
        AutomationProperties.SetAutomationId(_toolErase, "grp-tool-erase");

        _colPlus = new Button { Content = "列 +" };
        AutomationProperties.SetAutomationId(_colPlus, "grp-col-plus");
        _colPlus.Click += (_, _) => ResizeOp(session => session.AddColumn());
        _colMinus = new Button { Content = "列 −" };
        AutomationProperties.SetAutomationId(_colMinus, "grp-col-minus");
        _colMinus.Click += (_, _) => ResizeOp(session => session.RemoveColumn());
        _rowPlus = new Button { Content = "行 +" };
        AutomationProperties.SetAutomationId(_rowPlus, "grp-row-plus");
        _rowPlus.Click += (_, _) => ResizeOp(session => session.AddRow());
        _rowMinus = new Button { Content = "行 −" };
        AutomationProperties.SetAutomationId(_rowMinus, "grp-row-minus");
        _rowMinus.Click += (_, _) => ResizeOp(session => session.RemoveRow());

        _undoButton = new Button { Content = "撤销" };
        AutomationProperties.SetAutomationId(_undoButton, "grp-undo");
        _undoButton.Click += (_, _) =>
        {
            if (_session is { } session && session.Undo())
            {
                RefreshLayoutCanvas();
            }
        };
        _redoButton = new Button { Content = "重做" };
        AutomationProperties.SetAutomationId(_redoButton, "grp-redo");
        _redoButton.Click += (_, _) =>
        {
            if (_session is { } session && session.Redo())
            {
                RefreshLayoutCanvas();
            }
        };

        _layoutCanvas = new Canvas
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), // 命中面
        };
        AutomationProperties.SetAutomationId(_layoutCanvas, "grp-layout-canvas");
        var canvasHost = new ScrollViewer
        {
            Content = _layoutCanvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 400,
        };
        // 纯 Canvas 无 UIA peer：断言 Id 挂在有 peer 的宿主上（§7.3 契约）
        AutomationProperties.SetAutomationId(canvasHost, "grp-layout-canvas");
        _layoutCanvas.PointerPressed += OnLayoutPointerPressed;
        _layoutCanvas.PointerMoved += OnLayoutPointerMoved;
        _layoutCanvas.PointerReleased += OnLayoutPointerReleased;
        _layoutCanvas.PointerCanceled += OnLayoutPointerLost;
        _layoutCanvas.PointerCaptureLost += OnLayoutPointerLost;

        _layoutStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(_layoutStatus, "grp-layout-status");
        var confirmLayout = new Button { Content = "确认编辑" };
        AutomationProperties.SetAutomationId(confirmLayout, "grp-layout-confirm");
        confirmLayout.Click += (_, _) => ConfirmLayout();
        var cancelLayout = new Button { Content = "取消编辑" };
        AutomationProperties.SetAutomationId(cancelLayout, "grp-layout-cancel");
        cancelLayout.Click += (_, _) => CancelLayout();

        _layoutPage = BuildLayoutPage(canvasHost, confirmLayout, cancelLayout);

        // ——— 同窗页面切换 ———
        _rootGrid = new Grid();
        _rootGrid.Children.Add(_mainPage);
        _rootGrid.Children.Add(_layoutPage);
        Content = _rootGrid;
        ShowMainPage();

        // ——— 初值与事件 ———
        LoadInitialState();
        _initialDraft = BuildDraft(); // 构造后的真实初值（含字段投影）
        RefreshSummaryAndPreview();
        RefreshImagePreview();
        ValidateForm();
        _ = LoadCandidatesAsync(); // M6 §10：候选枚举与当前图下拉（异步，不阻塞窗口显示）

        _imageSingle.Checked += (_, _) => ValidateForm();
        _imageMultiple.Checked += (_, _) => ValidateForm();
        _imageFolder.Checked += (_, _) => ValidateForm();
        _backdrop.SelectionChanged += (_, _) =>
        {
            _backdropColorRow.Visibility = _backdrop.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            ValidateForm();
        };
        _backdropColorBox.TextChanged += (_, _) => ValidateForm();
        _titleBox.TextChanged += (_, _) => ValidateForm();
        _linkEdit.TextChanged += (_, _) =>
        {
            if (_linkEdit.Text.Length > 0)
            {
                _linkSource = LinkSource.Text;
            }
            else if (_linkSource == LinkSource.Text)
            {
                _linkSource = _pickedEntryDraft is null ? LinkSource.Untouched : LinkSource.Picked;
            }

            ValidateForm();
        };

        ConfigureWindowShape(isCreateMode, ownerHwnd);
        AppWindow.Closing += OnAppWindowClosing;
    }

    // ————————————————————————————— 装配 —————————————————————————————

    private FrameworkElement BuildMainPage(Button enterLayout, FrameworkElement previewHost, Button imageBrowse, Button folderBrowse, Button linkBrowse, Button linkClear, Button cancelButton, FrameworkElement imagePreviewHost, ComboBox imageCurrent, Button zoomOut, Button zoomIn, Button fitCover, Button fitAll, Button reset, TextBlock imageStatus)
    {
        var grid = new Grid { Padding = new Thickness(16), RowSpacing = 6 };
        for (var i = 0; i < 10; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        // §7.3 契约：窗根 AutomationId 挂在标题 TextBlock 上（TextBlock 恒有 UIA peer）
        var header = new TextBlock { Text = Title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(header, "grp-property-window");
        AutomationProperties.SetName(header, Title);
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var summaryRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        summaryRow.Children.Add(_sizeSummary);
        summaryRow.Children.Add(enterLayout);
        Grid.SetRow(summaryRow, 1);
        grid.Children.Add(summaryRow);

        Grid.SetRow(previewHost, 2);
        grid.Children.Add(previewHost);

        // M6 §10：变换预览（拖动=平移，滚轮=锚点缩放）与编辑控件同行左右布局（避免主页溢出滚动）
        var editColumn = new StackPanel { Spacing = 6 };
        var currentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        currentRow.Children.Add(new TextBlock { Text = "当前图", VerticalAlignment = VerticalAlignment.Center, Width = 48 });
        currentRow.Children.Add(imageCurrent);
        editColumn.Children.Add(currentRow);
        var editButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        editButtons.Children.Add(zoomOut);
        editButtons.Children.Add(zoomIn);
        editButtons.Children.Add(fitCover);
        editButtons.Children.Add(fitAll);
        editButtons.Children.Add(reset);
        editColumn.Children.Add(editButtons);
        editColumn.Children.Add(imageStatus);
        var imageRow = new Grid { ColumnSpacing = 12 };
        imageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        imageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        imagePreviewHost.HorizontalAlignment = HorizontalAlignment.Left;
        imagePreviewHost.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(imagePreviewHost, 0);
        imageRow.Children.Add(imagePreviewHost);
        Grid.SetColumn(editColumn, 1);
        imageRow.Children.Add(editColumn);
        Grid.SetRow(imageRow, 3);
        grid.Children.Add(imageRow);

        var sourceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sourceRow.Children.Add(new TextBlock { Text = "图片", VerticalAlignment = VerticalAlignment.Center, Width = 48 });
        sourceRow.Children.Add(_imageSingle);
        sourceRow.Children.Add(_imageMultiple);
        sourceRow.Children.Add(_imageFolder);
        Grid.SetRow(sourceRow, 4);
        grid.Children.Add(sourceRow);

        var pathsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pathsRow.Children.Add(new TextBlock { Text = "路径", VerticalAlignment = VerticalAlignment.Center, Width = 48 });
        _imagePathsBox.Width = 380;
        pathsRow.Children.Add(_imagePathsBox);
        pathsRow.Children.Add(imageBrowse);
        Grid.SetRow(pathsRow, 5);
        grid.Children.Add(pathsRow);

        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        folderRow.Children.Add(new TextBlock { Text = "文件夹", VerticalAlignment = VerticalAlignment.Center, Width = 48 });
        _folderPathBox.Width = 380;
        folderRow.Children.Add(_folderPathBox);
        folderRow.Children.Add(folderBrowse);
        Grid.SetRow(folderRow, 6);
        grid.Children.Add(folderRow);

        var backdropRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        backdropRow.Children.Add(new TextBlock { Text = "背景", VerticalAlignment = VerticalAlignment.Center, Width = 48 });
        backdropRow.Children.Add(_backdrop);
        Grid.SetRow(backdropRow, 7);
        grid.Children.Add(backdropRow);

        _backdropColorRow.Margin = new Thickness(56, 0, 0, 0);
        Grid.SetRow(_backdropColorRow, 8);
        grid.Children.Add(_backdropColorRow);

        var bottom = new StackPanel { Spacing = 8 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock { Text = "文字", VerticalAlignment = VerticalAlignment.Center, Width = 48 });
        _titleBox.Width = 430;
        titleRow.Children.Add(_titleBox);
        bottom.Children.Add(titleRow);

        var linkRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        linkRow.Children.Add(new TextBlock { Text = "链接", VerticalAlignment = VerticalAlignment.Center, Width = 48 });
        _linkDisplay.Width = 250;
        linkRow.Children.Add(_linkDisplay);
        linkRow.Children.Add(linkBrowse);
        linkRow.Children.Add(linkClear);
        bottom.Children.Add(linkRow);

        var linkEditRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        linkEditRow.Children.Add(new TextBlock { Text = "编辑", VerticalAlignment = VerticalAlignment.Center, Width = 48 });
        _linkEdit.Width = 430;
        linkEditRow.Children.Add(_linkEdit);
        bottom.Children.Add(linkEditRow);

        bottom.Children.Add(_validation);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_saveButton);
        buttons.Children.Add(cancelButton);
        bottom.Children.Add(buttons);
        Grid.SetRow(bottom, 9);
        grid.Children.Add(bottom);

        return new ScrollViewer
        {
            Content = grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, // M6 主页增高：溢出滚动（窗口尺寸不变）
        };
    }

    private Grid BuildLayoutPage(FrameworkElement canvasHost, Button confirmLayout, Button cancelLayout)
    {
        var grid = new Grid { Padding = new Thickness(16), RowSpacing = 10 };
        for (var i = 0; i < 5; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var header = new TextBlock { Text = "布局编辑（画墙 / 拆墙）", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetName(header, "布局编辑");
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var toolsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolsRow.Children.Add(new TextBlock { Text = "工具（先选）", VerticalAlignment = VerticalAlignment.Center });
        toolsRow.Children.Add(_toolDraw);
        toolsRow.Children.Add(_toolErase);
        toolsRow.Children.Add(_colMinus);
        toolsRow.Children.Add(_colPlus);
        toolsRow.Children.Add(_rowMinus);
        toolsRow.Children.Add(_rowPlus);
        toolsRow.Children.Add(_undoButton);
        toolsRow.Children.Add(_redoButton);
        Grid.SetRow(toolsRow, 1);
        grid.Children.Add(toolsRow);

        Grid.SetRow(canvasHost, 2);
        grid.Children.Add(canvasHost);

        Grid.SetRow(_layoutStatus, 3);
        grid.Children.Add(_layoutStatus);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(confirmLayout);
        buttons.Children.Add(cancelLayout);
        Grid.SetRow(buttons, 4);
        grid.Children.Add(buttons);

        return grid;
    }

    private static StackPanel FormRow(string label, params FrameworkElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 48 });
        foreach (var child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }

    private MenuFlyout BuildLinkBrowseMenu()
    {
        var menu = new MenuFlyout();
        AutomationProperties.SetAutomationId(menu, "grp-link-browse-menu");
        var pickFile = new MenuFlyoutItem { Text = "选择 .lnk/.url 或其他文件…" };
        pickFile.Click += async (_, _) => await PickLinkFileAsync();
        var pickFolder = new MenuFlyoutItem { Text = "选择文件夹…" };
        pickFolder.Click += async (_, _) => await PickLinkFolderAsync();
        menu.Items.Add(pickFile);
        menu.Items.Add(pickFolder);
        return menu;
    }

    // ————————————————————————————— 两级草稿状态机（§8.2/§9.2） —————————————————————————————

    private static GroupEditDraft BuildInitialDraft(GroupObject? target)
    {
        if (target is null)
        {
            // 创建模式默认值（B02）：4×4 十六块 1×1、链接空、Backdrop=BlurFill、Images=None
            return new GroupEditDraft
            {
                Size = new GridSize(4, 4),
                Partitions = PartitionLayout.FullGrid(new GridSize(4, 4)).Partitions,
            };
        }

        return new GroupEditDraft
        {
            Size = new GridSize(target.Bounds.Width, target.Bounds.Height),
            Partitions = target.Partitions,
            Images = target.Images,
            Backdrop = target.Visual.Backdrop,
            BackdropColorHex = target.Visual.Backdrop == BackdropKind.SolidColor ? target.Visual.BackgroundColor : null,
            TitleText = target.Entry is null
                ? target.Visual.TitleText ?? string.Empty
                : target.Visual.ShowTitle ? EntryNames.BaseNameOf(target.Entry.RelativePath) : string.Empty,
            Entry = new EntryDraft.KeepCurrent(), // 未触碰链接 → 不重写入口
            Carousel = target.Carousel,           // 透传：编辑属性不重置轮播基准（§7.3）
        };
    }

    private void EnterLayoutPage()
    {
        // 进画笔 = 子草稿会话（session 从当前 draft 快照出发）
        _session = new PartitionEditSession(_draft.Size, _draft.Partitions);
        ShowLayoutPage();
    }

    private void ConfirmLayout()
    {
        if (_session is null)
        {
            return;
        }

        // 确认 = 接受子草稿回主页（session 丢弃；主墙仍不变，B07）
        _draft = _draft with { Size = _session.Current.Size, Partitions = _session.Current.Partitions };
        _session = null;
        ShowMainPage();
        RefreshSummaryAndPreview();
        ValidateForm();
    }

    private void CancelLayout()
    {
        // 取消编辑 = 回进画笔前的状态（session 丢弃、draft 不变）
        _session = null;
        ShowMainPage();
    }

    private void ShowMainPage()
    {
        _mainPage.Visibility = Visibility.Visible;
        _layoutPage.Visibility = Visibility.Collapsed;
    }

    private void ShowLayoutPage()
    {
        _mainPage.Visibility = Visibility.Collapsed;
        _layoutPage.Visibility = Visibility.Visible;
        RefreshLayoutCanvas();
    }

    // ————————————————————————————— 布局子页（§10.3） —————————————————————————————

    private EditTool? SelectedTool => _toolDraw.IsChecked == true ? EditTool.Draw
        : _toolErase.IsChecked == true ? EditTool.Erase
        : null; // 必须先选工具：画布不响应未选工具的笔画（§8.1）

    private void ResizeOp(Func<PartitionEditSession, bool> op)
    {
        if (_session is null)
        {
            return;
        }

        if (!op(_session))
        {
            _layoutStatus.Text = "已到界限（列 2–8，行 2–25）";
            return;
        }

        RefreshLayoutCanvas();
    }

    private void RefreshLayoutCanvas()
    {
        if (_session is null)
        {
            return;
        }

        var layout = _session.Current;
        var m = CanvasMetrics;
        _layoutCanvas.Children.Clear();
        _layoutCanvas.Width = (2 * m.Margin) + m.WidthOf(layout.Size.Columns);
        _layoutCanvas.Height = (2 * m.Margin) + m.WidthOf(layout.Size.Rows);

        // 分块填充（M5 无图 → 组底色占位）
        foreach (var p in layout.Partitions)
        {
            var fill = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = m.WidthOf(p.Width),
                Height = m.HeightOf(p.Height),
                Fill = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.25 },
            };
            Canvas.SetLeft(fill, m.Margin + (p.Column * m.Pitch));
            Canvas.SetTop(fill, m.Margin + (p.Row * m.Pitch));
            _layoutCanvas.Children.Add(fill);
        }

        // 实线 = 已有墙 / 虚线 = 可操作边界（线型差异区分，P1 §3.3 可访问性）
        foreach (var edge in AllInternalEdges(layout.Size))
        {
            DrawBoundary(edge, layout.IsSolid(edge));
        }

        _undoButton.IsEnabled = _session.CanUndo;
        _redoButton.IsEnabled = _session.CanRedo;
        _colPlus.IsEnabled = layout.Size.Columns < WallGrid.GroupMaxColumns;
        _colMinus.IsEnabled = layout.Size.Columns > WallGrid.GroupMinColumns;
        _rowPlus.IsEnabled = layout.Size.Rows < WallGrid.GroupMaxRows;
        _rowMinus.IsEnabled = layout.Size.Rows > WallGrid.GroupMinRows;
        _layoutStatus.Text = $"当前 {layout.Size.Columns} 列 × {layout.Size.Rows} 行，{layout.PartitionCount} 块";
        AutomationProperties.SetName(_layoutStatus, _layoutStatus.Text);
    }

    /// <summary>
    /// 逐边绘制墙段（§8.1/P1 §3.3「实线=已有墙 / 虚线=可操作边界」的逐边语义）：
    /// 每条 WallEdge 恰跨一对相邻格，线段长度 = 一个格芯（CellCore），起于该格的步距起点——
    /// 同一内部列/行上虚实混合时各画各段，互不覆盖（全列/全高画法会让实线盖掉同线上的虚线段）。
    /// </summary>
    private void DrawBoundary(WallEdge edge, bool solid)
    {
        var m = CanvasMetrics;
        if (edge.IsVertical)
        {
            // 竖边 V(X,Y)：线段占格行 Y 的格芯带，位于格 X−1 与 X 之间的缝隙中线
            var x = m.Margin + (edge.X * m.Pitch) - (m.Gap / 2);
            var y = m.Margin + (edge.Y * m.Pitch);
            if (solid)
            {
                var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = 2,
                    Height = m.CellCore,
                    Fill = new SolidColorBrush(Microsoft.UI.Colors.Black),
                };
                Canvas.SetLeft(rect, x - 1);
                Canvas.SetTop(rect, y);
                _layoutCanvas.Children.Add(rect);
            }
            else
            {
                DrawDashed(x, y, thickness: 1, length: m.CellCore, horizontal: false);
            }
        }
        else
        {
            // 横边 H(X,Y)：线段占格列 X 的格芯带，位于格 Y−1 与 Y 之间的缝隙中线
            var x = m.Margin + (edge.X * m.Pitch);
            var y = m.Margin + (edge.Y * m.Pitch) - (m.Gap / 2);
            if (solid)
            {
                var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = m.CellCore,
                    Height = 2,
                    Fill = new SolidColorBrush(Microsoft.UI.Colors.Black),
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y - 1);
                _layoutCanvas.Children.Add(rect);
            }
            else
            {
                DrawDashed(x, y, thickness: 1, length: m.CellCore, horizontal: true);
            }
        }
    }

    /// <summary>虚线用短段矩形拼出（线型差异不只靠颜色，P1 §3.3；不依赖特殊描边 API）。</summary>
    private void DrawDashed(double x, double y, double thickness, double length, bool horizontal)
    {
        for (var offset = 0.0; offset < length; offset += 8)
        {
            var segment = Math.Min(4, length - offset);
            var dash = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = horizontal ? segment : thickness,
                Height = horizontal ? thickness : segment,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.7 },
            };
            Canvas.SetLeft(dash, horizontal ? x + offset : x);
            Canvas.SetTop(dash, horizontal ? y : y + offset);
            _layoutCanvas.Children.Add(dash);
        }
    }

    private void OnLayoutPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null || SelectedTool is null)
        {
            return; // 未选工具：画布不响应（§8.1）
        }

        var point = e.GetCurrentPoint(_layoutCanvas);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _strokeActive = true;
        _layoutCanvas.CapturePointer(e.Pointer);
        _session.BeginStroke(); // 单击 = Begin+Stroke+End 同样适用
        HandleLayoutPoint(point.Position);
        e.Handled = true;
    }

    private void OnLayoutPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(_layoutCanvas);
        if (_strokeActive)
        {
            HandleLayoutPoint(point.Position);
        }
        else
        {
            ShowHoverPreview(point.Position); // 悬停（未按下）：只出预览不改状态
        }
    }

    private void OnLayoutPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndStroke();
        _layoutCanvas.ReleasePointerCapture(e.Pointer);
    }

    private void OnLayoutPointerLost(object sender, PointerRoutedEventArgs e) => EndStroke();

    private void EndStroke()
    {
        if (!_strokeActive || _session is null)
        {
            _strokeActive = false;
            return;
        }

        _strokeActive = false;
        _session.EndStroke(); // 一次连续笔画 = 一个撤销单元（§8.4）
        RefreshLayoutCanvas();
    }

    private void HandleLayoutPoint(Windows.Foundation.Point position)
    {
        if (_session is null || SelectedTool is not { } tool)
        {
            return;
        }

        if (EdgeFromPoint(position) is not { } edge)
        {
            return;
        }

        var outcome = _session.Stroke(edge, tool);
        _layoutStatus.Text = outcome switch
        {
            StrokeOutcome.Applied => $"已{(tool == EditTool.Draw ? "画墙" : "拆墙")}：{edge}",
            StrokeOutcome.Debounced => "同一笔画已处理该边界（去抖，不反向切换）",
            StrokeOutcome.SolidForDraw => "此处已是实线：画墙忽略",
            StrokeOutcome.DashedForErase => "此处无墙（虚线）：拆墙忽略",
            _ => "外部边框不可操作",
        };
        AutomationProperties.SetName(_layoutStatus, _layoutStatus.Text);
        RefreshLayoutCanvas();
    }

    private void ShowHoverPreview(Windows.Foundation.Point position)
    {
        if (_session is null || SelectedTool is not { } tool)
        {
            return;
        }

        if (EdgeFromPoint(position) is not { } edge)
        {
            return;
        }

        var preview = _session.Preview(edge, tool);
        var baseText = $"当前 {_session.Current.Size.Columns} 列 × {_session.Current.Size.Rows} 行，{_session.Current.PartitionCount} 块；";
        _layoutStatus.Text = preview switch
        {
            { Applicable: true, NewLineExtent: { } line } =>
                $"{baseText}笔画预览：将在分块内新增整条{(edge.IsVertical ? "竖" : "横")}线（{line.Column},{line.Row}，{line.Width}×{line.Height}）",
            { Applicable: true, MergeExtent: { } merge } =>
                $"{baseText}笔画预览：将把范围（{merge.Column},{merge.Row}，{merge.Width}×{merge.Height}）合并为一块",
            { Outcome: StrokeOutcome.SolidForDraw } => $"{baseText}此处已是实线：画墙忽略",
            { Outcome: StrokeOutcome.DashedForErase } => $"{baseText}此处无墙（虚线）：拆墙忽略",
            _ => $"{baseText}外部边框不可操作",
        };
        AutomationProperties.SetName(_layoutStatus, _layoutStatus.Text);
        RefreshLayoutCanvas();

        // 预览叠加（落笔前高亮，§8.2/§8.3）
        if (preview.Applicable && preview.NewLineExtent is { } lineExtent)
        {
            AddPreviewLine(lineExtent);
        }
        else if (preview.Applicable && preview.MergeExtent is { } mergeExtent)
        {
            AddPreviewMerge(mergeExtent);
        }
    }

    private void AddPreviewLine(GridRect line)
    {
        var m = CanvasMetrics;
        Microsoft.UI.Xaml.Shapes.Rectangle rect;
        if (line.Width == 1) // 竖线段
        {
            rect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 3,
                Height = m.HeightOf(line.Height),
                Fill = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen) { Opacity = 0.9 },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(rect, m.Margin + (line.Column * m.Pitch) - (m.Gap / 2) - 0.5);
            Canvas.SetTop(rect, m.Margin + (line.Row * m.Pitch));
        }
        else
        {
            rect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = m.WidthOf(line.Width),
                Height = 3,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen) { Opacity = 0.9 },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(rect, m.Margin + (line.Column * m.Pitch));
            Canvas.SetTop(rect, m.Margin + (line.Row * m.Pitch) - (m.Gap / 2) - 0.5);
        }

        _layoutCanvas.Children.Add(rect);
    }

    private void AddPreviewMerge(GridRect merge)
    {
        var m = CanvasMetrics;
        var outline = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = m.WidthOf(merge.Width),
            Height = m.HeightOf(merge.Height),
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.Orange),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(outline, m.Margin + (merge.Column * m.Pitch));
        Canvas.SetTop(outline, m.Margin + (merge.Row * m.Pitch));
        _layoutCanvas.Children.Add(outline);
    }

    /// <summary>
    /// 单击边裁决（UI 折算层，§15.2）：按 DIP 距离取最近的内部格线（等距先横后竖）；
    /// Core 只见 WallEdge，调整此处不触引擎。
    /// </summary>
    private WallEdge? EdgeFromPoint(Windows.Foundation.Point position)
    {
        if (_session is null)
        {
            return null;
        }

        var m = CanvasMetrics;
        var size = _session.Current.Size;
        var localX = position.X - m.Margin;
        var localY = position.Y - m.Margin;

        var bestVertical = double.MaxValue;
        var bestVerticalIndex = -1;
        for (var x = 1; x < size.Columns; x++)
        {
            var distance = Math.Abs(localX - ((x * m.Pitch) - (m.Gap / 2)));
            if (distance < bestVertical)
            {
                bestVertical = distance;
                bestVerticalIndex = x;
            }
        }

        var bestHorizontal = double.MaxValue;
        var bestHorizontalIndex = -1;
        for (var y = 1; y < size.Rows; y++)
        {
            var distance = Math.Abs(localY - ((y * m.Pitch) - (m.Gap / 2)));
            if (distance < bestHorizontal)
            {
                bestHorizontal = distance;
                bestHorizontalIndex = y;
            }
        }

        if (bestHorizontalIndex < 0 && bestVerticalIndex < 0)
        {
            return null;
        }

        if (bestVerticalIndex >= 0 && (bestHorizontalIndex < 0 || bestVertical < bestHorizontal))
        {
            var row = Math.Clamp((int)(localY / m.Pitch), 0, size.Rows - 1);
            return new WallEdge(true, bestVerticalIndex, row);
        }

        var column = Math.Clamp((int)(localX / m.Pitch), 0, size.Columns - 1);
        return new WallEdge(false, column, bestHorizontalIndex);
    }

    private static IEnumerable<WallEdge> AllInternalEdges(GridSize size)
    {
        for (var y = 0; y < size.Rows; y++)
        {
            for (var x = 1; x < size.Columns; x++)
            {
                yield return new WallEdge(true, x, y);
            }
        }

        for (var y = 1; y < size.Rows; y++)
        {
            for (var x = 0; x < size.Columns; x++)
            {
                yield return new WallEdge(false, x, y);
            }
        }
    }

    // ————————————————————————————— 主页摘要与校验 —————————————————————————————

    private void RefreshSummaryAndPreview()
    {
        _sizeSummary.Text = $"当前 {_draft.Size.Columns} 列 × {_draft.Size.Rows} 行，{_draft.Partitions.Count} 块";
        AutomationProperties.SetName(_sizeSummary, _sizeSummary.Text);

        _previewCanvas.Children.Clear();
        _previewCanvas.Width = (_draft.Size.Columns * PreviewCell) + 2;
        _previewCanvas.Height = (_draft.Size.Rows * PreviewCell) + 2;
        foreach (var p in _draft.Partitions)
        {
            var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = (p.Width * PreviewCell) - 2,
                Height = (p.Height * PreviewCell) - 2,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.4 },
            };
            Canvas.SetLeft(rect, (p.Column * PreviewCell) + 1);
            Canvas.SetTop(rect, (p.Row * PreviewCell) + 1);
            _previewCanvas.Children.Add(rect);
        }
    }

    private GroupEditDraft BuildDraft() => _draft with
    {
        Images = new GroupImages
        {
            Kind = _imageFolder.IsChecked == true ? GroupImageSourceKind.Folder
                : _imageSingle.IsChecked == true ? GroupImageSourceKind.Single
                : _imageMultiple.IsChecked == true ? GroupImageSourceKind.Multiple
                : GroupImageSourceKind.None,
            ImagePaths = CollectImagePaths(),
            FolderPath = _imageFolder.IsChecked == true && _folderPathBox.Text.Length > 0 ? _folderPathBox.Text : null,
            Transforms = CollectTransforms(),
        },
        Backdrop = _backdrop.SelectedIndex switch
        {
            1 => BackdropKind.SolidColor,
            2 => BackdropKind.Transparent,
            _ => BackdropKind.BlurFill,
        },
        BackdropColorHex = _backdrop.SelectedIndex == 1 && _backdropColorBox.Text.Length > 0 ? _backdropColorBox.Text : null,
        TitleText = _titleBox.Text,
        Entry = DeriveEntryDraft(),
        Carousel = _context.Target?.Carousel, // 透传
    };

    private IReadOnlyList<string> CollectImagePaths()
    {
        if (_imageFolder.IsChecked == true || string.IsNullOrWhiteSpace(_imagePathsBox.Text))
        {
            return [];
        }

        return [.. _imagePathsBox.Text
            .Split('\n', '\r')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)];
    }

    /// <summary>把当前编辑图的未保存变更写入会话草稿（切图与保存前都会调用）。</summary>
    private void StashSessionEdit()
    {
        if (_editImageId is not null && _editTransform is not null && _editPixels is not null)
        {
            _sessionEdits[_editImageId] = (_editFit, _editTransform.Value, _editPixels.Value);
        }
    }

    /// <summary>
    /// M6 §10：变换进草稿——会话内全部编辑过图（含当前图）依次经 ImageTransformSet.Upsert
    /// （默认态自动剔除，XF-9）；保存 = 会话编辑整体提交，取消关窗 = 字典随窗口全弃。
    /// </summary>
    private IReadOnlyList<ImageTransformRecord> CollectTransforms()
    {
        StashSessionEdit();
        var result = _context.Target?.Images.Transforms ?? _draft.Images.Transforms;
        var canvas = SharedCanvas.CanvasSize(_draft.Size, CanvasMetricsBase);
        foreach (var (imageId, edit) in _sessionEdits)
        {
            result = ImageTransformSet.Upsert(result, imageId, edit.Fit, edit.Transform, edit.Pixels, canvas);
        }

        return result;
    }

    // ————————————————————————————— M6 图片编辑（§10） —————————————————————————————

    /// <summary>预览缩放比：显示坐标 = 画布 DIP × 该比（CanvasMetrics 换算同布局子页，§15.1 纪律）。</summary>
    private static double PreviewRatio => PreviewCell / GridMetrics.Default.CellCore;

    private static readonly GridMetrics CanvasMetricsBase = GridMetrics.Default;

    private async Task LoadCandidatesAsync()
    {
        var images = _context.Target?.Images ?? _draft.Images;
        if (images.Kind == GroupImageSourceKind.Folder)
        {
            _candidates = ImageCatalog.EnumerateCandidates(images, _context.Files);
        }
        else if (images.ImagePaths.Count > 0)
        {
            _candidates = [.. images.ImagePaths];
        }
        else if (!string.IsNullOrWhiteSpace(_folderPathBox.Text))
        {
            // 创建模式选了文件夹但未保存：即时枚举预览清单（§10 表「Folder 保存时枚举预览清单」）
            _candidates = ImageCatalog.EnumerateCandidates(
                new GroupImages { Kind = GroupImageSourceKind.Folder, FolderPath = _folderPathBox.Text },
                _context.Files);
        }
        else
        {
            _candidates = CollectImagePaths();
        }

        _imageCurrent.Items.Clear();
        foreach (var candidate in _candidates)
        {
            _imageCurrent.Items.Add(new ComboBoxItem { Content = Path.GetFileName(candidate), Tag = candidate });
        }

        // 当前编辑对象 = Carousel.CurrentImageId（无 → 首个候选，§10）
        var current = _context.Target?.Carousel?.CurrentImageId;
        var index = current is null ? 0 : _candidates.Select((c, i) => (c, i)).FirstOrDefault(x => string.Equals(x.c, current, StringComparison.OrdinalIgnoreCase)).i;
        if (index < 0)
        {
            index = 0;
        }

        if (_candidates.Count == 0)
        {
            _editImageId = null;
            _editPixels = null;
            _editTransform = null;
            _imageStatus.Text = "无图片候选：先在下方选择图片来源。";
            AutomationProperties.SetName(_imageStatus, _imageStatus.Text);
            return;
        }

        _imageCurrent.SelectedIndex = index;
        await OnEditImageChangedAsync(_candidates[index], selectInCombo: false);
    }

    private async Task OnEditImageChangedAsync(string path, bool selectInCombo = true)
    {
        StashSessionEdit(); // 切走前暂存上一张的未保存编辑（切回即恢复，不静默丢弃）
        _editImageId = path;
        _editPixels = null;
        _editTransform = null;
        if (selectInCombo)
        {
            for (var i = 0; i < _imageCurrent.Items.Count; i++)
            {
                if (_imageCurrent.Items[i] is ComboBoxItem { Tag: string tag } && string.Equals(tag, path, StringComparison.OrdinalIgnoreCase))
                {
                    _imageCurrent.SelectedIndex = i;
                    break;
                }
            }
        }

        try
        {
            var pixels = await ImageLoader.GetPixelSizeAsync(path); // 异步读尺寸：不阻塞 UI 线程
            if (_editImageId != path)
            {
                return; // 用户已切换到别的图：丢弃过期结果
            }

            if (pixels is not { } size || !size.IsValid)
            {
                _imageStatus.Text = "图片加载失败（文件缺失或无法解码）。";
                AutomationProperties.SetName(_imageStatus, _imageStatus.Text);
                RefreshImagePreview();
                return;
            }

            _editPixels = size;
            _editPreviewSource = new BitmapImage(new Uri(path)) { DecodePixelWidth = 512 }; // 预览低清即可（§13 风险 7）
            if (_sessionEdits.TryGetValue(path, out var sessionEdit))
            {
                _editFit = sessionEdit.Fit;
                _editTransform = sessionEdit.Transform; // 会话内已编辑：恢复草稿值
            }
            else
            {
                var found = ImageTransformSet.Find(_context.Target?.Images.Transforms ?? [], path);
                _editFit = found?.Fit ?? FitMode.CoverFill;
                _editTransform = found?.Transform
                    ?? SharedCanvasTransform.DefaultTransform(size, SharedCanvas.CanvasSize(_draft.Size, CanvasMetricsBase), FitMode.CoverFill);
            }
            _imageStatus.Text = $"{Path.GetFileName(path)}  {size.Width:0}×{size.Height:0} px";
            AutomationProperties.SetName(_imageStatus, _imageStatus.Text);
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or ArgumentException)
        {
            _imageStatus.Text = $"图片加载失败：{ex.Message}";
            AutomationProperties.SetName(_imageStatus, _imageStatus.Text);
        }

        RefreshImagePreview();
        ValidateForm();
    }

    private void ZoomEdit(double factor)
    {
        if (_editTransform is not { } t || _editPixels is not { } pixels)
        {
            return;
        }

        var canvas = SharedCanvas.CanvasSize(_draft.Size, CanvasMetricsBase);
        var anchor = new DipPoint(canvas.Width / 2, canvas.Height / 2); // 无指针语义 → 锚点=画布中心（XF-2）
        _editTransform = SharedCanvasTransform.ZoomAt(t, factor, anchor, SharedCanvasTransform.CoverFillScale(pixels, canvas));
        RefreshImagePreview();
        ValidateForm();
    }

    private void FitEdit(FitMode mode)
    {
        if (_editPixels is not { } pixels)
        {
            return;
        }

        var canvas = SharedCanvas.CanvasSize(_draft.Size, CanvasMetricsBase);
        _editFit = mode;
        _editTransform = SharedCanvasTransform.Reset(pixels, canvas, mode); // 两档适配 = 重置到哪一档（§10）
        RefreshImagePreview();
        ValidateForm();
    }

    private void ResetEdit()
    {
        if (_editPixels is not { } pixels)
        {
            return;
        }

        var canvas = SharedCanvas.CanvasSize(_draft.Size, CanvasMetricsBase);
        _editTransform = SharedCanvasTransform.Reset(pixels, canvas, _editFit); // 回当前档居中初始（XF 幂等）
        RefreshImagePreview();
        ValidateForm();
    }

    private void OnImagePreviewPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_editTransform is null)
        {
            return;
        }

        _imageDragActive = true;
        _imageDragLast = e.GetCurrentPoint(_imagePreviewCanvas).Position;
        _imagePreviewCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnImagePreviewPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_imageDragActive || _editTransform is not { } t)
        {
            return;
        }

        var position = e.GetCurrentPoint(_imagePreviewCanvas).Position;
        var ratio = PreviewRatio;
        _editTransform = SharedCanvasTransform.Translate(t, (position.X - _imageDragLast.X) / ratio, (position.Y - _imageDragLast.Y) / ratio);
        _imageDragLast = position;
        RefreshImagePreview();
        e.Handled = true;
    }

    private void OnImagePreviewPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndImageDrag(e);
        ValidateForm();
    }

    private void OnImagePreviewPointerLost(object sender, PointerRoutedEventArgs e) => EndImageDrag(e);

    private void EndImageDrag(PointerRoutedEventArgs e)
    {
        if (!_imageDragActive)
        {
            return;
        }

        _imageDragActive = false;
        _imagePreviewCanvas.ReleasePointerCapture(e.Pointer);
    }

    private void OnImagePreviewWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_editTransform is not { } t || _editPixels is not { } pixels)
        {
            return;
        }

        var point = e.GetCurrentPoint(_imagePreviewCanvas);
        var ratio = PreviewRatio;
        var anchor = new DipPoint(point.Position.X / ratio, point.Position.Y / ratio); // 指针下的画布点为锚（XF-2）
        var factor = point.Properties.MouseWheelDelta > 0 ? 1.25 : 0.8;
        var canvas = SharedCanvas.CanvasSize(_draft.Size, CanvasMetricsBase);
        _editTransform = SharedCanvasTransform.ZoomAt(t, factor, anchor, SharedCanvasTransform.CoverFillScale(pixels, canvas));
        RefreshImagePreview();
        e.Handled = true;
    }

    /// <summary>预览渲染：与主墙同一 ClipFor/偏移数学 × 预览比例（§10 纪律：禁止自绘第二套裁剪数学）。</summary>
    private void RefreshImagePreview()
    {
        _imagePreviewCanvas.Children.Clear();
        var ratio = PreviewRatio;
        var canvas = SharedCanvas.CanvasSize(_draft.Size, CanvasMetricsBase);
        _imagePreviewCanvas.Width = (canvas.Width * ratio) + 2;
        _imagePreviewCanvas.Height = (canvas.Height * ratio) + 2;
        // 与主墙 holder 同构：预览内容限幅在画布矩形内（缩放溢出不出界）
        _imagePreviewCanvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, _imagePreviewCanvas.Width, _imagePreviewCanvas.Height) };

        // 底色示意（Q6：SolidColor 模式铺配置色；BlurFill/透明透出窗底）
        if (_backdrop.SelectedIndex == 1)
        {
            _imagePreviewCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = (canvas.Width * ratio) + 2,
                Height = (canvas.Height * ratio) + 2,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.25 },
                IsHitTestVisible = false,
            });
        }

        // 图像：位置/尺寸 = 变换 × 比例；Clip = 预览画布（与主墙 holder 同构）
        if (_editTransform is { } t && _editPixels is { } pixels && _editPreviewSource is { } source)
        {
            var image = new Microsoft.UI.Xaml.Controls.Image
            {
                Source = _editPreviewSource,
                Width = pixels.Width * t.Scale * ratio,
                Height = pixels.Height * t.Scale * ratio,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(image, (t.OffsetX * ratio) + 1);
            Canvas.SetTop(image, (t.OffsetY * ratio) + 1);
            _imagePreviewCanvas.Children.Add(image);
        }

        // 分区缝示意：组内相对坐标 × 比例（与 grp-preview 同款语义）
        foreach (var p in _draft.Partitions)
        {
            var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = ((p.Column * GridMetrics.Default.Pitch) + GridMetrics.Default.WidthOf(p.Width)) * ratio,
                Height = 1,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.6 },
                IsHitTestVisible = false,
            };
            var lineX = (p.Column * GridMetrics.Default.Pitch) + GridMetrics.Default.WidthOf(p.Width);
            if (lineX < canvas.Width)
            {
                Canvas.SetLeft(rect, (lineX * ratio) + 1);
                Canvas.SetTop(rect, (p.Row * GridMetrics.Default.Pitch * ratio) + 1);
                _imagePreviewCanvas.Children.Add(rect);
            }
        }
    }

    /// <summary>链接草稿推导（复用 M4 §7.1 五态与浏览菜单语义）。</summary>
    private EntryDraft? DeriveEntryDraft() => _linkSource switch
    {
        LinkSource.Untouched => _context.Target is null ? null : new EntryDraft.KeepCurrent(),
        LinkSource.Text => DeriveFromEditText(_linkEdit.Text),
        LinkSource.Picked => _pickedEntryDraft,
        LinkSource.Cleared => new EntryDraft.NoneDraft(),
        _ => null,
    };

    private EntryDraft? DeriveFromEditText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var isHttpUrl = Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        var currentEntryKind = _context.Target?.Entry is { } entry
            ? EntryNames.KindOfRelativePath(entry.RelativePath)
            : EntryKind.None;

        if (isHttpUrl)
        {
            return currentEntryKind == EntryKind.Url ? new EntryDraft.EditUrlLine(text) : new EntryDraft.CreateFromUrl(text);
        }

        // 手输 .lnk/.url 路径与浏览一致：完整副本，禁止二层入口（C29 关注点）
        if (EntryNames.KindOfRelativePath(text) != EntryKind.None)
        {
            return new EntryDraft.CopyFromFile(text);
        }

        return currentEntryKind == EntryKind.Lnk ? new EntryDraft.EditLnkTarget(text) : new EntryDraft.CreateForPath(text);
    }

    private void LoadInitialState()
    {
        var target = _context.Target;
        _imageSingle.IsChecked = target?.Images.Kind == GroupImageSourceKind.Single;
        _imageMultiple.IsChecked = target?.Images.Kind == GroupImageSourceKind.Multiple;
        _imageFolder.IsChecked = target?.Images.Kind == GroupImageSourceKind.Folder;
        _imagePathsBox.Text = target is { Images.ImagePaths.Count: > 0 }
            ? string.Join(Environment.NewLine, target.Images.ImagePaths)
            : string.Empty;
        _folderPathBox.Text = target?.Images.FolderPath ?? string.Empty;
        _backdrop.SelectedIndex = target?.Visual.Backdrop switch
        {
            BackdropKind.SolidColor => 1,
            BackdropKind.Transparent => 2,
            _ => 0,
        };
        _backdropColorBox.Text = target?.Visual.Backdrop == BackdropKind.SolidColor
            ? target.Visual.BackgroundColor ?? string.Empty
            : string.Empty;
        _titleBox.Text = target is null
            ? string.Empty
            : target.Entry is not null
                ? target.Visual.ShowTitle ? EntryNames.BaseNameOf(target.Entry.RelativePath) : string.Empty
                : target.Visual.TitleText ?? string.Empty;
        _linkDisplay.Text = _context.CurrentEntryDisplay ?? "（空目标：可粘贴本地路径或 http(s) 网址，或点「浏览」）";
    }

    private void ValidateForm()
    {
        var draft = BuildDraft();
        var validation = GroupDraftValidator.Validate(draft, _context.Target, _context.Wall, _context.OtherRects);
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
        AutomationProperties.SetName(_validation, message); // Name = 文本（UIA 断言）
        _saveButton.IsEnabled = false;
    }

    private void Save()
    {
        ValidateForm();
        if (!_saveButton.IsEnabled)
        {
            return;
        }

        var error = _context.SaveHandler(BuildDraft()); // 提交层错误就地显示，窗口不关、零残留
        if (error is null)
        {
            _forceClose = true;
            Close();
        }
        else
        {
            ShowValidation(error);
        }
    }

    private bool IsDirty() => !BuildDraft().Equals(_initialDraft);

    /// <summary>关闭请求（取消钮；B08「有改动关窗确认」）：有改动 → 放弃确认框；无改动直接关。</summary>
    private void RequestClose()
    {
        if (_session is not null)
        {
            CancelLayout(); // 布局子页上取消 = 回进画笔前
            return;
        }

        if (IsDirty())
        {
            _ = ConfirmDiscardAsync();
            return;
        }

        _forceClose = true;
        Close();
    }

    // ————————————————————————————— 关闭确认（§3.5；B08） —————————————————————————————

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose)
        {
            return;
        }

        if (_session is not null)
        {
            // 布局子页上直接关窗 = 取消编辑（子草稿即弃、回主页），不误伤主页草稿
            args.Cancel = true;
            CancelLayout();
            return;
        }

        if (!IsDirty())
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
            Content = "磁贴组属性有改动，关闭将放弃全部未保存的更改（含已确认的布局修改）。",
            PrimaryButtonText = "放弃更改",
            CloseButtonText = "继续编辑",
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "grp-discard-dialog");
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _forceClose = true;
            Close();
        }
    }

    private void ConfigureWindowShape(bool isCreateMode, IntPtr ownerHwnd)
    {
        _ = isCreateMode;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var workArea = DisplayArea.Primary.WorkArea;
        const int width = 720;
        const int height = 720;
        AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2),
            width,
            height));

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetOwnerWindow(hwnd, ownerHwnd); // owned window：恒在墙前、随墙最小化，不抢系统全局置顶（设计 §3.3）
    }

    // ————————————————————————————— 文件选择器 —————————————————————————————

    private async Task PickImagesAsync()
    {
        var picker = new FileOpenPicker();
        InitializePicker(picker);
        picker.ViewMode = PickerViewMode.Thumbnail;
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".gif");
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".webp");
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0)
        {
            return;
        }

        if (files.Count == 1)
        {
            _imageSingle.IsChecked = true;
            _imagePathsBox.Text = files[0].Path;
        }
        else
        {
            _imageMultiple.IsChecked = true;
            _imagePathsBox.Text = string.Join(Environment.NewLine, files.Select(f => f.Path));
        }

        ValidateForm();
    }

    private async Task PickFolderAsync()
    {
        var picker = new FolderPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        _imageFolder.IsChecked = true;
        _folderPathBox.Text = folder.Path;
        ValidateForm();
    }

    private async Task PickLinkFileAsync()
    {
        var picker = new FileOpenPicker();
        InitializePicker(picker);
        picker.ViewMode = PickerViewMode.List;
        picker.FileTypeFilter.Add(".lnk");
        picker.FileTypeFilter.Add(".url");
        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        // .lnk/.url → 完整副本；其他文件 → 新建 .lnk（§7.1 链接草稿推导）
        _pickedEntryDraft = EntryNames.KindOfRelativePath(file.Path) == EntryKind.None
            ? new EntryDraft.CreateForPath(file.Path)
            : new EntryDraft.CopyFromFile(file.Path);
        _linkSource = LinkSource.Picked;
        _linkEdit.Text = string.Empty;
        _linkDisplay.Text = file.Path;
        ValidateForm();
    }

    private async Task PickLinkFolderAsync()
    {
        var picker = new FolderPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        _pickedEntryDraft = new EntryDraft.CreateForPath(folder.Path);
        _linkSource = LinkSource.Picked;
        _linkEdit.Text = string.Empty;
        _linkDisplay.Text = folder.Path;
        ValidateForm();
    }

    /// <summary>unpackaged WinRT 选择器需绑定 HWND 才能弹出（IInitializeWithWindow，官方文档模式）。</summary>
    private void InitializePicker(object picker)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ((IInitializeWithWindow)picker).Initialize(hwnd);
    }

    [ComImport]
    [Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithWindow
    {
        void Initialize(IntPtr hwnd);
    }

    // ————————————————————————————— owned window（§3.3） —————————————————————————————

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
