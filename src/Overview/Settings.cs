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
    public bool FirstRun { get; set; } = true;
    /// <summary>Chosen in the installer. Only installed copies check; the standalone exe never does.</summary>
    public bool CheckForUpdates { get; set; } = false;
    /// <summary>Show an auto-hidden taskbar while the overview is open (pauses auto-hide meanwhile).</summary>
    public bool ShowAutoHideTaskbar { get; set; } = true;
    /// <summary>Set while auto-hide is paused, so it's switched back on if Overview exits unexpectedly.</summary>
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

internal static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(App.AppName) is string;
    }

    /// <param name="exe">The exe to start (the installer passes the installed copy); defaults to this exe.</param>
    public static void Set(bool on, string? exe = null)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(App.AppName, $"\"{exe ?? Environment.ProcessPath}\"");
            else k.DeleteValue(App.AppName, false);
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
