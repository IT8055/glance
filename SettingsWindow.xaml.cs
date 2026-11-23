using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Glance.Interop;
using Glance.Providers;

namespace Glance;

/// <summary>
/// The options window. Every change applies to the widget immediately and is saved: there is no OK button.
/// </summary>
public partial class SettingsWindow : Window
{
    private sealed record Choice(string Name, double Value);

    private readonly AppConfig _config;
    private readonly Action<bool> _apply;
    private readonly Func<string?, bool> _trySetHotkey;
    private bool _loading;
    private bool _recording;

    /// <param name="apply">Called after each change. True when the sections or their data sources changed.</param>
    /// <param name="trySetHotkey">Registers a shortcut, or with null just releases the current one. False if taken.</param>
    public SettingsWindow(AppConfig config, Action<bool> apply, Func<string?, bool> trySetHotkey)
    {
        _config = config;
        _apply = apply;
        _trySetHotkey = trySetHotkey;
        InitializeComponent();

        PositionBox.ItemsSource = Anchors.All.Select(a => new { a.Name, a.Value }).ToList();
        HardwareRate.ItemsSource = new List<Choice>
        {
            new("4 times a second", 0.25), new("Twice a second", 0.5), new("Every second", 1),
            new("Every 2 seconds", 2), new("Every 5 seconds", 5),
        };
        ClaudeRate.ItemsSource = new List<Choice>
        {
            new("Every 30 seconds", 30), new("Every minute", 60), new("Every 2 minutes", 120), new("Every 5 minutes", 300),
        };

        Load();
    }

    /// <summary>Fills the controls from the settings. Also called when the widget changes them itself (dragging).</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            HotkeyButton.Content = _config.Hotkey;
            PositionBox.SelectedValue = _config.Anchor;
            ColumnsBox.SelectedIndex = _config.Columns - 1;
            SizeSlider.Value = Math.Round(_config.Scale * 100);
            OpacitySlider.Value = Math.Round(_config.Opacity * 100);
            StartWithWindows.IsChecked = AutoStart.IsEnabled();
            StartVisible.IsChecked = _config.StartVisible;
            ClickThrough.IsChecked = _config.ClickThrough;

            var hardware = _config.Hardware;
            HardwareRate.SelectedValue = Nearest(HardwareRate, hardware.RefreshSeconds);
            ClaudeRate.SelectedValue = Nearest(ClaudeRate, _config.Claude.RefreshSeconds);
            UseExternalSensors.IsChecked = hardware.ExternalSensors;

            ShowClaude.IsChecked = _config.Claude.Enabled;
            ShowFps.IsChecked = hardware.Fps;
            ShowGpu.IsChecked = hardware.Gpu;
            ShowCpu.IsChecked = hardware.Cpu;
            ShowMemory.IsChecked = hardware.Memory;
            ShowDisk.IsChecked = hardware.Disk;
            ShowDrives.IsChecked = hardware.Drives;
            ShowNetwork.IsChecked = hardware.Network;
            ShowWeather.IsChecked = _config.Weather.Enabled;
            ShowSpecs.IsChecked = hardware.Specs;

            PlaceBox.Text = _config.Weather.Place;
            PlaceStatus.Text = _config.Weather.Latitude is null ? "Type your town and press Find." : "";
            UnitsBox.SelectedIndex = _config.Weather.Fahrenheit ? 2 : _config.Weather.Mph ? 1 : 0;

            TracePermission.Visibility = EtwSession.IsAllowedForThisAccount() ? Visibility.Collapsed : Visibility.Visible;
            NetworkHint.Visibility = EtwSession.IsElevated() ? Visibility.Collapsed : Visibility.Visible;
            UpdateLabels();
        }
        finally
        {
            _loading = false;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_recording) StopRecording(_config.Hotkey);
        base.OnClosing(e);
    }

    // One handler for every simple control: copy the controls into the settings and apply them.
    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;

        var before = SourcesSignature();
        var hardware = _config.Hardware;

        if (PositionBox.SelectedValue is string anchor) _config.Anchor = anchor;
        _config.Columns = ColumnsBox.SelectedIndex + 1;
        _config.Scale = SizeSlider.Value / 100;
        _config.Opacity = OpacitySlider.Value / 100;
        _config.StartVisible = StartVisible.IsChecked == true;
        _config.ClickThrough = ClickThrough.IsChecked == true;

        if (HardwareRate.SelectedValue is double hardwareSeconds) hardware.RefreshSeconds = hardwareSeconds;
        if (ClaudeRate.SelectedValue is double claudeSeconds) _config.Claude.RefreshSeconds = (int)claudeSeconds;
        hardware.ExternalSensors = UseExternalSensors.IsChecked == true;

        _config.Claude.Enabled = ShowClaude.IsChecked == true;
        hardware.Fps = ShowFps.IsChecked == true;
        hardware.Gpu = ShowGpu.IsChecked == true;
        hardware.Cpu = ShowCpu.IsChecked == true;
        hardware.Memory = ShowMemory.IsChecked == true;
        hardware.Disk = ShowDisk.IsChecked == true;
        hardware.Drives = ShowDrives.IsChecked == true;
        hardware.Network = ShowNetwork.IsChecked == true;
        _config.Weather.Enabled = ShowWeather.IsChecked == true;
        hardware.Specs = ShowSpecs.IsChecked == true;

        _config.Weather.Fahrenheit = UnitsBox.SelectedIndex == 2;
        _config.Weather.Mph = UnitsBox.SelectedIndex >= 1;

        if (ReferenceEquals(sender, StartWithWindows))
        {
            try { AutoStart.Set(StartWithWindows.IsChecked == true); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                StartWithWindows.IsChecked = AutoStart.IsEnabled();
            }
        }

        UpdateLabels();
        _apply(before != SourcesSignature());
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
        return string.Join('|', _config.Claude.Enabled, _config.Claude.RefreshSeconds, h.RefreshSeconds, h.Fps, h.Gpu, h.Cpu,
            h.Memory, h.Disk, h.Drives, h.Network, h.Specs, h.ExternalSensors, w.Enabled, w.Latitude, w.Longitude, w.Fahrenheit, w.Mph);
    }

    private static double Nearest(ComboBox box, double value) =>
        ((IEnumerable<Choice>)box.ItemsSource).MinBy(c => Math.Abs(c.Value - value))!.Value;

    // Shortcut recorder: click the button, then press the combination.

    private void OnRecordHotkey(object sender, RoutedEventArgs e)
    {
        if (_recording) return;
        _recording = true;
        // Release the current shortcut so pressing it again is seen here instead of toggling the widget.
        _trySetHotkey(null);
        HotkeyButton.Content = "Press the new shortcut…";
        HotkeyHint.Text = "Esc cancels.";
        HotkeyButton.Focus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_recording)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return; // still holding modifiers down: wait for the real key
        }

        if (key == Key.Escape)
        {
            StopRecording(_config.Hotkey);
            HotkeyHint.Text = "Shows and hides the widget from anywhere. Click to change it.";
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        // A bare letter or digit as a global shortcut would swallow that key in every program.
        var needsModifier = key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or Key.Space or Key.Enter or Key.Tab or Key.Back;
        if (parts.Count == 0 && needsModifier)
        {
            HotkeyHint.Text = "Hold Ctrl, Alt, Shift or Win as well.";
            return;
        }

        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString());
        var shortcut = string.Join('+', parts);

        if (_trySetHotkey(shortcut))
        {
            _config.Hotkey = shortcut;
            StopRecording(shortcut, register: false);
            HotkeyHint.Text = "Shows and hides the widget from anywhere. Click to change it.";
            _apply(false);
        }
        else
        {
            StopRecording(_config.Hotkey);
            HotkeyHint.Text = $"{shortcut} is already used by another app. The shortcut is unchanged.";
        }
    }

    private void StopRecording(string shortcut, bool register = true)
    {
        _recording = false;
        if (register) _trySetHotkey(shortcut);
        HotkeyButton.Content = shortcut;
    }

    // Weather place search.

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
        _config.Weather.Enabled = true;

        _loading = true;
        PlaceBox.Text = place.Name;
        ShowWeather.IsChecked = true;
        _loading = false;

        PlaceStatus.Text = "";
        _apply(true);
    }

    // Permission for FPS and the web/LAN split, for accounts that do not have it yet.

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

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
