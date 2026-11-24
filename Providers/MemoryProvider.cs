using Glance.Core;
using Glance.Interop;

namespace Glance.Providers;

/// <summary>Physical memory in use, plus how much of the commit limit (RAM and page file) is promised to programs.</summary>
public sealed class MemoryProvider(TimeSpan interval) : IMetricProvider
{
    public string Title => "Memory";
    public string Id => "memory";
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
                Key = "ram", Percent = percent, ValueText = Format.SizePair(used, total), Brief = Format.Percent(percent),
                Number = percent, Unit = "%", Graph = true, GraphMax = 100,
                Level = percent >= 95 ? MetricLevel.Critical : percent >= 85 ? MetricLevel.Warning : MetricLevel.Normal,
            },
            new("Free") { Key = "free", ValueText = Format.Size(free), Number = free / (1024.0 * 1024 * 1024), Unit = "GB" },
        };
        if (commitLimit > 0)
        {
            // Committed memory hitting its limit is what actually causes "out of memory", even with RAM free.
            var commitPercent = committed / commitLimit * 100;
            items.Add(new MetricItem("Committed")
            {
                Key = "committed", ValueText = Format.Percent(commitPercent), Number = commitPercent, Unit = "%",
                Level = commitPercent >= 95 ? MetricLevel.Critical : commitPercent >= 85 ? MetricLevel.Warning : MetricLevel.Normal,
            });
        }

        return Task.FromResult(new MetricSnapshot { Items = items });
    }
}

/// <summary>Charge and state of the battery, on PCs that have one.</summary>
public sealed class BatteryProvider : IMetricProvider
{
    public string Title => "Battery";
    public string Id => "battery";
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(15);

    /// <summary>False on a desktop PC, where the section is simply left out.</summary>
    public static bool IsPresent => Native.GetSystemPowerStatus(out var status) && (status.BatteryFlag & 128) == 0 && status.BatteryFlag != 255;

    public Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        if (!Native.GetSystemPowerStatus(out var status) || status.BatteryLifePercent > 100)
            return Task.FromResult(MetricSnapshot.Error("Battery state is unavailable.", TimeSpan.FromMinutes(1)));

        double percent = status.BatteryLifePercent;
        var onMains = status.ACLineStatus == 1;
        var charging = (status.BatteryFlag & 8) != 0;

        var items = new List<MetricItem>
        {
            new("Charge")
            {
                Key = "charge", Percent = percent, ValueText = Format.Percent(percent), Number = percent, Unit = "%",
                // Low only matters when it is running down.
                Level = onMains ? MetricLevel.Normal : percent <= 10 ? MetricLevel.Critical : percent <= 25 ? MetricLevel.Warning : MetricLevel.Normal,
            },
            new("Power") { Key = "source", ValueText = charging ? "Charging" : onMains ? "Plugged in" : "On battery" },
        };
        // Windows reports -1 while plugged in or while it is still working the estimate out.
        if (!onMains && status.BatteryLifeTime is > 0 and var seconds)
        {
            var left = TimeSpan.FromSeconds(seconds);
            items.Add(new MetricItem("Time left") { Key = "left", ValueText = $"{(int)left.TotalHours}h {left.Minutes}m", Number = left.TotalMinutes, Unit = "min" });
        }

        return Task.FromResult(new MetricSnapshot { Items = items });
    }
}
