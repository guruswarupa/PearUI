# PearUI Privacy Policy

**Last updated: 27 November 2026**

PearUI is a free, open-source desktop shell for Windows (a menu bar, Dock, search, All Apps, Control Center, Stage Manager, a terminal and keyboard shortcuts). It is made by Guru Swarupa and its source code is public at <https://github.com/guruswarupa/PearUI>.

**The short version:** PearUI has no account, no advertising, and no analytics or telemetry. It does not collect your personal information and does not sell or share it. Everything it remembers stays on your PC. The only network requests it makes are the ones listed under [Network requests](#network-requests), and each is needed for a feature you can see.

## What PearUI does on your PC

To do its job, PearUI uses some information that stays on your device. None of it is sent anywhere.

| What | Why | Where it stays |
|---|---|---|
| **Your settings** (theme, pinned Dock apps, shortcuts, folders in All Apps, and so on) | To remember how you set PearUI up | `%APPDATA%\PearUI\settings.json` |
| **Open windows and running programs** (their titles, names, icons and thumbnails) | To show the Dock, Stage Manager previews, Alt+number window switching and the background-apps list | Memory only (not saved) |
| **Clipboard history**: text you copy | The clipboard history feature (Win+Shift+V). Text marked by a password manager as "exclude from clipboard history" is never recorded. You can turn the feature off, delete single items, or clear all of it | `%APPDATA%\PearUI\clipboard.dat`, encrypted for your Windows user (DPAPI) |
| **Terminal command history**: commands you type in PearUI Terminal | To search your history between sessions | `%APPDATA%\PearUI\terminal-history.json` (plain text). It can be cleared from the terminal's history panel |
| **Keyboard shortcuts** | PearUI watches key presses to recognise shortcuts such as Win+1 or Alt+Space. It only checks whether a key combination is one of your shortcuts. It does **not** record, store or send what you type | Not saved |
| **Wi-Fi and Bluetooth status, battery, volume, brightness, now-playing media** | To show them in the menu bar and Control Center, and to let you change them | Read from Windows when needed, not saved |
| **Your macOSicons API key and picked icons** (only if you use that option) | To search the icon library and show the icons you chose | The key is in `%APPDATA%\PearUI\settings.json` (plain text, so keep that file private); picked icons are in `%APPDATA%\PearUI\macicons\`; WhiteSur icons are in `%APPDATA%\PearUI\whitesur\` |
| **Icon and wallpaper caches** | So icons and wallpapers load quickly | `%LOCALAPPDATA%\PearUI\` |
| **Error log** | To help fix crashes. It stays on your PC and is never sent automatically | `%APPDATA%\PearUI\error.log` |

PearUI does not use your camera, microphone, contacts, calendar or location.

## Network requests

PearUI contacts the internet only in these cases. It sends no account information, no device identifier and no list of your apps, windows or files.

| When | What is requested | From |
|---|---|---|
| **Checking for updates** (when you press "Check for Updates", and automatically if you leave "Check for updates" on) | The latest release information; if you choose to update, the installer file | GitHub (`api.github.com`, `github.com`) |
| **Opening Settings → Support or About** | The list of the developer's other apps, the project's funding links and its contributors | GitHub (`raw.githubusercontent.com`, `api.github.com`) |
| **Opening the wallpaper picker** (Settings → Wallpaper, or the optional step in the welcome tour) | The list of wallpapers, small preview images, and the full image of a wallpaper you pick | GitHub; previews are resized by the image service `wsrv.nl` |
| **Choosing the WhiteSur icon style** (Settings → Dock → App Icons, or Icon → WhiteSur Icon in All Apps) | The list of icon names in the WhiteSur theme, then the picture file of each icon that matches one of your apps. The matching is done on your PC. Your list of apps is not sent, but each picture file requested is named after an icon, so GitHub can see which of those icons were fetched | GitHub (`api.github.com`, `raw.githubusercontent.com`) |
| **Searching macOSicons** (only when you turn on "Pick real macOS icons by hand", add your own API key and press Search in the icon picker) | Your API key and the words you searched for (by default the app's name); then the pictures of the icons shown or chosen | macOSicons (`api.macosicons.com`, `s3-new.macosicons.com`) |
| **Searching the web from PearUI search** (only when you choose "Search the web") | Your search words are sent by your default web browser to Bing | Bing, through your browser |
| **Opening a link** such as a sponsor page or the project page | The page you opened | Your default web browser |

Like any web request, these reveal your IP address and a program name (`PearUI`) to the server that answers them. They are handled by those services under their own privacy policies:

- GitHub: <https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement>
- wsrv.nl (image resizing): <https://wsrv.nl>
- macOSicons: <https://macosicons.com>
- Microsoft (Bing): <https://privacy.microsoft.com/privacystatement>

## Permissions on your PC

PearUI runs as a normal program under your Windows account. It does not need administrator rights, except when you ask it to apply an optional "Windows debloat" or optional-services change in Settings → Performance, which shows the Windows permission prompt first. It can start with Windows if you turn that on, and can hide the Windows taskbar if you turn that on. Both can be switched off in Settings.

## Your choices and control

- **Turn things off:** Settings lets you turn off clipboard history, automatic update checks and the individual shortcuts.
- **Clear your data:** clear clipboard history from its popup, and terminal history from the terminal.
- **Remove everything:** uninstall PearUI from Windows Settings → Apps. To delete what it left behind, delete the folders `%APPDATA%\PearUI` and `%LOCALAPPDATA%\PearUI`.

## Children

PearUI is a general-purpose desktop tool and is not directed at children. It collects no personal information from anyone.

## Changes to this policy

If PearUI ever starts collecting or sending anything new, this policy will be updated first and the "Last updated" date above will change. Earlier versions are available in the history of this file on GitHub.

## Contact

Questions or concerns about privacy: open an issue at <https://github.com/guruswarupa/PearUI/issues> (the source code can also be checked there).
