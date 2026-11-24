using Glance.Core;
using Glance.Providers;

namespace Glance;

/// <summary>
/// The sections the widget can show: their names, how each one is built for the PC it is running on,
/// and why one might not be available here. A new metric provider is registered in this file.
/// </summary>
internal static class Sections
{
    /// <summary>Every section, in the order they are offered in Settings.</summary>
    public static readonly IReadOnlyList<(string Id, string Name)> All =
    [
        ("claude", "Claude usage"),
        ("fps", "FPS"),
        ("gpu", "GPU"),
        ("gpuapps", "GPU by app"),
        ("cpu", "CPU"),
        ("cpuapps", "CPU by app"),
        ("memory", "Memory"),
        ("memoryapps", "Memory by app"),
        ("disk", "Disk activity"),
        ("ioapps", "I/O by app"),
        ("drives", "Drive space"),
        ("network", "Network"),
        ("ping", "Ping"),
        ("sensors", "Extra sensors"),
        ("battery", "Battery"),
        ("weather", "Weather"),
        ("benchmark", "Benchmark"),
        ("specs", "PC specs"),
    ];

    /// <summary>What a benchmark records, whether or not the profile in use shows them.</summary>
    public static readonly IReadOnlyList<string> Benchmarked = ["fps", "gpu", "cpu", "memory", "disk", "network", "sensors", "ping"];

    public static bool Exists(string id) => All.Any(s => s.Id == id);

    public static string NameOf(string id) => All.FirstOrDefault(s => s.Id == id).Name ?? id;

    /// <summary>
    /// Which section a provider belongs to. Usually the same name, but a PC with two graphics cards
    /// has providers "gpu" and "gpu2" that both belong to the "gpu" section.
    /// </summary>
    public static string SectionOf(string providerId) =>
        providerId.StartsWith("gpu", StringComparison.Ordinal) && providerId.Length > 3 && char.IsAsciiDigit(providerId[3]) ? "gpu" : providerId;

    /// <summary>A short reason the section cannot work on this PC, or null if it can.</summary>
    public static string? WhyUnavailable(string id, AppConfig config) => id switch
    {
        "claude" when !ClaudeUsageProvider.IsSignedIn(config.Claude) => "needs Claude Code signed in",
        "battery" when !BatteryProvider.IsPresent => "this PC has no battery",
        "gpu" or "gpuapps" when GpuProviders.MainAdapter() is null => "no graphics card found",
        _ => null,
    };

    /// <summary>
    /// Builds the providers for one section. Sections that cannot work on this PC build nothing, so they
    /// are quietly left out rather than showing an error for something the user cannot fix.
    /// </summary>
    public static IEnumerable<IMetricProvider> Create(string id, AppConfig config, BenchmarkRecorder recorder)
    {
        if (WhyUnavailable(id, config) is not null) return [];

        var hardware = config.Hardware;
        var rate = TimeSpan.FromSeconds(hardware.RefreshSeconds);
        return id switch
        {
            "claude" => [new ClaudeUsageProvider(config.Claude)],
            "fps" => [new FpsProvider(rate)],
            "gpu" => GpuProviders.Detect(rate),
            "gpuapps" => [new GpuAppsProvider(rate)],
            "cpu" => [new CpuProvider(rate, hardware.ThreadBars)],
            "cpuapps" => [new TopAppsProvider(TopAppsProvider.Kind.Cpu, rate)],
            "memory" => [new MemoryProvider(rate)],
            "memoryapps" => [new TopAppsProvider(TopAppsProvider.Kind.Memory, rate)],
            "disk" => [new DiskActivityProvider(rate)],
            "ioapps" => [new TopAppsProvider(TopAppsProvider.Kind.Io, rate)],
            "drives" => [new DriveSpaceProvider()],
            "network" => [new NetworkProvider(rate)],
            "ping" => [new PingProvider(hardware.PingHost.Trim(), rate)],
            "sensors" => [new SensorsProvider(hardware.Sensors.ToList(), rate)],
            "battery" => [new BatteryProvider()],
            "weather" => [new WeatherProvider(config.Weather)],
            "benchmark" => [new BenchmarkProvider(recorder, () => config.BenchmarkHotkey)],
            "specs" => [new SpecsProvider()],
            _ => [],
        };
    }
}
