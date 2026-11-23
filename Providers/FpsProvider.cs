using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Glance.Core;
using Glance.Interop;

namespace Glance.Providers;

/// <summary>
/// Frames per second of the app in front, measured the way PresentMon and the Xbox Game Bar do it:
/// by counting the "present" events Windows logs each time an app hands a finished frame to the screen.
/// Covers Direct3D 9, 10, 11 and 12. Nothing is injected into the game.
///
/// Needs permission to read those events (see <see cref="EtwSession"/>). The section explains itself if it lacks it.
/// </summary>
public sealed class FpsProvider(TimeSpan interval) : IMetricProvider, IPausable, IDisposable
{
    private readonly Dictionary<int, string> _names = [];
    private PresentTracker? _tracker;

    public string Title => "FPS";
    public TimeSpan RefreshInterval => interval;

    /// <summary>Every app seen rendering at the last fetch, not just the one shown. For the "--probe" diagnostics.</summary>
    internal IReadOnlyList<string> LastApps { get; private set; } = [];

    public async Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        // Held in a local: Pause can clear the field from another thread while this fetch is still running.
        var tracker = _tracker;
        if (tracker is null)
        {
            try
            {
                _tracker = tracker = new PresentTracker();
            }
            catch (UnauthorizedAccessException)
            {
                return MetricSnapshot.Error("FPS needs permission to read frame events. Turn it on in Settings.", TimeSpan.FromSeconds(60));
            }
            catch (InvalidOperationException ex)
            {
                Log.Write("FPS tracking could not start: " + ex.Message);
                return MetricSnapshot.Error("FPS tracking could not start. See glance.log.", TimeSpan.FromSeconds(60));
            }
        }

        var apps = await tracker.ReadAsync(ct);
        apps.RemoveAll(a => a.ProcessId == Environment.ProcessId);
        LastApps = apps.Select(a => $"{NameOf(a.ProcessId) ?? "?"} (pid {a.ProcessId}): {a.Fps:0.0} fps, average {a.Average:0.0}, 1% low {a.OnePercentLow:0.0}").ToList();

        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var foreground);
        var foregroundName = NameOf((int)foreground);

        // The app in front, or a helper process of it (browsers draw from a separate GPU process),
        // or failing that whatever is clearly rendering, such as a game on another monitor.
        var target = apps.FirstOrDefault(a => a.ProcessId == foreground)
            ?? apps.Where(a => foregroundName is not null && NameOf(a.ProcessId) == foregroundName).MaxBy(a => a.Fps)
            ?? apps.Where(a => a.Fps >= 20).MaxBy(a => a.Fps);

        if (target is null)
            return MetricSnapshot.Info("Nothing is drawing frames right now.", foregroundName);

        return new MetricSnapshot
        {
            Subtitle = NameOf(target.ProcessId),
            Items =
            [
                new MetricItem("Now") { ValueText = Whole(target.Fps) },
                new MetricItem("Frame time") { ValueText = target.FrameTimeMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms" },
                new MetricItem("Average") { ValueText = Whole(target.Average) },
                new MetricItem("1% low") { ValueText = Whole(target.OnePercentLow) },
            ],
        };
    }

    public void Pause()
    {
        _tracker?.Dispose();
        _tracker = null;
        _names.Clear();
    }

    public void Dispose() => Pause();

    private static string Whole(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);

    private string? NameOf(int processId)
    {
        if (processId <= 0) return null;
        if (_names.TryGetValue(processId, out var known)) return known;
        try
        {
            using var process = Process.GetProcessById(processId);
            return _names[processId] = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null; // already exited
        }
    }
}

/// <summary>Records when each app presents a frame and turns that into frame rate statistics.</summary>
internal sealed class PresentTracker : IDisposable
{
    /// <param name="Fps">Rate over the last second.</param>
    /// <param name="Average">Rate over the tracked window (up to 30 seconds).</param>
    /// <param name="OnePercentLow">Rate implied by the slowest 1% of frames in that window: the stutter figure.</param>
    public sealed record App(int ProcessId, double Fps, double Average, double OnePercentLow, double FrameTimeMs);

    private const long Second = 10_000_000; // event timestamps are in 100 ns units
    private const long Window = 30 * Second;

    private static readonly Guid DxgiId = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9"); // Microsoft-Windows-DXGI
    private static readonly Guid D3D9Id = new("783ACA0A-790E-4D7F-8451-AA850511C6B9"); // Microsoft-Windows-D3D9
    private static readonly (long Low, long High) Dxgi = EtwSession.Halves(DxgiId);
    private static readonly (long Low, long High) D3D9 = EtwSession.Halves(D3D9Id);
    private const int DxgiPresentStart = 42;
    private const int D3D9PresentStart = 1;

    private sealed class FrameLog
    {
        public readonly Queue<long> Times = new();
        public long Last;
    }

    private readonly object _gate = new();
    // One log per swap chain: an app with several windows presents each one separately.
    private readonly Dictionary<(int ProcessId, long SwapChain), FrameLog> _logs = [];
    private readonly EtwSession _session;

    public PresentTracker()
    {
        _session = EtwSession.Start("Glance-Frames", OnEvent, new EtwSession.Provider(DxgiId), new EtwSession.Provider(D3D9Id));
    }

    public async Task<List<App>> ReadAsync(CancellationToken ct)
    {
        _session.Flush();
        await Task.Delay(40, ct);

        var now = DateTime.UtcNow.ToFileTimeUtc();
        var best = new Dictionary<int, App>();
        lock (_gate)
        {
            foreach (var key in _logs.Keys.ToList())
            {
                var log = _logs[key];
                if (now - log.Last > Window)
                {
                    _logs.Remove(key);
                    continue;
                }
                // No frame for two seconds: the app is paused, minimised or gone. Not "0 fps", just not rendering.
                if (now - log.Last > 2 * Second) continue;

                if (Measure(key.ProcessId, log) is { } app && (!best.TryGetValue(app.ProcessId, out var current) || app.Fps > current.Fps))
                    best[app.ProcessId] = app;
            }
        }
        return best.Values.ToList();
    }

    public void Dispose() => _session.Dispose();

    private static App? Measure(int processId, FrameLog log)
    {
        var times = log.Times.ToArray();
        if (times.Length < 3) return null;

        var last = times[^1];
        var firstRecent = Array.FindIndex(times, t => t > last - Second);
        var recentFrames = times.Length - firstRecent;
        // Frames divided by the time they actually span, so a steady 60 reads as 60 rather than 59 or 61.
        var recentSpan = last - times[firstRecent];
        var fps = recentFrames >= 2 && recentSpan > 0 ? (recentFrames - 1) * (double)Second / recentSpan : recentFrames;
        if (fps <= 0) return null;

        var span = last - times[0];
        var average = span > 0 ? (times.Length - 1) * (double)Second / span : fps;

        var frameTimes = new long[times.Length - 1];
        for (var i = 1; i < times.Length; i++) frameTimes[i - 1] = times[i] - times[i - 1];
        Array.Sort(frameTimes);
        var worstCount = Math.Max(1, frameTimes.Length / 100);
        double worstTotal = 0;
        for (var i = frameTimes.Length - worstCount; i < frameTimes.Length; i++) worstTotal += frameTimes[i];
        var low = worstTotal > 0 ? worstCount * (double)Second / worstTotal : fps;

        return new App(processId, fps, average, low, 1000 / fps);
    }

    // Runs on the trace thread for every event from the two graphics providers.
    private void OnEvent(IntPtr record)
    {
        var low = EtwSession.ProviderLow(record);
        var high = EtwSession.ProviderHigh(record);
        var id = EtwSession.EventId(record);

        var isDxgi = low == Dxgi.Low && high == Dxgi.High && id == DxgiPresentStart;
        var isD3D9 = low == D3D9.Low && high == D3D9.High && id == D3D9PresentStart;
        if (!isDxgi && !isD3D9) return;

        // Payload starts with the swap chain pointer, then the present flags.
        var pointerSize = EtwSession.Is32Bit(record) ? 4 : 8;
        long swapChain = 0;
        if (EtwSession.DataLength(record) >= pointerSize + 4)
        {
            var data = EtwSession.Data(record);
            swapChain = pointerSize == 8 ? Marshal.ReadInt64(data) : Marshal.ReadInt32(data);
            // DXGI_PRESENT_TEST only asks "could I present?" and shows nothing, so it is not a frame.
            if (isDxgi && (Marshal.ReadInt32(data, pointerSize) & 1) != 0) return;
        }

        var time = EtwSession.Timestamp(record);
        var key = (EtwSession.ProcessId(record), swapChain);
        lock (_gate)
        {
            if (!_logs.TryGetValue(key, out var log)) _logs[key] = log = new FrameLog();
            log.Times.Enqueue(time);
            log.Last = time;
            while (log.Times.Count > 0 && log.Times.Peek() < time - Window) log.Times.Dequeue();
        }
    }
}
