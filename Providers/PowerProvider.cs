using System.Globalization;
using Glance.Core;
using Glance.Interop;

namespace Glance.Providers;

/// <summary>
/// How much electrical power the PC is using.
///
/// What can really be measured: the processor (Windows' built-in energy meter, or a monitoring tool)
/// and the graphics card (NVIDIA driver, or a monitoring tool). On a laptop running on battery the
/// drain on the battery is also measured, and that is the whole machine.
///
/// What cannot be measured without a hardware meter is the draw at the wall on a desktop. For that the
/// panel gives an estimate: the measured parts, plus an allowance for everything else (motherboard,
/// memory, drives, fans), divided by the power supply's efficiency. Both assumptions are in Settings,
/// and the line is always labelled as an estimate.
/// </summary>
public sealed class PowerProvider : IMetricProvider, IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly PowerConfig _config;
    private readonly IntPtr _meters;
    private readonly IntPtr _battery;
    private readonly List<IntPtr> _nvidia = [];

    public PowerProvider(PowerConfig config, TimeSpan interval)
    {
        _config = config;
        RefreshInterval = interval;
        _meters = _query.Add(EnergyMeterCounter);
        // Only present on PCs with a battery. Reads zero while on mains.
        _battery = _query.Add(@"\Battery Status(*)\Discharge Rate");

        try
        {
            if (Environment.GetEnvironmentVariable("GLANCE_NO_NVML") is null && NvidiaGpuProvider.Initialise() && Nvml.GetCount(out var count) == 0)
            {
                for (uint i = 0; i < count; i++)
                {
                    if (Nvml.GetHandle(i, out var device) == 0) _nvidia.Add(device);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // No NVIDIA driver on this PC.
        }
    }

    /// <summary>Windows' energy meters, in milliwatts. One instance per meter, such as "rapl_package0_pkg".</summary>
    public const string EnergyMeterCounter = @"\Energy Meter(*)\Power";

    public string Title => "Power";
    public string Id => "power";
    public TimeSpan RefreshInterval { get; }

    /// <summary>
    /// Processor power in watts from Windows' energy meter, or null on PCs that do not have one.
    /// "pkg" is the whole processor package, which includes its built-in graphics.
    /// </summary>
    internal static double? PackageWatts(PdhQuery query, IntPtr meters)
    {
        var packages = query.ReadAll(meters).Where(m => m.Instance.EndsWith("_pkg", StringComparison.OrdinalIgnoreCase)).ToList();
        return packages.Count == 0 ? null : packages.Sum(m => m.Value) / 1000;
    }

    public async Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        await _query.SampleAsync(ct);
        var sensors = ExternalSensors.Current;

        var cpu = PackageWatts(_query, _meters) ?? sensors.CpuPower;

        var gpus = new List<double>();
        foreach (var device in _nvidia)
        {
            if (Nvml.GetPowerMilliwatts(device, out var milliwatts) == 0) gpus.Add(milliwatts / 1000.0);
        }
        if (gpus.Count == 0 && sensors.Find("W", "GPU Power", "GPU1 power", "GPU power") is { } fromTool) gpus.Add(fromTool);

        var drain = _query.ReadAll(_battery).Sum(b => b.Value) / 1000;
        var onBattery = drain > 0.5;

        if (cpu is null && gpus.Count == 0 && !onBattery)
        {
            return MetricSnapshot.Error(
                "This PC does not report its power use. HWiNFO or MSI Afterburner can supply it if one is running.", TimeSpan.FromMinutes(1));
        }

        var measured = (cpu ?? 0) + gpus.Sum();
        var items = new List<MetricItem>();

        if (onBattery)
        {
            // The one case where the whole machine is measured: everything it uses comes out of the battery.
            items.Add(Total("Whole PC", drain, "measured from the battery"));
        }
        else
        {
            var efficiency = Math.Clamp(_config.Efficiency, 50, 100) / 100;
            var estimate = (measured + Math.Max(0, _config.RestWatts)) / efficiency;
            items.Add(Total("Whole PC (estimate)", estimate, null));
        }
        var whole = items[0].Number ?? 0;

        if (cpu is { } cpuWatts) items.Add(Part("CPU", "cpu", cpuWatts));
        for (var i = 0; i < gpus.Count; i++)
            items.Add(Part(gpus.Count > 1 ? $"GPU {i + 1}" : "GPU", i == 0 ? "gpu" : $"gpu{i + 1}", gpus[i]));
        if (cpu is not null && gpus.Count > 0)
            items.Add(new MetricItem("CPU + GPU") { Key = "measured", ValueText = Format.Whole(measured) + " W", Number = measured, Unit = "W" });

        if (_config.PricePerKwh > 0)
        {
            var hourly = whole / 1000 * _config.PricePerKwh;
            items.Add(new MetricItem("Cost an hour")
            {
                Key = "cost",
                // Small sums get a third decimal, or an idle PC would always read 0.00.
                ValueText = RegionInfo.CurrentRegion.CurrencySymbol + hourly.ToString(hourly < 0.1 ? "0.000" : "0.00", CultureInfo.InvariantCulture),
                Number = hourly,
            });
        }

        return new MetricSnapshot { Items = items };
    }

    public void Dispose() => _query.Dispose();

    // The headline figure. With the supply's size known it gets a bar showing how loaded the supply is.
    private MetricItem Total(string label, double watts, string? detail)
    {
        double? load = _config.SupplyWatts > 0 ? watts * 100 / _config.SupplyWatts : null;
        return new MetricItem(label)
        {
            Key = "total",
            ValueText = Format.Whole(watts) + " W",
            Number = watts,
            Unit = "W",
            Graph = true,
            FullWidth = true,
            Percent = load is { } percent ? Math.Clamp(percent, 0, 100) : null,
            Detail = load is { } share
                ? $"{Format.Whole(share)}% of the {Format.Whole(_config.SupplyWatts)} W supply" + (detail is null ? "" : ", " + detail)
                : detail,
            Level = load >= 100 ? MetricLevel.Critical : load >= 90 ? MetricLevel.Warning : MetricLevel.Normal,
        };
    }

    private static MetricItem Part(string label, string key, double watts) =>
        new(label) { Key = key, ValueText = Format.Whole(watts) + " W", Number = watts, Unit = "W", Graph = true };
}
