using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Glance.Core;

namespace Glance;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class OverlayViewModel : Observable
{
    private int _columns = 1;

    public ObservableCollection<SectionViewModel> Sections { get; } = [];

    /// <summary>How many columns the sections are spread across.</summary>
    public int Columns { get => _columns; set => Set(ref _columns, value); }

    /// <summary>Room available for one column of sections on the current screen. Set by the window.</summary>
    public double MaxColumnHeight { get => _maxColumnHeight; set => Set(ref _maxColumnHeight, value); }
    private double _maxColumnHeight = double.PositiveInfinity;
}

/// <summary>
/// One provider's block in the widget: a heading, full-width rows (with bars), compact tiles
/// (two to a line) and an optional notice.
/// </summary>
public sealed class SectionViewModel(string title) : Observable
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);
    private static readonly Brush InfoBrush = Palette.Frozen("#99FFFFFF");
    private static readonly Brush ErrorBrush = Palette.Frozen("#F2B04A");

    private DateTimeOffset? _lastGood;
    private string _subtitle = "";
    private string _notice = "Loading…";
    private Brush _noticeBrush = InfoBrush;
    private string _updatedText = "";

    public string Heading { get; } = title.ToUpperInvariant();
    public ObservableCollection<ItemViewModel> Rows { get; } = [];
    public ObservableCollection<ItemViewModel> Tiles { get; } = [];

    /// <summary>Shown beside the heading: the hardware model, the app being measured, the place.</summary>
    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }

    public bool HasTiles => Tiles.Count > 0;

    public string Notice
    {
        get => _notice;
        private set { Set(ref _notice, value); Raise(nameof(HasNotice)); }
    }

    public bool HasNotice => _notice.Length > 0;
    public Brush NoticeBrush { get => _noticeBrush; private set => Set(ref _noticeBrush, value); }

    /// <summary>Empty while the data is fresh, otherwise how old it is.</summary>
    public string UpdatedText { get => _updatedText; private set => Set(ref _updatedText, value); }

    public void Apply(MetricSnapshot snapshot, DateTimeOffset now)
    {
        // A failed fetch keeps the last good readings on screen and only adds the notice.
        if (!snapshot.IsError)
        {
            Sync(Rows, snapshot.Items.Where(i => i.Percent is not null || i.FullWidth), now);
            Sync(Tiles, snapshot.Items.Where(i => i.Percent is null && !i.FullWidth), now);
            Raise(nameof(HasTiles));
            Subtitle = snapshot.Subtitle ?? "";
            _lastGood = now;
        }

        NoticeBrush = snapshot.IsError ? ErrorBrush : InfoBrush;
        Notice = snapshot.Notice ?? "";
        Tick(now);
    }

    /// <summary>Refreshes the time-based text (countdowns, data age) without fetching.</summary>
    public void Tick(DateTimeOffset now)
    {
        foreach (var row in Rows) row.Tick(now);
        UpdatedText = _lastGood is { } last && now - last > StaleAfter ? TimeText.Ago(now - last) : "";
    }

    // Updates the existing view models in place so the widget does not flicker on every refresh.
    private static void Sync(ObservableCollection<ItemViewModel> target, IEnumerable<MetricItem> source, DateTimeOffset now)
    {
        var index = 0;
        foreach (var item in source)
        {
            if (index == target.Count) target.Add(new ItemViewModel());
            target[index++].Apply(item, now);
        }
        while (target.Count > index) target.RemoveAt(target.Count - 1);
    }
}

public sealed class ItemViewModel : Observable
{
    private static readonly Brush NormalBrush = Palette.Frozen("#6FB1FF");
    private static readonly Brush WarningBrush = Palette.Frozen("#F2B04A");
    private static readonly Brush CriticalBrush = Palette.Frozen("#F0616D");
    private static readonly Brush PlainValueBrush = Palette.Frozen("#FFFFFFFF");

    private MetricItem _item = new("");
    private string _label = "";
    private string _valueText = "";
    private double _percent;
    private bool _hasBar;
    private Brush _barBrush = NormalBrush;
    private Brush _valueBrush = PlainValueBrush;
    private string _detail = "";
    private bool _hasDetail;

    public string Label { get => _label; private set => Set(ref _label, value); }
    public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public bool HasBar { get => _hasBar; private set => Set(ref _hasBar, value); }
    public Brush BarBrush { get => _barBrush; private set => Set(ref _barBrush, value); }

    /// <summary>White normally. A tile has no bar, so a warning shows in the value itself.</summary>
    public Brush ValueBrush { get => _valueBrush; private set => Set(ref _valueBrush, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public bool HasDetail { get => _hasDetail; private set => Set(ref _hasDetail, value); }

    public void Apply(MetricItem item, DateTimeOffset now)
    {
        _item = item;
        Label = item.Label;
        ValueText = item.ValueText ?? "";
        Percent = item.Percent ?? 0;
        HasBar = item.Percent is not null;
        BarBrush = item.Level switch
        {
            MetricLevel.Critical => CriticalBrush,
            MetricLevel.Warning => WarningBrush,
            _ => NormalBrush,
        };
        ValueBrush = item.Percent is null && item.Level != MetricLevel.Normal ? BarBrush : PlainValueBrush;
        Tick(now);
    }

    public void Tick(DateTimeOffset now)
    {
        Detail = _item.ResetsAt is { } resetsAt ? TimeText.ResetsIn(resetsAt, now) : _item.Detail ?? "";
        HasDetail = Detail.Length > 0;
    }
}

internal static class Palette
{
    public static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

internal static class TimeText
{
    public static string ResetsIn(DateTimeOffset resetsAt, DateTimeOffset now)
    {
        var left = resetsAt - now;
        if (left <= TimeSpan.Zero) return "Resetting…";
        if (left < TimeSpan.FromHours(1)) return $"Resets in {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
        if (left < TimeSpan.FromHours(24)) return $"Resets in {(int)left.TotalHours}h {left.Minutes}m";

        var local = resetsAt.ToLocalTime();
        return $"Resets {local:ddd} {local:t}";
    }

    public static string Ago(TimeSpan age) =>
        age.TotalMinutes < 60 ? $"{Math.Max(1, (int)age.TotalMinutes)}m ago"
        : age.TotalHours < 24 ? $"{(int)age.TotalHours}h ago"
        : $"{(int)age.TotalDays}d ago";
}
