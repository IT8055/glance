using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glance.Core;

namespace Glance.Providers;

/// <summary>
/// Plan usage for the account signed in to Claude Code: the same numbers as /usage and the
/// desktop app's Settings > Usage page.
///
/// The access token is read from Claude Code's credentials file on every fetch, sent only to
/// api.anthropic.com, and never stored, logged or refreshed here. Claude Code owns the sign-in:
/// when the token expires, any Claude Code activity renews it and the widget picks it up.
/// </summary>
public sealed class ClaudeUsageProvider(ClaudeConfig config) : IMetricProvider
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const double WarningAt = 75;
    private const double CriticalAt = 90;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <param name="Key">Stable name used by claude.show and claude.labels: "session", "weekly_all", "weekly_fable".</param>
    private sealed record Limit(string Key, string Label, double Percent, DateTimeOffset? ResetsAt);

    public string Title => "Claude";

    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(Math.Max(30, config.RefreshSeconds));

    public async Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        var (token, expiresAt, problem) = ReadToken();
        if (token is null) return MetricSnapshot.Error(problem!);
        if (expiresAt <= DateTimeOffset.UtcNow)
            return MetricSnapshot.Error("Claude sign-in has expired. Use Claude Code once and it will renew.");

        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.UserAgent.ParseAdd("Glance/0.2");

            using var response = await Http.SendAsync(request, ct);
            body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return MetricSnapshot.Error("Claude sign-in was rejected. Use Claude Code once and it will renew.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return MetricSnapshot.Error("This Claude sign-in is not allowed to read plan usage.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5);
                return MetricSnapshot.Error("Usage service is rate limiting. Will retry shortly.", wait);
            }
            if (!response.IsSuccessStatusCode)
            {
                Log.Write($"Usage request failed: {(int)response.StatusCode} {Truncate(body, 300)}");
                return MetricSnapshot.Error($"Usage service returned {(int)response.StatusCode}.");
            }
        }
        catch (HttpRequestException)
        {
            return MetricSnapshot.Error("Cannot reach api.anthropic.com.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return MetricSnapshot.Error("Usage request timed out.");
        }

        // Kept for troubleshooting limit names. Contains usage numbers only, no credentials.
        try
        {
            Directory.CreateDirectory(AppPaths.Home);
            File.WriteAllText(AppPaths.LastUsageFile, body);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return Parse(body);
    }

    /// <summary>Maps the usage response to rows. Internal so it can be exercised without a network call.</summary>
    internal MetricSnapshot Parse(string body)
    {
        List<Limit> limits;
        try
        {
            using var doc = JsonDocument.Parse(body);
            limits = ReadLimits(doc.RootElement);
            if (limits.Count == 0) limits = ReadLegacyBuckets(doc.RootElement);
        }
        catch (JsonException)
        {
            return MetricSnapshot.Error("Usage service sent an unexpected response.");
        }

        var chosen = Select(limits);
        if (chosen.Count == 0)
        {
            return MetricSnapshot.Error(limits.Count == 0
                ? "No usage limits were reported for this account."
                : "No usage limit matches claude.show in settings.");
        }

        return new MetricSnapshot
        {
            Items = chosen.Select(l => new MetricItem(config.Labels.TryGetValue(l.Key, out var custom) && !string.IsNullOrWhiteSpace(custom) ? custom : l.Label)
            {
                Percent = Math.Clamp(l.Percent, 0, 100),
                ValueText = $"{Math.Round(l.Percent, MidpointRounding.AwayFromZero):0}%",
                ResetsAt = l.ResetsAt,
                Level = l.Percent >= CriticalAt ? MetricLevel.Critical
                      : l.Percent >= WarningAt ? MetricLevel.Warning
                      : MetricLevel.Normal,
            }).ToList(),
        };
    }

    // The response carries a "limits" list, which is what the desktop app's usage page is drawn from:
    //   { "kind": "session", "percent": 15, "resets_at": "..." }
    //   { "kind": "weekly_all", "percent": 28, ... }
    //   { "kind": "weekly_scoped", "group": "weekly", "percent": 15, "scope": { "model": { "display_name": "Fable" } } }
    // Per-model limits only exist here, so this list is preferred over the older top-level buckets.
    private static List<Limit> ReadLimits(JsonElement root)
    {
        var limits = new List<Limit>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("limits", out var array) || array.ValueKind != JsonValueKind.Array)
            return limits;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("percent", out var percent) || percent.ValueKind != JsonValueKind.Number) continue;

            var kind = Text(item, "kind") ?? "limit";
            var group = Text(item, "group") ?? kind;
            var scope = ScopeName(item);

            var (key, label) = (kind, scope) switch
            {
                ("session", null) => ("session", "Current session"),
                ("weekly_all", null) => ("weekly_all", "Week, all models"),
                (_, not null) => ($"{group}_{Slug(scope)}", $"{(group == "weekly" ? "Week" : Pretty(group))}, {scope}"),
                _ => (kind, Pretty(kind)),
            };
            limits.Add(new Limit(key, label, percent.GetDouble(), Time(item)));
        }
        return limits;
    }

    // Older shape, kept as a fallback: { "five_hour": { "utilization": 42.0, "resets_at": "..." }, "seven_day": ... }.
    private static List<Limit> ReadLegacyBuckets(JsonElement root)
    {
        var limits = new List<Limit>();
        if (root.ValueKind != JsonValueKind.Object) return limits;

        const string weekPrefix = "seven_day_";
        foreach (var property in root.EnumerateObject())
        {
            var value = property.Value;
            // Paid extra usage is a different kind of number, so it is not treated as a limit.
            if (value.ValueKind != JsonValueKind.Object || property.Name == "extra_usage") continue;
            if (!value.TryGetProperty("utilization", out var utilization) || utilization.ValueKind != JsonValueKind.Number) continue;

            var name = property.Name;
            var (key, label) = name switch
            {
                "five_hour" => ("session", "Current session"),
                "seven_day" => ("weekly_all", "Week, all models"),
                _ when name.StartsWith(weekPrefix, StringComparison.Ordinal) =>
                    ("weekly_" + name[weekPrefix.Length..], "Week, " + Pretty(name[weekPrefix.Length..])),
                _ => (name, Pretty(name)),
            };
            limits.Add(new Limit(key, label, utilization.GetDouble(), Time(value)));
        }

        static int Rank(string key) => key switch { "session" => 0, "weekly_all" => 1, _ => 2 };
        return limits.OrderBy(l => Rank(l.Key)).ToList();
    }

    private List<Limit> Select(List<Limit> limits)
    {
        if (config.Show.Count == 0) return limits;

        var chosen = new List<Limit>();
        foreach (var pattern in config.Show.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var regex = new Regex("^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);
            chosen.AddRange(limits.Where(l => regex.IsMatch(l.Key) && !chosen.Contains(l)));
        }
        return chosen;
    }

    private static string? ScopeName(JsonElement item)
    {
        if (!item.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object) return null;

        if (scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object &&
            (Text(model, "display_name") ?? Text(model, "id")) is { Length: > 0 } modelName)
        {
            return modelName;
        }
        if (scope.TryGetProperty("surface", out var surface))
        {
            if (surface.ValueKind == JsonValueKind.String && surface.GetString() is { Length: > 0 } text) return Pretty(text);
            if (surface.ValueKind == JsonValueKind.Object && (Text(surface, "display_name") ?? Text(surface, "id")) is { Length: > 0 } name) return name;
        }
        return null;
    }

    private static DateTimeOffset? Time(JsonElement item) =>
        item.TryGetProperty("resets_at", out var resets) && resets.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(resets.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Slug(string text) => Regex.Replace(text.Trim().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');

    private static string Pretty(string key)
    {
        var text = key.Replace('_', ' ').Trim();
        return text.Length == 0 ? key : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private (string? Token, DateTimeOffset? ExpiresAt, string? Problem) ReadToken()
    {
        var path = CredentialsPath();
        if (!File.Exists(path)) return (null, null, "Claude Code sign-in not found. Sign in to Claude Code first.");

        try
        {
            // Claude Code rewrites this file when it renews the token, so never lock it.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);

            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object ||
                !oauth.TryGetProperty("accessToken", out var tokenElement) || tokenElement.ValueKind != JsonValueKind.String ||
                tokenElement.GetString() is not { Length: > 0 } token)
            {
                return (null, null, "Claude Code is not signed in with a Claude plan.");
            }

            DateTimeOffset? expiresAt = null;
            if (oauth.TryGetProperty("expiresAt", out var expires) && expires.ValueKind == JsonValueKind.Number && expires.TryGetInt64(out var ms))
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(ms);

            return (token, expiresAt, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return (null, null, "Could not read the Claude Code sign-in file.");
        }
    }

    private string CredentialsPath()
    {
        if (!string.IsNullOrWhiteSpace(config.CredentialsPath))
            return Environment.ExpandEnvironmentVariables(config.CredentialsPath);

        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(configDir))
            configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(configDir, ".credentials.json");
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
