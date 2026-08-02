"""Generate No Click Switch (NCS) brand assets: app icon, multi-size ICO, GitHub social."""
from __future__ import annotations

import os
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ASSETS = Path(__file__).resolve().parent


def find_font(size: int, bold: bool = True) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    windir = Path(os.environ.get("WINDIR", r"C:\Windows"))
    fonts = windir / "Fonts"
    names = (
        ["segoeuib.ttf", "arialbd.ttf", "calibrib.ttf"]
        if bold
        else ["segoeui.ttf", "arial.ttf", "calibri.ttf"]
    )
    for name in names:
        path = fonts / name
        if path.exists():
            return ImageFont.truetype(str(path), size=size)
    return ImageFont.load_default()


def rounded_rect(draw: ImageDraw.ImageDraw, box, radius, fill, outline=None, width=1):
    draw.rounded_rectangle(box, radius=radius, fill=fill, outline=outline, width=width)


def make_icon(size: int = 512) -> Image.Image:
    base = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(base)
    r = int(size * 0.22)
    rounded_rect(d, (0, 0, size - 1, size - 1), r, fill=(26, 31, 42, 255))

    # Top highlight
    overlay = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    od = ImageDraw.Draw(overlay)
    rounded_rect(od, (0, 0, size - 1, size // 2), r, fill=(255, 255, 255, 18))
    base = Image.alpha_composite(base, overlay)
    d = ImageDraw.Draw(base)

    # Mini window bar
    bar_w = int(size * 0.66)
    bar_h = int(size * 0.11)
    bar_x = (size - bar_w) // 2
    bar_y = int(size * 0.18)
    rounded_rect(
        d,
        (bar_x, bar_y, bar_x + bar_w, bar_y + bar_h),
        int(size * 0.03),
        fill=(44, 51, 66, 255),
    )

    pad = int(size * 0.025)
    tab_h = int(bar_h * 0.52)
    tab_y = bar_y + (bar_h - tab_h) // 2
    tab_w = (bar_w - pad * 4) // 3
    for i in range(3):
        tx = bar_x + pad + i * (tab_w + pad)
        color = (47, 134, 232, 255) if i == 0 else (74, 83, 102, 255)
        rounded_rect(
            d,
            (tx, tab_y, tx + tab_w, tab_y + tab_h),
            int(size * 0.015),
            fill=color,
        )

    # NCS monogram
    font = find_font(int(size * 0.30), bold=True)
    text = "NCS"
    bbox = d.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    tx = (size - tw) // 2
    ty = int(size * 0.42)
    d.text((tx + 2, ty + 4), text, font=font, fill=(0, 0, 0, 120))
    d.text((tx, ty), text, font=font, fill=(244, 247, 251, 255))

    # Accent bar
    aw = int(size * 0.14)
    ah = max(6, int(size * 0.016))
    ax = (size - aw) // 2
    ay = int(size * 0.82)
    rounded_rect(d, (ax, ay, ax + aw, ay + ah), ah // 2, fill=(47, 134, 232, 255))
    return base


def make_social(w: int = 1280, h: int = 640) -> Image.Image:
    img = Image.new("RGBA", (w, h), (11, 14, 20, 255))

    glow = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.ellipse((-200, -100, 700, 600), fill=(47, 134, 232, 45))
    gd.ellipse((800, 250, 1500, 850), fill=(94, 176, 255, 22))
    glow = glow.filter(ImageFilter.GaussianBlur(80))
    img = Image.alpha_composite(img, glow)
    d = ImageDraw.Draw(img)

    # Top bar mock
    d.rectangle((0, 0, w, 44), fill=(22, 27, 36, 255))
    d.line((0, 44, w, 44), fill=(47, 134, 232, 180), width=2)
    px, py = 18, 14
    for i, ww in enumerate([110, 88, 88, 88]):
        color = (47, 134, 232, 255) if i == 0 else (58, 66, 84, 180)
        rounded_rect(d, (px, py, px + ww, py + 16), 6, fill=color)
        px += ww + 10

    icon = make_icon(168)
    tile_x, tile_y = 88, 190

    shadow = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shadow)
    rounded_rect(
        sd,
        (tile_x + 6, tile_y + 10, tile_x + 168 + 6, tile_y + 168 + 10),
        36,
        fill=(0, 0, 0, 90),
    )
    shadow = shadow.filter(ImageFilter.GaussianBlur(12))
    img = Image.alpha_composite(img, shadow)
    img.paste(icon, (tile_x, tile_y), icon)
    d = ImageDraw.Draw(img)

    title_font = find_font(64, bold=True)
    tag_font = find_font(26, bold=False)
    chip_font = find_font(16, bold=False)
    foot_font = find_font(18, bold=False)

    text_x = tile_x + 168 + 48
    text_y = 200
    d.text((text_x, text_y), "No Click Switch", font=title_font, fill=(238, 242, 248, 255))
    d.text(
        (text_x, text_y + 86),
        "Always-on-top window switcher for Windows · NCS",
        font=tag_font,
        fill=(154, 168, 189, 255),
    )

    chips = ["Hover to switch", "Tabs per window", "CPU · MEM · Temps"]
    cx = text_x
    cy = text_y + 140
    for chip in chips:
        bb = d.textbbox((0, 0), chip, font=chip_font)
        tw, th = bb[2] - bb[0], bb[3] - bb[1]
        pad_x, pad_y = 16, 10
        rounded_rect(
            d,
            (cx, cy, cx + tw + pad_x * 2, cy + th + pad_y * 2),
            999,
            fill=(28, 35, 48, 255),
            outline=(42, 51, 68, 255),
            width=1,
        )
        d.text((cx + pad_x, cy + pad_y - 1), chip, font=chip_font, fill=(197, 208, 224, 255))
        cx += tw + pad_x * 2 + 12

    d.text(
        (88, h - 56),
        "github.com/william-bohannan/no-click-switch",
        font=foot_font,
        fill=(111, 127, 150, 255),
    )
    domain = "noclickswitch.com"
    bb = d.textbbox((0, 0), domain, font=foot_font)
    dw = bb[2] - bb[0]
    d.text((w - 88 - dw, h - 56), domain, font=foot_font, fill=(126, 182, 240, 255))

    return img.convert("RGB")


def main() -> None:
    icon512 = make_icon(512)
    icon256 = icon512.resize((256, 256), Image.Resampling.LANCZOS)
    icon256.save(ASSETS / "app-icon-256.png", "PNG", optimize=True)
    icon512.save(ASSETS / "app-icon-512.png", "PNG", optimize=True)

    sizes = [16, 24, 32, 48, 64, 128, 256]
    ico_images = [icon512.resize((s, s), Image.Resampling.LANCZOS) for s in sizes]
    ico_images[0].save(
        ASSETS / "NoClickSwitch.ico",
        format="ICO",
        sizes=[(s, s) for s in sizes],
        append_images=ico_images[1:],
    )

    make_social().save(ASSETS / "git-repo-social.png", "PNG", optimize=True)

    for name in [
        "app-icon-256.png",
        "app-icon-512.png",
        "NoClickSwitch.ico",
        "git-repo-social.png",
    ]:
        p = ASSETS / name
        print(f"{name}: {p.stat().st_size} bytes")


if __name__ == "__main__":
    main()
