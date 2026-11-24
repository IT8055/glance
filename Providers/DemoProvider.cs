using System.Globalization;
using Glance.Core;

namespace Glance.Providers;

/// <summary>
/// Made-up numbers for previewing the widget with "Glance.exe --demo", and for the pictures in the README.
/// Touches no account, no network and no hardware, so the layout can be checked anywhere. The figures drift
/// about so that graphs and lists behave as they would on a busy PC, and use the same names as the real
/// sections so the compact strip works too.
/// </summary>
public sealed class DemoProvider(string id, string title, int reservedRows, Func<double, MetricSnapshot> read) : IMetricProvider
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public string Title => title;
    public string Id => id;
    public int ReservedRows => reservedRows;
    public TimeSpan RefreshInterval => TimeSpan.FromMilliseconds(500);

    public Task<MetricSnapshot> FetchAsync(CancellationToken ct) => Task.FromResult(read((DateTime.UtcNow - Start).TotalSeconds));

    public static IEnumerable<IMetricProvider> All() =>
    [
        new DemoProvider("claude", "Claude", 0, _ =>
        {
            var now = DateTimeOffset.Now;
            return new MetricSnapshot
            {
                Items =
                [
                    Limit("Current session", "session", 42, now.AddMinutes(134), "on pace for 76%"),
                    Limit("Week, all models", "weekly_all", 58, now.AddDays(3).AddHours(2), "on pace for 91%"),
                    Limit("Week, Fable", "weekly_fable", 83, now.AddDays(3).AddHours(2), "runs out " + now.AddDays(1).ToString("ddd") + " 18:40", MetricLevel.Warning),
                    new MetricItem("From Code") { Key = "from_code", ValueText = "71%" },
                    new MetricItem("From Cowork") { Key = "from_cowork", ValueText = "22%" },
                ],
            };
        }),
        new DemoProvider("fps", "FPS", 0, t =>
        {
            var fps = Fps(t);
            return new MetricSnapshot
            {
                Subtitle = "nebula-drift",
                Items =
                [
                    Graphed("Now", "now", fps, "fps", Format.Whole(fps)),
                    Plain("Frame time", "frametime", 1000 / fps, "ms", (1000 / fps).ToString("0.0", CultureInfo.InvariantCulture) + " ms"),
                    Plain("Average", "average", 139, "fps", Format.Whole(Between(t, 11, 137, 141))),
                    Plain("1% low", "low1", 104, "fps", Format.Whole(Between(t, 12, 101, 107))),
                ],
            };
        }),
        new DemoProvider("gpu", "GPU", 0, t =>
        {
            var (load, temp, watts, vram) = (Between(t, 1, 91, 99), GpuTemp(t), GpuWatts(t), Between(t, 4, 11.2, 12.1));
            return new MetricSnapshot
            {
                Subtitle = "16 GB graphics card",
                Items =
                [
                    Graphed("Load", "load", load, "%", Format.Percent(load), percent: load, max: 100),
                    Graphed("VRAM", "vram", vram, "GB", $"{Format.Number(vram)} / 16 GB", percent: vram / 16 * 100, max: 16),
                    Graphed("Temp", "temp", temp, "°C", Format.Whole(temp) + " °C"),
                    Graphed("Power", "power", watts, "W", Format.Whole(watts) + " W"),
                    Plain("Fan", "fan", 52, "%", Format.Percent(Between(t, 5, 49, 55))),
                    Plain("Clock", "clock", 2740, "MHz", Format.Whole(Between(t, 6, 2715, 2760)) + " MHz"),
                ],
            };
        }),
        new DemoProvider("gpuapps", "GPU by app", 5, t => Apps(
            ("nebula-drift", $"{Format.Number(Between(t, 4, 9.1, 9.8))} GB · {Format.Whole(Between(t, 1, 88, 95))}%"),
            ("render-studio", "1.4 GB"), ("browser", "612 MB · 2%"), ("desktop", "448 MB · 1%"), ("recorder", "310 MB · 4%"))),
        new DemoProvider("cpu", "CPU", 0, t =>
        {
            var threads = Enumerable.Range(0, 16).Select(i => Math.Clamp(Between(t, 100 + i, -8, 62) + (i is 2 or 5 or 11 ? 26 : 0), 0, 100)).ToList();
            var (load, temp, watts, busiest) = (threads.Average(), Between(t, 8, 58, 66), CpuWatts(t), threads.Max());
            return new MetricSnapshot
            {
                Subtitle = "8 cores, 16 threads",
                Items =
                [
                    Graphed("Load", "load", load, "%", Format.Percent(load), percent: load, max: 100),
                    Plain("Clock", "clock", 5.0, "GHz", Between(t, 9, 4.88, 5.12).ToString("0.00", CultureInfo.InvariantCulture) + " GHz"),
                    Graphed("Temp", "temp", temp, "°C", Format.Whole(temp) + " °C"),
                    Graphed("Power", "power", watts, "W", Format.Whole(watts) + " W"),
                    new MetricItem("Threads") { Key = "threads", ValueText = "busiest " + Format.Percent(busiest), Brief = Format.Percent(busiest), Number = busiest, Unit = "%", Bars = threads },
                ],
            };
        }),
        new DemoProvider("cpuapps", "CPU by app", 5, t => Apps(
            ("nebula-drift", Format.Percent(Between(t, 20, 17, 24))), ("render-studio", Tenths(Between(t, 21, 5.2, 7.9)) + "%"),
            ("browser", Tenths(Between(t, 22, 2.1, 3.9)) + "%"), ("recorder", Tenths(Between(t, 23, 1.1, 1.8)) + "%"), ("music", Tenths(Between(t, 24, 0.3, 0.7)) + "%"))),
        new DemoProvider("power", "Power", 0, t =>
        {
            var (cpu, gpu) = (CpuWatts(t), GpuWatts(t));
            var whole = (cpu + gpu + 50) / 0.9;
            return new MetricSnapshot
            {
                Items =
                [
                    Graphed("Whole PC (estimate)", "total", whole, "W", Format.Whole(whole) + " W", percent: whole / 8.5) with { Detail = Format.Whole(whole / 8.5) + "% of the 850 W supply", FullWidth = true },
                    Graphed("CPU", "cpu", cpu, "W", Format.Whole(cpu) + " W"),
                    Graphed("GPU", "gpu", gpu, "W", Format.Whole(gpu) + " W"),
                    Plain("CPU + GPU", "measured", cpu + gpu, "W", Format.Whole(cpu + gpu) + " W"),
                    Plain("Cost an hour", "cost", whole / 1000 * 0.27, "", "£" + (whole / 1000 * 0.27).ToString("0.00", CultureInfo.InvariantCulture)),
                ],
            };
        }),
        new DemoProvider("memory", "Memory", 0, t =>
        {
            var used = Between(t, 30, 18.2, 19.4);
            return new MetricSnapshot
            {
                Items =
                [
                    Graphed("RAM", "ram", used / 32 * 100, "%", $"{Format.Number(used)} / 32 GB", percent: used / 32 * 100, max: 100),
                    Plain("Free", "free", 32 - used, "GB", Format.Number(32 - used) + " GB"),
                    Plain("Committed", "committed", 61, "%", "61%"),
                ],
            };
        }),
        new DemoProvider("memoryapps", "Memory by app", 5, t => Apps(
            ("nebula-drift", Format.Number(Between(t, 31, 7.1, 7.4)) + " GB"), ("browser", Format.Number(Between(t, 32, 3.2, 3.5)) + " GB"),
            ("render-studio", "1.9 GB"), ("editor", "1.1 GB"), ("chat", "640 MB"))),
        new DemoProvider("disk", "Disk", 0, t =>
        {
            var (active, reading, writing) = (Between(t, 40, 6, 34), Between(t, 41, 40, 310), Between(t, 42, 4, 46));
            return new MetricSnapshot
            {
                Items =
                [
                    Graphed("Active", "active", active, "%", Format.Percent(active), percent: active, max: 100),
                    Graphed("Read", "read", reading, "MB/s", Format.Number(reading) + " MB/s"),
                    Graphed("Write", "write", writing, "MB/s", Format.Number(writing) + " MB/s"),
                ],
            };
        }),
        new DemoProvider("ioapps", "I/O by app", 5, t => Apps(
            ("nebula-drift", Format.Number(Between(t, 41, 36, 290)) + " MB/s"), ("recorder", Format.Number(Between(t, 42, 4, 30)) + " MB/s"),
            ("browser", "1.2 MB/s"), ("cloud-sync", "640 KB/s"), ("editor", "18 KB/s"))),
        new DemoProvider("drives", "Drives", 0, _ => new MetricSnapshot
        {
            Items =
            [
                new MetricItem("C: System") { Key = "c", Percent = 61, ValueText = "720 GB free" },
                new MetricItem("D: Games") { Key = "d", Percent = 93, ValueText = "138 GB free", Level = MetricLevel.Warning },
            ],
        }),
        new DemoProvider("network", "Network", 0, t =>
        {
            var (webDown, webUp, lanDown, lanUp) = (Between(t, 50, 61, 94), Between(t, 51, 3.4, 6.2), Between(t, 52, 0.2, 0.9), Between(t, 53, 18, 27));
            return new MetricSnapshot
            {
                Items =
                [
                    Speed("Down", "down", webDown + lanDown + 1), Speed("Up", "up", webUp + lanUp + 0.4),
                    Speed("Web down", "web_down", webDown), Speed("Web up", "web_up", webUp),
                    Speed("LAN down", "lan_down", lanDown), Speed("LAN up", "lan_up", lanUp),
                ],
            };
        }),
        new DemoProvider("netapps", "Network by app", 5, t => Apps(
            ("game-launcher", $"↓ {Mbps(Between(t, 50, 55, 86))}  ↑ {Mbps(0.9)}"), ("media-server", $"↓ {Mbps(0.3)}  ↑ {Mbps(Between(t, 53, 18, 27))}"),
            ("browser", $"↓ {Mbps(Between(t, 54, 2.2, 4.6))}  ↑ {Mbps(0.4)}"), ("chat", "↓ 96 kbps  ↑ 88 kbps"), ("cloud-sync", "↓ 12 kbps  ↑ 2.1 Mbps"))),
        new DemoProvider("webapps", "Web by app", 5, t => Apps(
            ("game-launcher", $"↓ {Mbps(Between(t, 50, 55, 86))}  ↑ {Mbps(0.9)}"), ("browser", $"↓ {Mbps(Between(t, 54, 2.2, 4.6))}  ↑ {Mbps(0.4)}"),
            ("cloud-sync", "↓ 12 kbps  ↑ 2.1 Mbps"), ("chat", "↓ 96 kbps  ↑ 88 kbps"), ("mail", "↓ 14 kbps  ↑ 3 kbps"))),
        new DemoProvider("lanapps", "LAN by app", 5, t => Apps(
            ("media-server", $"↓ {Mbps(0.3)}  ↑ {Mbps(Between(t, 53, 18, 27))}"), ("backup", "↓ 41 kbps  ↑ 1.8 Mbps"), ("remote-desktop", "↓ 182 kbps  ↑ 72 kbps"))),
        new DemoProvider("ping", "Ping", 0, t =>
        {
            var ms = Between(t, 60, 11, 17);
            return new MetricSnapshot
            {
                Items =
                [
                    Graphed("Now", "now", ms, "ms", Format.Whole(ms) + " ms"), Plain("Average", "average", 13, "ms", "13 ms"),
                    Plain("Jitter", "jitter", 2, "ms", "2 ms"), Plain("Loss", "loss", 0, "%", "0%"),
                ],
            };
        }),
        new DemoProvider("weather", "Weather", 0, _ => new MetricSnapshot
        {
            Subtitle = "London · Light rain",
            Items =
            [
                Plain("Now", "now", 14, "°C", "14 °C"), Plain("Feels like", "feels", 12, "°C", "12 °C"),
                Plain("Wind", "wind", 11, "mph", "11 mph"), Plain("Humidity", "humidity", 78, "%", "78%"),
                new MetricItem("Today") { Key = "today", ValueText = "9 to 16 °C" }, Plain("Rain chance", "rain", 65, "%", "65%"),
            ],
        }),
        new DemoProvider("benchmark", "Benchmark", 0, _ => new MetricSnapshot
        {
            Subtitle = "nebula-drift",
            Items =
            [
                new MetricItem("Average") { Key = "average", ValueText = "141 fps" }, new MetricItem("1% low") { Key = "low1", ValueText = "104 fps" },
                new MetricItem("0.1% low") { Key = "low01", ValueText = "88 fps" }, new MetricItem("Length") { Key = "length", ValueText = "2:30" },
                new MetricItem("GPU peak") { Key = "gpu_peak", ValueText = "69 °C" }, new MetricItem("CPU peak") { Key = "cpu_peak", ValueText = "68 °C" },
            ],
        }),
    ];

    private static double Fps(double t) => Between(t, 10, 131, 149);
    private static double GpuTemp(double t) => Between(t, 2, 64, 69);
    private static double GpuWatts(double t) => Between(t, 3, 268, 306);
    private static double CpuWatts(double t) => Between(t, 7, 74, 108);

    // A value that wanders between two ends: a few waves of unrelated lengths, different for every seed.
    private static double Between(double t, int seed, double low, double high)
    {
        var wave = Math.Sin(t / 7.3 + seed * 1.7) * 0.5 + Math.Sin(t / 2.9 + seed * 4.1) * 0.3 + Math.Sin(t / 1.1 + seed * 2.3) * 0.2;
        return low + (high - low) * (wave + 1) / 2;
    }

    private static MetricItem Limit(string label, string key, double percent, DateTimeOffset resets, string pace, MetricLevel level = MetricLevel.Normal) =>
        new(label) { Key = key, Percent = percent, ValueText = Format.Percent(percent), Number = percent, Unit = "%", ResetsAt = resets, Detail = pace, Level = level };

    private static MetricItem Graphed(string label, string key, double value, string unit, string text, double? percent = null, double? max = null) =>
        new(label) { Key = key, ValueText = text, Brief = key == "vram" ? Format.Number(value) + " GB" : text, Number = value, Unit = unit, Percent = percent, Graph = true, GraphMax = max };

    private static MetricItem Plain(string label, string key, double value, string unit, string text) =>
        new(label) { Key = key, ValueText = text, Number = value, Unit = unit };

    private static MetricItem Speed(string label, string key, double mbps) => Graphed(label, key, mbps, "Mbps", Mbps(mbps));

    private static MetricSnapshot Apps(params (string Name, string Value)[] apps) =>
        new() { Items = apps.Select(a => new MetricItem(a.Name) { ValueText = a.Value, FullWidth = true }).ToList() };

    private static string Mbps(double value) => Format.BitRate(value * 1e6 / 8);
    private static string Tenths(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
