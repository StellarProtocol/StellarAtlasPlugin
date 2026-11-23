"""Map marker tiles for Atlas points on the game's own big map (M) and minimap, in the game's flora-icon style:
rounded square (radius 5/32 of the size), 2 px dark border #2a2118, fill = the point's colour, white glyph centred.
Chests get a chest glyph with a keyhole cut out in the fill colour; every other group a white dot.

Redrawn PER SIZE (house icon rule, Stellar-Design-Language.md section 3 — never one master scaled down): the border is a
whole number of pixels on pixel boundaries, glyph rectangles snap to the pixel grid, curves stay anti-aliased (rendered
at 16x and box-filtered — never crispEdges). Coordinate maths uses floor(v + 0.5), never round() (banker's rounding).

Visible square: the game's flora icon (mapicon_collect_unlock) leaves transparent margin around its art, and a
full-bleed tile drew ~1.45x bigger than it in game (2026-11-23). So the tile is drawn INSIDE the PNG at VISIBLE = 1/1.45
of the side, centred, with transparent padding (the unit's RectTransform stays untouched); the inner side is snapped
so the margin is a whole number of pixels on each side.

Sizes: a map flag unit draws at ~42 px at 1440p (scene-tag IconRawSize default 70x70 design units) → 32 (1080p),
42 (1440p), 64 (1600p-ish / spare), 96 (4K). The plugin picks the next larger for the current screen height.

Usage:  python -I make-map-icons.py <out_dir> [--contact <sheet.png>]
"""
import math
import sys

from PIL import Image, ImageDraw

SIZES = (32, 42, 64, 96)
SS = 16
BORDER = (0x2a, 0x21, 0x18, 255)
WHITE = (255, 255, 255, 255)

# name → (fill, glyph). Names match the C# Chip enum (lower-case).
TILES = {
    "common":      (0x98a2a7, "chest"),
    "exquisite":   (0xc8763a, "chest"),
    "luxurious":   (0xf2bf1a, "chest"),
    "otherchests": (0xd9604a, "chest"),
    "herbs":       (0x8ce68c, "dot"),
    "ore":         (0xb385ff, "dot"),
    "fishing":     (0x66d9ff, "dot"),
    "npc":         (0xf0a8d0, "dot"),
    "monster":     (0xff6b6b, "dot"),
    "reading":     (0xd9c48c, "dot"),
    "traversal":   (0x7fe0c8, "dot"),
    "teleport":    (0xa0a8ff, "dot"),
    "activity":    (0xffb366, "dot"),
    "engram":      (0xe6e66b, "dot"),
    "resonance":   (0xc79bff, "dot"),
    "other":       (0xb0b0b0, "dot"),
}


def rnd(v: float) -> int:
    return math.floor(v + 0.5)


def rgb(c: int):
    return ((c >> 16) & 0xff, (c >> 8) & 0xff, c & 0xff, 255)


VISIBLE = 1 / 1.45


def draw(size: int, fill: int, glyph: str) -> Image.Image:
    inner = rnd(size * VISIBLE)
    if (size - inner) % 2:
        inner += 1                                  # equal whole-pixel margins
    tile = draw_tile(inner, fill, glyph)
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    off = (size - inner) // 2
    out.alpha_composite(tile, (off, off))
    return out


def draw_tile(size: int, fill: int, glyph: str) -> Image.Image:
    S = size * SS
    im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    b = max(2, rnd(size / 21))                      # border: 2 px up to ~52 px of tile, 3 above
    r = size * 5 / 32
    d.rounded_rectangle([0, 0, S - 1, S - 1], radius=r * SS, fill=BORDER)
    d.rounded_rectangle([b * SS, b * SS, S - 1 - b * SS, S - 1 - b * SS], radius=max(1.0, r - b) * SS, fill=rgb(fill))

    def px(v: float) -> int:                        # grid-snapped coordinate in supersampled units
        return rnd(v) * SS

    if glyph == "chest":
        # chest on the inner area: lid (rounded top) + body, a dark-free white glyph with a keyhole in the fill colour
        x0, x1 = px(size * 0.24), px(size * 0.76)
        lid0, lid1 = px(size * 0.27), px(size * 0.45)
        body1 = px(size * 0.74)
        gap = max(1, rnd(size / 32)) * SS              # the lid/body seam, in the fill colour
        d.rounded_rectangle([x0, lid0, x1, lid1], radius=(x1 - x0) * 0.22, fill=WHITE)
        d.rectangle([x0, lid1 - (lid1 - lid0) // 2, x1, lid1], fill=WHITE)   # square off the lid's bottom corners
        d.rectangle([x0, lid1 + gap, x1, body1], fill=WHITE)
        # keyhole: circle + slot, centred on the seam
        cx = (x0 + x1) / 2
        kr = max(1.5, size * 0.065) * SS
        ky = lid1 + gap + kr * 1.25
        d.ellipse([cx - kr, ky - kr, cx + kr, ky + kr], fill=rgb(fill))
        sw = kr * 0.9
        d.polygon([(cx - sw / 2, ky), (cx + sw / 2, ky), (cx + sw * 0.8, ky + kr * 2.0), (cx - sw * 0.8, ky + kr * 2.0)], fill=rgb(fill))
    else:
        cr = size * 0.18 * SS
        c = S / 2
        d.ellipse([c - cr, c - cr, c + cr, c + cr], fill=WHITE)

    return im.resize((size, size), Image.BOX)


def contact(rows, path: str) -> None:
    zoom, pad = 3, 8
    width = sum(s * zoom + pad for s in SIZES) + pad
    height = len(rows) * (max(SIZES) * zoom + pad) + pad
    sheet = Image.new("RGBA", (width, height), (60, 70, 60, 255))
    y = pad
    for images in rows:
        x = pad
        for s, im in zip(SIZES, images):
            sheet.alpha_composite(im.resize((s * zoom, s * zoom), Image.NEAREST), (x, y))
            x += s * zoom + pad
        y += max(SIZES) * zoom + pad
    sheet.save(path)


def main() -> None:
    out = sys.argv[1]
    rows = []
    for name, (fill, glyph) in TILES.items():
        images = []
        for s in SIZES:
            im = draw(s, fill, glyph)
            im.save(f"{out}/map-{name}-{s}.png")
            images.append(im)
        if glyph == "chest" or name == "herbs":
            rows.append(images)
    if "--contact" in sys.argv:
        contact(rows, sys.argv[sys.argv.index("--contact") + 1])


if __name__ == "__main__":
    main()
