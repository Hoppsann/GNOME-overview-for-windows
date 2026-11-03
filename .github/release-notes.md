## What's new in 1.8.7
**Search learns what you use**:
- **What you picked for what you typed comes first.** If you open Command Prompt after typing "c", then next time "c" puts Command Prompt first instead of Calculator or Clock. Typing "comm" and picking it also teaches "c" and "co". If you've picked different apps for the same letters, the most recent pick wins.
- **Apps you use more rank higher**, counted wherever you open them from: Overview, the taskbar, the Start menu or the desktop. Recent use counts more.
- How well the text matches still comes first, so "clock" always finds Clock.
- If the top result is already open, Enter switches to its window instead of starting a second copy.
- What search has learned is stored only on your PC in `%APPDATA%\Overview\usage.json`. Delete it to start over.

## Download
- **`Overview-Setup.exe`**: the installer. Choose *Start with Windows*, *Check for updates automatically* and *Desktop shortcut*. Installs for your user only, no admin needed. If you already have Overview installed with updates on, it will offer this version by itself.
- **`Overview-standalone.exe`**: the same app without installing. Run it from anywhere. It never checks for updates.

Windows SmartScreen may warn because the exe isn't code-signed yet: click **More info → Run anyway**.
