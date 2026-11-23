using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Glance.Core;
using Glance.Providers;

namespace Glance;

public partial class App : Application
{
    // One running Glance per settings folder. A copy started with its own GLANCE_HOME (testing, a second
    // profile) is a separate instance and does not defer to the normal one.
    private static readonly string InstanceName = @"Local\Glance.Overlay" +
        (Environment.GetEnvironmentVariable("GLANCE_HOME") is { Length: > 0 } home
            ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(home.ToLowerInvariant())), 0, 8)
            : "");

    private readonly OverlayViewModel _viewModel = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private Mutex? _instanceLock;
    private EventWaitHandle? _showSignal;
    private AppConfig _config = new();
    private OverlayWindow _window = null!;
    private MetricsHost? _host;
    private Tray? _tray;
    private SettingsWindow? _settings;
    private bool _demo;

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
        var firstRun = !File.Exists(AppPaths.ConfigFile);
        _config = AppConfig.Load();
        if (firstRun) _config.Save();

        // Development aid: print what each data source reads on this PC, without any window.
        if (Argument(e.Args, "--probe") is { } probeFile)
        {
            _ = ProbeThenExit(probeFile, int.TryParse(Argument(e.Args, "--seconds"), out var seconds) ? seconds : 5);
            return;
        }

        // Saving is debounced so dragging a slider does not rewrite the file on every tick.
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _config.Save();
        };

        _viewModel.Columns = _config.Columns;
        _window = new OverlayWindow(_config) { DataContext = _viewModel };
        _window.HotKeyPressed += Toggle;
        _window.RefreshRequested += () => _host?.RefreshNow();
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

        _host = new MetricsHost(_viewModel.Sections);
        _host.SetProviders(BuildProviders());

        _tray = new Tray();
        _tray.ToggleRequested += Toggle;
        _tray.RefreshRequested += () => _host.RefreshNow();
        _tray.SettingsRequested += OpenSettings;
        _tray.ClickThroughChanged += on =>
        {
            _config.ClickThrough = on;
            ApplySettings(sourcesChanged: false);
            _settings?.Load();
        };
        _tray.ExitRequested += Shutdown;
        _tray.Update(_config);

        var hotKeyOk = RegisterHotKey(_config.Hotkey);
        if (!hotKeyOk)
        {
            _tray.Notify("Glance shortcut unavailable",
                $"{_config.Hotkey} is not valid or is used by another app. Pick another one in Settings.");
        }
        ListenForSecondInstance();

        var snapshotPath = Argument(e.Args, "--snapshot");
        var settingsSnapshotPath = Argument(e.Args, "--snapshot-settings");

        if (firstRun || !hotKeyOk || _config.StartVisible || snapshotPath is not null || e.Args.Contains("--show"))
            ShowOverlay();
        if (firstRun && hotKeyOk)
            _tray.Notify("Glance is running", $"Press {_config.Hotkey} to show or hide the widget. Hover over it for the settings button.");

        if (snapshotPath is not null) _ = SnapshotThenExit(snapshotPath);
        if (settingsSnapshotPath is not null) _ = SnapshotSettingsThenExit(settingsSnapshotPath);
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

    /// <summary>
    /// The sections of the widget, top to bottom. New metric providers get added here.
    /// A source that cannot start on this PC is logged and left out, so one bad sensor never blocks the rest.
    /// </summary>
    private List<IMetricProvider> BuildProviders()
    {
        if (_demo) return DemoProvider.All().ToList();

        var providers = new List<IMetricProvider>();
        void Add(string name, Func<IEnumerable<IMetricProvider>> create)
        {
            try { providers.AddRange(create()); }
            catch (Exception ex) { Log.Write($"{name} section is unavailable on this PC: {ex.Message}"); }
        }

        var hardware = _config.Hardware;
        var rate = TimeSpan.FromSeconds(hardware.RefreshSeconds);
        ExternalSensors.Enabled = hardware.ExternalSensors;

        if (_config.Claude.Enabled) Add("Claude", () => [new ClaudeUsageProvider(_config.Claude)]);
        if (hardware.Fps) Add("FPS", () => [new FpsProvider(rate)]);
        if (hardware.Gpu) Add("GPU", () => GpuProviders.Detect(rate));
        if (hardware.Cpu) Add("CPU", () => [new CpuProvider(rate)]);
        if (hardware.Memory) Add("Memory", () => [new MemoryProvider(rate)]);
        if (hardware.Disk) Add("Disk", () => [new DiskActivityProvider(rate)]);
        if (hardware.Drives) Add("Drives", () => [new DriveSpaceProvider()]);
        if (hardware.Network) Add("Network", () => [new NetworkProvider(rate)]);
        if (_config.Weather.Enabled) Add("Weather", () => [new WeatherProvider(_config.Weather)]);
        if (hardware.Specs) Add("PC specs", () => [new SpecsProvider()]);
        return providers;
    }

    private void Toggle()
    {
        if (_window.IsVisible) HideOverlay();
        else ShowOverlay();
    }

    private void ShowOverlay()
    {
        _window.ShowOverlay();
        if (_host is not null) _host.Active = true; // this also refreshes every section
    }

    private void HideOverlay()
    {
        _window.HideOverlay();
        if (_host is not null) _host.Active = false;
    }

    private bool RegisterHotKey(string? shortcut)
    {
        var ok = _window.SetHotKey(shortcut);
        if (shortcut is not null)
            Log.Write(ok ? $"Hotkey registered: {shortcut}" : $"Hotkey could not be registered: {shortcut}");
        return ok;
    }

    /// <summary>Pushes the current settings to the widget and schedules a save. Called after every change in Settings.</summary>
    private void ApplySettings(bool sourcesChanged)
    {
        _viewModel.Columns = _config.Columns;
        _window.ApplyConfig(_config);
        _tray?.Update(_config);
        if (sourcesChanged) _host?.SetProviders(BuildProviders());

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OpenSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(_config, ApplySettings, RegisterHotKey);
            _settings.Closed += (_, _) => _settings = null;
            // The widget needs to be on screen to see what the settings do.
            ShowOverlay();
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
                while (signal.WaitOne()) Dispatcher.BeginInvoke(ShowOverlay);
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

    private async Task SnapshotThenExit(string path)
    {
        // Long enough for sources that need two samples, and for a web request.
        await Task.Delay(_demo ? 1500 : 4000);
        _window.SaveSnapshot(path);
        Shutdown();
    }

    private async Task SnapshotSettingsThenExit(string path)
    {
        // Shown off screen and without taking focus: this is a design check, not something to interact with.
        var settings = new SettingsWindow(_config, _ => { }, _ => true)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
        };
        settings.Show();
        await Task.Delay(1500);
        Snapshot.Save((FrameworkElement)settings.Content, path, Color.FromRgb(0x20, 0x20, 0x20));
        Shutdown();
    }

    private async Task ProbeThenExit(string path, int seconds)
    {
        var providers = BuildProviders();
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
                    lines.Add($"[{provider.Title}] subtitle='{snapshot.Subtitle}' notice='{snapshot.Notice}' error={snapshot.IsError}");
                    foreach (var item in snapshot.Items)
                        lines.Add($"    {item.Label,-14} {item.ValueText,-16} bar={item.Percent:0.#} level={item.Level}");
                    if (provider is FpsProvider fps)
                        lines.AddRange(fps.LastApps.Select(app => "    rendering: " + app));
                }
                catch (Exception ex)
                {
                    lines.Add($"[{provider.Title}] EXCEPTION {ex}");
                }
            }
            await Task.Delay(1000);
        }

        foreach (var provider in providers) (provider as IDisposable)?.Dispose();
        File.WriteAllLines(path, lines);
        Shutdown();
    }
}
