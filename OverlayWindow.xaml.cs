using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glance.Interop;

namespace Glance;

/// <summary>
/// The widget itself: a borderless, always-on-top window that never takes keyboard focus and
/// stays out of the taskbar and Alt+Tab. It also owns the global hotkey, because the hotkey
/// needs a window handle that lives for the whole session.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int HotKeyId = 0x474C;

    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private AppConfig _config;
    private IntPtr _hwnd;
    private bool _placed;
    private bool _dragging;
    private Native.POINT _dragCursor;
    private Native.RECT _dragWindow;

    public event Action? HotKeyPressed;
    public event Action? RefreshRequested;
    public event Action? MenuRequested;
    public event Action? SettingsRequested;
    public event Action<int, int>? Moved;

    public OverlayWindow(AppConfig config)
    {
        _config = config;
        InitializeComponent();
        Zoom.ScaleX = Zoom.ScaleY = config.Scale;
        _topmostTimer.Tick += (_, _) => AssertTopmost();

        // An anchored widget stays pinned to its corner as sections come and go.
        SizeChanged += (_, _) =>
        {
            if (IsVisible && _config.Anchor != Anchors.Custom) Place();
        };

        // Create the native window now (still hidden) so the hotkey works before the first show.
        new WindowInteropHelper(this).EnsureHandle();
    }

    public IntPtr Handle => _hwnd;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        ApplyExtendedStyle();
    }

    /// <summary>Applies settings that can change at runtime (position, size, opacity, click-through).</summary>
    public void ApplyConfig(AppConfig config)
    {
        _config = config;
        ApplyExtendedStyle();
        Zoom.ScaleX = Zoom.ScaleY = config.Scale;
        if (!IsVisible) return;

        FitToScreen();
        Place();
        BeginAnimation(OpacityProperty, null);
        Opacity = _config.Opacity;
    }

    /// <summary>Registers the global show/hide shortcut. Returns false if it is invalid or already taken.</summary>
    public bool SetHotKey(string? text)
    {
        Native.UnregisterHotKey(_hwnd, HotKeyId);
        return HotKeyParser.TryParse(text, out var modifiers, out var key)
            && Native.RegisterHotKey(_hwnd, HotKeyId, modifiers | Native.MOD_NOREPEAT, key);
    }

    public void ShowOverlay()
    {
        if (IsVisible) return;

        Opacity = 0;
        FitToScreen();
        Show();
        // Re-home the widget if this is the first show or its monitor has been unplugged since.
        if (!_placed || _config.Anchor != Anchors.Custom || !IsOnAMonitor()) Place();
        AssertTopmost();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, _config.Opacity, TimeSpan.FromMilliseconds(140)));
        _topmostTimer.Start();
    }

    public void HideOverlay()
    {
        if (!IsVisible) return;

        _topmostTimer.Stop();
        BeginAnimation(OpacityProperty, null);
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer.Stop();
        Native.UnregisterHotKey(_hwnd, HotKeyId);
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt64() == HotKeyId)
        {
            handled = true;
            HotKeyPressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    private void ApplyExtendedStyle()
    {
        var style = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        style |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        style = _config.ClickThrough ? style | Native.WS_EX_TRANSPARENT : style & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(style));
    }

    // Another always-on-top window that was raised later would cover the widget, so re-assert while visible.
    private void AssertTopmost() =>
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    private void MoveTo(int x, int y) =>
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

    // Tells the layout how tall a column may be on this screen, so a long list of sections wraps into more
    // columns instead of running off the bottom. Matters most on smaller displays.
    private void FitToScreen()
    {
        if (DataContext is not OverlayViewModel viewModel) return;

        var monitor = _placed
            ? Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST)
            : Native.MonitorFromPoint(default, Native.MONITOR_DEFAULTTOPRIMARY);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) return;

        // Screen pixels to layout units, then allow for the card's own padding and shadow margin.
        var pixels = info.rcWork.Bottom - info.rcWork.Top;
        var units = pixels / VisualTreeHelper.GetDpi(this).DpiScaleY / _config.Scale;
        viewModel.MaxColumnHeight = Math.Max(200, units - 24 / _config.Scale - 22);
    }

    // Positions are kept in physical screen pixels so they survive mixed-DPI monitor setups.
    private void Place()
    {
        var firstTime = !_placed;
        _placed = true;
        UpdateLayout();
        if (!Native.GetWindowRect(_hwnd, out var rect)) return;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;

        var anchor = _config.Anchor ?? Anchors.TopRight;
        if (anchor == Anchors.Custom)
        {
            // Only trust the saved spot if it is still on a connected monitor.
            if (_config.X is { } x && _config.Y is { } y && IsOnAMonitor(x, y, width))
            {
                MoveTo(x, y);
                return;
            }
            anchor = Anchors.TopRight;
        }

        // Presets apply to the screen the widget is already on. Before its first placement that is the main screen.
        var monitor = firstTime
            ? Native.MonitorFromPoint(default, Native.MONITOR_DEFAULTTOPRIMARY)
            : Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) return;
        var work = info.rcWork;

        // The window already carries a transparent margin for its shadow, so no extra gap from the edge is needed.
        var left = anchor.EndsWith("left", StringComparison.Ordinal) ? work.Left
                 : anchor.EndsWith("right", StringComparison.Ordinal) ? work.Right - width
                 : work.Left + (work.Right - work.Left - width) / 2;
        var top = anchor.StartsWith("bottom", StringComparison.Ordinal) ? work.Bottom - height : work.Top;
        if (left != rect.Left || top != rect.Top) MoveTo(left, top);
    }

    private bool IsOnAMonitor() =>
        Native.GetWindowRect(_hwnd, out var rect) && IsOnAMonitor(rect.Left, rect.Top, rect.Right - rect.Left);

    // Checks a point near the top middle of the card, the part you would grab to drag it back.
    private static bool IsOnAMonitor(int x, int y, int width) =>
        Native.MonitorFromPoint(new Native.POINT { X = x + width / 2, Y = y + 24 }, Native.MONITOR_DEFAULTTONULL) != IntPtr.Zero;

    private void OnGearClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    // Dragging is done by hand: the usual DragMove does not track live on a window that never activates.
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            RefreshRequested?.Invoke();
            return;
        }

        if (Native.GetCursorPos(out _dragCursor) && Native.GetWindowRect(_hwnd, out _dragWindow))
            _dragging = CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging && Native.GetCursorPos(out var cursor))
            MoveTo(_dragWindow.Left + cursor.X - _dragCursor.X, _dragWindow.Top + cursor.Y - _dragCursor.Y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();
        if (Native.GetWindowRect(_hwnd, out var rect) && (rect.Left != _dragWindow.Left || rect.Top != _dragWindow.Top))
            Moved?.Invoke(rect.Left, rect.Top);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        MenuRequested?.Invoke();
    }

    /// <summary>Renders the widget to a PNG. Used by "--snapshot" for design checks.</summary>
    public void SaveSnapshot(string path) => Snapshot.Save((FrameworkElement)Content, path, Color.FromRgb(0x4A, 0x5A, 0x6E), margin: 24);
}

/// <summary>Renders a piece of UI to a PNG file without capturing the screen.</summary>
internal static class Snapshot
{
    public static void Save(FrameworkElement root, string path, Color backdrop, double margin = 0)
    {
        const double scale = 2;
        root.UpdateLayout();
        var bounds = new Rect(0, 0, root.ActualWidth + margin, root.ActualHeight + margin);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(backdrop), null, bounds);
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None }, null, bounds);
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
