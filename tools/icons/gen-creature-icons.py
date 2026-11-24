"""Magical creature art for Atlas (approved prototype _scratch/prototypes/atlas-creatures.tpl.html), pre-drawn per size
(house icon rule, Stellar-Design-Language.md section 3 / feedback "icons pre-drawn per size": never one master scaled
down at runtime). Source = the game's own 128 px round head icons (<src_dir>/<id>.png, passed on the command line).

Window art (Resources/creatures):
  cre-<id>-<px>.png       head cropped to a circle on the dark disc #1a141e (SHOW chips: 16 units, list rows: 30 units)
  cre-<id>-dim-<px>.png   the same, desaturated (60 %) at 45 % opacity (chip OFF / Absent row, prototype .chip:not(.on))

Map art (Resources/creatures) — the unit canvas is the chest tiles' (32 / 42 / 64 / 96, tools/make-map-icons.py):
  mapc-<id>-here-<px>.png   head disc in a 2 px dark ring #2a2118 + a green ring #5fd38d with a soft green halo
                            (alive on your line)
  mapc-<id>-other-<px>.png  head disc in the dark ring, FULL opacity, no desaturation (alive on other lines; in-game
                            2026-11-24: the old 55 % faded tile was "too transparent" on the map art)
  mapc-<id>-absent-<px>.png head disc in the dark ring at 80 % opacity, desaturated 30 % (absent fixed-schedule boarlet)
  mapc-glyphs-<px>.png      white badge glyphs "0123456789·:+" in fixed cells (CELL_W[px] wide) for the line badge
                            and the next-spawn time label. The plugin trims each cell's empty columns and COMPOSITES
                            these pixels onto the tile (no scaling) — MapMarkers.CreatureTiles.cs.
The disc sits at the top centre of the canvas and leaves the bottom band free for the badge / time pill, which the
plugin draws over the canvas bottom (they may overlap the disc's lower edge, as in the prototype).

Coordinate maths uses floor(v + 0.5), never round() (banker's rounding). Curves anti-aliased at 16x, box-filtered.

Usage:  python -I gen-creature-icons.py <src_dir_with_109xx.png> <out_dir> [--contact <sheet.png>]
"""
import math
import sys

from PIL import Image, ImageDraw, ImageEnhance, ImageFont

IDS = (10900, 10901, 10902, 10903, 10904)
UI_SIZES = (12, 16, 20, 24, 30, 32, 40, 48, 60, 64, 80)
MAP_SIZES = (32, 42, 64, 96)
SS = 16
DISC_BG = (0x1a, 0x14, 0x1e, 255)
RING_DARK = (0x2a, 0x21, 0x18, 255)
RING_GREEN = (0x5f, 0xd3, 0x8d, 255)
DISC = 0.78                 # head disc diameter / map canvas
GLYPHS = "0123456789\u00b7:+"
GLYPH_FONT = {32: 9, 42: 11, 64: 16, 96: 23}     # px font size of the badge digits per map canvas
FONT = "C:/Windows/Fonts/segoeuib.ttf"


def rnd(v: float) -> int:
    return math.floor(v + 0.5)


def circle_mask(size: int, inset: float = 0.0) -> Image.Image:
    S = size * SS
    a = Image.new("L", (S, S), 0)
    ImageDraw.Draw(a).ellipse([inset * SS, inset * SS, S - 1 - inset * SS, S - 1 - inset * SS], fill=255)
    return a.resize((size, size), Image.BOX)


def head(src: Image.Image, size: int) -> Image.Image:
    """The head cropped to a circle on the dark disc, `size` px (Lanczos from the 128 px source = drawn for this size)."""
    art = src.resize((size, size), Image.LANCZOS)
    disc = Image.new("RGBA", (size, size), DISC_BG)
    disc.alpha_composite(art)
    disc.putalpha(circle_mask(size))
    return disc


def dim(im: Image.Image, sat: float, alpha: float) -> Image.Image:
    a = im.getchannel("A").point(lambda v: rnd(v * alpha))
    rgb = ImageEnhance.Color(im.convert("RGB")).enhance(sat)
    out = rgb.convert("RGBA")
    out.putalpha(a)
    return out


def ring_layer(size: int, outer: float, width: float, color) -> Image.Image:
    """An anti-aliased ring of `width` px whose outer diameter is `outer`, centred in a `size` canvas."""
    S = size * SS
    a = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(a)
    o0 = (size - outer) / 2 * SS
    d.ellipse([o0, o0, S - 1 - o0, S - 1 - o0], fill=255)
    i0 = o0 + width * SS
    d.ellipse([i0, i0, S - 1 - i0, S - 1 - i0], fill=0)
    a = a.resize((size, size), Image.BOX)
    layer = Image.new("RGBA", (size, size), color)
    layer.putalpha(a.point(lambda v: rnd(v * color[3] / 255)))
    return layer


def map_tile(src: Image.Image, n: int, kind: str) -> Image.Image:
    here = kind == "here"
    d = rnd(n * DISC)
    if d % 2 != n % 2:
        d += 1                                       # whole-pixel, equal side margins
    b = max(2, rnd(n / 21))                         # dark ring: 2 px up to ~52 px canvas (as the chest tiles)
    g = max(1, rnd(n / 32)) if here else 0          # green ring inside the dark ring's outer edge
    inner = d - 2 * b
    tile = Image.new("RGBA", (d, d), (0, 0, 0, 0))
    if here:
        tile.alpha_composite(ring_layer(d, d, b, RING_GREEN))
    else:
        tile.alpha_composite(ring_layer(d, d, b, RING_DARK))
    h = head(src, inner)
    tile.alpha_composite(h, (b, b))
    if here and g:
        tile.alpha_composite(ring_layer(d, d - 2 * b + 2 * g, g, RING_DARK))   # a thin dark line keeps the art apart
    out = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    off = (n - d) // 2
    hw = max(1, rnd(n / 21))                        # halo width (prototype: 2 px green @45 % around the ring)
    top = hw + max(0, rnd(n * 0.01))
    if here:
        halo = ring_layer(d + 2 * hw, d + 2 * hw, hw, (0x5f, 0xd3, 0x8d, 115))
        out.alpha_composite(halo, (off - hw, top - hw))
    out.alpha_composite(dim(tile, 0.7, 0.8) if kind == "absent" else tile, (off, top))
    return out


def glyph_strip(n: int):
    size = GLYPH_FONT[n]
    font = ImageFont.truetype(FONT, size)
    asc, desc = font.getmetrics()
    cell_w = rnd(size * 0.75)
    h = rnd(size * 0.80)                            # cap height band (digits) + a pixel of AA
    strip = Image.new("L", (cell_w * len(GLYPHS), h), 0)
    d = ImageDraw.Draw(strip)
    for i, ch in enumerate(GLYPHS):
        bbox = font.getbbox("0")
        top = bbox[1]
        w = font.getlength(ch)
        x = i * cell_w + (cell_w - w) / 2
        d.text((rnd(x), -top + max(0, rnd((h - (bbox[3] - bbox[1])) / 2))), ch, font=font, fill=255)
    out = Image.new("RGBA", strip.size, (255, 255, 255, 0))
    out.putalpha(strip)
    return out


def contact(rows, path: str) -> None:
    zoom, pad = 3, 8
    width = sum(im.width * zoom + pad for im in rows[0]) + pad
    height = sum(max(im.height for im in r) * zoom + pad for r in rows) + pad
    sheet = Image.new("RGBA", (max(width, 900), height), (70, 80, 70, 255))
    y = pad
    for r in rows:
        x = pad
        for im in r:
            sheet.alpha_composite(im.resize((im.width * zoom, im.height * zoom), Image.NEAREST), (x, y))
            x += im.width * zoom + pad
        y += max(im.height for im in r) * zoom + pad
    sheet.save(path)


def main() -> None:
    src_dir, out = sys.argv[1], sys.argv[2]
    rows = []
    for cid in IDS:
        src = Image.open(f"{src_dir}/{cid}.png").convert("RGBA")
        for s in UI_SIZES:
            h = head(src, s)
            h.save(f"{out}/cre-{cid}-{s}.png")
            dim(h, 0.4, 0.45).save(f"{out}/cre-{cid}-dim-{s}.png")
        row = []
        for n in MAP_SIZES:
            tiles = [map_tile(src, n, k) for k in ("here", "other", "absent")]
            for k, im in zip(("here", "other", "absent"), tiles):
                im.save(f"{out}/mapc-{cid}-{k}-{n}.png")
            row += tiles
        rows.append(row)
    for n in MAP_SIZES:
        glyph_strip(n).save(f"{out}/mapc-glyphs-{n}.png")
    if "--contact" in sys.argv:
        contact(rows, sys.argv[sys.argv.index("--contact") + 1])


if __name__ == "__main__":
    main()
