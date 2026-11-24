using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Glance.Interop;
using Glance.Providers;

namespace Glance;

/// <summary>One line in a list of things to tick: a section, a strip reading, a sensor.</summary>
public sealed class TickRow(string id, string name) : Observable
{
    private bool _isOn;

    public string Id { get; } = id;
    public string Name { get; } = name;

    /// <summary>Dimmed text after the name: why it is unavailable, or its current value.</summary>
    public string Note { get; init; } = "";
    public bool IsAvailable { get; init; } = true;
    public bool IsOn { get => _isOn; set => Set(ref _isOn, value); }
}

/// <summary>
/// The options window. Every change applies to the widget immediately and is saved: there is no OK button.
/// </summary>
public partial class SettingsWindow : Window
{
    private sealed record Choice(string Name, double Value);
    private sealed record ReadingChoice(string Key, string Name);

    private const string DefaultHint = "Click a shortcut, then press the keys you want.";
    private const string NoShortcut = "None (click to set)";
    private const string NoGameProfile = "(don't switch)";

    private readonly AppConfig _config;
    private readonly ISettingsHost _host;
    private readonly ObservableCollection<TickRow> _sections = [];
    private readonly ObservableCollection<TickRow> _strip = [];
    private readonly ObservableCollection<TickRow> _sensors = [];
    private readonly DispatcherTimer _readingsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _readingsSignature = "";
    private bool _loading;
    private Button? _recording;
    private bool _adminStart;

    internal SettingsWindow(AppConfig config, ISettingsHost host)
    {
        _config = config;
        _host = host;
        InitializeComponent();

        VersionText.Text = "Glance " + App.Version;
        AboutVersion.Text = "Version " + App.Version;
        AboutAuthor.Text = About.Author;
        ShowLink(AboutWebPage, About.WebPage, About.WebPage.Replace("https://", ""));
        ShowLink(AboutRepository, About.Repository, About.Repository.Replace("https://", ""));
        ShowLink(AboutIssues, About.Issues, null);
        if (About.Email.Length > 0) ShowLink(AboutEmail, "mailto:" + About.Email, About.Email);
        else AboutContact.Visibility = Visibility.Collapsed;
        PositionBox.ItemsSource = Anchors.All.Select(a => new { a.Name, a.Value }).ToList();
        HardwareRate.ItemsSource = new List<Choice>
        {
            new("4 times a second", 0.25), new("Twice a second", 0.5), new("Every second", 1),
            new("Every 2 seconds", 2), new("Every 5 seconds", 5),
        };
        ClaudeRate.ItemsSource = new List<Choice>
        {
            new("Every 3 minutes", 180), new("Every 5 minutes", 300), new("Every 10 minutes", 600),
            new("Every 15 minutes", 900), new("Every 30 minutes", 1800),
        };
        OledRate.ItemsSource = new List<Choice>
        {
            new("every 30 seconds", 30), new("every minute", 60), new("every 3 minutes", 180),
        };
        OledTravel.ItemsSource = new List<Choice>
        {
            new("4 pixels", 4), new("8 pixels", 8), new("12 pixels", 12),
        };
        ThemeBox.ItemsSource = Themes.All.Select(t => t.Name).ToList();
        SectionList.ItemsSource = _sections;
        StripList.ItemsSource = _strip;
        SensorList.ItemsSource = _sensors;

        _adminStart = AdminStart.IsEnabled();
        Load();

        // Readings arrive a moment after the window opens, and change as hardware comes and goes.
        _readingsTimer.Tick += (_, _) => RefreshReadingLists();
        _readingsTimer.Start();
        Closed += (_, _) => _readingsTimer.Stop();
    }

    public void SelectTab(int index) => Tabs.SelectedIndex = Math.Clamp(index, 0, Tabs.Items.Count - 1);

    /// <summary>Fills the controls from the settings. Also called when the app changes them itself (dragging, a profile shortcut).</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            ThemeBox.SelectedItem = _config.Theme;
            PositionBox.SelectedValue = _config.Anchor;
            ColumnsBox.SelectedIndex = _config.Columns - 1;
            ArrangeHint.Text = _config.ActiveProfile.Layout is null
                ? "Panels are arranged automatically. Drag one by the dots beside its heading to place it yourself."
                : "You have placed the panels yourself. Choosing a number of columns here arranges them automatically again.";
            SizeSlider.Value = Math.Round(_config.Scale * 100);
            OpacitySlider.Value = Math.Round(_config.Opacity * 100);
            Graphs.IsChecked = _config.Graphs;
            Ranges.IsChecked = _config.Ranges;
            ThreadBars.IsChecked = _config.Hardware.ThreadBars;
            ClickThrough.IsChecked = _config.ClickThrough;
            OledMode.IsChecked = _config.OledMode;
            OledRate.SelectedValue = Nearest(OledRate, _config.OledMoveSeconds);
            OledTravel.SelectedValue = Nearest(OledTravel, _config.OledTravel);
            OledOptions.IsEnabled = _config.OledMode;

            foreach (var (button, slot) in ShortcutButtons()) button.Content = Shown(ShortcutOf(slot));
            if (_recording is null) HotkeyHint.Text = DefaultHint;
            CycleCompact.IsChecked = _config.CycleCompact;
            StartWithWindows.IsChecked = _adminStart || AutoStart.IsEnabled();
            AdminNotice.Visibility = _adminStart ? Visibility.Visible : Visibility.Collapsed;
            StartVisible.IsChecked = _config.StartVisible;

            ProfileBox.ItemsSource = _config.Profiles.Select(p => p.Name).ToList();
            ProfileBox.SelectedItem = _config.Profile;
            ProfileName.Text = _config.Profile;
            DeleteProfile.IsEnabled = _config.Profiles.Count > 1;
            GameProfileBox.ItemsSource = new[] { NoGameProfile }.Concat(_config.Profiles.Select(p => p.Name)).ToList();
            GameProfileBox.SelectedItem = _config.GameProfile.Length > 0 ? _config.GameProfile : NoGameProfile;
            LoadSections();

            Alerts.IsChecked = _config.Claude.Alerts;
            AlertLevels.Text = string.Join(", ", _config.Claude.AlertAt);
            AlertOnReset.IsChecked = _config.Claude.AlertOnReset;

            HardwareRate.SelectedValue = Nearest(HardwareRate, _config.Hardware.RefreshSeconds);
            ClaudeRate.SelectedValue = Nearest(ClaudeRate, _config.Claude.RefreshSeconds);
            UseExternalSensors.IsChecked = _config.Hardware.ExternalSensors;
            PingHost.Text = _config.Hardware.PingHost;
            PowerRest.Text = Number(_config.Power.RestWatts);
            PowerEfficiency.Text = Number(_config.Power.Efficiency);
            PowerSupply.Text = Number(_config.Power.SupplyWatts);
            PowerPrice.Text = Number(_config.Power.PricePerKwh);

            PlaceBox.Text = _config.Weather.Place;
            PlaceStatus.Text = _config.Weather.Latitude is null ? "Type your town and press Find." : "";
            UnitsBox.SelectedIndex = _config.Weather.Fahrenheit ? 2 : _config.Weather.Mph ? 1 : 0;

            TracePermission.Visibility = EtwSession.IsAllowedForThisAccount() ? Visibility.Collapsed : Visibility.Visible;
            UpdateLabels();
        }
        finally
        {
            _loading = false;
        }

        _readingsSignature = "";
        RefreshReadingLists();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_recording is not null) StopRecording(restore: true);
        base.OnClosing(e);
    }

    // One handler for every simple control: copy the controls into the settings and apply them.
    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;

        var before = SourcesSignature();
        var hardware = _config.Hardware;

        if (ThemeBox.SelectedItem is string theme) _config.Theme = theme;
        if (PositionBox.SelectedValue is string anchor) _config.Anchor = anchor;
        if (ReferenceEquals(sender, ColumnsBox))
        {
            // Asking for a number of columns hands the arranging back to Glance.
            _config.ActiveProfile.Layout = null;
            ArrangeHint.Text = "Panels are arranged automatically. Drag one by the dots beside its heading to place it yourself.";
        }
        _config.Columns = ColumnsBox.SelectedIndex + 1;
        _config.Scale = SizeSlider.Value / 100;
        _config.Opacity = OpacitySlider.Value / 100;
        _config.Graphs = Graphs.IsChecked == true;
        _config.Ranges = Ranges.IsChecked == true;
        hardware.ThreadBars = ThreadBars.IsChecked == true;
        _config.ClickThrough = ClickThrough.IsChecked == true;
        _config.OledMode = OledMode.IsChecked == true;
        OledOptions.IsEnabled = _config.OledMode;

        // Only when one of the two lists was the thing changed, so a value set by hand in the settings file
        // (one the lists do not offer) is not replaced by the nearest one they do.
        if (ReferenceEquals(sender, OledRate) && OledRate.SelectedValue is double oledSeconds) _config.OledMoveSeconds = (int)oledSeconds;
        if (ReferenceEquals(sender, OledTravel) && OledTravel.SelectedValue is double oledTravel) _config.OledTravel = (int)oledTravel;

        _config.CycleCompact = CycleCompact.IsChecked == true;
        _config.StartVisible = StartVisible.IsChecked == true;

        _config.GameProfile = GameProfileBox.SelectedItem is string game && game != NoGameProfile ? game : "";
        if (TrayBox.SelectedValue is string tray) _config.TrayReading = tray;

        _config.Claude.Alerts = Alerts.IsChecked == true;
        _config.Claude.AlertOnReset = AlertOnReset.IsChecked == true;
        var levels = AlertLevels.Text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => int.TryParse(t.TrimEnd('%'), out var level) ? level : 0)
            .Where(level => level is > 0 and <= 100).Distinct().Order().ToList();
        if (levels.Count > 0) _config.Claude.AlertAt = levels;

        if (HardwareRate.SelectedValue is double hardwareSeconds) hardware.RefreshSeconds = hardwareSeconds;
        if (ClaudeRate.SelectedValue is double claudeSeconds) _config.Claude.RefreshSeconds = (int)claudeSeconds;
        hardware.ExternalSensors = UseExternalSensors.IsChecked == true;
        if (PingHost.Text.Trim() is { Length: > 0 } host) hardware.PingHost = host;

        // A box left with something that is not a number keeps the previous value.
        var power = _config.Power;
        power.RestWatts = Math.Clamp(Parse(PowerRest.Text) ?? power.RestWatts, 0, 1000);
        power.Efficiency = Math.Clamp(Parse(PowerEfficiency.Text) ?? power.Efficiency, 50, 100);
        power.SupplyWatts = Math.Clamp(Parse(PowerSupply.Text) ?? power.SupplyWatts, 0, 5000);
        power.PricePerKwh = Math.Max(0, Parse(PowerPrice.Text) ?? power.PricePerKwh);

        _config.Weather.Fahrenheit = UnitsBox.SelectedIndex == 2;
        _config.Weather.Mph = UnitsBox.SelectedIndex >= 1;

        if (ReferenceEquals(sender, StartWithWindows)) SetStartWithWindows(StartWithWindows.IsChecked == true);

        UpdateLabels();
        _host.Apply(before != SourcesSignature());
    }

    private void UpdateLabels()
    {
        SizeValue.Text = $"{SizeSlider.Value:0}%";
        OpacityValue.Text = $"{OpacitySlider.Value:0}%";

        if (_config.Hardware.ExternalSensors)
        {
            ExternalSensors.Enabled = true;
            var source = ExternalSensors.Current.Source;
            SensorStatus.Text = source.Length > 0
                ? $"Reading from {source}."
                : "Neither is running right now. HWiNFO also needs Shared Memory Support switched on.";
        }
        else
        {
            SensorStatus.Text = "Adds CPU temperature and power, which Windows cannot read on its own.";
        }
    }

    // Everything that decides which sections exist and where their data comes from.
    private string SourcesSignature()
    {
        var h = _config.Hardware;
        var w = _config.Weather;
        return string.Join('|', _config.Profile, string.Join(',', _config.ActiveProfile.Sections), string.Join(',', _config.Strip),
            string.Join(',', h.Sensors), _config.TrayReading, _config.Claude.Alerts, _config.Claude.RefreshSeconds, h.RefreshSeconds,
            h.ExternalSensors, h.ThreadBars, h.PingHost, w.Latitude, w.Longitude, w.Fahrenheit, w.Mph,
            _config.Power.RestWatts, _config.Power.Efficiency, _config.Power.SupplyWatts, _config.Power.PricePerKwh);
    }

    private static string Number(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    // Accepts "0.28" and, for people whose keyboards give a comma, "0,28".
    private static double? Parse(string text) =>
        double.TryParse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

    private static double Nearest(ComboBox box, double value) =>
        ((IEnumerable<Choice>)box.ItemsSource).MinBy(c => Math.Abs(c.Value - value))!.Value;

    // ---- Profiles and sections ----

    private void LoadSections()
    {
        var on = _config.ActiveProfile.Sections;
        var order = on.Concat(Sections.All.Select(s => s.Id).Where(id => !on.Contains(id)));

        _sections.Clear();
        foreach (var id in order)
        {
            var why = Sections.WhyUnavailable(id, _config);
            _sections.Add(new TickRow(id, Sections.NameOf(id))
            {
                IsOn = on.Contains(id),
                Note = why is null ? "" : $"({why})",
                // Still tickable if it is already on, so it can be taken off.
                IsAvailable = why is null || on.Contains(id),
            });
        }
    }

    private void SaveSections()
    {
        _config.ActiveProfile.SetSections(_sections.Where(r => r.IsOn).Select(r => r.Id).ToList());
        _host.Apply(sourcesChanged: true);
    }

    private void OnListTicked(object sender, RoutedEventArgs e)
    {
        if (_loading || (sender as FrameworkElement)?.DataContext is not TickRow row) return;

        if (_sections.Contains(row)) SaveSections();
        else if (_strip.Contains(row)) SaveStrip();
        else if (_sensors.Contains(row)) SaveSensors();
    }

    private void OnSectionUp(object sender, RoutedEventArgs e) => Move(SectionList, _sections, -1, SaveSections);
    private void OnSectionDown(object sender, RoutedEventArgs e) => Move(SectionList, _sections, 1, SaveSections);
    private void OnStripUp(object sender, RoutedEventArgs e) => Move(StripList, _strip, -1, SaveStrip);
    private void OnStripDown(object sender, RoutedEventArgs e) => Move(StripList, _strip, 1, SaveStrip);

    private static void Move(ListBox list, ObservableCollection<TickRow> rows, int by, Action save)
    {
        var from = list.SelectedIndex;
        var to = from + by;
        if (from < 0 || to < 0 || to >= rows.Count) return;

        rows.Move(from, to);
        list.SelectedIndex = to;
        list.ScrollIntoView(rows[to]);
        save();
    }

    private void OnProfileChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileBox.SelectedItem is not string name || name == _config.Profile) return;

        _config.Profile = name;
        _host.Apply(sourcesChanged: true);
        Load();
    }

    private void OnNewProfile(object sender, RoutedEventArgs e)
    {
        var number = _config.Profiles.Count + 1;
        string name;
        do { name = $"Profile {number++}"; } while (_config.Profiles.Any(p => p.Name == name));

        // Starts as a copy of the profile in use, which is usually close to what is wanted.
        _config.Profiles.Add(new ProfileConfig { Name = name, Sections = _config.ActiveProfile.Sections.ToList() });
        _config.Profile = name;
        _host.Apply(sourcesChanged: true);
        Load();
        ProfileName.Focus();
        ProfileName.SelectAll();
    }

    private void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        if (_config.Profiles.Count <= 1) return;

        _config.Profiles.Remove(_config.ActiveProfile);
        _config.Profile = _config.Profiles[0].Name;
        if (_config.Profiles.All(p => p.Name != _config.GameProfile)) _config.GameProfile = "";
        _host.Apply(sourcesChanged: true);
        Load();
    }

    private void OnProfileNameKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnProfileRenamed(sender, e);
    }

    private void OnProfileRenamed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var name = ProfileName.Text.Trim();
        var profile = _config.ActiveProfile;
        if (name.Length == 0 || name == profile.Name || name == NoGameProfile || _config.Profiles.Any(p => p.Name == name))
        {
            ProfileName.Text = profile.Name;
            return;
        }

        if (_config.GameProfile == profile.Name) _config.GameProfile = name;
        profile.Name = name;
        _config.Profile = name;
        _host.Apply(sourcesChanged: false);
        Load();
    }

    // ---- Tray reading and compact strip ----

    // Rebuilds the two pickers when the set of readings changes, keeping what is chosen.
    private void RefreshReadingLists()
    {
        if (TrayBox.IsDropDownOpen) return;

        var readings = _host.Readings();
        var signature = string.Join('|', readings.Select(r => r.Key)) + "#" + string.Join('|', _config.Strip) + "#" + _config.TrayReading;
        if (signature == _readingsSignature) return;
        _readingsSignature = signature;

        var names = new Dictionary<string, string>();
        foreach (var (key, name) in readings) names.TryAdd(key, name);

        _loading = true;
        try
        {
            var tray = new List<ReadingChoice> { new("", "The Glance icon, no reading") };
            tray.AddRange(names.Select(n => new ReadingChoice(n.Key, n.Value)));
            // A chosen reading whose section is not answering right now still has to be listed, or it would be lost.
            if (_config.TrayReading.Length > 0 && !names.ContainsKey(_config.TrayReading))
                tray.Add(new ReadingChoice(_config.TrayReading, _config.TrayReading + " (not available right now)"));
            TrayBox.ItemsSource = tray;
            TrayBox.SelectedValue = _config.TrayReading;

            var selected = (StripList.SelectedItem as TickRow)?.Id;
            _strip.Clear();
            foreach (var key in _config.Strip)
            {
                _strip.Add(new TickRow(key, names.GetValueOrDefault(key, key))
                {
                    IsOn = true,
                    Note = names.ContainsKey(key) ? "" : "(not available right now)",
                });
            }
            foreach (var (key, name) in names.Where(n => !_config.Strip.Contains(n.Key)))
                _strip.Add(new TickRow(key, name));
            StripList.SelectedItem = _strip.FirstOrDefault(r => r.Id == selected);
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveStrip()
    {
        _config.Strip = _strip.Where(r => r.IsOn).Select(r => r.Id).ToList();
        _readingsSignature = "";
        _host.Apply(sourcesChanged: true);
    }

    // ---- Extra sensors ----

    private void OnSensorPickerOpened(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            _sensors.Clear();
            var chosen = _config.Hardware.Sensors;
            var seen = new HashSet<string>();
            foreach (var reading in ExternalSensors.Current.Readings)
            {
                var id = SensorsProvider.NameOf(reading);
                if (!seen.Add(id) || !double.IsFinite(reading.Value)) continue;
                _sensors.Add(new TickRow(id, reading.Label)
                {
                    IsOn = chosen.Contains(id),
                    Note = Format.WithUnit(reading.Value, reading.Unit),
                });
            }
            // Picked earlier but not being published now (the tool is closed): keep them listed so they are not lost.
            foreach (var id in chosen.Where(id => !seen.Contains(id)))
                _sensors.Add(new TickRow(id, id.Replace("|", ": ")) { IsOn = true, Note = "(not available right now)" });

            if (_sensors.Count == 0)
                _sensors.Add(new TickRow("", "Start HWiNFO or MSI Afterburner, then open this again.") { IsAvailable = false });
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveSensors()
    {
        _config.Hardware.Sensors = _sensors.Where(r => r.IsOn && r.Id.Length > 0).Select(r => r.Id).ToList();

        // Picking a sensor is asking to see it, so the section is added to the profile if it is not there.
        var profile = _config.ActiveProfile;
        if (_config.Hardware.Sensors.Count > 0 && !profile.Sections.Contains("sensors"))
        {
            profile.Add("sensors");
            _loading = true;
            LoadSections();
            _loading = false;
        }
        _host.Apply(sourcesChanged: true);
    }

    // ---- Shortcut recorder: click a button, then press the combination ----

    private IEnumerable<(Button Button, int Slot)> ShortcutButtons() =>
        [(HotkeyMain, OverlayWindow.ShowHide), (HotkeyCompact, OverlayWindow.CompactStrip),
         (HotkeyBenchmark, OverlayWindow.Benchmark), (HotkeyProfile, OverlayWindow.NextProfile)];

    private string ShortcutOf(int slot) => slot switch
    {
        OverlayWindow.ShowHide => _config.Hotkey,
        OverlayWindow.CompactStrip => _config.CompactHotkey,
        OverlayWindow.Benchmark => _config.BenchmarkHotkey,
        _ => _config.ProfileHotkey,
    };

    private void SetShortcut(int slot, string shortcut)
    {
        switch (slot)
        {
            case OverlayWindow.ShowHide: _config.Hotkey = shortcut; break;
            case OverlayWindow.CompactStrip: _config.CompactHotkey = shortcut; break;
            case OverlayWindow.Benchmark: _config.BenchmarkHotkey = shortcut; break;
            default: _config.ProfileHotkey = shortcut; break;
        }
    }

    private static string Shown(string shortcut) => shortcut.Length > 0 ? shortcut : NoShortcut;
    private int SlotOf(Button button) => ShortcutButtons().First(b => b.Button == button).Slot;

    private void OnRecordHotkey(object sender, RoutedEventArgs e)
    {
        if (_recording is not null || sender is not Button button) return;

        _recording = button;
        // Release the current shortcut so pressing it again is seen here instead of acting.
        _host.TrySetHotkey(SlotOf(button), null);
        button.Content = "Press the new shortcut…";
        HotkeyHint.Text = SlotOf(button) == OverlayWindow.ShowHide ? "Esc cancels." : "Esc cancels. Backspace removes the shortcut.";
        button.Focus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_recording is not { } button)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
        var slot = SlotOf(button);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return; // still holding modifiers down: wait for the real key
        }

        var modifiers = Keyboard.Modifiers;
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            StopRecording(restore: true);
            return;
        }
        if (key is Key.Back or Key.Delete && modifiers == ModifierKeys.None && slot != OverlayWindow.ShowHide)
        {
            SetShortcut(slot, "");
            StopRecording(restore: false);
            _host.Apply(sourcesChanged: false);
            return;
        }

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        // A bare letter or digit as a global shortcut would swallow that key in every program.
        var needsModifier = key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or Key.Space or Key.Enter or Key.Tab or Key.Back or Key.Delete;
        if (parts.Count == 0 && needsModifier)
        {
            HotkeyHint.Text = "Hold Ctrl, Alt, Shift or Win as well.";
            return;
        }

        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString());
        var shortcut = string.Join('+', parts);

        if (ShortcutButtons().Any(b => b.Slot != slot && string.Equals(ShortcutOf(b.Slot), shortcut, StringComparison.OrdinalIgnoreCase)))
        {
            StopRecording(restore: true, hint: $"{shortcut} is already one of the shortcuts above.");
        }
        else if (_host.TrySetHotkey(slot, shortcut))
        {
            SetShortcut(slot, shortcut);
            StopRecording(restore: false);
            _host.Apply(sourcesChanged: false);
        }
        else
        {
            StopRecording(restore: true, hint: $"{shortcut} is already used by another app. The shortcut is unchanged.");
        }
    }

    private void StopRecording(bool restore, string? hint = null)
    {
        if (_recording is not { } button) return;

        var slot = SlotOf(button);
        _recording = null;
        if (restore) _host.TrySetHotkey(slot, ShortcutOf(slot));
        button.Content = Shown(ShortcutOf(slot));
        HotkeyHint.Text = hint ?? DefaultHint;
    }

    // ---- Starting with Windows ----

    private void SetStartWithWindows(bool on)
    {
        try
        {
            if (!on && _adminStart)
            {
                // The administrator task is what starts it, so that is what has to go. Windows asks for approval.
                if (AdminStart.Remove()) _adminStart = false;
            }
            AutoStart.Set(on && !_adminStart);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        _loading = true;
        StartWithWindows.IsChecked = _adminStart || AutoStart.IsEnabled();
        AdminNotice.Visibility = _adminStart ? Visibility.Visible : Visibility.Collapsed;
        _loading = false;
    }

    // An earlier version could be told to start as administrator. Nothing needs that any more, so this
    // removes the task that does it and leaves Glance starting with Windows in the ordinary way.
    private void OnRemoveAdminStart(object sender, RoutedEventArgs e)
    {
        if (!AdminStart.Remove())
        {
            AdminHint.Text = "Windows did not approve the change, so nothing was changed.";
            return;
        }

        _adminStart = false;
        try
        {
            AutoStart.Set(true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        _loading = true;
        StartWithWindows.IsChecked = AutoStart.IsEnabled();
        AdminNotice.Visibility = Visibility.Collapsed;
        _loading = false;
    }

    // ---- Weather place search ----

    private void OnPlaceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnFindPlace(sender, e);
    }

    private async void OnFindPlace(object sender, RoutedEventArgs e)
    {
        var query = PlaceBox.Text.Trim();
        if (query.Length < 2 || !FindButton.IsEnabled) return;

        FindButton.IsEnabled = false;
        PlaceStatus.Text = "Searching…";
        try
        {
            var places = await WeatherProvider.SearchAsync(query);
            if (places.Count == 0)
            {
                PlaceResults.Visibility = Visibility.Collapsed;
                PlaceStatus.Text = "No place found with that name.";
                return;
            }

            _loading = true;
            PlaceResults.ItemsSource = places;
            PlaceResults.SelectedIndex = 0;
            PlaceResults.Visibility = places.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _loading = false;
            UsePlace(places[0]);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            PlaceStatus.Text = "Could not reach the place search. Check the internet connection.";
        }
        finally
        {
            _loading = false;
            FindButton.IsEnabled = true;
        }
    }

    private void OnPlaceChosen(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && PlaceResults.SelectedItem is WeatherProvider.Place place) UsePlace(place);
    }

    private void UsePlace(WeatherProvider.Place place)
    {
        _config.Weather.Place = place.Name;
        _config.Weather.Latitude = place.Latitude;
        _config.Weather.Longitude = place.Longitude;

        // Choosing a town is asking for the weather, so the section is added to the profile if it is not there.
        var profile = _config.ActiveProfile;
        profile.Add("weather");

        _loading = true;
        PlaceBox.Text = place.Name;
        LoadSections();
        _loading = false;

        PlaceStatus.Text = "";
        _host.Apply(sourcesChanged: true);
    }

    // ---- Permission for the FPS counter, for accounts that do not have it yet ----

    private void OnGrantPermission(object sender, RoutedEventArgs e)
    {
        try
        {
            // The group is addressed by its fixed identifier because its name is translated on non-English Windows.
            var account = WindowsIdentity.GetCurrent().Name.Replace("'", "''");
            var process = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -WindowStyle Hidden -Command \"Add-LocalGroupMember -SID 'S-1-5-32-559' -Member '{account}'\"")
            {
                UseShellExecute = true,
                Verb = "runas", // this is what raises the Windows approval prompt
            });
            process?.WaitForExit(15000);
            GrantStatus.Text = process is { HasExited: true, ExitCode: 0 }
                ? "Done. Sign out of Windows and back in, then FPS will appear."
                : "Windows did not confirm the change. You can add your account to Performance Log Users by hand.";
        }
        catch (Win32Exception)
        {
            GrantStatus.Text = "Cancelled. Nothing was changed.";
        }
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        _config.Save();
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.ConfigFile) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // No app is associated with .json files.
            Process.Start("notepad.exe", $"\"{AppPaths.ConfigFile}\"");
        }
    }

    private void OnOpenBenchmarks(object sender, RoutedEventArgs e) => _host.OpenBenchmarksFolder();

    // ---- About ----

    /// <summary>Which tab is About, for opening the window at it.</summary>
    public const int AboutTab = 5;

    private string? _updatePage;

    private static void ShowLink(System.Windows.Documents.Hyperlink link, string address, string? text)
    {
        link.NavigateUri = new Uri(address);
        if (text is null) return;
        link.Inlines.Clear();
        link.Inlines.Add(text);
    }

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        UpdateGet.Visibility = Visibility.Collapsed;
        UpdateStatus.Text = "Checking…";

        var result = await UpdateCheck.RunAsync(App.FullVersion);
        _updatePage = result.Page;
        UpdateStatus.Text = result.Message;
        UpdateGet.Visibility = result.Page is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateButton.IsEnabled = true;
    }

    private void OnVisitWebPage(object sender, RoutedEventArgs e) => OpenAddress(About.WebPage);

    private void OnGetUpdate(object sender, RoutedEventArgs e)
    {
        if (_updatePage is { } page) OpenAddress(page);
    }

    private void OnFollowLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        OpenAddress(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private static void OpenAddress(string address)
    {
        try
        {
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // No browser or mail program is set up to open it.
            Log.Write($"Could not open {address}: {ex.Message}");
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
