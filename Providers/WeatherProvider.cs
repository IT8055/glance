using System.Globalization;
using System.Text.Json;
using Glance.Core;

namespace Glance.Providers;

/// <summary>
/// Current weather for a place the user picks in Settings, from Open-Meteo (free, no account or key).
/// The only thing sent is the place's coordinates, rounded to about a kilometre. Nothing is sent
/// until a place has been chosen.
/// </summary>
public sealed class WeatherProvider(WeatherConfig config) : IMetricProvider
{
    public sealed record Place(string Name, double Latitude, double Longitude);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly HttpClient Http = CreateClient();

    public string Title => "Weather";
    public TimeSpan RefreshInterval => TimeSpan.FromMinutes(15);

    // Conditions are published every 15 minutes, so showing the widget again does not need a new request.
    public TimeSpan FreshFor => TimeSpan.FromMinutes(5);

    public async Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        if (config.Latitude is not { } latitude || config.Longitude is not { } longitude)
            return MetricSnapshot.Info("Add your town in Settings to see the weather.");

        // Two decimal places is about a kilometre: plenty for weather, and no more precise than it needs to be.
        var url = "https://api.open-meteo.com/v1/forecast" +
            $"?latitude={latitude.ToString("0.##", Inv)}&longitude={longitude.ToString("0.##", Inv)}" +
            "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,relative_humidity_2m" +
            "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max&forecast_days=1&timezone=auto" +
            $"&temperature_unit={(config.Fahrenheit ? "fahrenheit" : "celsius")}&wind_speed_unit={(config.Mph ? "mph" : "kmh")}";

        try
        {
            using var response = await Http.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.Write($"Weather request failed: {(int)response.StatusCode} {body[..Math.Min(200, body.Length)]}");
                return MetricSnapshot.Error($"Weather service returned {(int)response.StatusCode}.", TimeSpan.FromMinutes(5));
            }

            using var doc = JsonDocument.Parse(body);
            var current = doc.RootElement.GetProperty("current");
            var degrees = config.Fahrenheit ? " °F" : " °C";
            var speed = config.Mph ? " mph" : " km/h";

            var items = new List<MetricItem>
            {
                new("Now") { ValueText = Whole(current, "temperature_2m") + degrees },
                new("Feels like") { ValueText = Whole(current, "apparent_temperature") + degrees },
                new("Wind") { ValueText = Whole(current, "wind_speed_10m") + speed },
                new("Humidity") { ValueText = Whole(current, "relative_humidity_2m") + "%" },
            };

            if (doc.RootElement.TryGetProperty("daily", out var daily))
            {
                if (First(daily, "temperature_2m_min") is { } low && First(daily, "temperature_2m_max") is { } high)
                    items.Add(new MetricItem("Today") { ValueText = $"{Round(low)} to {Round(high)}{degrees}" });
                if (First(daily, "precipitation_probability_max") is { } rain)
                    items.Add(new MetricItem("Rain chance") { ValueText = Round(rain) + "%" });
            }

            var conditions = current.TryGetProperty("weather_code", out var code) && code.TryGetInt32(out var wmo) ? Describe(wmo) : null;
            return new MetricSnapshot
            {
                Items = items,
                Subtitle = conditions is null ? config.Place : $"{ShortPlace(config.Place)} · {conditions}",
            };
        }
        catch (HttpRequestException)
        {
            return MetricSnapshot.Error("Cannot reach the weather service.", TimeSpan.FromMinutes(2));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return MetricSnapshot.Error("Weather request timed out.", TimeSpan.FromMinutes(2));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return MetricSnapshot.Error("Weather service sent an unexpected response.", TimeSpan.FromMinutes(5));
        }
    }

    /// <summary>Looks a town up by name. Returns the best matches, most likely first.</summary>
    public static async Task<List<Place>> SearchAsync(string query, CancellationToken ct = default)
    {
        var url = "https://geocoding-api.open-meteo.com/v1/search?count=5&language=en&format=json&name=" + Uri.EscapeDataString(query.Trim());
        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        var places = new List<Place>();
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return places;

        foreach (var r in results.EnumerateArray())
        {
            if (!r.TryGetProperty("latitude", out var lat) || !r.TryGetProperty("longitude", out var lon)) continue;
            var parts = new[] { Text(r, "name"), Text(r, "admin1"), Text(r, "country") }
                .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct();
            places.Add(new Place(string.Join(", ", parts), lat.GetDouble(), lon.GetDouble()));
        }
        return places;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Glance/0.2");
        return client;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Whole(JsonElement element, string name) => Round(element.GetProperty(name).GetDouble());

    private static string Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", Inv);

    private static double? First(JsonElement daily, string name) =>
        daily.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > 0 &&
        array[0].ValueKind == JsonValueKind.Number ? array[0].GetDouble() : null;

    // "Leeds, England, United Kingdom" is too long for the heading line: the town is enough.
    private static string ShortPlace(string place)
    {
        var comma = place.IndexOf(',');
        return comma > 0 ? place[..comma] : place;
    }

    // WMO weather interpretation codes, as used by Open-Meteo.
    private static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 or 81 => "Showers",
        82 => "Heavy showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm, hail",
        _ => "Unsettled",
    };
}
