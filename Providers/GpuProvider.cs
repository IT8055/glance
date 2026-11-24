using System.Diagnostics;
using System.Text.RegularExpressions;
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
                            providers.Add(new NvidiaGpuProvider(device, i == 0 ? "gpu" : $"gpu{i + 1}", count > 1 ? $"GPU {i + 1}" : "GPU", interval));
                    }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                // No NVIDIA driver on this PC.
            }
        }

        if (providers.Count == 0 && MainAdapter() is { } gpu) providers.Add(new GenericGpuProvider(gpu, interval));
        return providers;
    }

    /// <summary>The adapter doing the work: the one with the most video memory of its own.</summary>
    internal static Dxgi.Adapter? MainAdapter() => Machine.Gpus.OrderByDescending(a => a.DedicatedMemory).FirstOrDefault();
}

internal sealed class NvidiaGpuProvider(IntPtr device, string id, string title, TimeSpan interval) : IMetricProvider
{
    private static readonly object InitGate = new();
    private static bool? _initialised;

    private readonly string? _name = Nvml.Name(device) is { } name ? Machine.ShortGpuName(name) : null;

    public string Title => title;
    public string Id => id;
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
        {
            items.Add(new MetricItem("Load")
            {
                Key = "load", Percent = Math.Min(100, utilization.Gpu), ValueText = Format.Percent(utilization.Gpu),
                Number = utilization.Gpu, Unit = "%", Graph = true, GraphMax = 100,
            });
        }

        if (Nvml.GetMemory(device, out var memory) == 0 && memory.Total > 0)
        {
            var percent = memory.Used * 100.0 / memory.Total;
            items.Add(new MetricItem("VRAM")
            {
                Key = "vram", Percent = percent, ValueText = Format.SizePair(memory.Used, memory.Total), Brief = Format.Size(memory.Used),
                Number = memory.Used / (1024.0 * 1024 * 1024), Unit = "GB", Graph = true, GraphMax = memory.Total / (1024.0 * 1024 * 1024),
                Level = percent >= 97 ? MetricLevel.Critical : percent >= 90 ? MetricLevel.Warning : MetricLevel.Normal,
            });
        }

        if (Nvml.GetTemperature(device, Nvml.TemperatureGpu, out var celsius) == 0)
        {
            items.Add(new MetricItem("Temp")
            {
                Key = "temp", ValueText = $"{celsius} °C", Number = celsius, Unit = "°C", Graph = true,
                Level = celsius >= 90 ? MetricLevel.Critical : celsius >= 83 ? MetricLevel.Warning : MetricLevel.Normal,
            });
        }
        if (Nvml.GetPowerMilliwatts(device, out var milliwatts) == 0)
        {
            var watts = milliwatts / 1000.0;
            items.Add(new MetricItem("Power") { Key = "power", ValueText = Format.Whole(watts) + " W", Number = watts, Unit = "W", Graph = true });
        }
        if (Nvml.GetFanPercent(device, out var fan) == 0)
            items.Add(new MetricItem("Fan") { Key = "fan", ValueText = $"{fan}%", Number = fan, Unit = "%" });
        if (Nvml.GetClockMhz(device, Nvml.ClockGraphics, out var mhz) == 0)
            items.Add(new MetricItem("Clock") { Key = "clock", ValueText = $"{mhz} MHz", Number = mhz, Unit = "MHz" });

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
    public string Id => "gpu";
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
            .GroupBy(e => GpuCounters.EngineOf(e.Instance))
            .Select(g => g.Sum(e => e.Value))
            .DefaultIfEmpty(0)
            .Max();
        load = Math.Clamp(load, 0, 100);

        var items = new List<MetricItem>
        {
            new("Load") { Key = "load", Percent = load, ValueText = Format.Percent(load), Number = load, Unit = "%", Graph = true, GraphMax = 100 },
        };

        var used = _query.ReadAll(_memory)
            .Where(m => m.Instance.Contains(tag, StringComparison.OrdinalIgnoreCase))
            .Sum(m => m.Value);
        if (_adapter.DedicatedMemory > 0)
        {
            var percent = used * 100.0 / _adapter.DedicatedMemory;
            items.Add(new MetricItem("VRAM")
            {
                Key = "vram", Percent = Math.Clamp(percent, 0, 100), ValueText = Format.SizePair(used, _adapter.DedicatedMemory),
                Brief = Format.Size(used), Number = used / (1024.0 * 1024 * 1024), Unit = "GB",
                Graph = true, GraphMax = _adapter.DedicatedMemory / (1024.0 * 1024 * 1024),
                Level = percent >= 97 ? MetricLevel.Critical : percent >= 90 ? MetricLevel.Warning : MetricLevel.Normal,
            });
        }

        // Temperature and power only exist here if a monitoring tool is sharing them.
        var sensors = ExternalSensors.Current;
        if (sensors.Find("°C", "GPU Temperature", "GPU temperature") is { } temperature)
            items.Add(new MetricItem("Temp") { Key = "temp", ValueText = Format.Whole(temperature) + " °C", Number = temperature, Unit = "°C", Graph = true });
        if (sensors.Find("W", "GPU Power", "GPU power") is { } power)
            items.Add(new MetricItem("Power") { Key = "power", ValueText = Format.Whole(power) + " W", Number = power, Unit = "W", Graph = true });

        return new MetricSnapshot { Items = items, Subtitle = Machine.ShortGpuName(_adapter.Name) };
    }

    public void Dispose() => _query.Dispose();
}

/// <summary>
/// Which programs are using the graphics card: video memory and load per app. Useful when several
/// heavy tools (a renderer, a game engine, an image generator) are competing for the same card.
/// Works on any vendor's card, from the counters behind Task Manager's GPU columns.
/// </summary>
public sealed class GpuAppsProvider : IMetricProvider, IDisposable
{
    private const int Shown = 5;
    private const double WorthListing = 64 * 1024 * 1024; // skip the long tail of apps holding a few megabytes

    private readonly PdhQuery _query = new();
    private readonly IntPtr _memory;
    private readonly IntPtr _engines;
    private readonly string? _tag = GpuProviders.MainAdapter()?.CounterTag;
    private readonly Dictionary<int, string> _names = [];

    public GpuAppsProvider(TimeSpan interval)
    {
        // The per-process counters have hundreds of instances, so this section never updates faster than every 2 seconds.
        RefreshInterval = interval < TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : interval;
        _memory = _query.Add(@"\GPU Process Memory(*)\Dedicated Usage");
        _engines = _query.Add(@"\GPU Engine(*)\Utilization Percentage");
    }

    public string Title => "GPU by app";
    public string Id => "gpuapps";
    public TimeSpan RefreshInterval { get; }

    public async Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        if (_memory == IntPtr.Zero || _tag is null)
            return MetricSnapshot.Error("This version of Windows does not report GPU use per app.", TimeSpan.FromMinutes(5));

        await _query.SampleAsync(ct);

        var memory = _query.ReadAll(_memory)
            .Where(m => m.Instance.Contains(_tag, StringComparison.OrdinalIgnoreCase))
            .GroupBy(m => GpuCounters.ProcessOf(m.Instance))
            .ToDictionary(g => g.Key, g => g.Sum(m => m.Value));

        // Per app: add up its share of each engine, then take its busiest engine.
        var load = _query.ReadAll(_engines)
            .Where(e => e.Instance.Contains(_tag, StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => GpuCounters.ProcessOf(e.Instance))
            .ToDictionary(g => g.Key, g => g.GroupBy(e => GpuCounters.EngineOf(e.Instance)).Max(engine => engine.Sum(e => e.Value)));

        var top = memory.Where(m => m.Key > 0 && m.Value >= WorthListing)
            .OrderByDescending(m => m.Value)
            .Take(Shown)
            .ToList();
        if (top.Count == 0) return MetricSnapshot.Info("No app is using much video memory.");

        var items = new List<MetricItem>();
        foreach (var (processId, bytes) in top)
        {
            var percent = Math.Clamp(load.GetValueOrDefault(processId), 0, 100);
            items.Add(new MetricItem(NameOf(processId))
            {
                // Named by process id: two copies of the same program are different rows.
                Key = "pid" + processId,
                ValueText = percent >= 1 ? $"{Format.Size(bytes)} · {Format.Percent(percent)}" : Format.Size(bytes),
                FullWidth = true,
            });
        }
        return new MetricSnapshot { Items = items };
    }

    public void Dispose() => _query.Dispose();

    private string NameOf(int processId)
    {
        if (_names.TryGetValue(processId, out var known)) return known;
        if (_names.Count > 500) _names.Clear(); // process ids come and go all day
        try
        {
            using var process = Process.GetProcessById(processId);
            return _names[processId] = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return $"pid {processId}";
        }
    }
}

/// <summary>Reads the instance names of the Windows GPU counters.</summary>
internal static partial class GpuCounters
{
    /// <summary>"pid_1234_luid_..._phys_0_eng_3_engtype_3D" gives "_eng_3".</summary>
    public static string EngineOf(string instance)
    {
        var start = instance.IndexOf("_eng_", StringComparison.Ordinal);
        if (start < 0) return instance;
        var end = instance.IndexOf("_engtype", start, StringComparison.Ordinal);
        return end < 0 ? instance[start..] : instance[start..end];
    }

    /// <summary>"pid_1234_luid_..." gives 1234.</summary>
    public static int ProcessOf(string instance)
    {
        var match = ProcessId().Match(instance);
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : 0;
    }

    [GeneratedRegex(@"pid_(\d+)_")]
    private static partial Regex ProcessId();
}
