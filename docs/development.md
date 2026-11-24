# Glance for developers

A WPF app on .NET 10. The [front page](../README.md) shows what it does, and the [guide](guide.md) explains every panel.

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

## How it fits together

- **Providers** ([Providers](../Providers)) each read one thing and return a snapshot of readings. They run on worker threads and know nothing about the screen.
- **[MetricsHost.cs](../MetricsHost.cs)** runs the providers. A section is only fetched while something wants it: the overlay, the strip, a benchmark, the tray icon or an alert. Nothing else runs, and anything kept going between fetches is paused.
- **[ViewModels.cs](../ViewModels.cs)** turns snapshots into rows, tiles and graphs, and **[OverlayWindow.xaml](../OverlayWindow.xaml)** draws them.
- **[SteadyBox.cs](../SteadyBox.cs)**, **[ColumnsPanel.cs](../ColumnsPanel.cs)** and **[TopList.cs](../Providers/TopList.cs)** keep panels, columns and lists from jumping about.
- **[Interop](../Interop)** holds the Windows interfaces: performance counters, event tracing, the NVIDIA driver, graphics adapters.

## Adding a section

Implement `IMetricProvider` (see [Core/Metrics.cs](../Core/Metrics.cs)) and register it in [Sections.cs](../Sections.cs): add a name to `All` and a line to `Create`. Each provider is one section. A reading is a `MetricItem`:

- Give it a `Percent` and it is drawn as a row with a bar. Leave that out and it becomes a compact tile, two to a line.
- Give it a `Key`, a `Number` and a `Unit` and it can be put in the tray icon and the compact strip, and is logged by benchmarks.
- Set `Graph` and it gets a history graph and a lowest and highest range when those are switched on.
- Keep the same readings from one fetch to the next, with `Format.Missing` as the text of one that has no value, so the panel does not change size. If the section is a list whose length varies, implement `ReservedRows`.

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

## Development switches

- `--demo`: made-up data instead of the real sources. `--sections "claude,fps;gpu"` picks which panels it shows, with a semicolon between columns.
- `--show` and `--compact`: start with the widget or the strip visible.
- `--snapshot <file.png>` and `--snapshot-settings <file.png> --tab <n>`: render the widget or a settings tab to an image and exit, off screen and without a tray icon. `--seconds <n>` sets how long to gather readings first, and `--transparent` leaves out the background.
- `--record <folder> --seconds <n> --frames <n>`: save the widget as a numbered picture twice a second, for making an animation. The pictures on the front page were made with this and `--demo`.
- `--probe <file.txt> --seconds <n>`: write what every section of the profile in use reads on this PC to a text file, with no window. Useful when a section misbehaves on someone else's machine.
- `--probe-events <file.txt> --seconds <n>`: count graphics events per program, for when a game's frame rate is not being picked up.
- `--probe-etw <file.txt> --seconds <n> --providers "name=GUID[:keywords];..." [--pid <n>]`: list the events any Windows event provider logs, with samples of their raw contents for one process. This is how the layout of the socket events behind the Network sections was worked out: send a known amount of data from a known process and find it in the events. `--key-bytes`, `--key-offset` and `--sum-offset` group that process's events by part of their contents and add up a field.
- `--benchmark --seconds <n>`: record a benchmark of that length and exit.
- `--drag "memory:350,0;disk:1200,50"`: move panels as if dragged by their handles to those points (measured within the area the panels sit in), logging what each move did. This is how panel dragging is tested without a mouse.
- `--watch-layout <file.txt> --seconds <n>`: log every change in the size or place of any panel, then exit. With `--show`, this is how to check that panels stay steady.
- `GLANCE_NO_CLAUDE`: set it and Glance never looks at the Claude Code sign-in, whatever the settings say. Use it for any test run.
- `GLANCE_HOME`: environment variable that points Glance at a different settings folder. A copy started this way is a separate instance with its own event sessions, so it can run next to your normal one without disturbing it. Benchmarks then go to `benchmarks` inside that folder.
- `GLANCE_NO_NVML`: set it to test the vendor-neutral GPU path on an NVIDIA machine.
- `tools\make-icon.ps1` redraws `Glance.ico`.
