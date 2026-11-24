# Glance

A small always-on-top overlay for Windows 10 and 11. It stays hidden until you press a global shortcut, then sits above your other windows without taking keyboard focus. Showing it refreshes everything. While it is hidden, nothing is measured except what the tray icon and alerts need.

It detects the hardware of whatever PC it runs on, so the same exe can be handed to other people.

## What it shows

Each block is a section. Profiles decide which sections appear and in what order.

| Section | Readings | Source |
| --- | --- | --- |
| Claude usage | Current session, week (all models), per-model weekly limits such as Fable, each with its reset time and a pace forecast. Where the week's use came from. | Your Claude Code sign-in |
| FPS | Now, frame time, average and 1% low for the app in front | Windows frame events: Direct3D 9 to 12, OpenGL, Vulkan |
| GPU | Load, VRAM, and on NVIDIA cards temperature, power, fan and clock | NVIDIA driver, or Windows counters for AMD and Intel |
| GPU by app | Video memory and load of the five heaviest programs | Windows counters |
| CPU | Load, a bar per thread, clock speed, plus temperature and power if a monitoring tool is running | Windows counters, HWiNFO or MSI Afterburner |
| CPU by app | The five programs using the most processor | Windows counters |
| Memory | RAM in use, free, committed | Windows |
| Memory by app | The five programs using the most memory | Windows counters |
| Disk activity | Active time of the busiest disk, read and write speed | Windows counters |
| I/O by app | The five programs reading and writing the most (files, network and devices together) | Windows counters |
| Drive space | Free space per fixed drive | Windows |
| Network | Download and upload, split into web and LAN when run as administrator | Windows counters and network events |
| Ping | Latency, average, jitter and packet loss to a host you choose | One ping per refresh |
| Extra sensors | Any readings you pick from HWiNFO or MSI Afterburner: fans, voltages, drive temperatures | That tool's shared readings |
| Battery | Charge, power source, time left. Only on PCs that have one. | Windows |
| Weather | Temperature, feels like, wind, humidity, today's range, rain chance | Open-Meteo |
| Benchmark | The timer while recording, then the last result | See Benchmarks below |
| PC specs | CPU, GPUs, memory, display, Windows version | Windows |

A section that cannot work on a PC (Claude usage without Claude Code, Battery on a desktop) is left out rather than showing an error.

## Build and run

Needs the .NET 10 SDK.

```powershell
.\publish.ps1
.\dist\Glance.exe
```

`publish.ps1` makes two builds:

- `dist\Glance.exe`: small, needs the .NET 10 Desktop Runtime. Use this on your own PC.
- `dist\share\Glance.exe`: about 70 MB with everything included. Runs on any 64-bit Windows 10 or 11 PC with nothing installed. Send this one to other people.

The exes are not signed, so Windows SmartScreen warns the first time someone else runs one (More info, then Run anyway). The only fix is a bought code-signing certificate. Once you have one, `.\publish.ps1 -Sign <thumbprint>` signs both builds.

The first launch shows the widget once so you can see it. After that it starts hidden.

Preview the layout with made-up numbers (touches no account, network or hardware):

```powershell
.\dist\Glance.exe --demo --show
```

## Using it

| Action | Default shortcut | Also |
| --- | --- | --- |
| Show or hide the widget | `Ctrl+Alt+U` | Left-click the icon near the clock |
| Show or hide the compact strip | `Ctrl+Alt+Shift+U` | Right-click menu |
| Start or stop a benchmark | `Ctrl+Alt+Shift+B` | Right-click menu |
| Next profile | `Ctrl+Alt+Shift+P` | Right-click menu, Profile |
| Settings | | Hover over the widget and click the gear, or double-click the icon near the clock |
| Move | | Drag the widget, or pick a corner in Settings |
| Refresh now | | Double-click the widget |

Starting `Glance.exe` while it is already running shows the widget, which makes a desktop shortcut work as a "show" button.

### Panels

Each section is a panel of its own. To rearrange them, drag a panel by the six dots beside its heading: a coloured line shows where it will land. Drop it between two panels, at the top or foot of another column, or past either side of the widget to start a new column. The arrangement is remembered per profile.

Until you drag one, Glance arranges the panels itself into the number of columns chosen in Settings, adding columns if they would not fit the height of the screen. Choosing a number of columns again hands the arranging back to Glance.

Dragging anywhere else on the widget, such as its title line or the gaps between panels, moves the whole widget.

### Colours

Settings, Widget, Colours switches between eight colour schemes: Midnight, Graphite, Ocean, Forest, Ember, Violet, Light and High contrast. A scheme is one line in [Theme.cs](Theme.cs), so adding your own is easy.

### Claude usage and its limit

The usage service rate limits callers that ask too often, so Glance never asks more than once every 3 minutes, however often the widget is shown or its settings changed. In between it shows the last answer, and says how old it is once that is over a minute and a half. Restarting Glance does not cost a request either: it carries on from the last saved answer.

To check sooner, press the refresh button on the Claude panel (or "Check Claude usage now" in the right-click menu). If the service does rate limit, Glance waits at least 10 minutes before trying again and shows when that will be.

### Compact strip

One line of the readings you choose, for example `FPS 142  GPU 97% 64 °C  CPU 41%`. Only the sections those readings come from are measured while the strip is showing. Pick the readings and their order in Settings, Tray and strip.

### Tray icon

The icon near the clock can show one live reading as a number, coloured amber or red as it climbs: Claude session percentage, GPU temperature, FPS, whatever you pick in Settings. That reading stays measured while the widget is hidden (Claude usage every few minutes, hardware every couple of seconds).

### Profiles

A profile is a named, ordered set of sections. Glance starts with "Everyday" and "Gaming". Switch with the shortcut or the menu, or have Glance switch by itself while a full-screen app is in front and back again afterwards.

### Graphs and ranges

In Settings, Widget:

- **Graphs** draws the last minute of load, temperature, frame rate, network and disk speed as a small line in place of the bar.
- **Lowest and highest** adds the range seen for each of those readings. Reset it from the right-click menu.

### Claude alerts

A notification when a limit passes 80% and 95% (changeable), and when the session starts over. With alerts on, usage keeps being checked while the widget is hidden, at the interval set in Settings (never under 3 minutes).

## Benchmarks

Press the benchmark shortcut to start recording and again to stop. A red REC timer shows on the widget and strip. The widget can be hidden while it records.

Glance records every frame time of the app in front, and once a second every hardware reading. When stopped it writes three files to `Documents\Glance benchmarks`:

- `<date> <app>.txt`: the summary. Average, 1% low and 0.1% low frame rate, and the lowest, average and highest of every reading.
- `<date> <app>.csv`: the readings, one line a second.
- `<date> <app> frames.csv`: every frame time in milliseconds.

"1% low" is the frame rate of the slowest 1% of frames: their average frame time turned back into frames per second.

## Settings

Everything in the settings window applies immediately. The tabs are Widget (colours, position, columns, size, opacity, graphs), Shortcuts (the four shortcuts and starting with Windows), Sections (profiles, which sections, their order), Tray and strip, and Data (update rates, monitoring tools, extra sensors, ping host, weather town).

Settings are kept in `%APPDATA%\Glance\config.json`. Two options only exist there: `claude.show` (which limits to list, in order, with `*` wildcards, for example `["session", "weekly_fable"]`) and `claude.labels` (rename a limit, for example `{ "weekly_all": "This week" }`). Restart Glance after editing the file by hand.

## What needs what

Most sections work for any user on any PC. A few depend on the machine:

- **FPS** needs permission to read Windows performance events: an administrator, or an account in the Performance Log Users group. If the account lacks it, the FPS section says so and Settings offers a button that adds the account to that group (Windows asks for approval, then sign out and back in).
- **Web and LAN split** in the Network section needs Glance to be run as administrator, because only administrators may read per-connection network events. Settings, Shortcuts has "Start it as administrator", which sets that up with one approval. Otherwise the section shows total download and upload.
- **GPU temperature, power, fan and clock** come from the NVIDIA driver. On AMD and Intel graphics the section shows load and VRAM.
- **CPU temperature and power** cannot be read by Windows without a hardware driver, so Glance borrows them from a monitoring tool if one is running:
  - HWiNFO, with Shared Memory Support switched on in its settings.
  - MSI Afterburner, with CPU temperature and CPU power ticked on its Monitoring tab.
  - CPU-Z is not supported: it has no interface other programs can read.
- **The overlay itself** cannot draw over a game in exclusive full screen. Borderless or windowed full screen works.

## Privacy

- **Claude usage:** Glance reads the access token Claude Code keeps in `%USERPROFILE%\.claude\.credentials.json` and asks `https://api.anthropic.com/api/oauth/usage` for the figures that `/usage` shows. The token is read fresh for each request, sent only to api.anthropic.com, and never stored, logged or renewed by Glance. If it has expired the widget says so, and any Claude Code activity renews it. That endpoint is not documented by Anthropic and could change. The last response is saved to `%APPDATA%\Glance\last-usage.json` (usage numbers only) for troubleshooting.
- **Weather:** nothing is sent until you pick a town. After that, only that town's coordinates, rounded to about a kilometre, go to open-meteo.com.
- **Ping:** off until you add the section. It then pings the host you chose while the section is showing.
- **Everything else** is read locally and goes nowhere.

Errors are written to `%APPDATA%\Glance\glance.log`.

## Adding a section

Implement `IMetricProvider` (see [Core/Metrics.cs](Core/Metrics.cs)) and register it in [Sections.cs](Sections.cs): add a name to `All` and a line to `Create`. Each provider is one section. A reading is a `MetricItem`:

- Give it a `Percent` and it is drawn as a row with a bar. Leave that out and it becomes a compact tile, two to a line.
- Give it a `Key`, a `Number` and a `Unit` and it can be put in the tray icon and the compact strip, and is logged by benchmarks.
- Set `Graph` and it gets a history graph and a lowest and highest range when those are switched on.

```csharp
public sealed class FanProvider : IMetricProvider
{
    public string Title => "Fans";
    public string Id => "fans";
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(2);

    public Task<MetricSnapshot> FetchAsync(CancellationToken ct) =>
        Task.FromResult(new MetricSnapshot
        {
            Subtitle = "case",
            Items =
            [
                new MetricItem("Front") { Key = "front", Percent = 45, ValueText = "45%", Number = 45, Unit = "%", Graph = true, GraphMax = 100 },
                new MetricItem("Rear") { Key = "rear", ValueText = "900 RPM", Number = 900, Unit = "RPM" },
            ],
        });
}
```

`FetchAsync` runs on a worker thread and only while something wants the section. Implement `IDisposable` if the provider holds handles, and `IPausable` if it keeps something running between fetches that should stop when nothing wants it.

## Development

- `--demo`: made-up data instead of the real sources.
- `--show` and `--compact`: start with the widget or the strip visible.
- `--snapshot <file.png>` and `--snapshot-settings <file.png> --tab <n>`: render the widget or a settings tab to an image and exit. `--seconds <n>` sets how long to gather readings first.
- `--probe <file.txt> --seconds <n>`: write what every section of the profile in use reads on this PC to a text file, with no window. Useful when a section misbehaves on someone else's machine.
- `--probe-events <file.txt> --seconds <n>`: count graphics events per program, for when a game's frame rate is not being picked up.
- `--benchmark --seconds <n>`: record a benchmark of that length and exit.
- `--drag "memory:350,0;disk:1200,50"`: move panels as if dragged by their handles to those points (measured within the area the panels sit in), logging what each move did. This is how panel dragging is tested without a mouse.
- `GLANCE_NO_CLAUDE`: set it and Glance never looks at the Claude Code sign-in, whatever the settings say. Use it for any test run.
- `GLANCE_HOME`: environment variable that points Glance at a different settings folder. A copy started this way is a separate instance, so it can run next to your normal one. Benchmarks then go to `benchmarks` inside that folder.
- `GLANCE_NO_NVML`: set it to test the vendor-neutral GPU path on an NVIDIA machine.
- `tools\make-icon.ps1` redraws `Glance.ico`.
