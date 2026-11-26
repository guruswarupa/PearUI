# Contributing to PearUI

Thanks for wanting to help. Bug reports, ideas, docs fixes and code are all welcome, and you don't need to ask first for small changes.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md). Contributions are released under the project's [MIT License](LICENSE).

## Ways to help

- **Report a bug:** open an [issue](https://github.com/guruswarupa/PearUI/issues/new/choose) with your Windows version, what you did and what you expected. A screenshot helps a lot.
- **Suggest a feature:** describe the problem you want solved, not just the solution.
- **Pick up work:** issues labelled `good first issue` or `help wanted` are a good place to start. Comment on one so nobody doubles up.
- **Improve the docs, screenshots or translations.**

For anything big (a new feature, a large refactor), please open an issue to talk it through before you write the code. It saves everyone time.

## Build and run

You need Windows 10 or 11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/guruswarupa/PearUI.git
cd PearUI
dotnet build src\PearUI\PearUI.csproj
```

Run the build output directly:

```powershell
src\PearUI\bin\Debug\net8.0-windows10.0.22621.0\PearUI.exe           # start (start it again to open Settings)
src\PearUI\bin\Debug\net8.0-windows10.0.22621.0\PearUI.exe --quit    # stop the running copy cleanly
```

Only one copy of PearUI runs at a time, so quit an installed copy first. Closing PearUI (tray icon → Quit, or `--quit`) brings back the Windows taskbar. If a debug session crashes, run `PearUI.exe --restore-taskbar`.

To install your build like a user would (start-up task, Terminal and Linux-command setup):

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1              # build, install for this user, launch
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall   # remove it again
```

## Project layout

```
installer/          PearUI.iss (Inno Setup), configure.ps1 (Terminal, Linux commands, first-run setup, uninstall), build-installer.ps1
.github/            issue and pull request templates, release workflow
docs/               icon and screenshots used by the README
src/PearUI/
  App.xaml(.cs)     start-up, single instance, tray icon, hotkeys, live settings, clean shutdown
  Core/             settings, window tracking, app catalog, system status, start-up task, taskbar, Terminal setup, performance tweaks
  Native/           Win32 interop: AppBar, blur, shell icons, Core Audio, hotkeys, file operations
  Shell/            glass window base, glass menus, Wi-Fi and Bluetooth menus, hot corners, Windows-key handling
  Terminal/         PearUI Terminal: ConPTY shell host, VT emulator, renderer, tabs, saved history
  MenuBar/ Dock/ Launcher/ ControlCenter/ AllApps/ StageManager/ Onboarding/ Settings/
  Themes/           light/dark/accent theme and shared control styles
```

## Making a change

1. Fork the repo and create a branch from `main`: `git checkout -b fix/dock-preview-position`.
2. Make your change. Keep it focused: one fix or feature per pull request.
3. Build with no new warnings and try the feature yourself: light and dark theme, and a second monitor if it touches layout.
4. Commit with a clear message, in the present tense: `Fix Dock preview position on scaled displays`.
5. Open a pull request and fill in the template. Add screenshots for anything visual.

### Code style

- C# 12 / .NET 8, nullable-friendly, 4-space indents, file-scoped namespaces.
- Match the style of the file you're editing. Keep methods short and names descriptive.
- Comments explain *why*, not *what*. Leave out commented-out code and debug logging.
- Anything that touches the user's system (registry, services, files outside PearUI's folders) must be opt-in and reversible, and must be restored on uninstall or quit.
- Don't add dependencies without discussing them first. PearUI is meant to stay small.
- Win32 calls belong in `Native/`. Windows-key, hooks and window-management code should never block the UI thread.

### Things to be careful with

- **UI thread:** WPF windows must be touched on the dispatcher thread. Native callbacks (hooks, event hooks) arrive on other threads.
- **DPI:** the Dock, menu bar and pop-ups work in device pixels at the window edge and DIPs inside. Test at 100% and 150%.
- **Full-screen apps:** PearUI must stay out of the way of games and video. Respect `FullScreenGuard`.
- **Elevated windows:** a normal process can't hook keys or drop files into administrator windows. That's a Windows limit, not a bug.

## Publishing a release (maintainers)

1. Move the `[Unreleased]` notes in `CHANGELOG.md` under the new version.
2. Push a tag: `git tag v1.3.0 && git push origin v1.3.0`.
3. The **Release** workflow (`.github/workflows/release.yml`) builds `PearUI-Setup-1.3.0.exe` and attaches it to a new GitHub release.

The tag is the only place the version number is written. The app (Settings → About, and the file's properties in Windows), the installer and the GitHub release all take it from there, and the build stops if the number inside `PearUI.exe` doesn't match. Local builds use the newest tag in your clone, so run `git fetch --tags` first.

To build the installer locally, install Inno Setup (`winget install JRSoftware.InnoSetup`) and run:

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1                  # uses the newest git tag, e.g. dist\PearUI-Setup-1.3.0.exe
```

The installer is self-contained (no .NET needed on the user's PC) and pre-compiled for a quick start. To code-sign a release, sign `dist\publish\PearUI.exe` and the installer with `signtool`.

## Funding links and other apps

The funding links in Settings → Support, and GitHub's Sponsor button, both come from `.github/FUNDING.yml`. The "More apps" list in Settings → About comes from `.github/apps.json`. Edit either file to change what the app shows; it picks the change up within hours, with no new release.

## Questions

Open a [discussion or issue](https://github.com/guruswarupa/PearUI/issues). No question is too small.
