"""Pure layout timelines for scenarios that run more than one operation on a Dynamic map (DESIGN 0.88).

rigsplit grades one split and rigmerge one split and its merge back. A scenario that splits a child, merges two
children, collapses a child into a sibling or splits two families one after the other needs the same rectangle grade
over a sequence: `timeline` plays the operations on the map's owned rectangles and gives every cell the (owned,
interest, pair) of each layout it applies, by the rules rigsplit.expected_applies and rigmerge.merge_expected_applies
already state for one operation:

  split P -> X (kept K, given G): e1  P (P, P), X staged (nothing, G); e2  P (K, P), X staged (G, G); e3  P (K, K),
    X (G, G). X's e1 and e2 broadcasts carry the pair it already holds, so they log nothing there.
  merge S <- R (union U): e1  S (S, U), R (R, R); e2  S staged (U, U), R (nothing, R); e3  S (U, U); tidy (only when
    the family is one cell again) S (U, U). R quits after e2. A collapse is the same with R dead: it applies nothing.
  every other live cell of the map applies each broadcast owning its own rectangle (its interest is not graded).

`apply_violations` holds a cell's info lines to its list as rigsplit.split_apply_violations does (one lifetime, one
coordinator incarnation, rising versions, the first apply before any player, exactly the expected applies). Nothing here
touches a file or a process (python -m unittest Tools/ServerMeshing/test_riglayout.py).
"""
import collections

import rigsplit
import rigtools

NOTHING, ANY = rigsplit.NOTHING, rigsplit.ANY
# The test map's static cells (ServerMeshWorld.asset: grid origin (-300, -300), 200 m cells, 3 x 3).
TEST_MAP_RECTS = {cell: (-300.0 + 200.0 * (i % 3), -300.0 + 200.0 * (i // 3), 200.0, 200.0)
                  for i, cell in enumerate(("SW", "S", "SE", "W", "C", "E", "NW", "N", "NE"))}

# One operation of a timeline. kind 'split': a = the parent, b = the child, cut = (axis, at, side); kind 'merge': a =
# the survivor, b = the retiring cell, dead = the retiring cell applies nothing (a collapse of a cell that is down).
# pairs: the op's broadcast pairs by step (e1, e2, e3, tidy), each '<incarnation>/<version>' or None (not graded).
Op = collections.namedtuple("Op", "kind a b cut pairs dead")


def split_op(parent, child, axis, at, side, pairs=None):
    return Op("split", parent, child, (axis, at, side), dict(pairs or {}), False)


def merge_op(survivor, retiring, pairs=None, dead=False):
    return Op("merge", survivor, retiring, None, dict(pairs or {}), dead)


def union(a, b, tolerance=rigsplit.RECT_TOLERANCE_M):
    """The rectangle two rectangles make together when they share a full edge, else None."""
    ax, az, aw, ad = a
    bx, bz, bw, bd = b
    close = lambda p, q: abs(p - q) <= tolerance   # noqa: E731
    if close(az, bz) and close(ad, bd) and (close(ax + aw, bx) or close(bx + bw, ax)):
        return (min(ax, bx), az, aw + bw, ad)
    if close(ax, bx) and close(aw, bw) and (close(az + ad, bz) or close(bz + bd, az)):
        return (ax, min(az, bz), aw, ad + bd)
    return None


def timeline(cells, ops, members=None):
    """{cell: [(owned, interest, pair)]} of every layout each cell of the map applies, in order, for the operations
    `ops` played on `cells` ({static cell: its rectangle}). Each static cell starts with its first layout (its own
    rectangle; interest graded only for a cell some op names, or one in `members`); a child starts at its split's
    staged e1. ValueError for an op the map cannot make (a cut outside its parent, a merge of cells that share no full
    edge, an unknown cell)."""
    owned = dict(cells)
    named = set(members or ()) | {c for op in ops for c in (op.a, op.b)}
    out = {cell: [(rect, rect if cell in named else ANY, None)] for cell, rect in cells.items()}

    def others(pair, skip):
        for cell, rect in owned.items():
            if cell not in skip:
                out[cell].append((rect, ANY, pair))

    for op in ops:
        p = op.pairs
        if op.kind == "split":
            if op.a not in owned or op.b in owned:
                raise ValueError(f"split {op.a} -> {op.b}: {op.a} is not live or {op.b} already is")
            whole = owned[op.a]
            kept, given = rigsplit.split_rects(whole, *op.cut)
            out.setdefault(op.b, [])
            steps = (("e1", (whole, whole), (NOTHING, given)), ("e2", (kept, whole), (given, given)),
                     ("e3", (kept, kept), (given, given)))
            for step, parent, child in steps:
                out[op.a].append(parent + (p.get(step),))
                out[op.b].append(child + (p.get(step),))
                others(p.get(step), {op.a, op.b})
            owned[op.a], owned[op.b] = kept, given
        elif op.kind == "merge":
            if op.a not in owned or op.b not in owned:
                raise ValueError(f"merge {op.a} <- {op.b}: both must be live")
            s, r = owned[op.a], owned[op.b]
            u = union(s, r)
            if u is None:
                raise ValueError(f"merge {op.a} <- {op.b}: {rigsplit.format_rect(s)} and {rigsplit.format_rect(r)} "
                                 "share no full edge")
            steps = [("e1", (s, u), (r, r)), ("e2", (u, u), (NOTHING, r)), ("e3", (u, u), None)]
            if "tidy" in p:
                steps.append(("tidy", (u, u), None))
            for step, survivor, retiring in steps:
                out[op.a].append(survivor + (p.get(step),))
                if retiring is not None and not op.dead:
                    out[op.b].append(retiring + (p.get(step),))
                others(p.get(step), {op.a, op.b} if step in ("e1", "e2") else {op.a})
                if step == "e2":
                    del owned[op.b]
            owned[op.a] = u
        else:
            raise ValueError(f"unknown op kind {op.kind}")
    return out


def final_owned(cells, ops):
    """{live cell: its owned rectangle} after the operations (the family's store entries and the cut a scenario ends with)."""
    owned = dict(cells)
    for op in ops:
        if op.kind == "split":
            owned[op.a], owned[op.b] = rigsplit.split_rects(owned[op.a], *op.cut)
        else:
            owned[op.a] = union(owned[op.a], owned.pop(op.b))
    return owned


def apply_violations(cell, info_lines, expected, child=False, own_rect=None, label="the scenario"):
    """What a cell's info lines break of its expected applies ([(owned, interest, pair)], timeline's), as messages.

    One process lifetime (a child's starts at 'Own cell from layout: <cell>', rigsplit.lifetimes); a static cell's own
    rectangle is `own_rect` when given; no Static-neighbour apply line; every apply from one coordinator incarnation
    with rising versions, the first before any player registers; exactly the expected applies, a pair other than None
    being the apply's '<incarnation>/<version>'."""
    lives = rigsplit.lifetimes(info_lines, cell if child else None)
    if not lives:
        return [f"{cell}: no process lifetime since the scenario started"]
    problems = []
    if len(lives) > 1:
        problems.append(f"{cell}: {len(lives)} process lifetimes (restarted during {label})")
    lines = lives[0]
    if own_rect is not None and not child:
        own = rigtools.parse_own_cell(lines[0])
        if own is None or not rigsplit.same_rect(own[2], own_rect):
            problems.append(f"{cell}: own cell {rigsplit.format_rect(own[2]) if own else 'unknown'}, {label} expects "
                            f"{rigsplit.format_rect(own_rect)}")
    if any(rigtools.NEIGHBOURS_APPLIED.search(line) for line in lines):
        problems.append(f"{cell}: a Static-neighbour apply line on a Dynamic map")
    applied = [(i, a) for i, line in enumerate(lines) for a in [rigsplit.parse_apply(line)] if a]
    first_player = next((i for i, line in enumerate(lines) if rigtools.PLAYER_REGISTERED.search(line)), None)
    if applied and first_player is not None and applied[0][0] > first_player:
        problems.append(f"{cell}: a player registered before the first layout was applied")
    incarnations = sorted({a["incarnation"] for _, a in applied})
    if len(incarnations) > 1:
        problems.append(f"{cell}: layouts from {len(incarnations)} coordinator incarnations ({', '.join(incarnations)})")
    versions = [a["version"] for _, a in applied]
    if any(b <= a for a, b in zip(versions, versions[1:])):
        problems.append(f"{cell}: layout versions not rising ({', '.join(map(str, versions))})")
    if len(applied) != len(expected):
        problems.append(f"{cell}: {len(applied)} layout(s) applied, {label} expects {len(expected)}")
    for n, ((_, a), (owned, interest, pair)) in enumerate(zip(applied, expected), 1):
        at = f"{a['incarnation']}/{a['version']}"
        if pair is not None and at != pair:
            problems.append(f"{cell}: apply {n} is layout {at}, {label} expects {pair}")
        if not rigsplit.same_rect(a["owned"], owned) or not rigsplit.same_rect(a["interest"], interest):
            problems.append(f"{cell}: apply {n} ({at}) owns {rigsplit.format_rect(a['owned'])} interest "
                            f"{rigsplit.format_rect(a['interest'])}, expected owns {rigsplit.format_rect(owned)} "
                            f"interest {rigsplit.format_rect(interest)}")
    return problems


def step_pairs(states, step_rx):
    """{step: pair} of an op's states (rigsplit.STEP_PAIR or rigmerge.STEP_PAIR as `step_rx`)."""
    out = {}
    for state in states:
        m = step_rx.match(state)
        if m:
            out[m.group(1)] = m.group(2)
    return out
