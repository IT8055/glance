using Glance.Core;
using Glance.Interop;

namespace Glance.Providers;

/// <summary>What this PC is made of. Handy on a shared screenshot, so the numbers above it have context.</summary>
public sealed class SpecsProvider : IMetricProvider
{
    public string Title => "This PC";

    // None of this changes while the PC is running.
    public TimeSpan RefreshInterval => TimeSpan.FromHours(6);
    public TimeSpan FreshFor => TimeSpan.FromHours(6);

    public Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        var items = new List<MetricItem>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) items.Add(new MetricItem(label) { ValueText = value, FullWidth = true });
        }

        Add("CPU", Machine.CpuName);

        var gpus = Machine.Gpus.ToList();
        for (var i = 0; i < gpus.Count; i++)
            Add(gpus.Count > 1 ? $"GPU {i + 1}" : "GPU", $"{Machine.ShortGpuName(gpus[i].Name)}, {Format.Size(gpus[i].DedicatedMemory)}");

        var memory = new Native.MEMORYSTATUSEX { dwLength = 64 };
        if (Native.GlobalMemoryStatusEx(ref memory)) Add("Memory", Format.Size(memory.ullTotalPhys));

        Add("Display", Machine.MainDisplay());
        Add("System", Machine.WindowsName());

        return Task.FromResult(new MetricSnapshot { Items = items });
    }
}
