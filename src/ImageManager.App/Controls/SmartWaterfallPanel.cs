using Avalonia;
using Avalonia.Controls;

namespace ImageManager.App.Controls;

public class SmartWaterfallPanel : Panel
{
    public static readonly StyledProperty<string> ModeProperty =
        AvaloniaProperty.Register<SmartWaterfallPanel, string>(nameof(Mode), "None");

    public static readonly StyledProperty<double> ColumnWidthProperty =
        AvaloniaProperty.Register<SmartWaterfallPanel, double>(nameof(ColumnWidth), 160.0);

    // Inherited attached property — children bind their Width to this
    public static readonly AttachedProperty<double> ItemWidthProperty =
        AvaloniaProperty.RegisterAttached<SmartWaterfallPanel, Control, double>("ItemWidth", double.NaN, true);

    public static void SetItemWidth(Control element, double value) => element.SetValue(ItemWidthProperty, value);
    public static double GetItemWidth(Control element) => element.GetValue(ItemWidthProperty);

    static SmartWaterfallPanel()
    {
        ModeProperty.Changed.AddClassHandler<SmartWaterfallPanel>((x, e) => x.InvalidateMeasure());
        ColumnWidthProperty.Changed.AddClassHandler<SmartWaterfallPanel>((x, e) => x.InvalidateMeasure());
    }

    public string Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public double ColumnWidth
    {
        get => GetValue(ColumnWidthProperty);
        set => SetValue(ColumnWidthProperty, value);
    }

    private readonly Dictionary<Control, double> _childWidths = new();

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0) return new Size(0, 0);

        return Mode switch
        {
            "Vertical" => MeasureVertical(availableSize),
            "Horizontal" => MeasureHorizontal(availableSize),
            _ => MeasureDefault(availableSize)
        };
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0) return finalSize;

        return Mode switch
        {
            "Vertical" => ArrangeVertical(finalSize),
            "Horizontal" => ArrangeHorizontal(finalSize),
            _ => ArrangeDefault(finalSize)
        };
    }

    #region Vertical (Masonry)

    private Size MeasureVertical(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 1000 : availableSize.Width;
        int colCount = Math.Max(1, (int)(width / ColumnWidth));
        double actualColWidth = width / colCount;

        SetValue(ItemWidthProperty, actualColWidth);

        double[] colHeights = new double[colCount];

        foreach (Control child in Children)
        {
            child.Measure(new Size(actualColWidth, double.PositiveInfinity));

            int minCol = 0;
            for (int i = 1; i < colCount; i++)
                if (colHeights[i] < colHeights[minCol]) minCol = i;

            colHeights[minCol] += child.DesiredSize.Height;
        }

        return new Size(width, colHeights.Max());
    }

    private Size ArrangeVertical(Size finalSize)
    {
        int colCount = Math.Max(1, (int)(finalSize.Width / ColumnWidth));
        double actualColWidth = finalSize.Width / colCount;
        double[] colHeights = new double[colCount];

        foreach (Control child in Children)
        {
            int minCol = 0;
            for (int i = 1; i < colCount; i++)
                if (colHeights[i] < colHeights[minCol]) minCol = i;

            double x = minCol * actualColWidth;
            double y = colHeights[minCol];

            child.Arrange(new Rect(x, y, actualColWidth, child.DesiredSize.Height));
            colHeights[minCol] += child.DesiredSize.Height;
        }

        return finalSize;
    }

    #endregion

    #region Horizontal (Justified)

    private double TargetRowHeight => Math.Min(ColumnWidth * 180.0 / 160.0, 350);

    private Size MeasureHorizontal(Size availableSize)
    {
        double rowHeight = TargetRowHeight;
        double containerWidth = double.IsInfinity(availableSize.Width) ? 1000 : availableSize.Width;
        double totalHeight = 0;
        double currentRowWidth = 0;
        var currentRow = new List<Control>();

        _childWidths.Clear();

        // Horizontal mode: Image should auto-size, no fixed width
        SetValue(ItemWidthProperty, double.NaN);

        foreach (Control child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, rowHeight));
            double childWidth = child.DesiredSize.Width * (rowHeight / Math.Max(1, child.DesiredSize.Height));
            _childWidths[child] = childWidth;

            if (currentRowWidth + childWidth > containerWidth && currentRow.Count > 0)
            {
                double ratio = containerWidth / currentRowWidth;
                totalHeight += rowHeight * ratio;
                currentRow.Clear();
                currentRowWidth = 0;
            }

            currentRow.Add(child);
            currentRowWidth += childWidth;
        }

        if (currentRow.Count > 0)
            totalHeight += rowHeight;

        return new Size(containerWidth, totalHeight);
    }

    private Size ArrangeHorizontal(Size finalSize)
    {
        double rowHeight = TargetRowHeight;
        double y = 0;
        double currentRowWidth = 0;
        var currentRow = new List<Control>();

        foreach (Control child in Children)
        {
            if (!_childWidths.TryGetValue(child, out double childWidth))
                childWidth = child.DesiredSize.Width;

            if (currentRowWidth + childWidth > finalSize.Width && currentRow.Count > 0)
            {
                double ratio = finalSize.Width / currentRowWidth;
                double actualHeight = rowHeight * ratio;
                double x = 0;

                foreach (var rowChild in currentRow)
                {
                    double w = _childWidths[rowChild] * ratio;
                    rowChild.Arrange(new Rect(x, y, w, actualHeight));
                    x += w;
                }

                y += actualHeight;
                currentRow.Clear();
                currentRowWidth = 0;
            }

            currentRow.Add(child);
            currentRowWidth += childWidth;
        }

        double lastX = 0;
        foreach (var lastChild in currentRow)
        {
            if (_childWidths.TryGetValue(lastChild, out double w))
            {
                lastChild.Arrange(new Rect(lastX, y, w, rowHeight));
                lastX += w;
            }
        }

        return finalSize;
    }

    #endregion

    #region Default Grid

    private Size MeasureDefault(Size availableSize)
    {
        double itemWidth = ColumnWidth;
        SetValue(ItemWidthProperty, itemWidth);

        double curX = 0, curY = 0, maxRowH = 0;
        foreach (Control child in Children)
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            if (curX + child.DesiredSize.Width > availableSize.Width && curX > 0)
            {
                curX = 0; curY += maxRowH; maxRowH = 0;
            }
            curX += child.DesiredSize.Width;
            maxRowH = Math.Max(maxRowH, child.DesiredSize.Height);
        }
        return new Size(availableSize.Width, curY + maxRowH);
    }

    private Size ArrangeDefault(Size finalSize)
    {
        double curX = 0, curY = 0, maxRowH = 0;
        foreach (Control child in Children)
        {
            if (curX + child.DesiredSize.Width > finalSize.Width && curX > 0)
            {
                curX = 0; curY += maxRowH; maxRowH = 0;
            }
            child.Arrange(new Rect(curX, curY, child.DesiredSize.Width, child.DesiredSize.Height));
            curX += child.DesiredSize.Width;
            maxRowH = Math.Max(maxRowH, child.DesiredSize.Height);
        }
        return finalSize;
    }

    #endregion
}
