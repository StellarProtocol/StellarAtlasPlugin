"""Atlas glyph: a folded map (three panels, the approved prototype's 24-grid path), white flat on transparent; the
accent tile behind it is drawn by the plugin. Redrawn PER SIZE (house icon rule, Stellar-Design-Language.md section 3:
never one master scaled down).

Hinting: the stroke is a whole number of pixels and every VERTICAL edge (the outer sides and the two folds) is snapped
so its ink starts and ends on pixel boundaries; the zig-zag top/bottom edges are diagonals and stay anti-aliased
(SDL "Icon edges must stay anti-aliased": never crispEdges). Rendered at 16x and box-filtered down, white RGB on every
pixel so no dark fringe. Coordinate maths uses floor(v + 0.5), never round() (banker's rounding gotcha).

Usage:  python -I gen-atlas-icon.py <out_dir> [--contact <sheet.png>]
"""
import math
import sys

from PIL import Image, ImageDraw

# header glyph = 24 canvas units (18 @1080p, 24 @1440p, 36 @4K); launcher = 32 units (24/32/48/64); 16 + 128 spares.
SIZES = (16, 18, 24, 32, 36, 48, 64, 128)
SS = 16


def rnd(v: float) -> int:
    return math.floor(v + 0.5)


def draw(size: int) -> Image.Image:
    u = size / 24.0
    w = max(2, rnd(2 * u))                       # stroke in whole pixels
    half = w / 2.0

    def snap_x(x24: float) -> float:
        # centre of a vertical stroke so that [c - w/2, c + w/2] lands on pixel boundaries
        left = rnd(x24 * u - half)
        return left + half

    def y(y24: float) -> float:
        return y24 * u

    xs = [snap_x(3), snap_x(9), snap_x(15), snap_x(21)]
    # outline: M9 4 L3 6 V20 L9 18 L15 20 L21 18 V4 L15 6 Z  (+ folds x=9 4..18, x=15 6..20)
    outline = [(xs[1], y(4)), (xs[0], y(6)), (xs[0], y(20)), (xs[1], y(18)), (xs[2], y(20)), (xs[3], y(18)),
               (xs[3], y(4)), (xs[2], y(6)), (xs[1], y(4))]
    big = Image.new("L", (size * SS, size * SS), 0)
    d = ImageDraw.Draw(big)
    lw = w * SS
    pts = [(x * SS, yy * SS) for x, yy in outline]
    d.line(pts, fill=255, width=lw, joint="curve")
    for x, y0, y1 in ((xs[1], 4, 18), (xs[2], 6, 20)):
        d.line([(x * SS, y(y0) * SS), (x * SS, y(y1) * SS)], fill=255, width=lw)
    # round caps at the joints so the corners read soft like the prototype's stroke-linejoin=round
    r = lw / 2
    for x, yy in pts:
        d.ellipse([x - r, yy - r, x + r, yy + r], fill=255)
    alpha = big.resize((size, size), Image.BOX)
    im = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    im.putalpha(alpha)
    return im


def contact(images, path: str) -> None:
    zoom, pad = 4, 8
    width = sum(s * zoom + pad for s in SIZES) + pad
    height = max(SIZES) * zoom + 2 * pad
    sheet = Image.new("RGBA", (width, height), (35, 41, 47, 255))
    x = pad
    for s, im in zip(SIZES, images):
        tile = Image.new("RGBA", (s, s), (200, 80, 143, 255))
        tile.alpha_composite(im)
        sheet.paste(tile.resize((s * zoom, s * zoom), Image.NEAREST), (x, pad))
        x += s * zoom + pad
    sheet.save(path)


def main() -> None:
    out = sys.argv[1]
    images = []
    for s in SIZES:
        im = draw(s)
        im.save(f"{out}/atlas-{s}.png")
        images.append(im)
    if "--contact" in sys.argv:
        contact(images, sys.argv[sys.argv.index("--contact") + 1])


if __name__ == "__main__":
    main()
