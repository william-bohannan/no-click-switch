# Microsoft Store listing kit

Paste-ready copy, logos, and example screenshots for Partner Center. Follow [next-steps.md](../next-steps.md) for the account and submission order.

## Layout

| Path | Use |
|---|---|
| [listing.md](listing.md) | Name, description, search terms, captions |
| [privacy.md](privacy.md) | Privacy policy to host at a public HTTPS URL |
| `logos/` | 300×300 tile, 1080×1080 box art, 720×1080 poster |
| `screenshots/` | Four 1920×1080 desktop shots (minimum is one) |
| `promo/` | Optional 1920×1080 super-hero image |
| `generate-assets.ps1` | Rebuilds the PNGs from `Assets/app-icon-512.png` |

## Regenerate images

```powershell
powershell -ExecutionPolicy Bypass -File store\generate-assets.ps1
```

Logos are flattened from the real NCS icon. Screenshots are **UI-accurate mockups** (same chrome, tab size, and palette as the app). Replace them with live captures after you sideload the MSIX if you prefer photographs of your machine.

## Partner Center mapping

1. **Store listings** → paste text from `listing.md`
2. **Screenshots** (Desktop) → `screenshots/*.png`
3. **Store logos** → `logos/app-tile-300.png` (required). Add box art and poster if the form shows those slots.
4. **Trailers and additional assets** → optional `promo/super-hero-1920x1080.png`
5. **Properties** → privacy policy URL pointing at a hosted copy of `privacy.md`

Do not upload Xbox-only sizes unless you also publish to Xbox.
