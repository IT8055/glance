using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glance;

/// <summary>Where Glance keeps its settings, log and diagnostics. GLANCE_HOME overrides the folder.</summary>
internal static class AppPaths
{
    public static string Home { get; } =
        Environment.GetEnvironmentVariable("GLANCE_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glance");

    public static string ConfigFile => Path.Combine(Home, "config.json");
    public static string LogFile => Path.Combine(Home, "glance.log");
    public static string LastUsageFile => Path.Combine(Home, "last-usage.json");
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
    public bool Enabled { get; set; } = true;

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
}

/// <summary>The hardware sections. Each flag turns one section of the widget on or off.</summary>
public sealed class SystemConfig
{
    /// <summary>How often the hardware sections refresh while the widget is visible. 0.25 to 10.</summary>
    public double RefreshSeconds { get; set; } = 1;

    public bool Fps { get; set; } = true;
    public bool Gpu { get; set; } = true;
    public bool Cpu { get; set; } = true;
    public bool Memory { get; set; } = true;
    public bool Disk { get; set; } = true;
    public bool Drives { get; set; } = true;
    public bool Network { get; set; } = true;

    /// <summary>A list of what the PC is made of. Off by default: it never changes.</summary>
    public bool Specs { get; set; }

    /// <summary>Borrow extra readings (CPU temperature, power) from HWiNFO or MSI Afterburner when one is running.</summary>
    public bool ExternalSensors { get; set; } = true;
}

public sealed class WeatherConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>The chosen place, as shown in Settings. Empty until the user picks one.</summary>
    public string Place { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    // Sensible starting units for the PC's region: Fahrenheit in the US, miles per hour in the US and UK.
    public bool Fahrenheit { get; set; } = RegionInfo.CurrentRegion.TwoLetterISORegionName == "US";
    public bool Mph { get; set; } = RegionInfo.CurrentRegion.TwoLetterISORegionName is "US" or "GB";
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

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Writes "Ctrl+Alt+U" rather than "Ctrl+Alt+U". The file is local, never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Global show/hide shortcut, for example "Ctrl+Alt+U" or "Win+Shift+F9".</summary>
    public string Hotkey { get; set; } = DefaultHotkey;

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

    public ClaudeConfig Claude { get; set; } = new();

    [JsonPropertyName("system")]
    public SystemConfig Hardware { get; set; } = new();

    public WeatherConfig Weather { get; set; } = new();

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
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.2, 1.0) : 0.96;
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, 0.7, 1.6) : 1.0;
        Columns = Math.Clamp(Columns, 1, 4);

        // Settings saved before anchors existed have a dragged position and no anchor: keep that position.
        if (Anchor is null || Anchors.All.All(a => a.Value != Anchor))
            Anchor = X is not null && Y is not null ? Anchors.Custom : Anchors.TopRight;

        Weather ??= new WeatherConfig();
        Weather.Place ??= "";

        Claude ??= new ClaudeConfig();
        Claude.Show ??= [];
        Claude.Labels ??= [];

        Hardware ??= new SystemConfig();
        Hardware.RefreshSeconds = double.IsFinite(Hardware.RefreshSeconds) ? Math.Clamp(Hardware.RefreshSeconds, 0.25, 10) : 1;
    }
}
