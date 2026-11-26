using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Overview;

/// <summary>
/// Everything search finds besides installed apps and open windows, the same kinds of things
/// Start's search finds: Settings pages, Control Panel items and tasks, Run-dialog commands and
/// paths ("regedit", "services.msc", "%appdata%", "C:\Users"), and files and folders from the
/// Windows Search index plus your recent files.
/// </summary>
internal static class SearchSources
{
    public const string SettingsAppParsingName =
        @"shell:AppsFolder\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";

    // ------------------------------------------------------------------ matching

    /// <summary>How well an entry matches, 0 for no match. Apps also match by exe name ("cmd",
    /// "regedit", "mspaint"), settings pages by their extra words ("wallpaper" → Background).</summary>
    public static double Match(string[] tokens, AppEntry a)
    {
        double m = Search.Score(tokens, a.Name);
        if (a.ExeStem != null && tokens.Length == 1)
        {
            string q = tokens[0];
            if (a.ExeStem == q) m = Math.Max(m, 96);
            else if (q.Length >= 3 && a.ExeStem.StartsWith(q, StringComparison.Ordinal)) m = Math.Max(m, 78);
        }
        if (a.Keywords != null) m = Math.Max(m, WordStartScore(tokens, a.Keywords));
        return m * a.Kind switch
        {
            EntryKind.Setting => 0.92,
            EntryKind.ControlPanel => 0.86,
            EntryKind.Power => 0.8,   // only when typed (nearly) in full: Enter on "Restart" by accident would hurt
            EntryKind.Folder or EntryKind.Command => 0.9,
            _ => 1.0
        };
    }

    /// <summary>Every token must start a word of <paramref name="text"/>: 70 if they are whole words, else 64.</summary>
    static double WordStartScore(string[] tokens, string text)
    {
        var words = text.ToLowerInvariant().Split(new[] { ' ', '-', '&', ',', '/' }, StringSplitOptions.RemoveEmptyEntries);
        bool whole = true;
        foreach (var q in tokens)
        {
            string? w = words.FirstOrDefault(x => x.StartsWith(q, StringComparison.Ordinal));
            if (w == null) return 0;
            if (w.Length != q.Length) whole = false;
        }
        return whole ? 70 : 64;
    }

    // ------------------------------------------------------------------ filters

    /// <summary>What a search is limited to, like Start's filters ("photos: beach", "settings: mouse").</summary>
    public enum Scope { All, Apps, Settings, Documents, Folders, Photos, Music, Videos, Web }

    static readonly (string Prefix, Scope Scope)[] Prefixes =
    {
        ("apps", Scope.Apps), ("app", Scope.Apps),
        ("settings", Scope.Settings), ("setting", Scope.Settings),
        ("documents", Scope.Documents), ("document", Scope.Documents), ("docs", Scope.Documents), ("doc", Scope.Documents),
        ("folders", Scope.Folders), ("folder", Scope.Folders),
        ("photos", Scope.Photos), ("photo", Scope.Photos), ("pictures", Scope.Photos), ("picture", Scope.Photos), ("images", Scope.Photos),
        ("music", Scope.Music), ("songs", Scope.Music),
        ("videos", Scope.Videos), ("video", Scope.Videos),
        ("web", Scope.Web),
    };

    /// <summary>Splits off a filter word with a colon at the start: "photos: beach" → (Photos, "beach").</summary>
    public static (Scope Scope, string Text) ParseScope(string query)
    {
        string q = query.TrimStart();
        int colon = q.IndexOf(':');
        if (colon < 2 || colon > 10) return (Scope.All, query);
        string word = q.Substring(0, colon).ToLowerInvariant();
        foreach (var (prefix, scope) in Prefixes)
            if (word == prefix) return (scope, q.Substring(colon + 1).TrimStart());
        return (Scope.All, query);
    }

    public static bool IsFileScope(Scope s) => s is Scope.Documents or Scope.Folders or Scope.Photos or Scope.Music or Scope.Videos;

    /// <summary>The Windows Search "kind" for a filter, and the extensions used for recent files.</summary>
    static (string Kind, string[] Exts)? KindOf(Scope s) => s switch
    {
        Scope.Documents => ("document", new[] { ".pdf", ".doc", ".docx", ".odt", ".rtf", ".txt", ".md", ".xls", ".xlsx", ".ods", ".csv", ".ppt", ".pptx", ".odp" }),
        Scope.Photos => ("picture", new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff", ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".svg", ".ico" }),
        Scope.Music => ("music", new[] { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".wma", ".opus" }),
        Scope.Videos => ("video", new[] { ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".mts" }),
        Scope.Folders => ("folder", Array.Empty<string>()),
        _ => null
    };

    // ------------------------------------------------------------------ places and drives

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    static string? KnownFolder(string guid)
    {
        var id = new Guid(guid);
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>
    /// Places Start finds by name: your user folders (Downloads, Documents…), This PC, Recycle Bin,
    /// Network, and your drives ("Local Disk (C:)", also found by typing "c:"). Background thread.
    /// </summary>
    public static List<AppEntry> LoadPlaces()
    {
        var list = new List<AppEntry>();
        void Folder(string name, string? path, string keywords)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            list.Add(new AppEntry { Name = name, Id = "file:" + path.ToLowerInvariant(), Target = path, Kind = EntryKind.Folder, Keywords = keywords, Detail = path });
        }
        void Shell(string name, string target, string keywords) =>
            list.Add(new AppEntry { Name = name, Id = "cmd:" + target.ToLowerInvariant(), Target = target, Kind = EntryKind.Command, Keywords = keywords, Detail = name });

        Folder("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "desktop");
        Folder("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "documents my documents");
        Folder("Downloads", KnownFolder("374DE290-123F-4565-9164-39C4925E467B"), "downloads");
        Folder("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "pictures photos");
        Folder("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "music");
        Folder("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "videos");
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Folder(Path.GetFileName(profile), profile, "user folder home profile");
        Shell("This PC", "shell:MyComputerFolder", "this pc computer my computer drives");
        Shell("Recycle Bin", "shell:RecycleBinFolder", "recycle bin trash deleted");
        Shell("Network", "shell:NetworkPlacesFolder", "network");
        Shell("File Explorer Home", "shell:::{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", "quick access home explorer");

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
                string letter = d.Name.Substring(0, 2); // "C:"
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel)
                    ? (d.DriveType == DriveType.Removable ? "USB Drive" : "Local Disk")
                    : d.VolumeLabel;
                list.Add(new AppEntry
                {
                    Name = $"{label} ({letter})",
                    Id = "file:" + d.Name.ToLowerInvariant(),
                    Target = d.Name,
                    Kind = EntryKind.Folder,
                    Keywords = $"{letter.ToLowerInvariant()} {letter[0]}-drive drive disk",
                    Detail = d.Name
                });
            }
            catch { }
        }
        return list;
    }

    // ------------------------------------------------------------------ power

    static AppEntry Power(string name, string action, string keywords) => new()
    {
        Name = name,
        Id = "power:" + action,
        Target = action,
        Kind = EntryKind.Power,
        Keywords = keywords,
        Detail = name
    };

    /// <summary>What Start's power button and account menu do.</summary>
    public static readonly IReadOnlyList<AppEntry> PowerActions = new[]
    {
        Power("Shut down", "shutdown", "shutdown power off turn off"),
        Power("Restart", "restart", "reboot"),
        Power("Sleep", "sleep", "suspend standby"),
        Power("Lock", "lock", "lock screen"),
        Power("Sign out", "signout", "log out logout log off logoff"),
        Power("Switch user", "switchuser", "change user"),
    };

    public static string PowerGlyph(AppEntry a) => a.Target switch
    {
        "restart" => "\uE777",
        "sleep" => "\uE708",
        "lock" => "\uE72E",
        "signout" => "\uF3B1",
        "switchuser" => "\uE748",
        _ => "\uE7E8"
    };

    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("user32.dll")] static extern bool ExitWindowsEx(uint flags, uint reason);
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);
    [DllImport("wtsapi32.dll")] static extern bool WTSDisconnectSession(IntPtr server, int sessionId, bool wait);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    /// <summary>Runs a power action. Shutting down, restarting and signing out ask first, since an
    /// Enter too many could otherwise cost unsaved work. Call on the launcher thread.</summary>
    static void RunPower(string action)
    {
        string? ask = action switch
        {
            "shutdown" => "Shut down the PC now?",
            "restart" => "Restart the PC now?",
            "signout" => "Sign out now?",
            _ => null
        };
        if (ask != null)
        {
            const uint MB_YESNO = 0x4, MB_ICONQUESTION = 0x20, MB_TOPMOST = 0x40000, MB_SETFOREGROUND = 0x10000;
            if (MessageBoxW(IntPtr.Zero, ask + "\n\nApps with unsaved work may ask you to save first.", "Overview",
                    MB_YESNO | MB_ICONQUESTION | MB_TOPMOST | MB_SETFOREGROUND) != 6) return; // IDYES
        }
        Log.Write("power: " + action);
        switch (action)
        {
            case "shutdown":
                // Like Start: a "hybrid" shutdown, so Fast Startup still works. Plain if that's unavailable.
                if (RunShutdown("/s /hybrid /t 0") != 0) RunShutdown("/s /t 0");
                break;
            case "restart":
                RunShutdown("/r /t 0");
                break;
            case "signout":
                ExitWindowsEx(0 /* EWX_LOGOFF */, 0);
                break;
            case "lock":
                LockWorkStation();
                break;
            case "switchuser":
                // Disconnecting the session shows the sign-in screen, where you pick another user.
                if (!WTSDisconnectSession(IntPtr.Zero, -1, false)) LockWorkStation();
                break;
            case "sleep":
                // "Modern standby" PCs (most laptops) sleep when the screen turns off; others have classic sleep.
                if (ModernStandby() || !SetSuspendState(false, false, false))
                    SendMessage(new IntPtr(0xFFFF), 0x0112 /* WM_SYSCOMMAND */, new IntPtr(0xF170) /* SC_MONITORPOWER */, new IntPtr(2));
                break;
        }
    }

    [DllImport("powrprof.dll")] static extern bool GetPwrCapabilities(byte[] capabilities);

    /// <summary>SYSTEM_POWER_CAPABILITIES.AoAc (byte 20): the PC uses modern standby instead of S3 sleep.</summary>
    static bool ModernStandby()
    {
        try
        {
            var caps = new byte[256];
            return GetPwrCapabilities(caps) && caps[20] != 0;
        }
        catch { return false; }
    }

    static int RunShutdown(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), args)
            { UseShellExecute = false, CreateNoWindow = true });
            if (p == null) return -1;
            p.WaitForExit(10000);
            return p.HasExited ? p.ExitCode : 0;
        }
        catch (Exception e) { Log.Write(e); return -1; }
    }

    // ------------------------------------------------------------------ calculator

    /// <summary>
    /// A result for arithmetic typed into search ("12*7.5", "(2+3)^2", "15% of 80" isn't supported):
    /// + − × ÷ ^ and brackets, with "," or "." as the decimal mark. Enter copies the answer.
    /// </summary>
    public static AppEntry? Calculate(string query)
    {
        string q = query.Trim().TrimEnd('=').Trim();
        if (q.Length < 3 || !q.Any(char.IsDigit) || q.IndexOfAny(new[] { '+', '-', '*', '/', '^', 'x', '×', '÷', '(' }) < 0) return null;
        if (System.Text.RegularExpressions.Regex.IsMatch(q, @"^\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4}$")) return null; // a date
        if (System.Text.RegularExpressions.Regex.IsMatch(q, @"^[\d\s]+$")) return null; // just a number
        double? v;
        try { v = new Calc(q).Parse(); }
        catch { return null; }
        if (v is not double r || double.IsNaN(r) || double.IsInfinity(r)) return null;
        string answer = Math.Round(r, 10).ToString("G12", System.Globalization.CultureInfo.CurrentCulture);
        return new AppEntry
        {
            Name = "= " + answer,
            Id = "calc",
            Target = answer,
            Kind = EntryKind.Calc,
            Detail = "Press Enter to copy " + answer
        };
    }

    /// <summary>Recursive-descent arithmetic. Throws on anything it doesn't understand.</summary>
    sealed class Calc
    {
        readonly string _s;
        int _i;
        public Calc(string s) { _s = s.Replace('×', '*').Replace('÷', '/').Replace('x', '*').Replace('X', '*').Replace(',', '.'); }

        public double? Parse()
        {
            double v = Sum();
            Skip();
            if (_i != _s.Length) throw new FormatException();
            return v;
        }

        void Skip() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }
        bool Eat(char c) { Skip(); if (_i < _s.Length && _s[_i] == c) { _i++; return true; } return false; }

        double Sum()
        {
            double v = Product();
            while (true)
            {
                if (Eat('+')) v += Product();
                else if (Eat('-')) v -= Product();
                else return v;
            }
        }

        double Product()
        {
            double v = Power();
            while (true)
            {
                if (Eat('*')) v *= Power();
                else if (Eat('/')) v /= Power();
                else if (Eat('%')) v %= Power();
                else return v;
            }
        }

        double Power()
        {
            double b = Unary();
            return Eat('^') ? Math.Pow(b, Power()) : b; // right-associative
        }

        double Unary()
        {
            if (Eat('-')) return -Unary();
            if (Eat('+')) return Unary();
            if (Eat('('))
            {
                double v = Sum();
                if (!Eat(')')) throw new FormatException();
                return v;
            }
            Skip();
            int start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            if (_i == start) throw new FormatException();
            return double.Parse(_s.AsSpan(start, _i - start), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    // ------------------------------------------------------------------ opening

    public static void Open(AppEntry a)
    {
        try
        {
            switch (a.Kind)
            {
                case EntryKind.Power:
                    RunPower(a.Target!);
                    break;
                case EntryKind.Calc:
                    break; // copied on the UI thread
                case EntryKind.ControlPanel:
                    OpenParsingName(a.Target!);
                    break;
                case EntryKind.Command when a.Target!.StartsWith("shell:", StringComparison.OrdinalIgnoreCase):
                    Process.Start(new ProcessStartInfo("explorer.exe", a.Target) { UseShellExecute = true });
                    break;
                default:
                    var psi = new ProcessStartInfo(a.Target ?? a.Id) { UseShellExecute = true };
                    if (a.Kind == EntryKind.File)
                    {
                        try { psi.WorkingDirectory = Path.GetDirectoryName(a.Target) ?? ""; } catch { }
                    }
                    Process.Start(psi);
                    break;
            }
        }
        catch (Exception e) { Log.Write(e); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

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

    /// <summary>Opens a shell item (a Control Panel item or task) the way Explorer does: by its ID list.</summary>
    static void OpenParsingName(string parsingName)
    {
        if (SHParseDisplayName(parsingName, IntPtr.Zero, out var pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
        {
            try
            {
                var info = new SHELLEXECUTEINFO
                {
                    cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                    fMask = 0x0000000C, // SEE_MASK_INVOKEIDLIST
                    lpIDList = pidl,
                    nShow = 1 // SW_SHOWNORMAL
                };
                if (ShellExecuteEx(ref info)) return;
                Log.Write($"open {parsingName}: error {Marshal.GetLastWin32Error()}");
            }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }
        // Explorer understands these names too.
        Process.Start(new ProcessStartInfo("explorer.exe", "shell:" + parsingName) { UseShellExecute = true });
    }

    // ------------------------------------------------------------------ Settings pages

    static AppEntry Page(string name, string page, string keywords = "") => new()
    {
        Name = name,
        Id = "ms-settings:" + page,
        Target = "ms-settings:" + page,
        Kind = EntryKind.Setting,
        Keywords = keywords.Length > 0 ? keywords : null,
        Detail = "Settings › " + name
    };

    /// <summary>The Settings app's pages, with the words people search them by.</summary>
    public static readonly IReadOnlyList<AppEntry> SettingsPages = new[]
    {
        // System
        Page("Display", "display", "screen resolution brightness scale scaling monitor refresh rate hdr orientation multiple displays"),
        Page("Night light", "nightlight", "blue light warm night mode"),
        Page("Graphics", "display-advancedgraphics", "gpu preference hardware-accelerated gpu scheduling"),
        Page("Sound", "sound", "audio volume speakers headphones microphone output input"),
        Page("Volume mixer", "apps-volume", "app volume sound"),
        Page("Notifications", "notifications", "alerts banners do not disturb"),
        Page("Focus", "quiethours", "do not disturb focus assist"),
        Page("Power & battery", "powersleep", "power mode sleep screen timeout battery energy"),
        Page("Battery saver", "batterysaver", "energy saver"),
        Page("Storage", "storagesense", "disk space storage sense cleanup temporary files"),
        Page("Nearby sharing", "crossdevice", "share"),
        Page("Multitasking", "multitasking", "snap windows alt tab virtual desktops"),
        Page("Activation", "activation", "product key license"),
        Page("Troubleshoot", "troubleshoot", "troubleshooters fix problems"),
        Page("Recovery", "recovery", "reset this pc advanced startup go back"),
        Page("Projecting to this PC", "project", "miracast cast wireless display"),
        Page("Remote Desktop", "remotedesktop", "rdp remote"),
        Page("Clipboard", "clipboard", "clipboard history copy paste"),
        Page("Optional features", "optionalfeatures", "features add"),
        Page("For developers", "developers", "developer mode sudo"),
        Page("About", "about", "pc name rename this pc specs system info device specifications windows version ram processor"),
        // Bluetooth & devices
        Page("Bluetooth & devices", "bluetooth", "bluetooth pair add device headphones"),
        Page("Devices", "connecteddevices", "add device"),
        Page("Printers & scanners", "printers", "printer scanner print"),
        Page("Phone Link", "mobile-devices", "phone"),
        Page("Cameras", "camera", "webcam"),
        Page("Mouse", "mousetouchpad", "pointer speed scroll wheel buttons cursor"),
        Page("Touchpad", "devices-touchpad", "trackpad gestures"),
        Page("Pen & Windows Ink", "pen", "stylus"),
        Page("AutoPlay", "autoplay", "usb removable"),
        Page("USB", "usb", "usb"),
        // Network & internet
        Page("Network & internet", "network", "internet connection status"),
        Page("Wi-Fi", "network-wifi", "wifi wireless network internet"),
        Page("Ethernet", "network-ethernet", "wired lan cable"),
        Page("VPN", "network-vpn", "vpn"),
        Page("Mobile hotspot", "network-mobilehotspot", "hotspot tethering share internet"),
        Page("Airplane mode", "network-airplanemode", "flight mode"),
        Page("Proxy", "network-proxy", "proxy"),
        Page("Dial-up", "network-dialup", "modem"),
        Page("Advanced network settings", "network-advancedsettings", "network adapters network reset"),
        // Personalization
        Page("Personalization", "personalization", "customize"),
        Page("Background", "personalization-background", "wallpaper desktop background picture slideshow"),
        Page("Colors", "colors", "dark mode light mode accent color theme transparency"),
        Page("Themes", "themes", "theme desktop icons"),
        Page("Lock screen", "lockscreen", "lockscreen"),
        Page("Text input", "personalization-textinput", "touch keyboard emoji"),
        Page("Start", "personalization-start", "start menu"),
        Page("Taskbar", "taskbar", "taskbar auto-hide hide alignment system tray"),
        Page("Fonts", "fonts", "font typeface install fonts"),
        Page("Device usage", "deviceusage", "usage"),
        // Apps
        Page("Installed apps", "appsfeatures", "uninstall remove programs apps and features add"),
        Page("Advanced app settings", "advanced-apps", "app execution aliases install from"),
        Page("Default apps", "defaultapps", "default browser file associations open with"),
        Page("Offline maps", "maps", "maps"),
        Page("Apps for websites", "appsforwebsites", "links"),
        Page("Video playback", "videoplayback", "hdr video"),
        Page("Startup apps", "startupapps", "startup autostart boot start with windows"),
        // Accounts
        Page("Your info", "yourinfo", "account microsoft account profile picture"),
        Page("Email & accounts", "emailandaccounts", "email accounts"),
        Page("Sign-in options", "signinoptions", "password pin windows hello fingerprint face sign in"),
        Page("Family", "family-group", "family"),
        Page("Other users", "otherusers", "add user account"),
        Page("Windows backup", "backup", "onedrive backup"),
        Page("Access work or school", "workplace", "work school"),
        // Time & language
        Page("Date & time", "dateandtime", "clock time zone date"),
        Page("Language & region", "regionlanguage", "language region display language country format"),
        Page("Typing", "typing", "autocorrect spelling spellcheck"),
        Page("Keyboard", "keyboard", "keyboard layout input"),
        Page("Speech", "speech", "voice"),
        // Gaming
        Page("Game Bar", "gaming-gamebar", "xbox game bar"),
        Page("Captures", "gaming-gamedvr", "screen recording game dvr"),
        Page("Game Mode", "gaming-gamemode", "game mode"),
        // Accessibility
        Page("Accessibility", "easeofaccess", "ease of access"),
        Page("Text size", "easeofaccess-display", "bigger text larger text"),
        Page("Mouse pointer and touch", "easeofaccess-mousepointer", "cursor size color"),
        Page("Text cursor", "easeofaccess-cursor", "caret"),
        Page("Magnifier", "easeofaccess-magnifier", "zoom"),
        Page("Color filters", "easeofaccess-colorfilter", "colorblind grayscale"),
        Page("Contrast themes", "easeofaccess-highcontrast", "high contrast"),
        Page("Narrator", "easeofaccess-narrator", "screen reader"),
        Page("Captions", "easeofaccess-closedcaptioning", "subtitles"),
        Page("Speech recognition", "easeofaccess-speechrecognition", "voice access"),
        // Privacy & security
        Page("Privacy & security", "privacy", "permissions"),
        Page("Windows Security", "windowsdefender", "antivirus defender virus firewall protection"),
        Page("Find my device", "findmydevice", "lost"),
        Page("Location", "privacy-location", "gps"),
        Page("Camera permissions", "privacy-webcam", "webcam privacy"),
        Page("Microphone permissions", "privacy-microphone", "mic privacy"),
        Page("Search permissions", "search-permissions", "safesearch"),
        Page("Searching Windows", "cortana-windowssearch", "indexing search index enhanced"),
        // Windows Update
        Page("Windows Update", "windowsupdate", "updates update check for updates patch"),
        Page("Update history", "windowsupdate-history", "updates"),
        Page("Advanced options", "windowsupdate-options", "windows update delivery optimization"),
        Page("Windows Insider Program", "windowsinsider", "insider preview"),
    };

    // ------------------------------------------------------------------ Control Panel

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item, out uint fetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IntPtr ppenum);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    const uint SIGDN_NORMALDISPLAY = 0, SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    static Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    /// <summary>
    /// Every Control Panel item (Device Manager, Sound, Mouse, Programs and Features…) and every
    /// Control Panel task ("Edit the system environment variables", "Change mouse pointer
    /// size"…), with the names Windows shows in its own language. Call on an STA thread.
    /// </summary>
    public static List<AppEntry> LoadControlPanel()
    {
        // It barely changes, and reading it takes a moment: once an hour is plenty.
        if (_controlPanel != null && DateTime.UtcNow - _controlPanelAt < TimeSpan.FromHours(1)) return _controlPanel;
        var list = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string parsing, string detail)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) return;
            list.Add(new AppEntry
            {
                Name = name,
                Id = "cp:" + parsing,
                Target = parsing,
                Kind = EntryKind.ControlPanel,
                Detail = detail
            });
        }

        Add("Control Panel", "::{26EE0668-A00A-44D7-9371-BEB064C98683}", "Control Panel");
        int items = 0, tasks = 0;
        // "All Control Panel Items", then every task ("God mode"). Tried in a couple of spellings.
        foreach (var f in new[] { "::{26EE0668-A00A-44D7-9371-BEB064C98683}\\0", "shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}\\0",
                                  "::{21EC2020-3AEA-1069-A2DD-08002B30309D}" })
            if ((items = Enumerate(f, (n, p) => Add(n, p, "Control Panel › " + n))) > 0) break;
        foreach (var f in new[] { "::{ED7BA470-8E54-465E-825C-99712043E01C}", "shell:::{ED7BA470-8E54-465E-825C-99712043E01C}" })
            if ((tasks = Enumerate(f, (n, p) => Add(n, p, "Control Panel › " + n))) > 0) break;
        Log.Write($"control panel: {items} items, {tasks} tasks");
        if (list.Count > 1) { _controlPanel = list; _controlPanelAt = DateTime.UtcNow; }
        return list;
    }

    static List<AppEntry>? _controlPanel;
    static DateTime _controlPanelAt;

    static int Enumerate(string folder, Action<string, string> add)
    {
        int count = 0;
        var iid = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iid, out var root) != 0 || root == null) return 0;
        IntPtr enumPtr = IntPtr.Zero;
        try
        {
            var enumIid = typeof(IEnumShellItems).GUID;
            if (root.BindToHandler(IntPtr.Zero, ref BHID_EnumItems, ref enumIid, out enumPtr) != 0 || enumPtr == IntPtr.Zero) return 0;
            var en = (IEnumShellItems)Marshal.GetObjectForIUnknown(enumPtr);
            try
            {
                while (en.Next(1, out var item, out uint fetched) == 0 && fetched == 1 && item != null)
                {
                    try
                    {
                        string? name = DisplayName(item, SIGDN_NORMALDISPLAY);
                        string? parsing = DisplayName(item, SIGDN_DESKTOPABSOLUTEPARSING);
                        if (name != null && parsing != null) { add(name, parsing); count++; }
                    }
                    finally { Marshal.ReleaseComObject(item); }
                    if (count > 2000) break;
                }
            }
            finally { Marshal.ReleaseComObject(en); }
        }
        catch (Exception e) { Log.Write($"enumerate {folder}: {e.Message}"); }
        finally
        {
            if (enumPtr != IntPtr.Zero) Marshal.Release(enumPtr);
            Marshal.ReleaseComObject(root);
        }
        return count;
    }

    static string? DisplayName(IShellItem item, uint sigdn)
    {
        if (item.GetDisplayName(sigdn, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    // ------------------------------------------------------------------ Run-dialog commands and paths

    static readonly Dictionary<string, AppEntry?> CommandCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What the Run dialog would open for this text: a folder or file path ("C:\Users", "%appdata%",
    /// "~\Downloads"), a shell: location, or a program/console by name ("regedit", "msconfig",
    /// "services.msc", "appwiz.cpl", "winver"). Null if it isn't one.
    /// </summary>
    public static AppEntry? MatchCommand(string query)
    {
        string q = query.Trim().Trim('"');
        if (q.Length < 2) return null;
        if (CommandCache.TryGetValue(q, out var cached)) return cached;
        AppEntry? found = null;
        try { found = FindCommand(q); }
        catch (Exception e) { Log.Write($"command {q}: {e.Message}"); }
        if (CommandCache.Count > 500) CommandCache.Clear();
        CommandCache[q] = found;
        return found;
    }

    static AppEntry? FindCommand(string q)
    {
        if (q.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && q.Length > 7)
            return new AppEntry { Name = q, Id = "cmd:" + q.ToLowerInvariant(), Target = q, Kind = EntryKind.Command, Detail = "Open " + q };

        string path = Environment.ExpandEnvironmentVariables(q);
        if (path == "~" || path.StartsWith("~\\") || path.StartsWith("~/"))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path.Substring(1);

        if (path.Length == 2 && char.IsLetter(path[0]) && path[1] == ':') path += "\\"; // "d:" → D:\
        bool unc = path.StartsWith(@"\\");
        bool rooted = unc || (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'));
        if (rooted)
        {
            if (unc)
            {
                // Checking a network path can hang for seconds: offer it without checking, like Run does.
                if (path.Length < 4 || path.IndexOf('\\', 2) == 2) return null;
                return new AppEntry { Name = path, Id = "file:" + path.ToLowerInvariant(), Target = path, Kind = EntryKind.Folder, Detail = path };
            }
            try
            {
                var drive = new DriveInfo(path.Substring(0, 1));
                if (drive.DriveType == DriveType.Network)
                    return new AppEntry { Name = path, Id = "file:" + path.ToLowerInvariant(), Target = path, Kind = EntryKind.Folder, Detail = path };
            }
            catch { }
            if (Directory.Exists(path))
                return new AppEntry { Name = NameOfPath(path), Id = "file:" + path.ToLowerInvariant(), Target = path, Kind = EntryKind.Folder, Detail = path };
            if (File.Exists(path))
                return new AppEntry { Name = Path.GetFileName(path), Id = "file:" + path.ToLowerInvariant(), Target = path, Kind = EntryKind.File, Detail = path };
            return null;
        }

        // A program or console by name, like the Run dialog: Windows folders and registered App Paths.
        if (q.IndexOfAny(new[] { ' ', '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0) return null;
        string? file = FindProgram(q);
        if (file == null) return null;
        string ext = Path.GetExtension(file).ToLowerInvariant();
        string name = Path.GetFileName(file);
        if (ext == ".exe")
        {
            try
            {
                var desc = FileVersionInfo.GetVersionInfo(file).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(desc) && desc.Length <= 60) name = desc;
            }
            catch { }
        }
        return new AppEntry
        {
            Name = name,
            Id = "cmd:" + Path.GetFileName(file).ToLowerInvariant(),
            Target = file,
            Kind = EntryKind.Command,
            ExeStem = Path.GetFileNameWithoutExtension(file).ToLowerInvariant(),
            Detail = file
        };
    }

    static string NameOfPath(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        string n = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(n) ? path : n;
    }

    static string? FindProgram(string q)
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string sys = Environment.SystemDirectory;
        bool hasExt = Path.HasExtension(q);
        string[] names = hasExt ? new[] { q } : new[] { q + ".exe", q + ".msc", q + ".cpl" };
        foreach (var dir in new[] { sys, win })
            foreach (var n in names)
            {
                string f = Path.Combine(dir, n);
                if (File.Exists(f)) return f;
            }
        string exeName = hasExt ? q : q + ".exe";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName);
                if (key?.GetValue(null) is string v)
                {
                    string f = Environment.ExpandEnvironmentVariables(v.Trim().Trim('"'));
                    if (File.Exists(f)) return f;
                }
            }
            catch { }
        }
        return null;
    }

    // ------------------------------------------------------------------ files and folders

    /// <summary>A file or folder that matched, with its score before usage ranking.</summary>
    public readonly record struct FileHit(AppEntry Entry, double Score);

    static readonly object FileGate = new();
    static (string Query, int Max, Dispatcher Dispatcher, Action<string, List<FileHit>> Done)? _fileRequest;
    static readonly AutoResetEvent FileSignal = new(false);
    static Thread? _fileThread;

    /// <summary>
    /// Looks for files and folders whose name matches, in the background: the Windows Search index
    /// (the same one Start searches: your user folders, plus the whole PC with "Enhanced" search on)
    /// and your recently opened files. Only the latest request is answered; <paramref name="done"/>
    /// runs on the calling thread with the query it answers.
    /// </summary>
    public static void FindFiles(string query, int max, Action<string, List<FileHit>> done)
    {
        lock (FileGate)
        {
            _fileRequest = (query, max, Dispatcher.CurrentDispatcher, done);
            if (_fileThread == null)
            {
                _fileThread = new Thread(FileWorker) { IsBackground = true, Name = "Overview file search", Priority = ThreadPriority.BelowNormal };
                _fileThread.SetApartmentState(ApartmentState.STA);
                _fileThread.Start();
            }
        }
        FileSignal.Set();
    }

    static void FileWorker()
    {
        while (true)
        {
            FileSignal.WaitOne();
            (string Query, int Max, Dispatcher Dispatcher, Action<string, List<FileHit>> Done)? req;
            lock (FileGate) { req = _fileRequest; _fileRequest = null; }
            if (req is not { } r) continue;
            List<FileHit> hits;
            try { hits = SearchFiles(r.Query, r.Max); }
            catch (Exception e) { Log.Write(e); hits = new(); }
            lock (FileGate)
            {
                if (_fileRequest != null) continue; // typed on meanwhile: answer that instead
            }
            r.Dispatcher.InvokeAsync(() =>
            {
                try { r.Done(r.Query, hits); } catch (Exception e) { Log.Write(e); }
            });
        }
    }

    static List<FileHit> SearchFiles(string query, int max)
    {
        var (scope, rest) = ParseScope(query);
        if (scope != Scope.All && !IsFileScope(scope)) return new();
        var kind = KindOf(scope);
        var tokens = Search.Tokenize(rest);
        if (tokens.Length == 0 && kind == null) return new();
        var sw = Stopwatch.StartNew();
        var found = new Dictionary<string, FileHit>(StringComparer.OrdinalIgnoreCase);

        foreach (var h in QueryIndex(tokens, rest.Trim(), max * 4, kind?.Kind))
            Consider(found, tokens, h.Path, h.Name, h.Folder, h.Modified, recent: false, content: h.Content);
        long indexMs = sw.ElapsedMilliseconds;

        foreach (var (path, name, folder, used) in RecentMatches(tokens, max * 2, kind))
            Consider(found, tokens, path, name, folder, used, recent: true, content: false);

        var list = found.Values.OrderByDescending(h => h.Score).Take(max * 2).ToList();
        Log.Write($"file search '{query}': {list.Count} hits, index {indexMs} ms, total {sw.ElapsedMilliseconds} ms");
        return list;
    }

    static void Consider(Dictionary<string, FileHit> found, string[] tokens, string path, string name, bool folder,
                         DateTime? when, bool recent, bool content)
    {
        double m;
        if (tokens.Length == 0) m = 45;       // "photos:" alone: newest first
        else if (content) m = 24;             // the words are inside the file, not in its name
        else
        {
            m = Search.Score(tokens, name);
            if (m <= 0) m = recent ? 0 : 30;  // the index matched a word inside the name the simple matcher didn't see
        }
        if (m <= 0) return;
        if (when is DateTime t)
        {
            double days = (DateTime.Now - t).TotalDays;
            m += days < 1 ? 12 : days < 7 ? 8 : days < 31 ? 4 : 0;
        }
        if (recent) m += 8; // opened lately
        if (found.TryGetValue(path, out var old) && old.Score >= m) return;
        found[path] = new FileHit(new AppEntry
        {
            Name = name,
            Id = "file:" + path.ToLowerInvariant(),
            Target = path,
            Kind = folder ? EntryKind.Folder : EntryKind.File,
            Detail = content ? path + "\n(the text you typed is inside this file)" : path
        }, m);
    }

    // ---- Windows Search index (through ADO, which every Windows has)

    static dynamic? _conn;
    static DateTime _indexBrokenUntil = DateTime.MinValue;

    static bool _containsWorks = true;

    readonly record struct IndexHit(string Path, string Name, bool Folder, DateTime? Modified, bool Content);

    /// <summary>
    /// Names that start with the text or have a word starting with it first (newest first); if that
    /// found few, names that contain it anywhere, then files that contain the words, like Start.
    /// <paramref name="kind"/> limits it to a Windows Search kind ("picture", "document", "folder"…).
    /// </summary>
    static List<IndexHit> QueryIndex(string[] tokens, string raw, int max, string? kind)
    {
        var results = new List<IndexHit>();
        if (DateTime.UtcNow < _indexBrokenUntil) return results;

        string like = Clean(raw, forLike: true);
        var words = tokens.Select(t => Clean(t, forLike: false)).Where(t => t.Length > 0).ToArray();
        if (like.Length == 0 && (tokens.Length > 0 || kind == null)) return results;

        string filter = " AND System.ItemType <> '.lnk' AND System.ItemType <> '.url' AND System.FileName <> 'desktop.ini'" +
                        (kind != null ? " AND System.Kind = '" + kind + "'" : "") +
                        " ORDER BY System.DateModified DESC";
        string Select(int top, string? where) =>
            "SELECT TOP " + top + " System.ItemPathDisplay, System.ItemNameDisplay, System.ItemType, System.DateModified " +
            "FROM SystemIndex WHERE SCOPE='file:'" + (where != null ? " AND (" + where + ")" : "") + filter;

        string phrase = string.Join(" AND ", words.Select(w => "\"" + w + "*\""));
        try
        {
            if (like.Length == 0)
            {
                Read(Select(max, null), results, false); // just a filter: the newest of that kind
                return results;
            }

            string prefix = "System.FileName LIKE '" + like + "%'";
            if (_containsWorks && words.Length > 0)
            {
                try { Read(Select(max, prefix + " OR CONTAINS(System.FileName, '" + phrase + "')"), results, false); }
                catch (Exception e)
                {
                    // Some words (like "the") can't be used with CONTAINS; if it never works, stop trying it.
                    Log.Write("file index (word search): " + e.Message);
                    if (results.Count == 0 && ++_containsFailures >= 3) _containsWorks = false;
                    results.Clear();
                    Read(Select(max, prefix), results, false);
                }
            }
            else Read(Select(max, prefix), results, false);

            if (results.Count < max / 2 && like.Length >= 3)
                Read(Select(max, "System.FileName LIKE '%" + like + "%'"), results, false);

            // Inside files (documents, text, code): what Start also finds, ranked below name matches.
            if (results.Count < max / 2 && _containsWorks && words.Length > 0 && words.Sum(w => w.Length) >= 3 && kind != "folder")
            {
                try { Read(Select(max / 2, "CONTAINS(*, '" + phrase + "')"), results, true); }
                catch (Exception e) { Log.Write("file index (contents): " + e.Message); }
            }
        }
        catch (Exception e)
        {
            Log.Write("file index: " + e.Message);
            try { _conn?.Close(); } catch { }
            _conn = null;
            _indexBrokenUntil = DateTime.UtcNow.AddMinutes(2); // Windows Search off or busy: try later
        }
        return results;
    }

    static int _containsFailures;

    static void Read(string sql, List<IndexHit> results, bool content)
    {
        if (_conn == null)
        {
            var type = Type.GetTypeFromProgID("ADODB.Connection") ?? throw new InvalidOperationException("ADO is missing");
            dynamic c = Activator.CreateInstance(type)!;
            c.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            _conn = c;
        }
        dynamic rs = _conn!.Execute(sql);
        try
        {
            if ((bool)rs.EOF) return;
            // All rows in one call, as [column, row]: fewer COM round trips than reading field by field.
            if (rs.GetRows() is not object[,] rows) return;
            int lo = rows.GetLowerBound(1), hi = rows.GetUpperBound(1), c0 = rows.GetLowerBound(0);
            for (int i = lo; i <= hi; i++)
            {
                if (rows[c0, i] is not string path || path.Length == 0) continue;
                string name = rows[c0 + 1, i] as string ?? Path.GetFileName(path);
                bool folder = rows[c0 + 2, i] is string t && t.Equals("Directory", StringComparison.OrdinalIgnoreCase);
                DateTime? date = rows[c0 + 3, i] is DateTime d ? d.ToLocalTime() : null;
                results.Add(new IndexHit(path, name, folder, date, content));
            }
        }
        finally
        {
            try { rs.Close(); } catch { }
        }
    }

    /// <summary>Only letters, digits and a few safe characters go into the query text.</summary>
    static string Clean(string s, bool forLike)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '-' || (forLike && c is ' ' or '(' or ')' or '&' or '+' or '#' or '!' or ',')) sb.Append(c);
            else if (forLike && c == '_') sb.Append("[_]");
        }
        return sb.ToString().Trim();
    }

    // ---- Recent files (the same list File Explorer's "Recent" shows)

    static List<(string Stem, string Lnk, DateTime Used)> _recent = new();
    static DateTime _recentAt = DateTime.MinValue;
    static object? _wsh;

    static IEnumerable<(string Path, string Name, bool Folder, DateTime? Used)> RecentMatches(string[] tokens, int max, (string Kind, string[] Exts)? kind)
    {
        var results = new List<(string, string, bool, DateTime?)>();
        try
        {
            if (DateTime.UtcNow - _recentAt > TimeSpan.FromSeconds(30))
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                _recent = Directory.Exists(dir)
                    ? new DirectoryInfo(dir).EnumerateFiles("*.lnk")
                        .Select(f => (Path.GetFileNameWithoutExtension(f.Name), f.FullName, f.LastWriteTime))
                        .OrderByDescending(x => x.LastWriteTime)
                        .Take(400)
                        .ToList()
                    : new();
                _recentAt = DateTime.UtcNow;
            }
            // The shortcut's name is the file's name ("report.docx"), so the extension can filter before resolving.
            bool KindOk(string stem)
            {
                if (kind is not { } k) return true;
                if (k.Kind == "folder") return !Path.HasExtension(stem);
                return k.Exts.Contains(Path.GetExtension(stem).ToLowerInvariant());
            }
            var matches = _recent.Where(r => KindOk(r.Stem) && (tokens.Length == 0 || Search.Score(tokens, r.Stem) >= 40)).Take(max);
            foreach (var (stem, lnk, used) in matches)
            {
                string? target = LinkTarget(lnk);
                if (string.IsNullOrEmpty(target)) continue;
                bool folder;
                if (target.StartsWith(@"\\")) folder = !Path.HasExtension(target); // don't touch the network
                else if (Directory.Exists(target)) folder = true;
                else if (File.Exists(target)) folder = false;
                else continue; // moved or deleted
                if (kind is { Kind: "folder" } && !folder) continue;
                results.Add((target, folder ? NameOfPath(target) : Path.GetFileName(target), folder, used));
            }
        }
        catch (Exception e) { Log.Write("recent files: " + e.Message); }
        return results;
    }

    static string? LinkTarget(string lnk)
    {
        try
        {
            if (_wsh == null)
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null) return null;
                _wsh = Activator.CreateInstance(type);
            }
            dynamic sh = _wsh!;
            dynamic link = sh.CreateShortcut(lnk);
            try { return (string?)link.TargetPath; }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        catch { return null; }
    }
}
