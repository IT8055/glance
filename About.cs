using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Glance;

/// <summary>Who made Glance and where to find it. Shown on the About tab in Settings.</summary>
internal static class About
{
    public const string Author = "Gary Walker-Jones";
    public const string WebPage = "https://walker-jones.eu";
    public const string Repository = "https://github.com/IT8055/glance";
    public const string Issues = Repository + "/issues";

    /// <summary>Empty leaves the line out.</summary>
    public const string Email = "dev@walker-jones.eu";
}

/// <summary>
/// Compares this copy of Glance with the newest release on GitHub. Only runs when the user presses the
/// button: nothing is downloaded or installed, and the request says nothing about the PC or who is asking.
/// </summary>
internal static class UpdateCheck
{
    private const string Latest = "https://api.github.com/repos/IT8055/glance/releases/latest";
    private const string ReleasesPage = About.Repository + "/releases";

    private static readonly HttpClient Http = CreateClient();

    /// <param name="Message">What to tell the user.</param>
    /// <param name="Page">Where to get the newer version, or null if there is not one.</param>
    public sealed record Result(string Message, string? Page = null);

    public static async Task<Result> RunAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(Latest, ct);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => Read(await response.Content.ReadAsStringAsync(ct), current),
                HttpStatusCode.NotFound => new Result("No release has been published yet, so there is nothing newer to get."),
                HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => new Result("GitHub is limiting how often it can be asked. Try again in an hour."),
                var status => new Result($"GitHub answered with an error ({(int)status}). Try again later."),
            };
        }
        catch (HttpRequestException)
        {
            return new Result("Cannot reach GitHub. Check your internet connection.");
        }
        catch (OperationCanceledException)
        {
            return new Result("GitHub took too long to answer. Try again later.");
        }
    }

    /// <summary>Works out what to say from GitHub's description of the newest release.</summary>
    internal static Result Read(string json, Version current)
    {
        string? tag, page;
        try
        {
            using var document = JsonDocument.Parse(json);
            tag = Text(document.RootElement, "tag_name");
            page = Text(document.RootElement, "html_url");
        }
        catch (JsonException)
        {
            tag = page = null;
        }

        if (!TryVersion(tag, out var newest)) return new Result("GitHub sent an answer that could not be understood.");

        var mine = Tidy(current);
        if (newest == mine) return new Result($"You have the newest version, {Shown(mine)}.");
        if (newest < mine) return new Result($"You have {Shown(mine)}, which is newer than the latest release, {Shown(newest)}.");

        // The page is opened in the browser, so it has to be one of this project's own.
        var safe = page is not null && page.StartsWith(ReleasesPage + "/", StringComparison.OrdinalIgnoreCase) ? page : ReleasesPage + "/latest";
        return new Result($"Version {Shown(newest)} is available. You have {Shown(mine)}.", safe);
    }

    /// <summary>Reads a release tag such as "v0.6.0" or "0.6". False if it is not a version number.</summary>
    internal static bool TryVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var text = (tag ?? "").Trim().TrimStart('v', 'V');
        if (text.IndexOfAny(['-', '+', ' ']) is > 0 and var end) text = text[..end]; // "0.6.0-beta" counts as 0.6.0
        if (!text.Contains('.')) text += ".0";
        if (!Version.TryParse(text, out var parsed)) return false;
        version = Tidy(parsed);
        return true;
    }

    // Version treats 0.6 and 0.6.0 as different. Here they are the same.
    private static Version Tidy(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
    private static string Shown(Version v) => v.ToString(3);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Glance", null)); // GitHub insists on one
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
