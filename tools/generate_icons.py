#!/usr/bin/env python3
"""Generate the SimpliPDF app-icon asset set (Windows 11 / WinUI 3 Fluent style).

This script is the single source of truth for the app's iconography. It draws a
textless "stacked / merging pages" mark — two overlapping rounded document pages
with a red PDF header band and a folded top-right corner (dog-ear) — and emits the
full MSIX asset matrix plus a multi-size ``AppIcon.ico``.

Design is rendered at high resolution and downsampled with LANCZOS so the simple
geometry comes out vector-crisp at every target size. No SVG rasterizer or extra
native dependency is required (only Pillow).

Usage::

    python tools/generate_icons.py            # write the full asset matrix + .ico
    python tools/generate_icons.py --preview  # write a contact sheet for inspection
"""

from __future__ import annotations

import argparse
import io
import os
import struct

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

# --- Paths ------------------------------------------------------------------
HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.abspath(os.path.join(HERE, "..", "SimpliPDF", "Assets"))

# --- Palette ----------------------------------------------------------------
RED_TOP = (228, 56, 47)      # #E4382F  header band, top
RED_BOT = (197, 38, 30)      # #C5261E  header band, bottom
FOLD_TOP = (255, 255, 255)   # folded-corner underside, top
FOLD_BOT = (236, 239, 243)   # folded-corner underside, bottom
PAGE_TOP = (255, 255, 255)   # front page, top
PAGE_BOT = (240, 242, 246)   # #F0F2F6  front page, bottom
BACK_TOP = (228, 231, 236)   # back page, top
BACK_BOT = (208, 213, 220)   # back page, bottom
LINE = (203, 209, 217)       # #CBD1D9  content lines
EDGE_LIGHT = (171, 179, 191) # #ABB3BF  edge stroke for light-unplated theme

# Internal master resolution. Every concrete asset is downsampled from a master
# of this size, so it must exceed the largest glyph we ever emit (310@400% = 1240).
MASTER = 1536

# How the "PDF" wordmark is rendered: "band" (white on the red header band),
# "body" (bold red on the white page), or "none" (textless). Tiny sizes always
# fall back to textless so the taskbar / title-bar icons stay crisp.
WORDMARK = "band"

# --- Low-level drawing helpers ---------------------------------------------

def _vgrad(w: int, h: int, top: tuple[int, int, int], bottom: tuple[int, int, int]) -> Image.Image:
    """Opaque RGBA image with a vertical top->bottom gradient."""
    ramp = Image.linear_gradient("L").resize((w, h))  # 0 (top) .. 255 (bottom)
    top_img = Image.new("RGBA", (w, h), top + (255,))
    bot_img = Image.new("RGBA", (w, h), bottom + (255,))
    return Image.composite(bot_img, top_img, ramp)


def _rounded_mask(w: int, h: int, r: int) -> Image.Image:
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, w - 1, h - 1], radius=r, fill=255)
    return mask


def _page(w: int, h: int, r: int, top, bottom, edge=None) -> Image.Image:
    """A rounded page filled with a vertical gradient, optionally edge-stroked."""
    img = _vgrad(w, h, top, bottom)
    img.putalpha(_rounded_mask(w, h, r))
    if edge is not None:
        ImageDraw.Draw(img).rounded_rectangle(
            [0, 0, w - 1, h - 1], radius=r, outline=edge + (255,), width=max(1, w // 130)
        )
    return img


# --- Text (the optional "PDF" wordmark) ------------------------------------
_FONT_CACHE: dict[int, ImageFont.FreeTypeFont] = {}
_FONT_FILES = ("segoeuib.ttf", "seguisb.ttf", "arialbd.ttf")  # Segoe UI Bold, then fallbacks


def _font(size: int) -> ImageFont.FreeTypeFont:
    size = max(6, int(size))
    if size not in _FONT_CACHE:
        loaded = None
        fonts_dir = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts")
        for name in _FONT_FILES:
            try:
                loaded = ImageFont.truetype(os.path.join(fonts_dir, name), size)
                break
            except OSError:
                continue
        _FONT_CACHE[size] = loaded or ImageFont.load_default()
    return _FONT_CACHE[size]


def _fit_font(text: str, max_w: int, max_h: int) -> ImageFont.FreeTypeFont:
    """Largest bold font whose ``text`` fits inside ``max_w`` x ``max_h``."""
    size = int(max_h)
    while size > 6:
        fnt = _font(size)
        left, top, right, bottom = fnt.getbbox(text)
        if (right - left) <= max_w and (bottom - top) <= max_h:
            return fnt
        size -= max(1, size // 16)
    return _font(6)


def _draw_centered(layer: Image.Image, text: str, cx: float, cy: float,
                   font: ImageFont.FreeTypeFont, fill) -> None:
    left, top, right, bottom = font.getbbox(text)
    x = cx - (right - left) / 2 - left
    y = cy - (bottom - top) / 2 - top
    ImageDraw.Draw(layer).text((x, y), text, font=font, fill=fill)


# --- The mark ---------------------------------------------------------------

def render_master(r_px: int, pad: float, theme: str = "dark", simple: bool = False,
                  wordmark: str = "band") -> Image.Image:
    """Render the stacked-pages mark into a transparent square of ``r_px``.

    ``pad`` is the safe-zone fraction inset on each side. ``theme='light'`` adds a
    cool-gray edge so the white pages stay visible on a light taskbar. ``simple``
    drops fine detail (lines / wordmark) for tiny icon sizes. ``wordmark`` selects
    the "PDF" treatment: ``"band"``, ``"body"`` or ``"none"``.
    """
    R = r_px
    canvas = Image.new("RGBA", (R, R), (0, 0, 0, 0))

    cx0 = int(R * pad)
    cy0 = int(R * pad)
    cw = R - 2 * cx0
    ch = R - 2 * cy0

    # Portrait pages stacked on a diagonal (back page peeks up and to the right),
    # with the union of both pages centered inside the safe-zone content box.
    ar = 0.84                          # page width / height (portrait)
    f = 0.14                           # stack offset as a fraction of page height
    page_h = int(min(cw / (ar + f), ch / (1.0 + f)))
    page_w = int(page_h * ar)
    off = int(page_h * f)
    ox = cx0 + (cw - (page_w + off)) // 2
    oy = cy0 + (ch - (page_h + off)) // 2
    rad = max(2, int(page_w * 0.08))
    edge = EDGE_LIGHT if theme == "light" else None

    fx0, fy0 = ox, oy + off            # front page, lower-left
    bx0, by0 = ox + off, oy           # back page, upper-right

    # Soft drop shadow under the whole stack.
    shadow = Image.new("RGBA", (R, R), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shadow)
    sd.rounded_rectangle([bx0, by0, bx0 + page_w, by0 + page_h], radius=rad, fill=(15, 18, 24, 70))
    sd.rounded_rectangle([fx0, fy0, fx0 + page_w, fy0 + page_h], radius=rad, fill=(15, 18, 24, 95))
    shadow = shadow.filter(ImageFilter.GaussianBlur(R * 0.013))
    shadow = ImageChops.offset(shadow, 0, max(1, int(R * 0.007)))
    canvas = Image.alpha_composite(canvas, shadow)

    # Back page.
    canvas.alpha_composite(_page(page_w, page_h, rad, BACK_TOP, BACK_BOT, edge=edge), (bx0, by0))

    # Front page (built in its own coordinate space, then composited).
    front = _page(page_w, page_h, rad, PAGE_TOP, PAGE_BOT)
    fmask = _rounded_mask(page_w, page_h, rad)

    # Red header band (hosts the optional "PDF" wordmark), clipped to the
    # page's rounded top corners. Tiny sizes stay textless and use a slim band.
    show_text = wordmark in ("band", "body") and not simple
    band_h = int(page_h * (0.42 if (show_text and wordmark == "band") else 0.24))
    band = _vgrad(page_w, band_h, RED_TOP, RED_BOT)
    band_layer = Image.new("RGBA", (page_w, page_h), (0, 0, 0, 0))
    band_layer.alpha_composite(band, (0, 0))
    band_layer.putalpha(ImageChops.multiply(band_layer.getchannel("A"), fmask))
    front = Image.alpha_composite(front, band_layer)

    lx = int(page_w * 0.17)
    lh = max(2, int(page_h * 0.045))

    if show_text and wordmark == "band":
        # White "PDF" in the band, nudged left so it clears the dog-ear corner.
        fnt = _fit_font("PDF", int(page_w * 0.60), int(band_h * 0.50))
        _draw_centered(front, "PDF", page_w * 0.44, band_h * 0.5, fnt, (255, 255, 255, 255))
        ld = ImageDraw.Draw(front)
        ly = int(band_h + (page_h - band_h) * 0.42)
        for i, wf in enumerate((0.66, 0.48)):
            y = ly + i * int(page_h * 0.12)
            ld.rounded_rectangle([lx, y, lx + int(page_w * wf), y + lh], radius=lh // 2, fill=LINE + (255,))
    elif show_text and wordmark == "body":
        # Bold red "PDF" on the white body, with slim content lines beneath.
        fnt = _fit_font("PDF", int(page_w * 0.74), int((page_h - band_h) * 0.40))
        _draw_centered(front, "PDF", page_w * 0.5, band_h + (page_h - band_h) * 0.38, fnt, RED_BOT + (255,))
        ld = ImageDraw.Draw(front)
        ly = int(band_h + (page_h - band_h) * 0.70)
        for i, wf in enumerate((0.58, 0.44)):
            y = ly + i * int(page_h * 0.10)
            ld.rounded_rectangle([lx, y, lx + int(page_w * wf), y + lh], radius=lh // 2, fill=LINE + (255,))
    elif not simple:
        # Textless: three content lines.
        ld = ImageDraw.Draw(front)
        ly = int(band_h + page_h * 0.15)
        gap = int(page_h * 0.13)
        for i, wf in enumerate((0.62, 0.74, 0.48)):
            y = ly + i * gap
            ld.rounded_rectangle([lx, y, lx + int(page_w * wf), y + lh], radius=lh // 2, fill=LINE + (255,))

    # Folded top-right corner (dog-ear): cut the corner, then lay the fold flap.
    fold = int(page_w * 0.26)
    a = (page_w - fold, 0)
    b = (page_w, fold)
    c = (page_w - fold, fold)
    # Cut away the page corner above/right of the diagonal a->b.
    cut = Image.new("L", (page_w, page_h), 255)
    ImageDraw.Draw(cut).polygon([a, (page_w, 0), b], fill=0)
    front.putalpha(ImageChops.multiply(front.getchannel("A"), cut))
    # Soft shadow cast by the flap onto the band.
    flap_shadow = Image.new("RGBA", (page_w, page_h), (0, 0, 0, 0))
    ImageDraw.Draw(flap_shadow).polygon([a, b, c], fill=(80, 12, 9, 150))
    flap_shadow = flap_shadow.filter(ImageFilter.GaussianBlur(max(1, page_w // 120)))
    flap_shadow = ImageChops.offset(flap_shadow, max(1, page_w // 160), max(1, page_w // 160))
    flap_shadow.putalpha(ImageChops.multiply(flap_shadow.getchannel("A"), fmask))
    front = Image.alpha_composite(front, flap_shadow)
    # The fold flap itself (lighter page underside).
    flap = _vgrad(page_w, page_h, FOLD_TOP, FOLD_BOT)
    fold_mask = Image.new("L", (page_w, page_h), 0)
    ImageDraw.Draw(fold_mask).polygon([a, b, c], fill=255)
    flap.putalpha(fold_mask)
    front = Image.alpha_composite(front, flap)

    # Re-clip the front to keep the other three rounded corners crisp.
    front.putalpha(ImageChops.multiply(front.getchannel("A"), fmask))

    if edge is not None:
        # Stroke the (now folded) outline so it reads on a light background.
        es = Image.new("RGBA", (page_w, page_h), (0, 0, 0, 0))
        ed = ImageDraw.Draw(es)
        ew = max(1, page_w // 130)
        ed.line([a, b], fill=EDGE_LIGHT + (255,), width=ew)
        es.putalpha(ImageChops.multiply(es.getchannel("A"), fmask))
        front = Image.alpha_composite(front, es)
        outline = Image.new("RGBA", (page_w, page_h), (0, 0, 0, 0))
        ImageDraw.Draw(outline).rounded_rectangle(
            [0, 0, page_w - 1, page_h - 1], radius=rad, outline=EDGE_LIGHT + (255,), width=ew
        )
        outline.putalpha(ImageChops.multiply(outline.getchannel("A"), cut))
        front = Image.alpha_composite(front, outline)

    canvas.alpha_composite(front, (fx0, fy0))
    return canvas


# --- Master cache + concrete sizing ----------------------------------------
_MASTERS: dict[tuple, Image.Image] = {}


def _master(pad: float, theme: str, simple: bool) -> Image.Image:
    key = (round(pad, 4), theme, simple, WORDMARK)
    if key not in _MASTERS:
        _MASTERS[key] = render_master(MASTER, pad, theme, simple, WORDMARK)
    return _MASTERS[key]


def glyph(px: int, pad: float, theme: str = "dark", simple: bool = False) -> Image.Image:
    """A square icon of side ``px`` downsampled from the cached master."""
    src = _master(pad, theme, simple)
    if px == MASTER:
        return src.copy()
    return src.resize((px, px), Image.LANCZOS)


def banner(w: int, h: int, pad: float, height_frac: float) -> Image.Image:
    """Place the square mark, centered, on a transparent ``w``x``h`` canvas."""
    canvas = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    g = max(8, int(h * height_frac))
    mark = glyph(g, pad)
    canvas.alpha_composite(mark, ((w - g) // 2, (h - g) // 2))
    return canvas


# --- ICO assembly (hand-built so each size can use tailored art) ------------

def save_ico(images: list[Image.Image], path: str) -> None:
    """Write a PNG-compressed .ico containing the given (already-sized) images."""
    entries = []
    for im in images:
        buf = io.BytesIO()
        im.save(buf, format="PNG")
        entries.append((im.width, im.height, buf.getvalue()))

    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(entries)))  # ICONDIR
        offset = 6 + 16 * len(entries)
        for w, h, data in entries:
            f.write(struct.pack(
                "<BBBBHHII",
                w if w < 256 else 0,
                h if h < 256 else 0,
                0, 0, 1, 32, len(data), offset,
            ))
            offset += len(data)
        for _, _, data in entries:
            f.write(data)


# --- Asset matrix -----------------------------------------------------------
SCALES = {100: 1.0, 125: 1.25, 150: 1.5, 200: 2.0, 400: 4.0}
TARGET_SIZES = (16, 24, 32, 48, 256)
TILE_PAD = 0.14
UNPLATED_PAD = 0.05

# (logical base name, base side)
SQUARE_TILES = [
    ("Square44x44Logo", 44),
    ("Square71x71Logo", 71),
    ("Square150x150Logo", 150),
    ("Square310x310Logo", 310),
    ("StoreLogo", 50),
]


def _save(img: Image.Image, name: str) -> str:
    img.save(os.path.join(ASSETS, name), format="PNG")
    return name


def build_all() -> None:
    os.makedirs(ASSETS, exist_ok=True)
    written: list[str] = []

    # Square tiles at every scale (plated => generous safe-zone).
    for base_name, base in SQUARE_TILES:
        for scale, factor in SCALES.items():
            px = round(base * factor)
            written.append(_save(glyph(px, TILE_PAD), f"{base_name}.scale-{scale}.png"))
    # StoreLogo also gets an unqualified fallback (matches the manifest <Logo>).
    written.append(_save(glyph(50, TILE_PAD), "StoreLogo.png"))

    # Square44x44 target sizes: plated + unplated + light-unplated (taskbar/title bar).
    for ts in TARGET_SIZES:
        written.append(_save(glyph(ts, TILE_PAD), f"Square44x44Logo.targetsize-{ts}.png"))
        written.append(_save(glyph(ts, UNPLATED_PAD, simple=ts <= 24),
                              f"Square44x44Logo.targetsize-{ts}_altform-unplated.png"))
        written.append(_save(glyph(ts, UNPLATED_PAD, theme="light", simple=ts <= 24),
                              f"Square44x44Logo.targetsize-{ts}_altform-lightunplated.png"))

    # Wide tile (2.066:1) and splash (2.066:1) banners at every scale.
    for scale, factor in SCALES.items():
        w, h = round(310 * factor), round(150 * factor)
        written.append(_save(banner(w, h, TILE_PAD, 0.92), f"Wide310x150Logo.scale-{scale}.png"))
        sw, sh = round(620 * factor), round(300 * factor)
        written.append(_save(banner(sw, sh, TILE_PAD, 0.66), f"SplashScreen.scale-{scale}.png"))

    # Multi-size .ico for the Win32 window / title-bar icon.
    ico_imgs = [glyph(s, UNPLATED_PAD, simple=s <= 28) for s in (16, 20, 24, 32, 48, 64, 128, 256)]
    save_ico(ico_imgs, os.path.join(ASSETS, "AppIcon.ico"))
    written.append("AppIcon.ico")

    print(f"Wrote {len(written)} files to {ASSETS}")
    for name in sorted(written):
        print("  ", name)


def build_preview(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    sizes = (256, 128, 64, 48, 32, 24, 16)
    for bg_name, bg in (("dark", (32, 32, 32, 255)), ("light", (243, 243, 243, 255))):
        pad = 24
        total = sum(sizes) + pad * (len(sizes) + 1)
        sheet = Image.new("RGBA", (total, 256 + 2 * pad), bg)
        x = pad
        for s in sizes:
            theme = "light" if bg_name == "light" else "dark"
            g = glyph(s, UNPLATED_PAD, theme=theme, simple=s <= 28)
            sheet.alpha_composite(g, (x, pad + (256 - s)))
            x += s + pad
        sheet.convert("RGB").save(os.path.join(out_dir, f"preview_{bg_name}.png"))
    for bg_name, bg in (("dark", (32, 32, 32, 255)), ("light", (243, 243, 243, 255))):
        hero = Image.new("RGBA", (320, 320), bg)
        hero.alpha_composite(glyph(288, TILE_PAD), (16, 16))
        hero.convert("RGB").save(os.path.join(out_dir, f"hero_{bg_name}.png"))
    print(f"Wrote preview sheets to {out_dir}")


def main() -> None:
    global WORDMARK
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--preview", metavar="DIR", nargs="?", const=os.path.join(HERE, "_preview"),
                    help="write inspection contact sheets instead of the asset matrix")
    ap.add_argument("--style", choices=("band", "body", "none"), default=WORDMARK,
                    help="how to render the 'PDF' wordmark (default: %(default)s)")
    args = ap.parse_args()
    WORDMARK = args.style
    if args.preview:
        build_preview(args.preview)
    else:
        build_all()


if __name__ == "__main__":
    main()
