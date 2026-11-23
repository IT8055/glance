using System.Runtime.InteropServices;
using System.Text;

namespace Glance.Interop;

/// <summary>
/// NVIDIA Management Library (nvml.dll, installed with the NVIDIA driver). It is what nvidia-smi uses,
/// and it needs no admin rights. Every call returns 0 on success.
/// </summary>
internal static class Nvml
{
    private const string Dll = "nvml.dll";

    public const int TemperatureGpu = 0;
    public const int ClockGraphics = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Memory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [DllImport(Dll, EntryPoint = "nvmlInit_v2")]
    public static extern int Init();

    [DllImport(Dll, EntryPoint = "nvmlShutdown")]
    public static extern int Shutdown();

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetCount_v2")]
    public static extern int GetCount(out uint count);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    public static extern int GetHandle(uint index, out IntPtr device);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetName")]
    private static extern int GetName(IntPtr device, byte[] name, uint length);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetUtilizationRates")]
    public static extern int GetUtilization(IntPtr device, out Utilization utilization);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryV2
    {
        public uint Version;
        public ulong Total;
        public ulong Reserved;
        public ulong Free;
        public ulong Used;
    }

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMemoryInfo")]
    private static extern int GetMemoryV1(IntPtr device, out Memory memory);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMemoryInfo_v2")]
    private static extern int GetMemoryV2(IntPtr device, ref MemoryV2 memory);

    /// <summary>
    /// Video memory in use. The newer call is preferred because it leaves out memory the driver reserves
    /// for itself, which is what nvidia-smi reports. Old drivers only have the first version.
    /// </summary>
    public static int GetMemory(IntPtr device, out Memory memory)
    {
        try
        {
            // The version field is the structure size with the version number in the top byte.
            var v2 = new MemoryV2 { Version = (uint)Marshal.SizeOf<MemoryV2>() | (2u << 24) };
            if (GetMemoryV2(device, ref v2) == 0)
            {
                memory = new Memory { Total = v2.Total, Free = v2.Free, Used = v2.Used };
                return 0;
            }
        }
        catch (EntryPointNotFoundException)
        {
        }
        return GetMemoryV1(device, out memory);
    }

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetTemperature")]
    public static extern int GetTemperature(IntPtr device, int sensor, out uint celsius);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerUsage")]
    public static extern int GetPowerMilliwatts(IntPtr device, out uint milliwatts);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetFanSpeed")]
    public static extern int GetFanPercent(IntPtr device, out uint percent);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetClockInfo")]
    public static extern int GetClockMhz(IntPtr device, int type, out uint mhz);

    public static string? Name(IntPtr device)
    {
        var buffer = new byte[96];
        if (GetName(device, buffer, (uint)buffer.Length) != 0) return null;
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.ASCII.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }
}
