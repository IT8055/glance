using System.Runtime.InteropServices;
using Glance.Core;
using Glance.Interop;

namespace Glance.Providers;

/// <summary>
/// Network speed, split into internet ("web") and local network ("LAN") traffic when Windows allows it.
///
/// The split comes from kernel network events, which say which address every packet went to or came
/// from. That needs the same permission as the FPS counter (see <see cref="EtwSession"/>). Without it
/// the section falls back to plain download and upload totals from the network adapter counters.
/// </summary>
public sealed class NetworkProvider : IMetricProvider, IPausable, IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly IntPtr _received;
    private readonly IntPtr _sent;
    private TrafficTracker? _tracker;
    private bool _splitUnavailable;

    public NetworkProvider(TimeSpan interval)
    {
        RefreshInterval = interval;
        _received = _query.Add(@"\Network Interface(*)\Bytes Received/sec");
        _sent = _query.Add(@"\Network Interface(*)\Bytes Sent/sec");
    }

    public string Title => "Network";
    public TimeSpan RefreshInterval { get; }

    public async Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        if (_tracker is null && !_splitUnavailable)
        {
            try
            {
                _tracker = new TrafficTracker();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            {
                _splitUnavailable = true;
                Log.Write("Web and LAN traffic split is unavailable, showing totals instead: " + ex.Message);
            }
        }

        if (_tracker is { } tracker)
        {
            if (await tracker.ReadAsync(ct) is { } rates)
            {
                return new MetricSnapshot
                {
                    Items =
                    [
                        new MetricItem("Web down") { ValueText = Format.BitRate(rates.WebDown) },
                        new MetricItem("Web up") { ValueText = Format.BitRate(rates.WebUp) },
                        new MetricItem("LAN down") { ValueText = Format.BitRate(rates.LanDown) },
                        new MetricItem("LAN up") { ValueText = Format.BitRate(rates.LanUp) },
                    ],
                };
            }
            return MetricSnapshot.Info("Measuring…");
        }

        await _query.SampleAsync(ct);

        // A virtual switch reports the same bytes as the physical adapter behind it, so adding adapters up
        // would count traffic twice. The busiest adapter is the honest figure.
        var down = _query.ReadAll(_received);
        var up = _query.ReadAll(_sent).ToDictionary(a => a.Instance, a => a.Value);
        var busiest = down.Select(a => (Down: a.Value, Up: up.GetValueOrDefault(a.Instance)))
            .OrderByDescending(a => a.Down + a.Up)
            .FirstOrDefault();

        return new MetricSnapshot
        {
            Items =
            [
                new MetricItem("Down") { ValueText = Format.BitRate(busiest.Down) },
                new MetricItem("Up") { ValueText = Format.BitRate(busiest.Up) },
            ],
        };
    }

    public void Pause()
    {
        _tracker?.Dispose();
        _tracker = null;
    }

    public void Dispose()
    {
        Pause();
        _query.Dispose();
    }
}

/// <summary>Adds up bytes per direction and destination from kernel network events.</summary>
internal sealed class TrafficTracker : IDisposable
{
    public readonly record struct Rates(double WebDown, double WebUp, double LanDown, double LanUp);

    // Microsoft-Windows-Kernel-Network. Keywords 0x10 and 0x20 are IPv4 and IPv6.
    private static readonly (long Low, long High) KernelNetwork = EtwSession.Halves(new Guid("7DD42A49-5329-4832-8DFD-43D979153A88"));

    private enum Scope { Loopback, Local, Public }

    private readonly EtwSession _session;
    private readonly byte[] _a = new byte[16];
    private readonly byte[] _b = new byte[16];
    private long _webDown, _webUp, _lanDown, _lanUp;
    private (long WebDown, long WebUp, long LanDown, long LanUp, DateTime At)? _previous;

    public TrafficTracker()
    {
        _session = EtwSession.Start("Glance-Network", OnEvent,
            new EtwSession.Provider(new Guid("7DD42A49-5329-4832-8DFD-43D979153A88"), Level: 4, Keywords: 0x30));
    }

    /// <summary>Bytes per second since the previous call. Null on the first call, when there is nothing to compare with.</summary>
    public async Task<Rates?> ReadAsync(CancellationToken ct)
    {
        _session.Flush();
        await Task.Delay(40, ct);

        var now = (Interlocked.Read(ref _webDown), Interlocked.Read(ref _webUp), Interlocked.Read(ref _lanDown), Interlocked.Read(ref _lanUp), DateTime.UtcNow);
        var before = _previous;
        _previous = now;
        if (before is not { } b) return null;

        var seconds = (now.Item5 - b.At).TotalSeconds;
        if (seconds <= 0.05) return null;
        return new Rates((now.Item1 - b.WebDown) / seconds, (now.Item2 - b.WebUp) / seconds,
                         (now.Item3 - b.LanDown) / seconds, (now.Item4 - b.LanUp) / seconds);
    }

    public void Dispose() => _session.Dispose();

    // Runs on the trace thread, once per send or receive, so it stays allocation free.
    private void OnEvent(IntPtr record)
    {
        if (EtwSession.ProviderLow(record) != KernelNetwork.Low || EtwSession.ProviderHigh(record) != KernelNetwork.High) return;

        bool sending, v6;
        switch (EtwSession.EventId(record))
        {
            case 10 or 42: sending = true; v6 = false; break;   // TCP, UDP send over IPv4
            case 11 or 43: sending = false; v6 = false; break;  // TCP, UDP receive over IPv4
            case 26 or 58: sending = true; v6 = true; break;    // the same over IPv6
            case 27 or 59: sending = false; v6 = true; break;
            default: return;
        }

        // Payload: process id (4), size (4), then the two addresses, 4 bytes each for IPv4 or 16 for IPv6.
        var length = v6 ? 16 : 4;
        if (EtwSession.DataLength(record) < 8 + length * 2) return;
        var data = EtwSession.Data(record);
        long size = (uint)Marshal.ReadInt32(data, 4);
        Marshal.Copy(data + 8, _a, 0, length);
        Marshal.Copy(data + 8 + length, _b, 0, length);

        var first = Classify(_a, v6);
        var second = Classify(_b, v6);
        if (first == Scope.Loopback || second == Scope.Loopback) return;

        // One end is this PC. If the other end is on the local network too, or shares our IPv6 subnet, it is LAN.
        var lan = (first == Scope.Local && second == Scope.Local) || (v6 && _a.AsSpan(0, 8).SequenceEqual(_b.AsSpan(0, 8)));
        if (lan) Interlocked.Add(ref sending ? ref _lanUp : ref _lanDown, size);
        else Interlocked.Add(ref sending ? ref _webUp : ref _webDown, size);
    }

    private static Scope Classify(byte[] a, bool v6)
    {
        if (!v6) return ClassifyV4(a[0], a[1]);

        // ::1 is loopback, ::ffff:a.b.c.d is an IPv4 address in IPv6 clothing.
        var zeroPrefix = true;
        for (var i = 0; i < 10; i++) zeroPrefix &= a[i] == 0;
        if (zeroPrefix && a[10] == 0xFF && a[11] == 0xFF) return ClassifyV4(a[12], a[13]);
        if (zeroPrefix && a[10] == 0 && a[11] == 0 && a[12] == 0 && a[13] == 0 && a[14] == 0 && a[15] == 1) return Scope.Loopback;

        if (a[0] == 0xFF) return Scope.Local;                       // multicast
        if (a[0] == 0xFE && (a[1] & 0xC0) == 0x80) return Scope.Local; // fe80::/10 link local
        if ((a[0] & 0xFE) == 0xFC) return Scope.Local;              // fc00::/7 unique local
        return Scope.Public;
    }

    private static Scope ClassifyV4(byte a, byte b)
    {
        if (a == 127) return Scope.Loopback;
        if (a == 10) return Scope.Local;
        if (a == 172 && b is >= 16 and <= 31) return Scope.Local;
        if (a == 192 && b == 168) return Scope.Local;
        if (a == 169 && b == 254) return Scope.Local;               // link local
        if (a >= 224) return Scope.Local;                           // multicast and broadcast
        return Scope.Public;
    }
}
