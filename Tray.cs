using System.Drawing.Drawing2D;
using Glance.Interop;
using Microsoft.Win32;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace Glance;

/// <summary>Notification-area icon and the menu shared by the icon and the widget's right-click.</summary>
internal sealed class Tray : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private readonly WF.ContextMenuStrip _menu = new();
    private readonly WF.ToolStripMenuItem _toggle;
    private readonly WF.ToolStripMenuItem _clickThrough;
    private readonly IntPtr _iconHandle;

    public event Action? ToggleRequested;
    public event Action? RefreshRequested;
    public event Action<bool>? ClickThroughChanged;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public Tray()
    {
        _toggle = new WF.ToolStripMenuItem("Show / hide", null, (_, _) => ToggleRequested?.Invoke());

        // Kept in this menu as well as in Settings: with click-through on, the widget itself cannot be clicked.
        // CheckOnClick flips Checked before Click fires, so the handler reads the new state.
        _clickThrough = new WF.ToolStripMenuItem("Click-through") { CheckOnClick = true };
        _clickThrough.Click += (_, _) => ClickThroughChanged?.Invoke(_clickThrough.Checked);

        _menu.Items.AddRange(
        [
            _toggle,
            new WF.ToolStripMenuItem("Refresh now", null, (_, _) => RefreshRequested?.Invoke()),
            new WF.ToolStripSeparator(),
            _clickThrough,
            new WF.ToolStripMenuItem("Settings…", null, (_, _) => SettingsRequested?.Invoke()),
            new WF.ToolStripSeparator(),
            new WF.ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke()),
        ]);

        _icon = new WF.NotifyIcon
        {
            Icon = DrawIcon(out _iconHandle),
            Text = "Glance",
            ContextMenuStrip = _menu,
            Visible = true,
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

    public void Update(AppConfig config)
    {
        _toggle.ShortcutKeyDisplayString = config.Hotkey;
        _clickThrough.Checked = config.ClickThrough;
        _icon.Text = $"Glance ({config.Hotkey})";
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
        Native.DestroyIcon(_iconHandle);
    }

    // Drawn in code so the project needs no image assets: three meter bars on a dark tile.
    private static SD.Icon DrawIcon(out IntPtr handle)
    {
        using var bitmap = new SD.Bitmap(32, 32);
        using (var g = SD.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(SD.Color.Transparent);

            using var tile = new SD.SolidBrush(SD.Color.FromArgb(0x14, 0x16, 0x1B));
            FillRounded(g, tile, new SD.RectangleF(1, 1, 30, 30), 7);

            (SD.Color Color, float Fill)[] bars =
            [
                (SD.Color.FromArgb(0x6F, 0xB1, 0xFF), 0.85f),
                (SD.Color.FromArgb(0xF2, 0xB0, 0x4A), 0.60f),
                (SD.Color.FromArgb(0xF0, 0x61, 0x6D), 0.35f),
            ];
            for (var i = 0; i < bars.Length; i++)
            {
                using var brush = new SD.SolidBrush(bars[i].Color);
                FillRounded(g, brush, new SD.RectangleF(6, 7 + i * 7, 20 * bars[i].Fill, 4), 2);
            }
        }

        handle = bitmap.GetHicon();
        return SD.Icon.FromHandle(handle);
    }

    private static void FillRounded(SD.Graphics g, SD.Brush brush, SD.RectangleF r, float radius)
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
