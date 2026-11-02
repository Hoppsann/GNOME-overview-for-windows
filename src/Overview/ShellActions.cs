using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Overview;

/// <summary>The things the right-click menu does: run as admin, open file location, properties, uninstall.</summary>
internal static class ShellActions
{
    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb, lpFile, lpParameters, lpDirectory;
        public int nShow;
        public IntPtr hInstApp, lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon, hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    const int ERROR_CANCELLED = 1223; // user said no to UAC

    /// <summary>Store (UWP/MSIX) apps have an id like "Package_family!App".</summary>
    public static bool IsStoreApp(AppEntry a) => a.IsShellApp && a.Id.Contains('!');

    public static bool IsStoreAumid(string? aumid) => aumid != null && aumid.Contains('!');

    /// <summary>The app's exe or shortcut on disk, or null (Store apps, or nothing found).</summary>
    public static string? ResolveFile(AppEntry a)
    {
        try
        {
            if (!a.IsShellApp) return File.Exists(a.Id) ? a.Id : null;
            if (IsStoreApp(a)) return null;

            string id = a.Id;
            // Desktop apps are usually listed as "{known-folder-guid}\relative\path.exe".
            if (id.Length > 39 && id[0] == '{' && id[37] == '}' && id[38] == '\\' && Guid.TryParse(id.Substring(0, 38), out var g))
            {
                if (SHGetKnownFolderPath(ref g, 0, IntPtr.Zero, out var p) == 0)
                {
                    string folder = Marshal.PtrToStringUni(p) ?? "";
                    Marshal.FreeCoTaskMem(p);
                    string full = Path.Combine(folder, id.Substring(39));
                    if (File.Exists(full)) return full;
                }
            }
            if (File.Exists(id)) return id;

            // Apps registered under their own app id (Chrome, Discord…): use their Start Menu shortcut.
            return FindShortcut(a.Name);
        }
        catch (Exception e)
        {
            Log.Write(e);
            return null;
        }
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Path, DateTime At)> ShortcutCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Searches the Start Menu folders for "name.lnk". Cached for 10 minutes: the search
    /// walks every folder and runs when the right-click menu opens.</summary>
    static string? FindShortcut(string name)
    {
        if (ShortcutCache.TryGetValue(name, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10)
            && (hit.Path == null || File.Exists(hit.Path)))
            return hit.Path;
        var found = SearchStartMenu(name);
        ShortcutCache[name] = (found, DateTime.UtcNow);
        return found;
    }

    static string? SearchStartMenu(string name)
    {
        string file = name + ".lnk";
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                 })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                var hit = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories)
                                   .FirstOrDefault(f => string.Equals(Path.GetFileName(f), file, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Folders the user can actually browse (the Store's WindowsApps folder is locked).</summary>
    public static bool CanOpenLocation(string? path) =>
        path != null && File.Exists(path) && path.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) < 0;

    public static void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) { Log.Write(e); }
    }

    /// <summary>Shows the UAC prompt and starts the app elevated. Works for exes and shortcuts.</summary>
    public static void RunAsAdmin(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Verb = "runas" }); }
        catch (Win32Exception e) when (e.NativeErrorCode == ERROR_CANCELLED) { }
        catch (Exception e) { Log.Write(e); }
    }

    public static void OpenFileLocation(string file)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); }
        catch (Exception e) { Log.Write(e); }
    }

    public static void ShowProperties(string file)
    {
        try
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                fMask = SEE_MASK_INVOKEIDLIST,
                lpVerb = "properties",
                lpFile = file,
                nShow = 5 // SW_SHOW
            };
            if (!ShellExecuteEx(ref info)) Log.Write("properties failed: " + Marshal.GetLastWin32Error());
        }
        catch (Exception e) { Log.Write(e); }
    }

    /// <summary>Windows Settings → Apps → Installed apps, where every app can be uninstalled.</summary>
    public static void OpenInstalledApps() => Launch("ms-settings:appsfeatures");
}
