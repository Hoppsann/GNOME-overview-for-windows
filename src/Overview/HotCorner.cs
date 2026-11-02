using System;
using System.Windows.Threading;

namespace Overview;

/// <summary>GNOME-style hot corner: pushing the pointer into a top-left screen corner opens the overview.</summary>
internal sealed class HotCorner
{
    readonly DispatcherTimer _timer;
    readonly Action _trigger;
    bool _inCorner;

    public HotCorner(Action trigger)
    {
        _trigger = trigger;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += Tick;
    }

    public bool Enabled
    {
        get => _timer.IsEnabled;
        set { _timer.IsEnabled = value; _inCorner = false; }
    }

    void Tick(object? sender, EventArgs e)
    {
        if (!Native.GetCursorPos(out var p)) return;
        var m = Native.MonitorRect(Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST));

        // Only a real corner counts: no other monitor continues to the left of or above it.
        bool at = p.X <= m.Left && p.Y <= m.Top
                  && Native.MonitorFromPoint(new Native.POINT(p.X - 1, p.Y), Native.MONITOR_DEFAULTTONULL) == IntPtr.Zero
                  && Native.MonitorFromPoint(new Native.POINT(p.X, p.Y - 1), Native.MONITOR_DEFAULTTONULL) == IntPtr.Zero;

        if (at && !_inCorner)
        {
            _inCorner = true;
            bool buttonDown = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (!buttonDown && !Native.IsForegroundFullscreen()) _trigger();
        }
        else if (!at)
        {
            _inCorner = false;
        }
    }
}
