using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Glance.Interop;
using Microsoft.Win32;

namespace Glance.Providers;

/// <summary>Facts about the PC that do not change while Glance runs. Detected once, on whatever machine this is.</summary>
internal static partial class Machine
{
    private static readonly Lazy<string?> LazyCpuName = new(ReadCpuName);
    private static readonly Lazy<List<Dxgi.Adapter>> LazyAdapters = new(() =>
        Dxgi.Adapters().Where(a => !a.IsSoftware && !a.Name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)).ToList());

    /// <summary>Short processor name, for example "Ryzen 9 9950X3D" or "Core i7-13700K".</summary>
    public static string? CpuName => LazyCpuName.Value;

    /// <summary>Real graphics adapters (software renderers removed), in the order Windows lists them.</summary>
    public static IReadOnlyList<Dxgi.Adapter> GraphicsAdapters => LazyAdapters.Value;

    /// <summary>
    /// Adapters that are actual GPUs: they have video memory of their own. Virtual display drivers (remote
    /// desktop, streaming) show up as a second copy of the card they render on, so repeats are dropped.
    /// </summary>
    public static IEnumerable<Dxgi.Adapter> Gpus =>
        GraphicsAdapters.Where(a => a.DedicatedMemory >= 64 * 1024 * 1024).DistinctBy(a => (a.Name, a.DedicatedMemory));

    public static string ShortGpuName(string name) =>
        Spaces().Replace(name.Replace("NVIDIA ", "").Replace("(TM)", "").Replace("(R)", "").Replace("AMD ", "").Replace("Intel ", ""), " ").Trim();

    public static string WindowsName()
    {
        var build = Environment.OSVersion.Version.Build;
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var product = key?.GetValue("ProductName") as string ?? "Windows";
        // The registry still says "Windows 10" on Windows 11.
        if (build >= 22000) product = product.Replace("Windows 10", "Windows 11");
        var version = key?.GetValue("DisplayVersion") as string;
        return version is null ? $"{product} ({build})" : $"{product} {version}";
    }

    /// <summary>Resolution and refresh rate of the main display, for example "5120 x 1440, 60 Hz".</summary>
    public static string? MainDisplay()
    {
        const int size = 220; // DEVMODEW
        var mode = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, mode, size);
            Marshal.WriteInt16(mode, 68, size); // dmSize
            if (!EnumDisplaySettingsW(null, -1 /* current */, mode)) return null;
            return $"{Marshal.ReadInt32(mode, 172)} x {Marshal.ReadInt32(mode, 176)}, {Marshal.ReadInt32(mode, 184)} Hz";
        }
        finally
        {
            Marshal.FreeHGlobal(mode);
        }
    }

    private static string? ReadCpuName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        if (key?.GetValue("ProcessorNameString") is not string raw) return null;

        var name = raw.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "");
        name = CpuNoise().Replace(name, "");
        return Spaces().Replace(name, " ").Trim();
    }

    // "AMD Ryzen 9 9950X3D 16-Core Processor" and "Intel Core i7-13700K CPU @ 3.40GHz" lose everything but the model.
    [GeneratedRegex(@"^(AMD|Intel)\s+|\s+\d+-Core Processor|\s+Processor|\s+CPU\s*@.*$|\s+@.*$", RegexOptions.IgnoreCase)]
    private static partial Regex CpuNoise();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? device, int mode, IntPtr devMode);
}
