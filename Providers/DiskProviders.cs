using Glance.Core;
using Glance.Interop;

namespace Glance.Providers;

/// <summary>How hard the disks are working right now: active time of the busiest disk, and total read and write speed.</summary>
public sealed class DiskActivityProvider : IMetricProvider, IDisposable
{
    private const double MegaByte = 1024 * 1024;

    private readonly PdhQuery _query = new();
    private readonly IntPtr _idle;
    private readonly IntPtr _read;
    private readonly IntPtr _write;

    public DiskActivityProvider(TimeSpan interval)
    {
        RefreshInterval = interval;
        _idle = _query.Add(@"\PhysicalDisk(*)\% Idle Time");
        _read = _query.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _write = _query.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
    }

    public string Title => "Disk";
    public string Id => "disk";
    public TimeSpan RefreshInterval { get; }

    public async Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        if (_idle == IntPtr.Zero)
            return MetricSnapshot.Error("Disk counters are unavailable on this PC.", TimeSpan.FromMinutes(5));

        await _query.SampleAsync(ct);

        // Instances are "0 C:", "1 D: E:" and "_Total". Active time is what is left after idle time.
        var disks = _query.ReadAll(_idle).Where(d => d.Instance != "_Total").ToList();
        var busiest = disks.Count == 0 ? default : disks.MinBy(d => d.Value);
        var active = disks.Count == 0 ? 0 : Math.Clamp(100 - busiest.Value, 0, 100);

        // Name the busy disk by its drive letters once it is doing real work, so the label does not flicker at idle.
        var letters = busiest.Instance is { } name && name.IndexOf(' ') is var space and > 0 ? name[(space + 1)..] : null;

        var read = _query.Read(_read) ?? 0;
        var write = _query.Read(_write) ?? 0;
        return new MetricSnapshot
        {
            Subtitle = active >= 10 && letters is not null ? "busiest " + letters : null,
            Items =
            [
                new MetricItem("Active") { Key = "active", Percent = active, ValueText = Format.Percent(active), Number = active, Unit = "%", Graph = true, GraphMax = 100 },
                new MetricItem("Read") { Key = "read", ValueText = Format.ByteRate(read), Number = read / MegaByte, Unit = "MB/s", Graph = true },
                new MetricItem("Write") { Key = "write", ValueText = Format.ByteRate(write), Number = write / MegaByte, Unit = "MB/s", Graph = true },
            ],
        };
    }

    public void Dispose() => _query.Dispose();
}

/// <summary>Free space on each fixed drive.</summary>
public sealed class DriveSpaceProvider : IMetricProvider
{
    public string Title => "Drives";
    public string Id => "drives";

    // Free space moves slowly, and the widget refreshes on every show anyway.
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(30);

    public Task<MetricSnapshot> FetchAsync(CancellationToken ct)
    {
        var items = new List<MetricItem>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                // Fixed only: asking a sleeping network share or an empty card reader can hang for seconds.
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize <= 0) continue;

                var percent = (drive.TotalSize - drive.TotalFreeSpace) * 100.0 / drive.TotalSize;
                var letter = drive.Name.TrimEnd('\\');
                items.Add(new MetricItem(string.IsNullOrWhiteSpace(drive.VolumeLabel) ? letter : $"{letter} {drive.VolumeLabel}")
                {
                    Key = letter.TrimEnd(':').ToLowerInvariant(),
                    Percent = percent,
                    ValueText = Format.Size(drive.TotalFreeSpace) + " free",
                    Brief = $"{letter} {Format.Size(drive.TotalFreeSpace)}",
                    Number = drive.TotalFreeSpace / (1024.0 * 1024 * 1024),
                    Unit = "GB free",
                    Level = percent >= 97 ? MetricLevel.Critical : percent >= 90 ? MetricLevel.Warning : MetricLevel.Normal,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that vanished or is locked (BitLocker) is skipped rather than failing the section.
            }
        }

        return Task.FromResult(items.Count == 0
            ? MetricSnapshot.Error("No fixed drives were found.", TimeSpan.FromMinutes(5))
            : new MetricSnapshot { Items = items });
    }
}
