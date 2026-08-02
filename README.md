# Switched Bar

A minimal always-on-top Windows top bar that shows **one tab per open window**.

**Repository:** https://github.com/william-bohannan/switchedbar

## Behaviour

- Full-width bar pinned to the **top of the primary screen**
- **Always on top**
- **One tab per open window** (title + icon)
- **Hover** a tab to bring that window to the front (blue highlight)
- **Click** a tab to focus and fill the free space below the bar
- **Active window** tab uses a quiet grey highlight
- **Drag tabs** to reorder; order is **kept** when the list refreshes
- Each tab is **5em wide × 2em tall** (80 × 32 DIP at 16px em)
- Tabs **wrap** when they exceed the screen width; the bar **grows in height** (no fixed outer height)
- Top-right compact stats (vertical stacks): **CPU/MEM %**, **up to 2 disks %**, **CPU/GPU °C**
- Then: **auto-hide**, **menu (☰)**, **Explorer**, **Start**, **clock**
- Disks: first two fixed drives (system drive preferred); 2nd row only if present
- Temps via LibreHardwareMonitor (GPU row only if a sensor is found)
- **Menu**: Close, About (GitHub), Install / Uninstall, version, **Switched Bar**
- **Install**: copies the app to `%LocalAppData%\SwitchedBar` and adds **auto-start on login** (current user)
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
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Run

```powershell
dotnet run
```

## Build

```powershell
dotnet build -c Release
```
