using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Glance.Core;
using Glance.Interop;

namespace Glance.Providers;

/// <summary>
/// Counts network traffic per program, and whether it went to the internet ("web") or the local network
/// ("LAN"), from Windows' socket events (the Winsock provider). Unlike the kernel's network events, a
/// normal account in the Performance Log Users group is allowed to read these.
///
/// Every send and receive a program makes through a socket is logged with its size. Datagram traffic
/// (UDP: games, streaming, QUIC) carries the far address on every event. Connections (TCP) only say
/// where they go when they are opened, so for a connection that was already open when tracking started
/// the tracker goes by what that program's connections are mostly made to.
///
/// What this cannot see is traffic sent by Windows itself rather than by a program's socket, of which the
/// main example is file sharing (SMB). The Network panel's totals come from the adapter and do include it.
///
/// The event layout is not documented. It was worked out by sending known amounts of data and matching
/// the events to them (see "--probe-etw" in the README), and is checked defensively on every event.
/// </summary>
internal sealed class SocketTracker : IDisposable
{
    public enum Scope { Unknown, Loopback, Lan, Web }

    /// <summary>Running byte totals for one program. "Unknown" is traffic on connections opened before tracking began.</summary>
    public sealed class Counts
    {
        public long WebDown, WebUp, LanDown, LanUp, UnknownDown, UnknownUp;

        public Counts Copy() => (Counts)MemberwiseClone();
    }

    private static readonly Guid Winsock = new("E53C6823-7BB8-44BB-90DC-3F86090D48A6"); // Microsoft-Windows-Winsock-AFD
    private static readonly (long Low, long High) WinsockHalves = EtwSession.Halves(Winsock);

    // Payload layout shared by the events used here (64-bit Windows):
    //   0 phase (0 entering a call, 1 leaving it)   8 process object   16 socket object
    // and for the data events:
    //   36 byte count   40 status   44 address length   48 address
    // and for connect and accept:
    //   40 address length   44 address   then, for accept, the new connection's socket object
    private const int Entering = 0;
    private const int Leaving = 1;

    private const int Close = 1001;
    private const int Send = 1003;               // connections, and datagram sockets tied to one address
    private const int Receive = 1004;
    private const int SendTo = 1007;             // datagram with an address
    private const int ReceiveFrom = 1009;
    private const int SendMessage = 1013;        // datagram with an address, the form streaming and QUIC use
    private const int ReceiveMessage = 1015;

    // Events reach this process a little after they are logged. To know when everything logged up to a moment
    // has arrived, the tracker sends itself a tiny datagram at that moment and waits for the event it causes:
    // the session keeps one stream of events in the order they happened (see EtwSession's "busy" mode), so
    // once that marker is in, so is what came before it. The datagram's size, 1 to MarkerSizes bytes, says
    // which marker it is. Measured: the marker arrives within a few milliseconds of asking.
    private const int MarkerSizes = 16;
    private static readonly TimeSpan MarkerPatience = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan Shareable = TimeSpan.FromMilliseconds(250);
    private static readonly int OwnProcessId = Environment.ProcessId;

    private static readonly object SharedGate = new();
    private static SocketTracker? _shared;
    private static int _users;

    /// <summary>The totals per process id, and the moment (a Stopwatch timestamp) they are complete up to.</summary>
    public readonly record struct Reading(Dictionary<int, Counts> Totals, long AsOf);

    private readonly object _gate = new();
    private readonly EtwSession _session;
    private readonly Dictionary<long, int> _owners = [];     // process object -> process id
    private readonly Dictionary<long, Scope> _sockets = [];  // socket object -> where its far end is
    private readonly Dictionary<int, int> _sendDepth = [];   // thread -> how many nested send calls it is inside
    private readonly Dictionary<int, Counts> _counts = [];   // process id -> totals
    private readonly byte[] _address = new byte[28];
    private long _events;

    private readonly object _readGate = new();
    private Task<Reading>? _reading;
    private readonly Socket? _marker;
    private readonly EndPoint? _markerEnd;
    private readonly int _markerPort;
    private readonly byte[] _markerData = new byte[MarkerSizes];
    private readonly byte[] _markerSink = new byte[MarkerSizes];
    private int _markerSequence;
    private int _markerAwaited;                 // the size of the marker a reading is waiting for, 0 for none
    private TaskCompletionSource? _markerSeen;
    private int _reads, _markerMisses;
    private long _waitedTicks;

    private SocketTracker()
    {
        _session = EtwSession.Start("Glance-Sockets", OnEvent, busy: true, new EtwSession.Provider(Winsock));
        try
        {
            // Bound to this PC's own loopback address, so nothing leaves the machine and the firewall is not involved.
            _marker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _marker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _markerEnd = _marker.LocalEndPoint;
            _markerPort = ((IPEndPoint)_markerEnd!).Port;
        }
        catch (SocketException ex)
        {
            Log.Write("Network tracking will be less exact: no marker socket (" + ex.Message + ").");
            _marker?.Dispose();
            _marker = null;
        }
    }

    /// <summary>Events Windows dropped because they came too fast. Above zero means the byte counts are short.</summary>
    public long LostEvents => _session.LostEvents();

    /// <summary>For testing: how many readings there have been, how many gave up waiting for their marker, and the average wait.</summary>
    public (int Reads, int Misses, double AverageWaitMs) Sync
    {
        get
        {
            var reads = Volatile.Read(ref _reads);
            return (reads, Volatile.Read(ref _markerMisses),
                reads == 0 ? 0 : Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _waitedTicks)).TotalMilliseconds / reads);
        }
    }

    /// <summary>
    /// Starts tracking, or joins the tracking another panel already started. Every Acquire needs a Release.
    /// Throws UnauthorizedAccessException if this account may not read the events.
    /// </summary>
    public static SocketTracker Acquire()
    {
        lock (SharedGate)
        {
            _shared ??= new SocketTracker();
            _users++;
            return _shared;
        }
    }

    public static void Release()
    {
        lock (SharedGate)
        {
            if (_users > 0 && --_users == 0)
            {
                _shared?.Dispose();
                _shared = null;
            }
        }
    }

    /// <summary>
    /// Pushes buffered events through, then returns a copy of the totals together with the moment they are
    /// complete up to. Rates must be worked out from that moment, not from when the call returned, because
    /// the wait for the events to arrive varies. Panels asking at about the same time share one reading.
    /// </summary>
    public Task<Reading> ReadAsync(CancellationToken ct)
    {
        Task<Reading> reading;
        lock (_readGate)
        {
            var fresh = _reading is { IsCompleted: false } ||
                (_reading is { IsCompletedSuccessfully: true } done && Stopwatch.GetElapsedTime(done.Result.AsOf) < Shareable);
            reading = fresh ? _reading! : _reading = Task.Run(ReadFreshAsync);
        }
        return reading.WaitAsync(ct);
    }

    private async Task<Reading> ReadFreshAsync()
    {
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var size = 0;
        lock (_gate)
        {
            if (_marker is not null)
            {
                size = _markerAwaited = _markerSequence++ % MarkerSizes + 1;
                _markerSeen = seen;
            }
        }

        var asOf = Stopwatch.GetTimestamp();
        if (size > 0 && !SendMarker(size)) size = 0;
        _session.Flush();

        var arrived = size > 0 && await Task.WhenAny(seen.Task, Task.Delay(MarkerPatience)) == seen.Task;
        lock (_gate) _markerAwaited = 0;

        // Without a marker the only test there is: wait for arrivals to stop.
        for (var i = 0; !arrived && i < 8; i++)
        {
            var count = Interlocked.Read(ref _events);
            await Task.Delay(15);
            if (Interlocked.Read(ref _events) == count) break;
        }

        Interlocked.Increment(ref _reads);
        if (!arrived) Interlocked.Increment(ref _markerMisses);
        Interlocked.Add(ref _waitedTicks, Stopwatch.GetTimestamp() - asOf);

        lock (_gate)
        {
            return new Reading(_counts.ToDictionary(c => c.Key, c => c.Value.Copy()), asOf);
        }
    }

    private bool SendMarker(int size)
    {
        try
        {
            // Markers are sent to this socket itself. Reading the earlier ones back stops them piling up.
            while (_marker!.Available > 0) _marker.Receive(_markerSink);
            _marker.SendTo(_markerData, 0, size, SocketFlags.None, _markerEnd!);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _session.Dispose();
        _marker?.Dispose();
    }

    // Runs on the trace thread for every socket event on the PC, so it does no allocation and no lookups by name.
    private void OnEvent(IntPtr record)
    {
        if (EtwSession.ProviderLow(record) != WinsockHalves.Low || EtwSession.ProviderHigh(record) != WinsockHalves.High) return;

        Interlocked.Increment(ref _events);
        var length = EtwSession.DataLength(record);
        if (length < 24) return;
        var data = EtwSession.Data(record);
        var id = EtwSession.EventId(record);
        var phase = Marshal.ReadInt32(data, 0);
        var process = Marshal.ReadInt64(data, 8);
        var socket = Marshal.ReadInt64(data, 16);

        lock (_gate)
        {
            // A call is entered on the calling program's own thread, which is how the kernel's process
            // object gets matched to a process id. Completions can arrive on any thread.
            if (phase == Entering && EtwSession.ProcessId(record) is > 4 and var caller) _owners[process] = caller;

            switch (id)
            {
                case Close:
                    _sockets.Remove(socket);
                    return;

                case Send:
                {
                    // One send is logged as a call inside a call, each with the byte count. Count it once,
                    // when the outermost call returns. The depth is capped so a lost event cannot leave a
                    // thread stuck thinking it is still inside a call.
                    var thread = EtwSession.ThreadId(record);
                    var depth = _sendDepth.GetValueOrDefault(thread);
                    if (phase == Entering)
                    {
                        _sendDepth[thread] = Math.Min(depth + 1, 2);
                        return;
                    }
                    if (phase != Leaving) return;
                    if (depth > 1)
                    {
                        _sendDepth[thread] = depth - 1;
                        return;
                    }
                    _sendDepth.Remove(thread);
                    if (Bytes(data, length) is { } sent) Add(process, _sockets.GetValueOrDefault(socket), up: true, sent);
                    return;
                }

                case Receive:
                    // Leaving a receive reports what actually arrived, whether the call waited or not.
                    if (phase == Leaving && Bytes(data, length) is { } received)
                        Add(process, _sockets.GetValueOrDefault(socket), up: false, received);
                    return;

                case SendTo or ReceiveFrom or ReceiveMessage when phase == Leaving:
                case SendMessage when phase == Entering: // this one is only ever logged on the way in
                    if (Bytes(data, length) is { } datagram)
                    {
                        var scope = AddressAt(data, length, 44);
                        if (scope != Scope.Unknown) _sockets[socket] = scope;
                        if (scope == Scope.Loopback && id == SendTo && datagram == _markerAwaited && IsMarker(process, data))
                        {
                            _markerAwaited = 0;
                            _markerSeen?.TrySetResult();
                        }
                        Add(process, scope, up: id is SendTo or SendMessage, datagram);
                    }
                    return;

                case >= 1016 and <= 1040:
                {
                    // The connect and accept family. Any of them that carries an address says where a socket leads.
                    var scope = AddressAt(data, length, 40);
                    if (scope == Scope.Unknown) return;
                    _sockets[socket] = scope;

                    // An accept is logged against the listening socket, followed by the socket of the new connection.
                    var after = 44 + Marshal.ReadInt32(data, 40);
                    if (length >= after + 8 && Marshal.ReadInt64(data, after) is var accepted && (ulong)accepted >> 48 == 0xFFFF)
                        _sockets[accepted] = scope;
                    return;
                }
            }
        }
    }

    // True for a datagram this process sent to its own marker socket. The port sits after the address family, high byte first.
    private bool IsMarker(long process, IntPtr data) =>
        _owners.GetValueOrDefault(process) == OwnProcessId &&
        ((Marshal.ReadByte(data, 50) << 8) | Marshal.ReadByte(data, 51)) == _markerPort;

    // The byte count of a data event, or null if the call failed or the event is not shaped as expected.
    private static long? Bytes(IntPtr data, int length)
    {
        if (length < 44 || Marshal.ReadInt32(data, 40) != 0) return null;
        long bytes = (uint)Marshal.ReadInt32(data, 36);
        // A single call cannot move more than this. Anything larger means the layout is not what was assumed.
        return bytes is > 0 and <= 1 << 30 ? bytes : null;
    }

    private void Add(long process, Scope scope, bool up, long bytes)
    {
        if (scope == Scope.Loopback || !_owners.TryGetValue(process, out var processId)) return;
        if (!_counts.TryGetValue(processId, out var counts)) _counts[processId] = counts = new Counts();

        switch (scope, up)
        {
            case (Scope.Web, true): counts.WebUp += bytes; break;
            case (Scope.Web, false): counts.WebDown += bytes; break;
            case (Scope.Lan, true): counts.LanUp += bytes; break;
            case (Scope.Lan, false): counts.LanDown += bytes; break;
            case (_, true): counts.UnknownUp += bytes; break;
            default: counts.UnknownDown += bytes; break;
        }
    }

    // Reads a socket address (its length, then the address) and says where it leads. Unknown if there is
    // not a well-formed IPv4 or IPv6 address at that place.
    private Scope AddressAt(IntPtr data, int length, int lengthOffset)
    {
        if (length < lengthOffset + 4) return Scope.Unknown;
        var size = Marshal.ReadInt32(data, lengthOffset);
        var at = lengthOffset + 4;
        if (size is not (16 or 28) || length < at + size) return Scope.Unknown;

        var family = Marshal.ReadInt16(data, at);
        if (size == 16 && family == 2)
        {
            Marshal.Copy(data + at + 4, _address, 0, 4);
            return AddressScope.Of(_address.AsSpan(0, 4));
        }
        if (size == 28 && family == 23)
        {
            Marshal.Copy(data + at + 8, _address, 0, 16);
            return AddressScope.Of(_address.AsSpan(0, 16));
        }
        return Scope.Unknown;
    }
}

/// <summary>Sorts an address into this PC, the local network, or the internet.</summary>
internal static class AddressScope
{
    public static SocketTracker.Scope Of(ReadOnlySpan<byte> address)
    {
        if (address.Length == 4) return OfV4(address[0], address[1]);
        if (address.Length != 16) return SocketTracker.Scope.Unknown;

        // ::1 is this PC, ::ffff:a.b.c.d is an IPv4 address in IPv6 clothing.
        var zeroPrefix = true;
        for (var i = 0; i < 10; i++) zeroPrefix &= address[i] == 0;
        if (zeroPrefix && address[10] == 0xFF && address[11] == 0xFF) return OfV4(address[12], address[13]);
        if (zeroPrefix && address[10] == 0 && address[11] == 0 && address[12] == 0 && address[13] == 0 && address[14] == 0)
            return address[15] == 1 ? SocketTracker.Scope.Loopback : SocketTracker.Scope.Unknown;

        if (address[0] == 0xFF) return SocketTracker.Scope.Lan;                          // multicast
        if (address[0] == 0xFE && (address[1] & 0xC0) == 0x80) return SocketTracker.Scope.Lan; // fe80::/10 link local
        if ((address[0] & 0xFE) == 0xFC) return SocketTracker.Scope.Lan;                 // fc00::/7 unique local
        return SocketTracker.Scope.Web;
    }

    private static SocketTracker.Scope OfV4(byte a, byte b)
    {
        if (a == 0) return SocketTracker.Scope.Unknown;                                  // "any address": not a real far end
        if (a == 127) return SocketTracker.Scope.Loopback;
        if (a == 10) return SocketTracker.Scope.Lan;
        if (a == 172 && b is >= 16 and <= 31) return SocketTracker.Scope.Lan;
        if (a == 192 && b == 168) return SocketTracker.Scope.Lan;
        if (a == 169 && b == 254) return SocketTracker.Scope.Lan;                        // link local
        if (a >= 224) return SocketTracker.Scope.Lan;                                    // multicast and broadcast
        return SocketTracker.Scope.Web;
    }
}

/// <summary>
/// Turns the tracker's running totals into rates for one panel, and settles the traffic whose
/// destination is not known. Each panel has its own, so they do not disturb each other's sums.
/// </summary>
internal sealed class SocketRates
{
    /// <summary>Bytes per second for one program.</summary>
    public readonly record struct Rate(int ProcessId, double WebDown, double WebUp, double LanDown, double LanUp)
    {
        public double Down => WebDown + LanDown;
        public double Up => WebUp + LanUp;
    }

    private Dictionary<int, SocketTracker.Counts>? _before;
    private long _beforeAt;
    private List<Rate>? _last;
    private Dictionary<int, SocketTracker.Scope> _leaning = [];
    private long _leaningAt;

    /// <summary>Rates since the previous call. Null the first time, when there is nothing to compare with.</summary>
    public async Task<List<Rate>?> ReadAsync(SocketTracker tracker, CancellationToken ct)
    {
        var (now, at) = await tracker.ReadAsync(ct);
        if (_before is not { } before)
        {
            _before = now;
            _beforeAt = at;
            return null;
        }

        // Asked again almost at once (a refresh on top of the regular one): there is nothing new to work out.
        var seconds = Stopwatch.GetElapsedTime(_beforeAt, at).TotalSeconds;
        if (seconds < 0.2) return _last;
        _before = now;
        _beforeAt = at;

        // Connections do not change sides often, so the table of open connections is only re-read every few seconds.
        if (_leaningAt == 0 || Stopwatch.GetElapsedTime(_leaningAt, at) > TimeSpan.FromSeconds(4))
        {
            _leaning = Leanings();
            _leaningAt = at;
        }

        var rates = new List<Rate>();
        foreach (var (processId, counts) in now)
        {
            var was = before.GetValueOrDefault(processId) ?? new SocketTracker.Counts();
            double webDown = counts.WebDown - was.WebDown, webUp = counts.WebUp - was.WebUp;
            double lanDown = counts.LanDown - was.LanDown, lanUp = counts.LanUp - was.LanUp;
            double unknownDown = counts.UnknownDown - was.UnknownDown, unknownUp = counts.UnknownUp - was.UnknownUp;

            // Traffic on a connection opened before tracking began goes by what the program mostly connects to.
            // A program whose only connections are to this PC itself (tools talking to each other) is left out.
            switch (_leaning.GetValueOrDefault(processId))
            {
                case SocketTracker.Scope.Web: webDown += unknownDown; webUp += unknownUp; break;
                case SocketTracker.Scope.Lan: lanDown += unknownDown; lanUp += unknownUp; break;
            }

            if (webDown + webUp + lanDown + lanUp <= 0) continue;
            rates.Add(new Rate(processId, webDown / seconds, webUp / seconds, lanDown / seconds, lanUp / seconds));
        }
        return _last = rates;
    }

    // For each program with connections open: whether most of them lead to the internet or to the local network.
    private static Dictionary<int, SocketTracker.Scope> Leanings()
    {
        var tally = new Dictionary<int, (int Web, int Lan)>();
        foreach (var connection in TcpTable.Connections())
        {
            var scope = AddressScope.Of(connection.Remote);
            if (scope is not (SocketTracker.Scope.Web or SocketTracker.Scope.Lan)) continue;
            var t = tally.GetValueOrDefault(connection.ProcessId);
            tally[connection.ProcessId] = scope == SocketTracker.Scope.Web ? (t.Web + 1, t.Lan) : (t.Web, t.Lan + 1);
        }
        return tally.ToDictionary(t => t.Key, t => t.Value.Lan > t.Value.Web ? SocketTracker.Scope.Lan : SocketTracker.Scope.Web);
    }
}

/// <summary>
/// The five programs sending and receiving the most over the network, or over just the internet or just the
/// local network, averaged over the last few seconds so that the list can be read (see <see cref="TopList"/>).
/// </summary>
public sealed class NetAppsProvider(NetAppsProvider.Traffic traffic, TimeSpan interval) : IMetricProvider, IPausable, IDisposable
{
    public enum Traffic { All, Web, Lan }

    private const int Shown = 5;
    private const double WorthListing = 1000; // bytes a second: below this a program is just ticking over
    private static readonly TimeSpan Average = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(12);

    private readonly object _gate = new();
    private readonly Dictionary<int, string> _names = [];
    private SocketTracker? _tracker;
    private SocketRates _rates = new();
    private TopList _top = NewList();
    private List<SocketRates.Rate>? _lastRates;
    private List<TopList.Entry> _entries = [];
    private bool _disposed;

    public string Title => traffic switch { Traffic.Web => "Web by app", Traffic.Lan => "LAN by app", _ => "Network by app" };
    public string Id => traffic switch { Traffic.Web => "webapps", Traffic.Lan => "lanapps", _ => "netapps" };
    public int ReservedRows => Shown;

    // Rates over less than a second jump about too much to read.
    public TimeSpan RefreshInterval => interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : interval;

    public async Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        SocketTracker? tracker;
        try
        {
            lock (_gate)
            {
                if (_disposed) return MetricSnapshot.Info("");
                tracker = _tracker ??= SocketTracker.Acquire();
            }
        }
        catch (UnauthorizedAccessException)
        {
            return MetricSnapshot.Error("Per-program traffic needs the same Windows permission as the FPS counter. Turn it on in Settings.", TimeSpan.FromSeconds(60));
        }
        catch (InvalidOperationException ex)
        {
            Log.Write("Network tracking could not start: " + ex.Message);
            return MetricSnapshot.Error("Network tracking could not start. See glance.log.", TimeSpan.FromSeconds(60));
        }

        if (await _rates.ReadAsync(tracker, ct) is not { } rates) return MetricSnapshot.Info("Measuring…");

        // Asked again almost at once, the rates are the same ones as last time and must not be counted twice.
        if (!ReferenceEquals(rates, _lastRates))
        {
            _lastRates = rates;
            // Several processes of one program are added together, as in the other lists.
            var programs = rates.GroupBy(r => NameOf(r.ProcessId), StringComparer.OrdinalIgnoreCase)
                .Select(g => new TopList.Entry(g.Key, g.Sum(Down), g.Sum(Up)));
            _entries = _top.Update(Stopwatch.GetElapsedTime(0), programs);
        }
        if (_entries.Count == 0)
        {
            return MetricSnapshot.Info(traffic switch
            {
                Traffic.Web => "No program is using the internet much.",
                Traffic.Lan => "No program is using the local network much.",
                _ => "No program is using the network much.",
            });
        }

        return new MetricSnapshot
        {
            Items = _entries.Select(e => new MetricItem(e.Key)
            {
                ValueText = $"↓ {Format.BitRate(e.Down)}  ↑ {Format.BitRate(e.Up)}",
                FullWidth = true,
            }).ToList(),
        };
    }

    private double Down(SocketRates.Rate r) => traffic switch { Traffic.Web => r.WebDown, Traffic.Lan => r.LanDown, _ => r.Down };
    private double Up(SocketRates.Rate r) => traffic switch { Traffic.Web => r.WebUp, Traffic.Lan => r.LanUp, _ => r.Up };

    private static TopList NewList() => new(Shown, WorthListing, Average, Hold);

    public void Pause()
    {
        lock (_gate)
        {
            if (_tracker is not null) SocketTracker.Release();
            _tracker = null;
            _rates = new SocketRates();
            _top = NewList();
            _lastRates = null;
            _entries = [];
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Pause();
    }

    private string NameOf(int processId)
    {
        if (_names.TryGetValue(processId, out var known)) return known;
        if (_names.Count > 500) _names.Clear();
        try
        {
            using var process = Process.GetProcessById(processId);
            return _names[processId] = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "closed program"; // gone before it could be asked its name
        }
    }
}
