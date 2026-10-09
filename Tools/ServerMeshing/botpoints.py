"""Where step 8b's bots stand (step8b_design.md decisions 4 and 6): the placements a block names, and the points file
remoteb's bots-start reads. Pure: nothing here reads files, the database or the network.

    botpoints.bot_points(50, "spread")       # [(x, z)] * 50: a grid in the 100 x 100 m square centred on (0, 0)
    botpoints.column_points(5, -50)          # [(x, z)] * 5: a column at x = -50, z spread over +-40 m
    botpoints.format_points(points)          # the points file's text, one "x,z[,return_x]" line per bot
    botpoints.parse_points(text)             # its points again; ValueError naming the line it refuses

Placements: spread (coarse layout, cell C), west (a column at x = -30), pace-cut (x = -4: pace walks -4 -> +4, across
the halves' cut at x = 0 and the 1 m hysteresis), pace-west (x = -44: -44 -> -36, never crossing), and centre and
eye-centre (a short column at (0, 0), z within +-2 m: accept-b parts B-D; for eye-centre p0peer then walks the bots to
the eye group's logged centre).
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import math

SQUARE_M = 100.0         # spread: the square's side, centred on (0, 0)
COLUMN_HALF_M = 40.0     # west, pace-cut, pace-west: z from -40 to +40
CENTRE_HALF_M = 2.0      # centre, eye-centre: z from -2 to +2 around (0, 0)
COLUMNS = {"west": -30.0, "pace-cut": -4.0, "pace-west": -44.0}
CENTRES = ("centre", "eye-centre")
PLACEMENTS = ("spread",) + tuple(COLUMNS) + CENTRES


def _round(value):
    return round(value, 3) + 0.0   # + 0.0: no -0.0


def column_points(n, x, half=COLUMN_HALF_M):
    """n points at x, z evenly from -half to +half (one bot: z = 0). ValueError for n below 1."""
    if n < 1:
        raise ValueError(f"{n} bots: a placement needs at least 1")
    if n == 1:
        return [(_round(x), 0.0)]
    return [(_round(x), _round(-half + 2 * half * i / (n - 1))) for i in range(n)]


def bot_points(n, placement):
    """The n bots' (x, z) for a placement (PLACEMENTS). spread: a grid of ceil(sqrt(n)) columns and as many rows as
    needed, each point the centre of its grid cell in the 100 x 100 m square centred on (0, 0); the others: a column
    (column_points). ValueError for an unknown placement or n below 1."""
    if placement not in PLACEMENTS:
        raise ValueError(f"placement {placement!r} is not one of {', '.join(PLACEMENTS)}")
    if placement in CENTRES:
        return column_points(n, 0.0, CENTRE_HALF_M)
    if placement != "spread":
        return column_points(n, COLUMNS[placement])
    if n < 1:
        raise ValueError(f"{n} bots: a placement needs at least 1")
    cols = math.ceil(math.sqrt(n))
    rows = math.ceil(n / cols)
    dx, dz = SQUARE_M / cols, SQUARE_M / rows
    return [(_round(-SQUARE_M / 2 + (i % cols + 0.5) * dx), _round(-SQUARE_M / 2 + (i // cols + 0.5) * dz))
            for i in range(n)]


def _text(value):
    return f"{_round(value):.3f}".rstrip("0").rstrip(".")


def format_points(points):
    """The points file: one "x,z" or "x,z,return_x" line per point, CRLF-free, ending with a newline."""
    return "".join(",".join(_text(v) for v in point) + "\n" for point in points)


def parse_points(text):
    """The points of a points file: (x, z) or (x, z, return_x) floats per line; blank lines and '#' comments are
    skipped. ValueError naming the line for anything else (two or three finite numbers)."""
    points = []
    for number, line in enumerate(text.splitlines(), 1):
        body = line.split("#", 1)[0].strip()
        if not body:
            continue
        parts = [part.strip() for part in body.split(",")]
        try:
            values = tuple(float(part) for part in parts)
        except ValueError:
            values = ()
        if len(values) not in (2, 3) or not all(math.isfinite(v) for v in values):
            raise ValueError(f"points line {number}: '{body}' is not x,z[,return_x]")
        points.append(values)
    return points
