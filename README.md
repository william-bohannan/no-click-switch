# Swiztch Bar

A minimal always-on-top Windows top bar that shows **one tab per open window**.

## Behaviour

- Full-width bar pinned to the **top of the primary screen**
- **Always on top**
- **One tab per open window** (title + icon; click to focus and fill space below the bar)
- **Drag tabs** to reorder; order is **kept** when the list refreshes
- Each tab is **5em wide × 2em tall** (80 × 32 DIP at 16px em)
- Tabs **wrap** when they exceed the screen width; the bar **grows in height** (no fixed outer height)
- Top-right controls: **auto-hide**, **Start** (Windows logo), **clock** (time over date), **close**
- Windows logo opens the **Start** menu
- Clock shows **time on top, date on bottom** (updates every second)
- Grey **×** closes the bar
- Toggle turns **Windows taskbar auto-hide** on/off
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
