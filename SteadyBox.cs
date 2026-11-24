using System.Windows;
using System.Windows.Controls;

namespace Glance;

/// <summary>
/// Keeps what is inside it from shrinking while it is on screen. Readings come and go all the time (a
/// program drops out of a list, a message clears), and a panel that followed every change would make the
/// whole widget twitch. So it grows when it must, and gives the space back only when the widget is hidden
/// or the display settings change (see <see cref="Epoch"/>).
/// </summary>
public sealed class SteadyBox : Decorator
{
    /// <summary>A new value here says the content is meant to be a different size now, so start again.</summary>
    public static readonly DependencyProperty EpochProperty = DependencyProperty.Register(
        nameof(Epoch), typeof(int), typeof(SteadyBox),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((SteadyBox)d).Release()));

    private double _width;
    private double _height;

    public SteadyBox()
    {
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) Release();
        };
    }

    public int Epoch
    {
        get => (int)GetValue(EpochProperty);
        set => SetValue(EpochProperty, value);
    }

    public bool HoldWidth { get; set; }
    public bool HoldHeight { get; set; }

    protected override Size MeasureOverride(Size constraint)
    {
        var size = base.MeasureOverride(constraint);
        if (HoldWidth) size.Width = Math.Max(_width, size.Width);
        if (HoldHeight) size.Height = Math.Max(_height, size.Height);
        return size;
    }

    // The size to keep is noted here and not while measuring. A change is measured in several goes, from the
    // inside out, and part way through the content can look bigger than it is about to be (a reading that has
    // moved from one list to another is briefly counted in both). Arranging waits until that is finished.
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (IsVisible && Child is { } child)
        {
            if (HoldWidth) _width = Math.Max(_width, child.DesiredSize.Width);
            if (HoldHeight) _height = Math.Max(_height, child.DesiredSize.Height);
        }
        return base.ArrangeOverride(arrangeSize);
    }

    private void Release()
    {
        _width = _height = 0;
        InvalidateMeasure();
    }
}
