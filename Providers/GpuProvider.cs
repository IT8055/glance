using Glance.Core;
using Glance.Interop;

namespace Glance.Providers;

/// <summary>
/// Picks the best GPU source for the machine it is running on:
/// NVIDIA cards get the full set (load, memory, temperature, power, fan, clock) from the driver's own library.
/// Anything else (AMD, Intel) gets load and memory from Windows performance counters, which every vendor supports.
/// </summary>
public static class GpuProviders
{
    public static IReadOnlyList<IMetricProvider> Detect(TimeSpan interval)
    {
        var providers = new List<IMetricProvider>();

        // GLANCE_NO_NVML forces the generic path, for testing it on an NVIDIA machine.
        if (Environment.GetEnvironmentVariable("GLANCE_NO_NVML") is null)
        {
            try
            {
                if (NvidiaGpuProvider.Initialise() && Nvml.GetCount(out var count) == 0)
                {
                    for (uint i = 0; i < count; i++)
                    {
                        if (Nvml.GetHandle(i, out var device) == 0)
                            providers.Add(new NvidiaGpuProvider(device, count > 1 ? $"GPU {i + 1}" : "GPU", interval));
                    }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                // No NVIDIA driver on this PC.
            }
        }

        if (providers.Count == 0)
        {
            // The adapter with the most video memory of its own is the one doing the work.
            var gpu = Machine.Gpus.OrderByDescending(a => a.DedicatedMemory).FirstOrDefault();
            if (gpu is not null) providers.Add(new GenericGpuProvider(gpu, interval));
        }

        return providers;
    }
}

internal sealed class NvidiaGpuProvider(IntPtr device, string title, TimeSpan interval) : IMetricProvider
{
    private static readonly object InitGate = new();
    private static bool? _initialised;

    private readonly string? _name = Nvml.Name(device) is { } name ? Machine.ShortGpuName(name) : null;

    public string Title => title;
    public TimeSpan RefreshInterval => interval;

    /// <summary>Loads the NVIDIA library once per run. It stays loaded: device handles are only valid while it is.</summary>
    public static bool Initialise()
    {
        lock (InitGate)
        {
            return _initialised ??= Nvml.Init() == 0;
        }
    }

    public Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        var items = new List<MetricItem>();

        if (Nvml.GetUtilization(device, out var utilization) == 0)
            items.Add(new MetricItem("Load") { Percent = Math.Min(100, utilization.Gpu), ValueText = Format.Percent(utilization.Gpu) });

        if (Nvml.GetMemory(device, out var memory) == 0 && memory.Total > 0)
        {
            var percent = memory.Used * 100.0 / memory.Total;
            items.Add(new MetricItem("VRAM")
            {
                Percent = percent,
                ValueText = Format.SizePair(memory.Used, memory.Total),
                Level = percent >= 97 ? MetricLevel.Critical : percent >= 90 ? MetricLevel.Warning : MetricLevel.Normal,
            });
        }

        if (Nvml.GetTemperature(device, Nvml.TemperatureGpu, out var celsius) == 0)
        {
            items.Add(new MetricItem("Temp")
            {
                ValueText = $"{celsius} °C",
                Level = celsius >= 90 ? MetricLevel.Critical : celsius >= 83 ? MetricLevel.Warning : MetricLevel.Normal,
            });
        }
        if (Nvml.GetPowerMilliwatts(device, out var milliwatts) == 0)
            items.Add(new MetricItem("Power") { ValueText = Format.Number(Math.Round(milliwatts / 1000.0)) + " W" });
        if (Nvml.GetFanPercent(device, out var fan) == 0)
            items.Add(new MetricItem("Fan") { ValueText = $"{fan}%" });
        if (Nvml.GetClockMhz(device, Nvml.ClockGraphics, out var mhz) == 0)
            items.Add(new MetricItem("Clock") { ValueText = $"{mhz} MHz" });

        return Task.FromResult(items.Count == 0
            ? MetricSnapshot.Error("The NVIDIA driver is not answering.", TimeSpan.FromSeconds(30))
            : new MetricSnapshot { Items = items, Subtitle = _name });
    }
}

/// <summary>Vendor-neutral GPU readings, the same ones Task Manager's GPU page shows.</summary>
internal sealed class GenericGpuProvider : IMetricProvider, IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly Dxgi.Adapter _adapter;
    private readonly IntPtr _engines;
    private readonly IntPtr _memory;

    public GenericGpuProvider(Dxgi.Adapter adapter, TimeSpan interval)
    {
        _adapter = adapter;
        RefreshInterval = interval;
        _engines = _query.Add(@"\GPU Engine(*)\Utilization Percentage");
        _memory = _query.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
    }

    public string Title => "GPU";
    public TimeSpan RefreshInterval { get; }

    public async Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        if (_engines == IntPtr.Zero)
            return MetricSnapshot.Error("This version of Windows does not report GPU activity.", TimeSpan.FromMinutes(5));

        await _query.SampleAsync(ct);
        var tag = _adapter.CounterTag;

        // One instance per process per engine: "pid_1234_luid_0x..._0x..._phys_0_eng_3_engtype_3D".
        // Add up the processes on each engine, then take the busiest engine, as Task Manager does.
        var load = _query.ReadAll(_engines)
            .Where(e => e.Instance.Contains(tag, StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => EngineOf(e.Instance))
            .Select(g => g.Sum(e => e.Value))
            .DefaultIfEmpty(0)
            .Max();
        load = Math.Clamp(load, 0, 100);

        var items = new List<MetricItem>
        {
            new("Load") { Percent = load, ValueText = Format.Percent(load) },
        };

        var used = _query.ReadAll(_memory)
            .Where(m => m.Instance.Contains(tag, StringComparison.OrdinalIgnoreCase))
            .Sum(m => m.Value);
        if (_adapter.DedicatedMemory > 0)
        {
            var percent = used * 100.0 / _adapter.DedicatedMemory;
            items.Add(new MetricItem("VRAM")
            {
                Percent = Math.Clamp(percent, 0, 100),
                ValueText = Format.SizePair(used, _adapter.DedicatedMemory),
                Level = percent >= 97 ? MetricLevel.Critical : percent >= 90 ? MetricLevel.Warning : MetricLevel.Normal,
            });
        }

        // Temperature and power only exist here if a monitoring tool is sharing them.
        var sensors = ExternalSensors.Current;
        if (sensors.Find("°C", "GPU Temperature", "GPU temperature") is { } temperature)
            items.Add(new MetricItem("Temp") { ValueText = Format.Number(Math.Round(temperature)) + " °C" });
        if (sensors.Find("W", "GPU Power", "GPU power") is { } power)
            items.Add(new MetricItem("Power") { ValueText = Format.Number(Math.Round(power)) + " W" });

        return new MetricSnapshot { Items = items, Subtitle = Machine.ShortGpuName(_adapter.Name) };
    }

    public void Dispose() => _query.Dispose();

    private static string EngineOf(string instance)
    {
        var start = instance.IndexOf("_eng_", StringComparison.Ordinal);
        if (start < 0) return instance;
        var end = instance.IndexOf("_engtype", start, StringComparison.Ordinal);
        return end < 0 ? instance[start..] : instance[start..end];
    }
}
