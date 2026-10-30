"""Pure helpers shared by the Server Meshing harness scripts (regress.py and the load tools).

Nothing here talks to the editor, the database or a process; everything takes its inputs as arguments so it can be
unit-tested (python -m unittest Tools/ServerMeshing/test_rigtools.py).
"""
import datetime
import hashlib
import glob
import os
import re

# The kit's file log: " INFO <tag> [YYYY-MM-DD HH:MM:SS] - message". Long logger tags push the stamp far to the
# right, so it is searched across the whole line, never in a fixed-width prefix.
STAMP = re.compile(r"\[(\d{4}-\d\d-\d\d) (\d\d:\d\d:\d\d)\]")
LOG_NAME = re.compile(r"^Log_Map\((?P<map>[^)]*)\)-Ch\((?P<cell>[^)]*)\)-Alloc\(False\)-Instance\(\)_(?P<day>\d{4}-\d\d-\d\d)\.(?P<sev>info|warn|error)\.log$")


def stamp_of(line):
    """The (date, time) strings of a kit log line's stamp, or None when it has none."""
    m = STAMP.search(line)
    return (m.group(1), m.group(2)) if m else None


def shift_stamp(line, seconds):
    """The line with its kit stamp moved by whole `seconds` (another machine's clock put on this one's); unchanged
    without a stamp or a shift."""
    m = STAMP.search(line)
    if not m or not seconds:
        return line
    moved = datetime.datetime.fromisoformat(f"{m.group(1)} {m.group(2)}") + datetime.timedelta(seconds=seconds)
    return line[:m.start()] + moved.strftime("[%Y-%m-%d %H:%M:%S]") + line[m.end():]


def stamp_datetime(line):
    """The stamp of a kit log line as a datetime, or None."""
    s = stamp_of(line)
    if s is None:
        return None
    return datetime.datetime.fromisoformat(f"{s[0]} {s[1]}")


def parse_log_name(name):
    """(map, cell, day, severity) of a kit map-server log file name, or None for any other file."""
    m = LOG_NAME.match(os.path.basename(name))
    if not m:
        return None
    return m.group("map"), m.group("cell"), m.group("day"), m.group("sev")


def discover_cells(logs_dir, days, since=None):
    """Every (cell, map) that has a map-server log in logs_dir for any of the given days, sorted; with `since`
    ("YYYY-MM-DD HH:MM:SS"), only those with at least one line stamped at or after it.

    Dynamic cells get ids at runtime, so the graded set cannot be a fixed list; the log files say who ran. A day file
    only says the cell ran at some point that day: a child of an earlier split scenario keeps its file all day, so a
    scenario grades by `since` (its start) or every later scenario that day grades that child on no lines.
    """
    found = set()
    for path in glob.glob(os.path.join(logs_dir, "Log_Map(*)-Ch(*)-Alloc(False)-Instance()_*.log")):
        parsed = parse_log_name(path)
        if not parsed or parsed[2] not in days or (parsed[1], parsed[0]) in found:
            continue
        if since is None or logged_since(path, since):
            found.add((parsed[1], parsed[0]))
    return sorted(found)


def logged_since(path, since):
    """Whether the kit log at path has a line stamped at or after `since` ("YYYY-MM-DD HH:MM:SS")."""
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            stamp = stamp_of(line)
            if stamp and f"{stamp[0]} {stamp[1]}" >= since:
                return True
    return False


def newest_mtime(paths):
    """The latest modification time among the paths (0 when there are none)."""
    return max((os.path.getmtime(p) for p in paths), default=0.0)


def runtime_sources(assets_dir):
    """The add-on's player-side C# sources: everything under Assets/ServerMeshing except Editor folders."""
    out = []
    for path in glob.glob(os.path.join(assets_dir, "ServerMeshing", "**", "*.cs"), recursive=True):
        parts = re.split(r"[\\/]", os.path.relpath(path, assets_dir))
        if "Editor" not in parts:
            out.append(path)
    return out


def build_is_stale(build_dll, sources):
    """True when the player build's Assembly-CSharp.dll is missing or older than any of the sources."""
    if not os.path.exists(build_dll):
        return True
    return os.path.getmtime(build_dll) < newest_mtime(sources)


MANIFEST_EXTENSIONS = {".cs", ".asmdef", ".asset", ".prefab", ".unity"}


def in_manifest(rel):
    """Whether a project-relative path (forward slashes) is in the build manifest; mirrors ServerMeshRigBuild.InManifest."""
    ext = os.path.splitext(rel)[1].lower()
    if ext not in MANIFEST_EXTENSIONS:
        return False
    if ext in (".cs", ".asmdef"):
        return "Editor" not in rel.split("/")   # editor-only code is not in the player
    return True


def normalized_bytes(data):
    """The content with CRLF line ends turned into LF, as ServerMeshRigBuild.NormalizedBytes hashes it: the editor
    re-saves YAML assets with either, and a line-end change does not change what is built."""
    return data.replace(b"\r\n", b"\n")


def source_manifest(root):
    """{path: sha1} of every player-side source and asset, the same set ServerMeshRigBuild records at build time."""
    out = {}
    for top in ("Assets", "ProjectSettings"):
        base = os.path.join(root, top)
        for dirpath, _, files in os.walk(base):
            for name in files:
                full = os.path.join(dirpath, name)
                rel = os.path.relpath(full, root).replace("\\", "/")
                if in_manifest(rel):
                    with open(full, "rb") as f:
                        out[rel] = hashlib.sha1(normalized_bytes(f.read())).hexdigest()
    return out


def read_manifest(path):
    """The {path: sha1} a build recorded, or None when there is no manifest."""
    try:
        with open(path, encoding="utf-8") as f:
            entries = {}
            for line in f:
                line = line.rstrip("\n")
                if not line:
                    continue
                digest, _, rel = line.partition(" ")
                entries[rel] = digest
            return entries
    except FileNotFoundError:
        return None


def manifest_diff(built, current):
    """(changed, added, removed) paths between the manifest a build recorded and the working tree's."""
    changed = sorted(p for p in built.keys() & current.keys() if built[p] != current[p])
    added = sorted(current.keys() - built.keys())
    removed = sorted(built.keys() - current.keys())
    return changed, added, removed


def build_staleness(root, manifest_path):
    """Why the player build is stale, as a list of reasons (empty when it matches the working tree)."""
    built = read_manifest(manifest_path)
    if built is None:
        return ["the build has no manifest (it was not made by ServerMeshRigBuild); rebuild with --build"]
    changed, added, removed = manifest_diff(built, source_manifest(root))
    reasons = []
    for label, paths in (("changed", changed), ("added", added), ("removed", removed)):
        if paths:
            reasons.append(f"{len(paths)} {label} since the build, e.g. {', '.join(paths[:3])}")
    return reasons


def read_kv(path):
    """key=value lines of a status file as a dict (repeated keys are kept as a list under that key)."""
    out = {}
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                if "=" not in line:
                    continue
                k, v = line.rstrip("\n").split("=", 1)
                if k in out:
                    prev = out[k]
                    out[k] = prev + [v] if isinstance(prev, list) else [prev, v]
                else:
                    out[k] = v
    except FileNotFoundError:
        pass
    return out


def read_status_file(path, key="isPlaying", tries=5, pause=0.05):
    """read_kv of a status file the editor rewrites every second (truncate, then write): a read that lands in between
    comes back empty or cut short, so it is read again, up to `tries` times, until it holds `key`. A missing file is {}."""
    import time
    st = {}
    for attempt in range(tries):
        if not os.path.exists(path):
            return {}
        st = read_kv(path)
        if key in st:
            return st
        if attempt + 1 < tries:
            time.sleep(pause)
    return st


def as_list(value):
    """A status value as a list (read_kv keeps a single occurrence as a string)."""
    if value is None:
        return []
    return value if isinstance(value, list) else [value]


# ------------------------------------------------------------------ Mesh stats lines
STATS_FIELDS = {
    "players": re.compile(r"players=(\d+) owned=(\d+)"),
    "ghosts": re.compile(r"ghosts=(\d+)p/(\d+)m/(\d+)d/(\d+)s"),
    "cost": re.compile(r"frame mean ([\d.]+) ms max ([\d.]+) ms cpu (\d+)% mem (\d+) MB clients=(\d+) clientOut=([\d.]+) kB/s"),
    "link": re.compile(r"link out ([\d.]+) kB/s"),
    "work": re.compile(r"\| work mean ([\d.]+) ms p95 ([\d.]+) ms max ([\d.]+) ms wait ([\d.]+) ms util (\d+)% sat (\d+)% probe=(\w+)"),
}


def parse_stats_line(line):
    """The numbers of one 'Mesh stats' line as a dict, or None for any other line. Missing sections are left out."""
    if "Mesh stats" not in line:
        return None
    out = {}
    m = STATS_FIELDS["players"].search(line)
    if m:
        out["players"], out["owned"] = int(m.group(1)), int(m.group(2))
    m = STATS_FIELDS["ghosts"].search(line)
    if m:
        out["ghost_players"], out["ghost_monsters"] = int(m.group(1)), int(m.group(2))
        out["ghost_drops"], out["ghost_statics"] = int(m.group(3)), int(m.group(4))
    m = STATS_FIELDS["cost"].search(line)
    if m:
        out["frame_mean"], out["frame_max"] = float(m.group(1)), float(m.group(2))
        out["cpu"], out["mem"], out["clients"], out["client_out"] = int(m.group(3)), int(m.group(4)), int(m.group(5)), float(m.group(6))
    m = STATS_FIELDS["link"].search(line)
    if m:
        out["link_out"] = float(m.group(1))
    m = STATS_FIELDS["work"].search(line)
    if m:
        out["work_mean"], out["work_p95"], out["work_max"] = float(m.group(1)), float(m.group(2)), float(m.group(3))
        out["wait_mean"], out["util"], out["sat"], out["probe"] = float(m.group(4)), int(m.group(5)), int(m.group(6)), m.group(7)
    return out


# ------------------------------------------------------------------ load plans
def rect_overlap(a, b):
    """Area shared by two rectangles given as (minX, minZ, sizeX, sizeZ)."""
    w = min(a[0] + a[2], b[0] + b[2]) - max(a[0], b[0])
    h = min(a[1] + a[3], b[1] + b[3]) - max(a[1], b[1])
    return w * h if w > 0 and h > 0 else 0.0


GROUND_INSET = 0.5   # ServerMeshLoadGenerator keeps synthetic players this far off the map's outer edge


def rects_touch(a, b):
    """Whether two rectangles (minX, minZ, sizeX, sizeZ) share an edge or a corner (overlap counts too)."""
    return (max(a[0], b[0]) <= min(a[0] + a[2], b[0] + b[2])
            and max(a[1], b[1]) <= min(a[1] + a[3], b[1] + b[3]))


def load_plan(cells, total, area=None):
    """Split `total` synthetic players over cells [(id, rect)] in proportion to how much of the load area each covers.

    The load area is the union of the cells (spread load) or a square (x, z, r) of half-size r (a crowd). Counts are
    whole numbers that add up to the total (largest remainder), so every layout of an A/B carries the same load.
    """
    if total < 0:
        raise ValueError("total must not be negative")
    if area is None:
        weights = [(cid, rect[2] * rect[3]) for cid, rect in cells]
    else:
        x, z, r = area
        if r <= 0:
            raise ValueError("the crowd square needs a positive half-size")
        # The generator draws a crowd only where the square meets the ground kept GROUND_INSET off its outer edge, so
        # the weights use that same area: a square reaching the ground only in that strip gets no players.
        min_x = min(rect[0] for _, rect in cells) + GROUND_INSET
        min_z = min(rect[1] for _, rect in cells) + GROUND_INSET
        max_x = max(rect[0] + rect[2] for _, rect in cells) - GROUND_INSET
        max_z = max(rect[1] + rect[3] for _, rect in cells) - GROUND_INSET
        draw_x0, draw_z0 = max(min_x, x - r), max(min_z, z - r)
        draw_x1, draw_z1 = min(max_x, x + r), min(max_z, z + r)
        if draw_x1 <= draw_x0 or draw_z1 <= draw_z0:
            raise ValueError("the load area does not overlap any cell")
        drawn = (draw_x0, draw_z0, draw_x1 - draw_x0, draw_z1 - draw_z0)
        weights = [(cid, rect_overlap(rect, drawn)) for cid, rect in cells]
    weight_sum = sum(w for _, w in weights)
    if weight_sum <= 0:
        raise ValueError("the load area does not overlap any cell")
    exact = [(cid, total * w / weight_sum) for cid, w in weights]
    plan = {cid: int(v) for cid, v in exact}
    short = total - sum(plan.values())
    for cid, v in sorted(exact, key=lambda kv: kv[1] - int(kv[1]), reverse=True)[:short]:
        plan[cid] += 1
    return {cid: n for cid, n in plan.items() if n > 0}


def plan_env(plan):
    """A load plan as the MESH_LOAD_PLAN value."""
    return ",".join(f"{cid}:{n}" for cid, n in sorted(plan.items()))


# ------------------------------------------------------------------ work windows and A/B verdicts (DESIGN 0.71)
# One line per work-probe window (MeshMapNetworkManager_Load.WorkWindowLine), written in invariant culture.
WORK_WINDOW = re.compile(
    r"Work window ([\d.]+) s: frames (\d+) work mean ([\d.]+) ms p95 ([\d.]+) ms max ([\d.]+) ms wait ([\d.]+) ms "
    r"period ([\d.]+) ms budget ([\d.]+) ms sat ([\d.]+)% probe=(\w+) \| owned players (\d+) monsters (\d+) "
    r"\| puppets players (\d+) monsters (\d+) \| clients (\d+) cpu (-?\d+)%")
OWN_CELL = re.compile(r"Own cell from [^:]+: (\S+) of map (\S+), grid \(-?\d+,-?\d+\), "
                      r"world x \[(-?[\d.]+), (-?[\d.]+)\) z \[(-?[\d.]+), (-?[\d.]+)\)")
# A cell past these is saturated for the A/B (the plan's fast-path trigger): its work per frame is no longer the cost
# of its load at the tick rate, because time-driven work piles into fewer, longer frames.
SATURATED_SHARE = 20.0   # percent of frames that did not wait
SATURATED_UTIL = 0.95    # mean work over the frame budget


def parse_work_window(line):
    """The figures of one 'Work window' line, with its stamp as 'end', or None for any other line."""
    m = WORK_WINDOW.search(line)
    stamp = stamp_datetime(line) if m else None
    if stamp is None:
        return None
    g = m.groups()
    return {"end": stamp, "seconds": float(g[0]), "frames": int(g[1]), "work_mean": float(g[2]), "work_p95": float(g[3]),
            "work_max": float(g[4]), "wait_mean": float(g[5]), "period_mean": float(g[6]), "budget": float(g[7]),
            "sat": float(g[8]), "probe": g[9], "owned_players": int(g[10]), "owned_monsters": int(g[11]),
            "puppet_players": int(g[12]), "puppet_monsters": int(g[13]), "clients": int(g[14]), "cpu": int(g[15])}


def parse_own_cell(line):
    """(cell id, map id, (minX, minZ, sizeX, sizeZ)) from a cell's 'Own cell from ...' line, or None."""
    m = OWN_CELL.search(line)
    if not m:
        return None
    min_x, max_x, min_z, max_z = (float(v) for v in m.groups()[2:])
    return m.group(1), m.group(2), (min_x, min_z, max_x - min_x, max_z - min_z)


def aggregate_windows(windows, start, end):
    """Frame-weighted figures over the work windows that lie wholly inside [start, end] (datetimes), or None.

    A window's stamp is its end, at whole-second resolution, so one that began within a second before `start` counts.
    'probe' lists the windows' states in time order; 'work_p95' is the worst window's p95.
    """
    slack = datetime.timedelta(seconds=1)
    inside = sorted((w for w in windows
                     if w["end"] <= end and w["end"] - datetime.timedelta(seconds=w["seconds"]) >= start - slack),
                    key=lambda w: w["end"])
    frames = sum(w["frames"] for w in inside)
    if frames == 0:
        return None

    def weighted(key):
        return sum(w[key] * w["frames"] for w in inside) / frames

    def mean(key):
        return sum(w[key] for w in inside) / len(inside)

    budget = inside[-1]["budget"]
    work = weighted("work_mean")
    return {"windows": len(inside), "frames": frames, "seconds": sum(w["seconds"] for w in inside),
            "work_mean": work, "wait_mean": weighted("wait_mean"), "period_mean": weighted("period_mean"),
            "work_p95": max(w["work_p95"] for w in inside), "work_max": max(w["work_max"] for w in inside),
            "sat": weighted("sat"), "budget": budget, "util": work / budget if budget > 0 else 0.0,
            "probe": [w["probe"] for w in inside],
            "owned_players": mean("owned_players"), "owned_monsters": mean("owned_monsters"),
            "puppet_players": mean("puppet_players"), "puppet_monsters": mean("puppet_monsters"),
            "clients": mean("clients"), "cpu": mean("cpu")}


def is_saturated(stats):
    """Whether a cell's window figures show it saturated (see SATURATED_SHARE / SATURATED_UTIL)."""
    return stats["sat"] > SATURATED_SHARE or stats["util"] >= SATURATED_UTIL


def rotate(items, k):
    """items rotated left by k: run r of an A/B takes rotate(layouts, r), so each layout runs in each position once."""
    if not items:
        return []
    k %= len(items)
    return list(items[k:]) + list(items[:k])


def point_verdict(ratios, threshold, min_runs):
    """GO when every paired ratio is at or under the threshold, NO-GO when every one is over it, MARGINAL when they
    straddle it, INSUFFICIENT with fewer than min_runs."""
    if len(ratios) < max(1, min_runs):
        return "INSUFFICIENT"
    if max(ratios) <= threshold:
        return "GO"
    if min(ratios) > threshold:
        return "NO-GO"
    return "MARGINAL"


def overall_verdict(verdicts):
    """GO only when every point is GO. Any NO-GO decides NO-GO; otherwise a point without a verdict leaves none."""
    if not verdicts:
        return "NO VERDICT"
    if "NO-GO" in verdicts:
        return "NO-GO"
    if any(v not in ("GO", "MARGINAL") for v in verdicts):
        return "NO VERDICT"
    return "MARGINAL" if "MARGINAL" in verdicts else "GO"


def crossing_rate(players, width, height, cut_length, mean_step=12.5, think_seconds=4.0):
    """Crossings per second of a straight cut by synthetic players spread evenly over width x height, wandering a
    mean_step metres in a random direction every think_seconds (ServerMeshLoadGenerator's brain: 5-20 m every 2-6 s).
    Density x cut length x mean |step across the cut| (mean step x 2/pi) / think time. An upper estimate: a player
    that is fighting does not wander."""
    if width <= 0 or height <= 0 or think_seconds <= 0:
        raise ValueError("the area and the think time must be positive")
    return players / (width * height) * cut_length * mean_step * 2.0 / 3.141592653589793 / think_seconds


# ---- dynamic cells (DESIGN 0.72): what the layout grade reads from the cells' logs ----
LAYOUT_APPLIED = re.compile(r"Layout ([0-9a-f]+)/(\d+) applied: (\S+) owns ")
# Slice 2b decision 14: a Static map's cell beside a Dynamic map applies only the band-neighbour part of a broadcast.
NEIGHBOURS_APPLIED = re.compile(r"Layout ([0-9a-f]+)/(\d+) neighbours applied: (\S+) band ?(.*?)\s*$")
LAYOUT_GATE_WARNING = re.compile(r"Layout gate: cell \S+ has had no answer")
# Case-sensitive on purpose: the work probe's "automatic splits" is not an operation.
OP_LINE = re.compile(r"\b(Split|Merge|Retire|Retired)\b")
# The MeshTrace stamp of a player registering on a server ("S"); the client's own lines carry "C".
PLAYER_REGISTERED = re.compile(r" S (?:f\d+ )?player registered ")
REGISTRY_SCRIPT_GUID = "5c1e2a7b9d3f4e6a8b0c1d2e3f4a5b6c"   # MeshWorldRegistry.cs.meta
CELL_MODES = ("Static", "Dynamic")                            # MeshCellMode, by value
ALL_MAPS = "all"


def asset_cell_modes(asset_path):
    """{mapId: 'Static' | 'Dynamic'} from a registry asset's YAML; a layout saved before cell modes existed is Static."""
    modes = {}
    current = None
    with open(asset_path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = re.match(r"^\s*- mapId: (.*?)\s*$", line)
            if m:
                current = m.group(1).strip("'\"")
                modes[current] = CELL_MODES[0]
                continue
            m = re.match(r"^\s+cellMode: (\d+)\s*$", line)
            if m and current is not None:
                value = int(m.group(1))
                modes[current] = CELL_MODES[value] if value < len(CELL_MODES) else f"unknown({value})"
    return modes


def parse_cell_modes(value):
    """MESH_CELL_MODE read as MeshLayoutOverride.ParseCellModes reads it: {mapId or 'all': mode}; ValueError if malformed."""
    modes = {}
    if not value or not value.strip():
        return modes
    for raw in value.split(","):
        part = raw.strip()
        if ":" not in part:
            raise ValueError(f"MESH_CELL_MODE: '{part}' is not <mapId|all>:<Static|Dynamic>")
        map_id, name = (s.strip() for s in part.split(":", 1))
        if not map_id:
            raise ValueError(f"MESH_CELL_MODE: '{part}' names no map")
        if map_id.lower() == ALL_MAPS:
            map_id = ALL_MAPS
        mode = next((m for m in CELL_MODES if m.lower() == name.lower()), None)
        if mode is None:
            raise ValueError(f"MESH_CELL_MODE: '{name}' in '{part}' is neither Static nor Dynamic")
        if map_id in modes:
            raise ValueError(f"MESH_CELL_MODE: map '{map_id}' is given twice")
        modes[map_id] = mode
    return modes


def dynamic_maps(env_value, asset_modes, map_ids):
    """The maps that run Dynamic: the asset's own modes, then MESH_CELL_MODE on top ('all' first, a map's own entry wins)."""
    modes = {m: asset_modes.get(m, CELL_MODES[0]) for m in map_ids}
    overrides = parse_cell_modes(env_value)
    if ALL_MAPS in overrides:
        for m in modes:
            modes[m] = overrides[ALL_MAPS]
    for m, mode in overrides.items():
        if m != ALL_MAPS and m in modes:
            modes[m] = mode
    return {m for m, mode in modes.items() if mode == "Dynamic"}


def layout_apply_violations(cell, info_lines, dynamic):
    """What a cell's info log (in file order) breaks of the 2a layout rules, as messages; empty when it keeps them.

    A cell of a Dynamic map, in each process lifetime (from its 'Own cell from' line): a layout applied before the first
    player registers, and at most one per coordinator incarnation (a central restart may add one). A cell of a Static
    map: none at all, though a Static neighbour's band-neighbour applies (NEIGHBOURS_APPLIED, Slice 2b) are its own
    business; that line on a Dynamic map's cell is a violation. Lines before the first 'Own cell from' belong to a
    process that started earlier and are not graded. A dynamic cell's 'Own cell from layout' line comes with its first
    apply, so it does not start a lifetime.
    """
    lifetimes = []
    current = None
    for line in info_lines:
        if OWN_CELL.search(line) and "Own cell from layout:" not in line:
            current = []
            lifetimes.append(current)
        if current is not None:
            current.append(line)
    problems = []
    for n, lines in enumerate(lifetimes, 1):
        applied = [(i, m) for i, line in enumerate(lines) for m in [LAYOUT_APPLIED.search(line)] if m]
        neighbours = sum(1 for line in lines if NEIGHBOURS_APPLIED.search(line))
        if dynamic and neighbours:
            problems.append(f"{cell}: lifetime {n}: {neighbours} Static-neighbour apply line(s) on a Dynamic map")
        if not dynamic:
            if applied:
                problems.append(f"{cell}: {len(applied)} layout(s) applied on a Static map")
            continue
        if not applied:
            problems.append(f"{cell}: lifetime {n}: no layout applied")
            continue
        first_player = next((i for i, line in enumerate(lines) if PLAYER_REGISTERED.search(line)), None)
        if first_player is not None and applied[0][0] > first_player:
            problems.append(f"{cell}: lifetime {n}: a player registered before the layout was applied")
        per_incarnation = {}
        for _, m in applied:
            per_incarnation[m.group(1)] = per_incarnation.get(m.group(1), 0) + 1
        for incarnation, times in sorted(per_incarnation.items()):
            if times > 1:
                problems.append(f"{cell}: lifetime {n}: {times} layouts applied from coordinator {incarnation}")
    return problems


def assets_with_script(assets_dir, guid):
    """Every .asset under assets_dir whose m_Script is the script with this guid (for the registry: every world), sorted."""
    rx = re.compile(r"m_Script: \{fileID: \d+, guid: " + re.escape(guid) + r",")
    found = []
    for path in glob.glob(os.path.join(assets_dir, "**", "*.asset"), recursive=True):
        try:
            with open(path, encoding="utf-8", errors="replace") as f:
                head = f.read(4096)
        except OSError:
            continue
        if rx.search(head):
            found.append(path)
    return sorted(found)
