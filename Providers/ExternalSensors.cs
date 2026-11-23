using System.IO.MemoryMappedFiles;
using System.Text;

namespace Glance.Providers;

/// <summary>
/// Readings that Windows itself does not expose (CPU temperature and power need a hardware driver),
/// borrowed from a monitoring tool if the user already has one running:
///
///  - HWiNFO, when "Shared Memory Support" is switched on in its settings.
///  - MSI Afterburner, for whatever is ticked on its Monitoring tab.
///
/// CPU-Z is not supported because it has no interface other programs can read from.
/// Nothing here starts or changes those tools: it only reads the memory block they publish.
/// </summary>
internal static class ExternalSensors
{
    public sealed record Reading(string Sensor, string Label, string Unit, double Value);

    public sealed record Snapshot(string Source, IReadOnlyList<Reading> Readings)
    {
        public static readonly Snapshot None = new("", []);

        /// <summary>First reading whose label contains one of the given texts, tried in order.</summary>
        public double? Find(string unit, params string[] labels)
        {
            foreach (var wanted in labels)
            {
                foreach (var r in Readings)
                {
                    if (r.Unit.Equals(unit, StringComparison.OrdinalIgnoreCase) &&
                        r.Label.Contains(wanted, StringComparison.OrdinalIgnoreCase) && double.IsFinite(r.Value))
                    {
                        return r.Value;
                    }
                }
            }
            return null;
        }

        // HWiNFO names the package temperature differently per CPU family, so these go from most to least specific.
        public double? CpuTemperature => Find("°C", "CPU (Tctl/Tdie)", "CPU Package", "CPU Die (average)", "CPU temperature", "CPU (Tctl)", "CPU Core", "CPU");
        public double? CpuPower => Find("W", "CPU Package Power", "CPU power", "CPU PPT");
    }

    private static readonly object Gate = new();
    private static Snapshot _latest = Snapshot.None;
    private static DateTime _readAt = DateTime.MinValue;

    public static bool Enabled { get; set; } = true;

    /// <summary>The latest readings, re-read at most twice a second however many sections ask.</summary>
    public static Snapshot Current
    {
        get
        {
            if (!Enabled) return Snapshot.None;
            lock (Gate)
            {
                if (DateTime.UtcNow - _readAt < TimeSpan.FromMilliseconds(500)) return _latest;
                _readAt = DateTime.UtcNow;
                _latest = ReadHwInfo() ?? ReadAfterburner() ?? Snapshot.None;
                return _latest;
            }
        }
    }

    // HWiNFO shared memory, layout "HWiNFO_SENSORS_SHARED_MEM2". The header gives the offset, size and count of
    // the sensor and reading tables, so this copes with HWiNFO adding fields to the end of each element.
    private static Snapshot? ReadHwInfo()
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(@"Global\HWiNFO_SENS_SM2", MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            if (view.ReadUInt32(0) != 0x53695748 /* "HWiS" */) return null; // also covers "DEAD", written when HWiNFO stops sharing
            var sensorOffset = view.ReadUInt32(20);
            var sensorSize = view.ReadUInt32(24);
            var sensorCount = view.ReadUInt32(28);
            var readingOffset = view.ReadUInt32(32);
            var readingSize = view.ReadUInt32(36);
            var readingCount = view.ReadUInt32(40);
            if (sensorSize < 264 || readingSize < 316 || sensorCount > 2048 || readingCount > 16384) return null;

            var sensors = new string[sensorCount];
            for (var i = 0; i < sensorCount; i++)
                sensors[i] = ReadText(view, sensorOffset + (long)i * sensorSize + 8 + 128, 128); // user-visible sensor name

            var readings = new List<Reading>((int)readingCount);
            for (var i = 0; i < readingCount; i++)
            {
                var at = readingOffset + (long)i * readingSize;
                var sensorIndex = view.ReadUInt32(at + 4);
                var label = ReadText(view, at + 12 + 128, 128); // the label as the user sees it
                var unit = ReadText(view, at + 268, 16);
                var value = view.ReadDouble(at + 284);
                readings.Add(new Reading(sensorIndex < sensors.Length ? sensors[sensorIndex] : "", label, unit, value));
            }
            return new Snapshot("HWiNFO", readings);
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null; // not running, or sharing is switched off
        }
    }

    // MSI Afterburner hardware monitoring shared memory ("MAHM"). Entry size comes from the header.
    private static Snapshot? ReadAfterburner()
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting("MAHMSharedMemory", MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            if (view.ReadUInt32(0) != 0x4D41484D /* "MAHM" */) return null;
            var headerSize = view.ReadUInt32(8);
            var count = view.ReadUInt32(12);
            var entrySize = view.ReadUInt32(16);
            if (entrySize < 1304 || count > 1024) return null;

            var readings = new List<Reading>((int)count);
            for (var i = 0; i < count; i++)
            {
                var at = headerSize + (long)i * entrySize;
                var name = ReadText(view, at, 260);
                var unit = ReadText(view, at + 260, 260);
                var value = view.ReadSingle(at + 1300);
                // Afterburner reports FLT_MAX for a source it cannot read at the moment.
                if (value > 1e30f) continue;
                readings.Add(new Reading("MSI Afterburner", name, unit, value));
            }
            return new Snapshot("MSI Afterburner", readings);
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static string ReadText(MemoryMappedViewAccessor view, long offset, int length)
    {
        var bytes = new byte[length];
        view.ReadArray(offset, bytes, 0, length);
        var end = Array.IndexOf(bytes, (byte)0);
        // Both tools write Windows-1252 style text here; Latin1 keeps the degree sign intact.
        return Encoding.Latin1.GetString(bytes, 0, end < 0 ? length : end).Trim();
    }
}
