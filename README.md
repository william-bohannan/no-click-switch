# Switched Bar

A minimal always-on-top Windows top bar that shows **one tab per open window**.

**Repository:** https://github.com/william-bohannan/switchedbar

## Install (Windows)

No admin rights. Installs to `%LocalAppData%\SwitchedBar`, starts with Windows (current user), and launches the app.

**PowerShell** (recommended):

```powershell
irm https://raw.githubusercontent.com/william-bohannan/switchedbar/main/install.ps1 | iex
```

**Command Prompt:**

```cmd
powershell -NoProfile -ExecutionPolicy Bypass -Command "irm https://raw.githubusercontent.com/william-bohannan/switchedbar/main/install.ps1 | iex"
```

The script downloads the latest **self-contained** release (no .NET install required). If no release is available, it falls back to building from source when the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) is present.

### Uninstall

```powershell
irm https://raw.githubusercontent.com/william-bohannan/switchedbar/main/uninstall.ps1 | iex
```

You can also use **Uninstall** from the app’s ☰ menu.

## Behaviour

- Full-width bar pinned to the **top of the primary screen**
- **Always on top**
- **One tab per open window** (title + icon)
- **Hover** a tab to bring that window to the front (blue highlight)
- **Click** a tab to focus and fill the free space below the bar
- **Right-click** a tab → **Close window**
- **Active window** tab uses a quiet grey highlight
- **Drag tabs** to reorder; order is **kept** when the list refreshes
- Each tab is **5em wide × 2em tall** (80 × 32 DIP at 16px em)
- Tabs **wrap** when they exceed the screen width; the bar **grows in height** (no fixed outer height)
- Left: **menu (☰)**, **Start**, **File Explorer**
- Right: compact stats (**CPU/MEM %**, **up to 2 disks %**, **CPU/GPU °C**), **auto-hide**, **clock**
- Disks: first two fixed drives (system drive preferred); 2nd row only if present
- Temps via LibreHardwareMonitor (GPU row only if a sensor is found)
- **Menu**: Close, About (GitHub), Install / Uninstall, version, **Switched Bar**
- **Install** (in-app): copies the app to `%LocalAppData%\SwitchedBar` and adds **auto-start on login** (current user)
- **Uninstall**: removes auto-start and installed files (shown only when installed)
- Explorer icon opens **File Explorer**
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
dotnet run
```

## Build

```powershell
dotnet build -c Release
```

Self-contained package (same layout as GitHub Releases):

```powershell
dotnet publish SwitchedBar.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish
```
