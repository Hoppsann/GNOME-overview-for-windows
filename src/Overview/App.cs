using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Overview;

public sealed class App : Application
{
    internal const string AppName = "Overview";

    static Mutex? _instanceMutex;
    static EventWaitHandle? _showEvent;
    static EventWaitHandle? _quitEvent;

    internal static Settings Settings = new();

    OverviewWindow? _overview;
    KeyboardHook? _hook;
    HotCorner? _hotCorner;
    Forms.NotifyIcon? _tray;

    [STAThread]
    public static int Main(string[] args)
    {
        // One exe, four jobs: uninstaller, silent updater, setup wizard (when named "...Setup..."), or the app.
        if (HasArg(args, "--uninstall")) return Installer.Uninstall(quiet: HasArg(args, "--quiet"));
        if (HasArg(args, "--update")) return Installer.RunUpdate();
        if (Installer.IsSetupFileName)
        {
            var setup = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            return setup.Run(new SetupWindow());
        }

        _instanceMutex = new Mutex(true, @"Local\Hoppsann.Overview.Instance", out bool first);
        if (!first)
        {
            // Already running: launching it again opens the overview instead.
            try { using var ev = EventWaitHandle.OpenExisting(@"Local\Hoppsann.Overview.Show"); ev.Set(); }
            catch { }
            return 0;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Hoppsann.Overview.Show");
        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Installer.QuitEventName);

        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        return app.Run();
    }

    static bool HasArg(string[] args, string name) =>
        Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write(ex.Exception);
            ex.Handled = true;
            _overview?.ForceHide();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Write(ex.ExceptionObject);
            if (Settings.RestoreTaskbarAutoHide) Native.SetTaskbarAutoHide(true); // never leave it paused
        };

        Settings = Settings.Load();
        if (Settings.RestoreTaskbarAutoHide)
        {
            // Overview quit while it had taskbar auto-hide paused: switch it back on.
            Native.SetTaskbarAutoHide(true);
            Settings.RestoreTaskbarAutoHide = false;
            Settings.Save();
        }
        Mru.Start();
        AppCatalog.StartRefresh(); // also preloads every icon in the background

        _overview = new OverviewWindow();
        _overview.Prepare();

        _hook = new KeyboardHook(Dispatcher, ToggleOverview) { Enabled = Settings.SuperKey };
        _hook.Start();

        _hotCorner = new HotCorner(ToggleOverview) { Enabled = Settings.HotCorner };

        ThreadPool.RegisterWaitForSingleObject(_showEvent!, (_, _) => Dispatcher.InvokeAsync(ToggleOverview), null, -1, false);
        // The installer asks a running copy to quit before replacing it.
        ThreadPool.RegisterWaitForSingleObject(_quitEvent!, (_, _) => Dispatcher.InvokeAsync(Quit), null, -1, true);

        CreateTray();

        string version = Updater.Current.ToString();
        if (Settings.FirstRun)
        {
            Settings.FirstRun = false;
            _tray!.ShowBalloonTip(6000, "Overview is running",
                "Tap the Windows key to open it. Right-click the tray icon for options.", Forms.ToolTipIcon.Info);
        }
        else if (Settings.LastVersion != null && Settings.LastVersion != version)
        {
            _tray!.ShowBalloonTip(5000, $"Overview updated to {version}", "Tap the Windows key to open it.", Forms.ToolTipIcon.Info);
        }
        Settings.LastVersion = version;
        Settings.Save();

        if (Installer.IsInstalledCopy) StartUpdateChecks();
    }

    // ------------------------------------------------------------------ updates

    DispatcherTimer? _updateTimer;
    bool _checkingForUpdates;
    Version? _declinedVersion;

    /// <summary>Installed copies with updates turned on check 30 s after start, then every 12 hours.</summary>
    void StartUpdateChecks()
    {
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(12);
            if (Settings.CheckForUpdates) _ = CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();
    }

    async System.Threading.Tasks.Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingForUpdates) return;
        _checkingForUpdates = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var latest = await Updater.GetLatestAsync(timeout.Token);
            if (latest == null || !Updater.IsNewer(latest))
            {
                if (manual) Ask($"You have the latest version ({Updater.Current}).", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!manual && _declinedVersion == latest.Version) return; // said "No": don't nag again until next start

            var answer = Ask($"Overview {latest.Version} is available. You have {Updater.Current}.\n\n" +
                             "Update now? It takes a few seconds and keeps your settings.",
                             MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes)
            {
                _declinedVersion = latest.Version;
                return;
            }

            _tray?.ShowBalloonTip(4000, "Updating Overview", $"Downloading version {latest.Version}…", Forms.ToolTipIcon.Info);
            string setup = await Updater.DownloadSetupAsync(latest, CancellationToken.None);
            // The setup installs the new version, asks this copy to quit, and starts the new one.
            Process.Start(new ProcessStartInfo(setup, "--update") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.Write(e);
            if (manual) Ask("Couldn't check for updates:\n\n" + e.Message, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _checkingForUpdates = false; }
    }

    static MessageBoxResult Ask(string text, MessageBoxButton buttons, MessageBoxImage image) =>
        // DefaultDesktopOnly keeps the box on top, since Overview has no window of its own to own it.
        MessageBox.Show(text, "Overview", buttons, image,
            buttons == MessageBoxButton.YesNo ? MessageBoxResult.Yes : MessageBoxResult.OK,
            MessageBoxOptions.DefaultDesktopOnly);

    void ToggleOverview()
    {
        try { _overview?.Toggle(); }
        catch (Exception e) { Log.Write(e); _overview?.ForceHide(); }
    }

    void CreateTray()
    {
        Forms.Application.EnableVisualStyles();

        var open = new Forms.ToolStripMenuItem("Open overview", null, (_, _) => Dispatcher.InvokeAsync(ToggleOverview));
        open.Font = new Drawing.Font(open.Font, Drawing.FontStyle.Bold);

        var superKey = new Forms.ToolStripMenuItem("Tap Windows key to open") { Checked = Settings.SuperKey, CheckOnClick = true };
        superKey.CheckedChanged += (_, _) =>
        {
            Settings.SuperKey = superKey.Checked;
            if (_hook != null) _hook.Enabled = superKey.Checked;
            Settings.Save();
        };

        var corner = new Forms.ToolStripMenuItem("Hot corner (top-left)") { Checked = Settings.HotCorner, CheckOnClick = true };
        corner.CheckedChanged += (_, _) =>
        {
            Settings.HotCorner = corner.Checked;
            if (_hotCorner != null) _hotCorner.Enabled = corner.Checked;
            Settings.Save();
        };

        var suggestions = new Forms.ToolStripMenuItem("Web search suggestions") { Checked = Settings.WebSuggestions, CheckOnClick = true };
        suggestions.ToolTipText = "Shows your search engine's suggestions while you type (sends what you type to it)";
        suggestions.CheckedChanged += (_, _) =>
        {
            Settings.WebSuggestions = suggestions.Checked;
            Settings.Save();
        };

        var taskbar = new Forms.ToolStripMenuItem("Show auto-hidden taskbar in overview") { Checked = Settings.ShowAutoHideTaskbar, CheckOnClick = true };
        taskbar.ToolTipText = "If your taskbar auto-hides, it's shown while the overview is open (maximized windows make room meanwhile)";
        taskbar.CheckedChanged += (_, _) =>
        {
            Settings.ShowAutoHideTaskbar = taskbar.Checked;
            Settings.Save();
        };

        var startup = new Forms.ToolStripMenuItem("Start with Windows") { Checked = AutoStart.IsEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => AutoStart.Set(startup.Checked);

        var quit = new Forms.ToolStripMenuItem("Quit", null, (_, _) => Quit());

        var menu = new Forms.ContextMenuStrip();
        var version = new Forms.ToolStripMenuItem($"Overview {Updater.Current}") { Enabled = false };
        menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            open, version, new Forms.ToolStripSeparator(), superKey, corner, suggestions, taskbar, startup
        });

        // Update options only for the installed copy (the standalone exe never updates itself).
        if (Installer.IsInstalledCopy)
        {
            var autoUpdate = new Forms.ToolStripMenuItem("Check for updates automatically") { Checked = Settings.CheckForUpdates, CheckOnClick = true };
            autoUpdate.CheckedChanged += (_, _) =>
            {
                Settings.CheckForUpdates = autoUpdate.Checked;
                Settings.Save();
            };
            var checkNow = new Forms.ToolStripMenuItem("Check for updates now", null, (_, _) => Dispatcher.InvokeAsync(() => CheckForUpdatesAsync(manual: true)));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(autoUpdate);
            menu.Items.Add(checkNow);
        }

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(quit);

        _tray = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Overview — tap the Windows key",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Dispatcher.InvokeAsync(ToggleOverview);
        };
    }

    static Drawing.Icon LoadIcon()
    {
        try
        {
            using var s = typeof(App).Assembly.GetManifestResourceStream("overview.ico");
            if (s != null) return new Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception e) { Log.Write(e); }
        return Drawing.SystemIcons.Application;
    }

    void Quit()
    {
        try
        {
            _hook?.Stop();
            Mru.Stop();
            if (_hotCorner != null) _hotCorner.Enabled = false;
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            _overview?.ForceHide();
            _overview?.AllowRealClose();
            _overview?.Close();
        }
        catch (Exception e) { Log.Write(e); }
        Shutdown();
    }
}
