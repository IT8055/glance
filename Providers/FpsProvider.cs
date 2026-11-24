using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Glance.Core;
using Glance.Interop;

namespace Glance.Providers;

/// <summary>
/// Frames per second of the app in front, measured the way PresentMon and the Xbox Game Bar do it:
/// by counting the "present" events Windows logs each time an app hands a finished frame to the screen.
/// Direct3D 9 to 12 are counted from the graphics runtime. OpenGL and Vulkan, which bypass it, are counted
/// from the graphics kernel. Nothing is injected into the game.
///
/// Needs permission to read those events (see <see cref="EtwSession"/>). The section explains itself if it lacks it.
/// </summary>
public sealed class FpsProvider(TimeSpan interval) : IMetricProvider, IPausable, IDisposable
{
    /// <param name="FrameTimesMs">Time between consecutive frames over the whole recording.</param>
    public sealed record Capture(string? App, double[] FrameTimesMs);

    private readonly object _gate = new();
    private readonly Dictionary<int, string> _names = [];
    private readonly Dictionary<int, int> _captureTargets = [];
    private PresentTracker? _tracker;
    private bool _capturing;
    private bool _disposed;

    public string Title => "FPS";
    public string Id => "fps";
    public TimeSpan RefreshInterval => interval;

    /// <summary>Every app seen rendering at the last fetch, not just the one shown. For the "--probe" diagnostics.</summary>
    internal IReadOnlyList<string> LastApps { get; private set; } = [];

    public async Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        // Held in a local: Pause can clear the field from another thread while this fetch is still running.
        PresentTracker? tracker;
        try
        {
            lock (_gate)
            {
                // A fetch that was already queued when the section was shut down must not start a new trace:
                // nothing would ever stop it again.
                if (_disposed) return MetricSnapshot.Info("");
                if (_tracker is null)
                {
                    _tracker = new PresentTracker();
                    if (_capturing) _tracker.BeginCapture();
                }
                tracker = _tracker;
            }
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

        // With nothing to measure the readings stay, as dashes, so the panel does not change size each time
        // an app that draws frames comes to the front or leaves it.
        if (target is null)
        {
            return new MetricSnapshot
            {
                Subtitle = foregroundName is null ? "no frames" : foregroundName + ", no frames",
                Items = Readings(null, null, null, null),
            };
        }

        if (_capturing)
        {
            lock (_captureTargets) _captureTargets[target.ProcessId] = _captureTargets.GetValueOrDefault(target.ProcessId) + 1;
        }

        return new MetricSnapshot
        {
            Subtitle = NameOf(target.ProcessId),
            Items = Readings(target.Fps, target.FrameTimeMs, target.Average, target.OnePercentLow),
        };
    }

    private static MetricItem[] Readings(double? fps, double? frameTimeMs, double? average, double? low)
    {
        return
        [
            Reading("Now", "now", fps, "fps") with { Graph = true },
            Reading("Frame time", "frametime", frameTimeMs, "ms") with
            {
                ValueText = frameTimeMs is { } ms ? ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms" : Format.Missing,
            },
            Reading("Average", "average", average, "fps"),
            Reading("1% low", "low1", low, "fps"),
        ];

        static MetricItem Reading(string label, string key, double? value, string unit) =>
            new(label) { Key = key, ValueText = value is { } v ? Format.Whole(v) : Format.Missing, Number = value, Unit = unit };
    }

    /// <summary>Starts keeping every frame time, for a benchmark. Frames are attributed when the capture ends.</summary>
    public void BeginCapture()
    {
        lock (_captureTargets) _captureTargets.Clear();
        _capturing = true;
        lock (_gate) _tracker?.BeginCapture();
    }

    /// <summary>Ends the capture and returns the frame times of the app that was measured for most of it.</summary>
    public Capture EndCapture()
    {
        _capturing = false;
        int? processId;
        lock (_captureTargets)
        {
            processId = _captureTargets.Count == 0 ? null : _captureTargets.MaxBy(t => t.Value).Key;
        }

        double[] frames;
        lock (_gate) frames = _tracker?.EndCapture(processId ?? 0) ?? [];
        return new Capture(processId is { } id ? NameOf(id) : null, frames);
    }

    public void Pause()
    {
        // A benchmark keeps counting frames even if the widget is hidden while it runs.
        if (_capturing) return;
        lock (_gate)
        {
            _tracker?.Dispose();
            _tracker = null;
        }
        lock (_names) _names.Clear();
    }

    public void Dispose()
    {
        _capturing = false;
        lock (_gate) _disposed = true;
        Pause();
    }

    private string? NameOf(int processId)
    {
        if (processId <= 0) return null;
        lock (_names)
        {
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

    private static readonly Guid DxgiId = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");    // Microsoft-Windows-DXGI
    private static readonly Guid D3D9Id = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");    // Microsoft-Windows-D3D9
    private static readonly Guid KernelId = new("802EC45A-1E99-4B83-9920-87C98277BA9D");  // Microsoft-Windows-DxgKrnl
    private static readonly (long Low, long High) Dxgi = EtwSession.Halves(DxgiId);
    private static readonly (long Low, long High) D3D9 = EtwSession.Halves(D3D9Id);
    private static readonly (long Low, long High) Kernel = EtwSession.Halves(KernelId);
    private const int DxgiPresentStart = 42;
    private const int D3D9PresentStart = 1;
    // "Present, Info": logged once for every frame any app presents, whichever graphics API it used.
    private const int KernelPresent = 184;
    private const ulong KernelPresentKeyword = 0x8000000;

    /// <param name="FromKernel">Counted in the graphics kernel, so per process rather than per swap chain.</param>
    private readonly record struct Source(int ProcessId, long SwapChain, bool FromKernel);

    private sealed class FrameLog
    {
        public readonly Queue<long> Times = new();
        public long Last;
    }

    private readonly object _gate = new();
    private readonly Dictionary<Source, FrameLog> _logs = [];
    private readonly EtwSession _session;
    private Dictionary<Source, List<long>>? _capture;

    public PresentTracker()
    {
        _session = EtwSession.Start("Glance-Frames", OnEvent,
            new EtwSession.Provider(DxgiId),
            new EtwSession.Provider(D3D9Id, Optional: true),
            new EtwSession.Provider(KernelId, Keywords: KernelPresentKeyword, Optional: true));
    }

    public async Task<List<App>> ReadAsync(CancellationToken ct)
    {
        _session.Flush();
        await Task.Delay(40, ct);

        var now = DateTime.UtcNow.ToFileTimeUtc();
        var best = new Dictionary<int, (App App, bool FromKernel)>();
        lock (_gate)
        {
            foreach (var source in _logs.Keys.ToList())
            {
                var log = _logs[source];
                if (now - log.Last > Window)
                {
                    _logs.Remove(source);
                    continue;
                }
                // No frame for two seconds: the app is paused, minimised or gone. Not "0 fps", just not rendering.
                if (now - log.Last > 2 * Second) continue;
                if (Measure(source.ProcessId, log.Times.ToArray()) is not { } app) continue;

                // The graphics runtime sees each swap chain separately, which is more exact for apps with several
                // windows. The kernel count is the fallback for OpenGL and Vulkan, which the runtime never sees.
                var better = !best.TryGetValue(app.ProcessId, out var current)
                    || (current.FromKernel && !source.FromKernel)
                    || (current.FromKernel == source.FromKernel && app.Fps > current.App.Fps);
                if (better) best[app.ProcessId] = (app, source.FromKernel);
            }
        }
        return best.Values.Select(b => b.App).ToList();
    }

    /// <summary>From now on every frame time is kept, not just the last 30 seconds.</summary>
    public void BeginCapture()
    {
        lock (_gate) _capture = [];
    }

    /// <summary>Stops keeping frames and returns the frame times, in milliseconds, of one process.</summary>
    public double[] EndCapture(int processId)
    {
        _session.Flush();
        Dictionary<Source, List<long>>? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
        }
        if (capture is null) return [];

        var mine = capture.Where(c => c.Key.ProcessId == processId).ToList();
        var chosen = mine.Where(c => !c.Key.FromKernel).OrderByDescending(c => c.Value.Count).FirstOrDefault().Value
                  ?? mine.Where(c => c.Key.FromKernel).Select(c => c.Value).FirstOrDefault();
        if (chosen is null || chosen.Count < 2) return [];

        var frames = new double[chosen.Count - 1];
        for (var i = 1; i < chosen.Count; i++) frames[i - 1] = (chosen[i] - chosen[i - 1]) / 10_000.0;
        return frames;
    }

    public void Dispose() => _session.Dispose();

    private static App? Measure(int processId, long[] times)
    {
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
        return new App(processId, fps, average, FrameStats.LowFps(frameTimes, 0.01, Second), 1000 / fps);
    }

    // Runs on the trace thread for every event from the graphics providers.
    private void OnEvent(IntPtr record)
    {
        var low = EtwSession.ProviderLow(record);
        var high = EtwSession.ProviderHigh(record);
        var id = EtwSession.EventId(record);

        var isKernel = low == Kernel.Low && high == Kernel.High;
        if (isKernel && id != KernelPresent) return;
        var isDxgi = !isKernel && low == Dxgi.Low && high == Dxgi.High && id == DxgiPresentStart;
        var isD3D9 = !isKernel && !isDxgi && low == D3D9.Low && high == D3D9.High && id == D3D9PresentStart;
        if (!isKernel && !isDxgi && !isD3D9) return;

        long swapChain = 0;
        if (!isKernel)
        {
            // Payload starts with the swap chain pointer, then the present flags.
            var pointerSize = EtwSession.Is32Bit(record) ? 4 : 8;
            if (EtwSession.DataLength(record) >= pointerSize + 4)
            {
                var data = EtwSession.Data(record);
                swapChain = pointerSize == 8 ? Marshal.ReadInt64(data) : Marshal.ReadInt32(data);
                // DXGI_PRESENT_TEST only asks "could I present?" and shows nothing, so it is not a frame.
                if (isDxgi && (Marshal.ReadInt32(data, pointerSize) & 1) != 0) return;
            }
        }

        var time = EtwSession.Timestamp(record);
        var source = new Source(EtwSession.ProcessId(record), swapChain, isKernel);
        lock (_gate)
        {
            if (!_logs.TryGetValue(source, out var log)) _logs[source] = log = new FrameLog();
            log.Times.Enqueue(time);
            log.Last = time;
            while (log.Times.Count > 0 && log.Times.Peek() < time - Window) log.Times.Dequeue();

            if (_capture is { } capture)
            {
                if (!capture.TryGetValue(source, out var frames)) capture[source] = frames = [];
                frames.Add(time);
            }
        }
    }
}

/// <summary>Frame time arithmetic shared by the live readings and benchmark summaries.</summary>
internal static class FrameStats
{
    /// <summary>
    /// The frame rate implied by the slowest share of frames (0.01 for "1% low"): the average frame time of
    /// those frames, turned back into frames per second. Low numbers here are what stutter feels like.
    /// </summary>
    /// <param name="perSecond">How many frame time units make one second.</param>
    public static double LowFps<T>(T[] frameTimes, double share, double perSecond) where T : struct, IConvertible
    {
        if (frameTimes.Length == 0) return 0;
        var sorted = frameTimes.Select(t => t.ToDouble(CultureInfo.InvariantCulture)).OrderBy(t => t).ToArray();
        var count = Math.Max(1, (int)(sorted.Length * share));
        var total = 0.0;
        for (var i = sorted.Length - count; i < sorted.Length; i++) total += sorted[i];
        return total > 0 ? count * perSecond / total : 0;
    }
}
