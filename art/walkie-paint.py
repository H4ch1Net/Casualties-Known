"""The walkie-talkie for the main menu, painted in the menu's own style: smooth shapes,
a thick dark outline, soft moonlit shading (the moon is up and to the right), a rim light,
glowing screen and LED. Drawn 4x larger and scaled down for clean edges."""
import sys
from PIL import Image, ImageDraw, ImageFilter, ImageChops

S = 4                       # supersampling
W, H = 100, 184             # final size (menu pixels)
OUT = (20, 16, 32, 255)     # outline: the menu's near-black purple
INNER = (26, 22, 40, 255)   # lines between faces

def big(v): return int(round(v * S))

def box(x0, y0, x1, y1): return [big(x0), big(y0), big(x1), big(y1)]

def mask():
    return Image.new("L", (W * S, H * S), 0)

def poly(points): return [(big(x), big(y)) for x, y in points]

def gradient(m, top, bottom, left_dark=0.0):
    """A vertical gradient (top -> bottom colours) inside mask m, darker to the left by left_dark."""
    w, h = m.size
    g = Image.new("RGBA", (w, h))
    px = g.load()
    for y in range(h):
        t = y / (h - 1)
        base = [top[i] + (bottom[i] - top[i]) * t for i in range(3)]
        for x in range(w):
            k = 1.0 - left_dark * (1.0 - x / (w - 1))
            px[x, y] = (int(base[0] * k), int(base[1] * k), int(base[2] * k), 255)
    out = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    out.paste(g, (0, 0), m)
    return out

canvas = Image.new("RGBA", (W * S, H * S), (0, 0, 0, 0))

# ---- shapes (final-size coordinates)
front = box(12, 58, 66, 170)
side = poly([(66, 62), (80, 54), (80, 160), (66, 170)])
top = poly([(13, 58), (27, 50), (80, 50), (80, 54), (66, 62), (13, 62)])
antenna = box(21, 6, 30, 60)
collar = box(18, 44, 33, 58)
knob = box(50, 36, 62, 54)
ptt = box(6, 80, 14, 116)

sil = mask()
d = ImageDraw.Draw(sil)
d.rounded_rectangle(front, radius=big(9), fill=255)
d.polygon(side, fill=255)
d.polygon(top, fill=255)
d.rounded_rectangle(antenna, radius=big(4.5), fill=255)
d.rounded_rectangle(collar, radius=big(3), fill=255)
d.rounded_rectangle(knob, radius=big(3), fill=255)
d.rounded_rectangle(ptt, radius=big(3.5), fill=255)

# Thick outline: the silhouette grown by ~3 px.
outline = sil.filter(ImageFilter.MaxFilter(big(3) * 2 + 1))
canvas.paste(Image.new("RGBA", canvas.size, OUT), (0, 0), outline)

def fill_shape(drawfn, top_c, bottom_c, left_dark=0.0):
    m = mask()
    drawfn(ImageDraw.Draw(m))
    canvas.alpha_composite(gradient(m, top_c, bottom_c, left_dark))
    return m

# Body faces: front (shaded down and to the left), side (moonlit), top (brightest).
m_front = fill_shape(lambda g: g.rounded_rectangle(front, radius=big(9), fill=255), (64, 60, 104), (36, 33, 60), 0.35)
m_side = fill_shape(lambda g: g.polygon(side, fill=255), (104, 100, 160), (64, 60, 104))
m_top = fill_shape(lambda g: g.polygon(top, fill=255), (132, 128, 190), (112, 108, 170))
fill_shape(lambda g: g.rounded_rectangle(ptt, radius=big(3.5), fill=255), (70, 66, 112), (44, 41, 70))
fill_shape(lambda g: g.rounded_rectangle(knob, radius=big(3), fill=255), (60, 56, 92), (30, 28, 46), 0.3)
fill_shape(lambda g: g.rounded_rectangle(collar, radius=big(3), fill=255), (48, 45, 74), (28, 26, 42))
fill_shape(lambda g: g.rounded_rectangle(antenna, radius=big(4.5), fill=255), (44, 41, 66), (26, 24, 38), 0.4)

d = ImageDraw.Draw(canvas)
# Lines where the faces meet, and the antenna's moonlit edge.
d.line([(big(66), big(62)), (big(66), big(168))], fill=INNER, width=big(1.4))
d.line([(big(13), big(62)), (big(66), big(62)), (big(80), big(54))], fill=INNER, width=big(1.4))
d.line([(big(28), big(10)), (big(28), big(42))], fill=(96, 92, 140, 255), width=big(1.2))
d.line([(big(60), big(39)), (big(60), big(52))], fill=(110, 106, 160, 255), width=big(1))

# Speaker grille: a recessed panel with slots.
d.rounded_rectangle(box(20, 70, 58, 104), radius=big(4), fill=(30, 28, 50, 255))
for i in range(6):
    y = 74 + i * 5
    d.rounded_rectangle(box(23, y, 55, y + 2.2), radius=big(1), fill=(14, 12, 24, 255))
    d.line([(big(24), big(y + 2.8)), (big(54), big(y + 2.8))], fill=(70, 66, 110, 255), width=big(0.7))

# Screen, glowing.
scr = box(22, 110, 56, 130)
glow = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
ImageDraw.Draw(glow).rounded_rectangle(box(19, 107, 59, 133), radius=big(5), fill=(79, 209, 176, 90))
canvas.alpha_composite(glow.filter(ImageFilter.GaussianBlur(big(3))))
d = ImageDraw.Draw(canvas)
d.rounded_rectangle(scr, radius=big(3), fill=(16, 52, 54, 255), outline=(10, 26, 30, 255), width=big(1))
for x0, x1, y in [(26, 34, 116), (37, 41, 116), (26, 44, 122), (46, 52, 122)]:
    d.rounded_rectangle(box(x0, y, x1, y + 2.4), radius=big(1), fill=(127, 245, 207, 255))
d.line([(big(24), big(112)), (big(38), big(112))], fill=(90, 170, 160, 255), width=big(0.8))

# Keys: three by two, lit from above.
for row in range(2):
    for col in range(3):
        x0 = 22 + col * 12
        y0 = 136 + row * 12
        d.rounded_rectangle(box(x0, y0 + 1, x0 + 10, y0 + 9), radius=big(2.5), fill=(22, 20, 36, 255))
        d.rounded_rectangle(box(x0, y0, x0 + 10, y0 + 7.5), radius=big(2.5), fill=(84, 80, 128, 255))
        d.line([(big(x0 + 2.5), big(y0 + 1.6)), (big(x0 + 7.5), big(y0 + 1.6))], fill=(138, 134, 196, 255), width=big(0.8))

# LED, glowing green.
led = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
ImageDraw.Draw(led).ellipse(box(55, 61, 65, 71), fill=(125, 255, 160, 120))
canvas.alpha_composite(led.filter(ImageFilter.GaussianBlur(big(2.2))))
d = ImageDraw.Draw(canvas)
d.ellipse(box(57.5, 63.5, 62.5, 68.5), fill=(150, 255, 180, 255), outline=(30, 90, 50, 255), width=big(0.7))

# Belt clip shadow on the side, and the moon's rim light on the right and top edges.
d.polygon(poly([(70, 92), (76, 89), (76, 140), (70, 144)]), fill=(52, 48, 86, 255))
d.line([(big(79), big(56)), (big(79), big(156))], fill=(170, 166, 226, 255), width=big(1))
d.line([(big(28), big(51)), (big(79), big(51))], fill=(176, 172, 232, 255), width=big(1))
d.arc(box(13, 58, 31, 76), start=180, end=270, fill=(120, 116, 178, 255), width=big(1))

# A soft ambient shadow at the bottom of the front face (the ground's bounce is dark).
shade = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
ImageDraw.Draw(shade).rectangle(box(12, 150, 66, 170), fill=(10, 8, 20, 110))
shade = shade.filter(ImageFilter.GaussianBlur(big(5)))
canvas.alpha_composite(Image.composite(shade, Image.new("RGBA", canvas.size, (0, 0, 0, 0)), m_front))

# A light painted grain over the faces, as the menu's art has (not flat vector colour).
import random
random.seed(7)
grain = Image.new("L", (W * S // 2, H * S // 2), 128)
gp = grain.load()
for y in range(grain.height):
    for x in range(grain.width):
        gp[x, y] = 128 + random.randint(-14, 14)
grain = grain.resize(canvas.size, Image.BILINEAR).filter(ImageFilter.GaussianBlur(big(0.6)))
r, g, b, al = canvas.split()
def tint(ch):
    return ImageChops.add(ch, grain, 1.0, -128)  # ch + (grain - 128)
inside = sil
r = Image.composite(tint(r), r, inside); g = Image.composite(tint(g), g, inside); b = Image.composite(tint(b), b, inside)
canvas = Image.merge("RGBA", (r, g, b, al))

final = canvas.resize((W, H), Image.LANCZOS)
final.save(sys.argv[1])
final.resize((W * 4, H * 4), Image.LANCZOS).save(sys.argv[1].replace(".png", "-x4.png"))
print("ok", final.size)
