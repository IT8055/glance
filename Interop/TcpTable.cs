using System.Runtime.InteropServices;

namespace Glance.Interop;

/// <summary>The TCP connections currently open on this PC, with the process that owns each. Needs no special rights.</summary>
internal static class TcpTable
{
    private const int OwnerPidConnections = 4; // TCP_TABLE_OWNER_PID_CONNECTIONS
    private const int InterNetwork = 2;
    private const int InterNetworkV6 = 23;

    /// <param name="Remote">The far end's address: 4 bytes for IPv4, 16 for IPv6.</param>
    public readonly record struct Connection(int ProcessId, byte[] Remote);

    public static List<Connection> Connections()
    {
        var connections = new List<Connection>();
        // IPv4 rows: state, local address, local port, remote address, remote port, owning process. 24 bytes.
        Read(InterNetwork, 24, row => new Connection(Marshal.ReadInt32(row, 20), Bytes(row + 12, 4)), connections);
        // IPv6 rows: local address (16), scope, port, remote address (16), scope, port, state, owning process. 56 bytes.
        Read(InterNetworkV6, 56, row => new Connection(Marshal.ReadInt32(row, 52), Bytes(row + 24, 16)), connections);
        return connections;
    }

    private static void Read(int family, int rowSize, Func<IntPtr, Connection> parse, List<Connection> into)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, OwnerPidConnections, 0);
        if (size <= 0) return;

        // The table can grow between the two calls, so leave some room.
        size += 4096;
        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, family, OwnerPidConnections, 0) != 0) return;
            var count = Marshal.ReadInt32(table);
            for (var i = 0; i < count; i++) into.Add(parse(table + 4 + i * rowSize));
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    private static byte[] Bytes(IntPtr at, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(at, bytes, 0, length);
        return bytes;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);
}
