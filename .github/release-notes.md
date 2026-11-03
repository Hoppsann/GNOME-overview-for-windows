## What's new in 1.8.6
Faster start when you sign in to Windows:
- **Starts sooner after sign-in.** *Start with Windows* now uses a Task Scheduler task that runs as soon as you sign in. Apps in the Startup list are held back by Windows for several seconds. If your account can't create the task, it falls back to the Startup list. Existing installs switch over automatically.
- **Faster to load.** The app unpacks itself once to a cache folder instead of decompressing about 170 MB on every start.
- **The Windows key works sooner.** The keyboard hook starts first. The wallpaper blur, app list and icon preloading wait until startup is done and run at low priority, so they don't slow down the rest of your PC while it boots.

## Download
- **`Overview-Setup.exe`**: the installer. Choose *Start with Windows*, *Check for updates automatically* and *Desktop shortcut*. Installs for your user only, no admin needed. If you already have Overview installed with updates on, it will offer this version by itself.
- **`Overview-standalone.exe`**: the same app without installing. Run it from anywhere. It never checks for updates.

Windows SmartScreen may warn because the exe isn't code-signed yet: click **More info → Run anyway**.
