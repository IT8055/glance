using System.Runtime.InteropServices;

namespace Glance.Interop;

/// <summary>
/// A Windows performance counter query (the same data Task Manager and perfmon use).
/// Works without admin rights. Counter paths are always the English names, on any display language.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint Format = 0x00000200 /* double */ | 0x00008000 /* do not cap at 100 */;
    private const uint MoreData = 0x800007D2;
    private const int ItemSize = 24; // PDH_FMT_COUNTERVALUE_ITEM_W on x64: name pointer, status, padding, double

    // Rate counters need two samples. After a pause this long the old sample is too stale to pair with.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private IntPtr _query;
    private IntPtr _buffer;
    private uint _bufferSize;
    private DateTime _lastSample = DateTime.MinValue;

    public PdhQuery()
    {
        var status = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (status != 0) throw new InvalidOperationException($"Could not open a performance counter query (0x{status:X8}).");
    }

    /// <summary>Adds a counter. Returns zero when this machine does not have it; reads of zero return nothing.</summary>
    public IntPtr Add(string englishPath)
    {
        lock (_gate)
        {
            return _query != IntPtr.Zero && PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out var counter) == 0
                ? counter
                : IntPtr.Zero;
        }
    }

    /// <summary>
    /// Takes a sample. On the first call, or after the widget has been hidden for a while, it takes two
    /// samples a quarter of a second apart so the values are current straight away.
    /// </summary>
    public async Task SampleAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastSample > StaleAfter)
        {
            Collect();
            await Task.Delay(250, ct);
        }
        Collect();
        _lastSample = DateTime.UtcNow;
    }

    public double? Read(IntPtr counter)
    {
        lock (_gate)
        {
            if (_query == IntPtr.Zero || counter == IntPtr.Zero) return null;
            var status = PdhGetFormattedCounterValue(counter, Format, IntPtr.Zero, out var value);
            return status == 0 && value.Status is 0 or 1 ? value.Value : null;
        }
    }

    /// <summary>Reads every instance of a wildcard counter, for example one value per disk.</summary>
    public List<(string Instance, double Value)> ReadAll(IntPtr counter)
    {
        var result = new List<(string, double)>();
        lock (_gate)
        {
            if (_query == IntPtr.Zero || counter == IntPtr.Zero) return result;

            var size = _bufferSize;
            var status = PdhGetFormattedCounterArrayW(counter, Format, ref size, out var count, _buffer);
            if (status == MoreData)
            {
                Marshal.FreeHGlobal(_buffer);
                _bufferSize = size + 8192; // head room, the instance list grows as processes start
                _buffer = Marshal.AllocHGlobal((int)_bufferSize);
                size = _bufferSize;
                status = PdhGetFormattedCounterArrayW(counter, Format, ref size, out count, _buffer);
            }
            if (status != 0) return result;

            for (var i = 0; i < count; i++)
            {
                var item = _buffer + i * ItemSize;
                if (Marshal.ReadInt32(item, 8) is not (0 or 1)) continue;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                result.Add((name, BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16))));
            }
        }
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_query != IntPtr.Zero) PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _bufferSize = 0;
        }
    }

    private void Collect()
    {
        lock (_gate)
        {
            if (_query != IntPtr.Zero) PdhCollectQueryData(_query);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue
    {
        public uint Status;
        public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out CounterValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
