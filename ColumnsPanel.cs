using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Glance;

/// <summary>
/// Lays the widget's sections out in one or more equal-width columns, filling each column top to bottom
/// and keeping the columns about the same height. Draws a thin rule between sections that share a column.
/// </summary>
public sealed class ColumnsPanel : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(ColumnsPanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnHeightProperty = DependencyProperty.Register(
        nameof(MaxColumnHeight), typeof(double), typeof(ColumnsPanel),
        new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private static readonly Brush RuleBrush = Palette.Frozen("#1FFFFFFF");
    private readonly List<Rect> _rules = [];

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>
    /// The tallest a column may be, normally the height of the screen. If the chosen number of columns
    /// would be taller than this, more columns are used so the widget never runs off the screen.
    /// </summary>
    public double MaxColumnHeight
    {
        get => (double)GetValue(MaxColumnHeightProperty);
        set => SetValue(MaxColumnHeightProperty, value);
    }

    public double ColumnWidth { get; set; } = 264;
    public double ColumnGap { get; set; } = 28;

    /// <summary>Space between two sections in the same column. The rule is drawn at the top of it.</summary>
    public double SectionGap { get; set; } = 14;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(ColumnWidth, double.PositiveInfinity));

        var columns = Distribute();
        var height = columns.Count == 0 ? 0 : columns.Max(HeightOf);
        var count = Math.Max(1, columns.Count);
        return new Size(count * ColumnWidth + (count - 1) * ColumnGap, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _rules.Clear();
        double x = 0;
        foreach (var column in Distribute())
        {
            double y = 0;
            for (var i = 0; i < column.Count; i++)
            {
                if (i > 0)
                {
                    _rules.Add(new Rect(x, y, ColumnWidth, 1));
                    y += SectionGap;
                }
                var height = column[i].DesiredSize.Height;
                column[i].Arrange(new Rect(x, y, ColumnWidth, height));
                y += height;
            }
            x += ColumnWidth + ColumnGap;
        }

        InvalidateVisual();
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var rule in _rules) dc.DrawRectangle(RuleBrush, null, rule);
    }

    private double HeightOf(List<UIElement> column) =>
        column.Sum(c => c.DesiredSize.Height) + SectionGap * Math.Max(0, column.Count - 1);

    private List<List<UIElement>> Distribute()
    {
        var children = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        if (children.Count == 0) return [];

        // Start with the number of columns asked for, and add more only if that does not fit the screen.
        for (var count = Math.Clamp(Columns, 1, children.Count); ; count++)
        {
            var columns = Distribute(children, count);
            if (count >= children.Count || columns.Max(HeightOf) <= MaxColumnHeight) return columns;
        }
    }

    // Sections keep their order. A new column starts once the current one has reached its share of the total height.
    private static List<List<UIElement>> Distribute(List<UIElement> children, int count)
    {
        var columns = new List<List<UIElement>>();
        var target = children.Sum(c => c.DesiredSize.Height) / count;

        var current = new List<UIElement>();
        columns.Add(current);
        double height = 0;
        foreach (var child in children)
        {
            var childHeight = child.DesiredSize.Height;
            if (current.Count > 0 && columns.Count < count && height + childHeight / 2 > target)
            {
                current = [];
                columns.Add(current);
                height = 0;
            }
            current.Add(child);
            height += childHeight;
        }
        return columns;
    }
}
