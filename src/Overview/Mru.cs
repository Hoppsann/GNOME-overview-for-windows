using System;
using System.Collections.Generic;
using System.Linq;

namespace Overview;

/// <summary>
/// Most-recently-used window order, like Alt+Tab keeps it. Seeded from the z-order at startup,
/// then updated every time a window comes to the foreground. UI thread only.
/// </summary>
internal static class Mru
{
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0x0000, GA_ROOTOWNER = 3;

    static readonly Dictionary<IntPtr, long> Stamps = new();
    static long _counter;
    static Native.WinEventDelegate? _proc; // keep the delegate alive
    static IntPtr _hook;

    public static void Start()
    {
        var zOrder = new List<IntPtr>();
        Native.EnumWindows((h, _) => { zOrder.Add(h); return true; }, IntPtr.Zero);
        for (int i = zOrder.Count - 1; i >= 0; i--) Stamps[zOrder[i]] = ++_counter; // top of z-order = newest

        _proc = OnForeground;
        _hook = Native.SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        if (_hook == IntPtr.Zero) Log.Write("SetWinEventHook failed; falling back to z-order");
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>Marks a window as the most recently used one.</summary>
    public static void Touch(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) Stamps[hwnd] = ++_counter;
    }

    /// <summary>Higher = used more recently. 0 = never seen.</summary>
    public static long Of(IntPtr hwnd) => Stamps.TryGetValue(hwnd, out var s) ? s : 0;

    static void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero) return;
        Stamps[hwnd] = ++_counter;
        var root = Native.GetAncestor(hwnd, GA_ROOTOWNER); // a dialog counts as using its main window
        if (root != IntPtr.Zero && root != hwnd) Stamps[root] = _counter;

        if (Stamps.Count > 4000)
            foreach (var dead in Stamps.Keys.Where(h => !Native.IsWindow(h)).ToList()) Stamps.Remove(dead);
    }
}
