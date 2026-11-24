namespace Glance.Providers;

/// <summary>
/// Picks the busiest few of something that comes in bursts, steadily enough to read. Network traffic stops
/// and starts all the time, so going by each second on its own makes programs pop in and out of a list.
/// Instead each one is judged on its average over the last few seconds, stays listed for a while after it
/// goes quiet, and only moves past another once it has been clearly ahead of it for a few seconds running.
/// </summary>
/// <param name="shown">How many places there are.</param>
/// <param name="worthListing">The least that earns a place. Also the least difference that counts as being ahead.</param>
/// <param name="average">How far back the figures shown are averaged.</param>
/// <param name="hold">
/// How long a place is kept after falling below <paramref name="worthListing"/>. When the list is full, who
/// gives up a place to a newcomer is also decided on the average over this long.
/// </param>
internal sealed class TopList(int shown, double worthListing, TimeSpan average, TimeSpan hold)
{
    private const double Ahead = 1.2;
    private static readonly TimeSpan AheadFor = TimeSpan.FromSeconds(3);

    public readonly record struct Entry(string Key, double Down, double Up)
    {
        public double Total => Down + Up;
    }

    private readonly record struct Sample(TimeSpan At, double Seconds, Dictionary<string, Entry> Rates);

    private readonly Queue<Sample> _samples = new();
    private readonly Dictionary<string, TimeSpan> _lastBusy = [];
    private readonly List<string> _listed = [];
    private Dictionary<(string Lower, string Upper), TimeSpan> _aheadSince = [];
    private TimeSpan? _updated;

    /// <param name="now">Time from any fixed start. Only differences matter.</param>
    /// <param name="rates">What each one is doing at the moment. Any not mentioned are doing nothing.</param>
    public List<Entry> Update(TimeSpan now, IEnumerable<Entry> rates)
    {
        // Each sample counts for as long as it covers, so an odd short one does not weigh as much as a full one.
        var seconds = _updated is { } before ? Math.Max((now - before).TotalSeconds, 0.01) : 1;
        _updated = now;
        _samples.Enqueue(new Sample(now, seconds, rates.ToDictionary(r => r.Key)));
        while (_samples.Count > 1 && now - _samples.Peek().At >= hold) _samples.Dequeue();

        var recent = Averages(now, average);
        var longer = Averages(now, hold);
        double Recent(string key) => recent.GetValueOrDefault(key).Total;
        double Longer(string key) => longer.GetValueOrDefault(key).Total;

        foreach (var entry in recent.Values)
        {
            if (entry.Total >= worthListing) _lastBusy[entry.Key] = now;
        }
        foreach (var quiet in _lastBusy.Where(b => now - b.Value > hold).Select(b => b.Key).ToList())
        {
            _lastBusy.Remove(quiet);
            _listed.Remove(quiet);
        }

        // A newcomer goes straight to where it belongs.
        foreach (var newcomer in recent.Values.Where(e => e.Total >= worthListing && !_listed.Contains(e.Key)).OrderByDescending(e => e.Total).ToList())
        {
            int place;
            if (_listed.Count < shown)
            {
                _listed.Add(newcomer.Key);
                place = _listed.Count - 1;
            }
            else
            {
                var weakest = _listed.MinBy(Longer)!;
                if (!IsAhead(Longer(newcomer.Key), Longer(weakest))) continue;
                place = _listed.IndexOf(weakest);
                _listed[place] = newcomer.Key;
            }
            for (; place > 0 && IsAhead(newcomer.Total, Recent(_listed[place - 1])); place--)
                (_listed[place], _listed[place - 1]) = (_listed[place - 1], _listed[place]);
        }

        // One already listed has to stay ahead for a while first. Every pair is timed, not just neighbours, so
        // one that shoots up from the bottom moves past all the others in one go.
        var aheadSince = new Dictionary<(string Lower, string Upper), TimeSpan>();
        for (var lower = 1; lower < _listed.Count; lower++)
        {
            for (var upper = 0; upper < lower; upper++)
            {
                var pair = (_listed[lower], _listed[upper]);
                if (IsAhead(Recent(pair.Item1), Recent(pair.Item2)))
                    aheadSince[pair] = _aheadSince.TryGetValue(pair, out var since) ? since : now;
            }
        }
        _aheadSince = aheadSince;
        for (var i = 1; i < _listed.Count; i++)
        {
            for (var j = i; j > 0 && _aheadSince.TryGetValue((_listed[j], _listed[j - 1]), out var since) && now - since >= AheadFor; j--)
                (_listed[j], _listed[j - 1]) = (_listed[j - 1], _listed[j]);
        }

        return _listed.Select(key => recent.TryGetValue(key, out var entry) ? entry : new Entry(key, 0, 0)).ToList();
    }

    private bool IsAhead(double value, double other) => value > other * Ahead + worthListing;

    private Dictionary<string, Entry> Averages(TimeSpan now, TimeSpan window)
    {
        // Always at least the newest sample, however slow the refresh rate.
        var samples = _samples.Where(s => now - s.At < window).ToList();
        var covered = samples.Sum(s => s.Seconds);
        var totals = new Dictionary<string, Entry>();
        foreach (var sample in samples)
        {
            var share = sample.Seconds / covered;
            foreach (var (key, rate) in sample.Rates)
            {
                var sum = totals.GetValueOrDefault(key);
                totals[key] = new Entry(key, sum.Down + rate.Down * share, sum.Up + rate.Up * share);
            }
        }
        return totals;
    }
}
