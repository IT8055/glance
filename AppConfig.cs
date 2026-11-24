using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glance;

/// <summary>Where Glance keeps its settings, log and diagnostics. GLANCE_HOME overrides the folder.</summary>
internal static class AppPaths
{
    private static readonly string? Custom = Environment.GetEnvironmentVariable("GLANCE_HOME") is { Length: > 0 } home ? home : null;

    public static string Home { get; } =
        Custom ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glance");

    /// <summary>
    /// Empty for the normal settings folder, and a short tag that differs for every other one. Whatever is
    /// shared across the PC by name (the single-copy lock, trace sessions) carries it, so a second copy of
    /// Glance started with its own settings folder never takes over anything belonging to the first.
    /// </summary>
    public static string InstanceTag { get; } = Custom is null
        ? ""
        : "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Custom.ToLowerInvariant())), 0, 8);

    public static string ConfigFile => Path.Combine(Home, "config.json");
    public static string LogFile => Path.Combine(Home, "glance.log");
    public static string LastUsageFile => Path.Combine(Home, "last-usage.json");

    /// <summary>Benchmark results go to Documents, where they are easy to find and share.</summary>
    public static string Benchmarks { get; } = Custom is not null
        ? Path.Combine(Custom, "benchmarks")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Glance benchmarks");
}

internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Home);
                var file = AppPaths.LogFile;
                if (File.Exists(file) && new FileInfo(file).Length > 256 * 1024) File.Delete(file);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the widget down.
        }
    }
}

public sealed class ClaudeConfig
{
    /// <summary>How often to refresh while the widget is visible. Minimum 30.</summary>
    public int RefreshSeconds { get; set; } = 60;

    /// <summary>Leave null to use Claude Code's own credentials file.</summary>
    public string? CredentialsPath { get; set; }

    /// <summary>
    /// Which limits to show, in order. Supports * wildcards, for example
    /// ["session", "weekly_all", "weekly_fable"]. Empty means every limit the account has.
    /// </summary>
    public List<string> Show { get; set; } = [];

    /// <summary>Optional display names per limit key, for example { "weekly_all": "This week" }.</summary>
    public Dictionary<string, string> Labels { get; set; } = [];

    /// <summary>Add "on pace for 62%" or "runs out Thu 14:00" to each limit.</summary>
    public bool Pace { get; set; } = true;

    /// <summary>Show how much of the week's use came from Claude Code, chats and so on.</summary>
    public bool Breakdown { get; set; } = true;

    /// <summary>Notify when a limit passes one of the <see cref="AlertAt"/> levels. Keeps usage checked while the widget is hidden.</summary>
    public bool Alerts { get; set; } = true;

    /// <summary>Percentages that raise a notification on the way up.</summary>
    public List<int> AlertAt { get; set; } = [80, 95];

    /// <summary>Also notify when the session limit starts over.</summary>
    public bool AlertOnReset { get; set; } = true;

    // Replaced by profiles. Only read from old settings files, never written.
    public bool? Enabled { get; set; }
}

/// <summary>Options for the hardware sections.</summary>
public sealed class SystemConfig
{
    /// <summary>How often the hardware sections refresh while the widget is visible. 0.25 to 10.</summary>
    public double RefreshSeconds { get; set; } = 1;

    /// <summary>Borrow extra readings (CPU temperature, power) from HWiNFO or MSI Afterburner when one is running.</summary>
    public bool ExternalSensors { get; set; } = true;

    /// <summary>Show a small bar for each CPU thread.</summary>
    public bool ThreadBars { get; set; } = true;

    /// <summary>The host the Ping section measures.</summary>
    public string PingHost { get; set; } = "1.1.1.1";

    /// <summary>Readings picked from HWiNFO or MSI Afterburner for the Sensors section, as "sensor|label".</summary>
    public List<string> Sensors { get; set; } = [];

    // Replaced by profiles. Only read from old settings files, never written.
    public bool? Fps { get; set; }
    public bool? Gpu { get; set; }
    public bool? Cpu { get; set; }
    public bool? Memory { get; set; }
    public bool? Disk { get; set; }
    public bool? Drives { get; set; }
    public bool? Network { get; set; }
    public bool? Specs { get; set; }
}

/// <summary>The assumptions behind the Power panel's estimate of what the whole PC draws.</summary>
public sealed class PowerConfig
{
    /// <summary>Allowance in watts for everything that cannot be measured: motherboard, memory, drives, fans.</summary>
    public double RestWatts { get; set; } = 50;

    /// <summary>How efficient the power supply is taken to be, in percent. Most modern supplies are 85 to 94.</summary>
    public double Efficiency { get; set; } = 90;

    /// <summary>Rated size of the power supply in watts. With it set, the panel shows how loaded the supply is. 0 if unknown.</summary>
    public double SupplyWatts { get; set; }

    /// <summary>What a kilowatt-hour costs, in the PC's own currency. With it set, the panel shows the cost per hour. 0 to leave that out.</summary>
    public double PricePerKwh { get; set; }
}

public sealed class WeatherConfig
{
    /// <summary>The chosen place, as shown in Settings. Empty until the user picks one.</summary>
    public string Place { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    // Sensible starting units for the PC's region: Fahrenheit in the US, miles per hour in the US and UK.
    public bool Fahrenheit { get; set; } = RegionInfo.CurrentRegion.TwoLetterISORegionName == "US";
    public bool Mph { get; set; } = RegionInfo.CurrentRegion.TwoLetterISORegionName is "US" or "GB";

    // Replaced by profiles. Only read from old settings files, never written.
    public bool? Enabled { get; set; }
}

/// <summary>A named set of sections, in the order they are shown. Switch between them to change what the widget is for.</summary>
public sealed class ProfileConfig
{
    public string Name { get; set; } = "";

    /// <summary>Section names (see Sections.cs), in display order.</summary>
    public List<string> Sections { get; set; } = [];

    /// <summary>
    /// The same sections arranged in columns, once the user has dragged a panel somewhere.
    /// Null means Glance arranges them itself, into the number of columns chosen in Settings.
    /// </summary>
    public List<List<string>>? Layout { get; set; }

    /// <summary>
    /// Replaces the list of sections (from the tick boxes and arrows in Settings) while keeping a
    /// dragged arrangement as far as it still applies.
    /// </summary>
    public void SetSections(List<string> sections)
    {
        if (Layout is not null)
        {
            var sameSections = sections.Count == Sections.Count && !sections.Except(Sections).Any();
            if (sameSections)
            {
                // Only the order changed: keep the columns the same height and refill them in the new order.
                var filled = 0;
                Layout = Layout.Select(column =>
                {
                    var refill = sections.Skip(filled).Take(column.Count).ToList();
                    filled += column.Count;
                    return refill;
                }).ToList();
            }
            else
            {
                // Something was switched on or off: take the gone ones out, put new ones at the end of the last column.
                foreach (var column in Layout) column.RemoveAll(id => !sections.Contains(id));
                Layout.RemoveAll(column => column.Count == 0);
                if (Layout.Count == 0) Layout = null;
                else Layout[^1].AddRange(sections.Where(id => !Layout.Any(column => column.Contains(id))));
            }
        }

        Sections = Layout?.SelectMany(column => column).ToList() ?? sections;
    }

    public void Add(string section)
    {
        if (!Sections.Contains(section)) SetSections([.. Sections, section]);
    }
}

/// <summary>Where the widget sits. Anything but Custom keeps it pinned there as its size changes.</summary>
public static class Anchors
{
    public const string Custom = "custom";
    public const string TopLeft = "top-left";
    public const string TopCenter = "top-center";
    public const string TopRight = "top-right";
    public const string BottomLeft = "bottom-left";
    public const string BottomCenter = "bottom-center";
    public const string BottomRight = "bottom-right";

    public static readonly IReadOnlyList<(string Value, string Name)> All =
    [
        (TopLeft, "Top left"), (TopCenter, "Top centre"), (TopRight, "Top right"),
        (BottomLeft, "Bottom left"), (BottomCenter, "Bottom centre"), (BottomRight, "Bottom right"),
        (Custom, "Where I drag it"),
    ];
}

public sealed class AppConfig
{
    public const string DefaultHotkey = "Ctrl+Alt+U";

    // Raised when a new version needs to adjust settings saved by an older one.
    private const int CurrentVersion = 5;

    /// <summary>Which version of Glance's settings layout this file was last saved with.</summary>
    public int Version { get; set; }

    /// <summary>Name of the colour scheme (see Theme.cs).</summary>
    public string Theme { get; set; } = Themes.Default;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Leaves out the retired options (always null once read) and an unset position.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Writes "Ctrl+Alt+U" rather than "Ctrl+Alt+U". The file is local, never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Global shortcuts. An empty one is simply not registered.

    /// <summary>Shows and hides the widget, for example "Ctrl+Alt+U" or "Win+Shift+F9".</summary>
    public string Hotkey { get; set; } = DefaultHotkey;

    /// <summary>Shows and hides the one-line compact strip.</summary>
    public string CompactHotkey { get; set; } = "Ctrl+Alt+Shift+U";

    /// <summary>Starts and stops a benchmark recording.</summary>
    public string BenchmarkHotkey { get; set; } = "Ctrl+Alt+Shift+B";

    /// <summary>Switches to the next profile.</summary>
    public string ProfileHotkey { get; set; } = "Ctrl+Alt+Shift+P";

    /// <summary>Make the show/hide shortcut step through widget, compact strip, hidden.</summary>
    public bool CycleCompact { get; set; }

    /// <summary>Widget opacity, 0.2 to 1.</summary>
    public double Opacity { get; set; } = 0.96;

    /// <summary>Widget size multiplier, 0.7 to 1.6.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>When true, mouse clicks pass through the widget to whatever is underneath.</summary>
    public bool ClickThrough { get; set; }

    /// <summary>Show the widget as soon as Glance starts, instead of waiting for the hotkey.</summary>
    public bool StartVisible { get; set; }

    /// <summary>How many columns the sections are laid out in, 1 to 4. More are used if the screen is too short.</summary>
    public int Columns { get; set; } = 2;

    /// <summary>One of the <see cref="Anchors"/> values. Dragging the widget switches this to "custom".</summary>
    public string? Anchor { get; set; }

    /// <summary>Top-left position in screen pixels, used when the anchor is "custom".</summary>
    public int? X { get; set; }
    public int? Y { get; set; }

    /// <summary>
    /// OLED mode: while the widget is on screen, move it a pixel at a time so nothing stays lit in one place.
    /// See <see cref="PixelShift"/>.
    /// </summary>
    public bool OledMode { get; set; }

    /// <summary>Seconds between one-pixel moves in OLED mode.</summary>
    public int OledMoveSeconds { get; set; } = PixelShift.DefaultSeconds;

    /// <summary>How many pixels the widget may stray, across and down, in OLED mode.</summary>
    public int OledTravel { get; set; } = PixelShift.DefaultTravel;

    /// <summary>Draw a graph of the last minute for load, temperature and frame rate readings.</summary>
    public bool Graphs { get; set; }

    /// <summary>Show the lowest and highest value seen for each reading.</summary>
    public bool Ranges { get; set; }

    public List<ProfileConfig> Profiles { get; set; } = [];

    /// <summary>Name of the profile in use.</summary>
    public string Profile { get; set; } = "";

    /// <summary>Profile to switch to while a full-screen app is in front. Empty for never.</summary>
    public string GameProfile { get; set; } = "";

    /// <summary>The reading drawn in the icon near the clock, for example "claude.session". Empty for the plain icon.</summary>
    public string TrayReading { get; set; } = "";

    /// <summary>The readings in the compact strip, in order.</summary>
    public List<string> Strip { get; set; } = ["fps.now", "gpu.load", "gpu.temp", "cpu.load", "cpu.temp"];

    public ClaudeConfig Claude { get; set; } = new();

    [JsonPropertyName("system")]
    public SystemConfig Hardware { get; set; } = new();

    public WeatherConfig Weather { get; set; } = new();

    public PowerConfig Power { get; set; } = new();

    [JsonIgnore]
    public ProfileConfig ActiveProfile => Profiles.FirstOrDefault(p => p.Name == Profile) ?? Profiles[0];

    public static AppConfig Load()
    {
        var config = new AppConfig();
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
                config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile), Json) ?? config;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Write($"Could not read config.json, using defaults: {ex.Message}");
            // Keep the unreadable file so a later save does not destroy hand edits.
            try { File.Copy(AppPaths.ConfigFile, AppPaths.ConfigFile + ".bad", overwrite: true); } catch { }
        }

        config.Normalize();
        return config;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Home);
            File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Could not save config.json: {ex.Message}");
        }
    }

    private void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Hotkey)) Hotkey = DefaultHotkey;
        CompactHotkey ??= "";
        BenchmarkHotkey ??= "";
        ProfileHotkey ??= "";
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.2, 1.0) : 0.96;
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, 0.7, 1.6) : 1.0;
        Columns = Math.Clamp(Columns, 1, 4);
        OledMoveSeconds = Math.Clamp(OledMoveSeconds, 1, 3600);
        OledTravel = Math.Clamp(OledTravel, 2, 24);
        Theme = Themes.Find(Theme).Name;

        // Settings saved before anchors existed have a dragged position and no anchor: keep that position.
        if (Anchor is null || Anchors.All.All(a => a.Value != Anchor))
            Anchor = X is not null && Y is not null ? Anchors.Custom : Anchors.TopRight;

        Weather ??= new WeatherConfig();
        Weather.Place ??= "";

        Power ??= new PowerConfig();
        Power.RestWatts = double.IsFinite(Power.RestWatts) ? Math.Clamp(Power.RestWatts, 0, 1000) : 50;
        Power.Efficiency = double.IsFinite(Power.Efficiency) ? Math.Clamp(Power.Efficiency, 50, 100) : 90;
        Power.SupplyWatts = double.IsFinite(Power.SupplyWatts) ? Math.Clamp(Power.SupplyWatts, 0, 5000) : 0;
        Power.PricePerKwh = double.IsFinite(Power.PricePerKwh) ? Math.Max(0, Power.PricePerKwh) : 0;

        Claude ??= new ClaudeConfig();
        Claude.Show ??= [];
        Claude.Labels ??= [];
        Claude.AlertAt = (Claude.AlertAt ?? []).Where(p => p is > 0 and <= 100).Distinct().Order().ToList();

        Hardware ??= new SystemConfig();
        Hardware.RefreshSeconds = double.IsFinite(Hardware.RefreshSeconds) ? Math.Clamp(Hardware.RefreshSeconds, 0.25, 10) : 1;
        Hardware.Sensors ??= [];
        if (string.IsNullOrWhiteSpace(Hardware.PingHost)) Hardware.PingHost = "1.1.1.1";

        Strip ??= [];
        TrayReading ??= "";
        GameProfile ??= "";

        NormalizeProfiles();
    }

    private void NormalizeProfiles()
    {
        Profiles = (Profiles ?? []).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name)).ToList();

        if (Profiles.Count == 0)
        {
            // First run, or settings from before profiles: the old per-section switches become the first profile.
            var everyday = new List<string>();
            void Add(string id, bool? switchedOn, bool otherwise = true)
            {
                if (switchedOn ?? otherwise) everyday.Add(id);
            }

            Add("claude", Claude.Enabled);
            Add("fps", Hardware.Fps);
            Add("gpu", Hardware.Gpu);
            Add("gpuapps", Hardware.Gpu);
            Add("cpu", Hardware.Cpu);
            Add("cpuapps", Hardware.Cpu);
            Add("power", Hardware.Cpu);
            Add("memory", Hardware.Memory);
            Add("memoryapps", Hardware.Memory);
            Add("disk", Hardware.Disk);
            Add("drives", Hardware.Drives);
            Add("network", Hardware.Network);
            Add("netapps", Hardware.Network);
            Add("battery", null);
            Add("weather", Weather.Enabled);
            Add("specs", Hardware.Specs, otherwise: false);

            Profiles =
            [
                new ProfileConfig { Name = "Everyday", Sections = everyday },
                new ProfileConfig { Name = "Gaming", Sections = ["fps", "gpu", "cpu", "memory", "benchmark"] },
            ];
            Profile = "Everyday";
        }

        Claude.Enabled = null;
        Weather.Enabled = null;
        Hardware.Fps = Hardware.Gpu = Hardware.Cpu = Hardware.Memory = null;
        Hardware.Disk = Hardware.Drives = Hardware.Network = Hardware.Specs = null;

        foreach (var profile in Profiles)
        {
            profile.Sections = (profile.Sections ?? []).Where(Sections.Exists).Distinct().ToList();

            // Version 4 added "by app" views for CPU and memory. A profile that already shows the GPU one
            // wants that kind of detail, so it gets the new ones next to their parent section.
            if (Version < 4 && profile.Sections.Contains("gpuapps"))
            {
                foreach (var (parent, child) in new[] { ("cpu", "cpuapps"), ("memory", "memoryapps") })
                {
                    var at = profile.Sections.IndexOf(parent);
                    if (at >= 0 && !profile.Sections.Contains(child)) profile.Sections.Insert(at + 1, child);
                }
            }

            // Version 5 added the Power and Network by app panels. They go into the profile in use, next to
            // the panels they belong with.
            if (Version < 5 && profile.Name == Profile)
            {
                if (!profile.Sections.Contains("power") && (profile.Sections.Contains("cpu") || profile.Sections.Contains("gpu")))
                {
                    var after = new[] { "cpuapps", "cpu", "gpuapps", "gpu" }.Select(id => profile.Sections.IndexOf(id)).First(index => index >= 0);
                    profile.Sections.Insert(after + 1, "power");
                }
                if (!profile.Sections.Contains("netapps") && profile.Sections.IndexOf("network") is var network and >= 0)
                    profile.Sections.Insert(network + 1, "netapps");
            }

            // A hand-edited or outdated arrangement is brought back in line with the section list.
            if (profile.Layout is { } layout)
            {
                var placed = new HashSet<string>();
                profile.Layout = layout
                    .Select(column => (column ?? []).Where(id => profile.Sections.Contains(id) && placed.Add(id)).ToList())
                    .Where(column => column.Count > 0)
                    .ToList();
                if (profile.Layout.Count == 0) profile.Layout = null;
                else profile.Layout[^1].AddRange(profile.Sections.Where(id => !placed.Contains(id)));
                if (profile.Layout is not null) profile.Sections = profile.Layout.SelectMany(column => column).ToList();
            }
        }
        Version = CurrentVersion;

        if (Profiles.All(p => p.Name != Profile)) Profile = Profiles[0].Name;
        if (Profiles.All(p => p.Name != GameProfile)) GameProfile = "";
    }
}
