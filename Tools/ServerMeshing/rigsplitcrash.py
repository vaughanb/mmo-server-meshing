"""Pure graders of Slice 2d's supervision and fencing scenarios (Docs/ServerMeshing/slices/slice2d_design.md, rev 1).

What regress_splitcrash.py reads: the split's mirrored status lines with Slice 2d's states (aborted on loss, paused,
rebound, the retired broadcast) and the supervisor's 'Supervisor:' lines (decision 15), the cells' layout applies per
process lifetime, the FAULT lines of decision 12, the handoffs and rollbacks a child's crash during the drain makes
(decision 17), the monsters a child holds when its base dies and their release (decision 18), the owned-monster sums
of the work windows, the bots' re-login lines (decision 14) and the PowerShell kill of a cell's process. Nothing here
touches a file, a process or the database (python -m unittest Tools/ServerMeshing/test_rigsplitcrash.py).
"""
import datetime
import re

import rigmerge
import rigsplit
import rigtools

CHILD_MAX_RESTARTS = 3   # MeshDynamicSettings.childMaxRestarts

# ------------------------------------------------------------------ the status file (decisions 2, 6, 15)
SUP_RESTART = re.compile(r"^Supervisor: (\S+) (down|hung) (\d+(?:\.\d+)?) s, restart (\d+) of (\d+) requested$")
SUP_BACK = re.compile(r"^Supervisor: (\S+) back, incarnation ([0-9a-f]+)$")
SUP_FAILED = re.compile(r"^Supervisor: (\S+) restart (\d+) failed \((.*)\)$")
SUP_COLLAPSE = re.compile(r"^Supervisor: (\S+) collapse queued after (\d+) failed restart\(s\)$")
SUP_CANNOT = re.compile(r"^Supervisor: (\S+) cannot collapse \(no live cell of its family shares a full edge\); left down$")
SUP_KILLED = re.compile(r"^Supervisor: (\S+) killed by its spawner \((.*)\)$")
SUP_ANY = re.compile(r"^Supervisor: (\S+) ?(.*)$")
ABORTED_LOST = re.compile(r"^aborted (\S+) lost$")
PAUSED = re.compile(r"^(\S+) lost, op paused$")
REBOUND = re.compile(r"^(\S+) rebound ([0-9a-f]+), op resumed$")
BLIP = re.compile(r"^(\S+) back, op resumed$")
STEP_PAIR = re.compile(r"^(retired|settle|reverted) broadcast ([0-9a-f]+/\d+)$")
SETTLE = re.compile(r"^stragglers gone, settle broadcast ([0-9a-f]+/\d+)$")
CHANNEL_REMOVED = re.compile(r"^channel (\S+) removed$")


def seq_texts(lines, seq):
    """The texts of a status file's lines under `seq`, in order (the seq word removed)."""
    out = []
    for raw in lines:
        parts = raw.strip().split(None, 1)
        if len(parts) == 2 and parts[0] == str(seq):
            out.append(parts[1])
    return out


def parse_supervisor(lines, seq):
    """[{event, cell, at, text, ...}] of the 'Supervisor:' lines under `seq`, in order; `at` is the line's index among
    the seq's lines. Events: 'down'/'hung' (seconds, n, max), 'back' (incarnation), 'failed' (n, why), 'collapse' (n),
    'cannot', 'killed' (why), or 'other' for a Supervisor line of no known form."""
    out = []
    for at, text in enumerate(seq_texts(lines, seq)):
        if not text.startswith("Supervisor:"):
            continue
        m = SUP_RESTART.match(text)
        if m:
            out.append({"event": m.group(2), "cell": m.group(1), "seconds": float(m.group(3)), "n": int(m.group(4)),
                        "max": int(m.group(5)), "at": at, "text": text})
            continue
        for rx, event, keys in ((SUP_BACK, "back", ("incarnation",)), (SUP_FAILED, "failed", ("n", "why")),
                                (SUP_COLLAPSE, "collapse", ("n",)), (SUP_CANNOT, "cannot", ()),
                                (SUP_KILLED, "killed", ("why",))):
            m = rx.match(text)
            if m:
                entry = {"event": event, "cell": m.group(1), "at": at, "text": text}
                for key, value in zip(keys, m.groups()[1:]):
                    entry[key] = int(value) if key == "n" else value
                out.append(entry)
                break
        else:
            m = SUP_ANY.match(text)
            out.append({"event": "other", "cell": m.group(1) if m else "", "at": at, "text": text})
    return out


def crash_status(lines, seq, kind="Split"):
    """rigsplit.parse_op_status's record of `seq` plus Slice 2d's: texts (every line under the seq, Supervisor lines
    included, in order), lost_aborted (the cell of 'aborted <cell> lost'), paused [cell], rebound [(cell, inc)], blips
    [cell] ('<cell> back, op resumed'), retired / settle (their broadcast pairs), channel_removed, supervisor
    (parse_supervisor)."""
    out = rigsplit.parse_op_status(lines, seq, kind)
    out.update({"texts": seq_texts(lines, seq), "lost_aborted": None, "paused": [], "rebound": [], "blips": [],
                "retired": None, "settle": None, "channel_removed": None, "supervisor": parse_supervisor(lines, seq)})
    for state in out["states"]:
        for rx, key in ((ABORTED_LOST, "lost_aborted"), (CHANNEL_REMOVED, "channel_removed")):
            m = rx.match(state)
            if m:
                out[key] = m.group(1)
        m = STEP_PAIR.match(state) or SETTLE.match(state)
        if m:
            out["settle" if m.re is SETTLE else m.group(1)] = m.group(m.lastindex)
        if PAUSED.match(state):
            out["paused"].append(PAUSED.match(state).group(1))
        if REBOUND.match(state):
            out["rebound"].append(REBOUND.match(state).groups())
        if BLIP.match(state):
            out["blips"].append(BLIP.match(state).group(1))
    return out


def order_problems(texts, patterns):
    """What `texts` misses of `patterns` (regexes, searched in order: each after the previous one's first match), as
    messages; empty when every one is found in order."""
    at, problems = 0, []
    for pattern in patterns:
        rx = re.compile(pattern)
        hit = next((i for i in range(at, len(texts)) if rx.search(texts[i])), None)
        if hit is None:
            anywhere = any(rx.search(t) for t in texts)
            problems.append(f"'{pattern}' {'out of order' if anywhere else 'missing'}")
            continue
        at = hit + 1
    return problems


def op_pattern(state, kind="Split"):
    """The regex of one op state line ('<Kind> <op>: <state>'), the state taken literally."""
    return rf"^{kind} \S+: {re.escape(state)}$"


# ------------------------------------------------------------------ processes (decision 18's kill)
def kill_script(exe, cell):
    """The PowerShell snippet killing the build's processes of a cell: one Get-CimInstance query filtered on the
    build's executable and rigmerge.channel_filter, each match stopped. Prints how many it found."""
    name = exe.replace("\\", "/").rsplit("/", 1)[-1]
    return ("$p = @(Get-CimInstance Win32_Process -Filter \"Name='" + name.replace("'", "''") + "'\" -ErrorAction Stop"
            " | Where-Object { $_.ExecutablePath -eq '" + exe.replace("'", "''") + "' -and $_.CommandLine -like '"
            + rigmerge.channel_filter(cell) + "' }); foreach ($q in $p) { Stop-Process -Id $q.ProcessId -Force "
            "-ErrorAction Stop }; $p.Count")


def process_sequence(samples):
    """The process counts of a series of samples with consecutive repeats collapsed (None, a failed count, kept)."""
    out = []
    for n in samples:
        if not out or out[-1] != n:
            out.append(n)
    return out


def contains_in_order(sequence, wanted):
    """Whether `wanted` is a subsequence of `sequence`."""
    it = iter(sequence)
    return all(any(x == w for x in it) for w in wanted)


# ------------------------------------------------------------------ map-side lines (decisions 10-12, 17, 18)
FAULT = re.compile(r"FAULT: dying on (.+) \((MESH_FAULT_\w+)\)")
FENCE = re.compile(r"\bFence: (\S+) ")
LAYOUT_HELLO = re.compile(r"LayoutHello sent: cell (\S+) incarnation ([0-9a-f]+)")
ROLLBACK = re.compile(r"Handoff ROLLBACK (\S+) -> zone (\S+?):")
HANDOFF_ACK = re.compile(r"Handoff ACK (\S+) by zone (\S+?):")
MONSTER_TAKEN = re.compile(r"Monster (?:promoted|spawned) by handoff (\S+) (\S+?)->(\S+) at ")
MONSTER_SENT = re.compile(r"Monster handoff (\S+) (\S+?)->(\S+) at ")
MONSTER_BACK_FAILED = re.compile(r"Monster handoff (\S+) (\S+?)->(\S+) failed there \(.*\); re-promoted here")
BORROWED_DIED = re.compile(r"Borrowed monster (\S+) died here")
BORROWED_RELEASED = re.compile(r"Borrowed monster (\S+) released \(([^)]*)\)")
BORROWED_STATIC_RELEASED = re.compile(r"Borrowed static (\S+) released \(([^)]*)\)")
SPAWN_AREAS = re.compile(r"Spawn areas of (\S+): (\d+) monster\(s\) planned in the base rectangle")
SESSION_LOST = re.compile(r"Session lost \(([^)]*)\); re-login (\d+)")
RELOGIN_ROUTED = re.compile(r"Re-login routed to (\S+):(\d+)")


def faults(lines, key):
    """[what] of a cell's 'FAULT: dying on <what> (<key>)' lines for one fault key."""
    return [m.group(1) for line in lines for m in [FAULT.search(line)] if m and m.group(2) == key]


def incarnation(lines, cell):
    """The incarnation of a cell's first 'LayoutHello sent: cell <cell> incarnation <inc>' line, or None."""
    return next((m.group(2) for line in lines for m in [LAYOUT_HELLO.search(line)] if m and m.group(1) == cell), None)


def disallowed_lines(cell, lines, faulted=None, retired=None, allow_aborted=False):
    """The map-side lines a cell logs that the case does not allow (decision 19): any Split/Merge/Retire problem of
    rigmerge.OP_PROBLEM's set (an abort only where `allow_aborted`), a Fence line, a FAULT line on any cell but
    `faulted`; the prune line of the `retired` zone and a cell's own 'Drain on' line are allowed."""
    problem = re.compile(r"retired before it owned anything|\b(Merge|Retire)\b|\bRetired\b|Split \S+: frozen\b"
                         + ("" if allow_aborted else r"|Split \S+: aborted\b"))
    out = []
    for line in lines:
        prune = rigmerge.PRUNE.search(line)
        if prune and prune.group(1) == retired:
            continue
        drain = rigmerge.DRAIN_ON.search(line)
        if drain and drain.group(1) == cell:
            continue
        if FAULT.search(line):
            if cell != faulted:
                out.append(line)
            continue
        if FENCE.search(line) or problem.search(line):
            out.append(line)
    return out


# ------------------------------------------------------------------ layout applies per lifetime (decisions 15-18)
def crash_expected_lives(case, split, cell, own_rect, pairs, drain_timeout=False):
    """[[(owned, interest, pair)] per process lifetime] of every layout a cell of the split's map applies in a case.

    prewarm: the base's first layout, e1 (P, P), the retired broadcast (P, P); the child one lifetime with its staged e1
    (nothing, R); the others their own rect at first, e1, retired. drain: the base the split's four; the child
    lifetime 1 e1 (nothing, R), e2 (R, R) and lifetime 2 e2 (R, R), e3 (R, R); the others four. base: the base the
    split's four, then a second lifetime with one apply (kept, kept) at the settle pair (or e3); the child the split's
    three; the others four. A settle broadcast ('settle' in pairs) adds one apply to every cell's last lifetime (the
    base's first in the base case); after a drain timeout the base's e3 keeps interest P. A pair of None is not graded
    (the first layout's is never known)."""
    kept, given = rigsplit.split_rects(split.base_rect, split.axis, split.at, split.side)
    p, nothing, any_rect = split.base_rect, rigsplit.NOTHING, rigsplit.ANY
    e1, e2, e3, retired, settle = (pairs.get(k) for k in ("e1", "e2", "e3", "retired", "settle"))
    if case == "prewarm":
        if cell == split.base:
            return [[(p, p, None), (p, p, e1), (p, p, retired)]]
        if cell == split.child:
            return [[(nothing, given, e1)]]
        return [[(own_rect, any_rect, None), (own_rect, any_rect, e1), (own_rect, any_rect, retired)]]
    if cell == split.base:
        lives = [[(p, p, None), (p, p, e1), (kept, p, e2), (kept, p if drain_timeout else kept, e3)]]
        tail = (kept, kept)
    elif cell == split.child:
        lives = ([[(nothing, given, e1), (given, given, e2)], [(given, given, e2), (given, given, e3)]]
                 if case == "drain" else [[(nothing, given, e1), (given, given, e2), (given, given, e3)]])
        tail = (given, given)
    else:
        lives = [[(own_rect, any_rect, None), (own_rect, any_rect, e1), (own_rect, any_rect, e2),
                  (own_rect, any_rect, e3)]]
        tail = (own_rect, any_rect)
    if settle:
        lives[-1].append(tail + (settle,))
    if case == "base" and cell == split.base:
        lives.append([(kept, kept, settle or e3)])
    return lives


def lifetime_apply_violations(cell, info_lines, split, lives, label="the case"):
    """What a cell of the split's map breaks of a per-lifetime apply grade, as messages; empty when it keeps it.

    `lives` is [[(owned, interest, pair)]] per process lifetime, or a function of the cell's own rect (its first
    lifetime's 'Own cell from' line; ANY for a child) giving them (crash_expected_lives). The lifetimes are
    rigsplit.lifetimes' (a child's start at 'Own cell from layout: <child>'); in each, the applies rise in version,
    the first comes before any player registers there, and they are exactly the expected ones (a pair other than None
    must be the apply's '<incarnation>/<version>'); every apply comes from one coordinator incarnation; the base's own
    cell is its base rect; no Static-neighbour apply line."""
    child = cell if cell == split.child else None
    found = rigsplit.lifetimes(info_lines, child)
    if not found:
        return [f"{cell}: no process lifetime since the scenario started"]
    if callable(lives):
        own = rigtools.parse_own_cell(found[0][0]) if child is None else None
        lives = lives(own[2] if own else rigsplit.ANY)
    problems = []
    if len(found) != len(lives):
        problems.append(f"{cell}: {len(found)} process lifetime(s), {label} expects {len(lives)}")
    incarnations = set()
    for k, (lines, expected) in enumerate(zip(found, lives), 1):
        life = f" lifetime {k}" if len(lives) > 1 else ""
        own = rigtools.parse_own_cell(lines[0]) if child is None else None
        if cell == split.base and own and not rigsplit.same_rect(own[2], split.base_rect):
            problems.append(f"{cell}{life}: own cell {rigsplit.format_rect(own[2])}, {label} expects "
                            f"{rigsplit.format_rect(split.base_rect)}")
        if any(rigtools.NEIGHBOURS_APPLIED.search(line) for line in lines):
            problems.append(f"{cell}{life}: a Static-neighbour apply line on a Dynamic map")
        applied = [(i, a) for i, line in enumerate(lines) for a in [rigsplit.parse_apply(line)] if a]
        incarnations |= {a["incarnation"] for _, a in applied}
        first_player = next((i for i, line in enumerate(lines) if rigtools.PLAYER_REGISTERED.search(line)), None)
        if applied and first_player is not None and applied[0][0] > first_player:
            problems.append(f"{cell}{life}: a player registered before the first layout was applied")
        versions = [a["version"] for _, a in applied]
        if any(b <= a for a, b in zip(versions, versions[1:])):
            problems.append(f"{cell}{life}: layout versions not rising ({', '.join(map(str, versions))})")
        if len(applied) != len(expected):
            problems.append(f"{cell}{life}: {len(applied)} layout(s) applied, {label} expects {len(expected)}")
        for n, ((_, a), (owned, interest, pair)) in enumerate(zip(applied, expected), 1):
            at = f"{a['incarnation']}/{a['version']}"
            if pair is not None and at != pair:
                problems.append(f"{cell}{life}: apply {n} is layout {at}, {label} expects {pair}")
            if not rigsplit.same_rect(a["owned"], owned) or not rigsplit.same_rect(a["interest"], interest):
                problems.append(f"{cell}{life}: apply {n} ({at}) owns {rigsplit.format_rect(a['owned'])} interest "
                                f"{rigsplit.format_rect(a['interest'])}, expected owns {rigsplit.format_rect(owned)} "
                                f"interest {rigsplit.format_rect(interest)}")
    if len(incarnations) > 1:
        problems.append(f"{cell}: layouts from {len(incarnations)} coordinator incarnations "
                        f"({', '.join(sorted(incarnations))})")
    return problems


# ------------------------------------------------------------------ the base's crash (decision 18)
def before(lines, second):
    """The lines stamped before `second` (a datetime; lines without a stamp are dropped)."""
    return [line for line in lines if (rigtools.stamp_datetime(line) or second) < second]


def held_monsters(child_lines, base, child, head=None):
    """The monster ids the child holds at the end of `child_lines` (its lines up to the kill): taken by handoff
    base->child, minus those it handed back (child->base) unless the hand-back 'failed there; re-promoted here', minus
    those that died here; only ids whose mesh-id head (rigsplit.mesh_id_parts) is `head` ('<base>:<incarnation>')
    or, without a head, starts with '<base>:'. Sorted."""
    held = set()
    for line in child_lines:
        m = MONSTER_TAKEN.search(line)
        if m and (m.group(2), m.group(3)) == (base, child):
            held.add(m.group(1))
            continue
        m = MONSTER_BACK_FAILED.search(line)
        if m and (m.group(2), m.group(3)) == (child, base):
            held.add(m.group(1))
            continue
        m = MONSTER_SENT.search(line)
        if m and (m.group(2), m.group(3)) == (child, base):
            held.discard(m.group(1))
            continue
        m = BORROWED_DIED.search(line)
        if m:
            held.discard(m.group(1))

    def ours(mesh_id):
        h, _ = rigsplit.mesh_id_parts(mesh_id)
        return h == head if head else h.startswith(base + ":")
    return sorted(i for i in held if ours(i))


HAND_BACK_AFTER_KILL_S = 5.0   # LiteNetLib's disconnect timeout: until then the child still hands monsters to the dead base


def unreleased(held, child_lines, base, child, kill_second, within_s=30.0):
    """The held ids with neither a 'Borrowed monster <id> released (...)' line on the child within `within_s` of the
    kill second, nor a hand-back to the base (or a death) stamped from the kill second until the child's link to the
    base times out (HAND_BACK_AFTER_KILL_S): a monster handed to the dead base is no longer the child's to release (the
    base's restart spawns it again; the pair-sum check counts it)."""
    end = kill_second + datetime.timedelta(seconds=within_s)
    gone_by = kill_second + datetime.timedelta(seconds=HAND_BACK_AFTER_KILL_S)
    ok = set()
    for line in child_lines:
        t = rigtools.stamp_datetime(line)
        if t is None:
            continue
        m = BORROWED_RELEASED.search(line)
        if m and kill_second <= t <= end:
            ok.add(m.group(1))
        m = MONSTER_SENT.search(line)
        if m and (m.group(2), m.group(3)) == (child, base) and kill_second <= t <= gone_by:
            ok.add(m.group(1))
        m = BORROWED_DIED.search(line)
        if m and kill_second <= t <= gone_by:
            ok.add(m.group(1))
    return [i for i in held if i not in ok]


def statics_released(child_lines, head):
    """How many 'Borrowed static <id> released' lines the child logged for ids starting '<head>:'."""
    return sum(1 for line in child_lines for m in [BORROWED_STATIC_RELEASED.search(line)]
               if m and m.group(1).startswith(head + ":"))


def windows(lines, start=None, end=None):
    """The parsed Work window lines (rigtools.parse_work_window) stamped within [start, end] (either bound optional)."""
    out = []
    for line in lines:
        w = rigtools.parse_work_window(line)
        if w and (start is None or w["end"] >= start) and (end is None or w["end"] <= end):
            out.append(w)
    return out


def monster_pairs(base_windows, child_windows, tolerance_s=5.0):
    """[(base window end, owned monsters base + child)] pairing each base window with the child's latest window stamped
    within `tolerance_s` of it; a base window with none is skipped."""
    out = []
    for w in base_windows:
        near = [c for c in child_windows if abs((c["end"] - w["end"]).total_seconds()) <= tolerance_s]
        if near:
            c = max(near, key=lambda x: x["end"])
            out.append((w["end"], w["owned_monsters"] + c["owned_monsters"]))
    return out


def monster_sum_problems(sums, total, run=3):
    """Why owned-monster sums fail decision 18, as messages: `total` for `run` consecutive pairs at least once, and never
    more than `total` for `run` consecutive pairs."""
    problems = []
    if not any(all(s == total for s in sums[i:i + run]) for i in range(len(sums) - run + 1)):
        problems.append(f"never {total} for {run} consecutive pairs ({', '.join(map(str, sums[:24])) or 'no pairs'})")
    if any(all(s > total for s in sums[i:i + run]) for i in range(len(sums) - run + 1)):
        problems.append(f"over {total} for {run} consecutive pairs ({', '.join(map(str, sums[:24]))})")
    return problems


# ------------------------------------------------------------------ the bots (decisions 14, 16-18)
def session_losses(lines):
    """[(why, n)] of a bot log's 'Session lost (<why>); re-login <n>' lines."""
    return [(m.group(1), int(m.group(2))) for line in lines for m in [SESSION_LOST.search(line)] if m]


def routed(lines):
    """['<ip>:<port>'] of a bot log's 'Re-login routed to <ip:port>' lines."""
    return [f"{m.group(1)}:{m.group(2)}" for line in lines for m in [RELOGIN_ROUTED.search(line)] if m]


def bot_problems(result, migrations=None, relogins=None):
    """Why a bot's RESULT misses its expected migrations (an int, a set of allowed counts, or None for any) and relogins
    (an int or None), as messages."""
    if not result:
        return ["no RESULT stand line"]
    problems = []
    if migrations is not None:
        allowed = migrations if isinstance(migrations, (set, frozenset)) else {migrations}
        if result.get("migrations") not in allowed:
            problems.append(f"migrations={result.get('migrations')}, expected {' or '.join(map(str, sorted(allowed)))}")
    if relogins is not None and result.get("relogins") != relogins:
        problems.append(f"relogins={result.get('relogins')}, expected {relogins}")
    return problems


def drain_bot_problems(result, bot_lines, starts, acks_after, line_x):
    """(f, problems) of an R-bot in the drain case (decision 17): f its fellBack=True count (0 or 1); migrations and
    its Handoff START count base->child both 1 + f; its last MIGRATION ready at x >= line_x; at least one Handoff ACK by
    the child after the child came back (`acks_after`)."""
    f = rigmerge.fell_back(bot_lines).count(True)
    if not result:
        return f, ["no RESULT stand line"]
    problems = [] if f <= 1 else [f"{f} fallback hops, expected 0 or 1"]
    if result.get("migrations") != 1 + f:
        problems.append(f"migrations={result.get('migrations')}, expected {1 + f} (fellBack {f})")
    if starts != 1 + f:
        problems.append(f"{starts} Handoff START to the child, expected {1 + f}")
    last = (rigsplit.parse_migrations(bot_lines) or [None])[-1]
    if last is None or not last["ready"] or last["x"] < line_x:
        problems.append("last MIGRATION " + ("missing" if last is None else
                                             f"ready={last['ready']} at x={last['x']:g}") + f", expected ready at x >= {line_x:g}")
    if acks_after < 1:
        problems.append("no Handoff ACK by the child after it came back")
    return f, problems
