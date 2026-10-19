"""Pure readers and expectations for Slice 4's `splitbuild` (Docs/ServerMeshing/slices/slice4_design.md decisions 15-16):
the map-side building transfer and load lines, the tester's buildtree RESULT, the database row checks and the per-lifetime apply expectations
(the split's, the merge's, then the restarted base's own rectangle). Nothing here touches the editor, the database or
a process; regress_building drives the rig with what these decide.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py" (test_rigbuilding)
"""
import re

import rigmerge
import rigsplit
import rigtools

# ------------------------------------------------------------------ map-side lines (contract: Interfaces)
TRANSFER = re.compile(r"Building transfer (\S+) zone (\S+)->(\S+): (.*?)\s*$")
SENT = re.compile(r"^sent \((\d+) node\(s\)\)$")
TAKEN = re.compile(r"^promoted (\d+), spawned (\d+), owned already (\d+)$")
LOADED = re.compile(r"Buildings loaded on (\S+): (\d+) placed in (\d+) tree\(s\) and (\d+) scene, (\d+) tree\(s\) owned elsewhere, "
                    r"(\d+) scene row\(s\) of other cells skipped, (\d+) already here \(channel (\S+), (\d+) row\(s\)\)")
KEPT_HERE = re.compile(r"Building \S+ placed at .* belongs to zone \S+, which is not ready: kept here")
CHANNEL = re.compile(r"Building channel on (\S+): (\S+)")
LOAD_FIELDS = ("cell", "placed", "trees", "scene", "elsewhere", "skipped", "already", "channel", "rows")


def transfers(lines):
    """[{root, from, to, what}] of a cell's 'Building transfer' lines, in order."""
    out = []
    for line in lines:
        m = TRANSFER.search(line)
        if m:
            out.append({"root": m.group(1), "from": m.group(2), "to": m.group(3), "what": m.group(4)})
    return out


def sent_counts(lines, root, from_zone, to_zone):
    """The node counts of the 'sent (<n> node(s))' lines of one root from one cell to another."""
    out = []
    for t in transfers(lines):
        m = SENT.match(t["what"])
        if m and (t["root"], t["from"], t["to"]) == (root, from_zone, to_zone):
            out.append(int(m.group(1)))
    return out


def taken_counts(lines, root, from_zone, to_zone):
    """[(promoted, spawned, owned already)] of the receiver's lines for one root."""
    out = []
    for t in transfers(lines):
        m = TAKEN.match(t["what"])
        if m and (t["root"], t["from"], t["to"]) == (root, from_zone, to_zone):
            out.append((int(m.group(1)), int(m.group(2)), int(m.group(3))))
    return out


def roots_sent(lines, from_zone, to_zone):
    """The roots a cell sent to another, in order (one per 'sent' line)."""
    return [t["root"] for t in transfers(lines) if SENT.match(t["what"]) and (t["from"], t["to"]) == (from_zone, to_zone)]


def transfer_problems(sender_lines, receiver_lines, root, from_zone, to_zone, nodes=3):
    """Why one tree's hand-over from one cell to another fails the grade (decision 16 steps 3 and 5): sent once with
    `nodes` nodes and done on the sender; taken once on the receiver with promoted + spawned = `nodes` and none owned
    already; no released, failed, save-failed or storage-still-in-use line about it on either side."""
    problems = []
    if root is None:
        return [f"no 'Building transfer ... sent' line {from_zone}->{to_zone}"]
    sent = sent_counts(sender_lines, root, from_zone, to_zone)
    if sent != [nodes]:
        problems.append(f"sent lines {sent}, expected one with {nodes} node(s)")
    done = sum(1 for t in transfers(sender_lines) if t["what"] == "done" and (t["root"], t["from"], t["to"]) == (root, from_zone, to_zone))
    if done != 1:
        problems.append(f"{done} 'done' line(s) on {from_zone}, expected 1")
    taken = taken_counts(receiver_lines, root, from_zone, to_zone)
    if len(taken) != 1 or taken[0][0] + taken[0][1] != nodes or taken[0][2] != 0:
        problems.append(f"taken lines {taken}, expected one with promoted + spawned = {nodes} and 0 owned already")
    for who, lines in ((from_zone, sender_lines), (to_zone, receiver_lines)):
        bad = [t["what"] for t in transfers(lines) if t["root"] == root
               and (t["what"].startswith(("released", "failed", "save failed", "storage ")))]
        if bad:
            problems.append(f"{who}: {', '.join(bad[:3])}")
    return problems


def loads(lines):
    """[{cell, placed, trees, scene, elsewhere, skipped, already, channel, rows}] of a cell's load lines, in order."""
    out = []
    for line in lines:
        m = LOADED.search(line)
        if m:
            d = dict(zip(LOAD_FIELDS, m.groups()))
            for k in LOAD_FIELDS:
                if k not in ("cell", "channel"):
                    d[k] = int(d[k])
            out.append(d)
    return out


def load_problems(load, cell, placed, channel, rows):
    """Why a load line is not the expected one: the cell, placed count, channel and row count; nothing owned elsewhere,
    skipped or already here."""
    if load is None:
        return [f"no 'Buildings loaded on {cell}' line"]
    problems = []
    if load["cell"] != cell:
        problems.append(f"cell {load['cell']}")
    if load["placed"] != placed:
        problems.append(f"placed {load['placed']}, expected {placed}")
    if load["channel"] != channel:
        problems.append(f"channel {load['channel']}, expected {channel}")
    if load["rows"] != rows:
        problems.append(f"{load['rows']} row(s), expected {rows}")
    for k in ("elsewhere", "skipped", "already"):
        if load[k]:
            problems.append(f"{k} {load[k]}")
    return problems


# ------------------------------------------------------------------ the tester's RESULT (decision 15)
RESULT_FIELDS = {"placed": (r"\bplaced=(\d+)", int), "root": (r"\broot=(\S+)", str), "storageBefore": (r"\bstorageBefore=(\S+)", str),
                 "storageAfter": (r"\bstorageAfter=(\S+)", str), "doorOpened": (r"\bdoorOpened=(True|False)", "True".__eq__),
                 "movesOk": (r"\bmovesOk=(\d+)", int), "movesRefused": (r"\bmovesRefused=(\d+)", int),
                 "movesFailed": (r"\bmovesFailed=(\d+)", int), "scrollsBag": (r"\bscrollsBag=(-?\d+)", int),
                 "scrollsStorage": (r"\bscrollsStorage=(-?\d+)", int), "total": (r"\btotal=(-?\d+)", int),
                 "buildingsAfterRelogin": (r"\bbuildingsAfterRelogin=(-?\d+)", int),
                 "storageAfterRelogin": (r"\bstorageAfterRelogin=(-?\d+)", int)}
SCROLLS = 20


def parse_result(line):
    """The buildtree RESULT's fields (plus the stand suffix through rigsplit) and 'passed'; {} for an empty line."""
    if not line or "RESULT buildtree:" not in line:
        return {}
    out = rigsplit.parse_result(line)
    for key, (pattern, convert) in RESULT_FIELDS.items():
        m = re.search(pattern, line)
        if m:
            out[key] = convert(m.group(1))
    return out


def result_problems(result, base, child):
    """Why the tester's RESULT fails decision 16 step 8: PASS, three placed, the first storage open on the base and the
    second on the child (the client knows the serving cell's channel id from the kit's server info), the door, no failed
    move, the scrolls summing to 20, and the re-login counts when they happened (-1 is 'no re-login inside the window';
    a note is the caller's). Returns (problems, notes)."""
    if not result:
        return ["no RESULT buildtree line"], []
    problems, notes = [], []
    if not result.get("passed"):
        problems.append("RESULT did not pass")
    if result.get("placed") != 3:
        problems.append(f"placed={result.get('placed')}, expected 3")
    if result.get("storageBefore") != base:
        problems.append(f"storageBefore={result.get('storageBefore')}, expected {base}")
    if result.get("storageAfter") != child:
        problems.append(f"storageAfter={result.get('storageAfter')}, expected {child}")
    if result.get("doorOpened") is not True:
        problems.append("doorOpened=False")
    if result.get("movesFailed", 1) != 0:
        problems.append(f"movesFailed={result.get('movesFailed')}")
    if result.get("total") != SCROLLS:
        problems.append(f"total={result.get('total')} (bag {result.get('scrollsBag')} + storage {result.get('scrollsStorage')}), expected {SCROLLS}")
    k = result.get("buildingsAfterRelogin", -1)
    if k < 0:
        notes.append("the tester's re-login did not finish inside its window: the after-restart counts are not graded")
    else:
        if k != 3:
            problems.append(f"buildingsAfterRelogin={k}, expected 3")
        if result.get("storageAfterRelogin") != result.get("scrollsStorage"):
            problems.append(f"storageAfterRelogin={result.get('storageAfterRelogin')}, expected {result.get('scrollsStorage')}")
    notes.append(f"moves ok={result.get('movesOk')} refused={result.get('movesRefused')} failed={result.get('movesFailed')}")
    return problems, notes


# ------------------------------------------------------------------ the database (decision 16 steps 6-7)
def rows_problems(rows, base, root, expected=3):
    """Why the map's building rows are wrong after the merge: `rows` is [(id, parentId, channel)]; expected count, every
    channel the base's, exactly one root (parentId empty) which is `root`, the others its children."""
    problems = []
    if len(rows) != expected:
        problems.append(f"{len(rows)} building row(s), expected {expected}")
    channels = sorted({r[2] for r in rows})
    if channels != [base]:
        problems.append(f"channels {channels}, expected [{base!r}]")
    roots = [r[0] for r in rows if not r[1]]
    if root is not None and roots != [root]:
        problems.append(f"root row(s) {roots}, expected [{root!r}]")
    orphans = [r[0] for r in rows if r[1] and (root is None or r[1] != root)]
    if orphans:
        problems.append(f"row(s) whose parent is not the root: {orphans}")
    return problems


def storage_sum(rows):
    """The scrolls in a storage from its storageitem rows [(amount,)]."""
    return sum(int(r[0]) for r in rows)


# ------------------------------------------------------------------ the layout grade (decision 16 step 8)
def expected_lives(split, cell, own_rect, split_pairs, merge_pairs):
    """[[(owned, interest, pair)] per lifetime]: the split's and the merge's applies (rigmerge) as every cell's first
    lifetime; the base adds a second lifetime with one apply of its base rectangle (the family was tidied out of the
    store, so the central's answer to the restarted base is the registry's cut: its pair cannot be known)."""
    lives = [list(rigmerge.merge_expected_applies(split, cell, own_rect, split_pairs, merge_pairs))]
    if cell == split.base:
        lives.append([(split.base_rect, split.base_rect, None)])
    return lives


def second_life_op_lines(info_lines):
    """The Split, Merge or Retire lines a restarted base logs in its second lifetime (there must be none)."""
    lives = rigsplit.lifetimes(info_lines)
    if len(lives) < 2:
        return []
    return [line for line in lives[1] if rigtools.OP_LINE.search(line)]
