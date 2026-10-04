"""Phase 0 p0geom (step8a_design.md decision G; meshing reply section 7 item 5): what a ship fight's geometry asks of
the interest system, offline (no rig). Seeded scenarios place a fight's ships; every player ship is a client and each
client's counts are read in 3D and on the X/Z plane, for A0 2 km and 3 km.

    python Tools/ServerMeshing/p0geom.py                      # the default grid (both fights, every shape, 10 seeds)
    python Tools/ServerMeshing/p0geom.py --fight 64+64 --shape furball --radius 1000 --seeds 10

    p0geom.visible_range(125.33, 125.33, 2000.0)   # 2313.325: A = A0 + (|v_t| + f) x 1.25 s, capped at the roam
    p0geom.generate("furball", 1000.0, 64, 64, random.Random(0))   # the ships of one seed
    p0geom.client_counts(ships, 2000.0)            # per player ship: V, near shares, pilots, carry, lock, extra
    p0geom.summary([2, 3, 3, 2, 0])                # {"mean": 2.0, "p95": 3, "max": 3}
    p0geom.run_point("64+64", "furball", 1000.0)   # one row per A0: every metric's mean, p95, max over clients and seeds

Scenarios: furball (every ship uniform in a sphere of radius R, headings uniform on the sphere); lines (players in a
disc of radius 500 m centred at x = -D/2, AI ships in one at x = +D/2, both on y = 0, each ship heading to the other
group's centre); patrol (uniform in an L x L x 1,000 m box, headings uniform on the X/Z plane). Speeds are drawn per
ship: player ships full 125.33 m/s 75 %, boost 250.67 m/s 15 %, patrol 50.13 m/s 10 %; AI ships patrol 37.6 m/s 30 %,
attack clamp(d / 400 m, 0.35, 1) x 94.0 m/s 60 % (d: the 3D distance to its nearest player ship), full 94.0 m/s 10 %.
Each ship is its own group, so f is the fastest ship of the fight.
Counts per client c (inclusive: a distance equal to a limit counts): V = ships t != c whose range A_t covers c (the
range belongs to the target); near = the share of the other ships within 1 km; pilots = other player ships within 80 m
X/Z (the kit's default range); carry = player ships t != c whose A_t covers c (seated characters carry their ship's
range; AI pilots carry none); lock = 1 when c (player ships only) locks the nearest AI ship within 3,000 m (3D) and
inside a 25 degree half-angle cone about its heading, the cone skipped within 100 m (ShipMissileComponent's
TargetInLockCone); extra = 1 when that lock's distance is above the target's A_t (an extra subscription).
Output: Logs/p0/geom/p0geom_<stamp>.md and .jsonl (a sub-folder: p0blocks.read_history reads every Logs/p0/*.jsonl as
mesh-p0/1 rows and would refuse these), every row labelled "offline (no rig)".
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import argparse
import collections
import datetime
import json
import math
import os
import random
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT_DIR = os.path.normpath(os.path.join(HERE, "..", "..", "Logs", "p0", "geom"))
LABEL = "offline (no rig)"
SCHEMA = "p0geom/1"

PLAYER_MIX = (("full", 125.33, 0.75), ("boost", 250.67, 0.15), ("patrol", 50.13, 0.10))
AI_MIX = (("patrol", 37.6, 0.30), ("attack", 94.0, 0.60), ("full", 94.0, 0.10))
ATTACK_MIN, ATTACK_FULL_M = 0.35, 400.0   # an attacking AI ship's speed share at its nearest player's distance / 400 m
LEAD_S = 1.25
A0S = (2000.0, 3000.0)
ROAM_CAP_M = 30000.0
NEAR_M, PILOT_M = 1000.0, 80.0
LOCK_RANGE_M, LOCK_HALF_ANGLE_DEG, LOCK_CONE_SKIP_M = 3000.0, 25.0, 100.0
ANGLE_SLACK_DEG = 1e-9   # so a target exactly on the cone's edge stays inside it in floats
FIGHTS = ("64+64", "400+100")
SHAPES = ("furball", "lines", "patrol")
LINES_DISC_M, PATROL_HEIGHT_M = 500.0, 1000.0
DEFAULT_SIZES = {"furball": (500.0, 1000.0, 2000.0), "lines": (1000.0, 3000.0), "patrol": (5000.0, 10000.0)}
SEEDS = 10
METRICS = ("v_3d", "v_xz", "near_3d", "near_xz", "pilots_xz", "carry_3d", "carry_xz", "lock", "extra_3d", "extra_xz")
SHARES = ("near_3d", "near_xz")

Ship = collections.namedtuple("Ship", "pos heading speed player speed_class", defaults=((1.0, 0.0, 0.0), 0.0, True, ""))


def parse_fight(text):
    """A fight "<players>+<ai>" as (players, ai): players at least 1, AI ships at least 0. ValueError otherwise."""
    parts = str(text).split("+")
    if len(parts) != 2 or not all(p.strip().isdigit() for p in parts):
        raise ValueError(f"fight '{text}' is not <players>+<ai>")
    players, ai = (int(p) for p in parts)
    if players < 1:
        raise ValueError(f"fight '{text}' has no player ship, so no client")
    return players, ai


def visible_range(speed, fastest, a0, cap=ROAM_CAP_M):
    """A ship's visible range: A0 + (|v| + f) x 1.25 s, at most the roam cap (metres)."""
    if a0 <= 0 or cap <= 0:
        raise ValueError(f"A0 {a0} m and the roam cap {cap} m must be above 0")
    return min(a0 + (speed + fastest) * LEAD_S, cap)


def distance(a, b, metric="3d"):
    """The distance between two positions (x, y, z), in 3D or on the X/Z plane ("xz"; y is up)."""
    if metric == "3d":
        return math.sqrt((a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2 + (a[2] - b[2]) ** 2)
    if metric == "xz":
        return math.hypot(a[0] - b[0], a[2] - b[2])
    raise ValueError(f"metric '{metric}' is not 3d or xz")


def angle_deg(u, v):
    """The angle between two vectors in degrees as Unity's Vector3.Angle takes it (0 for a zero vector)."""
    norms = math.sqrt((u[0] ** 2 + u[1] ** 2 + u[2] ** 2) * (v[0] ** 2 + v[1] ** 2 + v[2] ** 2))
    if norms < 1e-15:
        return 0.0
    dot = (u[0] * v[0] + u[1] * v[1] + u[2] * v[2]) / norms
    return math.degrees(math.acos(max(-1.0, min(1.0, dot))))


def in_lock_cone(shooter, target_pos):
    """ShipMissileComponent.TargetInLockCone: within 3,000 m (3D), and within 100 m or inside the 25 degree half-angle
    cone about the shooter's heading; every limit inclusive."""
    d = distance(shooter.pos, target_pos)
    if d > LOCK_RANGE_M:
        return False
    if d <= LOCK_CONE_SKIP_M:
        return True
    to_target = tuple(t - s for t, s in zip(target_pos, shooter.pos))
    return angle_deg(shooter.heading, to_target) <= LOCK_HALF_ANGLE_DEG + ANGLE_SLACK_DEG


def lock_target(index, ships):
    """The index of the ship ships[index] locks, else None: AI ships never lock; a player ship locks the nearest AI ship
    (3D) inside its lock cone, the first such ship on a tie."""
    shooter = ships[index]
    if not shooter.player:
        return None
    best, best_d = None, None
    for t, ship in enumerate(ships):
        if ship.player or not in_lock_cone(shooter, ship.pos):
            continue
        d = distance(shooter.pos, ship.pos)
        if best_d is None or d < best_d:
            best, best_d = t, d
    return best


def client_rows(ships, a0s=A0S, cap=ROAM_CAP_M):
    """{a0: per-client rows} for every A0, the clients being the player ships in order. A row holds METRICS: v_3d,
    v_xz (ships whose range covers the client), near_3d, near_xz (the share of the other ships within 1 km; 0 alone),
    pilots_xz (other player ships within 80 m X/Z), carry_3d, carry_xz (player ships whose range covers the client),
    lock (0 or 1), extra_3d, extra_xz (1 when the locked target's distance is above its range). Distances are
    computed once for every A0."""
    if not ships:
        return {a0: [] for a0 in a0s}
    fastest = max(ship.speed for ship in ships)
    ranges = {a0: [visible_range(ship.speed, fastest, a0, cap) for ship in ships] for a0 in a0s}
    others = len(ships) - 1
    rows = {a0: [] for a0 in a0s}
    for c, client in enumerate(ships):
        if not client.player:
            continue
        d3 = [distance(client.pos, ship.pos) for ship in ships]
        dxz = [distance(client.pos, ship.pos, "xz") for ship in ships]
        players = [t for t, ship in enumerate(ships) if ship.player and t != c]
        near_3d = sum(1 for t, d in enumerate(d3) if t != c and d <= NEAR_M)
        near_xz = sum(1 for t, d in enumerate(dxz) if t != c and d <= NEAR_M)
        pilots = sum(1 for t in players if dxz[t] <= PILOT_M)
        locked = lock_target(c, ships)
        for a0 in a0s:
            reach = ranges[a0]
            rows[a0].append({
                "v_3d": sum(1 for t, d in enumerate(d3) if t != c and d <= reach[t]),
                "v_xz": sum(1 for t, d in enumerate(dxz) if t != c and d <= reach[t]),
                "near_3d": near_3d / others if others else 0.0, "near_xz": near_xz / others if others else 0.0,
                "pilots_xz": pilots,
                "carry_3d": sum(1 for t in players if d3[t] <= reach[t]),
                "carry_xz": sum(1 for t in players if dxz[t] <= reach[t]),
                "lock": 0 if locked is None else 1,
                "extra_3d": 0 if locked is None else int(d3[locked] > reach[locked]),
                "extra_xz": 0 if locked is None else int(dxz[locked] > reach[locked])})
    return rows


def client_counts(ships, a0, cap=ROAM_CAP_M):
    """The per-client rows of client_rows for one A0."""
    return client_rows(ships, (a0,), cap)[a0]


def summary(values):
    """{"mean", "p95", "max"} of a list: p95 by nearest rank (the value at rank max(1, ceil(0.95 n)) in sorted order,
    as MeshWorkHistogram ranks). ValueError for an empty list."""
    if not values:
        raise ValueError("no values to summarise")
    ordered = sorted(values)
    rank = max(1, math.ceil(0.95 * len(ordered) - 1e-9))
    return {"mean": sum(ordered) / len(ordered), "p95": ordered[rank - 1], "max": ordered[-1]}


def _unit(rng, flat=False):
    while True:
        v = (rng.gauss(0.0, 1.0), 0.0 if flat else rng.gauss(0.0, 1.0), rng.gauss(0.0, 1.0))
        norm = math.sqrt(v[0] ** 2 + v[1] ** 2 + v[2] ** 2)
        if norm > 1e-12:
            return tuple(x / norm for x in v)


def _in_disc(rng, centre_x, radius):
    angle, r = 2.0 * math.pi * rng.random(), radius * math.sqrt(rng.random())
    return (centre_x + r * math.cos(angle), 0.0, r * math.sin(angle))


def _heading_to(pos, target):
    v = tuple(t - p for t, p in zip(target, pos))
    norm = math.sqrt(sum(x * x for x in v))
    return tuple(x / norm for x in v) if norm > 1e-12 else (1.0, 0.0, 0.0)


def _draw(rng, mix):
    u, total = rng.random(), 0.0
    for name, speed, share in mix:
        total += share
        if u < total:
            return name, speed
    return mix[-1][0], mix[-1][1]


def generate(shape, size, players, ai, rng):
    """One seed's ships: players first (player=True), then AI ships, placed and headed by the shape (size: furball's
    R, lines' D, patrol's L, in metres), then given speeds from PLAYER_MIX and AI_MIX (draws in ship order). ValueError
    for an unknown shape or a size not above 0."""
    if shape not in SHAPES:
        raise ValueError(f"shape '{shape}' is not {', '.join(SHAPES)}")
    if not size > 0:
        raise ValueError(f"{shape} size {size} m is not above 0")
    sides = [True] * players + [False] * ai
    placed = []
    for player in sides:
        if shape == "furball":
            direction = _unit(rng)
            r = size * rng.random() ** (1.0 / 3.0)
            pos, heading = tuple(r * x for x in direction), _unit(rng)
        elif shape == "lines":
            pos = _in_disc(rng, -size / 2 if player else size / 2, LINES_DISC_M)
            heading = _heading_to(pos, (size / 2 if player else -size / 2, 0.0, 0.0))
        else:
            pos = (size * (rng.random() - 0.5), PATROL_HEIGHT_M * rng.random(), size * (rng.random() - 0.5))
            heading = _unit(rng, flat=True)
        placed.append((pos, heading, player))
    player_pos = [pos for pos, _, player in placed if player]
    ships = []
    for pos, heading, player in placed:
        name, speed = _draw(rng, PLAYER_MIX if player else AI_MIX)
        if name == "attack" and not player:
            nearest = min((distance(pos, p) for p in player_pos), default=math.inf)
            speed *= min(1.0, max(ATTACK_MIN, nearest / ATTACK_FULL_M))
        ships.append(Ship(pos, heading, speed, player, name))
    return ships


def size_text(shape, size):
    """The scenario's geometry as a label: "R 1000", "D 1000 r 500", "L 5000 H 1000"."""
    if shape == "furball":
        return f"R {size:g}"
    if shape == "lines":
        return f"D {size:g} r {LINES_DISC_M:g}"
    return f"L {size:g} H {PATROL_HEIGHT_M:g}"


def run_point(fight, shape, size, seeds=tuple(range(SEEDS)), a0s=A0S, cap=ROAM_CAP_M):
    """One scenario point over its seeds: a row per A0 with the point, the seeds, "clients" (client samples over
    seeds), "fastest_mps" (f's mean over seeds) and "metrics" {name: summary over clients and seeds}."""
    players, ai = parse_fight(fight)
    pooled = {a0: collections.defaultdict(list) for a0 in a0s}
    fastest = []
    for seed in seeds:
        ships = generate(shape, size, players, ai, random.Random(seed))
        fastest.append(max(ship.speed for ship in ships))
        for a0, rows in client_rows(ships, a0s, cap).items():
            for row in rows:
                for name in METRICS:
                    pooled[a0][name].append(row[name])
    return [{"schema": SCHEMA, "label": LABEL, "fight": fight, "players": players, "ai": ai, "shape": shape,
             "size_m": size, "geometry": size_text(shape, size), "a0_m": a0, "roam_cap_m": cap, "seeds": list(seeds),
             "clients": len(pooled[a0]["v_3d"]), "fastest_mps": sum(fastest) / len(fastest),
             "metrics": {name: summary(pooled[a0][name]) for name in METRICS}} for a0 in a0s]


def grid(fight=None, shape=None, size=None):
    """The scenario points (fight, shape, size) the CLI runs: the default grid, narrowed to one fight, one shape and
    one size when given. ValueError for a size without a shape, or an unknown fight or shape."""
    if size is not None and shape is None:
        raise ValueError("--radius needs --shape: it is furball's R, lines' D or patrol's L")
    fights = FIGHTS if fight is None else (fight,)
    for f in fights:
        parse_fight(f)
    if shape is not None and shape not in SHAPES:
        raise ValueError(f"shape '{shape}' is not {', '.join(SHAPES)}")
    return [(f, s, z) for f in fights for s in (SHAPES if shape is None else (shape,))
            for z in (DEFAULT_SIZES[s] if size is None else (float(size),))]


COLUMNS = ("Fight", "Shape", "Geometry (m)", "A0 (m)", "V 3D", "V X/Z", "Within 1 km 3D (%)", "Within 1 km X/Z (%)",
           "Pilots within 80 m X/Z", "Carry 3D", "Carry X/Z", "Locks", "Extra 3D", "Extra X/Z", "n")


def _cell(stats, name):
    if name in SHARES:
        return f"{100 * stats['mean']:.1f} / {100 * stats['p95']:.1f} / {100 * stats['max']:.1f}"
    if name in ("lock", "extra_3d", "extra_xz"):
        return f"{stats['mean']:.3f} / {stats['p95']:g} / {stats['max']:g}"
    return f"{stats['mean']:.1f} / {stats['p95']:g} / {stats['max']:g}"


def markdown(rows, stamp):
    """The rows as a Markdown page: a title naming the stamp and "offline (no rig)", the definitions, and one table
    line per row, each metric "mean / p95 / max" over clients and seeds."""
    lines = [f"# p0geom {stamp}: fight geometry, {LABEL}", "",
             "Every player ship is a client; cells are mean / p95 / max over clients and seeds. V: ships whose range "
             "A_t = A0 + (|v_t| + f) x 1.25 s (cap "
             f"{rows[0]['roam_cap_m']:g} m) covers the client. Within 1 km: share of the other ships. Pilots: other "
             "player ships within 80 m X/Z. Carry: player ships whose range covers the client. Locks: share of "
             "clients locking an AI ship (3,000 m, 25 degree half-angle, cone skipped within 100 m). Extra: locks "
             "beyond the target's range." if rows else "No rows.", "",
             "| " + " | ".join(COLUMNS) + " |", "|" + "---|" * len(COLUMNS)]
    for row in rows:
        cells = [row["fight"], row["shape"], row["geometry"], f"{row['a0_m']:g}"]
        cells += [_cell(row["metrics"][name], name) for name in METRICS]
        lines.append("| " + " | ".join(cells + [str(row["clients"])]) + " |")
    return "\n".join(lines) + "\n"


def jsonl(rows):
    """The rows as JSONL text, one compact object per line."""
    return "".join(json.dumps(row, separators=(",", ":"), allow_nan=False) + "\n" for row in rows)


def build_parser():
    ap = argparse.ArgumentParser(description="p0geom: offline fight geometry (step8a_design.md decision G)")
    ap.add_argument("--fight", help="<players>+<ai>, e.g. 64+64 or 400+100 (default both)")
    ap.add_argument("--shape", choices=SHAPES, help="furball, lines or patrol (default all)")
    ap.add_argument("--radius", type=float, help="the shape's size in metres: furball R, lines D, patrol L")
    ap.add_argument("--seeds", type=int, default=SEEDS, help=f"seeds 0..n-1 per point (default {SEEDS})")
    ap.add_argument("--cap", type=float, default=ROAM_CAP_M, help=f"the roam cap in metres (default {ROAM_CAP_M:g})")
    ap.add_argument("--out", default=OUT_DIR, help="the output folder (default Logs/p0/geom)")
    return ap


def main(argv=None):
    """Runs the chosen points, writes p0geom_<stamp>.md and .jsonl to --out, prints the table; 0, or 2 for arguments
    it refuses."""
    args = build_parser().parse_args(argv)
    try:
        if args.seeds < 1:
            raise ValueError(f"--seeds {args.seeds} is not at least 1")
        points = grid(args.fight, args.shape, args.radius)
        rows = [row for point in points for row in run_point(*point, seeds=tuple(range(args.seeds)), cap=args.cap)]
    except ValueError as err:
        print(f"p0geom: {err}", file=sys.stderr)
        return 2
    stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    os.makedirs(args.out, exist_ok=True)
    text = markdown(rows, stamp)
    for ext, content in (("md", text), ("jsonl", jsonl(rows))):
        path = os.path.join(args.out, f"p0geom_{stamp}.{ext}")
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(content)
        print("written", path)
    print(text, end="")
    return 0


if __name__ == "__main__":
    sys.exit(main())
