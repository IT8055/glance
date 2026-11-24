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
public sealed class ClaudeUsageProvider(ClaudeConfig config) : IMetricProvider, IManualCheck
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const double WarningAt = 75;
    private const double CriticalAt = 90;

    /// <summary>
    /// The usage service is never asked more often than this, however often the widget is shown or
    /// rebuilt. It rate limits callers that poll it, and the numbers barely move in three minutes anyway.
    /// Only the "check now" button gets past it.
    /// </summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(3);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Shared by every instance: the section is rebuilt whenever a setting changes, and that must not cost a request.
    private static readonly object CacheGate = new();
    private static string? _lastBody;
    private static DateTimeOffset _lastFetched = DateTimeOffset.MinValue;
    private static DateTimeOffset _backOffUntil = DateTimeOffset.MinValue;
    private volatile bool _checkNow;

    /// <summary>Lets the next fetch through even if the last one was less than three minutes ago.</summary>
    public void CheckNow() => _checkNow = true;

    /// <param name="Key">Stable name used by claude.show and claude.labels: "session", "weekly_all", "weekly_fable".</param>
    /// <param name="Window">How long the limit's period is, when known. Needed to work out the pace.</param>
    private sealed record Limit(string Key, string Label, double Percent, DateTimeOffset? ResetsAt, TimeSpan? Window);

    private static readonly TimeSpan SessionWindow = TimeSpan.FromHours(5);
    private static readonly TimeSpan WeekWindow = TimeSpan.FromDays(7);

    public string Title => "Claude";

    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(Math.Max(MinimumGap.TotalSeconds, config.RefreshSeconds));
    public TimeSpan BackgroundInterval => RefreshInterval;

    // Showing the widget does not refetch readings that are still within the three minutes.
    public TimeSpan FreshFor => MinimumGap;

    /// <summary>
    /// True when this PC has a Claude Code sign-in to read. Only checks that the file is there, not what is in it.
    /// On a PC without Claude Code the section is simply left out.
    /// </summary>
    public static bool IsSignedIn(ClaudeConfig config) =>
        // GLANCE_NO_CLAUDE is a hard off switch for test runs: with it set the sign-in is never looked at,
        // whatever the settings say.
        Environment.GetEnvironmentVariable("GLANCE_NO_CLAUDE") is null && File.Exists(CredentialsPath(config));

    public async Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        var asked = _checkNow;
        _checkNow = false;
        var now = DateTimeOffset.Now;

        lock (CacheGate)
        {
            // After a restart, pick up where the last run left off rather than asking again straight away.
            if (_lastBody is null) RememberSavedResponse();

            if (_lastBody is not null && !asked && now - _lastFetched < MinimumGap)
                return Parse(_lastBody) with { AsOf = _lastFetched };

            // Told to wait by the service: not even the button overrides that.
            if (now < _backOffUntil)
            {
                var notice = $"Usage service is rate limiting. Next try at {_backOffUntil.ToLocalTime():t}.";
                return _lastBody is null
                    ? MetricSnapshot.Error(notice, _backOffUntil - now)
                    : Parse(_lastBody) with { AsOf = _lastFetched, Notice = notice, RetryAfter = _backOffUntil - now };
            }
        }

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
                // Honour what the service asks for, and never retry sooner than ten minutes after being told off.
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.Zero;
                if (wait < TimeSpan.FromMinutes(10)) wait = TimeSpan.FromMinutes(10);
                lock (CacheGate) _backOffUntil = DateTimeOffset.Now + wait;
                Log.Write($"Claude usage service rate limited the request. Waiting until {_backOffUntil.ToLocalTime():t}.");

                var notice = $"Usage service is rate limiting. Next try at {_backOffUntil.ToLocalTime():t}.";
                lock (CacheGate)
                {
                    return _lastBody is null
                        ? MetricSnapshot.Error(notice, wait)
                        : Parse(_lastBody) with { AsOf = _lastFetched, Notice = notice, RetryAfter = wait };
                }
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

        lock (CacheGate)
        {
            _lastBody = body;
            _lastFetched = DateTimeOffset.Now;
        }

        // Kept so the next start can carry on from it, and for troubleshooting limit names.
        // Contains usage numbers only, no credentials.
        try
        {
            Directory.CreateDirectory(AppPaths.Home);
            File.WriteAllText(AppPaths.LastUsageFile, body);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return Parse(body);
    }

    // Called with CacheGate held. The saved file's timestamp is when it was fetched.
    private static void RememberSavedResponse()
    {
        try
        {
            var file = new FileInfo(AppPaths.LastUsageFile);
            if (!file.Exists || DateTimeOffset.Now - file.LastWriteTime >= MinimumGap) return;
            _lastBody = File.ReadAllText(file.FullName);
            _lastFetched = file.LastWriteTime;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Maps the usage response to rows. Internal so it can be exercised without a network call.</summary>
    internal MetricSnapshot Parse(string body)
    {
        List<Limit> limits;
        List<MetricItem> sources;
        try
        {
            using var doc = JsonDocument.Parse(body);
            limits = ReadLimits(doc.RootElement);
            if (limits.Count == 0) limits = ReadLegacyBuckets(doc.RootElement);
            sources = config.Breakdown ? ReadSources(doc.RootElement) : [];
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

        var now = DateTimeOffset.Now;
        var items = chosen.Select(l => new MetricItem(config.Labels.TryGetValue(l.Key, out var custom) && !string.IsNullOrWhiteSpace(custom) ? custom : l.Label)
        {
            Key = l.Key,
            Percent = Math.Clamp(l.Percent, 0, 100),
            ValueText = Format.Percent(l.Percent),
            Number = l.Percent,
            Unit = "%",
            ResetsAt = l.ResetsAt,
            Detail = config.Pace ? Pace(l, now) : null,
            Level = l.Percent >= CriticalAt ? MetricLevel.Critical
                  : l.Percent >= WarningAt ? MetricLevel.Warning
                  : MetricLevel.Normal,
        }).ToList();
        items.AddRange(sources);

        return new MetricSnapshot { Items = items };
    }

    /// <summary>
    /// Where the limit is heading if use carries on at the average rate so far this period:
    /// either the moment it would run out, or the level it would reach by the reset.
    /// </summary>
    private static string? Pace(Limit limit, DateTimeOffset now)
    {
        if (limit.Window is not { } window || limit.ResetsAt is not { } resets) return null;
        if (limit.Percent is <= 0 or >= 100) return null;

        var start = resets - window;
        var elapsed = now - start;
        // In the first few percent of a period one burst of use would give a wild forecast.
        if (elapsed < window * 0.05 || elapsed >= window) return null;

        var atReset = limit.Percent * (window / elapsed);
        if (atReset < 100) return $"on pace for {Format.Whole(atReset)}%";

        var runsOut = start + elapsed * (100 / limit.Percent);
        return $"runs out {TimeText.DayAndTime(runsOut, now)}";
    }

    // "seven_day_breakdown": { "rows": [ { "key": "claude_code", "display_name": "Claude Code", "percent": 99 }, ... ] }
    // The share of this week's use that came from each place. Shown as small tiles under the limits.
    private static List<MetricItem> ReadSources(JsonElement root)
    {
        var items = new List<MetricItem>();
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("seven_day_breakdown", out var breakdown) || breakdown.ValueKind != JsonValueKind.Object ||
            !breakdown.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!row.TryGetProperty("percent", out var share) || share.ValueKind != JsonValueKind.Number || share.GetDouble() < 1) continue;
            if ((Text(row, "display_name") ?? Text(row, "key")) is not { Length: > 0 } name) continue;

            // "Claude Code" becomes "From Code", which fits a half-width tile.
            if (name.StartsWith("Claude ", StringComparison.OrdinalIgnoreCase)) name = name["Claude ".Length..];
            items.Add(new MetricItem("From " + name)
            {
                Key = "from_" + Slug(Text(row, "key") ?? name),
                ValueText = Format.Percent(share.GetDouble()),
                Number = share.GetDouble(),
                Unit = "%",
            });
        }
        return items;
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
            TimeSpan? window = group == "session" ? SessionWindow : group == "weekly" ? WeekWindow : null;
            limits.Add(new Limit(key, label, percent.GetDouble(), Time(item), window));
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
            TimeSpan? window = name == "five_hour" ? SessionWindow : name.StartsWith("seven_day", StringComparison.Ordinal) ? WeekWindow : null;
            limits.Add(new Limit(key, label, utilization.GetDouble(), Time(value), window));
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
        var path = CredentialsPath(config);
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

    private static string CredentialsPath(ClaudeConfig config)
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
