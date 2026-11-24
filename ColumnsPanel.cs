using System.Windows;
using System.Windows.Controls;

namespace Glance;

/// <summary>
/// Lays the widget's section panels out in equal-width columns.
///
/// Left to itself it fills the columns top to bottom in order, keeping them about the same height.
/// Once the user has dragged a panel, every panel carries the column it was put in (the Column attached
/// property) and the panel simply honours that.
/// </summary>
public sealed class ColumnsPanel : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(ColumnsPanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnHeightProperty = DependencyProperty.Register(
        nameof(MaxColumnHeight), typeof(double), typeof(ColumnsPanel),
        new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The column a child has been placed in by the user, or -1 for "wherever it fits".</summary>
    public static readonly DependencyProperty ColumnProperty = DependencyProperty.RegisterAttached(
        "Column", typeof(int), typeof(ColumnsPanel),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    /// <summary>Where a dragged panel would land, and where to draw the marker that shows it.</summary>
    /// <param name="NewColumn">True when it would start a column of its own at position <paramref name="Column"/>.</param>
    public readonly record struct DropTarget(int Column, int Index, bool NewColumn, Rect Marker);

    private List<List<UIElement>> _arranged = [];
    private List<List<Rect>> _bounds = [];

    public static int GetColumn(UIElement element) => (int)element.GetValue(ColumnProperty);
    public static void SetColumn(UIElement element, int value) => element.SetValue(ColumnProperty, value);

    /// <summary>How many columns to use when arranging automatically.</summary>
    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>
    /// The tallest a column may be when arranging automatically, normally the height of the screen. If the
    /// chosen number of columns would be taller than this, more are used so the widget never runs off the screen.
    /// </summary>
    public double MaxColumnHeight
    {
        get => (double)GetValue(MaxColumnHeightProperty);
        set => SetValue(MaxColumnHeightProperty, value);
    }

    public double ColumnWidth { get; set; } = 288;

    /// <summary>Space between panels, across and down.</summary>
    public double Gap { get; set; } = 10;

    /// <summary>The children as last laid out: one list per column, top to bottom.</summary>
    public IReadOnlyList<IReadOnlyList<UIElement>> Arranged => _arranged;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(ColumnWidth, double.PositiveInfinity));

        var columns = Distribute();
        var height = columns.Count == 0 ? 0 : columns.Max(HeightOf);
        var count = Math.Max(1, columns.Count);
        return new Size(count * ColumnWidth + (count - 1) * Gap, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _arranged = Distribute();
        _bounds = [];
        double x = 0;
        foreach (var column in _arranged)
        {
            var bounds = new List<Rect>();
            double y = 0;
            foreach (var child in column)
            {
                var rect = new Rect(x, y, ColumnWidth, child.DesiredSize.Height);
                child.Arrange(rect);
                bounds.Add(rect);
                y += rect.Height + Gap;
            }
            _bounds.Add(bounds);
            x += ColumnWidth + Gap;
        }
        return finalSize;
    }

    /// <summary>Works out where a panel dropped at this point (relative to this panel) would go.</summary>
    public DropTarget? DropTargetAt(Point point)
    {
        if (_arranged.Count == 0) return null;

        var step = ColumnWidth + Gap;
        var right = _arranged.Count * step - Gap;
        var height = Math.Max(ActualHeight, 40);

        // Dragged clear of either side: a new column there.
        if (point.X > right + Gap) return new DropTarget(_arranged.Count, 0, true, new Rect(right + 3, 0, 3, height));
        if (point.X < -Gap) return new DropTarget(0, 0, true, new Rect(-6, 0, 3, height));

        var column = Math.Clamp((int)((point.X + Gap / 2) / step), 0, _arranged.Count - 1);
        var bounds = _bounds[column];
        var index = bounds.Count(b => b.Top + b.Height / 2 < point.Y);

        // The marker sits in the gap above the panel it would go in front of, or below the last one.
        var y = index == 0 ? -Gap / 2 - 1.5 : bounds[index - 1].Bottom + Gap / 2 - 1.5;
        return new DropTarget(column, index, false, new Rect(column * step, y, ColumnWidth, 3));
    }

    private double HeightOf(List<UIElement> column) =>
        column.Sum(c => c.DesiredSize.Height) + Gap * Math.Max(0, column.Count - 1);

    private List<List<UIElement>> Distribute()
    {
        var children = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        if (children.Count == 0) return [];

        // Placed by the user: keep each panel in its column, in the order the panels come in.
        if (children.Any(c => GetColumn(c) >= 0))
        {
            return children.GroupBy(c => Math.Max(0, GetColumn(c)))
                .OrderBy(g => g.Key)
                .Select(g => g.ToList())
                .ToList();
        }

        // Start with the number of columns asked for, and add more only if that does not fit the screen.
        for (var count = Math.Clamp(Columns, 1, children.Count); ; count++)
        {
            var columns = Balance(children, count);
            if (count >= children.Count || columns.Max(HeightOf) <= MaxColumnHeight) return columns;
        }
    }

    // Panels keep their order. A new column starts once the current one has reached its share of the total height.
    private static List<List<UIElement>> Balance(List<UIElement> children, int count)
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
