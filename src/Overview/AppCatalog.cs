using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Media;

namespace Overview;

internal sealed class AppEntry
{
    public string Name = "";
    /// <summary>shell:AppsFolder id (AUMID or known-folder path) when IsShellApp, otherwise a .lnk path.</summary>
    public string Id = "";
    public bool IsShellApp;
}

/// <summary>
/// Every installed app, the same list Start's "All apps" shows (desktop and Store apps),
/// read from shell:AppsFolder on a background STA thread. Falls back to Start Menu shortcuts.
/// </summary>
internal static class AppCatalog
{
    sealed class Snapshot
    {
        public List<AppEntry> Apps = new();
        public Dictionary<string, string> NameById = new(StringComparer.OrdinalIgnoreCase);
    }

    static volatile Snapshot _snap = new();
    static int _busy;
    static DateTime _lastRefresh = DateTime.MinValue;

    public static IReadOnlyList<AppEntry> Apps => _snap.Apps;

    public static string? NameForId(string id) => _snap.NameById.TryGetValue(id, out var n) ? n : null;

    public static void RefreshIfStale()
    {
        if (DateTime.UtcNow - _lastRefresh > TimeSpan.FromMinutes(2)) StartRefresh();
    }

    public static void StartRefresh()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        _lastRefresh = DateTime.UtcNow;
        var t = new Thread(() =>
        {
            try
            {
                var list = LoadShellApps();
                if (list.Count == 0) list = LoadStartMenu();
                Publish(list);
                Preload(list);
            }
            catch (Exception e)
            {
                Log.Write(e);
                try { Publish(LoadStartMenu()); } catch (Exception e2) { Log.Write(e2); }
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        })
        { IsBackground = true, Name = "Overview app catalog" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    static void Publish(List<AppEntry> list)
    {
        var snap = new Snapshot { Apps = list };
        foreach (var a in list) snap.NameById.TryAdd(a.Id, a.Name);
        _snap = snap;
    }

    /// <summary>
    /// Loads every icon the overview could need (app results, app grid, open windows) up front,
    /// so opening the overview or typing never waits on the disk. Runs on this STA thread.
    /// </summary>
    static void Preload(List<AppEntry> list)
    {
        try { WindowEnumerator.Enumerate(IntPtr.Zero); } // warms window icons + app descriptions
        catch (Exception e) { Log.Write(e); }
        try { WebSearch.BrowserIcon(); } catch { }
        foreach (var a in list)
        {
            try { Icon(a, 96); } catch { }
        }
    }

    static List<AppEntry> LoadShellApps()
    {
        var list = new List<AppEntry>();
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type == null) return list;
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic folder = shell.NameSpace("shell:AppsFolder");
        if (folder == null) return list;
        dynamic items = folder.Items();
        int count = items.Count;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            try
            {
                dynamic item = items.Item(i);
                if (item == null) continue;
                string name = item.Name;
                string path = item.Path;
                if (!Keep(name, path)) continue;
                if (!seen.Add(name)) continue;
                list.Add(new AppEntry { Name = name, Id = path, IsShellApp = true });
            }
            catch { /* skip odd items */ }
        }
        return list;
    }

    static List<AppEntry> LoadStartMenu()
    {
        var list = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                 })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (!Keep(name, f) || !seen.Add(name)) continue;
                list.Add(new AppEntry { Name = name, Id = f, IsShellApp = false });
            }
        }
        return list;
    }

    static bool Keep(string? name, string? path)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return false;
        var n = name.ToLowerInvariant();
        return !(n.StartsWith("uninstall") || n.Contains("readme") || n.Contains("read me") || n.EndsWith(" uninstaller"));
    }

    public static string ParsingName(AppEntry a) => a.IsShellApp ? @"shell:AppsFolder\" + a.Id : a.Id;

    public static ImageSource? Icon(AppEntry a, int px) => IconLoader.ForParsingName(ParsingName(a), px);

    public static void Launch(AppEntry a)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ParsingName(a)) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.Write(e);
            if (a.IsShellApp)
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"shell:AppsFolder\\{a.Id}\"") { UseShellExecute = true }); }
                catch (Exception e2) { Log.Write(e2); }
            }
        }
    }
}
