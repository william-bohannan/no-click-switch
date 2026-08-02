# No Click Switch (NCS)

A minimal always-on-top Windows top bar that shows **one tab per open window**.

| | |
|---|---|
| **Short name** | **NCS** |
| **Repository** | https://github.com/william-bohannan/no-click-switch |
| **Website** | https://noclickswitch.com *(coming soon)* |
| **Install path** | `%LocalAppData%\NoClickSwitch` |

## Install (Windows)

No admin rights. Installs to `%LocalAppData%\NoClickSwitch`, starts with Windows (current user), and launches the app.

**PowerShell** (recommended):

```powershell
irm https://raw.githubusercontent.com/william-bohannan/no-click-switch/main/install.ps1 | iex
```

**Command Prompt:**

```cmd
powershell -NoProfile -ExecutionPolicy Bypass -Command "irm https://raw.githubusercontent.com/william-bohannan/no-click-switch/main/install.ps1 | iex"
```

The script downloads the latest **self-contained** release (no .NET install required). If no release is available, it falls back to building from source when the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) is present.

### Uninstall

```powershell
irm https://raw.githubusercontent.com/william-bohannan/no-click-switch/main/uninstall.ps1 | iex
```

You can also use **Uninstall** from the app’s ☰ menu.

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
  - **Upgrade to x.y.z** (only when a newer GitHub release exists) — downloads the GitHub release zip in-app and restarts (no remote PowerShell). Unsigned builds may still trigger SmartScreen; use **More info → Run anyway** if you trust the project.
  - **Install** / **Uninstall**
  - App name, version, **GitHub**, **Website**
  - **Close**
- **Settings** (Customization): mode, theme, opacity/blur (Mica/Acrylic), hover delay, stats, tab width, bar auto-hide, exclude list, **keyboard**, **monitors / tray**, **addons**. Stored in `%LocalAppData%\NoClickSwitch\settings.json`
- **Addons**: optional tools on the bar. **Flameshot** — icon right of Terminal when installed; **Install** / **Uninstall** via Settings (PowerShell + winget)
- **Tab context menu**: Pin / Unpin, Minimize, Close
- **Pinned** processes stay at the front of the strip (pin from the tab menu)
- **Hotkeys**: Ctrl+Alt+1…9 and Ctrl+Alt+0 jump to tabs 1–10 (optional Win+1…0; shell may override)
- **Multi-monitor**: primary-only bar (all windows) or one bar per monitor (windows on that display)
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

Self-contained package (same layout as GitHub Releases):

```powershell
dotnet publish NoClickSwitch.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish
```

## Brand assets

App icon and GitHub social preview live under `Assets/`:

- `Assets/NoClickSwitch.ico` — multi-size app icon (NCS monogram)
- `Assets/app-icon-256.png` / `app-icon-512.png` — PNG sources
- `Assets/git-repo-social.png` — repo social card (`No Click Switch` · NCS · noclickswitch.com)

Regenerate with:

```powershell
python Assets/generate_brand.py
```
