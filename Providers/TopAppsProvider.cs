using System.Text.RegularExpressions;
using Glance.Core;
using Glance.Interop;

namespace Glance.Providers;

/// <summary>
/// The five programs using the most of something: processor, memory, or disk and network traffic.
/// From the same per-process counters Task Manager reads, so it needs no special rights.
/// Several processes of one program (a browser's many) are added together, as Task Manager groups them.
/// </summary>
public sealed partial class TopAppsProvider : IMetricProvider, IDisposable
{
    public enum Kind { Cpu, Memory, Io }

    private const int Shown = 5;

    private readonly PdhQuery _query = new();
    private readonly Kind _kind;
    private readonly IntPtr _counter;

    public TopAppsProvider(Kind kind, TimeSpan interval)
    {
        _kind = kind;
        // Hundreds of processes are read each time, so this never updates faster than every 2 seconds.
        RefreshInterval = interval < TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : interval;
        _counter = _query.Add(kind switch
        {
            Kind.Cpu => @"\Process(*)\% Processor Time",
            // What Task Manager's Memory column shows: memory that belongs to the program alone.
            Kind.Memory => @"\Process(*)\Working Set - Private",
            // Bytes read and written a second across files, network and devices together.
            _ => @"\Process(*)\IO Data Bytes/sec",
        });
    }

    public string Title => _kind switch { Kind.Cpu => "CPU by app", Kind.Memory => "Memory by app", _ => "I/O by app" };
    public string Id => _kind switch { Kind.Cpu => "cpuapps", Kind.Memory => "memoryapps", _ => "ioapps" };
    public TimeSpan RefreshInterval { get; }

    public async Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        if (_counter == IntPtr.Zero)
            return MetricSnapshot.Error("Per-program counters are unavailable on this PC.", TimeSpan.FromMinutes(5));

        await _query.SampleAsync(ct);

        // Instances are the program name, with "#1", "#2" added for further copies of the same program.
        var apps = _query.ReadAll(_counter)
            .Where(p => p.Instance is not ("_Total" or "Idle") && p.Value > 0)
            .GroupBy(p => CopyNumber().Replace(p.Instance, ""), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Value: g.Sum(p => p.Value)))
            .OrderByDescending(a => a.Value)
            .Take(Shown)
            .ToList();

        var items = new List<MetricItem>();
        foreach (var (name, value) in apps)
        {
            var text = _kind switch
            {
                // The counter is a percentage of one thread. Dividing by the thread count gives the share of the whole processor.
                Kind.Cpu => Share(value / Environment.ProcessorCount),
                Kind.Memory => Format.Size(value),
                _ => Format.ByteRate(value),
            };
            if (text is null) continue;
            items.Add(new MetricItem(name) { Key = MetricItem.Slug(name), ValueText = text, FullWidth = true });
        }

        return items.Count == 0
            ? MetricSnapshot.Info(_kind == Kind.Io ? "No program is reading or writing much." : "Nothing is busy right now.")
            : new MetricSnapshot { Items = items };
    }

    public void Dispose() => _query.Dispose();

    // Below a tenth of a percent a program is not worth a line.
    private static string? Share(double percent) =>
        percent < 0.05 ? null : percent < 10 ? percent.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%" : Format.Percent(percent);

    [GeneratedRegex(@"#\d+$")]
    private static partial Regex CopyNumber();
}
