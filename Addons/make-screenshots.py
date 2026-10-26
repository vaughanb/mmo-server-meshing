# Draws the add-on's screenshot for the Open MMORPG Addon Manager listing.
# A diagram, not a gameplay capture: the cell grid, the ghost bands along shared edges, a handoff in progress
# and a sleeping cell.
import math
import pathlib
from PIL import Image, ImageDraw, ImageFont

W, H = 1280, 720
BG = (16, 19, 26)
PANEL = (23, 27, 36)
INK = (232, 236, 244)
DIM = (139, 149, 168)
FAINT = (58, 66, 82)
ACCENT = (108, 196, 255)

FONTS = pathlib.Path("C:/Windows/Fonts")


def font(name, size):
    return ImageFont.truetype(str(FONTS / name), size)


B = lambda s: font("segoeuib.ttf", s)
R = lambda s: font("segoeui.ttf", s)

# Zone colours, matching the test map's zone materials (NW red ... SE pink), muted for a dark background.
ZONES = {
    "NW": (196, 72, 72),
    "N": (204, 126, 58),
    "NE": (198, 182, 64),
    "W": (92, 176, 104),
    "C": (150, 158, 172),
    "E": (74, 178, 196),
    "SW": (74, 124, 204),
    "S": (176, 82, 184),
    "SE": (198, 106, 150),
}
GRID = [["NW", "N", "NE"], ["W", "C", "E"], ["SW", "S", "SE"]]


def mix(c, other, t):
    return tuple(round(a + (b - a) * t) for a, b in zip(c, other))


def text(d, xy, s, f, fill, anchor="la"):
    d.text(xy, s, font=f, fill=fill, anchor=anchor)


def arrow(d, x1, y1, x2, y2, colour, width=3, head=11):
    d.line((x1, y1, x2, y2), fill=colour, width=width)
    a = math.atan2(y2 - y1, x2 - x1)
    for s in (2.6, -2.6):
        d.line((x2, y2, x2 + head * math.cos(a + s), y2 + head * math.sin(a + s)), fill=colour, width=width)


def dashed_rect(d, box, colour, dash=9, gap=7, width=2):
    x0, y0, x1, y1 = box
    for x in range(int(x0), int(x1), dash + gap):
        xe = min(x + dash, x1)
        d.line((x, y0, xe, y0), fill=colour, width=width)
        d.line((x, y1, xe, y1), fill=colour, width=width)
    for y in range(int(y0), int(y1), dash + gap):
        ye = min(y + dash, y1)
        d.line((x0, y, x0, ye), fill=colour, width=width)
        d.line((x1, y, x1, ye), fill=colour, width=width)


def header(d, title, subtitle):
    d.rectangle((0, 0, W, 116), fill=PANEL)
    d.line((0, 116, W, 116), fill=(38, 44, 56), width=2)
    text(d, (64, 34), title, B(40), INK)
    text(d, (64, 84), subtitle, R(19), DIM)


def base():
    img = Image.new("RGB", (W, H), BG)
    return img, ImageDraw.Draw(img)


def cell(d, box, key, label, sub, sleeping=False):
    x0, y0, x1, y1 = box
    colour = ZONES[key]
    fill = mix(colour, BG, 0.80 if sleeping else 0.52)
    d.rectangle(box, fill=fill)
    if sleeping:
        dashed_rect(d, box, mix(colour, BG, 0.45))
    else:
        d.rectangle(box, outline=mix(colour, (255, 255, 255), 0.25), width=2)
    cx = (x0 + x1) / 2
    ink = DIM if sleeping else INK
    text(d, (cx, (y0 + y1) / 2 - 15), label, B(23), ink, anchor="mm")
    text(d, (cx, (y0 + y1) / 2 + 13), sub, R(14), mix(ink, BG, 0.30), anchor="mm")


def ghost_bands(img, ox, oy, size, cols, rows, band=13):
    """Translucent strips either side of every shared edge: the region cells stream to their neighbour."""
    overlay = Image.new("RGBA", img.size, (0, 0, 0, 0))
    od = ImageDraw.Draw(overlay)
    tint = (150, 210, 255, 64)
    for c in range(1, cols):
        x = ox + c * size
        od.rectangle((x - band, oy, x + band, oy + rows * size), fill=tint)
    for r in range(1, rows):
        y = oy + r * size
        od.rectangle((ox, y - band, ox + cols * size, y + band), fill=tint)
    return Image.alpha_composite(img.convert("RGBA"), overlay).convert("RGB")


def callout(d, x, y, swatch, title, body):
    d.rectangle((x, y + 4, x + 5, y + 46), fill=swatch)
    text(d, (x + 20, y), title, B(19), INK)
    for i, line in enumerate(body):
        text(d, (x + 20, y + 28 + i * 22), line, R(15), DIM)


def player(d, x, y, colour=(255, 255, 255), r=7):
    d.ellipse((x - r, y - r, x + r, y + r), fill=colour, outline=BG, width=2)


def ghost(d, x, y, colour, r=6):
    d.ellipse((x - r, y - r, x + r, y + r), outline=colour, width=2)


# ---------------------------------------------------------------- add-on
def addon(out):
    img, d = base()
    header(d, "Server Meshing", "One map across many map servers \u2014 seamless handoff, ghosting across cell edges, sleeping cells")

    ox, oy, size = 72, 168, 158
    channels = [[101, 102, 103], [104, 105, 106], [107, 108, 109]]
    load = [["38", "71", "12"], ["44", "266", "83"], ["9", "27", "asleep"]]
    for r, row in enumerate(GRID):
        for c, key in enumerate(row):
            x0, y0 = ox + c * size, oy + r * size
            sleeping = key == "SE"
            cell(d, (x0 + 2, y0 + 2, x0 + size - 2, y0 + size - 2), key,
                 f"cell {channels[r][c]}",
                 "sleeping" if sleeping else f"{load[r][c]} players", sleeping)

    # the ghost bands go over the cells: they are the strips the cells stream to each other
    img = ghost_bands(img, ox, oy, size, 3, 3)
    d = ImageDraw.Draw(img)

    # A handoff in flight across the 105|106 edge, and the ghosts the cells hold of each other inside the band.
    ex = ox + 2 * size
    ey = oy + 1.5 * size + 44
    arrow(d, ex - 36, ey, ex + 42, ey, (255, 255, 255), 3)
    player(d, ex - 42, ey)
    ghost(d, ex - 20, oy + 0.5 * size, ZONES["NE"])
    ghost(d, ex + 20, oy + 0.5 * size, ZONES["N"])
    ghost(d, ox + 1 * size + 22, oy + 2 * size + 24, ZONES["W"])

    # Legend, below the grid and clear of every cell label.
    ly = oy + 3 * size + 22
    player(d, ox + 8, ly)
    arrow(d, ox + 18, ly, ox + 46, ly, (255, 255, 255), 2, 8)
    text(d, (ox + 58, ly - 9), "handoff, 200–300 ms", R(15), DIM)
    gx = ox + 250
    ghost(d, gx + 8, ly, ZONES["N"])
    text(d, (gx + 24, ly - 9), "ghosts, streamed in the 80 m band", R(15), DIM)
    text(d, (ox, ly + 24), "one process per cell   ·   ~270 players per cell at a 60 fps server tick", R(15), DIM)

    px = 600
    text(d, (px, 168), "WHAT IT DOES", B(14), FAINT)
    callout(d, px, 196, (255, 255, 255), "Seamless handoff", [
        "Walk across a cell edge and the next server",
        "takes over \u2014 no loading screen, no break.",
    ])
    callout(d, px, 286, (150, 210, 255), "Ghosting across edges", [
        "Cells stream everything within 80 m of a shared",
        "edge to the neighbour \u2014 players, monsters, pets,",
        "drops, harvestables, buildings. Combat, loot,",
        "quests and building resolve on the owning side.",
    ])
    callout(d, px, 420, ZONES["SE"], "Sleeping cells", [
        "A cell with nobody near it does not run. It starts",
        "ahead of the first player who approaches.",
    ])
    callout(d, px, 510, ZONES["W"], "Seams between maps", [
        "Two maps placed side by side are crossed the",
        "same way, the client pre-loading the neighbour.",
    ])

    d.line((px, 620, W - 64, 620), fill=(38, 44, 56), width=2)
    text(d, (px, 640), "Subclasses on the kit's managers and prefabs plus one world-registry asset.", R(15), DIM)
    text(d, (px, 664), "The kit's core is not modified.", B(15), INK)

    img.save(out)
    print("wrote", out)


root = pathlib.Path(r"D:\Game Development\mmo-server-meshing\Addons")
addon(root / "ServerMeshing" / "dist" / "screenshot.png")
