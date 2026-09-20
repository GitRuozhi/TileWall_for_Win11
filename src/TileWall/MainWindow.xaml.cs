using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TileWall.Core.Grid;
using Windows.Graphics;

namespace TileWall;

/// <summary>
/// 磁贴墙主窗口：无边框、无标题栏、不可移动（拍板 Q7）；
/// 左下锚定主显示器工作区、不覆盖任务栏（设计 §4.1，WorkArea 已排除任务栏）；
/// 窗口尺寸 = <see cref="WallSizing.InitialForWorkArea"/> 作用于真实工作区的结果（拍板 Q3）。
/// 本阶段只消费 Core（GridMetrics / WallSizing），不新增核心逻辑。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly GridMetrics _metrics = GridMetrics.Default;
    private int _wallColumns;
    private int _wallRows;

    public MainWindow()
    {
        InitializeComponent();

        Title = "TileWall";
        ConfigureBorderlessPresenter();
        ((FrameworkElement)Content).Loaded += OnContentLoaded;
    }

    private void OnContentLoaded(object sender, RoutedEventArgs e)
    {
        PlaceWallWindow();
        DrawWallGridBase();
        PositionEmptyHint();
    }

    /// <summary>无边框无标题栏、不可移动、不可最大化/最小化（拍板 Q7；显隐与呼出属 M3+ 托盘/快捷键）。</summary>
    private void ConfigureBorderlessPresenter()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
    }

    /// <summary>
    /// 主显示器工作区（DisplayArea.WorkArea，物理像素、已排除任务栏）→ 按 XamlRoot 缩放折算 DIP →
    /// WallSizing.InitialForWorkArea 吸附 (栏数, 行数) → 换回物理像素，MoveAndResize 到工作区左下角。
    /// </summary>
    private void PlaceWallWindow()
    {
        var scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0 || !double.IsFinite(scale))
        {
            scale = 1.0;
        }

        var workArea = DisplayArea.Primary.WorkArea;
        var workWidthDip = workArea.Width / scale;
        var workHeightDip = workArea.Height / scale;
        (_wallColumns, _wallRows) = WallSizing.InitialForWorkArea(workWidthDip, workHeightDip, _metrics);
        if (_wallColumns < 1 || _wallRows < 1)
        {
            // 工作区容不下一栏八格 → 显示适配提示属后续阶段（设计 §4.5、A18）；本阶段保持默认窗口并仅显示空墙提示
            return;
        }

        var wallWidthPx = (int)Math.Round(_metrics.WallWidth(_wallColumns) * scale);
        var wallHeightPx = (int)Math.Round(_metrics.WallHeight(_wallRows) * scale);
        var left = workArea.X;
        var top = workArea.Y + workArea.Height - wallHeightPx; // 左下锚定
        AppWindow.MoveAndResize(new RectInt32(left, top, wallWidthPx, wallHeightPx));
    }

    /// <summary>竖栏网格底占位：每栏 = 底色块 + 描边；栏内画基础格网格线（行/列分隔，1 DIP）。</summary>
    private void DrawWallGridBase()
    {
        if (_wallColumns < 1 || _wallRows < 1)
        {
            return;
        }

        WallCanvas.Children.Clear();
        WallCanvas.Width = _metrics.WallWidth(_wallColumns);
        WallCanvas.Height = _metrics.WallHeight(_wallRows);

        var columnHeight = _metrics.WallHeight(_wallRows) - (2 * _metrics.Margin);
        var columnFill = (Brush)RootGrid.Resources["ColumnFillBrush"];
        var columnStroke = (Brush)RootGrid.Resources["ColumnStrokeBrush"];
        var gridLine = (Brush)RootGrid.Resources["GridLineBrush"];

        for (var n = 0; n < _wallColumns; n++)
        {
            var x = _metrics.CellX(n * GridMetrics.CellsPerColumn);

            var column = new Rectangle
            {
                Width = _metrics.ColumnWidth,
                Height = columnHeight,
                Fill = columnFill,
                Stroke = columnStroke,
                StrokeThickness = 1,
            };
            Canvas.SetLeft(column, x);
            Canvas.SetTop(column, _metrics.Margin);
            WallCanvas.Children.Add(column);

            for (var row = 1; row < _wallRows; row++)
            {
                var horizontal = new Rectangle
                {
                    Width = _metrics.ColumnWidth,
                    Height = 1,
                    Fill = gridLine,
                };
                Canvas.SetLeft(horizontal, x);
                Canvas.SetTop(horizontal, _metrics.CellY(row) - (_metrics.Gap / 2));
                WallCanvas.Children.Add(horizontal);
            }

            for (var cell = 1; cell < GridMetrics.CellsPerColumn; cell++)
            {
                var vertical = new Rectangle
                {
                    Width = 1,
                    Height = columnHeight,
                    Fill = gridLine,
                };
                Canvas.SetLeft(vertical, x + (cell * _metrics.Pitch) - (_metrics.Gap / 2));
                Canvas.SetTop(vertical, _metrics.Margin);
                WallCanvas.Children.Add(vertical);
            }
        }
    }

    /// <summary>空墙提示居中偏上（P1 §3.6）：顶部留约 28% 墙高。</summary>
    private void PositionEmptyHint()
    {
        if (_wallRows >= 1)
        {
            var wallHeight = _metrics.WallHeight(_wallRows);
            EmptyHint.Margin = new Thickness(0, wallHeight * 0.28, 0, 0);
        }
    }
}
