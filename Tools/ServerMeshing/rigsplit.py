"""Pure graders of Slice 2b's manual split (Docs/ServerMeshing/slices/slice2b_design.md, revision 1).

What regress_split.py reads: the cells' layout applies graded by rectangle (decision 14, 18), the operation's mirrored
status file (decision 17), the auto client's MIGRATION and RESULT lines (decision 15), and the map-side lines of the
handoffs a split causes (players, monsters, statics, synthetic players). Nothing here touches a file, a process or the
database; every function takes its lines as arguments (python -m unittest Tools/ServerMeshing/test_regress_split.py).
Split out of rigtools under the ~500-line rule; it only reads rigtools' log formats.
"""
import collections
import re

import rigtools

# ------------------------------------------------------------------ rectangles
# MeshMapNetworkManager_Layout.Span: "x[minX,maxX) z[minZ,maxZ)" with whole metres, or "nothing" for an empty rect.
SPAN = r"(nothing|x\[-?\d+(?:\.\d+)?,-?\d+(?:\.\d+)?\) z\[-?\d+(?:\.\d+)?,-?\d+(?:\.\d+)?\))"
SPAN_PARTS = re.compile(r"x\[(-?[\d.]+),(-?[\d.]+)\) z\[(-?[\d.]+),(-?[\d.]+)\)")
APPLY = re.compile(r"Layout ([0-9a-f]+)/(\d+) applied: (\S+) owns " + SPAN + r" interest " + SPAN)
OWN_FROM_LAYOUT = re.compile(r"Own cell from layout: (\S+) of map ")
NOTHING = None          # an empty rect ("nothing")
ANY = "any"             # a rect the grade does not look at
RECT_TOLERANCE_M = 0.6  # the span is printed to whole metres


def parse_span(text):
    """(minX, minZ, sizeX, sizeZ) of a Span, or NOTHING for "nothing"; ValueError for anything else."""
    if text == "nothing":
        return NOTHING
    m = SPAN_PARTS.fullmatch(text)
    if not m:
        raise ValueError(f"'{text}' is not a layout span")
    min_x, max_x, min_z, max_z = (float(v) for v in m.groups())
    return (min_x, min_z, max_x - min_x, max_z - min_z)


def format_rect(rect):
    """A rect as the Span prints it ("nothing" for NOTHING, "any" for ANY)."""
    if rect is NOTHING or rect == ANY:
        return "nothing" if rect is NOTHING else ANY
    return f"x[{rect[0]:g},{rect[0] + rect[2]:g}) z[{rect[1]:g},{rect[1] + rect[3]:g})"


def same_rect(a, b, tolerance=RECT_TOLERANCE_M):
    """Whether two rects agree (ANY agrees with everything, NOTHING only with NOTHING)."""
    if a == ANY or b == ANY:
        return True
    if a is NOTHING or b is NOTHING:
        return a is NOTHING and b is NOTHING
    return all(abs(p - q) <= tolerance for p, q in zip(a, b))


def parse_apply(line):
    """{incarnation, version, cell, owned, interest} of a dynamic cell's 'Layout ... applied:' line, or None."""
    m = APPLY.search(line)
    if not m:
        return None
    return {"incarnation": m.group(1), "version": int(m.group(2)), "cell": m.group(3),
            "owned": parse_span(m.group(4)), "interest": parse_span(m.group(5))}


def split_rects(rect, axis, at, side):
    """(kept, given) of a base rect cut at `at` along axis 'x' or 'z': `given` is the child's side ('+' the high one).
    ValueError for a cut that does not fall strictly inside the rect, or an unknown axis or side."""
    if axis not in ("x", "z") or side not in ("+", "-"):
        raise ValueError(f"a cut is along x or z, to + or -, not {axis} {side}")
    min_x, min_z, size_x, size_z = rect
    lo, size = (min_x, size_x) if axis == "x" else (min_z, size_z)
    if not lo < at < lo + size:
        raise ValueError(f"the cut {axis} = {at:g} is not inside {format_rect(rect)}")
    if axis == "x":
        low, high = (min_x, min_z, at - min_x, size_z), (at, min_z, min_x + size_x - at, size_z)
    else:
        low, high = (min_x, min_z, size_x, at - min_z), (min_x, at, size_x, min_z + size_z - at)
    return (low, high) if side == "+" else (high, low)


Split = collections.namedtuple("Split", "map_id base base_rect axis at side child")


def expected_applies(split, cell, own_rect):
    """The (owned, interest) of every layout a cell of the split's map applies in the scenario, in order.

    The base P: its first layout, e1 (X Pending), e2 (P owns P minus R, interest still P), e3 (interest P minus R).
    The child X: e1 staged (owns nothing, interest R), e2 staged (owns R), e3; the broadcasts of e1 and e2 carry the
    pair X already holds, so they log nothing there. Every other cell: its first layout and three extra, owning its
    own rect each time (its interest is not graded)."""
    kept, given = split_rects(split.base_rect, split.axis, split.at, split.side)
    p = split.base_rect
    if cell == split.base:
        return [(p, p), (p, p), (kept, p), (kept, kept)]
    if cell == split.child:
        return [(NOTHING, given), (given, given), (given, given)]
    return [(own_rect, ANY)] * 4


def lifetimes(info_lines, child=None):
    """The process lifetimes in a cell's info lines: each from an 'Own cell from' line other than 'from layout', or, for a
    child (whose first own-cell line comes from its first layout), from 'Own cell from layout: <child>'."""
    out, current = [], None
    for line in info_lines:
        if child is not None:
            m = OWN_FROM_LAYOUT.search(line)
            starts = m is not None and m.group(1) == child
        else:
            starts = rigtools.OWN_CELL.search(line) is not None and "Own cell from layout:" not in line
        if starts:
            current = []
            out.append(current)
        if current is not None:
            current.append(line)
    return out


def split_apply_violations(cell, info_lines, split, expected=None, label="the split"):
    """What a cell of the split's map breaks of the rectangle grade (decision 18), as messages; empty when it keeps it.

    One process lifetime; every apply from one coordinator incarnation with rising versions, the first before any
    player registers; exactly the applies of expected_applies, owning (and for P and X, with an interest of) the rects
    it names; no Static-neighbour apply line (that one is a Static map's). `expected` (Slice 2c's merge grade) replaces
    expected_applies: a function (cell, own_rect) giving [(owned, interest)] or [(owned, interest, pair)], where a pair
    other than None must be the apply's '<incarnation>/<version>'; `label` names the op in the messages."""
    child = cell if cell == split.child else None
    lives = lifetimes(info_lines, child)
    if not lives:
        return [f"{cell}: no process lifetime since the scenario started"]
    problems = []
    if len(lives) > 1:
        problems.append(f"{cell}: {len(lives)} process lifetimes (restarted during {label})")
    lines = lives[0]
    own = rigtools.parse_own_cell(lines[0]) if child is None else None
    own_rect = own[2] if own else ANY
    if cell == split.base and not same_rect(own_rect, split.base_rect):
        problems.append(f"{cell}: own cell {format_rect(own_rect)}, {label} expects {format_rect(split.base_rect)}")
    if any(rigtools.NEIGHBOURS_APPLIED.search(line) for line in lines):
        problems.append(f"{cell}: a Static-neighbour apply line on a Dynamic map")
    applied = [(i, a) for i, line in enumerate(lines) for a in [parse_apply(line)] if a]
    first_player = next((i for i, line in enumerate(lines) if rigtools.PLAYER_REGISTERED.search(line)), None)
    if applied and first_player is not None and applied[0][0] > first_player:
        problems.append(f"{cell}: a player registered before the first layout was applied")
    incarnations = sorted({a["incarnation"] for _, a in applied})
    if len(incarnations) > 1:
        problems.append(f"{cell}: layouts from {len(incarnations)} coordinator incarnations ({', '.join(incarnations)})")
    versions = [a["version"] for _, a in applied]
    if any(b <= a for a, b in zip(versions, versions[1:])):
        problems.append(f"{cell}: layout versions not rising ({', '.join(map(str, versions))})")
    expected = (expected or (lambda c, r: expected_applies(split, c, r)))(cell, own_rect)
    if len(applied) != len(expected):
        problems.append(f"{cell}: {len(applied)} layout(s) applied, {label} expects {len(expected)}")
    for n, ((_, a), want) in enumerate(zip(applied, expected), 1):
        owned, interest, pair = (tuple(want) + (None,))[:3]
        at = f"{a['incarnation']}/{a['version']}"
        if pair is not None and at != pair:
            problems.append(f"{cell}: apply {n} is layout {at}, {label} expects {pair}")
        if not same_rect(a["owned"], owned) or not same_rect(a["interest"], interest):
            problems.append(f"{cell}: apply {n} ({a['incarnation']}/{a['version']}) owns {format_rect(a['owned'])} "
                            f"interest {format_rect(a['interest'])}, expected owns {format_rect(owned)} "
                            f"interest {format_rect(interest)}")
    return problems


def neighbour_bands(lines, cell=None):
    """[(pair, cell, [band ids])] of every Static-neighbour apply line (of `cell` only, when given), in order."""
    out = []
    for line in lines:
        m = rigtools.NEIGHBOURS_APPLIED.search(line)
        if m and (cell is None or m.group(3) == cell):
            ids = [i for i in re.split(r"[\s,]+", m.group(4)) if i and i not in ("nothing", "none")]
            out.append((f"{m.group(1)}/{m.group(2)}", m.group(3), ids))
    return out


# ------------------------------------------------------------------ the op's status file (decision 17)
OP_STATE = re.compile(r"^(Split|Merge) (\S+): (.*)$")   # (kind, op, state)
REFUSED = re.compile(r"^Layout op refused: (.*)$")
STEP_PAIR = re.compile(r"^(e[123]) broadcast ([0-9a-f]+/\d+)$")
DRAIN_TIMEOUT = re.compile(r"^drain timeout, (\d+) straggler")


def parse_op_status(lines, seq, kind="Split"):
    """The command `seq`'s outcome from Temp/ServerMesh.LayoutCommandStatus.txt's lines ('<seq> Split <op>: <state>'
    or '<seq> Layout op refused: <rule>'): {kind, op, requested, refused, states, pairs, drain, stragglers, done,
    aborted, frozen, other}. Lines of other sequences are ignored; lines of this one the grade does not know, an op
    line of the other kind (Slice 2c's Merge) included, land in 'other'."""
    out = {"kind": kind, "op": None, "requested": None, "refused": None, "states": [], "pairs": {}, "drain": None,
           "stragglers": None, "done": False, "aborted": None, "frozen": None, "other": []}
    for raw in lines:
        parts = raw.strip().split(None, 1)
        if len(parts) != 2 or parts[0] != str(seq):
            continue
        text = parts[1]
        m = REFUSED.match(text)
        if m:
            out["refused"] = m.group(1)
            continue
        m = OP_STATE.match(text)
        if not m or m.group(1) != kind:
            out["other"].append(text)
            continue
        out["op"] = out["op"] or m.group(2)
        state = m.group(3)
        out["states"].append(state)
        step = STEP_PAIR.match(state)
        timeout = DRAIN_TIMEOUT.match(state)
        if state.startswith("requested "):
            out["requested"] = state[len("requested "):]
        elif step:
            out["pairs"][step.group(1)] = step.group(2)
        elif state == "drain gate open":
            out["drain"] = "open"
        elif timeout:
            out["drain"], out["stragglers"] = "timeout", int(timeout.group(1))
        elif state.startswith("straggler"):
            out["stragglers"] = max(out["stragglers"] or 0, 1)
        elif state == "done":
            out["done"] = True
        elif state.startswith("aborted"):
            out["aborted"] = state[len("aborted"):].strip() or "aborted"
        elif state.startswith("frozen"):
            out["frozen"] = state[len("frozen"):].strip() or "frozen"
    return out


def op_outcome(status):
    """'refused', 'aborted', 'frozen' or 'done' once the op has ended (the first that applies), else None."""
    for key in ("refused", "aborted", "frozen"):
        if status[key]:
            return key
    return "done" if status["done"] else None


def command_line(seq, base, axis, at, side, child):
    """The one line of Temp/ServerMesh.LayoutCommand.txt: '<seq> split <baseCell> <x|z> <at> <+|-> <childId>'."""
    if axis not in ("x", "z") or side not in ("+", "-"):
        raise ValueError(f"a cut is along x or z, to + or -, not {axis} {side}")
    return f"{seq} split {base} {axis} {at:g} {side} {child}"


# ------------------------------------------------------------------ the auto client (decision 15)
MIGRATION = re.compile(r"MIGRATION (\d+): gap=(-?[\d.]+)(?:ms)? ready=(True|False) jump=(-?[\d.]+)m? "
                       r"pos=(-?[\d.]+), ?(-?[\d.]+)")
STANDING = re.compile(r"Standing at \((-?[\d.]+), (-?[\d.]+), (-?[\d.]+)\)")
RESULT_FIELDS = {"migrations": (r"\bmigrations=(\d+)", int), "allReady": (r"\ballReady=(True|False)", "True".__eq__),
                 "maxGap": (r"\bmaxGap=(-?[\d.]+)", float), "maxJump": (r"\bmaxJump=(-?[\d.]+)", float),
                 "yield": (r"\byield=(\d+)", int), "pickup": (r"\bpickup=(True|False)", "True".__eq__),
                 "reached": (r"\breached=(True|False)", "True".__eq__)}


def parse_migrations(lines):
    """[{n, gap, ready, jump, x, z}] of a bot log's MIGRATION lines, in order."""
    out = []
    for line in lines:
        m = MIGRATION.search(line)
        if m:
            out.append({"n": int(m.group(1)), "gap": float(m.group(2)), "ready": m.group(3) == "True",
                        "jump": float(m.group(4)), "x": float(m.group(5)), "z": float(m.group(6))})
    return out


def parse_result(line):
    """The fields of a RESULT line (RESULT_FIELDS, those present) plus 'passed'; {} for an empty line."""
    if not line:
        return {}
    out = {"passed": "-> PASS" in line}
    for key, (pattern, convert) in RESULT_FIELDS.items():
        m = re.search(pattern, line)
        if m:
            out[key] = convert(m.group(1))
    return out


def standing_at(lines):
    """(x, z) of a bot log's first 'Standing at' line, or None."""
    for line in lines:
        m = STANDING.search(line)
        if m:
            return float(m.group(1)), float(m.group(3))
    return None


def migration_problems(result, expected, max_gap_ms=1000.0):
    """Why a bot's stand RESULT fails the split's migration checks (decision 18), as messages: `expected` migrations,
    and when 1 or more, every one ready and the worst gap under max_gap_ms."""
    if not result:
        return ["no RESULT stand line"]
    problems = []
    if result.get("migrations") != expected:
        problems.append(f"migrations={result.get('migrations')}, expected {expected}")
    if expected > 0:
        if result.get("allReady") is not True:
            problems.append(f"allReady={result.get('allReady')}")
        gap = result.get("maxGap")
        if gap is None or gap >= max_gap_ms:
            problems.append(f"maxGap={gap} ms, expected under {max_gap_ms:g}")
    return problems


def side_of(point, axis, at):
    """'+' when a point (x, z) lies at or past the cut, else '-'."""
    return "+" if (point[0] if axis == "x" else point[1]) >= at else "-"


# ------------------------------------------------------------------ the handoffs a split makes (map side)
def handoff_start(name, from_zone, to_zone):
    """MeshMapNetworkManager's 'Handoff START <name> (<id>) zone <from>-><to> ' for one character; the ids escaped, so
    'C->C.1' never matches 'C->C11' or 'C->C.10'."""
    return re.compile(r"Handoff START " + re.escape(name) + r" \([^)]*\) zone " + re.escape(from_zone) + "->"
                      + re.escape(to_zone) + r"(?![\w.])")


MONSTER_HANDOFF = re.compile(r"Monster (promoted|spawned) by handoff (\S+) (\S+?)->(\S+) at ")
STATIC_HANDOFF = re.compile(r"Static handoff (\S+) zone (\S+?)->([^\s:]+): (sent|promoted|spawned|failed|released)\b")
SYNTHETIC_ADOPTED = re.compile(r"Synthetic adopted (\S+) zone (\S+?)->(\S+?)(?=\s|$)")
PREWARM_WINDOW = re.compile(r"Prewarm window ([\d.]+) s: queue (\d+) spawned (\d+) \| max frame ([\d.]+) ms")
PUPPET_MOTION_DEFAULT = re.compile(r"Puppet motion: Kinematic \(Dynamic map default\)")
GHOST_SPAWNED = re.compile(r"Ghost spawned Player (\S+) \([^)]*\) from zone (\S+) at "
                           r"\((-?[\d.]+), (-?[\d.]+), (-?[\d.]+)\)")
# An existing ghost whose owner moved (the parent kept the puppet and the child took ownership); the C2 check takes it as
# well as a fresh spawn, with <new> as the owner.
GHOST_OWNER_CHANGED = re.compile(r"Ghost owner changed Player (\S+) \([^)]*\) zone \S+?->(\S+) at "
                                 r"\((-?[\d.]+), (-?[\d.]+), (-?[\d.]+)\)")
# Map-side lines a passing split never logs: a child retired by an abort, a merge or retire of 2c/2d, an op's abort or
# freeze mirrored into a cell's log. (The coordinator's own 'Split <op>:' lines are the central's, read from the status.)
SPLIT_OP_PROBLEM = re.compile(r"retired before it owned anything|\b(Merge|Retire)\b|Split \S+: (aborted|frozen)\b")


def count_kind(lines, rx, kind, from_zone, to_zone):
    """How many lines a (kind, ..., from, to) regex (MONSTER_HANDOFF, STATIC_HANDOFF) matches for this kind and route."""
    n = 0
    for line in lines:
        m = rx.search(line)
        if not m:
            continue
        groups = m.groups()
        route = groups[-3:-1] if rx is STATIC_HANDOFF else groups[-2:]
        what = groups[-1] if rx is STATIC_HANDOFF else groups[0]
        if what == kind and tuple(route) == (from_zone, to_zone):
            n += 1
    return n


GHOST_MONSTER = re.compile(r"Ghost spawned Monster\b.*?\(([^()\s]+)\) from zone (\S+) at ")


def mesh_id_parts(mesh_id):
    """('<cell>:<incarnation>', n) of a mesh id '<cell>:<incarnation>:<n>', or (mesh_id, None) when it has no number."""
    head, _, tail = mesh_id.rpartition(":")
    return (head, int(tail)) if head and tail.isdigit() else (mesh_id, None)


def monster_spawns(lines, prewarm_lines, from_zone, to_zone):
    """The monsters 'spawned by handoff' on the route in `lines`, split into (existing, respawned) id lists. A spawn
    area numbers its monsters upward within an incarnation, so a monster whose number is above every one the child
    ghosted in `prewarm_lines` (its lines before e2) was spawned after the pre-warm: a respawn the owner handed over
    before the child had a puppet. One at or below it existed during the pre-warm and should have been promoted."""
    highest = {}
    for line in prewarm_lines:
        m = GHOST_MONSTER.search(line)
        if m:
            head, n = mesh_id_parts(m.group(1))
            if n is not None:
                highest[head] = max(highest.get(head, n), n)
    existing, respawned = [], []
    for line in lines:
        m = MONSTER_HANDOFF.search(line)
        if not m or m.group(1) != "spawned" or (m.group(3), m.group(4)) != (from_zone, to_zone):
            continue
        head, n = mesh_id_parts(m.group(2))
        (respawned if n is not None and head in highest and n > highest[head] else existing).append(m.group(2))
    return existing, respawned


def until_applied(lines, pair):
    """The lines before the first 'Layout <pair> applied' (or 'neighbours applied') line; all of them when `pair` is None
    or never applied."""
    if pair:
        needle = re.compile(rf"Layout {re.escape(pair)} (neighbours )?applied:")
        for i, line in enumerate(lines):
            if needle.search(line):
                return lines[:i]
    return list(lines)


def adoptions(lines, from_zone, to_zone):
    """The names of the synthetic players adopted on the route, in order."""
    return [m.group(1) for line in lines for m in [SYNTHETIC_ADOPTED.search(line)]
            if m and (m.group(2), m.group(3)) == (from_zone, to_zone)]


def prewarm_max_frame(lines):
    """The worst 'max frame' of the Prewarm window lines (ms), or None when there are none."""
    frames = [float(m.group(4)) for line in lines for m in [PREWARM_WINDOW.search(line)] if m]
    return max(frames) if frames else None


def ghost_at(lines, name, owner, x, z, tolerance=3.0):
    """Whether a 'Ghost spawned Player <name> ... from zone <owner>' line, or a 'Ghost owner changed Player <name> ...
    zone <old>-><owner>' line, puts it within `tolerance` metres of (x, z)."""
    for line in lines:
        m = GHOST_SPAWNED.search(line) or GHOST_OWNER_CHANGED.search(line)
        if m and m.group(1) == name and m.group(2) == owner:
            if abs(float(m.group(3)) - x) <= tolerance and abs(float(m.group(5)) - z) <= tolerance:
                return True
    return False
