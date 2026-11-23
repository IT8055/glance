using Glance.Core;
using Glance.Interop;

namespace Glance.Providers;

/// <summary>Physical memory in use, plus how much of the commit limit (RAM and page file) is promised to programs.</summary>
public sealed class MemoryProvider(TimeSpan interval) : IMetricProvider
{
    public string Title => "Memory";
    public TimeSpan RefreshInterval => interval;

    public Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        var status = new Native.MEMORYSTATUSEX { dwLength = 64 };
        if (!Native.GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
            return Task.FromResult(MetricSnapshot.Error("Memory figures are unavailable.", TimeSpan.FromSeconds(30)));

        double total = status.ullTotalPhys;
        double free = status.ullAvailPhys;
        var used = total - free;
        var percent = used / total * 100;

        double commitLimit = status.ullTotalPageFile;
        var committed = commitLimit - status.ullAvailPageFile;

        var items = new List<MetricItem>
        {
            new("RAM")
            {
                Percent = percent,
                ValueText = Format.SizePair(used, total),
                Level = percent >= 95 ? MetricLevel.Critical : percent >= 85 ? MetricLevel.Warning : MetricLevel.Normal,
            },
            new("Free") { ValueText = Format.Size(free) },
        };
        if (commitLimit > 0)
        {
            // Committed memory hitting its limit is what actually causes "out of memory", even with RAM free.
            var commitPercent = committed / commitLimit * 100;
            items.Add(new MetricItem("Committed")
            {
                ValueText = Format.Percent(commitPercent),
                Level = commitPercent >= 95 ? MetricLevel.Critical : commitPercent >= 85 ? MetricLevel.Warning : MetricLevel.Normal,
            });
        }

        return Task.FromResult(new MetricSnapshot { Items = items });
    }
}
