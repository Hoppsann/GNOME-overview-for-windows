using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using Microsoft.Win32;

namespace Overview;

/// <summary>
/// Searches the web in the user's default browser, using that browser's own default search engine.
/// Chromium browsers (Chrome, Brave, Edge, Vivaldi, Opera…) search when given "? query";
/// Firefox-family browsers take "--search query". Anything else gets a Google URL in the default browser.
/// </summary>
internal static class WebSearch
{
    enum Kind { Unknown, Chromium, Firefox }

    static readonly string[] ChromiumExes =
        { "chrome", "brave", "msedge", "vivaldi", "opera", "launcher", "chromium", "thorium", "arc", "browser", "yandex" };
    static readonly string[] FirefoxExes =
        { "firefox", "librewolf", "waterfox", "floorp", "zen", "mullvadbrowser" };

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int AssocQueryString(uint flags, int str, string assoc, string? extra, StringBuilder? outStr, ref uint outLen);

    const uint ASSOCF_IS_PROTOCOL = 0x1000, ASSOCF_NOTRUNCATE = 0x20;
    const int ASSOCSTR_COMMAND = 1, ASSOCSTR_EXECUTABLE = 2;

    // Used from the UI, the icon preloader and launch threads, so guarded by a lock.
    static readonly object CacheGate = new();
    static string? _cachedExe;
    static DateTime _cachedAt = DateTime.MinValue;

    /// <summary>The default browser's exe. Re-checked every few seconds, so changing the default takes effect almost at once.</summary>
    public static string? BrowserExe()
    {
        lock (CacheGate)
        {
            if (DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(5)) return _cachedExe;
            _cachedExe = LookUpBrowserExe();
            _cachedAt = DateTime.UtcNow;
            return _cachedExe;
        }
    }

    static string? LookUpBrowserExe()
    {
        // Ask Windows itself which program opens https links. This is what Windows really uses,
        // whichever of its registry copies is current.
        var exe = Assoc(ASSOCSTR_EXECUTABLE);
        if (exe != null && File.Exists(exe)) return exe;
        exe = ExeFromCommand(Assoc(ASSOCSTR_COMMAND));
        if (exe != null) return exe;
        return BrowserExeFromRegistry();
    }

    static string? Assoc(int what)
    {
        try
        {
            uint len = 1024;
            var sb = new StringBuilder((int)len);
            int hr = AssocQueryString(ASSOCF_IS_PROTOCOL | ASSOCF_NOTRUNCATE, what, "https", "open", sb, ref len);
            return hr == 0 && sb.Length > 0 ? Environment.ExpandEnvironmentVariables(sb.ToString()) : null;
        }
        catch (Exception e)
        {
            Log.Write(e);
            return null;
        }
    }

    static string? BrowserExeFromRegistry()
    {
        try
        {
            string? progId = null;
            foreach (var sub in new[] { "UserChoice", "UserChoiceLatest" })
            {
                using var k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\" + sub);
                progId = k?.GetValue("ProgId") as string;
                if (!string.IsNullOrEmpty(progId)) break;
            }
            if (string.IsNullOrEmpty(progId)) return null;

            using var cmdKey = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
            return ExeFromCommand(cmdKey?.GetValue(null) as string);
        }
        catch (Exception e)
        {
            Log.Write(e);
            return null;
        }
    }

    static string? ExeFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        string exe;
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end < 0) return null;
            exe = command.Substring(1, end - 1);
        }
        else
        {
            int idx = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            exe = command.Substring(0, idx + 4);
        }
        exe = Environment.ExpandEnvironmentVariables(exe);
        return File.Exists(exe) ? exe : null;
    }

    static Kind KindOf(string exe)
    {
        string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
        foreach (var c in ChromiumExes) if (name == c) return Kind.Chromium;
        foreach (var f in FirefoxExes) if (name == f) return Kind.Firefox;
        return Kind.Unknown;
    }

    public static ImageSource? BrowserIcon()
    {
        var exe = BrowserExe();
        return exe != null ? IconLoader.ForParsingName(exe, 96) : null;
    }

    public static void Run(string query)
    {
        query = query.Replace('"', ' ').Trim();
        if (query.Length == 0) return;

        var exe = BrowserExe();
        Log.Write($"web search: default browser = {exe ?? "(unknown)"}");
        if (exe != null)
        {
            try
            {
                switch (KindOf(exe))
                {
                    case Kind.Chromium:
                        Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "? " + query }, UseShellExecute = false });
                        return;
                    case Kind.Firefox:
                        Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--search", query }, UseShellExecute = false });
                        return;
                }
            }
            catch (Exception e) { Log.Write(e); }
        }

        // Unknown browser: open a search URL, which Windows hands to the default browser.
        try
        {
            Process.Start(new ProcessStartInfo("https://www.google.com/search?q=" + Uri.EscapeDataString(query)) { UseShellExecute = true });
        }
        catch (Exception e) { Log.Write(e); }
    }
}
