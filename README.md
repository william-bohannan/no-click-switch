# No Click Switch (NCS)

A minimal always-on-top Windows top bar that shows **one tab per open window**.

| | |
|---|---|
| **Short name** | **NCS** |
| **Repository** | https://github.com/william-bohannan/no-click-switch |
| **Website** | https://noclickswitch.com *(coming soon)* |
| **Distribution** | Microsoft Store (MSIX) |
| **Settings** | `%LocalAppData%\NoClickSwitch\settings.json` |

## Install

Install from the **Microsoft Store**. Updates, Start Menu, Search, and uninstall are handled by Windows.

The app starts with Windows (MSIX startup task). Turn that off in **Settings → Apps → Startup**.

Sideload a local package (Developer Mode or a trusted test cert):

```powershell
powershell -ExecutionPolicy Bypass -File Package\pack-store.ps1 -Sideload
Add-AppxPackage Package\out\NoClickSwitch-*-x64.msix
```

## Behaviour

- Full-width bar pinned to the **top of the primary screen**
- **Always on top**
- **One tab per open window** (title + icon)
- **Hover** a tab to bring that window to the front and **fill the free space** under the bar (no click)
- **Click** a tab does the same (focus + fill under the bar)
- **Right-click** a tab → **Close window**
- **Active window** tab uses a quiet grey highlight
- **Drag tabs** to reorder; order is **kept** when the list refreshes
- Each tab is **5em wide × 2em tall** (80 × 32 DIP at 16px em)
- Tabs **wrap** when they exceed the screen width; the bar **grows in height** (no fixed outer height)
- Left: **menu (☰)**, **Start**, **File Explorer**, **Windows Terminal**
- Right: compact stats (**CPU/MEM %**, **up to 2 disks %**, **CPU/GPU °C**), **auto-hide**, **clock**
- Disks: first two fixed drives (system drive preferred); 2nd row only if present
- Temps via Windows thermal APIs (many laptops) and **nvidia-smi** for NVIDIA GPUs
- **Menu (☰)** (hover to open):
  - **Settings** → customization (left nav + right form)
  - App name, version, **GitHub**, **Website**
  - **Close**
- **Settings** (Customization): mode, theme, opacity/blur (Mica/Acrylic), hover delay, stats, tab width, bar auto-hide, exclude list, **keyboard**, **monitors / tray**, **addons**. Stored in `%LocalAppData%\NoClickSwitch\settings.json`
- **Addons**: optional extras, off until you choose them. **Flameshot** — icon right of Terminal when installed
- **Tab context menu**: Pin / Unpin, Minimize, Close
- **Pinned** processes stay at the front of the strip (pin from the tab menu)
- **Hotkeys**: Ctrl+Alt+1…9 and Ctrl+Alt+0 jump to tabs 1–10 (optional Win+1…0; shell may override)
- **Multi-monitor**: **one bar per monitor by default** (windows on that display), or primary-only (all windows on one bar)
- **Tray**: Show/Hide bar, Settings, Exit (closing the bar hides it when the tray icon is enabled)
- **One instance** per login session. A second launch shows the existing bars.
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
- **Store / sideload:** no extra runtime (self-contained MSIX)
- **Develop from source:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Run (development)

```powershell
dotnet run --project NoClickSwitch.csproj
```

## Build

```powershell
dotnet build NoClickSwitch.csproj -c Release
```

## Microsoft Store (MSIX)

Shipping build is a **win-x64 MSIX**. Microsoft re-signs Store packages — you do not need a code-signing certificate. Unpackaged `dotnet run` still works for local development.

Follow **[next-steps.md](next-steps.md)** to reserve the name, paste Partner Center identity, pack, and submit.

1. Create a free developer account at [storedeveloper.microsoft.com](https://storedeveloper.microsoft.com) (use that URL; it is the no-fee flow).
2. In [Partner Center](https://partner.microsoft.com/dashboard) → **Apps and games** → **New product** → **MSIX or PWA app**, reserve **No Click Switch**.
3. Open the reserved app → **Product identity**. Copy **Package/Identity/Name**, **Publisher**, and **Publisher display name** into `Package/identity.json`.
4. Pack:

```powershell
powershell -ExecutionPolicy Bypass -File Package\pack-store.ps1
```

Output: `Package\out\NoClickSwitch-<version>-x64.msix`.

5. Start a submission: pricing, properties, age rating, store listing (screenshots at 1366×768 or 1920×1080), then upload the `.msix`.
6. In **Notes for certification**, explain full trust: this is a desktop window switcher that enumerates HWNDs, brings windows to the front, registers hotkeys, and draws an always-on-top bar. It does **not** install kernel drivers or require administrator.

Optional local sideload (Developer Mode or a trusted test cert):

```powershell
powershell -ExecutionPolicy Bypass -File Package\pack-store.ps1 -Sideload
```

The package starts with Windows via an MSIX startup task (toggle in **Settings → Apps → Startup**). Updates come from the Store. The app does not install drivers or run as administrator.

## Brand assets

App icon and GitHub social preview live under `Assets/`:

- `Assets/NoClickSwitch.ico` — multi-size app icon (NCS monogram)
- `Assets/app-icon-256.png` / `app-icon-512.png` — PNG sources
- `Assets/git-repo-social.png` — repo social card (`No Click Switch` · NCS · noclickswitch.com)

Regenerate with:

```powershell
python Assets/generate_brand.py
```
