using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Overview;

/// <summary>
/// Proportional, smooth mouse-wheel scrolling for a ScrollViewer.
/// WPF scrolls a fixed 48 px for every wheel event, however small it is. Touchpads and free-spinning
/// or high-resolution wheels send many tiny events, so that adds up to far too fast. Here the distance
/// follows the wheel's actual movement (120 = one notch = Windows' "lines to scroll" setting) and the
/// view glides there instead of jumping.
/// </summary>
internal sealed class SmoothScroll
{
    const double PixelsPerLine = 16;
    const double Easing = 0.25; // share of the remaining distance covered per frame

    readonly ScrollViewer _viewer;
    double _target = double.NaN;
    bool _animating;

    SmoothScroll(ScrollViewer viewer)
    {
        _viewer = viewer;
        viewer.PreviewMouseWheel += OnWheel;
        viewer.Unloaded += (_, _) => Stop();
    }

    public static void Attach(ScrollViewer viewer) => _ = new SmoothScroll(viewer);

    static double PixelsPerNotch
    {
        get
        {
            int lines = SystemParameters.WheelScrollLines; // -1 = "one screen at a time"
            return lines > 0 ? lines * PixelsPerLine : 3 * PixelsPerLine;
        }
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewer.ScrollableHeight <= 0) return; // nothing to scroll: let parents have the wheel
        e.Handled = true;
        double from = double.IsNaN(_target) ? _viewer.VerticalOffset : _target;
        _target = Math.Clamp(from - e.Delta / 120.0 * PixelsPerNotch, 0, _viewer.ScrollableHeight);
        if (!SystemParameters.ClientAreaAnimation)
        {
            _viewer.ScrollToVerticalOffset(_target);
            _target = double.NaN;
            return;
        }
        if (!_animating)
        {
            _animating = true;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    void OnFrame(object? sender, EventArgs e)
    {
        if (double.IsNaN(_target)) { Stop(); return; }
        double current = _viewer.VerticalOffset;
        double remaining = _target - current;
        if (Math.Abs(remaining) < 0.5)
        {
            _viewer.ScrollToVerticalOffset(_target);
            Stop();
            return;
        }
        _viewer.ScrollToVerticalOffset(current + remaining * Easing);
    }

    void Stop()
    {
        if (_animating) CompositionTarget.Rendering -= OnFrame;
        _animating = false;
        _target = double.NaN;
    }
}
