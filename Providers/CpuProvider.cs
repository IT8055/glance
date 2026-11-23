using System.Globalization;
using Glance.Core;
using Glance.Interop;

namespace Glance.Providers;

/// <summary>
/// Processor load, the busiest core and the effective clock speed, from Windows performance counters.
/// Temperature and power are added when HWiNFO or MSI Afterburner is running, since Windows has no
/// driver-free way to read them.
/// </summary>
public sealed class CpuProvider : IMetricProvider, IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly IntPtr _load;
    private readonly IntPtr _cores;
    private readonly IntPtr _performance;
    private readonly IntPtr _frequency;

    public CpuProvider(TimeSpan interval)
    {
        RefreshInterval = interval;

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
    }

    public string Title => "CPU";
    public TimeSpan RefreshInterval { get; }

    public async Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        await _query.SampleAsync(ct);

        if (_query.Read(_load) is not { } load)
            return MetricSnapshot.Error("Processor counters are unavailable on this PC.", TimeSpan.FromSeconds(30));
        load = Math.Clamp(load, 0, 100);

        var items = new List<MetricItem>
        {
            new("Load") { Percent = load, ValueText = Format.Percent(load) },
        };

        // Instances look like "0,5" (group, core), plus "0,_Total" and "_Total" which are not cores.
        var cores = _query.ReadAll(_cores).Where(c => !c.Instance.Contains("_Total", StringComparison.Ordinal)).ToList();
        if (cores.Count > 0)
            items.Add(new MetricItem("Busiest core") { ValueText = Format.Percent(Math.Clamp(cores.Max(c => c.Value), 0, 100)) });

        // Nominal frequency scaled by "% Processor Performance" gives the live speed, turbo included.
        if (_query.Read(_frequency) is { } mhz and > 0 && _query.Read(_performance) is { } performance and > 0)
            items.Add(new MetricItem("Clock") { ValueText = (mhz * performance / 100 / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " GHz" });

        var sensors = ExternalSensors.Current;
        if (sensors.CpuTemperature is { } temperature)
        {
            items.Add(new MetricItem("Temp")
            {
                ValueText = Format.Number(Math.Round(temperature)) + " °C",
                Level = temperature >= 95 ? MetricLevel.Critical : temperature >= 85 ? MetricLevel.Warning : MetricLevel.Normal,
            });
        }
        if (sensors.CpuPower is { } power)
            items.Add(new MetricItem("Power") { ValueText = Format.Number(Math.Round(power)) + " W" });

        return new MetricSnapshot { Items = items, Subtitle = Machine.CpuName };
    }

    public void Dispose() => _query.Dispose();
}
