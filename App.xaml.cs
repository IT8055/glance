using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Glance.Core;
using Glance.Providers;

namespace Glance;

public partial class App : Application, ISettingsHost
{
    private enum Mode { Hidden, Full, Compact }

    // One running Glance per settings folder. A copy started with its own GLANCE_HOME (testing, a second
    // profile) is a separate instance and does not defer to the normal one.
    private static readonly string InstanceName = @"Local\Glance.Overlay" + AppPaths.InstanceTag;

    private readonly OverlayViewModel _viewModel = new();
    private readonly DisplayOptions _options = new();
    private readonly BenchmarkRecorder _recorder = new();
    private readonly UsageAlerts _alerts = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _secondTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Mutex? _instanceLock;
    private EventWaitHandle? _showSignal;
    private AppConfig _config = new();
    private OverlayWindow _window = null!;
    private MetricsHost _host = null!;
    private Tray? _tray;
    private SettingsWindow? _settings;
    private Mode _mode = Mode.Hidden;
    private bool _demo;
    private List<List<string>> _demoColumns = [];
    private bool _recording;
    private bool _stoppingBenchmark;
    private bool _listEverything;
    private string? _profileBeforeGame;
    private int _seconds;

    public static Version FullVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
    public static string Version { get; } = FullVersion.ToString(3);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceLock = new Mutex(initiallyOwned: true, InstanceName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Already running: ask that instance to show the widget, then leave.
            if (EventWaitHandle.TryOpenExisting(InstanceName + ".Show", out var signal))
            {
                signal.Set();
                signal.Dispose();
            }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("Unhandled error: " + args.Exception);
            args.Handled = true;
        };

        _demo = e.Args.Contains("--demo");
        // "--sections claude,fps;gpu" picks which sections the demo shows, with a semicolon between columns.
        if (Argument(e.Args, "--sections") is { } picked)
            _demoColumns = picked.Split(';').Select(c => c.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList()).ToList();
        var firstRun = !File.Exists(AppPaths.ConfigFile);
        _config = AppConfig.Load();
        if (firstRun) _config.Save();
        var seconds = int.TryParse(Argument(e.Args, "--seconds"), out var s) ? s : 5;

        // Development aids that run without any window.
        if (Argument(e.Args, "--probe-events") is { } eventsFile)
        {
            _ = Diagnostics.CountGraphicsEventsAsync(eventsFile, seconds).ContinueWith(_ => Dispatcher.Invoke(Shutdown));
            return;
        }
        if (Argument(e.Args, "--probe-etw") is { } etwFile)
        {
            _ = Diagnostics.ProbeProvidersAsync(etwFile, seconds, Argument(e.Args, "--providers") ?? "",
                    int.TryParse(Argument(e.Args, "--pid"), out var focus) ? focus : 0,
                    int.TryParse(Argument(e.Args, "--key-bytes"), out var keyBytes) ? keyBytes : 0,
                    int.TryParse(Argument(e.Args, "--sum-offset"), out var sumOffset) ? sumOffset : -1,
                    int.TryParse(Argument(e.Args, "--key-offset"), out var keyOffset) ? keyOffset : -1)
                .ContinueWith(_ => Dispatcher.Invoke(Shutdown));
            return;
        }
        if (Argument(e.Args, "--probe") is { } probeFile)
        {
            _ = ProbeThenExit(probeFile, seconds);
            return;
        }

        // Saving is debounced so dragging a slider does not rewrite the file on every tick.
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _config.Save();
        };

        _options.Graphs = _config.Graphs;
        _options.Ranges = _config.Ranges;
        _viewModel.Columns = _config.Columns;

        // A run that only takes pictures stays off the screen and out of the tray.
        var picture = e.Args.Any(a => a is "--snapshot" or "--record" or "--snapshot-settings");
        _window = new OverlayWindow(_config) { DataContext = _viewModel, KeepOffScreen = picture };
        _window.HotKeyPressed += OnHotKey;
        _window.RefreshRequested += () => _host.RefreshNow();
        _window.MenuRequested += () => _tray?.ShowMenuAtCursor(_window.Handle);
        _window.SettingsRequested += OpenSettings;
        _window.Moved += (x, y) =>
        {
            // Dragging the widget takes it off whatever corner it was pinned to.
            _config.X = x;
            _config.Y = y;
            _config.Anchor = Anchors.Custom;
            _config.Save();
            _settings?.Load();
        };
        _window.Closed += (_, _) => Shutdown();
        _window.CheckNowRequested += id => _host.CheckNow(id);
        _window.Rearranged += OnRearranged;

        _host = new MetricsHost(_viewModel.Sections, _options);
        _host.Fetched += OnFetched;

        _tray = new Tray(visible: !picture);
        _tray.ToggleRequested += () => OnHotKey(OverlayWindow.ShowHide);
        _tray.CompactRequested += () => OnHotKey(OverlayWindow.CompactStrip);
        _tray.BenchmarkRequested += ToggleBenchmark;
        _tray.ProfileRequested += name => SetProfile(name);
        _tray.RefreshRequested += () => _host.RefreshNow();
        _tray.ClaudeCheckRequested += () => _host.CheckNow("claude");
        _tray.ResetRangesRequested += () =>
        {
            _host.ResetRanges();
            _host.Redraw(resized: false);
        };
        _tray.ClickThroughChanged += on =>
        {
            _config.ClickThrough = on;
            Apply(sourcesChanged: false);
            _settings?.Load();
        };
        _tray.BenchmarksFolderRequested += OpenBenchmarksFolder;
        _tray.SettingsRequested += OpenSettings;
        _tray.AboutRequested += () =>
        {
            OpenSettings();
            _settings?.SelectTab(SettingsWindow.AboutTab);
        };
        _tray.ExitRequested += Shutdown;
        _tray.Update(_config, recording: false);

        RebuildProviders();

        var mainShortcutOk = RegisterHotKeys();
        if (!mainShortcutOk)
        {
            _tray.Notify("Glance shortcut unavailable",
                $"{_config.Hotkey} is not valid or is used by another app. Pick another one in Settings.");
        }
        ListenForSecondInstance();

        _secondTimer.Tick += (_, _) => EverySecond();
        _secondTimer.Start();

        var snapshotPath = Argument(e.Args, "--snapshot");
        var settingsSnapshotPath = Argument(e.Args, "--snapshot-settings");
        var startCompact = e.Args.Contains("--compact");

        if (startCompact) SetMode(Mode.Compact);
        else if (firstRun || !mainShortcutOk || _config.StartVisible || snapshotPath is not null || e.Args.Contains("--show")) SetMode(Mode.Full);

        if (firstRun && mainShortcutOk)
            _tray.Notify("Glance is running", $"Press {_config.Hotkey} to show or hide the widget. Hover over it for the settings button.");

        if (Argument(e.Args, "--drag") is { } drags) _ = SimulateDrags(drags);
        if (snapshotPath is not null) _ = SnapshotThenExit(snapshotPath, seconds);
        if (Argument(e.Args, "--record") is { } recordFolder)
            _ = RecordThenExit(recordFolder, seconds, int.TryParse(Argument(e.Args, "--frames"), out var frames) ? frames : 10);
        if (settingsSnapshotPath is not null)
            _ = SnapshotSettingsThenExit(settingsSnapshotPath, int.TryParse(Argument(e.Args, "--tab"), out var tab) ? tab : 0);
        if (e.Args.Contains("--benchmark")) _ = BenchmarkThenExit(seconds);
        if (Argument(e.Args, "--watch-layout") is { } layoutFile) _ = WatchLayoutThenExit(layoutFile, seconds);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_saveTimer.IsEnabled) _config.Save();
        _host?.Dispose();
        _tray?.Dispose();
        _showSignal?.Dispose();
        _instanceLock?.Dispose();
        base.OnExit(e);
    }

    // ---- What is shown, and what has to keep running for it ----

    /// <summary>
    /// Builds the data sources. That is the sections of the profile in use, plus any others that something
    /// else depends on: the tray icon's reading, the compact strip, usage alerts, a benchmark being recorded.
    /// A source that cannot start on this PC is logged and left out, so one bad sensor never blocks the rest.
    /// </summary>
    private void RebuildProviders()
    {
        if (_demo)
        {
            var demo = DemoProvider.All().ToList();
            var chosen = _demoColumns.Count > 0 ? _demoColumns.SelectMany(c => c).ToList() : demo.Select(p => p.Id).ToList();
            _host.SetProviders(demo, chosen);
            foreach (var section in _viewModel.Sections) section.Column = _demoColumns.FindIndex(c => c.Contains(section.Id));
            _viewModel.ProfileTitle = _demoColumns.Count > 0 ? _config.Profile.ToUpperInvariant() : "DEMO";
            UpdateDemand();
            return;
        }

        var profile = _config.ActiveProfile.Sections;
        var needed = new List<string>(profile);
        void Need(string section)
        {
            if (Sections.Exists(section) && !needed.Contains(section)) needed.Add(section);
        }

        foreach (var reading in _config.Strip) Need(SectionOfReading(reading));
        if (_config.TrayReading.Length > 0) Need(SectionOfReading(_config.TrayReading));
        if (_config.Claude.Alerts) Need("claude");
        if (_recording) foreach (var section in RecordedSections()) Need(section);
        if (_listEverything)
        {
            // While Settings is open every section is built so its readings can be offered for the tray and strip.
            // Ping is left out unless already in use: it would start contacting a host nobody asked it to.
            foreach (var (id, _) in Sections.All.Where(sec => sec.Id != "ping")) Need(id);
        }

        ExternalSensors.Enabled = _config.Hardware.ExternalSensors;
        var providers = new List<IMetricProvider>();
        var shown = new List<string>();
        foreach (var section in needed)
        {
            try
            {
                foreach (var provider in Sections.Create(section, _config, _recorder))
                {
                    providers.Add(provider);
                    if (profile.Contains(section)) shown.Add(provider.Id);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"{Sections.NameOf(section)} section is unavailable on this PC: {ex.Message}");
            }
        }

        _host.SetProviders(providers, shown);
        _viewModel.ProfileTitle = _config.Profile.ToUpperInvariant();
        ApplyLayout();
        UpdateDemand();
        if (_tray is not null) _tray.HasClaude = _host.Has("claude");
    }

    // Puts each panel in the column the user dragged it to, or hands them all back to automatic arranging.
    private void ApplyLayout()
    {
        var layout = _config.ActiveProfile.Layout;
        foreach (var section in _viewModel.Sections)
        {
            section.Column = layout?.FindIndex(column => column.Contains(Sections.SectionOf(section.Id))) ?? -1;
        }
    }

    // The user dragged a panel. Remember the arrangement for this profile.
    private void OnRearranged(IReadOnlyList<IReadOnlyList<string>> columns)
    {
        if (_demo) return;

        var profile = _config.ActiveProfile;
        var placed = new HashSet<string>();
        // Panels are named by provider. Two graphics cards are two panels of the one "gpu" section.
        profile.Layout = columns
            .Select(column => column.Select(Sections.SectionOf).Where(placed.Add).ToList())
            .Where(column => column.Count > 0)
            .ToList();
        profile.Sections = profile.Layout.SelectMany(column => column).ToList();
        ScheduleSave();
        _settings?.Load();
    }

    /// <summary>Tells the host which sections are wanted right now, so nothing else is measured.</summary>
    private void UpdateDemand()
    {
        var live = new HashSet<string>();
        var background = new HashSet<string>();

        if (_mode == Mode.Full) live.UnionWith(_viewModel.Sections.Select(sec => sec.Id));
        if (_mode == Mode.Compact) live.UnionWith(_config.Strip.Select(ProviderOfReading));
        if (_recording)
        {
            var recorded = RecordedSections();
            live.UnionWith(_host.AllSections.Select(sec => sec.Id).Where(id => recorded.Contains(Sections.SectionOf(id))));
        }

        if (_config.TrayReading.Length > 0) background.Add(ProviderOfReading(_config.TrayReading));
        if (_config.Claude.Alerts) background.Add("claude");

        _host.SetDemand(everythingLive: _listEverything, live, background);
    }

    // The frame counter and core hardware are always recorded. Disk, network and the rest only if the profile shows them.
    private HashSet<string> RecordedSections()
    {
        var profile = _config.ActiveProfile.Sections;
        return Sections.Benchmarked.Where(sec => sec is "fps" or "gpu" or "cpu" or "memory" || profile.Contains(sec)).ToHashSet();
    }

    // "gpu2.temp" comes from provider "gpu2", which belongs to section "gpu".
    private static string ProviderOfReading(string reading) => reading.IndexOf('.') is var dot and > 0 ? reading[..dot] : reading;
    private static string SectionOfReading(string reading) => Sections.SectionOf(ProviderOfReading(reading));

    private void SetMode(Mode mode)
    {
        _mode = mode;
        _viewModel.IsCompact = mode == Mode.Compact;
        if (mode == Mode.Compact) RefreshStrip();

        if (mode == Mode.Hidden) _window.HideOverlay();
        else _window.ShowOverlay();

        // Coming on screen is what refreshes every section.
        UpdateDemand();
    }

    private void OnHotKey(int slot)
    {
        switch (slot)
        {
            case OverlayWindow.ShowHide:
                SetMode(_mode switch
                {
                    Mode.Hidden => Mode.Full,
                    Mode.Full when _config.CycleCompact => Mode.Compact,
                    _ => Mode.Hidden,
                });
                break;
            case OverlayWindow.CompactStrip:
                SetMode(_mode == Mode.Compact ? Mode.Hidden : Mode.Compact);
                break;
            case OverlayWindow.Benchmark:
                ToggleBenchmark();
                break;
            case OverlayWindow.NextProfile:
                var profiles = _config.Profiles;
                var next = profiles[(profiles.FindIndex(p => p.Name == _config.Profile) + 1) % profiles.Count].Name;
                _profileBeforeGame = null; // choosing by hand overrides the automatic switch
                SetProfile(next);
                if (_mode == Mode.Hidden) SetMode(Mode.Full);
                break;
        }
    }

    private void SetProfile(string name, bool save = true)
    {
        if (name == _config.Profile || _config.Profiles.All(p => p.Name != name)) return;

        _config.Profile = name;
        RebuildProviders();
        _tray?.Update(_config, _recording);
        _settings?.Load();
        if (save) ScheduleSave();
    }

    private void OnFetched(string providerId, MetricSnapshot snapshot)
    {
        if (_mode == Mode.Compact) RefreshStrip();
        if (_config.TrayReading.Length > 0 && providerId == ProviderOfReading(_config.TrayReading)) RefreshTrayReading();
        if (providerId == "claude" && !_demo) _alerts.Check(snapshot, _config.Claude, (title, text) => _tray?.Notify(title, text));

        if (_recorder.IsRecording && RecordedSections().Contains(Sections.SectionOf(providerId)))
        {
            var title = _host.AllSections.FirstOrDefault(sec => sec.Id == providerId)?.Title ?? providerId;
            _recorder.Note(providerId, title, snapshot);
        }
    }

    private void RefreshStrip() => _viewModel.UpdateStrip(_config.Strip, _host.AllSections);

    private void RefreshTrayReading()
    {
        var reading = _config.TrayReading;
        var section = _host.AllSections.FirstOrDefault(sec => sec.Id == ProviderOfReading(reading));
        var item = reading.IndexOf('.') is var dot and > 0 ? section?.Find(reading[(dot + 1)..]) : null;

        if (section is null || item?.Number is not { } number) _tray?.ShowReading(null, MetricLevel.Normal, "");
        else _tray?.ShowReading(Format.Whole(number), item.Level, $"{section.Title}: {item.Label} {item.ValueText}");
    }

    private void EverySecond()
    {
        _seconds++;

        if (_recorder.IsRecording)
        {
            _recorder.Sample();
            _viewModel.Recording = "● REC " + TimeText.Clock(_recorder.Elapsed);
        }

        // Every other second: switch to the gaming profile while a full-screen app is in front, and back after.
        if (_seconds % 2 == 0 && _config.GameProfile.Length > 0 && FullScreen.AppInFront() is { } gameInFront)
        {
            if (gameInFront && _profileBeforeGame is null && _config.Profile != _config.GameProfile)
            {
                _profileBeforeGame = _config.Profile;
                SetProfile(_config.GameProfile, save: false);
            }
            else if (!gameInFront && _profileBeforeGame is { } before)
            {
                _profileBeforeGame = null;
                SetProfile(before, save: false);
            }
        }
    }

    // ---- Benchmark ----

    private async void ToggleBenchmark()
    {
        if (_demo || _stoppingBenchmark) return;

        if (!_recorder.IsRecording)
        {
            // Make sure the frame counter and hardware sections exist even if the profile in use hides them.
            _recording = true;
            var built = _host.AllSections.Select(sec => Sections.SectionOf(sec.Id)).ToHashSet();
            if (!RecordedSections().All(sec => built.Contains(sec) || Sections.WhyUnavailable(sec, _config) is not null)) RebuildProviders();

            _host.ResetRanges();
            _recorder.Start(_host.Provider<FpsProvider>());
            _viewModel.Recording = "● REC 0:00";
            UpdateDemand();
            _tray?.Update(_config, recording: true);
            return;
        }

        _stoppingBenchmark = true;
        try
        {
            var result = await _recorder.StopAsync();
            _recording = false;
            _viewModel.Recording = "";
            RebuildProviders();
            _host.RefreshNow();
            _tray?.Update(_config, recording: false);

            var frames = result.Frames > 0
                ? $"average {Format.Whole(result.AverageFps)} fps, 1% low {Format.Whole(result.OnePercentLow)}"
                : "no frames were counted";
            _tray?.Notify($"Benchmark saved: {result.App}",
                $"{TimeText.Clock(result.Duration)}, {frames}. " +
                (result.Folder is null ? "The files could not be written." : "The files are in " + result.Folder + "."));
        }
        finally
        {
            _stoppingBenchmark = false;
        }
    }

    // ---- Settings ----

    private bool RegisterHotKeys()
    {
        var main = _window.SetHotKey(OverlayWindow.ShowHide, _config.Hotkey);
        Log.Write(main ? $"Hotkey registered: {_config.Hotkey}" : $"Hotkey could not be registered: {_config.Hotkey}");

        foreach (var (slot, shortcut, what) in new[]
                 {
                     (OverlayWindow.CompactStrip, _config.CompactHotkey, "compact strip"),
                     (OverlayWindow.Benchmark, _config.BenchmarkHotkey, "benchmark"),
                     (OverlayWindow.NextProfile, _config.ProfileHotkey, "next profile"),
                 })
        {
            if (!_window.SetHotKey(slot, shortcut)) Log.Write($"Shortcut for {what} could not be registered: {shortcut}");
        }
        return main;
    }

    public bool TrySetHotkey(int slot, string? shortcut) => _window.SetHotKey(slot, shortcut);

    /// <summary>Pushes the current settings to the widget and schedules a save. Called after every change in Settings.</summary>
    public void Apply(bool sourcesChanged)
    {
        _viewModel.Columns = _config.Columns;
        _window.ApplyConfig(_config);

        var redraw = _options.Graphs != _config.Graphs || _options.Ranges != _config.Ranges;
        _options.Graphs = _config.Graphs;
        _options.Ranges = _config.Ranges;

        if (sourcesChanged) RebuildProviders();
        else
        {
            if (redraw)
            {
                _host.Redraw(resized: true);
                _viewModel.LayoutEpoch++;
            }
            ApplyLayout();
            UpdateDemand();
        }

        if (_mode == Mode.Compact) RefreshStrip();
        RefreshTrayReading();
        _tray?.Update(_config, _recording);
        ScheduleSave();
    }

    public IReadOnlyList<(string Key, string Name)> Readings() =>
        _host.AllSections
            .SelectMany(section => section.Items
                .Where(item => item.Number is not null)
                .Select(item => (Key: $"{section.Id}.{item.Key}", Name: $"{section.Title}: {item.Label}")))
            .ToList();

    public void OpenBenchmarksFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Benchmarks);
            Process.Start(new ProcessStartInfo(AppPaths.Benchmarks) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Log.Write("Could not open the benchmarks folder: " + ex.Message);
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OpenSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(_config, this);
            _settings.Closed += (_, _) =>
            {
                _settings = null;
                _listEverything = false;
                RebuildProviders();
            };

            // The widget needs to be on screen to see what the settings do, and every section is
            // run while the window is open so its readings can be listed.
            _listEverything = true;
            RebuildProviders();
            if (_mode == Mode.Hidden) SetMode(Mode.Full);
        }

        _settings.Show();
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    // Starting Glance a second time (for example from a shortcut) shows the widget of the running one.
    private void ListenForSecondInstance()
    {
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        var signal = _showSignal;
        new Thread(() =>
        {
            try
            {
                while (signal.WaitOne())
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_mode == Mode.Hidden) SetMode(Mode.Full);
                    });
                }
            }
            catch (ObjectDisposedException) { }
        })
        { IsBackground = true, Name = "Glance show signal" }.Start();
    }

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    // ---- Development aids ----

    // "--drag memory:400,10;network:700,300" moves panels as if dragged by their handles to those points
    // (measured within the area the panels sit in), and logs what each move did.
    private async Task SimulateDrags(string steps)
    {
        await Task.Delay(2000);
        foreach (var step in steps.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = step.Split(':', ',');
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (parts.Length != 3 ||
                !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out var x) ||
                !double.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out var y))
            {
                continue;
            }
            Log.Write("Drag " + _window.SimulatePanelDrag(parts[0], new Point(x, y)));
            await Task.Delay(300);
        }
    }

    // Writes a line every time any panel, or the widget itself, changes size or place.
    private async Task WatchLayoutThenExit(string path, int seconds)
    {
        var lines = new List<string>();
        var clock = Stopwatch.StartNew();
        var last = new Dictionary<string, Rect>();

        void Look()
        {
            var now = _window.PanelBounds().ToDictionary(p => p.Id, p => p.Bounds);
            now["(widget)"] = new Rect(0, 0, _window.ActualWidth, _window.ActualHeight);
            foreach (var (id, bounds) in now)
            {
                if (!last.TryGetValue(id, out var was))
                    lines.Add($"{clock.Elapsed.TotalSeconds,6:0.00}  {id,-11} appears  {bounds.Width:0}x{bounds.Height:0.#} at {bounds.X:0},{bounds.Y:0.#}");
                else if (Math.Abs(was.Height - bounds.Height) > 0.2 || Math.Abs(was.Width - bounds.Width) > 0.2)
                    lines.Add($"{clock.Elapsed.TotalSeconds,6:0.00}  {id,-11} resized  {was.Width:0}x{was.Height:0.#} to {bounds.Width:0}x{bounds.Height:0.#}");
                else if (Math.Abs(was.X - bounds.X) > 0.2 || Math.Abs(was.Y - bounds.Y) > 0.2)
                    lines.Add($"{clock.Elapsed.TotalSeconds,6:0.00}  {id,-11} moved    {was.X:0},{was.Y:0.#} to {bounds.X:0},{bounds.Y:0.#}");
            }
            foreach (var gone in last.Keys.Where(id => !now.ContainsKey(id)))
                lines.Add($"{clock.Elapsed.TotalSeconds,6:0.00}  {gone,-11} gone");
            last = now;
        }

        _window.LayoutUpdated += (_, _) => Look();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        File.WriteAllLines(path, lines);
        Shutdown();
    }

    private async Task SnapshotThenExit(string path, int seconds)
    {
        // Long enough for sources that need two samples, a web request, or a graph worth looking at.
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        _window.SaveSnapshot(path, transparent: Environment.GetCommandLineArgs().Contains("--transparent"));
        Shutdown();
    }

    // Saves the widget as a numbered picture twice a second, for making an animation.
    private async Task RecordThenExit(string folder, int seconds, int frames)
    {
        Directory.CreateDirectory(folder);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        for (var i = 0; i < frames; i++)
        {
            _window.SaveSnapshot(Path.Combine(folder, $"{i:000}.png"), transparent: true);
            await Task.Delay(500);
        }
        Shutdown();
    }

    private async Task SnapshotSettingsThenExit(string path, int tab)
    {
        _listEverything = true;
        RebuildProviders();

        // Shown off screen and without taking focus: this is a design check, not something to interact with.
        var settings = new SettingsWindow(_config, this)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
        };
        settings.Show();
        settings.SelectTab(tab);
        await Task.Delay(3000);
        Snapshot.Save((FrameworkElement)settings.Content, path, Color.FromRgb(0x20, 0x20, 0x20));
        Shutdown();
    }

    private async Task BenchmarkThenExit(int seconds)
    {
        await Task.Delay(1000);
        ToggleBenchmark();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        ToggleBenchmark();
        while (_recording) await Task.Delay(100);
        await Task.Delay(500);
        Shutdown();
    }

    private async Task ProbeThenExit(string path, int seconds)
    {
        var providers = new List<IMetricProvider>();
        foreach (var section in _config.ActiveProfile.Sections)
        {
            try { providers.AddRange(Sections.Create(section, _config, _recorder)); }
            catch (Exception ex) { Log.Write($"{Sections.NameOf(section)} section is unavailable on this PC: {ex.Message}"); }
        }

        var lines = new List<string>();
        var until = DateTime.UtcNow.AddSeconds(seconds);
        for (var round = 1; DateTime.UtcNow < until; round++)
        {
            lines.Add($"--- round {round} at {DateTime.Now:HH:mm:ss.fff}");
            foreach (var provider in providers)
            {
                try
                {
                    var snapshot = await Task.Run(() => provider.FetchAsync(CancellationToken.None));
                    lines.Add($"[{provider.Id}] {provider.Title} subtitle='{snapshot.Subtitle}' notice='{snapshot.Notice}' error={snapshot.IsError}");
                    foreach (var item in snapshot.Items)
                    {
                        var extras = (item.Detail is null ? "" : $" detail='{item.Detail}'") + (item.Bars is null ? "" : $" bars={item.Bars.Count}");
                        lines.Add($"    {item.Id,-14} {item.Label,-16} {item.ValueText,-18} number={item.Number:0.##} {item.Unit} bar={item.Percent:0.#} level={item.Level}{extras}");
                    }
                    if (provider is FpsProvider fps)
                        lines.AddRange(fps.LastApps.Select(app => "    rendering: " + app));
                }
                catch (Exception ex)
                {
                    lines.Add($"[{provider.Id}] EXCEPTION {ex}");
                }
            }
            await Task.Delay(1000);
        }

        foreach (var provider in providers) (provider as IDisposable)?.Dispose();
        File.WriteAllLines(path, lines);
        Shutdown();
    }
}
