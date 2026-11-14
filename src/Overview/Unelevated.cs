using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Overview;

/// <summary>
/// Starts things without admin rights when Overview itself runs as administrator (some people run
/// it that way so the Windows key also works while an admin window has focus). A program Overview
/// starts itself would inherit its admin rights, so Explorer is asked to start it instead: it then
/// runs as it would from the Start menu. When Overview isn't elevated, nothing here does anything.
/// </summary>
internal static class Unelevated
{
    static readonly bool Elevated = IsElevated();

    static bool IsElevated()
    {
        try { return Environment.IsPrivilegedProcess; }
        catch { return false; }
    }

    /// <summary>
    /// Has Explorer open <paramref name="file"/> (a program, shortcut, shell item or URL) the way
    /// ShellExecute would, so it runs without Overview's admin rights.
    /// </summary>
    /// <returns>
    /// True if the launch was handed to Explorer. False if Overview isn't elevated or Explorer
    /// can't be reached: the caller then starts it itself, as it always did.
    /// </returns>
    public static bool TryStart(string file, string? arguments = null)
    {
        if (!Elevated) return false;

        object? shell = null;
        try { shell = ExplorerShell(); }
        catch (Exception e) { Log.Write(e); }
        if (shell == null)
        {
            Log.Write("Explorer can't be reached: starting directly, with Overview's admin rights");
            return false;
        }

        // From here on the launch is Explorer's. If it fails, or the user says no to a UAC prompt,
        // the caller must not start it after all: it would then run as administrator unasked.
        try { ((dynamic)shell).ShellExecute(file, arguments ?? "", "", "", SW_SHOWNORMAL); }
        catch (Exception e) { Log.Write(e); }
        finally { Release(shell); }
        return true;
    }

    /// <summary>Quotes one command-line argument so the started program reads it back unchanged.</summary>
    public static string Quote(string argument)
    {
        // Windows' rules: a backslash only counts in front of a quote, where each backslash
        // (and the quote itself) needs one more backslash before it.
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\') { backslashes++; continue; }
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>
    /// Explorer's own "Shell.Application" object, reached through the desktop (which exists as
    /// long as Explorer runs), so what it starts is started by Explorer. Null without a desktop.
    /// The approach is from https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643
    /// </summary>
    static object? ExplorerShell()
    {
        // No desktop, or one that's not responding: a call into it would never come back.
        IntPtr desktopWindow = GetShellWindow();
        if (desktopWindow == IntPtr.Zero || Native.IsHungAppWindow(desktopWindow)) return null;

        IShellWindows? windows = null;
        IShellBrowser? browser = null;
        IShellView? view = null;
        object? desktop = null, folderView = null;
        try
        {
            windows = (IShellWindows?)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClass)!);
            if (windows == null) return null;
            desktop = windows.FindWindowSW(CSIDL_DESKTOP, null, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
            if (desktop == null) return null;

            Guid service = TopLevelBrowser, browserId = typeof(IShellBrowser).GUID, dispatchId = Dispatch;
            browser = (IShellBrowser)((IComServiceProvider)desktop).QueryService(ref service, ref browserId);
            view = browser.QueryActiveShellView();
            folderView = view.GetItemObject(SVGIO_BACKGROUND, ref dispatchId);
            return ((dynamic)folderView).Application;
        }
        finally { Release(folderView, view, browser, desktop, windows); }
    }

    static void Release(params object?[] comObjects)
    {
        foreach (var o in comObjects)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
            catch { }
        }
    }

    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();

    const int CSIDL_DESKTOP = 0, SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1, SW_SHOWNORMAL = 1;
    const uint SVGIO_BACKGROUND = 0;

    static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser
    static readonly Guid Dispatch = new("00020400-0000-0000-C000-000000000046");        // IID_IDispatch

    // The COM interfaces on the way from the desktop to Explorer's Shell.Application object. Only
    // the one method needed of each is real. The others are placeholders that keep it at its
    // position in the interface, with made-up signatures: they must never be called.

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    interface IShellWindows
    {
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW(object? location, object? root, int windowClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IComServiceProvider
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object QueryService(ref Guid service, ref Guid interfaceId);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        void GetWindow(); void ContextSensitiveHelp(); // IOleWindow
        void InsertMenusSB(); void SetMenuSB(); void RemoveMenusSB(); void SetStatusTextSB(); void EnableModelessSB();
        void TranslateAcceleratorSB(); void BrowseObject(); void GetViewStateStream(); void GetControlWindow(); void SendControlMsg();
        [return: MarshalAs(UnmanagedType.Interface)]
        IShellView QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellView
    {
        void GetWindow(); void ContextSensitiveHelp(); // IOleWindow
        void TranslateAccelerator(); void EnableModeless(); void UIActivate(); void Refresh(); void CreateViewWindow();
        void DestroyViewWindow(); void GetCurrentInfo(); void AddPropertySheetPages(); void SaveViewState(); void SelectItem();
        [return: MarshalAs(UnmanagedType.Interface)]
        object GetItemObject(uint item, ref Guid interfaceId);
    }
}
