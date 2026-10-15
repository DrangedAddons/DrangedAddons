"""Draws the DrangedAddons app icon: an original black cat head on a gold rounded tile."""
import sys
from PIL import Image, ImageDraw

S = 1024  # draw large, then downscale for each icon size
GOLD = (212, 167, 44, 255)
DARK = (22, 24, 29, 255)


def draw(size=S):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    u = size / 1024

    def p(*pts):
        return [(x * u, y * u) for x, y in pts]

    # Gold rounded tile
    d.rounded_rectangle((40 * u, 40 * u, 984 * u, 984 * u), radius=200 * u, fill=GOLD)

    # Cat head: ears + round face
    d.polygon(p((250, 470), (270, 170), (470, 330)), fill=DARK)   # left ear
    d.polygon(p((774, 470), (754, 170), (554, 330)), fill=DARK)   # right ear
    d.ellipse((215 * u, 270 * u, 809 * u, 830 * u), fill=DARK)    # face

    # Inner ears
    d.polygon(p((290, 400), (300, 240), (420, 335)), fill=(60, 50, 20, 255))
    d.polygon(p((734, 400), (724, 240), (604, 335)), fill=(60, 50, 20, 255))

    # Eyes (gold almonds with dark slit pupils)
    for cx in (395, 629):
        d.ellipse(((cx - 70) * u, 470 * u, (cx + 70) * u, 590 * u), fill=GOLD)
        d.ellipse(((cx - 16) * u, 478 * u, (cx + 16) * u, 582 * u), fill=DARK)

    # Nose
    d.polygon(p((480, 650), (544, 650), (512, 690)), fill=(150, 110, 30, 255))
    return img


def main(out_ico, out_png):
    big = draw()
    big.save(out_png)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    big.save(out_ico, format="ICO", sizes=[(s, s) for s in sizes])


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
