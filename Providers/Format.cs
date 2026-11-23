using System.Globalization;

namespace Glance.Providers;

/// <summary>Number formatting shared by the hardware sections. Always uses a full stop as the decimal mark.</summary>
internal static class Format
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Percent(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", Inv) + "%";

    /// <summary>"7.8 GB", "126 GB", "1.8 TB".</summary>
    public static string Size(double bytes)
    {
        var (value, unit) = Scale(bytes);
        return $"{Number(value)} {Units[unit]}";
    }

    /// <summary>"7.8 / 32 GB": both numbers in the unit of the total.</summary>
    public static string SizePair(double used, double total)
    {
        var (totalValue, unit) = Scale(total);
        return $"{Number(Math.Max(0, used) / Math.Pow(1024, unit))} / {Number(totalValue)} {Units[unit]}";
    }

    /// <summary>Disk style throughput in bytes: "1.2 MB/s".</summary>
    public static string ByteRate(double bytesPerSecond) => Size(bytesPerSecond) + "/s";

    /// <summary>Network style throughput in bits: "12.3 Mbps".</summary>
    public static string BitRate(double bytesPerSecond)
    {
        var bits = Math.Max(0, bytesPerSecond) * 8;
        return bits >= 1e9 ? Number(bits / 1e9) + " Gbps"
             : bits >= 1e6 ? Number(bits / 1e6) + " Mbps"
             : bits >= 1e3 ? Number(bits / 1e3) + " kbps"
             : "0 kbps";
    }

    public static string Number(double value) =>
        value >= 100 ? value.ToString("0", Inv) : value.ToString("0.#", Inv);

    private static (double Value, int Unit) Scale(double bytes)
    {
        var value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return (value, unit);
    }
}
