# Changelog

All notable changes to PearUI are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- Settings → About: "More apps by Guru Swarupa" (Launch, VRTrack, LinuxTV and LinuxTV Remote), read from the repository so it can grow without a release.
- Settings → Support: a UPI QR code (drawn on your PC) and copyable UPI ID, plus ways to fund the project (read live from the repository's `.github/FUNDING.yml`, so they can change without a release), other ways to help, and all contributors with their pictures.
- Updates over the air: PearUI checks its GitHub releases a few times a day and offers to update. One click downloads the installer, checks it against its published checksum, installs it quietly and restarts PearUI on the new version, keeping your settings. Also in Settings → About and the pear menu (Check for Updates…).
- Custom keyboard shortcuts (Settings → Shortcuts): bind any key combination, including the Windows key, to tiling and moving windows, opening apps, running commands, run-or-raise, PearUI features, volume, media and brightness.
- `info` command in PearUI Terminal: a system summary beside the app icon.
- Hover previews: live thumbnails and titles of an app's windows above its Dock icon.
- Window picker when clicking a Dock icon of an app with several windows.
- `Win` + `T` now hides the Terminal when it is already in front. The terminal's own keys (tabs, copy, paste, zoom, history…) can be changed in Settings → Shortcuts.
- PearUI Terminal shells: starts with Bash (Linux-style), finds zsh, fish, sh, dash, ksh, Nushell, PowerShell and WSL, has a shell picker, and remembers the shell you choose or type.
- PearUI Terminal (`Win` + `T`): a translucent, frosted-glass terminal with tabs, Command Prompt and PowerShell, and a command history that is kept between sessions (Up arrow and a searchable panel).
- `Win` + `S` opens PearUI search.
- Translucent app windows, with adjustable opacity (full-screen apps stay solid).
- Performance page with optional services and privacy and bloat tweaks that can each be reversed.

### Changed
- PearUI Terminal is readable in the light theme: it now uses a plain see-through window there (Windows' glass made dark text look pale), and `info` uses deeper colors and a deeper-green pear on a light terminal.
- Smoother, quicker animations: they run at your screen's full refresh rate (120, 144, 240 Hz) instead of 60, All Apps opens and closes faster without skipping its first frames, page swipes follow your fingers smoothly and settle with a spring that keeps the speed of your flick, and the Dock's zoom feels the same at any frame rate.
- The version now comes from the release tag alone: the app, the installer and the GitHub release always show the same number.
- A sharper app icon, drawn at every size Windows asks for.
- Project renamed to PearUI throughout.

## [1.0.0]

First release: menu bar, Dock, search, All Apps with folders, Control Center, Stage Manager, Hot Corners, Mac-style Terminal with Linux commands, welcome tour, and an installer.
