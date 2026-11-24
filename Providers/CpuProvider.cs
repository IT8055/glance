using System.Globalization;
using Glance.Core;
using Glance.Interop;

namespace Glance.Providers;

/// <summary>
/// Processor load, a bar per thread, and the effective clock speed, from Windows performance counters.
/// Power comes from Windows' energy meter on PCs that have one. Temperature (and power elsewhere) is
/// added when HWiNFO or MSI Afterburner is running, since Windows has no driver-free way to read it.
/// </summary>
public sealed class CpuProvider : IMetricProvider, IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly bool _threadBars;
    private readonly IntPtr _load;
    private readonly IntPtr _cores;
    private readonly IntPtr _performance;
    private readonly IntPtr _frequency;
    private readonly IntPtr _meters;

    /// <param name="threadBars">Show a small bar for every thread instead of just naming the busiest.</param>
    public CpuProvider(TimeSpan interval, bool threadBars)
    {
        RefreshInterval = interval;
        _threadBars = threadBars;

        // "% Processor Utility" is the figure Task Manager shows. Older Windows only has "% Processor Time".
        _load = _query.Add(@"\Processor Information(_Total)\% Processor Utility");
        _cores = _query.Add(@"\Processor Information(*)\% Processor Utility");
        if (_load == IntPtr.Zero)
        {
            _load = _query.Add(@"\Processor Information(_Total)\% Processor Time");
            _cores = _query.Add(@"\Processor Information(*)\% Processor Time");
        }
        _performance = _query.Add(@"\Processor Information(_Total)\% Processor Performance");
        _frequency = _query.Add(@"\Processor Information(_Total)\Processor Frequency");
        _meters = _query.Add(PowerProvider.EnergyMeterCounter);
    }

    public string Title => "CPU";
    public string Id => "cpu";
    public TimeSpan RefreshInterval { get; }

    public async Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        await _query.SampleAsync(ct);

        if (_query.Read(_load) is not { } load)
            return MetricSnapshot.Error("Processor counters are unavailable on this PC.", TimeSpan.FromSeconds(30));
        load = Math.Clamp(load, 0, 100);

        var items = new List<MetricItem>
        {
            new("Load") { Key = "load", Percent = load, ValueText = Format.Percent(load), Number = load, Unit = "%", Graph = true, GraphMax = 100 },
        };

        // Instances look like "0,5" (group, thread), plus "0,_Total" and "_Total" which are not threads.
        var threads = _query.ReadAll(_cores)
            .Where(c => !c.Instance.Contains("_Total", StringComparison.Ordinal))
            .OrderBy(c => ThreadOrder(c.Instance))
            .Select(c => Math.Clamp(c.Value, 0, 100))
            .ToList();
        if (threads.Count > 0)
        {
            var busiest = threads.Max();
            items.Add(_threadBars
                ? new MetricItem("Threads") { Key = "threads", ValueText = $"busiest {Format.Percent(busiest)}", Brief = Format.Percent(busiest), Number = busiest, Unit = "%", Bars = threads }
                : new MetricItem("Busiest core") { Key = "threads", ValueText = Format.Percent(busiest), Number = busiest, Unit = "%" });
        }

        // Nominal frequency scaled by "% Processor Performance" gives the live speed, turbo included.
        if (_query.Read(_frequency) is { } mhz and > 0 && _query.Read(_performance) is { } performance and > 0)
        {
            var ghz = mhz * performance / 100 / 1000;
            items.Add(new MetricItem("Clock") { Key = "clock", ValueText = ghz.ToString("0.00", CultureInfo.InvariantCulture) + " GHz", Number = ghz, Unit = "GHz" });
        }

        var sensors = ExternalSensors.Current;
        if (sensors.CpuTemperature is { } temperature)
        {
            items.Add(new MetricItem("Temp")
            {
                Key = "temp", ValueText = Format.Whole(temperature) + " °C", Number = temperature, Unit = "°C", Graph = true,
                Level = temperature >= 95 ? MetricLevel.Critical : temperature >= 85 ? MetricLevel.Warning : MetricLevel.Normal,
            });
        }
        // Windows' own energy meter where the PC has one, otherwise whatever a monitoring tool reports.
        if ((PowerProvider.PackageWatts(_query, _meters) ?? sensors.CpuPower) is { } power)
            items.Add(new MetricItem("Power") { Key = "power", ValueText = Format.Whole(power) + " W", Number = power, Unit = "W", Graph = true });

        return new MetricSnapshot { Items = items, Subtitle = Machine.CpuName };
    }

    public void Dispose() => _query.Dispose();

    // "0,5" sorts as group 0, thread 5, so the bars stay in the same order from one reading to the next.
    private static int ThreadOrder(string instance)
    {
        var parts = instance.Split(',');
        var group = parts.Length > 1 && int.TryParse(parts[0], out var g) ? g : 0;
        return int.TryParse(parts[^1], out var thread) ? group * 1024 + thread : int.MaxValue;
    }
}
