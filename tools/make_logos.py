"""Builds src/logos/*.png, the game-version logos in the top bar.

Every logo is scaled to the same area (so wide and tall logos look equally big) and centred on the same transparent
canvas (so switching game versions never changes the layout).

Usage: python tools/make_logos.py <folder with the downloaded logos>
Source files are the 480px thumbnails of these warcraft.wiki.gg files.
"""
import math
import os
import sys
from PIL import Image

NAMES = {'WoW_Forever_Logo.png': 'forever.png', 'Midnight_2024_logo.png': 'retail.png',
         'WoW_BC_Classic_Anniversary_logo.png': 'anniversary.png', 'WoW_MoP_Classic_logo.png': 'classic.png',
         'WoW_Classic_Hardcore.png': 'era.png'}

SCALE = 2                           # pixels per display pixel
CANVAS = (126 * SCALE, 72 * SCALE)  # shown at 126x72 in MainWindow.xaml

src = sys.argv[1]
dst = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'src', 'logos')

logos = {}
for s, d in NAMES.items():
    im = Image.open(os.path.join(src, s)).convert('RGBA')
    logos[d] = im.crop(im.getchannel('A').point(lambda a: 255 if a > 8 else 0).getbbox())

# Largest area that still lets every logo fit the canvas.
area = min(min(CANVAS[1] ** 2 * (im.width / im.height), CANVAS[0] ** 2 / (im.width / im.height))
           for im in logos.values())

for d, im in logos.items():
    aspect = im.width / im.height
    h = math.sqrt(area / aspect)
    im = im.resize((round(aspect * h), round(h)), Image.LANCZOS)
    canvas = Image.new('RGBA', CANVAS, (0, 0, 0, 0))
    canvas.alpha_composite(im, ((CANVAS[0] - im.width) // 2, (CANVAS[1] - im.height) // 2))
    canvas.save(os.path.join(dst, d), optimize=True)
    print(d, im.size)
