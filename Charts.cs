using System.Windows;
using System.Windows.Media;

namespace Glance;

/// <summary>A small line graph of recent readings, oldest on the left. No axes or labels: the value beside it says where it is now.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BaselineProperty = DependencyProperty.Register(
        nameof(Baseline), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Top of the scale, with zero at the bottom. Not a number fits the scale to the readings.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>The thin line along the bottom that the graph stands on.</summary>
    public Brush? Baseline
    {
        get => (Brush?)GetValue(BaselineProperty);
        set => SetValue(BaselineProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 2) return;

        if (Baseline is { } baseline) dc.DrawRectangle(baseline, null, new Rect(0, height - 1, width, 1));

        var values = Values;
        if (values is null || values.Count < 2) return;

        double low, high;
        if (double.IsNaN(Maximum))
        {
            // Fit to the readings, with a little air so a steady value is a line in the middle, not on an edge.
            low = values.Min();
            high = values.Max();
            var pad = Math.Max((high - low) * 0.15, Math.Abs(high) * 0.02 + 0.5);
            low = Math.Max(low - pad, low >= 0 ? 0 : double.NegativeInfinity);
            high += pad;
        }
        else
        {
            low = 0;
            high = Math.Max(Maximum, 1e-9);
        }
        var span = Math.Max(high - low, 1e-9);

        Point At(int i) => new(
            width * i / (values.Count - 1),
            1 + (height - 2) * (1 - Math.Clamp((values[i] - low) / span, 0, 1)));

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            l.BeginFigure(At(0), isFilled: false, isClosed: false);
            a.BeginFigure(new Point(0, height), isFilled: true, isClosed: true);
            a.LineTo(At(0), true, false);
            for (var i = 1; i < values.Count; i++)
            {
                var point = At(i);
                l.LineTo(point, true, true);
                a.LineTo(point, true, false);
            }
            a.LineTo(new Point(width, height), true, false);
        }
        line.Freeze();
        area.Freeze();

        var fill = Stroke.Clone();
        fill.Opacity = 0.16;
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);

        var pen = new Pen(Stroke, 1.4) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}

/// <summary>A row of thin vertical bars, each 0 to 100. Used for one bar per CPU thread.</summary>
public sealed class MiniBars : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(MiniBars),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(MiniBars),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(MiniBars),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>The unfilled part of each bar.</summary>
    public Brush? Track
    {
        get => (Brush?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        if (values is null || values.Count == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;

        // Whole-pixel bars and gaps stay crisp. Any leftover width is spread evenly by starting a little in.
        var gap = values.Count > 40 ? 1 : 2;
        var barWidth = Math.Max(1, Math.Floor((ActualWidth - gap * (values.Count - 1)) / values.Count));
        var used = barWidth * values.Count + gap * (values.Count - 1);
        var x = Math.Floor(Math.Max(0, ActualWidth - used) / 2);

        foreach (var value in values)
        {
            var filled = Math.Round(ActualHeight * Math.Clamp(value, 0, 100) / 100);
            if (Track is { } track) dc.DrawRectangle(track, null, new Rect(x, 0, barWidth, ActualHeight));
            if (filled > 0) dc.DrawRectangle(Fill, null, new Rect(x, ActualHeight - filled, barWidth, filled));
            x += barWidth + gap;
        }
    }
}
