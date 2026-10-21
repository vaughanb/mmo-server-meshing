"""Pure graders of Slice 5's central-restart scenarios (Docs/ServerMeshing/slices/slice5_design.md decision 16).

What regress_central.py reads: the central's kit log (recovery, resumed operations, login routing, the op lines of a
resumed merge), the cells' hello and answer lines, their database reconnects, and the layout applies per process lifetime
where a second coordinator incarnation may add applies (versions rise across the lifetime: the new central numbers on from
the adopted version). Nothing here touches a file, a process or the database (python -m unittest
Tools/ServerMeshing/test_rigcentral.py).
"""
import re

import rigmerge
import rigprocs
import rigsplit
import rigsplitcrash
import rigtools

# ------------------------------------------------------------------ the central's log (decisions 2-4, 7-9)
PAYLOAD = re.compile(r"\] - (.*)$")
ADOPTED = re.compile(r"Coordinator recovery: map (\S+) adopted layout ([0-9a-f]+/\d+) from (the store(?: \(staged\))?|cell \S+) "
                     r"with (\d+) entries \(([^)]*)\)")
FROZEN_FAMILY = re.compile(r"Coordinator recovery: family (\S+) of map (\S+) frozen: (.*)")
UNFROZEN = re.compile(r"Coordinator recovery: family (\S+) of map (\S+) unfrozen: (\S+) said hello")
MAP_FROZEN = re.compile(r"Coordinator recovery: (?:map \S+ )?frozen[:,]")
REOPENS = re.compile(r"re-opens recovery")
RESUMED = re.compile(r"^(Split|Merge) (\S+): resumed at (\S+) after a central restart$")
ABORTED_RESTART = re.compile(r"^(Split|Merge) (\S+): aborted the central restarted before (\S+)$")
DROPPED = re.compile(r"^(Split|Merge) (\S+): dropped after the restart: (.*)$")
FRESH_COOLDOWNS = re.compile(r"Coordinator policy: cooldowns start fresh for (.*) after the restart")
LOGIN_ROUTED = re.compile(r"Login routing: (\S+) at \(([^)]*)\) -> channel (\S+) ")
LOGIN_NO_SERVER = re.compile(r"Login routing: no server for cell (\S+) of (\S+) \((\S+) at")
LOGIN_WAITED = re.compile(r"Login routing: waited ([\d.]+) s for the layout recovery")
RECOVERY_FROZEN_NOTE = re.compile(r"recovery-frozen")
LAYOUT_REFUSED = re.compile(r"Layout op refused: (.*)$")
# The operator's unfreeze (DESIGN 0.85, slices/unfreeze_design.md): its own lines start "Unfreeze <base>: ".
UNFREEZE = re.compile(r"^Unfreeze (\S+): (.*)$")

# ------------------------------------------------------------------ the cells' lines (decisions 12, 13)
ANSWER = re.compile(r"Layout answer ([0-9a-f]+/\d+) on (\S+): already applied")
HELLO_SENT = rigsplitcrash.LAYOUT_HELLO
DB_LOST = re.compile(r"Database connection lost; reconnecting to (\S+)")
DB_OPEN = re.compile(r"Database connection to (\S+) is open")
FAULT_HELLO = re.compile(r"FAULT: dying on layout hello (\d+) \(MESH_FAULT_DIE_ON_HELLO\)")
SESSION_LOST = rigsplitcrash.SESSION_LOST
LIFETIME_GAP_S = 3.0  # a quiet stretch this long ends a run of lines; the central is down for CENTRAL_DOWN_S (8 s)


def lifetimes(lines):
    """The central's lines (every severity, in stamp order) split into process lifetimes. A lifetime opens at the first
    line of the unbroken run of lines (no gap of LIFETIME_GAP_S or more) that ends at its coordinator summary line
    (rigprocs.SUMMARY, once per lifetime): a process logs its start-up a moment before that line, sometimes a second
    earlier, and the central is down for longer than the gap between two lifetimes. Lines before the first lifetime form
    a lifetime of their own only when there are any; lines without a stamp are dropped. One lifetime without a summary."""
    stamped = [(t, line) for line in lines for t in [rigtools.stamp_datetime(line)] if t is not None]
    opens = []
    for i, (_, line) in enumerate(stamped):
        if not rigprocs.SUMMARY.search(line):
            continue
        b = i
        while (b > 0 and not rigprocs.SUMMARY.search(stamped[b - 1][1])
               and (stamped[b][0] - stamped[b - 1][0]).total_seconds() < LIFETIME_GAP_S):
            b -= 1
        opens.append(b)
    if not opens:
        return [list(lines)] if lines else []
    cuts = [0] + opens + [len(stamped)]
    out = [[line for _, line in stamped[a:b]] for a, b in zip(cuts, cuts[1:])]
    return out[1:] if not out[0] else out


def summary(lines):
    """(incarnation, maps text, store state, broadcast pair) of a lifetime's summary line, or None."""
    return rigprocs.summary_of(lines)


def payload(line):
    """The text after the kit logger's ' - ' (the whole line when it has no stamp)."""
    m = PAYLOAD.search(line)
    return m.group(1).strip() if m else line.strip()


def payloads(lines):
    return [payload(line) for line in lines]


def adoptions(lines):
    """[{map, pair, source, entries, ids}] of the central's adoption lines, in order."""
    out = []
    for line in lines:
        m = ADOPTED.search(line)
        if m:
            out.append({"map": m.group(1), "pair": m.group(2), "source": m.group(3), "entries": int(m.group(4)),
                        "ids": [i.strip() for i in m.group(5).split(",") if i.strip() and i.strip() != "none"]})
    return out


def resumed(lines):
    """[(kind, op, step)] of the resumed-operation lines."""
    return [(m.group(1), m.group(2), m.group(3)) for text in payloads(lines) for m in [RESUMED.match(text)] if m]


def op_states(lines, kind):
    """[(op, state)] of the central's own '<Kind> <op>: <state>' lines of one kind, in order."""
    out = []
    for text in payloads(lines):
        m = rigsplit.OP_STATE.match(text)
        if m and m.group(1) == kind:
            out.append((m.group(2), m.group(3)))
    return out


def frozen_lines(lines):
    """The central's frozen lines (a map or a family) and recovery-frozen notes."""
    return [line for line in lines if FROZEN_FAMILY.search(line) or MAP_FROZEN.search(line) or RECOVERY_FROZEN_NOTE.search(line)]


def login_routes(lines, character=None):
    """[(character, channel)] of the login's routing lines (one character's when given)."""
    return [(m.group(1), m.group(3)) for line in lines for m in [LOGIN_ROUTED.search(line)] if m
            and (character is None or m.group(1) == character)]


def unfreeze_lines(lines, base=None):
    """[(base, text)] of the central's 'Unfreeze <base>: <text>' lines (one base's when given), in order."""
    return [(m.group(1), m.group(2)) for text in payloads(lines) for m in [UNFREEZE.match(text)] if m
            and (base is None or m.group(1) == base)]


def frozen_families(lines):
    """[(base, map, reason)] of the central's 'family <base> of map <m> frozen: <reason>' lines."""
    return [(m.group(1), m.group(2), m.group(3).strip()) for line in lines for m in [FROZEN_FAMILY.search(line)] if m]


def refusals(lines):
    return [m.group(1) for text in payloads(lines) for m in [LAYOUT_REFUSED.search(text)] if m]


def answers(lines, cell):
    """[pair] of a cell's 'Layout answer <pair> on <cell>: already applied' lines."""
    return [m.group(1) for line in lines for m in [ANSWER.search(line)] if m and m.group(2) == cell]


def hellos_sent(lines, cell):
    """[incarnation] of a cell's LayoutHello sent lines."""
    return [m.group(2) for line in lines for m in [HELLO_SENT.search(line)] if m and m.group(1) == cell]


def database_reconnect(lines, after=None):
    """(lost, open) counts of a cell's database reconnect lines stamped at or after `after` (a datetime; None: all)."""
    lost = open_ = 0
    for line in lines:
        if after is not None:
            t = rigtools.stamp_datetime(line)
            if t is None or t < after:
                continue
        if DB_LOST.search(line):
            lost += 1
        if DB_OPEN.search(line):
            open_ += 1
    return lost, open_


def hello_faults(lines):
    """[n] of a cell's 'FAULT: dying on layout hello <n>' lines."""
    return [int(m.group(1)) for line in lines for m in [FAULT_HELLO.search(line)] if m]


def session_losses(bot_lines):
    return rigsplitcrash.session_losses(bot_lines)


# ------------------------------------------------------------------ layout applies per lifetime (decision 16)
def expected_lives(case, split, cell, own_rect, split_pairs, merge_pairs=None, adopted=None, settle=None):
    """[[(owned, interest, pair)] per process lifetime] of every layout a cell of the split's map applies in a case.

    split (splitcentral): one lifetime holding the split's applies (the restarted central adds none).
    merge (splitcentral_merge): one lifetime: the split's applies, the merge's e1 and e2 (first incarnation), e3 and tidy
    (second incarnation; pairs from the central's log when known, else None). The child quits after e2.
    base (splitcentral_base): the base two lifetimes, the second with one apply (kept, kept) at the adopted pair; the
    child and the others one lifetime, the split's applies. A settle adds one apply to the split's lifetime.
    unfreeze (splitcentral_unfreeze): as merge, but the child was killed after the split and applies nothing more; the
    collapse's e1 (kept, base), e2, e3 and tidy (base, base) come from the second incarnation (merge_pairs)."""
    kept, given = rigsplit.split_rects(split.base_rect, split.axis, split.at, split.side)
    p, nothing, any_rect = split.base_rect, rigsplit.NOTHING, rigsplit.ANY
    sp, mp = split_pairs or {}, merge_pairs or {}
    e1, e2, e3 = sp.get("e1"), sp.get("e2"), sp.get("e3")
    if cell == split.base:
        first = [(p, p, None), (p, p, e1), (kept, p, e2), (kept, kept, e3)]
        tail = (kept, kept)
    elif cell == split.child:
        first = [(nothing, given, e1), (given, given, e2), (given, given, e3)]
        tail = (given, given)
    else:
        first = [(own_rect, any_rect, None), (own_rect, any_rect, e1), (own_rect, any_rect, e2), (own_rect, any_rect, e3)]
        tail = (own_rect, any_rect)
    if settle:
        first.append(tail + (settle,))
    if case in ("merge", "unfreeze"):
        if cell == split.base:
            first += [(kept, p, mp.get("e1")), (p, p, mp.get("e2")), (p, p, mp.get("e3")), (p, p, mp.get("tidy"))]
        elif cell == split.child:
            first += [] if case == "unfreeze" else [(given, given, mp.get("e1")), (nothing, given, mp.get("e2"))]
        else:
            first += [(own_rect, any_rect, mp.get(s)) for s in ("e1", "e2", "e3", "tidy")]
        return [first]
    if case == "base" and cell == split.base:
        return [first, [(kept, kept, adopted or settle or e3)]]
    return [first]


def lifetime_apply_violations(cell, info_lines, split, lives, label="the case"):
    """rigsplitcrash.lifetime_apply_violations with Slice 5's one relaxation: applies from two coordinator incarnations
    are allowed when the incarnations come in order and the versions rise across the lifetime (a restarted central
    numbers on from the version it adopted)."""
    problems = rigsplitcrash.lifetime_apply_violations(cell, info_lines, split, lives, label=label)
    problems = [p for p in problems if "coordinator incarnations" not in p]
    child = cell if cell == split.child else None
    found = rigsplit.lifetimes(info_lines, child)
    if callable(lives):
        own = rigtools.parse_own_cell(found[0][0]) if found and child is None else None
        lives = lives(own[2] if own else rigsplit.ANY)
    for k, lines in enumerate(found, 1):
        life = f" lifetime {k}" if len(lives) > 1 else ""
        applied = [a for line in lines for a in [rigsplit.parse_apply(line)] if a]
        incarnations = []
        for a in applied:
            if not incarnations or incarnations[-1] != a["incarnation"]:
                incarnations.append(a["incarnation"])
        if len(incarnations) > 2 or len(incarnations) != len(set(incarnations)):
            problems.append(f"{cell}{life}: coordinator incarnations out of order ({', '.join(incarnations)})")
    return problems


def disallowed_lines(cell, lines, retiring=None):
    """The map-side lines a cell logs that a central-restart case does not allow: rigsplitcrash.disallowed_lines with no
    faulted cell (the base case checks its FAULT line itself) and, for the merge variant, the retiring cell's own lines
    (rigmerge.disallowed_op_lines) allowed."""
    if retiring is not None:
        return [line for line in rigmerge.disallowed_op_lines(cell, lines, retiring) if not FAULT_HELLO.search(line)]
    return [line for line in rigsplitcrash.disallowed_lines(cell, lines) if not FAULT_HELLO.search(line)]


def bot_problems(result, migrations, relogins, bot_lines):
    """rigsplitcrash.bot_problems plus: no 'Session lost' line (a player was never kicked)."""
    problems = rigsplitcrash.bot_problems(result, migrations, relogins)
    losses = session_losses(bot_lines)
    if losses and relogins == 0:
        problems.append(f"{len(losses)} 'Session lost' line(s) ({losses[0][0]})")
    return problems
