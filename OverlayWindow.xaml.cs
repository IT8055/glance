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
    // Shortcut slots. Each can hold one global shortcut.
    public const int ShowHide = 0;
    public const int CompactStrip = 1;
    public const int Benchmark = 2;
    public const int NextProfile = 3;
    private const int HotKeySlots = 4;
    private const int HotKeyBase = 0x4740;

    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private AppConfig _config;
    private IntPtr _hwnd;
    private bool _placed;
    private bool _dragging;
    private Native.POINT _dragCursor;
    private Native.RECT _dragWindow;

    /// <summary>Raised with the slot (ShowHide, CompactStrip, ...) whose shortcut was pressed.</summary>
    public event Action<int>? HotKeyPressed;
    public event Action? RefreshRequested;
    public event Action? MenuRequested;
    public event Action? SettingsRequested;
    public event Action<int, int>? Moved;

    /// <summary>Raised with a section's name when its "check now" button is pressed.</summary>
    public event Action<string>? CheckNowRequested;

    /// <summary>Raised after the user drags a panel, with the new arrangement: columns of section names.</summary>
    public event Action<IReadOnlyList<IReadOnlyList<string>>>? Rearranged;

    public OverlayWindow(AppConfig config)
    {
        _config = config;
        InitializeComponent();
        Themes.Apply(Resources, Themes.Find(config.Theme));
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
        Themes.Apply(Resources, Themes.Find(config.Theme));
        Zoom.ScaleX = Zoom.ScaleY = config.Scale;
        if (!IsVisible) return;

        FitToScreen();
        Place();
        BeginAnimation(OpacityProperty, null);
        Opacity = _config.Opacity;
    }

    /// <summary>
    /// Registers a global shortcut in one slot, replacing whatever that slot held. Empty text just clears it.
    /// Returns false if the shortcut is invalid or already taken by another app.
    /// </summary>
    public bool SetHotKey(int slot, string? text)
    {
        Native.UnregisterHotKey(_hwnd, HotKeyBase + slot);
        if (string.IsNullOrWhiteSpace(text)) return true;
        return HotKeyParser.TryParse(text, out var modifiers, out var key)
            && Native.RegisterHotKey(_hwnd, HotKeyBase + slot, modifiers | Native.MOD_NOREPEAT, key);
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
        for (var slot = 0; slot < HotKeySlots; slot++) Native.UnregisterHotKey(_hwnd, HotKeyBase + slot);
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt64() - HotKeyBase is >= 0 and < HotKeySlots and var slot)
        {
            handled = true;
            HotKeyPressed?.Invoke((int)slot);
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

        // Screen pixels to layout units, then allow for the shadow margin, the title line and the card's padding.
        var pixels = info.rcWork.Bottom - info.rcWork.Top;
        var units = pixels / VisualTreeHelper.GetDpi(this).DpiScaleY / _config.Scale;
        viewModel.MaxColumnHeight = Math.Max(200, units - 24 / _config.Scale - 50);
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

    private void OnCheckNowClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SectionViewModel section) CheckNowRequested?.Invoke(section.Id);
    }

    // Two kinds of drag start here. On a panel's handle (the six dots) it moves that panel within the
    // widget. Anywhere else it moves the whole widget, done by hand because the usual DragMove does not
    // track live on a window that never activates.
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (HandleUnder(e.OriginalSource) is { } section)
        {
            BeginPanelMove(section, e.GetPosition(this));
            CaptureMouse();
            return;
        }

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

        if (_held is not null)
        {
            ContinuePanelMove(e.GetPosition(this));
            return;
        }

        if (_dragging && Native.GetCursorPos(out var cursor))
            MoveTo(_dragWindow.Left + cursor.X - _dragCursor.X, _dragWindow.Top + cursor.Y - _dragCursor.Y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_held is not null)
        {
            FinishPanelMove();
            ReleaseMouseCapture();
            return;
        }

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
        EndPanelMove();
    }

    // ---- Moving a panel within the widget ----

    private SectionViewModel? _held;
    private Point _heldAt;
    private bool _movingPanel;
    private ColumnsPanel.DropTarget? _dropTarget;
    private ColumnsPanel? _sectionsPanel;

    // The three steps of moving a panel, with points in this window's coordinates. The mouse handlers
    // above call them, and so does SimulatePanelDrag, which is how the move is tested without a mouse.

    private void BeginPanelMove(SectionViewModel section, Point at)
    {
        _held = section;
        _heldAt = at;
        _movingPanel = false;
    }

    private void ContinuePanelMove(Point here)
    {
        if (_held is null) return;

        // A few pixels of slack, so a click on the handle that wobbles is not taken for a drag.
        if (!_movingPanel && (Math.Abs(here.X - _heldAt.X) > 5 || Math.Abs(here.Y - _heldAt.Y) > 5))
        {
            _movingPanel = true;
            SetDimmed(_held, true);
        }
        if (_movingPanel && SectionsPanel() is { } panel)
        {
            _dropTarget = panel.DropTargetAt(TranslatePoint(here, panel));
            ShowDropMarker(panel, _dropTarget);
        }
    }

    /// <summary>Drops the panel being moved. False if nothing was being moved, or it never left its handle.</summary>
    private bool FinishPanelMove()
    {
        if (_held is not { } section) return false;

        var target = _movingPanel ? _dropTarget : null;
        EndPanelMove();
        if (target is not { } spot) return false;
        MovePanel(section, spot);
        return true;
    }

    /// <summary>
    /// Runs the same steps as dragging a panel by its handle to a point (given relative to the area the
    /// panels sit in). For the "--drag" development switch. Returns a short account of what happened.
    /// </summary>
    internal string SimulatePanelDrag(string sectionId, Point toInPanels)
    {
        if (DataContext is not OverlayViewModel viewModel || SectionsPanel() is not { } panel) return "no panels";
        if (viewModel.Sections.FirstOrDefault(s => s.Id == sectionId) is not { } section) return $"no panel '{sectionId}'";
        if (SectionsHost.ItemContainerGenerator.ContainerFromItem(section) is not FrameworkElement container) return "panel not drawn";

        // Start from the innermost thing a real press on the handle would hit: one of its dots.
        var handle = Descendants(container).OfType<FrameworkElement>().FirstOrDefault(e => e.Tag as string == "SectionGrip");
        var dot = handle is null ? null : Descendants(handle).OfType<System.Windows.Shapes.Ellipse>().FirstOrDefault();
        if (handle is null || dot is null) return "handle not found";
        if (!ReferenceEquals(HandleUnder(dot), section)) return "a press on the handle did not find its panel";

        BeginPanelMove(section, dot.TranslatePoint(new Point(1, 1), this));
        var destination = panel.TranslatePoint(toInPanels, this);
        ContinuePanelMove(destination);
        var marker = DropMarker.Visibility == Visibility.Visible;
        var target = _dropTarget;
        var moved = FinishPanelMove();
        return $"{sectionId}: marker shown {marker}, target column {target?.Column} index {target?.Index} new column {target?.NewColumn}, moved {moved}";

        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var deeper in Descendants(child)) yield return deeper;
            }
        }
    }

    // The panel that lays the sections out lives inside the list's template, so it has to be looked for.
    private ColumnsPanel? SectionsPanel()
    {
        return _sectionsPanel ??= Find(SectionsHost);

        static ColumnsPanel? Find(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is ColumnsPanel panel) return panel;
                if (Find(child) is { } deeper) return deeper;
            }
            return null;
        }
    }

    // The section whose drag handle is under the mouse, if any.
    private SectionViewModel? HandleUnder(object source)
    {
        for (var node = source as DependencyObject; node is not null && !ReferenceEquals(node, this);)
        {
            if (node is FrameworkElement { Tag: "SectionGrip", DataContext: SectionViewModel section }) return section;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void SetDimmed(SectionViewModel section, bool dimmed)
    {
        if (SectionsHost.ItemContainerGenerator.ContainerFromItem(section) is UIElement container)
            container.Opacity = dimmed ? 0.45 : 1;
    }

    private void ShowDropMarker(ColumnsPanel panel, ColumnsPanel.DropTarget? target)
    {
        if (target is not { } spot || DropMarker.Parent is not UIElement canvas)
        {
            DropMarker.Visibility = Visibility.Collapsed;
            return;
        }

        // The target is in the panel's coordinates. The marker is drawn on a layer over the whole list.
        var origin = panel.TranslatePoint(new Point(0, 0), canvas);
        System.Windows.Controls.Canvas.SetLeft(DropMarker, origin.X + spot.Marker.X);
        System.Windows.Controls.Canvas.SetTop(DropMarker, origin.Y + spot.Marker.Y);
        DropMarker.Width = spot.Marker.Width;
        DropMarker.Height = spot.Marker.Height;
        DropMarker.Visibility = Visibility.Visible;
    }

    private void EndPanelMove()
    {
        if (_held is { } section) SetDimmed(section, false);
        _held = null;
        _movingPanel = false;
        _dropTarget = null;
        DropMarker.Visibility = Visibility.Collapsed;
    }

    // Puts the panel where it was dropped. From then on every panel stays in the column it is in.
    private void MovePanel(SectionViewModel section, ColumnsPanel.DropTarget target)
    {
        if (DataContext is not OverlayViewModel viewModel || SectionsPanel() is not { } panel) return;

        var columns = panel.Arranged
            .Select(column => column.Select(child => (child as FrameworkElement)?.DataContext).OfType<SectionViewModel>().ToList())
            .ToList();
        var from = columns.FindIndex(column => column.Contains(section));
        if (from < 0) return;

        var to = target.Column;
        var index = target.Index;
        if (target.NewColumn)
        {
            columns.Insert(to, []);
            if (from >= to) from++;
            index = 0;
        }

        // Taking it out of an earlier place in the same column shifts the landing spot up by one.
        var was = columns[from].IndexOf(section);
        if (from == to && was < index) index--;
        columns[from].RemoveAt(was);
        columns[to].Insert(Math.Clamp(index, 0, columns[to].Count), section);
        columns.RemoveAll(column => column.Count == 0);

        for (var c = 0; c < columns.Count; c++)
        {
            foreach (var placed in columns[c]) placed.Column = c;
        }
        var order = columns.SelectMany(column => column).ToList();
        for (var i = 0; i < order.Count; i++)
        {
            var at = viewModel.Sections.IndexOf(order[i]);
            if (at >= 0 && at != i) viewModel.Sections.Move(at, i);
        }

        Rearranged?.Invoke(columns.Select(column => (IReadOnlyList<string>)column.Select(placed => placed.Id).ToList()).ToList());
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
