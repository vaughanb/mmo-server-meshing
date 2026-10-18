"""Pure graders of Slice 2c's merge, drain and retire (Docs/ServerMeshing/slices/slice2c_design.md, revision 1).

What regress_merge.py reads: the merge op's mirrored status lines ('<seq> Merge <op>: <state>', decision 15), the
cells' layout applies against the merge's expectations (decision 16), the retiring cell's drain and retire lines, its
neighbours' prune and link-lost lines (decision 14), the bots' migrations (two for an R-bot: split and merge), and the
PowerShell process count the flow samples (one CIM query per sample; the snippet is built here, run by the rig).
Nothing here touches a file, a process or the database (python -m unittest Tools/ServerMeshing/test_rigmerge.py).
"""
import fnmatch
import re

import rigsplit

# ------------------------------------------------------------------ the op's status file (decisions 5, 15)
MERGE_OP_ID = re.compile(r"^merge-\d+$")
REQUESTED = re.compile(r"^requested (\S+) (\S+)$")
STEP_PAIR = re.compile(r"^(e[123]|tidy|reverted) broadcast ([0-9a-f]+/\d+)$")
EXITED_BEFORE = re.compile(r"^(\S+) exited before drained$")
EXITED = re.compile(r"^(\S+) exited$")
CHANNEL_REMOVED = re.compile(r"^channel (\S+) removed$")
DRAIN_STRAGGLING = re.compile(r"^drain straggling (\d+(?:\.\d+)?) s: ?(.*)$")
RETIRE_STRAGGLING = re.compile(r"^retire straggling (\d+(?:\.\d+)?) s$")
STEPS = ("e1", "e2", "e3", "tidy")


def parse_merge_status(lines, seq):
    """The merge command `seq`'s record from Temp/ServerMesh.LayoutCommandStatus.txt's lines: {kind, op, requested
    (survivor, retiring), refused, states, pairs (e1, e2, e3, tidy, reverted), retire_sent (how many), exited,
    exited_before, channel_removed, straggling [state], done, aborted, frozen, reverted, other}. Only 'Merge merge-<n>:'
    lines count (the runner mirrors a late Split line, or a cell's 'Merge retire:', under the latest seq: 'other')."""
    out = {"kind": "Merge", "op": None, "requested": None, "refused": None, "states": [], "pairs": {},
           "retire_sent": 0, "exited": None, "exited_before": None, "channel_removed": None, "straggling": [],
           "done": False, "aborted": None, "frozen": None, "reverted": False, "other": []}
    for raw in lines:
        parts = raw.strip().split(None, 1)
        if len(parts) != 2 or parts[0] != str(seq):
            continue
        text = parts[1]
        m = rigsplit.REFUSED.match(text)
        if m:
            out["refused"] = m.group(1)
            continue
        m = rigsplit.OP_STATE.match(text)
        if not m or m.group(1) != "Merge" or not MERGE_OP_ID.match(m.group(2)):
            out["other"].append(text)
            continue
        out["op"] = out["op"] or m.group(2)
        state = m.group(3)
        out["states"].append(state)
        step, before = STEP_PAIR.match(state), EXITED_BEFORE.match(state)
        if REQUESTED.match(state):
            out["requested"] = REQUESTED.match(state).groups()
        elif step:
            out["pairs"][step.group(1)] = step.group(2)
            out["reverted"] = out["reverted"] or step.group(1) == "reverted"
        elif state == "retire sent":
            out["retire_sent"] += 1
        elif before:
            out["exited_before"] = before.group(1)
        elif EXITED.match(state):
            out["exited"] = EXITED.match(state).group(1)
        elif CHANNEL_REMOVED.match(state):
            out["channel_removed"] = CHANNEL_REMOVED.match(state).group(1)
        elif DRAIN_STRAGGLING.match(state) or RETIRE_STRAGGLING.match(state):
            out["straggling"].append(state)
        elif state == "done":
            out["done"] = True
        elif state.startswith("aborted"):
            out["aborted"] = state[len("aborted"):].strip() or "aborted"
        elif state.startswith("frozen"):
            out["frozen"] = state[len("frozen"):].strip() or "frozen"
    return out


def merge_outcome(status):
    """'refused', 'aborted', 'frozen' or 'done' once the merge has ended (the first that applies), else None."""
    return rigsplit.op_outcome(status)


def required_states(retiring):
    """The states a passing merge logs, in order (decision 16): the broadcasts by their prefix."""
    return ["e1 broadcast", "e1 gate open", "e2 staged", "e2 acked", "e2 broadcast", "drain gate open", "retire sent",
            f"{retiring} exited", f"channel {retiring} removed", "e3 broadcast", "tidy broadcast", "done"]


def state_problems(status, retiring):
    """What a merge's states miss of required_states, or the order they first came in when it is wrong, as messages."""
    firsts = []
    for want in required_states(retiring):
        at = next((i for i, s in enumerate(status["states"]) if s == want or s.startswith(want + " ")), None)
        firsts.append((want, at))
    missing = [w for w, at in firsts if at is None]
    if missing:
        return [f"missing {', '.join(missing)}"]
    order = [at for _, at in firsts]
    if order != sorted(order):
        return ["states out of order: " + ", ".join(status["states"][:16])]
    return []


def bad_states(status):
    """The lines a passing merge never logs (aborted, frozen, reverted, exited before drained, straggling)."""
    bad = [f"aborted {status['aborted']}"] if status["aborted"] else []
    bad += [f"frozen {status['frozen']}"] if status["frozen"] else []
    bad += ["reverted broadcast"] if status["reverted"] else []
    bad += [f"{status['exited_before']} exited before drained"] if status["exited_before"] else []
    return bad + status["straggling"]


def command_line(seq, a, b):
    """The one line of Temp/ServerMesh.LayoutCommand.txt: '<seq> merge <a> <b>'."""
    for cell in (a, b):
        if not re.fullmatch(r"[\w.]+", cell or ""):
            raise ValueError(f"'{cell}' is not a cell id")
    return f"{seq} merge {a} {b}"


# ------------------------------------------------------------------ processes (decision 16 step 5 and 8)
def channel_filter(cell):
    """The -like pattern of a cell's map-server process: the spawner writes '  -channelId <cell>  -mapName ...'."""
    if not re.fullmatch(r"[\w.]+", cell or ""):
        raise ValueError(f"'{cell}' is not a cell id")
    return f"*-channelId {cell} *"


def command_line_matches(command_line, cell):
    """Whether a process command line passes channel_filter (PowerShell's -like: case-insensitive)."""
    return fnmatch.fnmatchcase((command_line or "").lower(), channel_filter(cell).lower())


def process_count_script(exe, cell):
    """The PowerShell snippet counting the build's processes of a cell: one Get-CimInstance query, filtered on the
    build's executable path and channel_filter. Prints one integer."""
    name = exe.replace("\\", "/").rsplit("/", 1)[-1]
    return ("@(Get-CimInstance Win32_Process -Filter \"Name='" + name.replace("'", "''") + "'\" -ErrorAction Stop"
            " | Where-Object { $_.ExecutablePath -eq '" + exe.replace("'", "''") + "' -and $_.CommandLine -like '"
            + channel_filter(cell) + "' }).Count")


def parse_count(out):
    """The integer process_count_script printed; ValueError for anything else."""
    text = (out or "").strip()
    if not text.isdigit():
        raise ValueError(f"not a process count: {text[:80]!r}")
    return int(text)


# ------------------------------------------------------------------ map-side lines (decisions 9-14)
WARNING = re.compile(r"^\W*WARN\b")
RETIRE_QUITTING = re.compile(r"Merge retire: (\S+) drained, quitting")
MERGE_RETIRE = re.compile(r"Merge retire: (\S+) ")
DRAIN_ON = re.compile(r"Drain on (\S+): ")
ENTER_REFUSED = re.compile(r"Enter game refused on draining cell (\S+): ")
PRUNE = re.compile(r"Retired zone (\S+): link closed, (\d+) record\(s\) released")
LINK_LOST = re.compile(r"Neighbour zone (\S+) link lost \(draining, retire expected\)")
LOAN_RELEASED = re.compile(r"(Lent|Borrowed) monster \S+ released")
# What a cell may not log in the merge scenario unless the allowances below take it: rigsplit's split problems, plus
# any Retired line (rigtools.OP_LINE's set).
OP_PROBLEM = re.compile(rigsplit.SPLIT_OP_PROBLEM.pattern + r"|\bRetired\b")
FELL_BACK = re.compile(r"Migration to \S+ \|.*\bfellBack=(True|False)\b")


def is_warning(line):
    """Whether a kit log line is a warning (' WARN <tag> [stamp] - ...')."""
    return WARNING.search(line) is not None


def prunes(lines, zone):
    """[n] of a cell's 'Retired zone <zone>: link closed, <n> record(s) released' lines, in order."""
    return [int(m.group(2)) for line in lines for m in [PRUNE.search(line)] if m and m.group(1) == zone]


def link_losts(lines, zone):
    """How many 'Neighbour zone <zone> link lost (draining, retire expected)' lines a cell logged."""
    return sum(1 for line in lines for m in [LINK_LOST.search(line)] if m and m.group(1) == zone)


def disallowed_op_lines(cell, lines, retiring):
    """The op lines a cell logs that the merge grade does not allow (decision 16): `Merge retire:` and `Enter game
    refused` only on the retiring cell, about itself; `Drain on` on any cell about itself (2b's split logs `Drain on
    <parent>: gave away ...` on the split's parent); the prune and link-lost lines on every cell; nothing else of
    Split/Merge/Retire (OP_PROBLEM)."""
    out = []
    for line in lines:
        if PRUNE.search(line) or LINK_LOST.search(line):
            continue
        drain = DRAIN_ON.search(line)
        if drain and drain.group(1) == cell:
            continue
        own = [m.group(1) for rx in (MERGE_RETIRE, DRAIN_ON, ENTER_REFUSED) for m in [rx.search(line)] if m]
        if own and cell == retiring and own[0] == retiring:
            continue
        if own or OP_PROBLEM.search(line):
            out.append(line)
    return out


def applied_pair(lines, pair):
    """Whether a cell applied layout `pair` (its own 'applied:' line or a Static neighbour's 'neighbours applied:')."""
    return pair is not None and len(rigsplit.until_applied(lines, pair)) < len(lines)


def after_applied(lines, pair):
    """The lines from a cell's apply of `pair` on, or None when it never applied it."""
    if not applied_pair(lines, pair):
        return None
    return list(lines)[len(rigsplit.until_applied(lines, pair)):]


GHOST_OWNER_CHANGED = re.compile(r"Ghost owner changed Player (\S+) \([^)]*\) zone (\S+?)->(\S+) at "
                                 r"\((-?[\d.]+), (-?[\d.]+), (-?[\d.]+)\)")


def owner_moved_at(lines, after_lines, name, old, new, x, z, tolerance=3.0):
    """Whether a puppet of `name` changed owner old->new near (x, z): a 'Ghost owner changed ... zone <old>-><new>' in
    `lines`, or a fresh 'Ghost spawned ... from zone <new>' in `after_lines` (the lines after the merge began: before
    it, a spawn from the survivor is the one from before the split)."""
    for line in lines:
        m = GHOST_OWNER_CHANGED.search(line)
        if m and m.groups()[:3] == (name, old, new) and abs(float(m.group(4)) - x) <= tolerance \
                and abs(float(m.group(6)) - z) <= tolerance:
            return True
    return rigsplit.ghost_at(after_lines or [], name, new, x, z, tolerance)


def statics_held(child_lines, base, child):
    """How many statics the child held at the end of `child_lines` (its lines before the merge's e2): its 'Static
    handoff ... zone <base>-><child>: promoted|spawned' lines minus its own 'sent' lines to any cell."""
    taken = sum(rigsplit.count_kind(child_lines, rigsplit.STATIC_HANDOFF, k, base, child) for k in ("promoted", "spawned"))
    sent = sum(1 for line in child_lines for m in [rigsplit.STATIC_HANDOFF.search(line)]
               if m and m.group(2) == child and m.group(4) == "sent")
    return taken - sent


# ------------------------------------------------------------------ the store file (decision 5, tidy)
def family_entries(store, map_id, base, cells):
    """The ids of the entries a coordinator store (Temp/ServerMesh.Coordinator.json, parsed) holds for a family: on
    `map_id`, with baseId `base` or an id in `cells` (ids compared case-insensitively), in its broadcast and, when
    it holds one, its staged layout."""
    names = {c.lower() for c in cells} | {base.lower()}
    snapshots = [store.get("broadcast") or {}] + ([store.get("staged") or {}] if store.get("hasStaged") else [])
    out = []
    for snapshot in snapshots:
        for entry in snapshot.get("entries") or []:
            if entry.get("mapId") == map_id and (str(entry.get("baseId", "")).lower() == base.lower()
                                                 or str(entry.get("id", "")).lower() in names):
                out.append(entry.get("id"))
    return out


# ------------------------------------------------------------------ layout applies (decision 16)
def merge_expected_applies(split, cell, own_rect, split_pairs, merge_pairs):
    """[(owned, interest, pair)] of every layout a cell of the split's map applies in the merge scenario, in order.

    The survivor (the split's base): the split's four, then e1 (kept, P), e2 (P, P; staged, its broadcast carries the
    same pair), e3 (P, P), tidy (P, P). The retiring child: the split's three, then e1 (R, R), e2 (nothing, R), and
    nothing after (it has quit). Every other cell: its own rect at all eight. A pair is None where the status gave
    none (the first layout's is never known)."""
    kept, given = rigsplit.split_rects(split.base_rect, split.axis, split.at, split.side)
    p = split.base_rect
    sp, mp = split_pairs or {}, merge_pairs or {}
    first = rigsplit.expected_applies(split, cell, own_rect)
    if cell == split.child:
        before = [sp.get("e1"), sp.get("e2"), sp.get("e3")]
        after = [(given, given, mp.get("e1")), (rigsplit.NOTHING, given, mp.get("e2"))]
    else:
        before = [None, sp.get("e1"), sp.get("e2"), sp.get("e3")]
        rects = [(kept, p), (p, p), (p, p), (p, p)] if cell == split.base else [(own_rect, rigsplit.ANY)] * 4
        after = [(o, i, mp.get(s)) for (o, i), s in zip(rects, STEPS)]
    return [(o, i, pair) for (o, i), pair in zip(first, before)] + after


def merge_apply_violations(cell, info_lines, split, split_pairs, merge_pairs):
    """rigsplit.split_apply_violations against merge_expected_applies."""
    return rigsplit.split_apply_violations(
        cell, info_lines, split, label="the merge",
        expected=lambda c, own: merge_expected_applies(split, c, own, split_pairs, merge_pairs))


# ------------------------------------------------------------------ the bots (decision 16)
def fell_back(lines):
    """[True|False] of a bot log's 'Migration to ... | SEAMLESS ... fellBack=<b>' lines, in order."""
    return [m.group(1) == "True" for line in lines for m in [FELL_BACK.search(line)] if m]


def harvest_problems(result, bot_lines, handoffs):
    """Why the harvest bot fails the merge (its RESULT is written between the split and the merge): RESULT passed with
    migrations=1, `handoffs` (its Handoff START lines child->survivor) 1, two 'fellBack=False' migrations in its log."""
    if not result:
        return ["no RESULT harvest line"]
    problems = []
    if not result.get("passed"):
        problems.append("RESULT did not pass")
    if result.get("migrations") != 1:
        problems.append(f"migrations={result.get('migrations')}, expected 1 (written before the merge)")
    if handoffs != 1:
        problems.append(f"{handoffs} Handoff START back to the survivor, expected 1")
    clean = fell_back(bot_lines).count(False)
    if clean != 2:
        problems.append(f"{clean} 'fellBack=False' migration(s) in its log, expected 2")
    return problems
