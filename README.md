# Glance

A small always-on-top overlay for Windows 10 and 11. It stays hidden until you press a global shortcut, then sits above your other windows without taking keyboard focus. Nothing is measured while it is hidden, and everything refreshes the moment you show it.

It detects the hardware of whatever PC it runs on, so the same exe can be handed to other people.

## What it shows

Each block is a section you can switch on or off in Settings.

| Section | Readings | Source |
| --- | --- | --- |
| Claude usage | Current session, week (all models), per-model weekly limits such as Fable, each with its reset time | Your Claude Code sign-in |
| FPS | Now, frame time, average and 1% low for the app in front | Windows frame events (Direct3D 9 to 12) |
| GPU | Load, VRAM, and on NVIDIA cards temperature, power, fan and clock | NVIDIA driver, or Windows counters for AMD and Intel |
| CPU | Load, busiest core, clock speed, plus temperature and power if a monitoring tool is running | Windows counters, HWiNFO or MSI Afterburner |
| Memory | RAM in use, free, committed | Windows |
| Disk activity | Active time of the busiest disk, read and write speed | Windows counters |
| Drive space | Free space per fixed drive | Windows |
| Network | Download and upload, split into web and LAN when run as administrator | Windows counters and network events |
| Weather | Temperature, feels like, wind, humidity, today's range, rain chance | Open-Meteo |
| PC specs | CPU, GPUs, memory, display, Windows version | Windows |

## Build and run

Needs the .NET 10 SDK.

```powershell
.\publish.ps1
.\dist\Glance.exe
```

`publish.ps1` makes two builds:

- `dist\Glance.exe`: small, needs the .NET 10 Desktop Runtime. Use this on your own PC.
- `dist\share\Glance.exe`: about 70 MB with everything included. Runs on any 64-bit Windows 10 or 11 PC with nothing installed. Send this one to other people.

The first launch shows the widget once so you can see it. After that it starts hidden.

Preview the layout with made-up numbers (touches no account, network or hardware):

```powershell
.\dist\Glance.exe --demo --show
```

## Using it

| Action | How |
| --- | --- |
| Show or hide | `Ctrl+Alt+U` (change it in Settings), or left-click the icon near the clock |
| Settings | Move the pointer over the widget and click the gear, or right-click the widget, or double-click the icon near the clock |
| Move | Drag the widget, or pick a corner in Settings |
| Refresh now | Double-click the widget |

Starting `Glance.exe` while it is already running shows the widget, which makes a desktop shortcut work as a "show" button.

## Settings

Everything in the settings window applies immediately.

- **Shortcut:** click the button, then press the keys.
- **Position:** a corner or edge of the screen the widget is on, or wherever you drag it. A pinned widget stays in its corner as sections come and go.
- **Columns:** 1 to 4. If the sections do not fit the height of the screen, more columns are used automatically.
- **Size and opacity:** sliders.
- **Start with Windows, show at startup, click-through.** With click-through on, the widget ignores the mouse, so turn it off again from the icon near the clock.
- **Sections:** tick the ones you want.
- **Updates:** how often hardware and Claude usage refresh while the widget is visible.
- **Weather:** type a town and press Find.

Settings are kept in `%APPDATA%\Glance\config.json`. Two options only exist there: `claude.show` (which limits to list, in order, with `*` wildcards, for example `["session", "weekly_fable"]`) and `claude.labels` (rename a limit, for example `{ "weekly_all": "This week" }`). Restart Glance after editing the file by hand.

## What needs what

Most sections work for any user on any PC. A few depend on the machine:

- **FPS** needs permission to read Windows performance events: an administrator, or an account in the Performance Log Users group. If the account lacks it, the FPS section says so and Settings offers a button that adds the account to that group (Windows asks for approval, then sign out and back in). FPS counts Direct3D frames. Games using Vulkan or OpenGL are only counted when the driver presents them through DirectX, which recent NVIDIA drivers do by default.
- **Web and LAN split** in the Network section needs Glance to be run as administrator, because only administrators may read per-connection network events. Otherwise the section shows total download and upload.
- **GPU temperature, power, fan and clock** come from the NVIDIA driver. On AMD and Intel graphics the section shows load and VRAM.
- **CPU temperature and power** cannot be read by Windows without a hardware driver, so Glance borrows them from a monitoring tool if one is running:
  - HWiNFO, with Shared Memory Support switched on in its settings.
  - MSI Afterburner, with CPU temperature and CPU power ticked on its Monitoring tab.
  - CPU-Z is not supported: it has no interface other programs can read.

## Privacy

- **Claude usage:** Glance reads the access token Claude Code keeps in `%USERPROFILE%\.claude\.credentials.json` and asks `https://api.anthropic.com/api/oauth/usage` for the figures that `/usage` shows. The token is read fresh for each request, sent only to api.anthropic.com, and never stored, logged or renewed by Glance. If it has expired the widget says so, and any Claude Code activity renews it. That endpoint is not documented by Anthropic and could change. The last response is saved to `%APPDATA%\Glance\last-usage.json` (usage numbers only) for troubleshooting.
- **Weather:** nothing is sent until you pick a town. After that, only that town's coordinates, rounded to about a kilometre, go to open-meteo.com.
- **Everything else** is read locally and goes nowhere.

Errors are written to `%APPDATA%\Glance\glance.log`.

## Adding a section

Implement `IMetricProvider` (see [Core/Metrics.cs](Core/Metrics.cs)) and add it to `BuildProviders` in [App.xaml.cs](App.xaml.cs). Each provider is one section. A reading is a `MetricItem`: give it a `Percent` and it is drawn as a row with a bar, leave that out and it becomes a compact tile, two to a line.

```csharp
public sealed class BatteryProvider : IMetricProvider
{
    public string Title => "Battery";
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(30);

    public Task<MetricSnapshot> FetchAsync(CancellationToken ct) =>
        Task.FromResult(new MetricSnapshot
        {
            Subtitle = "on mains",
            Items =
            [
                new MetricItem("Charge") { Percent = 82, ValueText = "82%" },
                new MetricItem("Health") { ValueText = "96%" },
            ],
        });
}
```

`FetchAsync` runs on a worker thread and only while the widget is visible. Implement `IDisposable` if the provider holds handles, and `IPausable` if it keeps something running between fetches that should stop while the widget is hidden.

## Development

- `--demo`: made-up data instead of the real sources.
- `--show`: start visible.
- `--snapshot <file.png>` and `--snapshot-settings <file.png>`: render the widget or the settings window to an image and exit.
- `--probe <file.txt> --seconds <n>`: write what every enabled source reads on this PC to a text file, with no window. Useful when a section misbehaves on someone else's machine.
- `GLANCE_HOME`: environment variable that points Glance at a different settings folder. A copy started this way is a separate instance, so it can run next to your normal one.
- `GLANCE_NO_NVML`: set it to test the vendor-neutral GPU path on an NVIDIA machine.
