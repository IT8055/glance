using System.Runtime.InteropServices;

namespace Glance.Interop;

/// <summary>Lists the graphics adapters Windows knows about, whatever the vendor.</summary>
internal static class Dxgi
{
    public sealed record Adapter(string Name, ulong DedicatedMemory, uint LuidLow, int LuidHigh, uint VendorId, bool IsSoftware)
    {
        /// <summary>How this adapter is named inside the "GPU Engine" and "GPU Adapter Memory" counter instances.</summary>
        public string CounterTag => $"luid_0x{LuidHigh:X8}_0x{LuidLow:X8}";
    }

    public static List<Adapter> Adapters()
    {
        var adapters = new List<Adapter>();
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var factory) != 0) return adapters;
            try
            {
                for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0; i++)
                {
                    try
                    {
                        if (adapter.GetDesc1(out var d) == 0)
                        {
                            adapters.Add(new Adapter(d.Description.Trim(), d.DedicatedVideoMemory, d.LuidLow, d.LuidHigh,
                                d.VendorId, IsSoftware: (d.Flags & 2) != 0));
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException)
        {
            // No DXGI (very old or stripped-down Windows): callers treat an empty list as "unknown".
        }
        return adapters;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    // Only EnumAdapters1 and GetDesc1 are called. The other members exist to keep the vtable order right.

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumAdapters(uint index, out IntPtr adapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr window, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr window);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr description, out IntPtr swapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
        [PreserveSig] int GetDesc(IntPtr description);
        [PreserveSig] int CheckInterfaceSupport(ref Guid name, out long version);
        [PreserveSig] int GetDesc1(out AdapterDesc1 description);
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 factory);
}
