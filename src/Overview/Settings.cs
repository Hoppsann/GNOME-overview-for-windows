using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Overview;

internal sealed class Settings
{
    public bool SuperKey { get; set; } = true;
    public bool HotCorner { get; set; } = false;
    public bool WebSuggestions { get; set; } = true;
    /// <summary>"default" (the browser's own engine), "google", "bing", "duckduckgo" or "brave".</summary>
    public string SearchEngine { get; set; } = "default";
    public bool LearnFromUsage { get; set; } = true;
    /// <summary>App results shown under the search bar (the web search tile comes on top of these).</summary>
    public int MaxAppResults { get; set; } = 7;
    /// <summary>Animation duration multiplier: 1 normal, 0.5 fast, 1.6 slow, 0 off.</summary>
    public double AnimationSpeed { get; set; } = 1.0;
    /// <summary>How much the wallpaper behind the overview is darkened, 0–80 %.</summary>
    public int DimPercent { get; set; } = 36;
    /// <summary>Tapping the Windows key while the overview is open switches to the highlighted window (else just closes).</summary>
    public bool SecondTapSwitches { get; set; } = true;
    public bool FirstRun { get; set; } = true;
    /// <summary>Chosen in the installer. Only installed copies check; the standalone exe never does.</summary>
    public bool CheckForUpdates { get; set; } = false;
    /// <summary>Left over from 1.8.3, which paused taskbar auto-hide: if set, auto-hide is switched back on.</summary>
    public bool RestoreTaskbarAutoHide { get; set; }
    /// <summary>Version that last ran, so an update can say "Updated to x.y.z".</summary>
    public string? LastVersion { get; set; }

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Overview");

    static string FilePath => Path.Combine(DataDir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception e) { Log.Write(e); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Log.Write(e); }
    }
}

/// <summary>
/// "Start with Windows". Preferred: a Task Scheduler task that runs at sign-in. It starts right away,
/// while apps in the Startup list (the Run key) are held back by Windows for several seconds after
/// signing in. If Windows won't let this account create the task, the Run key is used instead.
/// Which one is in use is remembered in HKCU\Software\Hoppsann\Overview\AutoStart ("task" or "run").
/// </summary>
internal static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string StateKey = @"Software\Hoppsann\Overview";
    const string TaskName = "Overview (start at sign-in)";

    static string? Mode
    {
        get
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(StateKey); return k?.GetValue("AutoStart") as string; }
            catch { return null; }
        }
        set
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(StateKey);
                if (value == null) k.DeleteValue("AutoStart", false);
                else k.SetValue("AutoStart", value);
            }
            catch (Exception e) { Log.Write(e); }
        }
    }

    static string? RunValue()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(App.AppName) as string; }
        catch { return null; }
    }

    public static bool IsEnabled() => Mode == "task" || RunValue() != null;

    /// <summary>True if autostart starts an exe in <paramref name="folder"/> (for uninstall).</summary>
    public static bool PointsInto(string folder) =>
        Mode == "task" || (RunValue()?.Contains(folder, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <param name="exe">The exe to start (the installer passes the installed copy); defaults to this exe.</param>
    public static void Set(bool on, string? exe = null)
    {
        exe ??= Environment.ProcessPath ?? "";
        try
        {
            if (on && CreateTask(exe))
            {
                SetRunValue(null);
                Mode = "task";
            }
            else if (on)
            {
                SetRunValue(exe);
                Mode = "run";
            }
            else
            {
                if (Mode == "task") DeleteTask();
                SetRunValue(null);
                Mode = null;
            }
        }
        catch (Exception e) { Log.Write(e); }
    }

    /// <summary>Installs from before the sign-in task (Run key only) switch to it once, quietly.</summary>
    public static void UpgradeToFastStart()
    {
        try
        {
            if (Mode != null) return; // already decided
            var run = RunValue();
            if (run == null) return;  // autostart is off
            string exe = run.Trim().Trim('"');
            if (File.Exists(exe)) Set(true, exe);
        }
        catch (Exception e) { Log.Write(e); }
    }

    static void SetRunValue(string? exe)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (exe != null) k.SetValue(App.AppName, $"\"{exe}\"");
        else k.DeleteValue(App.AppName, false);
    }

    static bool CreateTask(string exe)
    {
        string? sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        if (sid == null) return false;
        string esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;
        // For this user only; normal priority (tasks default to below normal); no time limit
        // (the default stops tasks after 3 days); runs on battery too.
        string xml = $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>Overview</Author>
    <Description>Starts Overview (GNOME-style overview for Windows) when you sign in.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{sid}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{sid}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{esc(exe)}</Command>
      <WorkingDirectory>{esc(Path.GetDirectoryName(exe) ?? "")}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
        string file = Path.Combine(Path.GetTempPath(), "overview-task.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode); // schtasks wants UTF-16
            int code = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            if (code != 0) Log.Write($"couldn't create the sign-in task (schtasks exit {code}), using the Startup list");
            return code == 0;
        }
        finally { try { File.Delete(file); } catch { } }
    }

    static void DeleteTask() => Schtasks($"/Delete /TN \"{TaskName}\" /F");

    static int Schtasks(string args)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p == null) return -1;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            return p.WaitForExit(15000) ? p.ExitCode : -1;
        }
        catch (Exception e)
        {
            Log.Write(e);
            return -1;
        }
    }
}

/// <summary>
/// The exe unpacks itself once into a cache folder (%TEMP%\.net\Overview\...) so later starts load
/// from there instead of decompressing ~170 MB every time. Each version gets its own folder; this
/// removes old versions' folders.
/// </summary>
internal static class StartupCache
{
    public static void CleanOldVersions()
    {
        try
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var parent = current.Parent;
            // Only touch the bundle cache: %TEMP%\.net\Overview\<hash>
            if (parent == null || !parent.Name.Equals("Overview", StringComparison.OrdinalIgnoreCase)
                || parent.Parent == null || !parent.Parent.Name.Equals(".net", StringComparison.OrdinalIgnoreCase))
                return;
            foreach (var dir in parent.GetDirectories())
            {
                if (dir.FullName.Equals(current.FullName, StringComparison.OrdinalIgnoreCase)) continue;
                // Skip recent ones: another version (e.g. the standalone exe) might still be running from it.
                if (DateTime.UtcNow - dir.LastWriteTimeUtc < TimeSpan.FromDays(3)) continue;
                try { dir.Delete(recursive: true); } catch { /* in use: try again next time */ }
            }
        }
        catch (Exception e) { Log.Write(e); }
    }
}

internal static class Log
{
    static readonly object Gate = new();

    static string FilePath => Path.Combine(Settings.DataDir, "log.txt");

    public static void Write(object? what)
    {
        if (what == null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.DataDir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {what}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* logging must never throw */ }
    }
}
