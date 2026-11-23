namespace Glance.Core;

public enum MetricLevel { Normal, Warning, Critical }

/// <summary>
/// One reading in the widget. With a <see cref="Percent"/> it is drawn as a full-width row with a bar.
/// Without one it is drawn as a compact tile (label and value), two to a line.
/// </summary>
public sealed record MetricItem(string Label)
{
    /// <summary>0 to 100 drives the bar. Null makes this a compact tile.</summary>
    public double? Percent { get; init; }

    /// <summary>Right-aligned value, for example "42%" or "61 °C".</summary>
    public string? ValueText { get; init; }

    /// <summary>Small line under the bar. Ignored when <see cref="ResetsAt"/> is set, and for tiles.</summary>
    public string? Detail { get; init; }

    /// <summary>When set, the detail line becomes a live "Resets in ..." countdown.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>Colours the bar, or the value of a tile.</summary>
    public MetricLevel Level { get; init; }

    /// <summary>Gives a reading without a bar a whole line to itself, for long values such as a model name.</summary>
    public bool FullWidth { get; init; }
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

    public static MetricSnapshot Error(string notice, TimeSpan? retryAfter = null) =>
        new() { Notice = notice, IsError = true, RetryAfter = retryAfter };

    public static MetricSnapshot Info(string notice, string? subtitle = null) =>
        new() { Notice = notice, Subtitle = subtitle };
}

/// <summary>
/// A source of readings for one section of the widget. To add a metric, implement this and add it
/// to the list in App.BuildProviders.
/// FetchAsync runs on a worker thread, never twice at once, and only while the widget is visible.
/// Providers that hold handles should also implement IDisposable; the host disposes them.
/// </summary>
public interface IMetricProvider
{
    string Title { get; }
    TimeSpan RefreshInterval { get; }
    Task<MetricSnapshot> FetchAsync(CancellationToken ct);

    /// <summary>
    /// Showing the widget refreshes every section, except ones fetched more recently than this.
    /// Raise it for slow-changing data behind a web service that should not be called on every show.
    /// </summary>
    TimeSpan FreshFor => TimeSpan.FromSeconds(5);
}

/// <summary>
/// For providers that keep something running between fetches (a trace session, a sensor poll).
/// Pause is called when the widget is hidden so nothing runs in the background. The next fetch resumes.
/// </summary>
public interface IPausable
{
    void Pause();
}
