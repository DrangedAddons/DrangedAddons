"""DrangedAddons app icon: the Penrose triangle template in two golds on a black rounded tile, as large as fits.

Usage: python tools/make_icon.py tools/icon-template.png src/app.ico <preview.png> [three|two]

The template (transparent background) is three separate pieces. Each piece runs along the outside of one side and
folds round a corner into the inside of the next, so it is split along the corners' fold lines (corner → centre):
the outer faces are bright gold and the inner faces a darker gold, which gives the depth.
"""
import math
import sys

import numpy as np
from PIL import Image, ImageChops, ImageDraw

S = 1024
SS = 4                      # supersampling factor for smooth edges
TILE = (40, 40, 984, 984)   # same tile as the addon icons
RADIUS = 200
PAD = 30                    # keep the triangle this far inside the tile edge
GOLD = (212, 167, 44, 255)
DARK_GOLD = (160, 120, 24, 255)
BLACK = (0, 0, 0, 255)
THREE_TONES = [(236, 194, 78, 255), (212, 167, 44, 255), (150, 112, 22, 255)]
MODE = "three"  # "three": one tone per piece; "two": lit outer faces, shaded inner faces


def components(alpha):
    """Split the template's opaque area into its separate pieces (4-connected), largest first."""
    a = np.array(alpha) >= 128
    labels = np.zeros(a.shape, dtype=int)
    count = 0
    for y, x in zip(*np.nonzero(a)):
        if labels[y, x]:
            continue
        count += 1
        stack = [(y, x)]
        labels[y, x] = count
        while stack:
            cy, cx = stack.pop()
            for ny, nx in ((cy + 1, cx), (cy - 1, cx), (cy, cx + 1), (cy, cx - 1)):
                if 0 <= ny < a.shape[0] and 0 <= nx < a.shape[1] and a[ny, nx] and not labels[ny, nx]:
                    labels[ny, nx] = count
                    stack.append((ny, nx))
    # Give the soft edge pixels of each piece back to it so the edges stay anti-aliased.
    pieces = []
    raw = np.array(alpha)
    for i in range(1, count + 1):
        own = labels == i
        grown = own.copy()
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            grown |= np.roll(own, (dy, dx), axis=(0, 1))
        pieces.append(Image.fromarray(np.where(grown & ~(labels > 0) | own, raw, 0).astype(np.uint8)))
    return pieces


def rounded(box, radius, scale=1):
    m = Image.new("L", (S * scale, S * scale), 0)
    ImageDraw.Draw(m).rounded_rectangle([v * scale for v in box], radius=radius * scale, fill=255)
    return m


def place(alpha, bbox, size_px):
    """alpha (cropped to the template bbox) scaled to size_px wide, centred, re-thresholded, at SS x resolution."""
    alpha = alpha.crop(bbox)
    w, h = alpha.size
    big_w, big_h = round(size_px * SS), round(size_px * h / w * SS)
    big = alpha.resize((big_w, big_h), Image.BICUBIC).point(lambda v: 255 if v >= 128 else 0)
    canvas = Image.new("L", (S * SS, S * SS), 0)
    canvas.paste(big, ((S * SS - big_w) // 2, (S * SS - big_h) // 2))
    return canvas


def fold_sectors(mask):
    """Three wedges around the centre, bounded by the fold lines (centre → each outer corner)."""
    m = np.array(mask) > 0
    ys, xs = np.nonzero(m)
    cx, cy = xs.mean(), ys.mean()
    angles = np.arctan2(ys - cy, xs - cx)
    dist = np.hypot(xs - cx, ys - cy)
    corners = []
    for k in range(3):  # the farthest point in each third of the circle is a corner
        lo = -math.pi + k * 2 * math.pi / 3 - math.pi / 2
        sel = ((angles - lo) % (2 * math.pi)) < 2 * math.pi / 3
        i = np.argmax(np.where(sel, dist, -1))
        corners.append(math.atan2(ys[i] - cy, xs[i] - cx))
    corners.sort()
    far = S * SS * 2
    sectors = []
    for k in range(3):
        a0, a1 = corners[k], corners[(k + 1) % 3] + (2 * math.pi if k == 2 else 0)
        steps = [a0 + (a1 - a0) * t / 32 for t in range(33)]
        wedge = Image.new("L", mask.size, 0)
        ImageDraw.Draw(wedge).polygon([(cx, cy)] + [(cx + far * math.cos(a), cy + far * math.sin(a)) for a in steps], fill=255)
        sectors.append(wedge)
    return sectors, (cx, cy)


def main(template, out_ico, out_png):
    alpha = Image.open(template).getchannel("A")
    bbox = alpha.getbbox()
    inner = rounded((TILE[0] + PAD, TILE[1] + PAD, TILE[2] - PAD, TILE[3] - PAD), max(RADIUS - PAD, 0), SS)
    lo, hi = 100.0, float(S)
    for _ in range(18):  # binary search for the largest size that stays inside the tile
        mid = (lo + hi) / 2
        if ImageChops.subtract(place(alpha, bbox, mid), inner).getbbox() is None:
            lo = mid
        else:
            hi = mid
    whole = place(alpha, bbox, lo)
    sectors, (cx, cy) = fold_sectors(whole)

    big = Image.new("RGBA", (S * SS, S * SS), (0, 0, 0, 0))
    big.paste(Image.new("RGBA", big.size, BLACK), (0, 0), rounded(TILE, RADIUS, SS))
    if MODE == "three":
        # One tone per piece: bright, mid and deep gold, so the three bars read as separate objects.
        for piece, tone in zip(components(alpha), THREE_TONES):
            big.paste(Image.new("RGBA", big.size, tone), (0, 0), place(piece, bbox, lo))
        return finish(big, out_ico, out_png, lo, alpha)

    gold, dark = Image.new("RGBA", big.size, GOLD), Image.new("RGBA", big.size, DARK_GOLD)
    for piece in components(alpha):
        piece_mask = place(piece, bbox, lo)
        parts = [ImageChops.multiply(piece_mask, s) for s in sectors]
        parts = [p for p in parts if p.getbbox()]
        # The part farther from the centre is the outer face (lit); the nearer one is the inner face (shaded).
        def mean_dist(p):
            ys, xs = np.nonzero(np.array(p.resize((S, S))) > 0)
            return np.hypot(xs * SS - cx, ys * SS - cy).mean() if len(xs) else 0
        parts.sort(key=mean_dist, reverse=True)
        for i, part in enumerate(parts):
            big.paste(gold if i == 0 else dark, (0, 0), part)
    return finish(big, out_ico, out_png, lo, alpha)


def finish(big, out_ico, out_png, lo, alpha):
    icon = big.resize((S, S), Image.LANCZOS)
    icon.save(out_png)
    icon.save(out_ico, format="ICO", sizes=[(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
    print(f"triangle width {lo / (TILE[2] - TILE[0]):.0%} of the tile; {len(components(alpha))} pieces")


if __name__ == "__main__":
    if len(sys.argv) > 4:
        MODE = sys.argv[4]
    main(*sys.argv[1:4])
