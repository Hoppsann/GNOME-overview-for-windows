using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace Overview;

internal sealed class InstallOptions
{
    public bool StartWithWindows = true;
    public bool CheckForUpdates = true;
    public bool DesktopShortcut;
}

/// <summary>
/// Per-user install (no admin): %LOCALAPPDATA%\Programs\Overview\Overview.exe, a Start Menu shortcut,
/// an entry in Settings → Installed apps, and optionally autostart and a desktop shortcut.
/// The installer is this same exe: the setup copies itself into place.
/// </summary>
internal static class Installer
{
    public const string QuitEventName = @"Local\Hoppsann.Overview.Quit";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Overview";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Overview");

    public static string InstalledExe => Path.Combine(InstallDir, "Overview.exe");

    static string StartMenuLink =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Overview.lnk");

    static string DesktopLink =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Overview.lnk");

    public static bool IsInstalled => File.Exists(InstalledExe);

    public static bool HasDesktopShortcut => File.Exists(DesktopLink);

    /// <summary>True when this process is the installed copy (only that one checks for updates).</summary>
    public static bool IsInstalledCopy => SamePath(Environment.ProcessPath, InstalledExe);

    /// <summary>The file name decides the mode: "Overview-Setup.exe" runs the installer.</summary>
    public static bool IsSetupFileName =>
        Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase);

    static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // ------------------------------------------------------------------ install

    public static void Install(InstallOptions o)
    {
        StopRunningCopies();
        Directory.CreateDirectory(InstallDir);

        string self = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find the setup file.");
        if (!SamePath(self, InstalledExe)) CopyWithRetry(self, InstalledExe);

        CreateShortcut(StartMenuLink, InstalledExe);
        if (o.DesktopShortcut) CreateShortcut(DesktopLink, InstalledExe);
        else TryDelete(DesktopLink);

        WriteUninstallEntry();
        AutoStart.Set(o.StartWithWindows, InstalledExe);

        var settings = Settings.Load();
        settings.CheckForUpdates = o.CheckForUpdates;
        settings.Save();
        Log.Write($"installed {Updater.Current} (autostart {o.StartWithWindows}, updates {o.CheckForUpdates}, desktop {o.DesktopShortcut})");
    }

    /// <summary>
    /// "Overview-Setup.exe --update", started by the app's updater: installs over the old version
    /// with the current choices kept, then starts the new version. No wizard.
    /// </summary>
    public static int RunUpdate()
    {
        try
        {
            var settings = Settings.Load();
            Install(new InstallOptions
            {
                StartWithWindows = AutoStart.IsEnabled(),
                CheckForUpdates = settings.CheckForUpdates,
                DesktopShortcut = HasDesktopShortcut
            });
            Launch();
            return 0;
        }
        catch (Exception e)
        {
            Log.Write(e);
            MessageBox.Show($"Overview couldn't be updated:\n\n{e.Message}\n\nDownload the latest version from:\n{Updater.RepoUrl}/releases",
                "Overview update", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    public static void Launch()
    {
        try { Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallDir }); }
        catch (Exception e) { Log.Write(e); }
    }

    static void CopyWithRetry(string from, string to)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(300); // the old exe can stay locked for a moment after it quits
            }
            catch (UnauthorizedAccessException) when (attempt < 20)
            {
                Thread.Sleep(300);
            }
        }
    }

    /// <summary>Asks every running Overview (installed or standalone) to quit, then makes sure the installed exe is free.</summary>
    static void StopRunningCopies()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(QuitEventName);
            ev.Set();
        }
        catch { /* not running */ }

        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline && InstalledCopiesRunning().Count > 0) Thread.Sleep(200);

        foreach (var p in InstalledCopiesRunning())
        {
            try { p.Kill(); p.WaitForExit(3000); }
            catch (Exception e) { Log.Write(e); }
        }
    }

    static List<Process> InstalledCopiesRunning()
    {
        var list = new List<Process>();
        int me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("Overview"))
        {
            if (p.Id == me) continue;
            try { if (SamePath(p.MainModule?.FileName, InstalledExe)) list.Add(p); }
            catch { /* can't inspect (other user): not ours */ }
        }
        return list;
    }

    static void CreateShortcut(string linkPath, string target)
    {
        object? shell = null, link = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is missing");
            shell = Activator.CreateInstance(type);
            dynamic sh = shell!;
            link = sh.CreateShortcut(linkPath);
            dynamic l = link!;
            l.TargetPath = target;
            l.WorkingDirectory = Path.GetDirectoryName(target);
            l.IconLocation = target + ",0";
            l.Description = "GNOME-style Activities overview for Windows";
            l.Save();
        }
        catch (Exception e) { Log.Write($"shortcut {linkPath}: {e.Message}"); }
        finally
        {
            if (link != null) Marshal.FinalReleaseComObject(link);
            if (shell != null) Marshal.FinalReleaseComObject(shell);
        }
    }

    static void WriteUninstallEntry()
    {
        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", "Overview");
        k.SetValue("DisplayVersion", Updater.Current.ToString());
        k.SetValue("Publisher", "Hoppsann");
        k.SetValue("DisplayIcon", InstalledExe + ",0");
        k.SetValue("InstallLocation", InstallDir);
        k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        k.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
        k.SetValue("URLInfoAbout", Updater.RepoUrl);
        k.SetValue("HelpLink", Updater.RepoUrl + "/issues");
        k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try { k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord); }
        catch { }
    }

    // ------------------------------------------------------------------ uninstall

    /// <summary>"Overview.exe --uninstall", run from Settings → Installed apps.</summary>
    public static int Uninstall(bool quiet)
    {
        if (!quiet && MessageBox.Show("Uninstall Overview?\n\nYour settings will be removed too.", "Uninstall Overview",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return 1;

        try
        {
            StopRunningCopies();
            if (Settings.Load().RestoreTaskbarAutoHide) Native.SetTaskbarAutoHide(true); // was paused when killed
            if (AutoStart.PointsInto(InstallDir)) AutoStart.Set(false);
            TryDelete(StartMenuLink);
            TryDelete(DesktopLink);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            try { Directory.Delete(Settings.DataDir, recursive: true); } catch { }

            // This exe is running, so it can't delete its own folder. A hidden cmd does it a moment after we exit.
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{InstallDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath()
            });

            if (!quiet) MessageBox.Show("Overview has been uninstalled.", "Uninstall Overview", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        catch (Exception e)
        {
            if (!quiet) MessageBox.Show("Overview couldn't be fully uninstalled:\n\n" + e.Message, "Uninstall Overview",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
