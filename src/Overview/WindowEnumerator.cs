using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Overview;

internal sealed class WinItem
{
    public IntPtr Hwnd;
    public uint Pid; // the app's own process (for UWP apps: the app, not ApplicationFrameHost)
    public string Title = "";
    public string AppName = "";
    public string ProcName = "";
    public string? ExePath;
    public string? Aumid;
    public bool Minimized;
    /// <summary>Visible frame in screen pixels (without the invisible resize borders).</summary>
    public Native.RECT Frame;
    public Native.RECT WindowRect;
    public ImageSource? Icon;
}

/// <summary>Lists the windows Alt+Tab would show on the current virtual desktop, in z-order.</summary>
internal static class WindowEnumerator
{
    static readonly ConcurrentDictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase);

    static readonly HashSet<string> SkipClasses = new()
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland"
    };

    /// <param name="loadIcons">True on background threads (loads missing icons now); the UI passes
    /// false and only gets icons that are already cached. The rest: <see cref="LoadIcon"/>, async.</param>
    public static List<WinItem> Enumerate(IntPtr exclude, bool loadIcons)
    {
        var handles = new List<IntPtr>();
        Native.EnumWindows((h, _) => { handles.Add(h); return true; }, IntPtr.Zero);

        var list = new List<WinItem>();
        foreach (var h in handles)
        {
            try
            {
                var w = TryCreate(h, exclude, loadIcons);
                if (w != null) list.Add(w);
            }
            catch (Exception e) { Log.Write(e); }
        }
        return list;
    }

    static WinItem? TryCreate(IntPtr h, IntPtr exclude, bool loadIcons)
    {
        if (h == exclude || !Native.IsWindowVisible(h)) return null;

        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && !appWindow) return null;
        if ((ex & Native.WS_EX_NOACTIVATE) != 0 && !appWindow) return null;
        // Click-through overlays (game/GPU overlays, screen recorders)
        if ((ex & Native.WS_EX_LAYERED) != 0 && (ex & Native.WS_EX_TRANSPARENT) != 0) return null;
        // Owned windows (dialogs, palettes) are skipped, unless their owner is a hidden helper
        // window, which is how some frameworks own their real main window.
        IntPtr owner = Native.GetWindow(h, Native.GW_OWNER);
        if (owner != IntPtr.Zero && !appWindow && Native.IsWindowVisible(owner) && !Native.IsCloaked(owner)) return null;
        if (Native.IsCloaked(h)) return null; // other virtual desktops, suspended UWP, hidden shell bits

        string title = Native.WindowText(h);
        if (string.IsNullOrWhiteSpace(title)) return null;

        string cls = Native.ClassName(h);
        if (SkipClasses.Contains(cls)) return null;

        var w = new WinItem { Hwnd = h, Title = title, Minimized = Native.IsIconic(h) };

        Native.GetWindowRect(h, out w.WindowRect);
        if (Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT frame, Marshal.SizeOf<Native.RECT>()) != 0)
            frame = w.WindowRect;
        w.Frame = frame;

        if (w.Minimized)
        {
            var wp = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
            if (Native.GetWindowPlacement(h, ref wp)) w.Frame = wp.rcNormalPosition;
        }
        else if (w.Frame.Width < 20 || w.Frame.Height < 20)
        {
            return null; // invisible helper windows
        }

        uint pid = AppProcessId(h, cls);
        w.Pid = pid;
        (w.ExePath, w.Aumid) = ProcessInfo(pid);
        w.ProcName = w.ExePath != null ? Path.GetFileNameWithoutExtension(w.ExePath) : "";
        w.AppName = (w.Aumid != null ? AppCatalog.NameForId(w.Aumid) : null)
                    ?? Description(w.ExePath)
                    ?? w.ProcName;

        w.Icon = loadIcons ? LoadIcon(w) : CachedIcon(w);
        return w;
    }

    /// <summary>The window's icon: its Store app's, else its exe's, else the one the window reports.
    /// Can be slow the first time: call it off the UI thread.</summary>
    public static ImageSource? LoadIcon(WinItem w) =>
        (w.Aumid != null ? IconLoader.ForParsingName(@"shell:AppsFolder\" + w.Aumid, 48) : null)
        ?? (w.ExePath != null ? IconLoader.ForParsingName(w.ExePath, 48) : null)
        ?? IconLoader.FromWindow(w.Hwnd);

    /// <summary>Only what's already cached (instant); null if the icon still needs loading.</summary>
    public static ImageSource? CachedIcon(WinItem w)
    {
        if (w.Aumid != null && IconLoader.TryGetCached(@"shell:AppsFolder\" + w.Aumid, 48, out var a) && a != null) return a;
        if (w.ExePath != null && IconLoader.TryGetCached(w.ExePath, 48, out var e) && e != null) return e;
        return null;
    }

    /// <summary>The process that owns a window's app (UWP apps live in a child CoreWindow owned by a
    /// different process than their ApplicationFrameWindow).</summary>
    static uint AppProcessId(IntPtr h, string cls)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        if (cls == "ApplicationFrameWindow")
        {
            uint host = pid;
            Native.EnumChildWindows(h, (c, _) =>
            {
                Native.GetWindowThreadProcessId(c, out uint cp);
                if (cp != host) { pid = cp; return false; }
                return true;
            }, IntPtr.Zero);
        }
        return pid;
    }

    /// <summary>The usage key of the app a window belongs to (see <see cref="AppCatalog.KeyFor"/>), or null.</summary>
    public static string? AppKeyOf(IntPtr hwnd)
    {
        try
        {
            uint pid = AppProcessId(hwnd, Native.ClassName(hwnd));
            if (pid == 0 || pid == (uint)Environment.ProcessId) return null; // not our own overview
            var (exe, aumid) = ProcessInfo(pid);
            return AppCatalog.KeyFor(exe, aumid);
        }
        catch { return null; }
    }

    static (string? path, string? aumid) ProcessInfo(uint pid)
    {
        IntPtr hp = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hp == IntPtr.Zero) return (null, null);
        try
        {
            string? path = null;
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (Native.QueryFullProcessImageName(hp, 0, sb, ref size)) path = sb.ToString();

            string? aumid = null;
            uint len = 0;
            if (Native.GetApplicationUserModelId(hp, ref len, null) == 122 /*ERROR_INSUFFICIENT_BUFFER*/ && len > 0)
            {
                var a = new StringBuilder((int)len);
                if (Native.GetApplicationUserModelId(hp, ref len, a) == 0) aumid = a.ToString();
            }
            return (path, aumid);
        }
        catch { return (null, null); }
        finally { Native.CloseHandle(hp); }
    }

    static string? Description(string? exe)
    {
        if (exe == null) return null;
        if (Descriptions.TryGetValue(exe, out var d)) return d.Length > 0 ? d : null;
        string desc = "";
        try { desc = FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim() ?? ""; } catch { }
        Descriptions[exe] = desc;
        return desc.Length > 0 ? desc : null;
    }
}
