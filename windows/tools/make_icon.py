"""Linux sürümündeki ekolayzer.svg tasarımını PIL ile çizip çok boyutlu .ico üretir.

Kullanım: python tools/make_icon.py app/ekolayzer.ico
"""
import sys

from PIL import Image, ImageDraw

SS = 8  # süper örnekleme


def bezier(p0, p1, p2, p3, n=40):
    pts = []
    for i in range(n + 1):
        t = i / n
        u = 1 - t
        pts.append((u ** 3 * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t ** 3 * p3[0],
                    u ** 3 * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t ** 3 * p3[1]))
    return pts


def draw(size):
    s = size * SS / 128.0
    big = size * SS
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))

    # degrade zemin (#3584e4 -> #1a5fb4), yuvarlatılmış kare
    grad = Image.new("RGBA", (big, big))
    gd = ImageDraw.Draw(grad)
    for y in range(big):
        t = y / max(big - 1, 1)
        c = tuple(round(a + (b - a) * t) for a, b in zip((0x35, 0x84, 0xE4), (0x1A, 0x5F, 0xB4)))
        gd.line([(0, y), (big, y)], fill=c + (255,))
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle([8 * s, 8 * s, 120 * s, 120 * s], radius=24 * s, fill=255)
    img.paste(grad, (0, 0), mask)

    over = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(over)
    w = 6 * s
    for x in (34, 54, 74, 94):  # kaydırıcı rayları
        d.line([(x * s, 28 * s), (x * s, 100 * s)], fill=(255, 255, 255, 90), width=round(w))
        for y in (28, 100):
            d.ellipse([x * s - w / 2, y * s - w / 2, x * s + w / 2, y * s + w / 2], fill=(255, 255, 255, 90))
    img.alpha_composite(over)

    d = ImageDraw.Draw(img)
    for x, y in ((24, 70), (44, 40), (64, 58), (84, 34)):  # kaydırıcı başları
        d.rounded_rectangle([x * s, y * s, (x + 20) * s, (y + 10) * s], radius=5 * s, fill=(255, 255, 255, 255))

    # sarı yanıt eğrisi: M20 86 C40 74,46 40,58 46 S74 70,84 60 S100 36,108 38
    pts = bezier((20, 86), (40, 74), (46, 40), (58, 46))
    pts += bezier((58, 46), (70, 52), (74, 70), (84, 60))[1:]
    pts += bezier((84, 60), (94, 50), (100, 36), (108, 38))[1:]
    pts = [(x * s, y * s) for x, y in pts]
    lw = 4 * s
    d.line(pts, fill=(0xF6, 0xD3, 0x2D, 255), width=round(lw), joint="curve")
    for x, y in (pts[0], pts[-1]):
        d.ellipse([x - lw / 2, y - lw / 2, x + lw / 2, y + lw / 2], fill=(0xF6, 0xD3, 0x2D, 255))
    return img.resize((size, size), Image.LANCZOS)


def main():
    out = sys.argv[1]
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    imgs = [draw(n) for n in sizes]
    imgs[-1].save(out, format="ICO", sizes=[(n, n) for n in sizes], append_images=imgs[:-1])
    print(out)


if __name__ == "__main__":
    main()
