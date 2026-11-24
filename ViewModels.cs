using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Glance.Core;
using Glance.Providers;

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

/// <summary>How readings are drawn. Shared by every section so a change in Settings reaches them all.</summary>
public sealed class DisplayOptions
{
    /// <summary>Draw a line of the last minute for readings that are worth one.</summary>
    public bool Graphs { get; set; }

    /// <summary>Show the lowest and highest value seen for each reading.</summary>
    public bool Ranges { get; set; }
}

/// <summary>One section's contribution to the compact strip: "GPU 97% 64 °C".</summary>
public sealed record StripGroup(string Title, IReadOnlyList<StripValue> Values);

/// <param name="Level">Colours the value, in the theme's warning or critical colour.</param>
public sealed record StripValue(string Text, MetricLevel Level);

public sealed class OverlayViewModel : Observable
{
    private int _columns = 1;
    private double _maxColumnHeight = double.PositiveInfinity;
    private bool _compact;
    private string _recording = "";
    private IReadOnlyList<StripGroup> _strip = [];
    private bool _stripChosen;
    private string _profileTitle = "";

    public ObservableCollection<SectionViewModel> Sections { get; } = [];

    /// <summary>The name of the profile in use, shown on the widget's title line.</summary>
    public string ProfileTitle { get => _profileTitle; set => Set(ref _profileTitle, value); }

    /// <summary>How many columns the sections are spread across, when Glance is arranging them itself.</summary>
    public int Columns { get => _columns; set => Set(ref _columns, value); }

    /// <summary>Room available for one column of sections on the current screen. Set by the window.</summary>
    public double MaxColumnHeight { get => _maxColumnHeight; set => Set(ref _maxColumnHeight, value); }

    /// <summary>True while the one-line strip is showing instead of the full widget.</summary>
    public bool IsCompact
    {
        get => _compact;
        set { Set(ref _compact, value); Raise(nameof(IsFull)); }
    }

    public bool IsFull => !_compact;

    public IReadOnlyList<StripGroup> Strip
    {
        get => _strip;
        private set
        {
            Set(ref _strip, value);
            Raise(nameof(StripHint));
            Raise(nameof(HasStripHint));
        }
    }

    /// <summary>Stands in for the strip while it has nothing to show.</summary>
    public string StripHint => _strip.Count > 0 ? "" : _stripChosen ? "Waiting for readings…" : "Pick readings for the strip in Settings";
    public bool HasStripHint => _strip.Count == 0;

    /// <summary>"REC 0:42" while a benchmark is being recorded, otherwise empty.</summary>
    public string Recording
    {
        get => _recording;
        set { Set(ref _recording, value); Raise(nameof(IsRecording)); }
    }

    public bool IsRecording => _recording.Length > 0;

    /// <summary>Rebuilds the compact strip from the chosen readings ("gpu.temp"), grouped by section in the order given.</summary>
    public void UpdateStrip(IEnumerable<string> readings, IEnumerable<SectionViewModel> sections)
    {
        var byId = sections.ToDictionary(s => s.Id);
        var groups = new List<(string Title, List<StripValue> Values)>();
        _stripChosen = false;
        foreach (var reading in readings)
        {
            _stripChosen = true;
            var dot = reading.IndexOf('.');
            if (dot <= 0 || !byId.TryGetValue(reading[..dot], out var section)) continue;
            if (section.Find(reading[(dot + 1)..]) is not { } item || item.Brief.Length == 0) continue;

            var group = groups.FirstOrDefault(g => g.Title == section.Title);
            if (group.Values is null) groups.Add(group = (section.Title, []));
            group.Values.Add(new StripValue(item.Brief, item.Level));
        }
        Strip = groups.Select(g => new StripGroup(g.Title.ToUpperInvariant(), g.Values)).ToList();
    }
}

/// <summary>
/// One provider's panel in the widget: a heading, full-width rows (with bars or graphs), compact tiles
/// (two to a line) and an optional notice.
/// </summary>
public sealed class SectionViewModel(string id, string title, DisplayOptions options) : Observable
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);

    // Kept by reading name so a reading keeps its history when it moves between rows and tiles.
    private readonly Dictionary<string, ItemViewModel> _items = [];
    private MetricSnapshot? _last;
    private DateTimeOffset? _lastGood;
    private int _column = -1;
    private string _subtitle = "";
    private string _notice = "Loading…";
    private bool _noticeIsError;
    private string _updatedText = "";

    /// <summary>The section's stable name, the first half of a reading name such as "gpu.temp".</summary>
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Heading { get; } = title.ToUpperInvariant();
    public ObservableCollection<ItemViewModel> Rows { get; } = [];
    public ObservableCollection<ItemViewModel> Tiles { get; } = [];

    /// <summary>Every reading in the section, whichever way it is drawn.</summary>
    public IEnumerable<ItemViewModel> Items => Rows.Concat(Tiles);

    /// <summary>Which column the user has put this panel in, or -1 to let Glance decide.</summary>
    public int Column { get => _column; set => Set(ref _column, value); }

    /// <summary>True for a section that limits how often it really fetches: its panel gets a "check now" button.</summary>
    public bool CanCheckNow { get; init; }

    /// <summary>Shown beside the heading: the hardware model, the app being measured, the place.</summary>
    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }

    public bool HasTiles => Tiles.Count > 0;

    public string Notice
    {
        get => _notice;
        private set { Set(ref _notice, value); Raise(nameof(HasNotice)); }
    }

    public bool HasNotice => _notice.Length > 0;

    /// <summary>A notice about a problem is drawn in the warning colour.</summary>
    public bool NoticeIsError { get => _noticeIsError; private set => Set(ref _noticeIsError, value); }

    /// <summary>Empty while the data is fresh, otherwise how old it is.</summary>
    public string UpdatedText { get => _updatedText; private set => Set(ref _updatedText, value); }

    public ItemViewModel? Find(string key) => _items.GetValueOrDefault(key);

    public void Apply(MetricSnapshot snapshot, DateTimeOffset now) => Apply(snapshot, now, isNewReading: true);

    /// <summary>Draws the last reading again, after a display option changed. Does not count as a new sample.</summary>
    public void Reapply(DateTimeOffset now)
    {
        if (_last is not null) Apply(_last, now, isNewReading: false);
    }

    public void ResetRanges()
    {
        foreach (var item in _items.Values) item.ResetRange();
    }

    /// <summary>Refreshes the time-based text (countdowns, data age) without fetching.</summary>
    public void Tick(DateTimeOffset now)
    {
        foreach (var row in Rows) row.Tick(now);
        UpdatedText = _lastGood is { } last && now - last > StaleAfter ? TimeText.Ago(now - last) : "";
    }

    private void Apply(MetricSnapshot snapshot, DateTimeOffset now, bool isNewReading)
    {
        // A failed fetch keeps the last good readings on screen and only adds the notice.
        if (!snapshot.IsError)
        {
            _last = snapshot;
            var rows = new List<ItemViewModel>();
            var tiles = new List<ItemViewModel>();
            var seen = new HashSet<string>();

            foreach (var item in snapshot.Items)
            {
                if (!seen.Add(item.Id)) continue; // two readings with one name: the first wins
                if (!_items.TryGetValue(item.Id, out var view)) _items[item.Id] = view = new ItemViewModel(item.Id);

                var graphed = options.Graphs && item.Graph && item.Number is not null;
                var isRow = item.Percent is not null || item.FullWidth || item.Bars is not null || graphed;
                view.Apply(item, now, options, isRow, graphed, isNewReading);
                (isRow ? rows : tiles).Add(view);
            }
            foreach (var gone in _items.Keys.Where(k => !seen.Contains(k)).ToList()) _items.Remove(gone);

            Sync(Rows, rows);
            Sync(Tiles, tiles);
            Raise(nameof(HasTiles));
            Subtitle = snapshot.Subtitle ?? "";
            // Remembered readings carry the time they were really taken, so their age shows honestly.
            if (isNewReading) _lastGood = snapshot.AsOf ?? now;
        }

        if (isNewReading)
        {
            NoticeIsError = snapshot.IsError;
            Notice = snapshot.Notice ?? "";
        }
        Tick(now);
    }

    // Leaves the collection alone when nothing moved, so the widget does not flicker on every refresh.
    private static void Sync(ObservableCollection<ItemViewModel> target, List<ItemViewModel> source)
    {
        if (target.SequenceEqual(source)) return;
        target.Clear();
        foreach (var item in source) target.Add(item);
    }
}

public sealed class ItemViewModel(string key) : Observable
{
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromSeconds(60);

    private readonly Queue<(DateTimeOffset At, double Value)> _samples = new();
    private MetricItem _item = new("");
    private bool _isRow;
    private bool _showRange;
    private double _lowest = double.PositiveInfinity;
    private double _highest = double.NegativeInfinity;

    private string _label = "";
    private string _valueText = "";
    private string _brief = "";
    private double _percent;
    private bool _showBar;
    private MetricLevel _level;
    private MetricLevel _valueLevel;
    private bool _showGraph;
    private double _graphMax = double.NaN;
    private IReadOnlyList<double> _history = [];
    private IReadOnlyList<double> _bars = [];
    private bool _hasBars;
    private string _detail = "";
    private bool _hasDetail;
    private string _rangeText = "";
    private bool _hasRange;

    public string Key { get; } = key;
    public string Label { get => _label; private set => Set(ref _label, value); }
    public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }

    /// <summary>Short value for the compact strip and the tray tooltip.</summary>
    public string Brief { get => _brief; private set => Set(ref _brief, value); }

    /// <summary>The latest reading as a number, when it has one.</summary>
    public double? Number => _item.Number;
    public string Unit => _item.Unit ?? "";

    /// <summary>Colours the bar or graph. The theme decides the actual colour.</summary>
    public MetricLevel Level { get => _level; private set => Set(ref _level, value); }

    /// <summary>Colours the value text. Normal when there is a bar to carry the warning instead.</summary>
    public MetricLevel ValueLevel { get => _valueLevel; private set => Set(ref _valueLevel, value); }

    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public bool ShowBar { get => _showBar; private set => Set(ref _showBar, value); }
    public bool ShowGraph { get => _showGraph; private set => Set(ref _showGraph, value); }

    /// <summary>Top of the graph scale. Not a number means "fit the graph to its own range".</summary>
    public double GraphMax { get => _graphMax; private set => Set(ref _graphMax, value); }

    /// <summary>The last minute of readings, oldest first.</summary>
    public IReadOnlyList<double> History { get => _history; private set => Set(ref _history, value); }

    public IReadOnlyList<double> Bars { get => _bars; private set => Set(ref _bars, value); }
    public bool HasBars { get => _hasBars; private set => Set(ref _hasBars, value); }

    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public bool HasDetail { get => _hasDetail; private set => Set(ref _hasDetail, value); }

    /// <summary>Lowest and highest seen, for tiles. Rows put it in their detail line instead.</summary>
    public string RangeText { get => _rangeText; private set => Set(ref _rangeText, value); }
    public bool HasRange { get => _hasRange; private set => Set(ref _hasRange, value); }

    public void Apply(MetricItem item, DateTimeOffset now, DisplayOptions options, bool isRow, bool graphed, bool isNewReading)
    {
        _item = item;
        _isRow = isRow;
        // Only for readings that move about. On drive space or today's forecast it would just repeat the value.
        _showRange = options.Ranges && item.Graph;

        if (isNewReading && item.Number is { } number && double.IsFinite(number))
        {
            _samples.Enqueue((now, number));
            while (_samples.Count > 0 && now - _samples.Peek().At > HistoryWindow) _samples.Dequeue();
            _lowest = Math.Min(_lowest, number);
            _highest = Math.Max(_highest, number);
        }

        Label = item.Label;
        ValueText = item.ValueText ?? "";
        Brief = item.Brief ?? item.ValueText ?? "";
        Percent = item.Percent ?? 0;
        Level = item.Level;
        ValueLevel = item.Percent is null ? item.Level : MetricLevel.Normal;

        // A graph already shows the current level at its right-hand end, so it takes the bar's place.
        ShowGraph = graphed;
        ShowBar = item.Percent is not null && !graphed;
        GraphMax = item.GraphMax ?? double.NaN;
        if (graphed) History = _samples.Select(s => s.Value).ToArray();

        Bars = item.Bars ?? [];
        HasBars = item.Bars is { Count: > 0 };

        Tick(now);
    }

    public void ResetRange()
    {
        _lowest = double.PositiveInfinity;
        _highest = double.NegativeInfinity;
    }

    public void Tick(DateTimeOffset now)
    {
        // Rows have a line to themselves ("low 41 · high 67 °C"). Tiles are half as wide ("41 to 67 °C").
        var range = !_showRange || _lowest > _highest ? ""
            : _isRow ? $"low {Format.Compact(_lowest)} · high {Format.WithUnit(_highest, Unit)}"
            : $"{Format.Compact(_lowest)} to {Format.WithUnit(_highest, Unit)}";

        var parts = new List<string>(3);
        if (_item.ResetsAt is { } resetsAt) parts.Add(TimeText.ResetsIn(resetsAt, now));
        if (!string.IsNullOrEmpty(_item.Detail)) parts.Add(_item.Detail);
        if (_isRow && range.Length > 0) parts.Add(range);
        Detail = string.Join(" · ", parts);
        HasDetail = Detail.Length > 0;

        RangeText = _isRow ? "" : range;
        HasRange = RangeText.Length > 0;
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

    /// <summary>"Thu 14:00", or just the time when it is today.</summary>
    public static string DayAndTime(DateTimeOffset moment, DateTimeOffset now)
    {
        var local = moment.ToLocalTime();
        return local.Date == now.ToLocalTime().Date ? $"{local:t}" : $"{local:ddd} {local:t}";
    }

    public static string Ago(TimeSpan age) =>
        age.TotalMinutes < 60 ? $"{Math.Max(1, (int)age.TotalMinutes)}m ago"
        : age.TotalHours < 24 ? $"{(int)age.TotalHours}h ago"
        : $"{(int)age.TotalDays}d ago";

    /// <summary>"0:42", "12:05", "1:02:03".</summary>
    public static string Clock(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}";
}
