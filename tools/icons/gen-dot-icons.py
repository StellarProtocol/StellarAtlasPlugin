"""SHOW chip dots: a SOLID dot per chip colour (chip ON) and one HOLLOW ring (chip OFF), drawn as images because the UI
font has no reliable dot / ring glyph (WindowBuilder-Patterns: "●" draws blank on the TMP path).

The dot is 8 canvas units: 6 px at 1080p, 8 at 1440p, 12 at 4K (+ 10 / 16 spares); pre-drawn per size (house icon rule,
Stellar-Design-Language.md section 3), anti-aliased circles rendered at 16x and box-filtered, white-free colours baked
in. The ring is the house Muted tone (#8a7a92) with a whole-pixel stroke (1 px up to 10, 2 above).

Usage:  python -I gen-dot-icons.py <out_dir>
"""
import math
import sys

from PIL import Image, ImageDraw

SIZES = (6, 8, 10, 12, 16)
SS = 16
DOTS = {
    "common":      0x98a2a7,
    "exquisite":   0xc8763a,
    "luxurious":   0xf2bf1a,
    "otherchests": 0xd9604a,
    "books":       0xe0c98a,
}
RING = 0x8a7a92


def rnd(v: float) -> int:
    return math.floor(v + 0.5)


def rgb(c: int):
    return ((c >> 16) & 0xff, (c >> 8) & 0xff, c & 0xff)


def circle(size: int, color: int, stroke: int = 0) -> Image.Image:
    S = size * SS
    a = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(a)
    d.ellipse([0, 0, S - 1, S - 1], fill=255)
    if stroke:
        w = stroke * SS
        d.ellipse([w, w, S - 1 - w, S - 1 - w], fill=0)
    a = a.resize((size, size), Image.BOX)
    im = Image.new("RGBA", (size, size), rgb(color) + (0,))
    im.putalpha(a)
    return im


def main() -> None:
    out = sys.argv[1]
    for s in SIZES:
        for name, c in DOTS.items():
            circle(s, c).save(f"{out}/dot-{name}-{s}.png")
        circle(s, RING, stroke=1 if s <= 10 else 2).save(f"{out}/ring-{s}.png")


if __name__ == "__main__":
    main()
