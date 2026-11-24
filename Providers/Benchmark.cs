using System.Diagnostics;
using System.Globalization;
using System.Text;
using Glance.Core;

namespace Glance.Providers;

/// <param name="Key">Reading name, for example "gpu.temp".</param>
public sealed record BenchmarkStat(string Key, string Name, string Unit, double Lowest, double Average, double Highest);

/// <param name="Folder">Where the files were written, or null if they could not be.</param>
public sealed record BenchmarkResult(
    string App, DateTime Started, TimeSpan Duration, int Frames,
    double AverageFps, double OnePercentLow, double PointOnePercentLow,
    IReadOnlyList<BenchmarkStat> Stats, string? Folder);

/// <summary>
/// Records a benchmark run: every frame time of the app being measured, plus a once-a-second log of every
/// hardware reading. When stopped it works out the summary and writes three files next to each other:
/// the readings (.csv), the frame times (frames.csv) and a readable summary (.txt).
/// </summary>
public sealed class BenchmarkRecorder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Stopwatch _clock = new();
    private readonly List<string> _columns = [];
    private readonly Dictionary<string, (string Name, string Unit)> _about = [];
    private readonly Dictionary<string, double> _latest = [];
    private readonly List<(double Seconds, Dictionary<string, double> Values)> _rows = [];
    private FpsProvider? _fps;

    public bool IsRecording { get; private set; }
    public DateTime Started { get; private set; }
    public TimeSpan Elapsed => _clock.Elapsed;
    public int Samples => _rows.Count;
    public BenchmarkResult? Last { get; private set; }

    /// <param name="fps">The frame counter, if the PC allows one. Without it only hardware readings are recorded.</param>
    public void Start(FpsProvider? fps)
    {
        _columns.Clear();
        _about.Clear();
        _latest.Clear();
        _rows.Clear();
        _fps = fps;
        _fps?.BeginCapture();
        Started = DateTime.Now;
        _clock.Restart();
        IsRecording = true;
    }

    /// <summary>Notes the newest value of every numeric reading in a section. Called as sections refresh.</summary>
    public void Note(string sectionId, string sectionTitle, MetricSnapshot snapshot)
    {
        if (!IsRecording || snapshot.IsError) return;
        foreach (var item in snapshot.Items)
        {
            if (item.Number is not { } number || !double.IsFinite(number)) continue;
            var key = $"{sectionId}.{item.Id}";
            if (!_about.ContainsKey(key))
            {
                _columns.Add(key);
                _about[key] = ($"{sectionTitle}: {item.Label}", item.Unit ?? "");
            }
            _latest[key] = number;
        }
    }

    /// <summary>Adds one line to the log from the newest values. Called once a second while recording.</summary>
    public void Sample()
    {
        if (IsRecording && _latest.Count > 0) _rows.Add((_clock.Elapsed.TotalSeconds, new Dictionary<string, double>(_latest)));
    }

    /// <summary>Ends the recording, works out the summary and writes the files.</summary>
    public async Task<BenchmarkResult> StopAsync()
    {
        IsRecording = false;
        _clock.Stop();
        var duration = _clock.Elapsed;
        var capture = _fps?.EndCapture() ?? new FpsProvider.Capture(null, []);
        _fps = null;

        var columns = _columns.ToList();
        var about = new Dictionary<string, (string Name, string Unit)>(_about);
        var rows = _rows.ToList();
        var started = Started;

        var result = await Task.Run(() =>
        {
            var frames = capture.FrameTimesMs;
            var totalMs = frames.Sum();
            var stats = columns
                .Select(key => (key, values: rows.Where(r => r.Values.ContainsKey(key)).Select(r => r.Values[key]).ToList()))
                .Where(c => c.values.Count > 0)
                .Select(c => new BenchmarkStat(c.key, about[c.key].Name, about[c.key].Unit, c.values.Min(), c.values.Average(), c.values.Max()))
                .ToList();

            var summary = new BenchmarkResult(
                capture.App ?? "No app measured", started, duration, frames.Length,
                totalMs > 0 ? frames.Length * 1000 / totalMs : 0,
                FrameStats.LowFps(frames, 0.01, 1000), FrameStats.LowFps(frames, 0.001, 1000),
                stats, null);

            return summary with { Folder = Write(summary, columns, about, rows, frames) };
        });

        Last = result;
        return result;
    }

    private static string? Write(BenchmarkResult result, List<string> columns, Dictionary<string, (string Name, string Unit)> about,
        List<(double Seconds, Dictionary<string, double> Values)> rows, double[] frames)
    {
        try
        {
            var folder = AppPaths.Benchmarks;
            Directory.CreateDirectory(folder);
            var safeApp = string.Concat(result.App.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var stem = Path.Combine(folder, $"{result.Started:yyyy-MM-dd HH.mm.ss} {safeApp}");

            // Readings: one line a second, one column per reading.
            var log = new StringBuilder();
            log.Append("seconds");
            foreach (var key in columns) log.Append(',').Append(Csv(about[key].Unit.Length > 0 ? $"{about[key].Name} ({about[key].Unit})" : about[key].Name));
            log.AppendLine();
            foreach (var (seconds, values) in rows)
            {
                log.Append(seconds.ToString("0.0", Inv));
                foreach (var key in columns) log.Append(',').Append(values.TryGetValue(key, out var v) ? v.ToString("0.###", Inv) : "");
                log.AppendLine();
            }
            File.WriteAllText(stem + ".csv", log.ToString());

            if (frames.Length > 0)
            {
                var frameLog = new StringBuilder("frame time (ms)" + Environment.NewLine);
                foreach (var frame in frames) frameLog.AppendLine(frame.ToString("0.###", Inv));
                File.WriteAllText(stem + " frames.csv", frameLog.ToString());
            }

            File.WriteAllText(stem + ".txt", Describe(result));
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Could not save the benchmark: " + ex.Message);
            return null;
        }
    }

    private static string Describe(BenchmarkResult result)
    {
        var text = new StringBuilder();
        text.AppendLine("Glance benchmark");
        text.AppendLine();
        text.AppendLine($"App        {result.App}");
        text.AppendLine($"Started    {result.Started:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"Length     {TimeText.Clock(result.Duration)}");
        text.AppendLine($"PC         {Machine.CpuName}, {string.Join(" + ", Machine.Gpus.Select(g => Machine.ShortGpuName(g.Name)))}");
        text.AppendLine($"Display    {Machine.MainDisplay()}");
        text.AppendLine();
        if (result.Frames > 0)
        {
            text.AppendLine($"Frames     {result.Frames.ToString("N0", Inv)}");
            text.AppendLine($"Average    {result.AverageFps.ToString("0.0", Inv)} fps");
            text.AppendLine($"1% low     {result.OnePercentLow.ToString("0.0", Inv)} fps");
            text.AppendLine($"0.1% low   {result.PointOnePercentLow.ToString("0.0", Inv)} fps");
        }
        else
        {
            text.AppendLine("No frames were counted, so there are no frame rate figures.");
        }
        text.AppendLine();
        text.AppendLine($"{"Reading",-30}{"Lowest",10}{"Average",10}{"Highest",10}");
        foreach (var stat in result.Stats)
        {
            var name = stat.Unit.Length > 0 ? $"{stat.Name} ({stat.Unit})" : stat.Name;
            text.AppendLine($"{name,-30}{stat.Lowest.ToString("0.#", Inv),10}{stat.Average.ToString("0.#", Inv),10}{stat.Highest.ToString("0.#", Inv),10}");
        }
        return text.ToString();
    }

    private static string Csv(string value) => value.Contains(',') || value.Contains('"') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}

/// <summary>The widget's benchmark section: the timer while recording, and the last result afterwards.</summary>
public sealed class BenchmarkProvider(BenchmarkRecorder recorder, Func<string> shortcut) : IMetricProvider
{
    public string Title => "Benchmark";
    public string Id => "benchmark";
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(1);
    public TimeSpan FreshFor => TimeSpan.Zero;

    public Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        var keys = shortcut();
        var how = keys.Length > 0 ? keys : "the menu";

        if (recorder.IsRecording)
        {
            return Task.FromResult(new MetricSnapshot
            {
                Subtitle = "recording",
                Notice = $"Press {how} again to stop.",
                Items =
                [
                    new MetricItem("Time") { Key = "time", ValueText = TimeText.Clock(recorder.Elapsed), Level = MetricLevel.Critical },
                    new MetricItem("Samples") { Key = "samples", ValueText = recorder.Samples.ToString(CultureInfo.InvariantCulture) },
                ],
            });
        }

        if (recorder.Last is not { } last)
            return Task.FromResult(MetricSnapshot.Info($"Press {how} to start recording, and again to stop."));

        var items = new List<MetricItem>();
        if (last.Frames > 0)
        {
            items.Add(new MetricItem("Average") { Key = "average", ValueText = Format.Whole(last.AverageFps) + " fps" });
            items.Add(new MetricItem("1% low") { Key = "low1", ValueText = Format.Whole(last.OnePercentLow) + " fps" });
            items.Add(new MetricItem("0.1% low") { Key = "low01", ValueText = Format.Whole(last.PointOnePercentLow) + " fps" });
        }
        items.Add(new MetricItem("Length") { Key = "length", ValueText = TimeText.Clock(last.Duration) });

        // The peaks people look for after a run.
        foreach (var (key, label) in new[] { ("gpu.temp", "GPU peak"), ("cpu.temp", "CPU peak"), ("gpu.power", "GPU power") })
        {
            if (last.Stats.FirstOrDefault(s => s.Key == key) is { } stat)
                items.Add(new MetricItem(label) { Key = MetricItem.Slug(label), ValueText = Format.WithUnit(stat.Highest, stat.Unit) });
        }

        return Task.FromResult(new MetricSnapshot
        {
            Items = items,
            Subtitle = last.App,
            Notice = last.Folder is null ? "The files could not be saved. See glance.log." : null,
        });
    }
}
