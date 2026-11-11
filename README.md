# AI Usage Bar

**English** · [Português](README.pt-BR.md) · **[Website & downloads](https://matheusvelloso.github.io/ai-usage-bar/)**

> **Independent project.** Not affiliated with, sponsored or endorsed by Anthropic, OpenAI, GitHub/Microsoft, Google, xAI/SpaceXAI or Anysphere (Cursor). Claude, Codex, GitHub Copilot, Gemini, Grok and Grok Bot are trademarks of their respective owners. The app relies on internal, undocumented endpoints of these services, which may change or stop working at any time. Use at your own risk.

A meter for your AI usage limits that lives in the macOS menu bar, the Windows tray or the Linux tray. Each AI shows up as its own official icon (Claude's star, Codex's blossom, Copilot's mascot…) inside a ring that fills with usage; one click opens a popover with the details for each service. With two or more AIs connected, the popover has **one tab per agent**, each showing its fullest limit (or the one you pick) so you can see them all at a glance. Prefer them all on one screen? ⚙ → **Popup** → **All AIs together**.

**Claude**
- **Current session** (5-hour window): how much is used and when it resets
- **Weekly, all models**
- **Weekly per model** (e.g. Fable), when your plan has a separate limit for it
- **Where this week's usage went**: Claude Code, Chat, Cowork…

**Codex** (OpenAI)
- **Current session** (5-hour window) and **Weekly**: how much is used and when each resets

**GitHub Copilot**
- **Premium requests** for the month: how much is used and when the allowance resets
- Chat and code completions on plans where they are limited (e.g. Copilot Free)

**Gemini** (Gemini CLI)
- **Daily** quota per model (2.5 Pro first, then Flash and Flash-Lite): how much is used and when it resets

**Grok Bot**
- **Weekly**: how much is used and when it resets
- Banked resets and on-demand usage, when present

The numbers are the same ones shown in claude.ai → Settings → Usage, in Claude Code's `/usage`, on ChatGPT's Codex usage page, in GitHub's Copilot settings, in the quotas the Gemini CLI reports and in Grok Bot's own Usage screen. Codex, Copilot, Gemini and Grok Bot only appear if they are signed in on this computer.

### Connecting your agents

The **＋** tab (or ⚙ → **Set up agents…**) lists every supported agent: ✓ connected, or a hint on how to sign in (for Copilot: sign in to GitHub Copilot in VS Code, or run `gh auth login`; for Codex: `codex login`; for Gemini: run `gemini` and sign in with Google). It opens by itself on the first launch. The app never asks you to log in: it reuses the logins your tools already have, so signing in elsewhere is enough and the tab appears within a few minutes. An agent shows up on its own once its usage loads. A login that returns no usage stays unticked, marked "Login found, but no usage data": for example the `gh` CLI signed in on an account without Copilot. Tick **Show** to track it anyway, with its warning, or untick it to hide an agent you do not want to track.

<p align="center">
  <img src="docs/screenshot-light.png" width="320" alt="Popover, light theme">
  <img src="docs/screenshot-dark.png" width="320" alt="Popover, dark theme">
</p>

### Screenshots (Windows)

One tab per agent, each with the agent's official icon and its fullest limit (or the one you pick); the **＋** tab lists every supported agent and how to connect the ones that are not signed in yet.

<p align="center">
  <img src="docs/screenshot-windows.png" width="300" alt="Windows popup on the Claude tab, with tabs for Claude, Codex and Copilot">
  <img src="docs/screenshot-windows-codex.png" width="300" alt="Windows popup on the Codex tab: current session and weekly limits">
  <img src="docs/screenshot-windows-dark.png" width="300" alt="Windows popup on the Copilot tab, dark theme">
</p>
<p align="center">
  <img src="docs/screenshot-windows-agents.png" width="300" alt="The Agents tab: Claude, Codex and Copilot connected, Gemini and Grok Bot with sign-in hints">
</p>

The optional [taskbar widget](#taskbar-widget-windows-optional), on a Windows 11 taskbar:

<p align="center"><img src="docs/screenshot-windows-widget.png" width="330" alt="The taskbar widget: Claude, Codex and Copilot icons in progress rings with their percentages"></p>

*The Windows screenshots are made from sample data with the real app (the `--snapshot` option, and a capture of the taskbar for the widget). They show the agents whose icons were installed on the machine that took them; an agent without an installed icon source shows its initial instead. The macOS images above predate Codex, Copilot and Gemini.*

## Menu bar

Under ⚙︎ in the popover:

**Menu bar style** (applies to every AI)
- **AI icon with ring** (default): each AI's icon inside a ring that fills with usage.
- **Percentage only**: no icon, just a small pie gauge and the number, like the very first version.

**What each AI shows** (⚙ → the AI → **In the menu bar**)

| Service | Options |
|---|---|
| Claude | **Current session only** · **Weekly only** · **Session + weekly** (default: session on top, weekly below) · **Hide** |
| Codex | **Current session only** · **Weekly only** · **Session + weekly** (default) · **Hide** |
| GitHub Copilot | **Monthly** (default) · **Hide** |
| Gemini | **Daily** (default, the Pro model's quota) · **Hide** |
| Grok Bot | **Weekly** (default) · **Hide** |

Everything is drawn in the menu bar's own color, like other menu bar icons: white on a dark menu bar, black on a light one. The ring (or pie) follows the first number (with "Session + weekly" it shows the session) and is the only element that changes color: orange when usage reaches a warning level and red when it is critical.

**Popup** (⚙ → **Popup**): **One tab per agent** (default) or **All AIs together**, every agent one below the other on a single screen.

**In its tab** (⚙ → the AI → **In its tab**, or right-click the tab): the number on each agent's tab. **Highest usage** (default) shows whichever limit is fullest. You can pin any one of that AI's limits instead: Claude's weekly limit for all models or for one model, Codex's session, Copilot's monthly requests, a Gemini model's daily quota.

**Alert colors in menu bar** (on by default): turn it off to keep the ring or pie in the menu bar color, without orange or red. The popover keeps its colors.

**Language**: English or Português. Applies to the popover, the gear menu, messages and times (AM/PM in English, 24-hour in Portuguese). A fresh install starts in the macOS language.

The icons are the agents' own official artwork, cut out at runtime from what is installed on the computer: the meter keeps only the artwork and drops the colored background, and draws it in the menu bar's or taskbar's color. Claude and Grok Bot come from their apps (on Windows also from Microsoft Store installs; on Linux, where there is no official Claude app, Claude's star comes from the Claude Code extension); GitHub Copilot, Codex and Gemini live in editors, so theirs come from their official editor extensions (`github.copilot-chat`, OpenAI's `openai.chatgpt` Codex extension, Google's `google.geminicodeassist`; also found in VS Code Insiders and Cursor). No brand artwork ships with this repository. If an icon cannot be found, the service's initial is shown instead.

<p align="center">
  <img src="docs/menubar.png" width="820" alt="Menu bar combinations in light and dark themes">
</p>

## Platforms

| System | Status | Where |
|---|---|---|
| macOS 13+ (Apple Silicon and Intel) | ✅ Native app (Swift/SwiftUI), `.dmg` installer | [`macos/`](macos/) |
| Windows 10/11 (x64) | 🧪 New: native app (C#/WPF), installer and portable `.exe`. Built and tested by CI, not yet on a real PC | [`windows/`](windows/) |
| Linux (x64) | 🧪 New: native app (C#/Avalonia), AppImage and portable `.tar.gz`. Built and tested by CI and on a virtual X11 desktop, not yet on a real one | [`linux/`](linux/) |

Each system gets its own native app and installer. Everything a new port needs is in [`docs/usage-api.md`](docs/usage-api.md): where each login comes from, which endpoints to call and how to read the responses. Use [`docs/sample-usage.json`](docs/sample-usage.json), [`docs/sample-codex-usage.json`](docs/sample-codex-usage.json), [`docs/sample-copilot-usage.json`](docs/sample-copilot-usage.json), [`docs/sample-gemini-usage.json`](docs/sample-gemini-usage.json) and [`docs/sample-grok-usage.json`](docs/sample-grok-usage.json) as test data.

## Download

The [website](https://matheusvelloso.github.io/ai-usage-bar/) always links to the latest release:

| System | File |
|---|---|
| macOS | [`AIUsageBar-macOS.dmg`](https://github.com/matheusvelloso/ai-usage-bar/releases/latest/download/AIUsageBar-macOS.dmg) |
| Windows (installer) | [`AIUsageBar-Windows-Setup.exe`](https://github.com/matheusvelloso/ai-usage-bar/releases/latest/download/AIUsageBar-Windows-Setup.exe) |
| Windows (portable) | [`AIUsageBar-Windows-Portable.exe`](https://github.com/matheusvelloso/ai-usage-bar/releases/latest/download/AIUsageBar-Windows-Portable.exe) |
| Linux (AppImage) | [`AIUsageBar-Linux-x86_64.AppImage`](https://github.com/matheusvelloso/ai-usage-bar/releases/latest/download/AIUsageBar-Linux-x86_64.AppImage) |
| Linux (portable) | [`AIUsageBar-Linux-x64.tar.gz`](https://github.com/matheusvelloso/ai-usage-bar/releases/latest/download/AIUsageBar-Linux-x64.tar.gz) |

## Updates

From 0.8.0 on, the app looks for a new release on GitHub when it starts and every 6 hours. When there is one, the popup shows **New version … → Update**: one click downloads it, installs it over the current copy and reopens the meter. To look right away: ⚙ → **Check for updates**.

- **macOS:** the app replaces itself where it is (usually Applications). If your user cannot write to that folder, the button opens the download page instead. If you use Grok Bot, macOS asks for "Always Allow" again after an update (see [Limitations](#limitations)).
- **Windows:** an installed copy runs the new Setup silently and keeps your "Start with Windows" choice; the portable .exe replaces itself in its folder.
- **Linux:** the AppImage replaces itself, and so does the portable executable; then the meter reopens. If your user cannot write to their folder, the button opens the download page instead.
- The check calls only GitHub's public releases API (`api.github.com/repos/matheusvelloso/ai-usage-bar/releases/latest`) and downloads only files from this repository's releases; nothing about you or your usage is sent. Versions before 0.8.0 do not have it, so update those once by hand.

## Install on a Mac

1. Open `AIUsageBar-<version>.dmg` and drag **AI Usage Bar** into **Applications**.
2. The first time, right-click the app → **Open**. The app is not signed with an Apple certificate yet, so macOS asks for this confirmation once.
3. If you use Grok Bot, macOS asks whether the app may access **"Grok Bot Safe Storage"** in the Keychain. Choose **Always Allow** (see [How it works](#how-it-works)).
4. To start with your Mac: ⚙︎ in the popover → **Open at login**.

**Requirements:** Claude Code signed in on this Mac (through the Claude app or the `claude` command). For Codex, the Codex CLI or extension signed in with ChatGPT (`codex login`). For GitHub Copilot, a Copilot plan and a signed-in editor plugin (VS Code, JetBrains) or the `gh` CLI. For Gemini, the Gemini CLI signed in with Google (`gemini`). For Grok Bot, the Grok Bot app installed and signed in.

## Install on Windows

1. Run `AIUsageBar-Windows-Setup.exe` (installs for your user only, no admin rights). If SmartScreen appears, click **More info** → **Run anyway**: the app is not signed with a Microsoft certificate yet.
2. Each AI gets its own **tray icon**, next to the clock. Windows may put new icons in the **^** overflow area; drag them onto the taskbar to keep them visible.
3. Left-click an icon for the popup, right-click for refresh and quit. Options (style, tabs or all AIs together, what each AI shows in the tray and in its tab, alert colors, language, start with Windows) are under ⚙ in the popup; right-click a tab to pick its number.

<p align="center"><img src="docs/screenshot-windows.png" width="320" alt="Popup on Windows"></p>

Windows tray icons are small squares, so each one shows a single value (session *or* weekly) around the AI's icon or as a number; the popup shows everything. Prefer not to install? `AIUsageBar-Windows-Portable.exe` runs as is.

## Install on Linux

1. Download `AIUsageBar-Linux-x86_64.AppImage`, make it executable (`chmod +x AIUsageBar-Linux-x86_64.AppImage`, or Properties → Allow executing) and run it. The first run adds **AI Usage Bar** to your app menu.
2. Each AI gets its own **tray icon**, like on Windows. Click one for the popup; its menu has open, refresh and quit. Options are under ⚙ in the popup, including **Start at login** and **Tray icon color** (for panels whose color the app guesses wrong).
3. **GNOME** shows tray icons only with the [AppIndicator and KStatusNotifierItem Support](https://extensions.gnome.org/extension/615/appindicator-support/) extension (Ubuntu ships it on). Without a tray, the popup opens at startup with a hint, and opening **AI Usage Bar** from the app menu again brings it back. KDE Plasma, Cinnamon, Xfce, Budgie, MATE and LXQt show tray icons as they are.

<p align="center"><img src="docs/screenshot-linux.png" width="320" alt="Popup on Linux, dark theme"></p>

The AppImage needs FUSE, which desktop distributions have (on Ubuntu 22.04 and later the static AppImage runtime needs no `libfuse2`). No FUSE, or you prefer a plain folder? `AIUsageBar-Linux-x64.tar.gz` holds one self-contained executable: extract it anywhere and run `./AIUsageBar`. Both follow the desktop's light/dark setting and accent color, and use X11 (XWayland in a Wayland session). On X11 the popup opens next to the icon you clicked; on Wayland, which does not tell apps where the pointer is, it opens in the corner of the panel (top right on GNOME, bottom right elsewhere).

Starting the app adds `~/.local/share/applications/ai-usage-bar.desktop` and its icon (and keeps them pointing at the file if you move it); **Start at login** writes `~/.config/autostart/ai-usage-bar.desktop`. Settings live in `~/.config/AIUsageBar`, the last usage responses in `~/.cache/AIUsageBar`. Remove those to uninstall.

### Taskbar widget (Windows, optional)

For a summary you can see without opening anything, turn on **⚙ → Taskbar widget → Show on the taskbar**. A small pill appears on the taskbar with one chip per agent: its official icon inside a progress ring that follows usage, plus the percentage, coloured orange or red when a limit is close.

- **Hover** a chip for a card with that agent's plan, every limit and when it resets. **Click** opens the full popup on that agent's tab, **drag** moves it, **right-click** has refresh, position, style and hide.
- **Position:** *left of the system tray* (default) or *left edge*, which uses the first free gap from the left (after Widgets on a centred taskbar, so it does not sit on Start or your apps). Drag to fine-tune; "Reset position" undoes it.
- **Style:** icon with ring and percentage, or just the ringed icon.
- It is independent of the tray icons: you can hide those and keep the widget.

Windows 11 has no supported way to add content to the taskbar, so the widget is a small borderless window kept above it. It hides by itself for full-screen apps, games and presentations and while an auto-hidden taskbar is hidden, follows the taskbar across DPI and monitor changes (the primary taskbar only), and is not shown on a vertical taskbar. If you click the taskbar itself, the widget can disappear behind it for up to a second before it comes back.

## Build

### macOS

Requires Xcode (or the Command Line Tools) with Swift 5.10 or newer.

```bash
cd macos
make test      # data layer tests
make build     # creates dist/AI Usage Bar.app and dist/AIUsageBar-<version>.dmg
make install   # copies the app to /Applications and opens it
```

The .dmg opens on a window with a background that shows where to drag the app. [dmgbuild](https://dmgbuild.readthedocs.io) lays it out: `make build` installs it in a private virtualenv the first time, which needs Python 3.10 or newer and network access. Without them you get a plain .dmg with the same contents. The background is `macos/Support/dmg-background.tiff`; to change it, edit `scripts/make-dmg-background.swift` and run `scripts/make-dmg-background.sh`.

The version comes from `macos/VERSION`. To sign with a Developer ID certificate, run `SIGN_IDENTITY="Developer ID Application: …" make build`.

### Windows

Requires the .NET 10 SDK. The app only runs on Windows, but everything builds (and the core tests run) on macOS and Linux too.

```bash
cd windows
dotnet test tests/UsageCore.Tests     # core tests (any OS)
dotnet run --project tools/Preview -- tray.png --claude-fixture ../docs/sample-usage.json --grok-fixture ../docs/sample-grok-usage.json
dotnet publish src/AIUsageBar -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64
```

The installer is made with [Inno Setup](https://jrsoftware.org/isinfo.php) from `windows/installer/AIUsageBar.iss` (see `.github/workflows/windows.yml`).

### Linux

Requires the .NET 10 SDK. The Linux app ([Avalonia](https://avaloniaui.net)) shares the Windows app's core (`windows/src/UsageCore`): providers, logins, tab logic and tray icon drawing are the same code.

```bash
cd linux
dotnet run --project src/AIUsageBar                    # run from source
./scripts/build.sh                                      # dist/AIUsageBar-Linux-x86_64.AppImage and dist/AIUsageBar-Linux-x64.tar.gz
./scripts/screens.sh publish/single/AIUsageBar screens  # the popup in every state, from the sample data
```

`build.sh` downloads [appimagetool](https://github.com/AppImage/appimagetool) the first time (set `APPIMAGETOOL` to use your own copy). The version comes from `linux/VERSION`.

## How it works

Each service is a provider (`UsageProvider`) that reads the login of an app that is already installed and calls the same endpoint that app uses for its own usage screen. It refreshes every 3 minutes, when the popover opens and when the Mac wakes up. The last response is cached so something shows up as soon as the app starts.

| Service | Login reused | Endpoint |
|---|---|---|
| Claude | Keychain item `Claude Code-credentials`, read through `/usr/bin/security` | `GET api.anthropic.com/api/oauth/usage` |
| Codex | `~/.codex/auth.json` (or `$CODEX_HOME/auth.json`): the ChatGPT access token and account id the Codex CLI and extension keep | `GET chatgpt.com/backend-api/wham/usage` |
| GitHub Copilot | The token the Copilot editor plugin keeps in `~/.config/github-copilot/apps.json` (or `hosts.json`), then `GH_TOKEN` / `GITHUB_TOKEN`, then `gh auth token` | `GET api.github.com/copilot_internal/user` |
| Gemini | `~/.gemini/oauth_creds.json`: the Google access token the Gemini CLI keeps | `POST cloudcode-pa.googleapis.com/v1internal:loadCodeAssist`, then `:retrieveUserQuota` |
| Grok Bot | `~/Library/Application Support/Grok Bot/sand-secrets.json`, encrypted by Electron with the key in the **Grok Bot Safe Storage** Keychain item | `POST api2.cursor.sh/aiserver.v1.DashboardService/GetSandUsageStatus` |

On Windows the same endpoints are used; Claude Code's login is read from `%USERPROFILE%\.claude\.credentials.json`, Codex's from `%USERPROFILE%\.codex\auth.json`, Gemini's from `%USERPROFILE%\.gemini\oauth_creds.json`, Copilot's from `%LOCALAPPDATA%\github-copilot\apps.json` and Grok Bot's from `%APPDATA%\Grok Bot\sand-secrets.json`, whose key Electron protects with DPAPI in `Local State`. These Windows locations follow how the apps work elsewhere but have not been checked on a real PC yet.

On Linux the logins are where the tools keep them there: `~/.claude/.credentials.json` (or `$CLAUDE_CONFIG_DIR`), `~/.codex/auth.json`, `~/.gemini/oauth_creds.json`, `~/.config/github-copilot/apps.json` (then `GH_TOKEN` / `GITHUB_TOKEN`, then `gh auth token`) and `~/.config/Grok Bot/sand-secrets.json`. On Linux, Electron encrypts that last one either with a fixed key (no keyring) or with a password kept in the desktop keyring, which the app reads with `secret-tool` (package `libsecret-tools`). The icons come from the Claude Code extension (Claude's star), the Copilot, Codex and Gemini extensions (`~/.vscode`, `~/.vscode-insiders`, `~/.cursor`, `~/.vscode-oss`) and the icon theme (Grok Bot, or a Claude desktop package).

Tokens are **read only**, in memory: the app never writes, refreshes or sends a token anywhere other than the service it belongs to. The cache holds usage responses only.

## Limitations

- **The endpoints are not public.** They are the ones Claude Code and Grok Bot use themselves and may change without notice. The app ignores unknown fields; if the format changes completely, it says it needs an update.
- **The Codex and Gemini endpoints are unofficial and not yet verified against a real account.** Their fields are known from third-party tools, not from OpenAI's or Google's documentation, and neither login existed on the machine where they were written, so they have only been tested against made-up samples. Codex and the Gemini CLI renew their own tokens when they run; this app never does, so if one sits idle until its token expires the meter asks you to open it once. The Gemini CLI also supports API-key and Vertex sign-ins, which have no quota to read here.
- **Codex, Copilot and Gemini on macOS are new and have not run on a Mac with real logins yet.** They were written to mirror the Windows versions and their tests.
- **The Copilot endpoint is unofficial and not yet verified against a real account.** `copilot_internal/user` is what GitHub's own editor plugins call; its fields are known from third-party tools, not from GitHub's documentation. Not every kind of GitHub token is accepted by it (the editor plugin's token is the safest). Organization-managed seats may report no premium allowance.
- **Logins expire.** Claude Code and Grok Bot refresh their own tokens when used. If one of them sits idle for hours, the meter shows the last value and asks you to open that app once.
- **Grok Bot and the Keychain:** macOS grants the Grok Bot key to *this build* of the app. Because the app is only signed locally (ad-hoc), each new version, including one installed by the in-app update, asks for "Always Allow" again. A Developer ID certificate removes that.
- **Not signed by Apple or Microsoft yet**, hence the right-click → Open step on macOS and the SmartScreen prompt on Windows.
- **Windows is new.** CI builds it, runs its tests and renders its popup from sample data, but it has not run on a real PC with real logins yet. Reports are welcome.
- **Linux is newer still.** CI builds it, runs the core tests on Linux and renders its popup; it was also run on a virtual X11 desktop against a stand-in tray (icons, menus, clicks, popup placement, single instance, first run without a tray). It has not run on a real GNOME or KDE desktop with real logins yet. Grok Bot on Linux is written from how Electron stores secrets there, without a Linux Grok Bot to try it on. Reports are welcome.

## Debugging

macOS:

```bash
APP="/Applications/AI Usage Bar.app/Contents/MacOS/AIUsageBar"
"$APP" --dump                 # prints current usage as text
"$APP" --dump --raw           # … plus the raw responses (no tokens)
"$APP" --snapshot pop.png     # renders the popover, and every menu bar combination to pop-menubar.png
"$APP" --snapshot pop.png --fixture docs/sample-usage.json --codex-fixture docs/sample-codex-usage.json --copilot-fixture docs/sample-copilot-usage.json --gemini-fixture docs/sample-gemini-usage.json --grok-fixture docs/sample-grok-usage.json --dark --lang en
"$APP" --snapshot pop.png --fixture docs/sample-usage.json --copilot-fixture docs/sample-copilot-usage.json --tab copilot   # a given tab
"$APP" --snapshot pop.png --fixture docs/sample-usage.json --first-run                                                   # the first-run Agents tab
"$APP" --snapshot pop.png --fixture docs/sample-usage.json --update-available 9.9.9                                      # the "New version" banner
"$APP" --snapshot pop.png --fixture docs/sample-usage.json --grok-fixture docs/sample-grok-usage.json --layout list        # all AIs together; --tab-limit claude=weekly_all picks a tab's number
"$APP" --install-update --no-relaunch   # the in-app update from the command line: installs a newer release over this copy
```

Windows (it is a GUI app, so use `Start-Process -Wait` to see the output):

```powershell
Start-Process AIUsageBar.exe -ArgumentList '--dump' -Wait -NoNewWindow
Start-Process AIUsageBar.exe -ArgumentList '--snapshot','pop.png','--fixture','docs\sample-usage.json' -Wait   # also writes pop-tray.png
# add '--copilot-fixture','docs\sample-copilot-usage.json', '--tab','copilot', '--first-run' or '--update-available','9.9.9' to render other states
Start-Process AIUsageBar.exe -ArgumentList '--install-update','--no-relaunch' -Wait   # the in-app update from the command line (CI adds '--as-version','0.0.1')
Start-Process AIUsageBar.exe -ArgumentList '--widget-demo','--fixture','docs\sample-usage.json','--copilot-fixture','docs\sample-copilot-usage.json'   # real tray + taskbar widget on sample data (no logins, no network); add --left-edge, --ring-only, --dark
```

Linux (a terminal shows the output directly):

```bash
./AIUsageBar --dump                 # prints current usage as text (--raw adds the raw responses, no tokens)
./AIUsageBar --snapshot pop.png --fixture docs/sample-usage.json --dark   # renders the popup without a display; also writes pop-tray.png
./AIUsageBar --demo --fixture docs/sample-usage.json --copilot-fixture docs/sample-copilot-usage.json   # real tray + popup on sample data (no logins, no network)
./AIUsageBar --install-update --no-relaunch   # the in-app update from the command line (CI adds --as-version 0.0.1)
```

The AppImage takes the same options; add `--appimage-extract-and-run` before them on a machine without FUSE.

## Layout

```
docs/
  usage-api.md            data contract for every port
  sample-usage.json       sample Claude response (made-up numbers)
  sample-codex-usage.json    sample Codex response (made-up numbers; field names from third-party tools)
  sample-copilot-usage.json  sample Copilot response (made-up numbers; field names from third-party tools)
  sample-gemini-usage.json   sample Gemini quota response (made-up numbers; field names from third-party tools)
  screenshot-windows*.png    Windows popup, Agents tab and taskbar widget
  screenshot-linux.png       Linux popup
  sample-grok-usage.json  sample Grok Bot response (made-up numbers)
macos/
  Sources/UsageCore/      providers (Claude, Codex, Copilot, Gemini, Grok Bot), logins, HTTP clients, tab logic, EN/PT strings (tested)
  Sources/AIUsageBar/     menu bar item (AppKit) + popover (SwiftUI)
  Tests/                  data layer tests
  Support/                Info.plist and app icon
  scripts/                .app/.dmg build and icon generation
windows/
  src/UsageCore/          providers, logins (DPAPI), tray icon drawing; cross-platform (tested)
  src/AIUsageBar/         tray icons (WinForms NotifyIcon) + popup (WPF)
  tests/                  core tests
  tools/Preview/          renders every tray icon combination to a PNG, on any OS
  installer/              Inno Setup script
linux/
  src/AIUsageBar/         tray icons (StatusNotifierItem) + popup (Avalonia); uses windows/src/UsageCore
  packaging/              AppImage entry point, .desktop file, portable README
  scripts/                build (AppImage + .tar.gz) and screenshots from the sample data
docs/index.html           website (GitHub Pages) with download links
.github/workflows/        macOS, Windows and Linux builds, tests and releases
```

## License

[MIT](LICENSE).
