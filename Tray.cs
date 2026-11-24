using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Glance.Core;
using Glance.Interop;
using Microsoft.Win32;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace Glance;

/// <summary>
/// The icon near the clock, which can show one live reading as a number, and the menu shared by
/// the icon and the widget's right-click.
/// </summary>
internal sealed class Tray : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private readonly WF.ContextMenuStrip _menu = new();
    private readonly WF.ToolStripMenuItem _toggle;
    private readonly WF.ToolStripMenuItem _compact;
    private readonly WF.ToolStripMenuItem _benchmark;
    private readonly WF.ToolStripMenuItem _profiles;
    private readonly WF.ToolStripMenuItem _clickThrough;
    private readonly WF.ToolStripMenuItem _claudeCheck;
    private SD.Icon _current;
    private IntPtr _currentHandle;
    private string _drawn = "";
    private string _baseTip = "Glance";

    public event Action? ToggleRequested;
    public event Action? CompactRequested;
    public event Action? BenchmarkRequested;
    public event Action<string>? ProfileRequested;
    public event Action? RefreshRequested;
    public event Action? ClaudeCheckRequested;
    public event Action? ResetRangesRequested;

    /// <summary>Whether this PC has a Claude section to check. Hides the menu item when it does not.</summary>
    public bool HasClaude
    {
        set => _claudeCheck.Visible = value;
    }
    public event Action<bool>? ClickThroughChanged;
    public event Action? BenchmarksFolderRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    /// <param name="visible">False to leave the icon out of the tray, for runs that only take a picture.</param>
    public Tray(bool visible = true)
    {
        _toggle = new WF.ToolStripMenuItem("Show / hide", null, (_, _) => ToggleRequested?.Invoke());
        _compact = new WF.ToolStripMenuItem("Compact strip", null, (_, _) => CompactRequested?.Invoke());
        _benchmark = new WF.ToolStripMenuItem("Start benchmark", null, (_, _) => BenchmarkRequested?.Invoke());
        _profiles = new WF.ToolStripMenuItem("Profile");

        // Kept in this menu as well as in Settings: with click-through on, the widget itself cannot be clicked.
        // CheckOnClick flips Checked before Click fires, so the handler reads the new state.
        _clickThrough = new WF.ToolStripMenuItem("Click-through") { CheckOnClick = true };
        _clickThrough.Click += (_, _) => ClickThroughChanged?.Invoke(_clickThrough.Checked);

        // Claude usage is otherwise checked at most every three minutes. This is the same as the button on its panel.
        _claudeCheck = new WF.ToolStripMenuItem("Check Claude usage now", null, (_, _) => ClaudeCheckRequested?.Invoke());

        _menu.Items.AddRange(
        [
            _toggle,
            _compact,
            _benchmark,
            new WF.ToolStripSeparator(),
            _profiles,
            new WF.ToolStripSeparator(),
            new WF.ToolStripMenuItem("Refresh now", null, (_, _) => RefreshRequested?.Invoke()),
            _claudeCheck,
            new WF.ToolStripMenuItem("Reset lowest and highest", null, (_, _) => ResetRangesRequested?.Invoke()),
            _clickThrough,
            new WF.ToolStripSeparator(),
            new WF.ToolStripMenuItem("Open benchmarks folder", null, (_, _) => BenchmarksFolderRequested?.Invoke()),
            new WF.ToolStripMenuItem("Settings…", null, (_, _) => SettingsRequested?.Invoke()),
            new WF.ToolStripSeparator(),
            new WF.ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke()),
        ]);

        _current = AppIcon.Draw(WF.SystemInformation.SmallIconSize.Width, out _currentHandle);
        _icon = new WF.NotifyIcon
        {
            Icon = _current,
            Text = _baseTip,
            ContextMenuStrip = _menu,
            Visible = visible,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left) ToggleRequested?.Invoke();
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left) SettingsRequested?.Invoke();
        };
    }

    /// <summary>Brings the menu and tooltip in line with the settings and what is going on.</summary>
    public void Update(AppConfig config, bool recording)
    {
        _toggle.ShortcutKeyDisplayString = config.Hotkey;
        _compact.ShortcutKeyDisplayString = config.CompactHotkey;
        _benchmark.ShortcutKeyDisplayString = config.BenchmarkHotkey;
        _benchmark.Text = recording ? "Stop benchmark" : "Start benchmark";
        _clickThrough.Checked = config.ClickThrough;

        _profiles.DropDownItems.Clear();
        foreach (var profile in config.Profiles)
        {
            var name = profile.Name;
            _profiles.DropDownItems.Add(new WF.ToolStripMenuItem(name, null, (_, _) => ProfileRequested?.Invoke(name))
            {
                Checked = name == config.Profile,
            });
        }

        _baseTip = $"Glance ({config.Hotkey})";
        if (_drawn.Length == 0) _icon.Text = _baseTip;
    }

    /// <summary>
    /// Draws a reading into the icon as a number, coloured by its level. Null goes back to the plain icon.
    /// </summary>
    /// <param name="tooltip">What the number is, shown when the pointer rests on the icon.</param>
    public void ShowReading(string? text, MetricLevel level, string tooltip)
    {
        var signature = text is null ? "" : $"{text}|{level}";
        if (signature != _drawn)
        {
            _drawn = signature;
            var size = WF.SystemInformation.SmallIconSize.Width;
            var icon = text is null ? AppIcon.Draw(size, out var handle) : DrawNumber(text, level, size, out handle);

            var old = _current;
            var oldHandle = _currentHandle;
            _icon.Icon = _current = icon;
            _currentHandle = handle;
            old.Dispose();
            Native.DestroyIcon(oldHandle);
        }

        // Windows cuts notification icon tooltips off at 127 characters.
        var tip = text is null ? _baseTip : tooltip;
        _icon.Text = tip.Length > 120 ? tip[..120] : tip;
    }

    /// <summary>Opens the menu at the mouse pointer, for right-clicks on the widget.</summary>
    public void ShowMenuAtCursor(IntPtr owner)
    {
        // Without a foreground window of ours the menu would not close when clicking elsewhere.
        // That takes keyboard focus from the app being used, so hand it back when the menu closes.
        var previous = Native.GetForegroundWindow();
        Native.SetForegroundWindow(owner);

        void Restore(object? sender, WF.ToolStripDropDownClosedEventArgs e)
        {
            _menu.Closed -= Restore;
            if (previous != owner && Native.GetForegroundWindow() == owner) Native.SetForegroundWindow(previous);
        }
        _menu.Closed += Restore;
        _menu.Show(WF.Cursor.Position);
    }

    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, text, WF.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _current.Dispose();
        Native.DestroyIcon(_currentHandle);
    }

    // Drawn at the exact size Windows shows it, so the digits are not blurred by scaling.
    private static SD.Icon DrawNumber(string text, MetricLevel level, int size, out IntPtr handle)
    {
        using var bitmap = new SD.Bitmap(size, size);
        using (var g = SD.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(SD.Color.Transparent);

            // A dark tile behind the digits keeps them readable on a light taskbar too.
            using var tile = new SD.SolidBrush(SD.Color.FromArgb(0x14, 0x16, 0x1B));
            AppIcon.FillRounded(g, tile, new SD.RectangleF(0, 0, size, size), size / 5f);

            var colour = level switch
            {
                MetricLevel.Critical => SD.Color.FromArgb(0xFF, 0x7B, 0x86),
                MetricLevel.Warning => SD.Color.FromArgb(0xF2, 0xB0, 0x4A),
                _ => SD.Color.White,
            };
            // Two digits fill the tile. A third has to shrink to fit.
            var scale = text.Length <= 2 ? 0.66f : text.Length == 3 ? 0.48f : 0.38f;
            using var font = new SD.Font("Segoe UI", size * scale, SD.FontStyle.Bold, SD.GraphicsUnit.Pixel);
            using var brush = new SD.SolidBrush(colour);
            using var format = new SD.StringFormat(SD.StringFormat.GenericTypographic)
            {
                Alignment = SD.StringAlignment.Center,
                LineAlignment = SD.StringAlignment.Center,
            };
            g.DrawString(text, font, brush, new SD.RectangleF(0, -size * 0.04f, size, size), format);
        }

        handle = bitmap.GetHicon();
        return SD.Icon.FromHandle(handle);
    }
}

/// <summary>The Glance mark: three meter bars on a dark tile. Drawn in code, at whatever size is asked for.</summary>
internal static class AppIcon
{
    public static SD.Icon Draw(int size, out IntPtr handle)
    {
        using var bitmap = Render(size);
        handle = bitmap.GetHicon();
        return SD.Icon.FromHandle(handle);
    }

    public static SD.Bitmap Render(int size)
    {
        var bitmap = new SD.Bitmap(size, size);
        using var g = SD.Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(SD.Color.Transparent);

        // Laid out on a 32 unit grid and scaled.
        var unit = size / 32f;
        using var tile = new SD.SolidBrush(SD.Color.FromArgb(0x14, 0x16, 0x1B));
        FillRounded(g, tile, new SD.RectangleF(unit, unit, 30 * unit, 30 * unit), 7 * unit);

        (SD.Color Color, float Fill)[] bars =
        [
            (SD.Color.FromArgb(0x6F, 0xB1, 0xFF), 0.85f),
            (SD.Color.FromArgb(0xF2, 0xB0, 0x4A), 0.60f),
            (SD.Color.FromArgb(0xF0, 0x61, 0x6D), 0.35f),
        ];
        for (var i = 0; i < bars.Length; i++)
        {
            using var brush = new SD.SolidBrush(bars[i].Color);
            FillRounded(g, brush, new SD.RectangleF(6 * unit, (7 + i * 7) * unit, 20 * bars[i].Fill * unit, 4 * unit), 2 * unit);
        }
        return bitmap;
    }

    public static void FillRounded(SD.Graphics g, SD.Brush brush, SD.RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        using var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}

/// <summary>Per-user "run at sign-in" entry. Only ever changed by the user, from Settings.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Glance";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
