"""Nexus Mods cover for Plague Dash.

Layout:
- 1280x720 canvas (16:9, standard Nexus cover)
- game-menu.jpg as the FULL background (same aspect ratio — just scaled)
- Subtle darkening so title text pops
- dashboard.jpg as a framed inset, centered, showing the FULL dashboard
  (not cut off) — positioned lower-middle so title sits at the top
- Vector biohazard icon + "Plague Dash" title at top-left
- Subtitle "Live web dashboard for Plague Inc." below title
"""
from PIL import Image, ImageDraw, ImageFont, ImageFilter
import os

W, H = 1300, 372  # Nexus Mods cover banner recommended size
ROOT = r"C:\Users\Thomas\Documents\plague-dash"
BG = os.path.join(ROOT, "docs", "img", "game-menu.jpg")
FG = os.path.join(ROOT, "docs", "img", "dashboard.jpg")
OUT = os.path.join(ROOT, "dist", "nexus", "cover.png")

canvas = Image.new("RGB", (W, H), (10, 10, 10))
draw = ImageDraw.Draw(canvas)

# 1. Background: game-menu full, scaled to canvas (same aspect ratio)
bg = Image.open(BG).convert("RGB")
bg = bg.resize((W, H), Image.LANCZOS)
canvas.paste(bg)

# 2. Darkening overlay for contrast with title + inset
overlay = Image.new("RGBA", (W, H), (0, 0, 0, 110))
canvas = Image.alpha_composite(canvas.convert("RGBA"), overlay).convert("RGB")
draw = ImageDraw.Draw(canvas)

# 3. Dashboard inset — scaled to fit completely within canvas
#    Leave ~100px at top for title, ~60px margins on sides/bottom
inset_margin_x = 70
inset_margin_top = 160  # space for title
inset_margin_bottom = 40

# Calculate max inset dimensions that fit within canvas
max_inset_w = W - (inset_margin_x * 2)
max_inset_h = H - inset_margin_top - inset_margin_bottom

fg = Image.open(FG).convert("RGB")
fg_ratio = fg.width / fg.height  # width per height

# Fit by width first
inset_w = max_inset_w
inset_h = int(inset_w / fg_ratio)

# If height exceeds, fit by height instead
if inset_h > max_inset_h:
    inset_h = max_inset_h
    inset_w = int(inset_h * fg_ratio)

fg = fg.resize((int(inset_w), int(inset_h)), Image.LANCZOS)

# Center horizontally, position below title
inset_x = (W - int(inset_w)) // 2
inset_y = inset_margin_top + (H - inset_margin_top - int(inset_h)) // 2

# Drop shadow (blur a black rect behind the inset)
shadow = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
shadow_draw = ImageDraw.Draw(shadow)
shadow_margin = 20
shadow_draw.rectangle(
    [inset_x + 12, inset_y + 14, inset_x + inset_w + 12, inset_y + inset_h + 14],
    fill=(0, 0, 0, 180),
)
shadow = shadow.filter(ImageFilter.GaussianBlur(radius=18))
canvas = Image.alpha_composite(canvas.convert("RGBA"), shadow).convert("RGB")
draw = ImageDraw.Draw(canvas)

# Thin light border around the inset (frame)
draw.rectangle(
    [inset_x - 2, inset_y - 2, inset_x + inset_w + 1, inset_y + inset_h + 1],
    outline=(220, 170, 60, 220),
    width=2,
)
canvas.paste(fg, (inset_x, inset_y))

# 4. Title block — top-left, with vector biohazard icon
# Try to load a bold font; fall back to default
def load_bold(size):
    candidates = [
        r"C:\Windows\Fonts\arialbd.ttf",
        r"C:\Windows\Fonts\segoeuib.ttf",
        r"C:\Windows\Fonts\calibrib.ttf",
    ]
    for p in candidates:
        if os.path.exists(p):
            return ImageFont.truetype(p, size)
    return ImageFont.load_default()

def load_regular(size):
    candidates = [
        r"C:\Windows\Fonts\arial.ttf",
        r"C:\Windows\Fonts\segoeui.ttf",
        r"C:\Windows\Fonts\calibri.ttf",
    ]
    for p in candidates:
        if os.path.exists(p):
            return ImageFont.truetype(p, size)
    return ImageFont.load_default()

title_font = load_bold(62)
subtitle_font = load_regular(28)

# Biohazard: three interlocking ellipses (vector, no font)
def draw_biohazard(draw, cx, cy, r, color):
    # Three outer arcs/rings arranged as a trefoil
    inner_r = r * 0.55
    petal_r = r * 0.42
    # Three petals at 90°, 210°, 330°
    import math
    for angle_deg in [90, 210, 330]:
        a = math.radians(angle_deg)
        px = cx + math.cos(a) * (r * 0.38)
        py = cy - math.sin(a) * (r * 0.38)
        draw.ellipse(
            [px - petal_r, py - petal_r, px + petal_r, py + petal_r],
            outline=color, width=3,
        )
    # Center ring
    draw.ellipse(
        [cx - inner_r * 0.35, cy - inner_r * 0.35, cx + inner_r * 0.35, cy + inner_r * 0.35],
        outline=color, width=3,
    )

icon_cx, icon_cy = 70, 70
icon_r = 30
draw_biohazard(draw, icon_cx, icon_cy, icon_r, (250, 185, 65))

# Title text beside icon
title_x = icon_cx + icon_r + 22
title_y = 40
# Shadow first, then text
for dx, dy in [(2, 2), (2, 2)]:
    draw.text((title_x + dx, title_y + dy), "Plague Dash", fill=(0, 0, 0, 200), font=title_font)
draw.text((title_x, title_y), "Plague Dash", fill=(250, 235, 200), font=title_font)

# Subtitle
sub_x = title_x + 4
sub_y = title_y + 72
for dx, dy in [(1, 1), (1, 1)]:
    draw.text((sub_x + dx, sub_y + dy), "Live web dashboard for Plague Inc.", fill=(0, 0, 0, 180), font=subtitle_font)
draw.text((sub_x, sub_y), "Live web dashboard for Plague Inc.", fill=(220, 210, 190), font=subtitle_font)

# 5. Save
canvas.save(OUT, "PNG", optimize=True)
sz = os.path.getsize(OUT)
print(f"Saved: {OUT}  ({canvas.size[0]}x{canvas.size[1]}, {sz // 1024} KB)")
