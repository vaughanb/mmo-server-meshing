"""Pure readers of the three-cell family scenarios (regress_family.py, DESIGN 0.88; slices/slice3_design.md decisions
11 and 14): `split C x 0 + C.1`, then `split C.1 z 0 + C.2`, so C.1 and C.2 share the full edge z = 0 inside x [0, 100)
and neither shares one with C. mergechildren merges the two children (the one with more owned players survives);
collapsesibling kills C.2 with its restarts failing and the supervisor collapses it into C.1.

What lives here: the bot plans and the cell each bot ends in, the migrations and handoffs each bot makes, the
supervisor's restart-and-collapse sequence in the status file, the collapse's states, the store's entries for the
family, and the fault marker that makes a restarted C.2 die. Nothing here touches a file, a process or the database
(python -m unittest Tools/ServerMeshing/test_rigfamily.py).
"""
import collections
import re

import riglayout
import rigsplit
import rigsplitcrash

BASE, FIRST, SECOND = "C", "C.1", "C.2"
FIRST_CUT = ("x", 0.0, "+")     # C keeps x [-100, 0); C.1 takes x [0, 100) z [-100, 100)
SECOND_CUT = ("z", 0.0, "+")    # C.1 keeps x [0, 100) z [-100, 0); C.2 takes x [0, 100) z [0, 100)
CELLS = riglayout.TEST_MAP_RECTS
STAND_BOTS = 8                  # meshbot2..9 (provision.reset_bot_positions(2, 8, ...))

Bot = collections.namedtuple("Bot", "user character point side harvest")   # regress_split.Bot's fields


def bots(points):
    """meshbot2.. at `points`, each with the side of the first cut it stands on (regress_split's Bot shape)."""
    return [Bot(f"meshbot{2 + i}", f"meshbot{2 + i}Char", p, rigsplit.side_of(p, FIRST_CUT[0], FIRST_CUT[1]), False)
            for i, p in enumerate(points)]


# mergechildren: two bots in C.1's final half, three in C.2 (so C.2 survives `merge C.1 C.2` on owned players, against
# the id tie-break), three in C. collapsesibling: C.2 empty (a dead cell's player would re-log in, which is
# splitcrash_base's subject, not this one's), four in C.1, four in C. Every point inside C's walkable inner square.
PLANS = {
    "mergechildren": bots([(40.0, -75.0), (80.0, -60.0), (20.0, 75.0), (60.0, 90.0), (80.0, 60.0),
                           (-40.0, -75.0), (-60.0, 90.0), (-80.0, -40.0)]),
    "collapsesibling": bots([(20.0, -75.0), (40.0, -60.0), (60.0, -90.0), (80.0, -40.0),
                             (-20.0, 75.0), (-40.0, -75.0), (-60.0, 90.0), (-80.0, -90.0)]),
}


def ops(name, pairs1=None, pairs2=None, merge_pairs=None, survivor=SECOND, retiring=FIRST):
    """The scenario's riglayout ops: the two splits, then the merge (mergechildren) or the collapse of a dead C.2 into
    C.1 (collapsesibling); the merge only when it has pairs (it was sent)."""
    out = [riglayout.split_op(BASE, FIRST, *FIRST_CUT, pairs1), riglayout.split_op(FIRST, SECOND, *SECOND_CUT, pairs2)]
    if merge_pairs:
        if name == "collapsesibling":
            out.append(riglayout.merge_op(FIRST, SECOND, merge_pairs, dead=True))
        else:
            out.append(riglayout.merge_op(survivor, retiring, merge_pairs))
    return out


def cell_of(point, merged=None):
    """The cell a point ends in: C west of x = 0, else C.1 south of z = 0 and C.2 north of it; `merged` (the
    survivor of a merge or collapse of the two children) owns all of x [0, 100)."""
    if point[0] < FIRST_CUT[1]:
        return BASE
    if merged:
        return merged
    return SECOND if point[1] >= SECOND_CUT[1] else FIRST


def expected_hops(name, point, survivor=SECOND):
    """[(from, to)] of the handoffs a bot standing at `point` makes in the scenario, in order."""
    if point[0] < FIRST_CUT[1]:
        return []
    hops = [(BASE, FIRST)]
    north = point[1] >= SECOND_CUT[1]
    if north:
        hops.append((FIRST, SECOND))
    if name == "mergechildren":
        here = SECOND if north else FIRST
        if here != survivor:
            hops.append((here, survivor))
    return hops


def owned_players(plan, survivor=None):
    """{cell: how many bots of the plan it owns} after the two splits (or after the merge into `survivor`)."""
    out = collections.Counter(cell_of(b.point, survivor) for b in plan)
    return dict(out)


def expected_survivor(plan):
    """Slice 3 decision 11: the child with more owned players (ties: the lower id, ordinal ignore case)."""
    n = owned_players(plan)
    a, b = n.get(FIRST, 0), n.get(SECOND, 0)
    return FIRST if a > b or (a == b and FIRST.lower() <= SECOND.lower()) else SECOND


# ------------------------------------------------------------------ the status file
COLLAPSE_STATES = ("requested {s} {r} collapse", "e1 broadcast", "e1 gate open", "e2 staged", "e2 broadcast",
                   "channel {r} removed", "e3 broadcast", "done")
MERGE_STATES = ("requested {s} {r}", "e1 broadcast", "e1 gate open", "e2 staged", "e2 acked", "e2 broadcast",
                "drain gate open", "retire sent", "{r} exited", "channel {r} removed", "e3 broadcast", "done")


def state_problems(states, wanted, survivor, retiring):
    """What a merge's states miss of `wanted` (MERGE_STATES or COLLAPSE_STATES), in order, as messages; a tidy is a
    problem too (the family is not one cell again)."""
    patterns = [rf"^{re.escape(w.format(s=survivor, r=retiring))}(\s|$)" for w in wanted]
    problems = rigsplitcrash.order_problems(states, patterns)
    tidy = [s for s in states if s.startswith("tidy")]
    return problems + ([f"a tidy ({tidy[0]}) while the family still has two cells"] if tidy else [])


def supervisor_problems(events, cell, restarts=rigsplitcrash.CHILD_MAX_RESTARTS):
    """What the supervisor's lines about `cell` (rigsplitcrash.parse_supervisor) miss of the collapse path, as messages:
    restart n of `restarts` requested and restart n failed for n = 1..restarts, in order, then 'collapse queued after
    <restarts> failed restart(s)'; never 'cannot collapse', never a kill by the spawner (nothing was hung)."""
    mine = [e for e in events if e["cell"] == cell]
    seq = []
    for e in mine:
        if e["event"] in ("down", "hung"):
            seq.append(("requested", e["n"]))
        elif e["event"] in ("failed", "collapse"):
            seq.append((e["event"], e["n"]))
    wanted = [step for n in range(1, restarts + 1) for step in (("requested", n), ("failed", n))] + [("collapse", restarts)]
    problems = [] if rigsplitcrash.contains_in_order(seq, wanted) else [f"supervisor steps {seq}, expected {wanted}"]
    bad = [e["text"] for e in mine if e["event"] in ("cannot", "killed", "hung")]
    return problems + [f"unexpected: {bad[0]}"] if bad else problems


def failure_reasons(events, cell):
    """The why of each 'restart <n> failed (<why>)' line about `cell`."""
    return [e["why"] for e in events if e["cell"] == cell and e["event"] == "failed"]


# ------------------------------------------------------------------ the store (decision 5)
def store_problems(store, map_id, live, gone):
    """What the coordinator store's broadcast breaks of the family's end state, as messages: `live` {id: owned rect}
    each Active (state 2) owning that rectangle; each of `gone` absent or Retired (state 4) owning nothing."""
    entries = {str(e.get("id", "")).lower(): e for e in (store.get("broadcast") or {}).get("entries") or []
               if e.get("mapId") == map_id}
    problems = []
    for cell, rect in live.items():
        e = entries.get(cell.lower())
        if e is None:
            problems.append(f"no entry for {cell}")
            continue
        owned = store_rect(e.get("owned"))
        if e.get("state") != 2 or not rigsplit.same_rect(owned, rect):
            problems.append(f"{cell} state {e.get('state')} owns {rigsplit.format_rect(owned)}, expected Active owning "
                            f"{rigsplit.format_rect(rect)}")
    for cell in gone:
        e = entries.get(cell.lower())
        if e is not None and (e.get("state") != 4 or store_rect(e.get("owned")) is not rigsplit.NOTHING):
            problems.append(f"{cell} still in the store, state {e.get('state')}")
    if store.get("hasOperation"):
        problems.append("an operation is still stored")
    return problems


def store_rect(r):
    """A store MeshRect {minX, minZ, maxX, maxZ} as (minX, minZ, sizeX, sizeZ), NOTHING when empty or missing."""
    if not r:
        return rigsplit.NOTHING
    w, d = r.get("maxX", 0.0) - r.get("minX", 0.0), r.get("maxZ", 0.0) - r.get("minZ", 0.0)
    if w <= 0 or d <= 0:
        return rigsplit.NOTHING
    return (r.get("minX", 0.0), r.get("minZ", 0.0), w, d)


def fenced(store, cell):
    """The incarnations the store fences for `cell`."""
    return [f.get("incarnation") for f in store.get("fencedIncarnations") or [] if (f.get("cellId") or "").lower() == cell.lower()]


# ------------------------------------------------------------------ the restart fault
HELLO_FAULT = "MESH_FAULT_DIE_ON_HELLO"


def hello_fault(cell):
    """MESH_FAULT_DIE_ON_HELLO's value for a cell that dies right after its first hello (MeshFault.HelloFires)."""
    return f"{cell}:1"


def marker_name(cell, key=HELLO_FAULT):
    """MeshFault.MarkerPath's file name: Temp/ServerMesh.FaultFired.<key>.<zone>. While it is a file the fault does not
    fire (C.2's first process lives); as a directory File.Exists is false and the write fails, so every incarnation
    started after the swap dies on its first hello (the restarts fail)."""
    return f"ServerMesh.FaultFired.{key}.{cell}"
