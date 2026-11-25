# PearUI

A Mac-inspired desktop layer for Windows 10/11: a menu bar, a Dock, Spotlight-style search, an All Apps screen, a Control Center, Stage Manager and Hot Corners, with a macOS-style Terminal and Linux commands in Command Prompt. It runs alongside File Explorer and never modifies system files.

> PearUI is an independent project. It is not affiliated with or endorsed by Apple. Mac and macOS are trademarks of Apple Inc.

## Install

Download **PearUI-Setup-x.y.z.exe** from the [Releases](../../releases) page and run it. No administrator rights are needed for PearUI itself, and .NET doesn't have to be installed. The installer lets you choose:

| Option | What it does |
|---|---|
| Start PearUI as soon as I sign in | A sign-in task starts PearUI right at logon at high priority (the Startup folder waits several seconds). |
| macOS-style Terminal | Windows Terminal (installed with winget if missing) with Command Prompt as the default profile, a translucent acrylic background, macOS "Pro" colors and a dark title bar. It also becomes Windows' default terminal app. The original Terminal settings are saved as `settings.json.pearui-backup`. |
| Linux commands in Command Prompt | `ls`, `grep`, `cat`, `touch`, `head`, `tail`, `which`, `sed`, `awk`, `nano` and more, from Git for Windows (installed with winget if missing). They go at the *end* of your user PATH, so Windows' own commands (`find`, `sort`, …) keep priority. Aliases `ll`, `la`, `pwd`, `clear` are added. |
| Full Linux (WSL) | Optional, off by default. Needs administrator approval and a restart. |

On the first start a welcome tour introduces everything and lets you pick appearance, accent color, and whether the Windows key opens All Apps and the Windows taskbar is hidden. (Reopen it from the pear menu → *Welcome to PearUI…*.)

Uninstall from *Settings → Apps*. It restores the taskbar, the Terminal settings, the PATH and every start-up entry. Your PearUI settings in `%APPDATA%\PearUI` are kept; Git for Windows and WSL are left installed.

> **Smart App Control / SmartScreen.** Windows may warn about, or block, an installer that isn't code-signed. SmartScreen: *More info → Run anyway*. With **Smart App Control** enforcing, unsigned programs are blocked outright; either sign the release (for example with Azure Trusted Signing, see below) or turn Smart App Control off in Windows Security → App & browser control.

## Features

| Feature | Details |
|---|---|
| **Menu bar** | Reserves the top of every display. Pear menu (About, Settings, Sleep/Restart/Shut Down, Lock, Log Out, Welcome tour), active app name, File / Edit / View / Window / Help menus (they send the usual shortcuts to the app), and on the right: now playing, battery, Wi‑Fi, volume (scroll to change), search, Control Center and the clock. Menus are real frosted glass. Hides for full-screen apps. |
| **Dock** | Pinned and running apps with real icons, magnification, launch bounce, running dots, Downloads stack and Recycle Bin. Auto-hides and stays visible on an empty desktop. Drop files from Explorer on the Recycle Bin (recycle), Downloads (move) or an app (open with it). Right-click for windows, Keep in Dock, Show in File Explorer, Hide, Quit. |
| **Search** (`Alt+Space`) | Apps, files (Windows Search index), settings pages, commands and quick math (`12*(3+4)`, `2^10`), with a web fallback. |
| **All Apps** (`Win` key, `Alt+F1`, Dock icon) | Full-screen Launchpad: your blurred wallpaper behind a paged grid. Two-finger swipe (one page per swipe), mouse wheel, pinch to close, type to search. Folders: drag an app onto another to create one, onto a folder to add to it, and drag out to remove. Duplicate shortcuts (VLC "skinned", PowerShell x86, …) are merged. |
| **Control Center** | Wi‑Fi and Bluetooth tiles (click toggles, **right-click picks a network or device**, with a password prompt for new Wi‑Fi), Stage Manager, Screen Mirroring, brightness and Dark Mode, volume and mute, media controls. |
| **Stage Manager** | Keeps your current app center stage. Push the pointer to the left edge to reveal tilted previews of your other recent apps and swap one in. |
| **Hot Corners** | Mission Control, Desktop, Notification Center, All Apps, Search, Stage Manager, Start, Lock Screen or display sleep. Defaults: bottom-left Mission Control, bottom-right Desktop. |
| **Glass** | Real acrylic blur with Windows' native rounded corners; falls back to see-through backgrounds if *Transparency effects* is off (Settings can switch it on). |
| **Windows key** | Optionally opens All Apps instead of the Start menu; Win+E, Win+D, Win+Tab and the rest keep working. |
| **Windows taskbar** | Optionally hidden while PearUI runs. It always comes back when PearUI quits, even after a crash. |

## Build from source

You need the .NET 8 SDK.

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1              # build, install for this user, set everything up, launch
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall   # remove it again
```

During development you can also run it from the build folder:

```powershell
dotnet build src\MacUI\MacUI.csproj
src\MacUI\bin\Debug\net8.0-windows10.0.22621.0\PearUI.exe           # start (run it again to open Settings)
src\MacUI\bin\Debug\net8.0-windows10.0.22621.0\PearUI.exe --quit    # stop the running instance cleanly
```

## Publish a release (GitHub Releases)

1. Bump `<Version>` in `src/MacUI/MacUI.csproj` if you like (the tag decides the installer's version).
2. Push a version tag:

   ```powershell
   git tag v1.0.0
   git push origin v1.0.0
   ```

3. The **Release** workflow (`.github/workflows/release.yml`) builds `PearUI-Setup-1.0.0.exe` and attaches it to a new GitHub release.

To build the installer on your own PC instead: install Inno Setup (`winget install JRSoftware.InnoSetup`) and run

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 1.0.0   # → dist\PearUI-Setup-1.0.0.exe
```

The installer is self-contained (no .NET needed on the user's PC) and ReadyToRun-compiled for a quick start. To **code-sign** releases, add your certificate to the workflow and sign `dist\publish\PearUI.exe` and the installer with `signtool` (Azure Trusted Signing works well for open-source projects). That removes the SmartScreen warning and the Smart App Control block.

## Project layout

```
installer/         PearUI.iss (Inno Setup), configure.ps1 (Terminal, Linux commands, first-run setup and uninstall), build-installer.ps1
.github/workflows/ release.yml (tag → installer → GitHub release)
src/MacUI/
  App.xaml(.cs)     start-up, single instance, tray icon, hotkeys, live settings, clean shutdown
  Core/             settings, window tracking, app catalog, system status, start-up task, taskbar, Terminal setup
  Native/           Win32 interop: AppBar, blur, shell icons, Core Audio, hotkeys, file operations
  Shell/            glass window base, glass menus, Wi‑Fi / Bluetooth menus, hot corners, Windows-key handling
  MenuBar/ Dock/ Launcher/ ControlCenter/ AllApps/ StageManager/ Onboarding/ Settings/
  Themes/           ThemeManager (light/dark/accent) and shared control styles
```

## Known limitations

- Windows apps don't publish their menus, so File / Edit / View send common shortcuts instead of showing the app's real menu.
- Gestures with three or four fingers belong to Windows. Set a four-finger gesture to `Alt+F1` in *Settings → Bluetooth & devices → Touchpad* to open All Apps with it.
- Brightness only works on built-in laptop displays.
- Connecting Bluetooth devices from the menu works through their audio services; devices without them can't be connected this way (pair and connect those in Windows).
- Stage Manager works on the primary display only.
