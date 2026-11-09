using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Remy.App.ViewModels;

namespace Remy.App.Overlay;

/// <summary>
/// Topmost, never-activating, click-through overlay window (parity/ui.md 4.1/4.2). Created in code (no XAML) because it only hosts OverlayPill.
/// Call <see cref="BeginSession"/> at dictation start so the monitor is chosen once per session.
/// </summary>
public sealed class OverlayWindow : Window
{
    readonly OverlayViewModel _vm;
    readonly OverlayPill _pill = new();
    IntPtr _hwnd, _monitor;
    DispatcherTimer? _hideTimer;
    bool _shown;

    public OverlayWindow(OverlayViewModel vm)
    {
        _vm = vm;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; ResizeMode = ResizeMode.NoResize; Focusable = false;
        Title = "remy.recording-overlay"; UseLayoutRounding = true;
        Width = 380; Height = 177;
        Content = _pill;
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, "remy.recording-overlay");
        _pill.Attach(vm);
        _pill.SnapHidden();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            ex |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => Reposition());
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(OverlayViewModel.State)) OnState();
            else if (e.PropertyName is nameof(OverlayViewModel.Size) && _shown) Reposition();
        };
    }

    /// <summary>Pins the monitor for this dictation (foreground window caret/rect, else cursor, else primary) and cancels a pending hide.</summary>
    public void BeginSession()
    {
        _hideTimer?.Stop();
        _monitor = IntPtr.Zero;
        if (_shown) { _pill.SnapHidden(); }
        _monitor = Native.PickMonitor();
    }

    void OnState()
    {
        if (_vm.State == OverlayState.Hidden) { Hide(0.3); return; }
        _hideTimer?.Stop();
        if (!_shown || !IsVisible)
        {
            if (_monitor == IntPtr.Zero || !Native.MonitorExists(_monitor)) _monitor = Native.PickMonitor();
            _shown = true;
            _pill.Refresh(animate: false);
            Reposition();
            Show();
            Reposition();
            _pill.SnapHidden();
            _pill.SetPresented(true);
        }
        else
        {
            if (Native.MonitorExists(_monitor) is false) _monitor = Native.PickMonitor();
            _pill.SetPresented(true);
            Reposition();
        }
    }

    /// <summary>Fade out and order the window out after the fade unless re-shown meanwhile.</summary>
    void Hide(double fadeSeconds)
    {
        if (!_shown) return;
        _shown = false;
        _pill.SetPresented(false);
        _hideTimer?.Stop();
        _hideTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(fadeSeconds + 0.02) };
        _hideTimer.Tick += (_, _) => { _hideTimer!.Stop(); if (!_shown) base.Hide(); };
        _hideTimer.Start();
    }

    /// <summary>Bottom-center of the chosen monitor's work area; pill bottom 16 DIP above it. Physical-pixel math, per-monitor DPI.</summary>
    public void Reposition()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_monitor == IntPtr.Zero || !Native.MonitorExists(_monitor)) _monitor = Native.PickMonitor();
        if (!Native.TryGetWork(_monitor, out var work, out uint dpi)) return;
        double scale = dpi / 96.0;
        var m = _pill.Metrics;
        int w = (int)Math.Round(m.CanvasWidth * scale), h = (int)Math.Round(m.CanvasHeight * scale);
        int x = work.left + ((work.right - work.left) - w) / 2;
        int y = work.bottom - (int)Math.Round(6 * scale) - h;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
    }
}

internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOACTIVATE = 0x10;
    const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO i);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr m, int type, out uint x, out uint y);

    /// <summary>Monitor of the foreground window's caret, else its rect, else the cursor, else primary.</summary>
    public static IntPtr PickMonitor()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                var tid = GetWindowThreadProcessId(fg, out _);
                var gi = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                if (GetGUIThreadInfo(tid, ref gi) && gi.hwndCaret != IntPtr.Zero && gi.rcCaret.right > gi.rcCaret.left)
                {
                    var p = new POINT { x = gi.rcCaret.left, y = gi.rcCaret.top };
                    if (ClientToScreen(gi.hwndCaret, ref p)) { var m = MonitorFromPoint(p, MONITOR_DEFAULTTONULL); if (m != IntPtr.Zero) return m; }
                }
                if (GetWindowRect(fg, out var r))
                {
                    var m = MonitorFromPoint(new POINT { x = (r.left + r.right) / 2, y = (r.top + r.bottom) / 2 }, MONITOR_DEFAULTTONULL);
                    if (m != IntPtr.Zero) return m;
                }
            }
            if (GetCursorPos(out var c)) { var m = MonitorFromPoint(c, MONITOR_DEFAULTTONULL); if (m != IntPtr.Zero) return m; }
        }
        catch { }
        return MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
    }

    public static bool MonitorExists(IntPtr m)
    {
        if (m == IntPtr.Zero) return false;
        var i = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(m, ref i);
    }

    public static bool TryGetWork(IntPtr m, out RECT work, out uint dpi)
    {
        work = default; dpi = 96;
        var i = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(m, ref i)) return false;
        work = i.rcWork;
        if (GetDpiForMonitor(m, 0, out var dx, out _) == 0 && dx > 0) dpi = dx;
        return true;
    }
}
