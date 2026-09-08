# Privacy policy — No Click Switch

Last updated: 8 September 2026

No Click Switch (“NCS”) is a local Windows utility. It does not require an account and does not include advertising.

## What we collect

We do not collect, sell, or transmit personal information.

The Microsoft Store may collect standard install, crash, and review data according to [Microsoft’s privacy statement](https://privacy.microsoft.com/). That is Microsoft’s processing, not ours.

## What stays on your PC

Settings are stored only on your device:

`%LocalAppData%\NoClickSwitch\settings.json`

That file can include theme, layout, exclude rules, pinned process names, and similar preferences you choose. It is not uploaded by the app.

Optional debug logs (if present) stay in the same folder.

## Window titles and processes

The bar lists windows that are already open on your PC (title and icon) so you can switch among them. That list is not sent over the network.

## Network

The app does not phone home for updates. Updates are delivered by the Microsoft Store.

The optional **Flameshot** addon may start your existing Windows Package Manager (`winget`) or Chocolatey to install or remove Flameshot. That uses *your* package manager and Flameshot’s publishers, not our servers. You can ignore the addon.

The GitHub and Website items in the menu open those pages in your browser only when you choose them.

## Temperature and system stats

CPU, memory, disk, and temperature figures are read from Windows APIs (and `nvidia-smi` when NVIDIA’s tool is already installed). They are shown on the bar and are not uploaded.

## Children

The app is a general productivity tool. It is not directed at children and does not collect data from anyone.

## Contact

Questions: open an issue at https://github.com/william-bohannan/no-click-switch/issues
