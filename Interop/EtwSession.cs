using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Glance.Interop;

/// <summary>
/// A real-time Event Tracing for Windows session: the mechanism PresentMon and the Xbox Game Bar use
/// to count frames, and the only way to see per-connection network traffic without a driver.
///
/// Windows allows this for administrators and for members of the "Performance Log Users" group.
/// Anyone else gets <see cref="UnauthorizedAccessException"/> from <see cref="Start"/>.
///
/// Events are delivered on a background thread as raw EVENT_RECORD pointers. The static helpers
/// read the handful of header fields Glance needs, so no payload schema is required.
/// </summary>
internal sealed class EtwSession : IDisposable
{
    private const uint AccessDenied = 5;
    private const uint AlreadyExists = 183;
    private const uint ControlStop = 1;
    private const uint ControlFlush = 3;
    private const uint EnableProvider = 1;
    private const ulong InvalidHandle = ulong.MaxValue;

    // Sizes and field offsets of the native structures on 64-bit Windows.
    private const int PropertiesSize = 120;  // EVENT_TRACE_PROPERTIES
    private const int NameBytes = 520;       // room for the session name, twice (name and log file name)
    private const int LogFileSize = 448;     // EVENT_TRACE_LOGFILEW

    private readonly string _name;
    private readonly Action<IntPtr> _onEvent;
    private readonly EventRecordCallback _callback; // held so the native side never calls a collected delegate
    private ulong _session;
    private ulong _trace = InvalidHandle;
    private Thread? _thread;

    private EtwSession(string name, Action<IntPtr> onEvent)
    {
        _name = name;
        _onEvent = onEvent;
        _callback = record =>
        {
            try { _onEvent(record); }
            catch { /* an exception must not unwind into native code */ }
        };
    }

    public readonly record struct Provider(Guid Id, byte Level = 4, ulong Keywords = 0);

    /// <summary>True when running as administrator. Kernel providers, such as network traffic, need this.</summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>True for an elevated administrator or a member of Performance Log Users (S-1-5-32-559).</summary>
    public static bool IsAllowedForThisAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator) || principal.IsInRole(new SecurityIdentifier("S-1-5-32-559"));
    }

    /// <summary>Starts the session and begins delivering events from the given providers.</summary>
    public static EtwSession Start(string name, Action<IntPtr> onEvent, params Provider[] providers)
    {
        var session = new EtwSession(name, onEvent);
        try
        {
            session.Open(providers);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void Open(Provider[] providers)
    {
        var properties = NewProperties();
        try
        {
            var status = StartTraceW(out _session, _name, properties);
            if (status == AlreadyExists)
            {
                // Left over from a crash: sessions outlive the process that made them.
                ControlTraceW(0, _name, properties, ControlStop);
                Marshal.FreeHGlobal(properties);
                properties = NewProperties();
                status = StartTraceW(out _session, _name, properties);
            }
            if (status == AccessDenied) throw new UnauthorizedAccessException("Event tracing is not allowed for this account.");
            if (status != 0) throw new InvalidOperationException($"Could not start the trace session (error {status}).");
        }
        finally
        {
            Marshal.FreeHGlobal(properties);
        }

        foreach (var provider in providers)
        {
            var status = EnableTraceEx2(_session, provider.Id, EnableProvider, provider.Level, provider.Keywords, 0, 0, IntPtr.Zero);
            if (status == AccessDenied) throw new UnauthorizedAccessException("Event tracing is not allowed for this account.");
            if (status != 0) throw new InvalidOperationException($"Could not enable trace provider {provider.Id} (error {status}).");
        }

        var logFile = Marshal.AllocHGlobal(LogFileSize);
        var namePointer = Marshal.StringToHGlobalUni(_name);
        try
        {
            Marshal.Copy(new byte[LogFileSize], 0, logFile, LogFileSize);
            Marshal.WriteIntPtr(logFile, 8, namePointer);                                     // LoggerName
            Marshal.WriteInt32(logFile, 28, 0x100 /* real time */ | 0x10000000 /* event record */); // ProcessTraceMode
            Marshal.WriteIntPtr(logFile, 424, Marshal.GetFunctionPointerForDelegate(_callback)); // EventRecordCallback
            _trace = OpenTraceW(logFile);
        }
        finally
        {
            Marshal.FreeHGlobal(namePointer);
            Marshal.FreeHGlobal(logFile);
        }
        if (_trace == InvalidHandle) throw new InvalidOperationException($"Could not open the trace session (error {Marshal.GetLastWin32Error()}).");

        // ProcessTrace blocks until the session is stopped, so it gets its own thread.
        var trace = _trace;
        _thread = new Thread(() => ProcessTrace([trace], 1, IntPtr.Zero, IntPtr.Zero))
        {
            IsBackground = true,
            Name = "Glance trace: " + _name,
        };
        _thread.Start();
    }

    /// <summary>
    /// Pushes buffered events out now. Without this they arrive in one-second batches, which would make
    /// every reading lag by up to a second.
    /// </summary>
    public void Flush()
    {
        if (_session == 0) return;
        var properties = NewProperties();
        ControlTraceW(_session, null, properties, ControlFlush);
        Marshal.FreeHGlobal(properties);
    }

    public void Dispose()
    {
        if (_session != 0)
        {
            var properties = NewProperties();
            ControlTraceW(_session, null, properties, ControlStop);
            Marshal.FreeHGlobal(properties);
            _session = 0;
        }
        if (_trace != InvalidHandle)
        {
            CloseTrace(_trace);
            _trace = InvalidHandle;
        }
        _thread?.Join(2000);
        _thread = null;
    }

    private static IntPtr NewProperties()
    {
        const int size = PropertiesSize + NameBytes * 2;
        var p = Marshal.AllocHGlobal(size);
        Marshal.Copy(new byte[size], 0, p, size);
        Marshal.WriteInt32(p, 0, size);                 // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);                   // Wnode.ClientContext: high resolution timestamps
        Marshal.WriteInt32(p, 44, 0x00020000);          // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 48, 64);                  // BufferSize, in KB
        Marshal.WriteInt32(p, 52, 4);                   // MinimumBuffers
        Marshal.WriteInt32(p, 56, 32);                  // MaximumBuffers
        Marshal.WriteInt32(p, 64, 0x100);               // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
        Marshal.WriteInt32(p, 68, 1);                   // FlushTimer, in seconds
        Marshal.WriteInt32(p, 116, PropertiesSize);     // LoggerNameOffset
        return p;
    }

    // EVENT_RECORD field readers. The record starts with an EVENT_HEADER.

    /// <summary>Bit 0x20 of the header flags: the event came from a 32-bit process, so pointers in it are 4 bytes.</summary>
    public static bool Is32Bit(IntPtr record) => (Marshal.ReadInt16(record, 4) & 0x20) != 0;
    public static int ProcessId(IntPtr record) => Marshal.ReadInt32(record, 12);

    /// <summary>Event time in 100 ns units (FILETIME, UTC).</summary>
    public static long Timestamp(IntPtr record) => Marshal.ReadInt64(record, 16);
    public static long ProviderLow(IntPtr record) => Marshal.ReadInt64(record, 24);
    public static long ProviderHigh(IntPtr record) => Marshal.ReadInt64(record, 32);
    public static int EventId(IntPtr record) => (ushort)Marshal.ReadInt16(record, 40);
    public static int DataLength(IntPtr record) => (ushort)Marshal.ReadInt16(record, 86);
    public static IntPtr Data(IntPtr record) => Marshal.ReadIntPtr(record, 96);

    /// <summary>Splits a provider GUID into the two 64-bit halves the readers above return.</summary>
    public static (long Low, long High) Halves(Guid provider)
    {
        var bytes = provider.ToByteArray();
        return (BitConverter.ToInt64(bytes, 0), BitConverter.ToInt64(bytes, 8));
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallback(IntPtr record);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong session, string name, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong session, string? name, IntPtr properties, uint control);

    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(ulong session, in Guid provider, uint control, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr parameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ulong OpenTraceW(IntPtr logFile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong handle);
}
