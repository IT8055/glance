using System.Collections.ObjectModel;
using System.Windows.Threading;
using Glance.Core;

namespace Glance;

/// <summary>
/// Runs the providers and feeds their results into the section view models.
///
/// A section is only fetched while something wants it. "Live" demand is the widget or the compact strip
/// being on screen, or a benchmark recording: fetched at the normal rate. "Background" demand is the tray
/// icon or an alert while everything is hidden: fetched slowly. With no demand a section is left alone
/// and anything it keeps running is paused.
/// </summary>
public sealed class MetricsHost : IDisposable
{
    private sealed class Entry(IMetricProvider provider, SectionViewModel section)
    {
        public IMetricProvider Provider { get; } = provider;
        public SectionViewModel Section { get; } = section;
        public DateTimeOffset LastStarted { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset NextDue { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset BackOffUntil { get; set; } = DateTimeOffset.MinValue;
        public bool Busy { get; set; }
        public bool Running { get; set; }
        public bool Live { get; set; }
    }

    private readonly ObservableCollection<SectionViewModel> _shown;
    private readonly DisplayOptions _options;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private List<Entry> _entries = [];
    private CancellationTokenSource _cts = new();
    private bool _everythingLive;
    private HashSet<string> _live = [];
    private HashSet<string> _background = [];

    /// <param name="shown">The sections drawn in the widget, in order. A subset of everything being run.</param>
    public MetricsHost(ObservableCollection<SectionViewModel> shown, DisplayOptions options)
    {
        _shown = shown;
        _options = options;
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Raised on the UI thread after each fetch, with the section name and what it read.</summary>
    public event Action<string, MetricSnapshot>? Fetched;

    /// <summary>Every section being run, including ones not drawn (kept for the tray icon, strip or alerts).</summary>
    public IEnumerable<SectionViewModel> AllSections => _entries.Select(e => e.Section);

    public T? Provider<T>() where T : class, IMetricProvider => _entries.Select(e => e.Provider).OfType<T>().FirstOrDefault();

    /// <param name="providers">Everything to run.</param>
    /// <param name="shownIds">Names of the sections to draw in the widget, in display order.</param>
    public void SetProviders(IEnumerable<IMetricProvider> providers, IReadOnlyList<string> shownIds)
    {
        Release();
        _cts = new CancellationTokenSource();
        _entries = providers
            .Select(p => new Entry(p, new SectionViewModel(p.Id, p.Title, _options) { CanCheckNow = p is IManualCheck }))
            .ToList();

        _shown.Clear();
        foreach (var id in shownIds)
        {
            if (_entries.FirstOrDefault(e => e.Provider.Id == id) is { } entry) _shown.Add(entry.Section);
        }

        // Nothing runs until SetDemand says what is wanted. The demand from before the rebuild may
        // name sections that are no longer needed, and must not be allowed to start them.
        _everythingLive = false;
        _live = [];
        _background = [];
        _timer.Stop();
    }

    /// <summary>Says which sections are wanted right now. See the class remarks for live and background.</summary>
    public void SetDemand(bool everythingLive, IEnumerable<string> live, IEnumerable<string> background)
    {
        _everythingLive = everythingLive;
        _live = [.. live];
        _background = [.. background];
        ApplyDemand(refreshNewlyLive: true);
    }

    /// <summary>
    /// Fetches every wanted section now. Sections that a service has told to back off are left alone.
    /// Unless <paramref name="force"/> is set, so are sections that are still fresh
    /// (see <see cref="IMetricProvider.FreshFor"/>), which keeps rapid show/hide from hammering a web service.
    /// </summary>
    public void RefreshNow(bool force = true)
    {
        var now = DateTimeOffset.Now;
        foreach (var entry in _entries.Where(e => IsLive(e) || IsBackground(e)))
        {
            var fresh = now - entry.LastStarted < entry.Provider.FreshFor;
            if ((force || !fresh) && now >= entry.BackOffUntil) entry.NextDue = now;
        }
        Tick();
    }

    /// <summary>
    /// Fetches one section now because the user pressed its "check now" button. This is the only thing
    /// that gets a section past its own limit on how often it really fetches.
    /// </summary>
    public void CheckNow(string providerId)
    {
        if (_entries.FirstOrDefault(e => e.Provider.Id == providerId) is not { } entry || entry.Busy) return;
        (entry.Provider as IManualCheck)?.CheckNow();
        _ = RunAsync(entry, _cts.Token);
    }

    public bool Has(string providerId) => _entries.Any(e => e.Provider.Id == providerId);

    /// <summary>Clears the lowest and highest values recorded so far.</summary>
    public void ResetRanges()
    {
        foreach (var entry in _entries) entry.Section.ResetRanges();
    }

    /// <summary>Redraws every section from its last reading, after a display option (graphs, ranges) changed.</summary>
    public void Redraw()
    {
        var now = DateTimeOffset.Now;
        foreach (var entry in _entries) entry.Section.Reapply(now);
    }

    public void Dispose()
    {
        _timer.Stop();
        Release();
    }

    private bool IsLive(Entry entry) => _everythingLive || _live.Contains(entry.Provider.Id);
    private bool IsBackground(Entry entry) => _background.Contains(entry.Provider.Id);

    private void ApplyDemand(bool refreshNewlyLive)
    {
        var now = DateTimeOffset.Now;
        var anyWanted = false;
        foreach (var entry in _entries)
        {
            var live = IsLive(entry);
            var wanted = live || IsBackground(entry);
            anyWanted |= wanted;

            // Coming on screen, or coming back into use: refresh straight away unless it was read moments ago.
            // This is what makes showing the widget refresh everything.
            var cameIntoUse = (live && !entry.Live) || (wanted && !entry.Running);
            var fresh = now - entry.LastStarted < entry.Provider.FreshFor;
            if (refreshNewlyLive && cameIntoUse && !fresh && now >= entry.BackOffUntil) entry.NextDue = now;

            // A fetch in flight could restart whatever Pause stops, so in that case the pause waits for it (see RunAsync).
            if (!wanted && entry.Running && !entry.Busy) (entry.Provider as IPausable)?.Pause();

            entry.Live = live;
            entry.Running = wanted;
        }

        if (anyWanted)
        {
            _timer.Start();
            Tick();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void Release()
    {
        _cts.Cancel();
        foreach (var entry in _entries)
        {
            try { (entry.Provider as IDisposable)?.Dispose(); }
            catch (Exception ex) { Log.Write($"{entry.Provider.Title} provider did not shut down cleanly: {ex.Message}"); }
        }
        _entries = [];
    }

    private void Tick()
    {
        var now = DateTimeOffset.Now;
        foreach (var entry in _entries)
        {
            entry.Section.Tick(now);
            if (entry.Running && !entry.Busy && now >= entry.NextDue) _ = RunAsync(entry, _cts.Token);
        }
    }

    private async Task RunAsync(Entry entry, CancellationToken ct)
    {
        var started = DateTimeOffset.Now;
        entry.Busy = true;
        entry.LastStarted = started;
        // Scheduled from the start time so a slow fetch does not stretch the interval.
        entry.NextDue = started + (IsLive(entry) ? entry.Provider.RefreshInterval : entry.Provider.BackgroundInterval);

        MetricSnapshot snapshot;
        try
        {
            // Providers may block (sensor reads, slow disks), so keep them off the UI thread.
            snapshot = await Task.Run(() => entry.Provider.FetchAsync(ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Write($"{entry.Provider.Title} provider failed: {ex}");
            snapshot = MetricSnapshot.Error("Could not read this metric. See glance.log.", TimeSpan.FromSeconds(30));
        }
        finally
        {
            entry.Busy = false;
            // Demand went away while this fetch was running: pause now that it has finished.
            if (!entry.Running) (entry.Provider as IPausable)?.Pause();
        }

        if (ct.IsCancellationRequested) return;

        var now = DateTimeOffset.Now;
        entry.Section.Apply(snapshot, now);
        if (snapshot.RetryAfter is { } wait)
        {
            entry.BackOffUntil = now + wait;
            entry.NextDue = entry.BackOffUntil;
        }
        Fetched?.Invoke(entry.Provider.Id, snapshot);
    }
}
