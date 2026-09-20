using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;
using TileWall.Core.Grid;
using TileWall.Core.Shell;
using Windows.Storage.Pickers;

namespace TileWall.Dialogs;

/// <summary>属性窗运行上下文（MainWindow 注入；Core 服务不进窗，窗口只做「绑定显示 → 草稿 → 回调」）。</summary>
public sealed record PropertyWindowContext(
    WallGrid Wall,
    IReadOnlyList<GridRect> OtherRects,
    LayoutObject? Target,               // null = 创建模式（稳定标识在保存时生成）
    string? CurrentEntryFullPath,
    string? CurrentEntryDisplay,        // 当前目标/网址显示文本（8.3 短路径已归一化）
    ILnkFileService LinkFiles,
    Func<TileDraft, string?> SaveHandler); // 返回 null = 成功（关窗）；否则错误文本就地显示

/// <summary>
/// 属性编辑窗（M4 设计 §7；创建/编辑共用同一表单与 TileDraft，P1 §3.2 线框）。
/// 窗口形态：常规标题栏、不可调尺寸、GWLP_HWNDPARENT 设为墙的 owned window（恒在墙前、不抢系统全局置顶）。
/// 有改动关闭经 AppWindow.Closing 确认（§3.5）；保存经注入回调走 EntryCommitService 联合提交（§5.5）。
/// M4 简化记录：前景默认占位/手动图片（图标提取顺延）；后景图像只存路径不复制进数据目录。
/// </summary>
public sealed class TilePropertyWindow : Window, IExitParticipant
{
    private const int GwlHwndParent = -8;

    private readonly PropertyWindowContext _context;
    private readonly Grid _rootGrid;
    private readonly NumberBox _colsBox;
    private readonly NumberBox _rowsBox;
    private readonly ComboBox _backgroundMode;
    private readonly TextBox _backgroundColorBox;
    private readonly StackPanel _backgroundColorRow;
    private readonly StackPanel _backgroundImageRow;
    private readonly TextBox _backgroundImagePath;
    private readonly TextBox _foregroundPath;
    private readonly TextBox _titleBox;
    private readonly TextBox _linkDisplay;
    private readonly TextBox _linkEdit;
    private readonly InfoBar _validation;
    private readonly Button _saveButton;
    private readonly MenuFlyout _linkBrowseMenu;

    private TileDraft _initialDraft;
    private EntryDraft? _pickedEntryDraft;   // 浏览产生的草稿（CopyFromFile / CreateForPath）
    private LinkSource _linkSource = LinkSource.Untouched;
    private bool _forceClose;

    private enum LinkSource
    {
        Untouched,   // 未触碰 → KeepCurrent / 空目标
        Text,        // 编辑框输入 → 按内容推导
        Picked,      // 浏览选中 → _pickedEntryDraft
        Cleared,     // 清空链接 → NoneDraft
    }

    public TilePropertyWindow(PropertyWindowContext context, IntPtr ownerHwnd)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        var isCreateMode = context.Target is null;
        Title = isCreateMode ? "新建磁贴" : "编辑磁贴";

        _linkBrowseMenu = BuildLinkBrowseMenu();

        _colsBox = new NumberBox { Minimum = 1, Maximum = WallGrid.TileMaxCells, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        _rowsBox = new NumberBox { Minimum = 1, Maximum = context.Wall.Rows, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        AutomationProperties.SetAutomationId(_colsBox, "prop-size-cols");
        AutomationProperties.SetAutomationId(_rowsBox, "prop-size-rows");

        _backgroundMode = new ComboBox { MinWidth = 140 };
        _backgroundMode.Items.Add("跟随系统");
        _backgroundMode.Items.Add("自定义颜色");
        _backgroundMode.Items.Add("图像");
        AutomationProperties.SetAutomationId(_backgroundMode, "prop-background");

        _backgroundColorBox = new TextBox { PlaceholderText = "#RRGGBB" };
        AutomationProperties.SetAutomationId(_backgroundColorBox, "prop-background-color");
        _backgroundColorRow = FormRow("颜色", _backgroundColorBox);

        _backgroundImagePath = new TextBox { IsReadOnly = true };
        AutomationProperties.SetAutomationId(_backgroundImagePath, "prop-background-image");
        var backgroundBrowse = new Button { Content = "浏览" };
        AutomationProperties.SetAutomationId(backgroundBrowse, "prop-background-browse");
        backgroundBrowse.Click += async (_, _) => await PickBackgroundImageAsync();
        _backgroundImageRow = FormRow("路径", _backgroundImagePath, backgroundBrowse);

        _foregroundPath = new TextBox { IsReadOnly = true, PlaceholderText = "（默认占位字形；可手动指定图片）" };
        AutomationProperties.SetAutomationId(_foregroundPath, "prop-foreground");
        var foregroundBrowse = new Button { Content = "更换" };
        AutomationProperties.SetAutomationId(foregroundBrowse, "prop-foreground-browse");
        foregroundBrowse.Click += async (_, _) => await PickForegroundImageAsync();
        var foregroundClear = new Button { Content = "清空" };
        AutomationProperties.SetAutomationId(foregroundClear, "prop-foreground-clear");
        foregroundClear.Click += (_, _) => _foregroundPath.Text = string.Empty;

        _titleBox = new TextBox { PlaceholderText = "留空 = 隐藏标题" };
        AutomationProperties.SetAutomationId(_titleBox, "prop-title");

        _linkDisplay = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.NoWrap };
        AutomationProperties.SetAutomationId(_linkDisplay, "prop-link");

        _linkEdit = new TextBox { PlaceholderText = "可直接粘贴本地路径或 http(s) 网址" };
        AutomationProperties.SetAutomationId(_linkEdit, "prop-link-edit");

        var linkBrowse = new Button { Content = "浏览", Flyout = _linkBrowseMenu };
        AutomationProperties.SetAutomationId(linkBrowse, "prop-link-browse");
        var linkClear = new Button { Content = "清空链接" };
        AutomationProperties.SetAutomationId(linkClear, "prop-link-clear");
        linkClear.Click += (_, _) =>
        {
            _linkSource = LinkSource.Cleared;
            _pickedEntryDraft = null;
            _linkEdit.Text = string.Empty;
            _linkDisplay.Text = "（清空后保存 → 撤销绑定）";
            ValidateForm();
        };

        _validation = new InfoBar { IsOpen = false, Severity = InfoBarSeverity.Warning, IsClosable = false };
        AutomationProperties.SetAutomationId(_validation, "prop-validation");

        _saveButton = new Button { Content = "保存" };
        AutomationProperties.SetAutomationId(_saveButton, "prop-save");
        _saveButton.Click += (_, _) => Save();
        var cancelButton = new Button { Content = "取消" };
        AutomationProperties.SetAutomationId(cancelButton, "prop-cancel");
        cancelButton.Click += (_, _) => Close();

        _rootGrid = BuildLayout(isCreateMode, _colsBox, _rowsBox, _backgroundMode, _backgroundColorRow, _backgroundImageRow, _foregroundPath, foregroundBrowse, foregroundClear, _titleBox, _linkDisplay, linkBrowse, linkClear, _linkEdit, _validation, _saveButton, cancelButton);
        Content = _rootGrid;

        _colsBox.ValueChanged += (_, _) => ValidateForm();
        _rowsBox.ValueChanged += (_, _) => ValidateForm();
        _backgroundMode.SelectionChanged += (_, _) =>
        {
            var mode = _backgroundMode.SelectedIndex;
            _backgroundColorRow.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
            _backgroundImageRow.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
            ValidateForm();
        };
        _backgroundColorBox.TextChanged += (_, _) => ValidateForm();
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

        LoadInitialState();
        _initialDraft = BuildDraft();

        ConfigureWindowShape(isCreateMode, ownerHwnd);
        AppWindow.Closing += OnAppWindowClosing;
    }

    // ————————————————————————————— 装配 —————————————————————————————

    private static Grid BuildLayout(
        bool isCreateMode,
        NumberBox colsBox,
        NumberBox rowsBox,
        ComboBox backgroundMode,
        StackPanel backgroundColorRow,
        StackPanel backgroundImageRow,
        TextBox foregroundPath,
        Button foregroundBrowse,
        Button foregroundClear,
        TextBox titleBox,
        TextBox linkDisplay,
        Button linkBrowse,
        Button linkClear,
        TextBox linkEdit,
        InfoBar validation,
        Button saveButton,
        Button cancelButton)
    {
        var grid = new Grid { Padding = new Thickness(16), RowSpacing = 10 };
        for (var i = 0; i < 10; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        // §7.3 契约：窗根 AutomationId 挂在标题上（TextBlock 带 peer，恒可被 UIA 断言）
        var header = new TextBlock { Text = isCreateMode ? "新建磁贴" : "编辑磁贴", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(header, "tile-property-window");
        AutomationProperties.SetName(header, isCreateMode ? "新建磁贴" : "编辑磁贴");
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sizeRow.Children.Add(new TextBlock { Text = "大小", VerticalAlignment = VerticalAlignment.Center, Width = 48 });
        sizeRow.Children.Add(new TextBlock { Text = "列", VerticalAlignment = VerticalAlignment.Center });
        colsBox.Width = 88;
        sizeRow.Children.Add(colsBox);
        sizeRow.Children.Add(new TextBlock { Text = "×  行", VerticalAlignment = VerticalAlignment.Center });
        rowsBox.Width = 88;
        sizeRow.Children.Add(rowsBox);
        Grid.SetRow(sizeRow, 1);
        grid.Children.Add(sizeRow);

        var backgroundRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        backgroundRow.Children.Add(new TextBlock { Text = "后景", VerticalAlignment = VerticalAlignment.Center, Width = 48 });
        backgroundRow.Children.Add(backgroundMode);
        Grid.SetRow(backgroundRow, 2);
        grid.Children.Add(backgroundRow);

        backgroundColorRow.Margin = new Thickness(56, 0, 0, 0);
        Grid.SetRow(backgroundColorRow, 3);
        grid.Children.Add(backgroundColorRow);

        backgroundImageRow.Margin = new Thickness(56, 0, 0, 0);
        Grid.SetRow(backgroundImageRow, 4);
        grid.Children.Add(backgroundImageRow);

        var foregroundRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foregroundRow.Children.Add(new TextBlock { Text = "前景", VerticalAlignment = VerticalAlignment.Center, Width = 48 });
        foregroundPath.Width = 300;
        foregroundRow.Children.Add(foregroundPath);
        foregroundRow.Children.Add(foregroundBrowse);
        foregroundRow.Children.Add(foregroundClear);
        Grid.SetRow(foregroundRow, 5);
        grid.Children.Add(foregroundRow);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock { Text = "文字", VerticalAlignment = VerticalAlignment.Center, Width = 48 });
        titleBox.Width = 430;
        titleRow.Children.Add(titleBox);
        Grid.SetRow(titleRow, 6);
        grid.Children.Add(titleRow);

        var linkRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        linkRow.Children.Add(new TextBlock { Text = "链接", VerticalAlignment = VerticalAlignment.Center, Width = 48 });
        linkDisplay.Width = 280;
        linkRow.Children.Add(linkDisplay);
        linkRow.Children.Add(linkBrowse);
        linkRow.Children.Add(linkClear);
        Grid.SetRow(linkRow, 7);
        grid.Children.Add(linkRow);

        var linkEditRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        linkEditRow.Children.Add(new TextBlock { Text = "编辑", VerticalAlignment = VerticalAlignment.Center, Width = 48 });
        linkEdit.Width = 430;
        linkEditRow.Children.Add(linkEdit);
        Grid.SetRow(linkEditRow, 8);
        grid.Children.Add(linkEditRow);

        var bottomRow = new StackPanel { Orientation = Orientation.Vertical, Spacing = 10 };
        bottomRow.Children.Add(validation);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(saveButton);
        buttons.Children.Add(cancelButton);
        bottomRow.Children.Add(buttons);
        Grid.SetRow(bottomRow, 9);
        grid.Children.Add(bottomRow);
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
        AutomationProperties.SetAutomationId(menu, "prop-link-browse-menu");
        var pickFile = new MenuFlyoutItem { Text = "选择 .lnk/.url 或其他文件…" };
        pickFile.Click += async (_, _) => await PickLinkFileAsync();
        var pickFolder = new MenuFlyoutItem { Text = "选择文件夹…" };
        pickFolder.Click += async (_, _) => await PickLinkFolderAsync();
        menu.Items.Add(pickFile);
        menu.Items.Add(pickFolder);
        return menu;
    }

    private void LoadInitialState()
    {
        var target = _context.Target;
        var visual = target?.Visual ?? new ObjectVisual();
        _colsBox.Value = target?.Bounds.Width ?? 1;
        _rowsBox.Value = target?.Bounds.Height ?? 1;
        _backgroundMode.SelectedIndex = visual.BackgroundImagePath is not null ? 2 : visual.BackgroundColor is not null ? 1 : 0;
        _backgroundColorBox.Text = visual.BackgroundColor ?? string.Empty;
        _backgroundImagePath.Text = visual.BackgroundImagePath ?? string.Empty;
        _foregroundPath.Text = visual.ForegroundIconPath ?? string.Empty;
        _titleBox.Text = target is null
            ? string.Empty
            : target.Entry is not null
                ? visual.ShowTitle
                    ? EntryNames.BaseNameOf(target.Entry.RelativePath) // 文字初值 = 托管文件名主体（§7.1）
                    : string.Empty // 隐藏标题对象保持为空，不把内部名称强行恢复为可见标题（M4 §7.1、设计 §6.3）
                : visual.TitleText ?? string.Empty;
        _linkDisplay.Text = _context.CurrentEntryDisplay ?? "（空目标：可粘贴本地路径或 http(s) 网址，或点「浏览」）";
    }

    private void ConfigureWindowShape(bool isCreateMode, IntPtr ownerHwnd)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var workArea = DisplayArea.Primary.WorkArea;
        var width = 640;
        var height = 620;
        AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2),
            width,
            height));

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetOwnerWindow(hwnd, ownerHwnd); // owned window：恒在墙前、随墙最小化，不抢系统全局置顶（设计 §3.3）
    }

    // ————————————————————————————— 草稿推导（§7.1） —————————————————————————————

    private TileDraft BuildDraft()
    {
        var entry = _linkSource switch
        {
            LinkSource.Untouched => _context.Target is null ? null : new EntryDraft.KeepCurrent(),
            LinkSource.Text => DeriveFromEditText(_linkEdit.Text),
            LinkSource.Picked => _pickedEntryDraft,
            LinkSource.Cleared => new EntryDraft.NoneDraft(),
            _ => null,
        };
        return new TileDraft
        {
            Size = new GridSize((int)_colsBox.Value, (int)_rowsBox.Value),
            BackgroundColorHex = _backgroundMode.SelectedIndex == 1 && _backgroundColorBox.Text.Length > 0 ? _backgroundColorBox.Text : null,
            BackgroundImagePath = _backgroundMode.SelectedIndex == 2 && _backgroundImagePath.Text.Length > 0 ? _backgroundImagePath.Text : null,
            ForegroundIconPath = _foregroundPath.Text.Length > 0 ? _foregroundPath.Text : null,
            TitleText = _titleBox.Text,
            Entry = entry,
        };
    }

    /// <summary>编辑框推导（§7.1 链接草稿推导）：http(s) → URL 类；现入口为普通 .lnk → 只改目标；否则新建 .lnk。</summary>
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

        // 手输 .lnk/.url 路径与浏览一致（§6.1 表）：完整副本，禁止创建指向 .lnk/.url 的二层入口（C29 关注点）
        if (EntryNames.KindOfRelativePath(text) != EntryKind.None)
        {
            return new EntryDraft.CopyFromFile(text);
        }

        return currentEntryKind == EntryKind.Lnk ? new EntryDraft.EditLnkTarget(text) : new EntryDraft.CreateForPath(text);
    }

    private bool IsDirty()
    {
        var draft = BuildDraft();
        return !DraftEquals(draft, _initialDraft);
    }

    private static bool DraftEquals(TileDraft a, TileDraft b) =>
        a.Size.Equals(b.Size)
        && string.Equals(a.BackgroundColorHex, b.BackgroundColorHex, StringComparison.Ordinal)
        && string.Equals(a.BackgroundImagePath, b.BackgroundImagePath, StringComparison.Ordinal)
        && string.Equals(a.ForegroundIconPath, b.ForegroundIconPath, StringComparison.Ordinal)
        && string.Equals(a.TitleText, b.TitleText, StringComparison.Ordinal)
        && Equals(a.Entry, b.Entry); // EntryDraft 判别联合为 record → 值相等

    // ————————————————————————————— 校验与保存 —————————————————————————————

    private void ValidateForm()
    {
        var draft = BuildDraft();
        var validation = DraftValidator.Validate(
            draft, _context.Target, _context.CurrentEntryFullPath, _context.Wall, _context.OtherRects, _context.LinkFiles);
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

    private void Save() => _ = ((IExitParticipant)this).TrySaveNow();

    // ————————————————————————————— IExitParticipant（M7 §9 退出三分支参与面） —————————————————————————————

    /// <summary>退出流程草稿判定：与「取消关窗确认」同一脏检查。</summary>
    bool IExitParticipant.HasUnsavedDraft => IsDirty();

    /// <summary>
    /// 同步保存（退出流程「保存后退出」分支）：成功则本窗自行关闭（_forceClose 免确认）并返回 null；
    /// 失败就地显示错误、窗口保持、磁盘零残留，返回错误文本——ExitCoordinator 据此中止退出。
    /// </summary>
    string? IExitParticipant.TrySaveNow()
    {
        ValidateForm();
        if (!_saveButton.IsEnabled)
        {
            return "表单尚未通过校验，无法保存";
        }

        var error = _context.SaveHandler(BuildDraft()); // 防御性复核（遮罩期墙面不会变，仍按 §7.1 保存时复核）
        if (error is null)
        {
            _forceClose = true;
            Close();
        }
        else
        {
            ShowValidation(error); // 提交层错误（IO/校验）就地显示，窗口不关、零残留
        }

        return error;
    }

    /// <summary>退出流程「放弃退出」分支：丢弃草稿（此后关闭不再弹确认）。</summary>
    void IExitParticipant.DiscardDraft() => _forceClose = true;

    // ————————————————————————————— 关闭确认（§3.5） —————————————————————————————

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
            Content = "磁贴属性有改动，关闭将放弃未保存的更改。",
            PrimaryButtonText = "放弃更改",
            CloseButtonText = "继续编辑",
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "prop-discard-dialog");
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _forceClose = true;
            Close();
        }
    }

    // ————————————————————————————— 文件选择器 —————————————————————————————

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

    private async Task PickBackgroundImageAsync()
    {
        var file = await PickImageAsync();
        if (file is not null)
        {
            _backgroundMode.SelectedIndex = 2;
            _backgroundImagePath.Text = file;
            ValidateForm();
        }
    }

    private async Task PickForegroundImageAsync()
    {
        var file = await PickImageAsync();
        if (file is not null)
        {
            _foregroundPath.Text = file;
            ValidateForm();
        }
    }

    private async Task<string?> PickImageAsync()
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
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
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

        // GWL_HWNDPARENT = GWL(-8)；HWND 句柄值为 32 位安全句柄，x86/x64 分别走 SetWindowLong(W)/(Ptr)W
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
