# Changelog

All notable changes to PearUI are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- **About This PC** (pear menu): a detailed look at your computer in a frosted-glass window styled after About This Mac, with your wallpaper in a circle, the system name in large type and the essentials beneath. Tabs for Overview, Processor, Memory (type, speed, usage and every memory slot with its module size, speed, maker, part number and serial number, plus empty slots), Graphics (every GPU with video memory and driver, and every display with resolution and refresh rate), Storage (every drive with type, health and capacity, and volumes with used and free space), Battery (health and cycles) and Network. Each part has its own illustration (processor, memory stick, graphics card, hard disk and SSD, a battery showing your real charge, a monitor, a Wi-Fi router and a Bluetooth adapter). Left and Right arrows move between tabs; "Copy Details" copies a summary; "System Report…" and "Software Update…" open Windows' own tools.

### Changed
- PearUI Settings has the same frosted-glass look: a translucent window with the pages as a segmented bar along the top, Windows-style minimize and close buttons, and softer, see-through cards. Up and Down arrows scroll the pages (and the wallpaper grid).
- Drop-down lists are modern: a rounded field with a chevron and a rounded floating list with soft selection.
- The hot corners picture in Settings shows your own wallpaper, and the Wallpaper page no longer sits inside an extra box.
- The welcome tour is a frosted-glass window like Settings and About This PC, instead of a full-screen overlay, and the Skip button is gone (the close button, Esc and Get Started still finish it).
- PearUI Terminal has the same top bar: no system title bar, the tabs as a segmented bar in the middle (or just the title with one tab), the shell and history buttons at the left, and Windows-style minimize, maximize and close buttons at the right.

### Fixed
- The Mac-style right-click menu now works on files in File Explorer's Details view (the columns of a row were treated as a text box, so the normal Windows menu appeared instead).
- Settings, About This PC, the welcome tour and the Terminal are readable in the light theme: their frosted glass uses a slightly stronger light tint, so dark text no longer gets lost against a dark wallpaper while the window stays see-through.
- The `info` command is easier to read on a light terminal: deeper greens for the labels, pear and bars, and darker greys.
- The window buttons and the Terminal's toolbar icons are white on the dark theme and dark on the light theme.

## [1.5.3] - 2026-11-28

### Added
- **Quick Look.** Select a file in File Explorer or on the desktop and press `Space` to preview it without opening it. Shows images, text and code, PDFs (every page, scrollable), video and audio, and a large thumbnail for other files. Folders open inside the preview, so you can browse into subfolders and files with the arrow keys or the mouse and go back with `Left` or `Backspace`. `Space` or `Esc` closes it. Turn it off in Settings → General.
- **Mac-style right-click menu** in File Explorer and on the desktop: Open, Open With, Quick Look, Move to Trash, Get Info, Rename, Compress, Duplicate, Copy, Copy as Path, New Folder, New Text Document, Paste, Open in Terminal and more. "Show More Options" (or `Shift` + right-click) opens the full Windows menu, and anywhere it does not recognise (the sidebar, column headers, the address bar) still gets the normal Windows menu. Turn it off in Settings → General.
- **Shake to find the pointer.** Wiggle the mouse and the pointer grows to a large, sharp copy that follows it, then shrinks back smoothly. Turn it off in Settings → General.
- **Auto-hide menu bar** (Settings → Menu Bar, off by default). The bar slides away and comes back when the pointer touches the top edge. While it is on, windows and All Apps use the full screen height.
- **File Explorer options** in Settings → Desktop: hide Gallery and OneDrive from the sidebar, hide file extensions, no recent files or frequent folders, and open File Explorer to This PC. Each one can be switched back off.
- **Terminal commands** (PearUI Terminal, every shell): `wg` with short winget commands (`wg l`, `wg i`, `wg r`, `wg s`, `wg su`, `wg u`, `wg ua` and more; type `wg` to see them all), and `update` to run Windows Update quietly in the background (`update all` also installs optional updates such as preview updates and drivers). The full list is in Settings → Shortcuts.
- `Win` + `E` opens File Explorer, or switches to the one that is already open, instead of opening another window. It is an ordinary shortcut you can change in Settings → Shortcuts.
- Settings → About: a Privacy Policy section and link.

### Changed
- "Open in Terminal" starts in the folder you chose, and adds a new tab when the Terminal is already open.
- Clicking outside a Dock right-click menu now closes it (the click also reaches whatever you clicked).
- The Downloads stack opens straight above its icon instead of appearing elsewhere and jumping into place.
- The menu bar clock reserves its width, so the items beside it no longer shift as the seconds change.
- Media popup: the controls are centered and previous and next sit closer to play/pause, and the buttons respond anywhere on them (shuffle was removed).
- The Terminal's history button closes the panel again.
- New app icons.

### Fixed
- The menu bar stays on top after `Win` + `D` (Show desktop) instead of disappearing behind the wallpaper.
- All Apps and the Welcome window fill the screen when the menu bar is auto-hidden.
- Quick Look no longer raises an error when it closes.

## [1.5.2] - 2026-11-27

### Added
- Virtual desktop shortcuts: `Win` + `1` … `0` switch desktops and `Win` + `Shift` + `1` … `0` move the window in front to that desktop, creating desktops as needed.
- Window number shortcuts: `Alt` + `1` … `0` jump to the numbered windows of the current desktop, in Dock order.

### Removed
- Translucent app windows, and the settings for them.

## [1.5.1] - 2026-11-27

### Added
- Wallpaper picker (Settings → Wallpaper) with an online catalog of default wallpapers from macOS, Windows and Linux, previews saved on your PC and the full image downloaded only when you pick it.

## [1.5.0] - 2026-11-27

### Added
- Menu bar popups: a now-playing card with cover art, a seek bar and controls; a volume popup with a per-app mixer; and Wi-Fi and Bluetooth menus.
- Background apps: pinned apps that run without a window show as running in the Dock.

### Changed
- Stage Manager handles windows better and animates more smoothly.
- The Dock reacts faster and more smoothly when icons grow under the pointer.

## [1.4.0] - 2026-11-26

### Added
- Clipboard history: a menu bar icon and `Win` + `Shift` + `V` open a searchable list of what you copied. Pin entries, delete them one at a time, and clear the rest. History is kept until you clear it and is saved encrypted for your Windows account. Turn it off in Settings.

## [1.3.0] - 2026-11-26

### Added
- Settings → About: "More apps by Guru Swarupa" (Launch, VRTrack, LinuxTV and LinuxTV Remote), read from the repository so it can grow without a release.
- Settings → Support: a UPI QR code (drawn on your PC) and copyable UPI ID, plus ways to fund the project (read live from the repository's `.github/FUNDING.yml`), other ways to help, and all contributors with their pictures.
- Updates over the air: PearUI checks its GitHub releases a few times a day and offers to update. One click downloads the installer, checks it against its published checksum, installs it quietly and restarts PearUI on the new version, keeping your settings. Also in Settings → About and the pear menu (Check for Updates…).
- Custom keyboard shortcuts (Settings → Shortcuts): bind any key combination, including the Windows key, to tiling and moving windows, opening apps, running commands, run-or-raise, PearUI features, volume, media and brightness.
- `info` command in PearUI Terminal: a system summary beside the app icon.
- Hover previews: live thumbnails and titles of an app's windows above its Dock icon.
- Window picker when clicking a Dock icon of an app with several windows.
- `Win` + `T` hides the Terminal when it is already in front. The Terminal's own keys (tabs, copy, paste, zoom, history…) can be changed in Settings → Shortcuts.
- PearUI Terminal shells: starts with Bash (Linux-style), finds zsh, fish, sh, dash, ksh, Nushell, PowerShell and WSL, has a shell picker, and remembers the shell you choose or type.
- PearUI Terminal (`Win` + `T`): a translucent, frosted-glass terminal with tabs, Command Prompt and PowerShell, and a command history that is kept between sessions (Up arrow and a searchable panel).
- `Win` + `S` opens PearUI search.
- Performance page with optional services and privacy and bloat tweaks that can each be reversed.
- The `Win` key and Hot Corners stay quiet while a full-screen app (a game or a movie) is in front.

### Changed
- PearUI Terminal is readable in the light theme, and `info` uses deeper colors and a deeper-green pear on a light terminal.
- Smoother, quicker animations: they run at your screen's full refresh rate (120, 144, 240 Hz) instead of 60, All Apps opens and closes faster without skipping its first frames, page swipes follow your fingers and settle with a spring that keeps the speed of your flick, and the Dock's zoom feels the same at any frame rate.
- The version now comes from the release tag alone: the app, the installer and the GitHub release always show the same number.
- A sharper app icon, drawn at every size Windows asks for.
- Project renamed to PearUI throughout.

## [1.0.0] - 2026-11-25

First release: menu bar, Dock, search, All Apps with folders, Control Center, Stage Manager, Hot Corners, Mac-style Terminal with Linux commands, welcome tour, and an installer.
