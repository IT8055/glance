using System.Net.NetworkInformation;
using System.Net.Sockets;
using Glance.Core;

namespace Glance.Providers;

/// <summary>
/// Latency, jitter and packet loss to a host the user chooses, from one small ping per refresh.
/// Off unless switched on, because it talks to that host for as long as the section is showing.
/// </summary>
public sealed class PingProvider(string host, TimeSpan interval) : IMetricProvider, IDisposable
{
    private const int Remembered = 30;

    private readonly Ping _ping = new();
    private readonly Queue<long?> _results = new(); // milliseconds, or null for a lost ping

    public string Title => "Ping";
    public string Id => "ping";

    // Pinging faster than once a second tells you nothing more and is impolite to the host.
    public TimeSpan RefreshInterval => interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : interval;

    public async Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        long? latest = null;
        try
        {
            var reply = await _ping.SendPingAsync(host, 1000);
            if (reply.Status == IPStatus.Success) latest = reply.RoundtripTime;
        }
        catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException)
        {
            // Name not found, no route, network down: all count as a lost ping.
        }

        _results.Enqueue(latest);
        while (_results.Count > Remembered) _results.Dequeue();

        var answered = _results.Where(r => r is not null).Select(r => (double)r!.Value).ToList();
        var loss = (_results.Count - answered.Count) * 100.0 / _results.Count;

        var items = new List<MetricItem>
        {
            latest is { } ms
                ? new MetricItem("Now") { Key = "now", ValueText = $"{ms} ms", Number = ms, Unit = "ms", Graph = true }
                : new MetricItem("Now") { Key = "now", ValueText = "lost", Level = MetricLevel.Critical },
        };
        if (answered.Count > 0)
        {
            items.Add(new MetricItem("Average") { Key = "average", ValueText = Format.Whole(answered.Average()) + " ms", Number = answered.Average(), Unit = "ms" });

            // Jitter: how much consecutive pings differ from each other on average.
            var jitter = answered.Count > 1 ? answered.Zip(answered.Skip(1), (a, b) => Math.Abs(a - b)).Average() : 0;
            items.Add(new MetricItem("Jitter") { Key = "jitter", ValueText = Format.Whole(jitter) + " ms", Number = jitter, Unit = "ms" });
        }
        items.Add(new MetricItem("Loss")
        {
            Key = "loss", ValueText = Format.Percent(loss), Number = loss, Unit = "%",
            Level = loss >= 5 ? MetricLevel.Critical : loss >= 1 ? MetricLevel.Warning : MetricLevel.Normal,
        });

        return new MetricSnapshot { Items = items, Subtitle = host };
    }

    public void Dispose() => _ping.Dispose();
}

/// <summary>
/// Any readings the user has picked from a running monitoring tool (HWiNFO or MSI Afterburner):
/// fan speeds, motherboard and drive temperatures, voltages, whatever that tool publishes.
/// </summary>
public sealed class SensorsProvider(IReadOnlyList<string> chosen, TimeSpan interval) : IMetricProvider
{
    public string Title => "Sensors";
    public string Id => "sensors";
    public TimeSpan RefreshInterval => interval;

    /// <summary>How a reading is remembered in settings: the sensor it belongs to and its label.</summary>
    internal static string NameOf(ExternalSensors.Reading reading) => $"{reading.Sensor}|{reading.Label}";

    public Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        if (chosen.Count == 0)
            return Task.FromResult(MetricSnapshot.Info("Pick the readings to show here in Settings, under Data."));

        var current = ExternalSensors.Current;
        if (current.Source.Length == 0)
            return Task.FromResult(MetricSnapshot.Info("Start HWiNFO or MSI Afterburner to see these readings."));

        var byName = new Dictionary<string, ExternalSensors.Reading>();
        foreach (var reading in current.Readings) byName.TryAdd(NameOf(reading), reading);

        var items = new List<MetricItem>();
        foreach (var name in chosen)
        {
            if (!byName.TryGetValue(name, out var reading) || !double.IsFinite(reading.Value)) continue;
            items.Add(new MetricItem(reading.Label)
            {
                Key = MetricItem.Slug(name),
                ValueText = Format.WithUnit(reading.Value, reading.Unit),
                Number = reading.Value,
                Unit = reading.Unit,
                Graph = true,
                // A long label would be cut to nothing in a half-width tile.
                FullWidth = reading.Label.Length > 13,
            });
        }

        return Task.FromResult(items.Count == 0
            ? MetricSnapshot.Info($"{current.Source} is not publishing the readings picked in Settings.", current.Source)
            : new MetricSnapshot { Items = items, Subtitle = current.Source });
    }
}
