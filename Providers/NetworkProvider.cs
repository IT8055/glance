using Glance.Core;
using Glance.Interop;

namespace Glance.Providers;

/// <summary>
/// Network speed. The totals come from the network adapter, so they include everything. Under them the
/// traffic made by programs is split into internet ("web") and local network ("LAN"), which comes from
/// socket events (see <see cref="SocketTracker"/>) and needs the same Windows permission as the FPS counter.
/// Without that permission the panel shows the totals alone.
/// </summary>
public sealed class NetworkProvider : IMetricProvider, IPausable, IDisposable
{
    private readonly object _gate = new();
    private readonly PdhQuery _query = new();
    private readonly IntPtr _received;
    private readonly IntPtr _sent;
    private SocketTracker? _tracker;
    private SocketRates _rates = new();
    private bool _splitUnavailable;
    private bool _disposed;

    public NetworkProvider(TimeSpan interval)
    {
        RefreshInterval = interval;
        _received = _query.Add(@"\Network Interface(*)\Bytes Received/sec");
        _sent = _query.Add(@"\Network Interface(*)\Bytes Sent/sec");
    }

    public string Title => "Network";
    public string Id => "network";
    public TimeSpan RefreshInterval { get; }

    public async Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        await _query.SampleAsync(ct);

        // A virtual switch reports the same bytes as the physical adapter behind it, so adding adapters up
        // would count traffic twice. The busiest adapter is the honest figure.
        var down = _query.ReadAll(_received);
        var up = _query.ReadAll(_sent).ToDictionary(a => a.Instance, a => a.Value);
        var busiest = down.Select(a => (Down: a.Value, Up: up.GetValueOrDefault(a.Instance)))
            .OrderByDescending(a => a.Down + a.Up)
            .FirstOrDefault();

        var items = new List<MetricItem> { Speed("Down", "down", busiest.Down), Speed("Up", "up", busiest.Up) };

        SocketTracker? tracker;
        lock (_gate)
        {
            // A fetch that was already queued when the section was shut down must not start a new trace.
            if (_disposed) return MetricSnapshot.Info("");
            if (_tracker is null && !_splitUnavailable)
            {
                try
                {
                    _tracker = SocketTracker.Acquire();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
                {
                    _splitUnavailable = true;
                }
            }
            tracker = _tracker;
        }

        if (tracker is not null)
        {
            // Nothing to show on the first look after the widget appears, but the readings keep their places.
            var rates = await _rates.ReadAsync(tracker, ct);
            items.Add(Speed("Web down", "web_down", rates?.Sum(r => r.WebDown)));
            items.Add(Speed("Web up", "web_up", rates?.Sum(r => r.WebUp)));
            items.Add(Speed("LAN down", "lan_down", rates?.Sum(r => r.LanDown)));
            items.Add(Speed("LAN up", "lan_up", rates?.Sum(r => r.LanUp)));
        }

        return new MetricSnapshot { Items = items };
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_tracker is not null) SocketTracker.Release();
            _tracker = null;
            _rates = new SocketRates();
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Pause();
        _query.Dispose();
    }

    // The number is kept in megabits per second, the unit people quote connection speeds in.
    private static MetricItem Speed(string label, string key, double? bytesPerSecond) => new(label)
    {
        Key = key,
        ValueText = bytesPerSecond is { } rate ? Format.BitRate(rate) : Format.Missing,
        Number = bytesPerSecond is { } bytes ? Math.Max(0, bytes) * 8 / 1_000_000 : null,
        Unit = "Mbps",
        Graph = true,
    };
}
