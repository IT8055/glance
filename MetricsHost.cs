using System.Collections.ObjectModel;
using System.Windows.Threading;
using Glance.Core;

namespace Glance;

/// <summary>
/// Runs the providers and feeds their results into the section view models.
/// Nothing is fetched while the widget is hidden. Showing it refreshes every section straight away.
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
    }

    private readonly ObservableCollection<SectionViewModel> _sections;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private List<Entry> _entries = [];
    private CancellationTokenSource _cts = new();

    public MetricsHost(ObservableCollection<SectionViewModel> sections)
    {
        _sections = sections;
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>True while the widget is on screen.</summary>
    public bool Active
    {
        get => _timer.IsEnabled;
        set
        {
            if (value == _timer.IsEnabled) return;
            if (value)
            {
                _timer.Start();
                RefreshNow(force: false);
            }
            else
            {
                _timer.Stop();
                foreach (var entry in _entries) (entry.Provider as IPausable)?.Pause();
            }
        }
    }

    public void SetProviders(IEnumerable<IMetricProvider> providers)
    {
        Release();
        _cts = new CancellationTokenSource();
        _sections.Clear();
        _entries = [];

        foreach (var provider in providers)
        {
            var section = new SectionViewModel(provider.Title);
            _sections.Add(section);
            _entries.Add(new Entry(provider, section));
        }

        if (Active) Tick();
    }

    /// <summary>
    /// Fetches every section now, even if the widget is hidden. Sections that a service has told to back off
    /// are left alone. Unless <paramref name="force"/> is set, so are sections that are still fresh
    /// (see <see cref="IMetricProvider.FreshFor"/>), which keeps rapid show/hide from hammering a web service.
    /// </summary>
    public void RefreshNow(bool force = true)
    {
        var now = DateTimeOffset.Now;
        foreach (var entry in _entries)
        {
            var fresh = now - entry.LastStarted < entry.Provider.FreshFor;
            if ((force || !fresh) && now >= entry.BackOffUntil) entry.NextDue = now;
        }
        Tick();
    }

    public void Dispose()
    {
        _timer.Stop();
        Release();
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
            if (!entry.Busy && now >= entry.NextDue) _ = RunAsync(entry, _cts.Token);
        }
    }

    private async Task RunAsync(Entry entry, CancellationToken ct)
    {
        var started = DateTimeOffset.Now;
        entry.Busy = true;
        entry.LastStarted = started;
        // Scheduled from the start time so a slow fetch does not stretch the interval.
        entry.NextDue = started + entry.Provider.RefreshInterval;

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
        }

        if (ct.IsCancellationRequested) return;

        var now = DateTimeOffset.Now;
        entry.Section.Apply(snapshot, now);
        if (snapshot.RetryAfter is { } wait)
        {
            entry.BackOffUntil = now + wait;
            entry.NextDue = entry.BackOffUntil;
        }
    }
}
