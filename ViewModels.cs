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

/// <summary>One value in the compact strip. Updated in place, so the strip is not rebuilt at every reading.</summary>
public sealed class StripValue(string reading) : Observable
{
    private string _text = "";
    private MetricLevel _level;

    /// <summary>Which reading this is, such as "gpu.temp".</summary>
    public string Reading { get; } = reading;
    public string Text { get => _text; set => Set(ref _text, value); }

    /// <summary>Colours the value, in the theme's warning or critical colour.</summary>
    public MetricLevel Level { get => _level; set => Set(ref _level, value); }
}

public sealed class OverlayViewModel : Observable
{
    private int _columns = 1;
    private double _maxColumnHeight = double.PositiveInfinity;
    private bool _compact;
    private string _recording = "";
    private IReadOnlyList<StripGroup> _strip = [];
    private bool _stripChosen;
    private readonly HashSet<string> _stripSeen = [];
    private string _profileTitle = "";
    private int _layoutEpoch;

    public ObservableCollection<SectionViewModel> Sections { get; } = [];

    /// <summary>The name of the profile in use, shown on the widget's title line.</summary>
    public string ProfileTitle { get => _profileTitle; set => Set(ref _profileTitle, value); }

    /// <summary>How many columns the sections are spread across, when Glance is arranging them itself.</summary>
    public int Columns { get => _columns; set => Set(ref _columns, value); }

    /// <summary>Room available for one column of sections on the current screen. Set by the window.</summary>
    public double MaxColumnHeight { get => _maxColumnHeight; set => Set(ref _maxColumnHeight, value); }

    /// <summary>Goes up when a display option has changed the size of the panels, so they are shared out among the columns afresh.</summary>
    public int LayoutEpoch { get => _layoutEpoch; set => Set(ref _layoutEpoch, value); }

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

    /// <summary>
    /// Brings the compact strip up to date with the chosen readings ("gpu.temp"), grouped by section in the
    /// order given. A reading that has been there and is missing for the moment leaves a dash in its place
    /// instead of making the strip shorter.
    /// </summary>
    public void UpdateStrip(IEnumerable<string> readings, IEnumerable<SectionViewModel> sections)
    {
        var byId = sections.ToDictionary(s => s.Id);
        var groups = new List<(string Title, List<(string Reading, string Text, MetricLevel Level)> Values)>();
        _stripChosen = false;
        foreach (var reading in readings)
        {
            _stripChosen = true;
            var dot = reading.IndexOf('.');
            if (dot <= 0 || !byId.TryGetValue(reading[..dot], out var section)) continue;

            var item = section.Find(reading[(dot + 1)..]);
            var text = item?.Brief ?? "";
            if (text.Length > 0) _stripSeen.Add(reading);
            else if (_stripSeen.Contains(reading)) text = Format.Missing;
            else continue;

            var group = groups.FirstOrDefault(g => g.Title == section.Title);
            if (group.Values is null) groups.Add(group = (section.Title, []));
            group.Values.Add((reading, text, item?.Level ?? MetricLevel.Normal));
        }

        // Only rebuilt when the readings themselves are different ones.
        var shape = groups.SelectMany(g => g.Values.Select(v => g.Title.ToUpperInvariant() + "|" + v.Reading));
        if (!_strip.SelectMany(g => g.Values.Select(v => g.Title + "|" + v.Reading)).SequenceEqual(shape) || groups.Count == 0)
        {
            Strip = groups
                .Select(g => new StripGroup(g.Title.ToUpperInvariant(), g.Values.Select(v => new StripValue(v.Reading)).ToList()))
                .ToList();
        }

        for (var g = 0; g < groups.Count; g++)
        {
            for (var v = 0; v < groups[g].Values.Count; v++)
            {
                _strip[g].Values[v].Text = groups[g].Values[v].Text;
                _strip[g].Values[v].Level = groups[g].Values[v].Level;
            }
        }
    }
}

/// <summary>
/// One provider's panel in the widget: a heading, full-width rows (with bars or graphs), compact tiles
/// (two to a line) and an optional notice.
/// </summary>
public sealed class SectionViewModel : Observable
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);

    // Kept by reading name so a reading keeps its history when it moves between rows and tiles.
    private readonly Dictionary<string, ItemViewModel> _items = [];
    private readonly DisplayOptions _options;
    private MetricSnapshot? _last;
    private DateTimeOffset? _lastGood;
    private int _column = -1;
    private int _epoch;
    private bool _renewed;
    private bool _hasReported;
    private int _listed;
    private string _subtitle = "";
    private string _notice = "";
    private string _listNotice = "";
    private bool _noticeIsError;
    private string _updatedText = "";

    /// <param name="reservedRows">
    /// For a list whose length changes from moment to moment: how many rows it always takes up. Rows it has
    /// nothing for are left blank, so the panel stays the same size. Zero for everything else.
    /// </param>
    public SectionViewModel(string id, string title, DisplayOptions options, int reservedRows = 0)
    {
        Id = id;
        Title = title;
        Heading = title.ToUpperInvariant();
        ReservedRows = reservedRows;
        _options = options;
        FillList([], DateTimeOffset.Now, isNewReading: false);
        ShowNotice("Loading…", isError: false);
    }

    /// <summary>The section's stable name, the first half of a reading name such as "gpu.temp".</summary>
    public string Id { get; }
    public string Title { get; }
    public string Heading { get; }
    public int ReservedRows { get; }

    /// <summary>Goes up when the panel is meant to be a different size from now on. See <see cref="SteadyBox"/>.</summary>
    public int Epoch { get => _epoch; private set => Set(ref _epoch, value); }

    /// <summary>True once there has been a first answer, good or bad, so the panel is about the size it will stay.</summary>
    public bool HasReported { get => _hasReported; private set => Set(ref _hasReported, value); }

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

    /// <summary>The notice of a list with nothing in it. Drawn over its blank rows, where it takes up no extra room.</summary>
    public string ListNotice
    {
        get => _listNotice;
        private set { Set(ref _listNotice, value); Raise(nameof(HasListNotice)); }
    }

    public bool HasListNotice => _listNotice.Length > 0;

    /// <summary>A notice about a problem is drawn in the warning colour.</summary>
    public bool NoticeIsError { get => _noticeIsError; private set => Set(ref _noticeIsError, value); }

    /// <summary>Empty while the data is fresh, otherwise how old it is.</summary>
    public string UpdatedText { get => _updatedText; private set => Set(ref _updatedText, value); }

    public ItemViewModel? Find(string key) => _items.GetValueOrDefault(key);

    public void Apply(MetricSnapshot snapshot, DateTimeOffset now) => Apply(snapshot, now, isNewReading: true);

    /// <summary>Draws the last reading again, after a display option changed. Does not count as a new sample.</summary>
    /// <param name="resized">True if the option changes how big the panel is.</param>
    public void Reapply(DateTimeOffset now, bool resized)
    {
        if (resized) Epoch++;
        if (_last is not null) Apply(_last, now, isNewReading: false);
    }

    /// <summary>
    /// Called when the section's source is rebuilt (after a settings change, for example). The panel keeps
    /// what it shows until the new source answers, instead of going blank, and takes its size from that answer.
    /// </summary>
    public void Renew() => _renewed = true;

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
            if (ReservedRows > 0) FillList(snapshot.Items, now, isNewReading);
            else FillReadings(snapshot.Items, now, isNewReading);

            Subtitle = snapshot.Subtitle ?? "";
            // Remembered readings carry the time they were really taken, so their age shows honestly.
            if (isNewReading) _lastGood = snapshot.AsOf ?? now;
        }

        if (isNewReading)
        {
            ShowNotice(snapshot.Notice ?? "", snapshot.IsError);
            if (_renewed) Epoch++;
            _renewed = false;
            HasReported = true;
        }
        Tick(now);
    }

    private void FillReadings(IReadOnlyList<MetricItem> items, DateTimeOffset now, bool isNewReading)
    {
        var rows = new List<ItemViewModel>();
        var tiles = new List<ItemViewModel>();
        var seen = new HashSet<string>();

        foreach (var item in items)
        {
            if (!seen.Add(item.Id)) continue; // two readings with one name: the first wins
            if (!_items.TryGetValue(item.Id, out var view)) _items[item.Id] = view = new ItemViewModel(item.Id);

            // A reading keeps its graph while it has no value for the moment, or it would change shape.
            var graphed = _options.Graphs && item.Graph;
            var isRow = item.Percent is not null || item.FullWidth || item.Bars is not null || graphed;
            view.Apply(item, now, _options, isRow, graphed, isNewReading);
            (isRow ? rows : tiles).Add(view);
        }
        foreach (var gone in _items.Keys.Where(k => !seen.Contains(k)).ToList()) _items.Remove(gone);

        Sync(Rows, rows);
        Sync(Tiles, tiles);
        Raise(nameof(HasTiles));
        _listed = items.Count;
    }

    // A list keeps the same rows throughout, whoever is in them, and blanks the ones it has nothing for.
    private void FillList(IReadOnlyList<MetricItem> items, DateTimeOffset now, bool isNewReading)
    {
        _listed = items.Count;
        for (var i = 0; i < Math.Max(ReservedRows, items.Count); i++)
        {
            var key = "row" + (i + 1);
            if (!_items.TryGetValue(key, out var view))
            {
                _items[key] = view = new ItemViewModel(key);
                Rows.Add(view);
            }
            view.Apply(i < items.Count ? items[i] : BlankRow, now, _options, isRow: true, graphed: false, isNewReading);
        }
    }

    // Non-breaking spaces: they draw as nothing but give the line its height.
    private static readonly MetricItem BlankRow = new("\u00A0") { ValueText = "\u00A0", FullWidth = true };

    private void ShowNotice(string text, bool isError)
    {
        var overList = ReservedRows > 0 && _listed == 0;
        NoticeIsError = isError;
        ListNotice = overList ? text : "";
        Notice = overList ? "" : text;
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

        if (isNewReading)
        {
            if (item.Number is { } number && double.IsFinite(number))
            {
                _samples.Enqueue((now, number));
                _lowest = Math.Min(_lowest, number);
                _highest = Math.Max(_highest, number);
            }
            while (_samples.Count > 0 && now - _samples.Peek().At > HistoryWindow) _samples.Dequeue();
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
