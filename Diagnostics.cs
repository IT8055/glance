using System.Diagnostics;
using Glance.Interop;

namespace Glance;

/// <summary>Headless diagnostics behind the "--probe-events" switch. Never runs in normal use.</summary>
internal static class Diagnostics
{
    /// <summary>
    /// Tries to trace the given event providers and tallies what arrives, per event number. For one chosen
    /// process it also prints the start of a few event payloads, which is how an undocumented event's
    /// layout gets worked out. Behind "--probe-etw", for development only.
    /// </summary>
    /// <param name="providers">"Name=guid:keywordsInHex" separated by semicolons.</param>
    public static async Task ProbeProvidersAsync(string path, int seconds, string providers, int focusPid, int keyBytes = 0, int sumOffset = -1, int keyOffset = -1)
    {
        // Optional finer tally for the chosen process: grouped by the first few payload bytes, adding up one field.
        var fine = new Dictionary<(string Provider, int Id, string Key), (int Count, long Sum)>();
        var lines = new List<string>();
        var specs = new List<(string Name, Guid Id, ulong Keywords)>();
        foreach (var spec in providers.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = spec[..spec.IndexOf('=')];
            var rest = spec[(spec.IndexOf('=') + 1)..].Split(':');
            specs.Add((name, new Guid(rest[0]), rest.Length > 1 ? Convert.ToUInt64(rest[1], 16) : 0));
        }
        var halves = specs.Select(p => (p.Name, Halves: EtwSession.Halves(p.Id))).ToArray();

        var tally = new Dictionary<(string Provider, int Id), (int Count, int Mine, int MinLength, int MaxLength)>();
        var samples = new Dictionary<(string Provider, int Id), List<string>>();
        try
        {
            using (var session = EtwSession.Start("Glance-Diagnostics", record =>
                   {
                       var low = EtwSession.ProviderLow(record);
                       var high = EtwSession.ProviderHigh(record);
                       var provider = Array.Find(halves, h => h.Halves.Low == low && h.Halves.High == high).Name ?? "?";
                       var key = (provider, EtwSession.EventId(record));
                       var length = EtwSession.DataLength(record);
                       var mine = EtwSession.ProcessId(record) == focusPid;
                       lock (tally)
                       {
                           var t = tally.GetValueOrDefault(key, (0, 0, int.MaxValue, 0));
                           tally[key] = (t.Item1 + 1, t.Item2 + (mine ? 1 : 0), Math.Min(t.Item3, length), Math.Max(t.Item4, length));
                           if (mine && keyBytes > 0 && length >= keyBytes)
                           {
                               var head = new byte[keyBytes];
                               System.Runtime.InteropServices.Marshal.Copy(EtwSession.Data(record), head, 0, keyBytes);
                               var extra = keyOffset >= 0 && length >= keyOffset + 4
                                   ? " +" + ((uint)System.Runtime.InteropServices.Marshal.ReadInt32(EtwSession.Data(record), keyOffset)).ToString("X8") : "";
                               var fineKey = (provider, key.Item2, Convert.ToHexString(head) + extra);
                               var f = fine.GetValueOrDefault(fineKey);
                               long add = sumOffset >= 0 && length >= sumOffset + 4
                                   ? (uint)System.Runtime.InteropServices.Marshal.ReadInt32(EtwSession.Data(record), sumOffset) : 0;
                               fine[fineKey] = (f.Count + 1, f.Sum + add);
                           }
                           if (mine)
                           {
                               if (!samples.TryGetValue(key, out var list)) samples[key] = list = [];
                               if (list.Count < 3)
                               {
                                   var bytes = new byte[Math.Min(length, 96)];
                                   System.Runtime.InteropServices.Marshal.Copy(EtwSession.Data(record), bytes, 0, bytes.Length);
                                   list.Add(Convert.ToHexString(bytes));
                               }
                           }
                       }
                   },
                   specs.Select(p => new EtwSession.Provider(p.Id, 4, p.Keywords, Optional: true)).ToArray()))
            {
                foreach (var spec in specs)
                    lines.Add($"{spec.Name}: {(session.Enabled.Contains(spec.Id) ? "allowed" : "REFUSED")}");
                await Task.Delay(TimeSpan.FromSeconds(seconds));
            }

            lines.Add($"Events over {seconds} s. 'mine' counts those from process {focusPid}.");
            foreach (var e in tally.OrderBy(t => t.Key.Provider).ThenBy(t => t.Key.Id))
            {
                lines.Add($"{e.Key.Provider,-10} id {e.Key.Id,5}  all {e.Value.Count,7}  mine {e.Value.Mine,5}  payload {e.Value.MinLength}-{e.Value.MaxLength} bytes");
                if (keyBytes == 0 && samples.TryGetValue(e.Key, out var list))
                    lines.AddRange(list.Select(hex => "      " + hex));
            }
            if (keyBytes > 0)
            {
                lines.Add($"For process {focusPid}, grouped by the first {keyBytes} payload bytes, with the field at offset {sumOffset} added up:");
                foreach (var f in fine.OrderBy(f => f.Key.Provider).ThenBy(f => f.Key.Id).ThenBy(f => f.Key.Key))
                    lines.Add($"{f.Key.Provider,-10} id {f.Key.Id,5}  key {f.Key.Key}  count {f.Value.Count,5}  sum {f.Value.Sum,10}");
            }
        }
        catch (Exception ex)
        {
            lines.Add("Could not trace: " + ex.Message);
        }

        File.WriteAllLines(path, lines);
    }

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
