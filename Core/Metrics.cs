using System.Text;

namespace Glance.Core;

public enum MetricLevel { Normal, Warning, Critical }

/// <summary>
/// One reading in the widget. With a <see cref="Percent"/> it is drawn as a full-width row with a bar.
/// Without one it is drawn as a compact tile (label and value), two to a line.
/// </summary>
public sealed record MetricItem(string Label)
{
    /// <summary>Stable name within the section, used by settings ("gpu.temp"). Defaults to a slug of the label.</summary>
    public string? Key { get; init; }

    /// <summary>0 to 100 drives the bar. Null makes this a compact tile.</summary>
    public double? Percent { get; init; }

    /// <summary>Right-aligned value, for example "42%" or "61 °C".</summary>
    public string? ValueText { get; init; }

    /// <summary>A shorter value for the compact strip, where "11 / 32 GB" would be too long. Defaults to the value text.</summary>
    public string? Brief { get; init; }

    /// <summary>The reading as a plain number. Drives graphs, lowest and highest, the tray icon and benchmark logs.</summary>
    public double? Number { get; init; }

    /// <summary>Unit of <see cref="Number"/>: "%", "°C", "fps".</summary>
    public string? Unit { get; init; }

    /// <summary>True for readings worth a history graph (load, temperature, frame rate).</summary>
    public bool Graph { get; init; }

    /// <summary>Fixed top of the graph scale, 100 for percentages. Null scales the graph to its own range.</summary>
    public double? GraphMax { get; init; }

    /// <summary>A strip of small 0 to 100 bars under the label, for example one per CPU thread.</summary>
    public IReadOnlyList<double>? Bars { get; init; }

    /// <summary>Small line under the bar. Shown after the countdown when <see cref="ResetsAt"/> is set. Not shown on tiles.</summary>
    public string? Detail { get; init; }

    /// <summary>When set, the detail line starts with a live "Resets in ..." countdown.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>Colours the bar, or the value of a tile.</summary>
    public MetricLevel Level { get; init; }

    /// <summary>Gives a reading without a bar a whole line to itself, for long values such as a model name.</summary>
    public bool FullWidth { get; init; }

    public string Id => Key ?? Slug(Label);

    /// <summary>"Busiest core" becomes "busiest_core".</summary>
    public static string Slug(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '_') builder.Append('_');
        }
        return builder.ToString().Trim('_');
    }
}

/// <summary>The result of one fetch from a provider.</summary>
public sealed record MetricSnapshot
{
    public IReadOnlyList<MetricItem> Items { get; init; } = [];

    /// <summary>Shown next to the section heading, for example the GPU model or the app being measured.</summary>
    public string? Subtitle { get; init; }

    /// <summary>Optional message shown under the rows.</summary>
    public string? Notice { get; init; }

    /// <summary>True when the fetch failed. The widget keeps the previous rows and shows the notice.</summary>
    public bool IsError { get; init; }

    /// <summary>Overrides the provider's refresh interval for the next fetch (back-off).</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// When the readings were actually taken, if that was not just now. A provider that hands back
    /// remembered readings sets this, so the widget can say how old they are.
    /// </summary>
    public DateTimeOffset? AsOf { get; init; }

    public static MetricSnapshot Error(string notice, TimeSpan? retryAfter = null) =>
        new() { Notice = notice, IsError = true, RetryAfter = retryAfter };

    public static MetricSnapshot Info(string notice, string? subtitle = null) =>
        new() { Notice = notice, Subtitle = subtitle };
}

/// <summary>
/// A source of readings for one section of the widget. To add a metric, implement this and register it
/// in Sections.cs.
/// FetchAsync runs on a worker thread, never twice at once, and only while something wants its readings.
/// Providers that hold handles should also implement IDisposable; the host disposes them.
/// </summary>
public interface IMetricProvider
{
    string Title { get; }
    TimeSpan RefreshInterval { get; }
    Task<MetricSnapshot> FetchAsync(CancellationToken ct);

    /// <summary>Stable name of the section, the first half of a reading's name ("gpu" in "gpu.temp").</summary>
    string Id => MetricItem.Slug(Title);

    /// <summary>
    /// Showing the widget refreshes every section, except ones fetched more recently than this.
    /// Raise it for slow-changing data behind a web service that should not be called on every show.
    /// </summary>
    TimeSpan FreshFor => TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often to fetch while the widget is hidden but something still needs this section:
    /// the tray icon, or an alert. Never faster than every two seconds.
    /// </summary>
    TimeSpan BackgroundInterval => RefreshInterval < TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : RefreshInterval;
}

/// <summary>
/// For providers that keep something running between fetches (a trace session, a sensor poll).
/// Pause is called when nothing wants the section any more, so nothing runs in the background. The next fetch resumes.
/// </summary>
public interface IPausable
{
    void Pause();
}

/// <summary>
/// For providers that limit how often they really fetch (a web service that must not be called too often).
/// Their panel gets a "check now" button, which calls this before the next fetch to lift the limit once.
/// </summary>
public interface IManualCheck
{
    void CheckNow();
}
