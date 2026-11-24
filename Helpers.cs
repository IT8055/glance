using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Glance.Core;
using Glance.Interop;
using Glance.Providers;

namespace Glance;

/// <summary>Raises a notification when a Claude limit climbs past a chosen level, or the session starts over.</summary>
internal sealed class UsageAlerts
{
    private readonly Dictionary<string, double> _last = [];

    public void Check(MetricSnapshot snapshot, ClaudeConfig config, Action<string, string> notify)
    {
        if (snapshot.IsError) return;

        foreach (var item in snapshot.Items)
        {
            // Limits are the rows with a bar. The "from Claude Code" tiles are shares, not limits.
            if (item.Percent is null || item.Number is not { } now) continue;

            // The first reading after starting only sets the baseline: no alert for where things already stood.
            if (config.Alerts && _last.TryGetValue(item.Id, out var before))
            {
                var passed = config.AlertAt.Where(level => before < level && now >= level).DefaultIfEmpty(0).Max();
                if (passed > 0)
                {
                    var resets = item.ResetsAt is { } at ? TimeText.ResetsIn(at, DateTimeOffset.Now) + "." : "";
                    notify($"Claude: {item.Label} at {Format.Whole(now)}%", $"Passed {passed}%. {resets}".Trim());
                }
                else if (config.AlertOnReset && item.Id == "session" && before >= 20 && now <= 5)
                {
                    notify("Claude session has reset", "A fresh session allowance is available.");
                }
            }
            _last[item.Id] = now;
        }
    }
}

/// <summary>
/// Version 0.3 and 0.4 could set Glance up to start as administrator at sign-in, through a scheduled
/// task, because the web and LAN traffic split needed it then. It no longer does, so all that is left
/// here is spotting that task and taking it away again.
/// </summary>
internal static class AdminStart
{
    private const string TaskName = "Glance (administrator)";

    public static bool IsEnabled()
    {
        try
        {
            using var query = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            return query is not null && query.WaitForExit(4000) && query.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Removes the task. Windows asks for approval. Returns false if that was declined or failed.</summary>
    public static bool Remove()
    {
        var script = $"Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false -ErrorAction Stop";

        try
        {
            // Passed encoded so quotes and spaces in paths cannot be misread on the way through.
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var process = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
            {
                UseShellExecute = true,
                Verb = "runas", // this is what raises the Windows approval prompt
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return process is not null && process.WaitForExit(30000) && process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false; // approval declined
        }
    }
}

/// <summary>Tells whether the app in front is running full screen, which is how a game is recognised.</summary>
internal static class FullScreen
{
    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000;

    // The desktop and taskbar cover the screen too, and are not apps.
    private static readonly string[] ShellWindows = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "XamlExplorerHostIslandWindow"];

    /// <summary>Null when there is no telling (nothing in front, or one of Glance's own windows).</summary>
    public static bool? AppInFront()
    {
        var window = Native.GetForegroundWindow();
        if (window == IntPtr.Zero) return null;

        Native.GetWindowThreadProcessId(window, out var processId);
        if (processId == Environment.ProcessId) return null;

        var name = new StringBuilder(64);
        Native.GetClassNameW(window, name, name.Capacity);
        if (ShellWindows.Contains(name.ToString())) return false;

        // A maximised window keeps its title bar. A game, or a video player in full screen, has none.
        if ((Native.GetWindowLongPtr(window, GWL_STYLE).ToInt64() & WS_CAPTION) == WS_CAPTION) return false;

        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetWindowRect(window, out var rect) ||
            !Native.GetMonitorInfo(Native.MonitorFromWindow(window, Native.MONITOR_DEFAULTTONEAREST), ref info))
        {
            return null;
        }

        var screen = info.rcMonitor;
        return rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
    }
}

/// <summary>What the settings window needs from the running app.</summary>
internal interface ISettingsHost
{
    /// <summary>Called after every change. True when the set of sections or where their data comes from changed.</summary>
    void Apply(bool sourcesChanged);

    /// <summary>Registers a shortcut in a slot, or with empty text releases it. False if another app has it.</summary>
    bool TrySetHotkey(int slot, string? shortcut);

    /// <summary>Every numeric reading currently known, as ("gpu.temp", "GPU: Temp"), for the tray and strip pickers.</summary>
    IReadOnlyList<(string Key, string Name)> Readings();

    void OpenBenchmarksFolder();
}
