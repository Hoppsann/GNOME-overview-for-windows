using System;
using System.Threading;

namespace Overview;

internal static class Launcher
{
    /// <summary>
    /// Runs shell work (starting apps, opening folders and pages, the UAC prompt for "Run as
    /// administrator") on its own thread, so the overview never freezes while it happens.
    /// Starting a Store app can take seconds, and a UAC prompt waits for the user.
    /// </summary>
    public static void Run(Action action)
    {
        // We're the foreground app right now: let whatever gets started take focus,
        // even if the overview has already closed by the time its window appears.
        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { Log.Write(e); }
        })
        { IsBackground = true, Name = "Overview launch" };
        thread.SetApartmentState(ApartmentState.STA); // shell APIs want STA
        thread.Start();
    }
}
