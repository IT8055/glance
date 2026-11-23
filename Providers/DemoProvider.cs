using Glance.Core;

namespace Glance.Providers;

/// <summary>
/// Made-up numbers for previewing the widget with "Glance.exe --demo". Touches no account, no network
/// and no hardware, so the layout can be checked anywhere.
/// </summary>
public sealed class DemoProvider(string title, string? subtitle, Func<DateTimeOffset, IReadOnlyList<MetricItem>> items) : IMetricProvider
{
    public string Title => title;

    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(5);

    public Task<MetricSnapshot> FetchAsync(CancellationToken ct) =>
        Task.FromResult(new MetricSnapshot { Items = items(DateTimeOffset.Now), Subtitle = subtitle });

    public static IEnumerable<IMetricProvider> All() =>
    [
        new DemoProvider("Claude", "demo", now =>
        [
            new MetricItem("Current session") { Percent = 42, ValueText = "42%", ResetsAt = now.AddMinutes(134) },
            new MetricItem("Week, all models") { Percent = 78, ValueText = "78%", ResetsAt = now.AddDays(3).AddHours(2), Level = MetricLevel.Warning },
            new MetricItem("Week, Fable") { Percent = 93, ValueText = "93%", ResetsAt = now.AddDays(3).AddHours(2), Level = MetricLevel.Critical },
        ]),
        new DemoProvider("FPS", "demo game", _ =>
        [
            new MetricItem("Now") { ValueText = "142" },
            new MetricItem("Frame time") { ValueText = "7.0 ms" },
            new MetricItem("Average") { ValueText = "138" },
            new MetricItem("1% low") { ValueText = "97" },
        ]),
        new DemoProvider("GPU", "Demo GPU 16 GB", _ =>
        [
            new MetricItem("Load") { Percent = 97, ValueText = "97%" },
            new MetricItem("VRAM") { Percent = 92, ValueText = "14.7 / 16 GB", Level = MetricLevel.Warning },
            new MetricItem("Temp") { ValueText = "84 °C", Level = MetricLevel.Warning },
            new MetricItem("Power") { ValueText = "312 W" },
            new MetricItem("Fan") { ValueText = "68%" },
            new MetricItem("Clock") { ValueText = "2610 MHz" },
        ]),
        new DemoProvider("CPU", "Demo CPU 16-core", _ =>
        [
            new MetricItem("Load") { Percent = 31, ValueText = "31%" },
            new MetricItem("Busiest core") { ValueText = "88%" },
            new MetricItem("Clock") { ValueText = "5.21 GHz" },
        ]),
        new DemoProvider("Drives", null, _ =>
        [
            new MetricItem("C: System") { Percent = 61, ValueText = "720 GB free" },
            new MetricItem("D: Games") { Percent = 98, ValueText = "71 GB free", Level = MetricLevel.Critical },
        ]),
        new DemoProvider("Network", null, _ =>
        [
            new MetricItem("Web down") { ValueText = "86 Mbps" },
            new MetricItem("Web up") { ValueText = "4.2 Mbps" },
            new MetricItem("LAN down") { ValueText = "0 kbps" },
            new MetricItem("LAN up") { ValueText = "310 kbps" },
        ]),
        new DemoProvider("This PC", null, _ =>
        [
            new MetricItem("CPU") { ValueText = "Demo CPU 16-core", FullWidth = true },
            new MetricItem("Display") { ValueText = "3840 x 2160, 144 Hz", FullWidth = true },
        ]),
    ];
}
