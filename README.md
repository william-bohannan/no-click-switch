# No Click Switch (NCS)

A minimal always-on-top Windows top bar that shows **one tab per open window**.

| | |
|---|---|
| **Short name** | **NCS** |
| **Repository** | https://github.com/william-bohannan/no-click-switch |
| **Website** | https://noclickswitch.com *(coming soon)* |
| **Install path** | `%LocalAppData%\NoClickSwitch` |

## Install

No admin rights. Installs to `%LocalAppData%\NoClickSwitch`, enables auto-start for the current user, and launches the app. Self-contained — no .NET install required. Reinstalls keep your `settings.json`.

### PowerShell (recommended)

Press **Start**, type `powershell`, and click the **Windows PowerShell** app — then paste:

```powershell
irm https://raw.githubusercontent.com/william-bohannan/no-click-switch/main/install.ps1 | iex
```

### Uninstall

```powershell
irm https://raw.githubusercontent.com/william-bohannan/no-click-switch/main/uninstall.ps1 | iex
```

Or use **Uninstall** from the app’s ☰ menu.

### Other options

**Command Prompt** (no PowerShell):

```cmd
curl.exe -L -o "%TEMP%\ncs-install.cmd" https://raw.githubusercontent.com/william-bohannan/no-click-switch/main/install.cmd && "%TEMP%\ncs-install.cmd"
```

**Manual:** download **NoClickSwitch-win-x64.zip** from [Releases](https://github.com/william-bohannan/no-click-switch/releases/latest), extract to `%LocalAppData%\NoClickSwitch`, run `NoClickSwitch.exe`, then **Install** from the ☰ menu.

If no release zip is available, the PowerShell installer falls back to building from source when the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) is present.

## Behaviour

- Full-width bar pinned to the **top of the primary screen**
- **Always on top**
- **One tab per open window** (title + icon)
- **Hover** a tab to bring that window to the front (blue highlight) — switch without a click
- **Click** a tab to focus and fill the free space below the bar
- **Right-click** a tab → **Close window**
- **Active window** tab uses a quiet grey highlight
- **Drag tabs** to reorder; order is **kept** when the list refreshes
- Each tab is **5em wide × 2em tall** (80 × 32 DIP at 16px em)
- Tabs **wrap** when they exceed the screen width; the bar **grows in height** (no fixed outer height)
- Left: **menu (☰)**, **Start**, **File Explorer**, **Windows Terminal**
- Right: compact stats (**CPU/MEM %**, **up to 2 disks %**, **CPU/GPU °C**), **auto-hide**, **clock**
- Disks: first two fixed drives (system drive preferred); 2nd row only if present
- Temps via LibreHardwareMonitor (GPU row only if a sensor is found)
- **Menu (☰)** (hover to open):
  - **Settings** → customization (left nav + right form)
  - **Upgrade to x.y.z** (only when a newer GitHub release exists) — downloads the GitHub zip in-app; a local `.cmd` + `robocopy` applies files (no PowerShell). Unsigned builds may still trigger SmartScreen; use **More info → Run anyway** if you trust the project.
  - **Install** / **Uninstall**
  - App name, version, **GitHub**, **Website**
  - **Close**
- **Settings** (Customization): mode, theme, opacity/blur (Mica/Acrylic), hover delay, stats, tab width, bar auto-hide, exclude list, **keyboard**, **monitors / tray**, **addons**. Stored in `%LocalAppData%\NoClickSwitch\settings.json`
- **Addons**: optional tools on the bar. **Flameshot** — icon right of Terminal when installed; **Install** / **Uninstall** via Settings (local `.cmd` + winget)
- **Tab context menu**: Pin / Unpin, Minimize, Close
- **Pinned** processes stay at the front of the strip (pin from the tab menu)
- **Hotkeys**: Ctrl+Alt+1…9 and Ctrl+Alt+0 jump to tabs 1–10 (optional Win+1…0; shell may override)
- **Multi-monitor**: **one bar per monitor by default** (windows on that display), or primary-only (all windows on one bar)
- **Tray**: Show/Hide bar, Settings, Exit (closing the bar hides it when the tray icon is enabled)
- **Install** (in-app): copies the app to `%LocalAppData%\NoClickSwitch` and adds **auto-start on login** (current user)
- **Uninstall**: removes auto-start and installed files (shown only when installed)
- Explorer icon opens **File Explorer**; **Ctrl+click** opens elevated (UAC)
- Terminal icon opens **Windows Terminal** (falls back to PowerShell / cmd); **Ctrl+click** opens elevated (UAC)
- Windows logo opens the **Start** menu
- Clock shows **time on top, date on bottom** (updates every second)
- Opening the bar **enables** Windows taskbar auto-hide
- Toggle turns **Windows taskbar auto-hide** on/off while running
- Closing the bar **restores the taskbar** (auto-hide off)
- Window list refreshes about every 1.5 seconds

## Windows Security / SmartScreen

No Click Switch is a small **unsigned** open-source utility. It controls windows (bring-to-front, hotkeys) and can download updates from **GitHub Releases**. Microsoft Defender’s machine-learning models sometimes flag that combination as a threat (names vary — e.g. “behavior”, “ClickFix”, “Commando”, “Wacatac”). That is a **false positive**, not a real trojan.

### Allow the app (recommended)

1. Open **Windows Security** → **Virus & threat protection** → **Protection history**
2. Find the block for **NoClickSwitch** / the zip
3. Choose **Actions** → **Allow** (or restore)
4. Optionally add an exclusion:  
   **Virus & threat protection** → **Manage settings** → **Exclusions** →  
   folder `%LocalAppData%\NoClickSwitch`

### SmartScreen “Windows protected your PC”

1. Click **More info**
2. Click **Run anyway**

### Report a false positive (helps everyone)

- [Microsoft Security Intelligence — submit a file](https://www.microsoft.com/en-us/wdsi/filesubmission)  
  Submit `NoClickSwitch.exe` or the release zip as a **software developer** false positive.

### What we do to reduce flags (no cert required)

- **No PowerShell for in-app upgrades or Flameshot helpers** — local `.cmd` + `robocopy` / `winget` only
- **Multi-file** self-contained releases (not a single packed self-extracting exe)
- Upgrades download the **official GitHub zip in-process** via HttpClient (no remote script)
- If Defender blocks `irm | iex`, use **install.cmd** or a manual zip extract (see *Other options* above)

Long-term reputation fix is a paid **code-signing certificate** (Authenticode), which we may add later.

## Requirements

- Windows 10/11
- **Install script / release:** no extra runtime (self-contained package)
- **Develop from source:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Run (development)

```powershell
dotnet run --project NoClickSwitch.csproj
```

## Build

```powershell
dotnet build NoClickSwitch.csproj -c Release
```

Self-contained package (same layout as GitHub Releases — multi-file, not single-file):

```powershell
dotnet publish NoClickSwitch.csproj -c Release -r win-x64 --self-contained true `
  -o publish
```

Zip the `publish` folder contents as `NoClickSwitch-win-x64.zip` for the release asset.

## Brand assets

App icon and GitHub social preview live under `Assets/`:

- `Assets/NoClickSwitch.ico` — multi-size app icon (NCS monogram)
- `Assets/app-icon-256.png` / `app-icon-512.png` — PNG sources
- `Assets/git-repo-social.png` — repo social card (`No Click Switch` · NCS · noclickswitch.com)

Regenerate with:

```powershell
python Assets/generate_brand.py
```
