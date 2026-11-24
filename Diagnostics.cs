using System.Diagnostics;
using Glance.Interop;

namespace Glance;

/// <summary>Headless diagnostics behind the "--probe-events" switch. Never runs in normal use.</summary>
internal static class Diagnostics
{
    /// <summary>
    /// Counts graphics events per process for a few seconds and writes the tally to a file.
    /// Used to see which events a game produces when its frame rate is not being picked up.
    /// </summary>
    public static async Task CountGraphicsEventsAsync(string path, int seconds)
    {
        (string Name, Guid Id, ulong Keywords)[] providers =
        [
            ("DXGI", new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9"), 0),
            ("D3D9", new Guid("783ACA0A-790E-4D7F-8451-AA850511C6B9"), 0),
            ("DxgKrnl", new Guid("802EC45A-1E99-4B83-9920-87C98277BA9D"), 0x8000001), // Present | Base
        ];
        var halves = providers.Select(p => (p.Name, Halves: EtwSession.Halves(p.Id))).ToArray();

        var counts = new Dictionary<(int Pid, string Provider, int Id, int Task, int Opcode), int>();
        var lines = new List<string>();
        try
        {
            using (EtwSession.Start("Glance-Diagnostics", record =>
                   {
                       var low = EtwSession.ProviderLow(record);
                       var high = EtwSession.ProviderHigh(record);
                       var provider = Array.Find(halves, h => h.Halves.Low == low && h.Halves.High == high).Name ?? "?";
                       var key = (EtwSession.ProcessId(record), provider, EtwSession.EventId(record), EtwSession.Task(record), EtwSession.Opcode(record));
                       lock (counts) counts[key] = counts.GetValueOrDefault(key) + 1;
                   },
                   providers.Select(p => new EtwSession.Provider(p.Id, 4, p.Keywords, Optional: true)).ToArray()))
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds));
            }

            lines.Add($"Events over {seconds} s, per process. 'per s' near the frame rate marks the per-frame events.");
            foreach (var process in counts.GroupBy(c => c.Key.Pid).OrderByDescending(g => g.Sum(c => c.Value)).Take(12))
            {
                string name;
                try { using var p = Process.GetProcessById(process.Key); name = p.ProcessName; }
                catch (Exception) { name = "?"; }

                lines.Add($"{name} (pid {process.Key})");
                foreach (var e in process.OrderByDescending(c => c.Value).Take(14))
                    lines.Add($"    {e.Key.Provider,-8} id {e.Key.Id,4}  task {e.Key.Task,4}  opcode {e.Key.Opcode}   {e.Value,6} events  {e.Value / (double)seconds,7:0.0} per s");
            }
        }
        catch (Exception ex)
        {
            lines.Add("Could not trace graphics events: " + ex.Message);
        }

        File.WriteAllLines(path, lines);
    }
}
