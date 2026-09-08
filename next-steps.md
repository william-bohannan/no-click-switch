# Next steps: Microsoft Store

No Click Switch is packaged as a **win-x64 MSIX**. Microsoft re-signs Store packages, so you do **not** need a code-signing certificate.

Do these in order. The first submission is blocked until step 3 (Partner Center identity) is in `Package/identity.json`.

## 1. Create a developer account

1. Open [storedeveloper.microsoft.com](https://storedeveloper.microsoft.com) — that URL is the **no-fee** flow.
2. Sign in with the Microsoft account that should own the app.
3. Choose **Individual** unless you are publishing as a registered company (account type cannot be changed later).
4. Finish identity verification (government ID + selfie for Individual).
5. You land in [Partner Center](https://partner.microsoft.com/dashboard).

## 2. Reserve the name

1. Partner Center → **Apps and games** → **New product** → **MSIX or PWA app**.
2. Check availability for **No Click Switch**.
3. **Reserve product name**.

The reservation holds the Store listing name. Use this exact display name in the submission.

## 3. Paste package identity

1. Open the reserved app → **Product identity** (sometimes under **Product management**).
2. Copy these three values into `Package/identity.json`:

```json
{
  "Name": "PASTE_Package_Identity_Name",
  "Publisher": "PASTE_Publisher_CN_STRING",
  "PublisherDisplayName": "PASTE_Publisher_display_name"
}
```

| Partner Center field | `identity.json` key |
|---|---|
| Package/Identity/Name | `Name` |
| Publisher (the `CN=…` string) | `Publisher` |
| Publisher display name | `PublisherDisplayName` |

The placeholders currently in the repo (`william-bohannan.NoClickSwitch` / `CN=william-bohannan`) are **only for local sideload**. A Store upload with those values will fail certification.

Commit the updated `identity.json` after you paste the real values.

## 4. Prepare the Store listing

Draft copy, logos, screenshots, and a privacy policy are in **[store/](store/README.md)**. Paste from `store/listing.md` and upload the PNGs.

**Screenshots** (required)

- At least **one**; four 1920×1080 mockups are in `store/screenshots/`.
- Size: **1366×768** or **1920×1080** (Windows 11 Store).
- After sideload, you can replace mockups with live captures.

**Text** — see `store/listing.md` (description, search terms, captions).

**Privacy policy URL** — host `store/privacy.md` over HTTPS (GitHub Pages, the website, or similar), then paste that URL.

**Age rating**

Complete the questionnaire. This app has no user-generated content, no location, no chat. Expect a low (Everyone / 3+) rating.

## 5. Optional: sideload-test on your PC

```powershell
powershell -ExecutionPolicy Bypass -File Package\pack-store.ps1 -Sideload
Add-AppxPackage Package\out\NoClickSwitch-*-x64.msix
```

Confirm:

- The bar appears on the primary display (and others if that setting is on).
- Hover / click switches windows.
- Settings save after a restart.
- The app is listed under **Settings → Apps → Installed apps**.
- **Settings → Apps → Startup** shows **No Click Switch** (enabled).
- There is **no** Install / Uninstall / Upgrade in the ☰ menu.
- Uninstall from **Settings → Apps** removes it cleanly.

Then uninstall the sideload build before installing the Store build later (two packages with different identity can sit side by side and confuse testing).

## 6. Pack for Store upload

After `identity.json` has Partner Center values:

```powershell
powershell -ExecutionPolicy Bypass -File Package\pack-store.ps1
```

Upload this file:

`Package\out\NoClickSwitch-<version>-x64.msix`

Do **not** use `-Sideload` for the Store upload. Microsoft re-signs the package.

Each new Store submission needs a **higher** four-part version (`1.1.22.0` → `1.1.23.0`). Bump `<Version>` in `NoClickSwitch.csproj` before packing.

## 7. Create the submission

In the reserved app → **Start your submission**:

1. **Pricing and availability** — free, all markets you care about (or a subset for the first release).
2. **Properties** — category, support contact, privacy policy URL.
3. **Age ratings** — finish the questionnaire.
4. **Packages** — upload the `.msix` from step 6. Architecture is **x64** only.
5. **Store listings** — English (United States) at minimum: description, screenshots, search terms.
6. **Notes for certification** — paste the block below.
7. **Submit for certification**.

Typical first-app review: a few business days.

## 8. Notes for certification (paste this)

```
No Click Switch is a classic Win32 / WPF desktop utility packaged with Desktop Bridge (runFullTrust).

It draws an always-on-top top bar, enumerates open HWNDs, brings windows to the foreground on hover or click, and registers optional global hotkeys. Those window-management APIs require full trust; the app is not a UWP sandbox app.

It does not:
- install kernel drivers
- require administrator
- download or apply its own updates (Microsoft Store owns updates)
- copy itself into %LocalAppData%
- run a background service

User settings are stored in %LocalAppData%\NoClickSwitch\settings.json.

An MSIX startup task starts the app at sign-in (the user can disable it in Settings → Apps → Startup).

The optional Flameshot addon only launches an already-installed screenshot tool, or the user’s own winget/Chocolatey, to install Flameshot. It does not bundle Flameshot.
```

If certification asks about `runFullTrust`, that paragraph is the answer.

## 9. After it is live

- Install from the Store on a clean machine and re-run the checklist in step 5.
- Point `https://noclickswitch.com` and the GitHub README “Install” section at the Store product page.
- Future releases: bump the csproj version → pack → new submission. Do not ship GitHub zip installers; Store is the distribution.

## If something fails

| Problem | What to do |
|---|---|
| Package identity / publisher mismatch | Step 3 — `identity.json` must match Product identity exactly, then re-pack. |
| `runFullTrust` rejected | Resubmit with the notes in step 8; this is a normal desktop utility capability. |
| Missing screenshots / privacy policy | Step 4 — those fields block submit, not just certification. |
| `makeappx` / pack script error | Restore happens via `Microsoft.Windows.SDK.BuildTools`. Install the .NET 8 SDK and re-run `Package\pack-store.ps1`. |
| Sideload “package is not signed” | Use `-Sideload` (test cert) or Developer Mode; Store uploads do not need your cert. |
