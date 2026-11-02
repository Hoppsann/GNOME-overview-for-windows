## What's new in 1.8.5
Stability fixes for freezes and odd behavior:
- **Fixed: the Windows key could get stuck "held down".** Overview used to block the real Windows-key release and send a fake one. Windows silently drops fake key presses while an admin window has focus, so the key could stay "down" and every key you typed acted as a Win+ shortcut. Real key presses are never blocked now.
- **No more freezing while starting things.** Opening apps (Store apps can take seconds), *Run as administrator* (which waited for the UAC prompt), file locations and web searches now start on a separate thread.
- **Icons load in the background**, so opening the overview and typing never wait on the disk.
- **Switching windows can't hang** on an app that's Not Responding, and no longer pops open the old app's menu bar.
- Windows-key taps made while the PC was busy no longer pile up and flicker the overview open and closed.
- The keyboard hook reinstalls itself every 10 minutes, in case Windows removed it while the PC was overloaded (that made the Windows key stop opening the overview).
- The installed-apps list can't get stuck, and the app grid no longer rebuilds itself when nothing changed.

## Download
- **`Overview-Setup.exe`**: the installer. Choose *Start with Windows*, *Check for updates automatically* and *Desktop shortcut*. Installs for your user only, no admin needed. If you already have Overview installed with updates on, it will offer this version by itself.
- **`Overview-standalone.exe`**: the same app without installing. Run it from anywhere. It never checks for updates.

Windows SmartScreen may warn because the exe isn't code-signed yet: click **More info → Run anyway**.
