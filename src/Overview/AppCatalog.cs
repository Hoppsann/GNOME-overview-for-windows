using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;

namespace Overview;

/// <summary>What a search result is. Everything but App comes from <see cref="SearchSources"/>.</summary>
internal enum EntryKind { App, Setting, ControlPanel, Command, File, Folder }

internal sealed class AppEntry
{
    public string Name = "";
    /// <summary>shell:AppsFolder id (AUMID or known-folder path) when IsShellApp, otherwise a .lnk path.
    /// For other kinds: a unique key for usage ranking ("ms-settings:…", "cp:…", "file:…").</summary>
    public string Id = "";
    public bool IsShellApp;
    public EntryKind Kind = EntryKind.App;
    /// <summary>What opens: an ms-settings: URI, a shell parsing name, or a file/folder/command path.</summary>
    public string? Target;
    /// <summary>Extra words it's found by (settings pages), matched by word start.</summary>
    public string? Keywords;
    /// <summary>The exe's name without ".exe" ("cmd", "regedit"), so typing it finds the app, like Start does.</summary>
    public string? ExeStem;
    /// <summary>Shown as the tooltip, e.g. a file's full path.</summary>
    public string? Detail;
    /// <summary>Found by search, but not listed in the app grid (Windows Tools and other hidden Start Menu shortcuts).</summary>
    public bool HiddenFromGrid;
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
        /// <summary>Searchable but not in the grid: hidden Start Menu shortcuts, Control Panel items.</summary>
        public List<AppEntry> Extras = new();
        public Dictionary<string, string> NameById = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>exe path → app id, so a window (or a launch from the taskbar) counts for its app in search.</summary>
        public Dictionary<string, string> ExeToId = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The key search usage is recorded under: the app's id when the window can be matched to an
    /// installed app (by Store app id or by exe), otherwise "exe:" + the exe path.
    /// </summary>
    public static string? KeyFor(string? exePath, string? aumid)
    {
        var snap = _snap;
        if (aumid != null && snap.NameById.ContainsKey(aumid)) return aumid;
        if (exePath == null) return null;
        return snap.ExeToId.TryGetValue(exePath, out var id) ? id : "exe:" + exePath.ToLowerInvariant();
    }

    static volatile Snapshot _snap = new();
    static int _busy;
    static DateTime _lastRefresh = DateTime.MinValue;

    public static IReadOnlyList<AppEntry> Apps => _snap.Apps;

    /// <summary>Things search finds besides the apps: Windows Tools and other Start Menu shortcuts
    /// that "All apps" hides, and every Control Panel item and task.</summary>
    public static IReadOnlyList<AppEntry> Extras => _snap.Extras;

    public static string? NameForId(string id) => _snap.NameById.TryGetValue(id, out var n) ? n : null;

    public static void RefreshIfStale()
    {
        if (DateTime.UtcNow - _lastRefresh > TimeSpan.FromMinutes(2)) StartRefresh();
    }

    static long _busySinceTicks;

    public static void StartRefresh()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            // A refresh is running. If it has hung in the shell for over a minute, give up on it
            // (it can finish or not on its own) and start a fresh one, so icons keep preloading.
            if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _busySinceTicks) < TimeSpan.TicksPerMinute) return;
            Log.Write("app list refresh seemed stuck, starting a new one");
        }
        Interlocked.Exchange(ref _busySinceTicks, DateTime.UtcNow.Ticks);
        _lastRefresh = DateTime.UtcNow;
        var t = new Thread(() =>
        {
            try
            {
                var list = LoadShellApps();
                var startMenu = LoadStartMenu();
                if (list.Count == 0) { list = startMenu; startMenu = new(); }
                var extras = BuildExtras(list, startMenu);
                Publish(list, extras);
                Preload(list);
            }
            catch (Exception e)
            {
                Log.Write(e);
                try { Publish(LoadStartMenu(), new()); } catch (Exception e2) { Log.Write(e2); }
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        })
        // Below normal: reading hundreds of apps and icons must never compete with what you're doing,
        // least of all right after signing in, when everything else is starting too.
        { IsBackground = true, Name = "Overview app catalog", Priority = ThreadPriority.BelowNormal };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>
    /// Start Menu shortcuts that "All apps" doesn't list (Windows Tools such as Services, Event Viewer
    /// and Registry Editor live there), plus Control Panel items and tasks. Start's search finds these.
    /// </summary>
    static List<AppEntry> BuildExtras(List<AppEntry> apps, List<AppEntry> startMenu)
    {
        var names = new HashSet<string>(apps.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        var extras = new List<AppEntry>();
        foreach (var e in startMenu)
            if (names.Add(e.Name)) { e.HiddenFromGrid = true; extras.Add(e); }
        try
        {
            foreach (var e in SearchSources.LoadControlPanel())
                if (names.Add(e.Name)) extras.Add(e);
        }
        catch (Exception e) { Log.Write(e); }
        return extras;
    }

    static void Publish(List<AppEntry> list, List<AppEntry> extras)
    {
        // Same apps as before: keep the old list, so the app grid doesn't rebuild hundreds of tiles,
        // and only work out the exes of the extras again.
        var old = _snap;
        bool same = old.Apps.Count == list.Count;
        for (int i = 0; i < list.Count && same; i++)
            same = old.Apps[i].Id == list[i].Id && old.Apps[i].Name == list[i].Name;
        if (same) list = old.Apps;
        var snap = new Snapshot { Apps = list, Extras = extras, ExeToId = same ? old.ExeToId : new(StringComparer.OrdinalIgnoreCase) };
        foreach (var a in list) snap.NameById.TryAdd(a.Id, a.Name);
        foreach (var a in extras) snap.NameById.TryAdd(a.Id, a.Name);
        _snap = snap; // usable right away; the exe map follows
        try
        {
            var map = same ? new Dictionary<string, string>(old.ExeToId, StringComparer.OrdinalIgnoreCase)
                           : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddExes(same ? extras : list.Concat(extras), map);
            snap.ExeToId = map;
        }
        catch (Exception e) { Log.Write(e); }
    }

    /// <summary>
    /// Which installed app each exe belongs to, and each app's exe name (so "cmd" finds Command
    /// Prompt). Shortcuts with arguments are skipped: those are web apps and the like (e.g. a
    /// YouTube app that runs chrome.exe), not the program itself. Runs on the catalog's background thread.
    /// </summary>
    static void AddExes(IEnumerable<AppEntry> list, Dictionary<string, string> map)
    {
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type != null) shell = Activator.CreateInstance(type);
            foreach (var a in list)
            {
                if (a.Kind != EntryKind.App || ShellActions.IsStoreApp(a)) continue; // Store apps are matched by their app id
                string? file = ShellActions.ResolveFile(a);
                if (file == null) continue;
                string? exe = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(shell, file) : file;
                if (exe == null) continue;
                string ext = Path.GetExtension(exe).ToLowerInvariant();
                if (ext is ".exe" or ".msc" or ".cpl") a.ExeStem = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (ext == ".exe") map.TryAdd(exe, a.Id);
            }
        }
        finally
        {
            if (shell != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    static string? ShortcutTarget(object? shell, string lnk)
    {
        if (shell == null) return null;
        object? link = null;
        try
        {
            dynamic sh = shell;
            link = sh.CreateShortcut(lnk);
            dynamic l = link!;
            string args = l.Arguments ?? "";
            if (args.Trim().Length > 0) return null;
            string target = l.TargetPath ?? "";
            return target.Length > 0 ? Environment.ExpandEnvironmentVariables(target) : null;
        }
        catch { return null; }
        finally
        {
            if (link != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>
    /// Loads every icon the overview could need (app results, app grid, open windows) up front,
    /// so opening the overview or typing never waits on the disk. Runs on this STA thread.
    /// </summary>
    static void Preload(List<AppEntry> list)
    {
        try { WindowEnumerator.Enumerate(IntPtr.Zero, loadIcons: true); } // warms window icons + app descriptions
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

    public static string ParsingName(AppEntry a) => a.Kind switch
    {
        EntryKind.App => a.IsShellApp ? @"shell:AppsFolder\" + a.Id : a.Id,
        EntryKind.Setting => SearchSources.SettingsAppParsingName,
        _ => a.Target ?? a.Id
    };

    public static ImageSource? Icon(AppEntry a, int px) => IconLoader.ForParsingName(ParsingName(a), px);

    public static void Launch(AppEntry a)
    {
        if (a.Kind != EntryKind.App) { SearchSources.Open(a); return; }
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
