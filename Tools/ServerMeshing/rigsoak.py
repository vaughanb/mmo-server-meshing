"""Pure readers of the dynamic-cell soak (regress_soak.py, DESIGN 0.91): hours of automatic splits and merges with
child kills and central restarts, graded over the whole run rather than per operation.

What regress_soak hands in and what comes out:
  - the coordinator store (Temp/ServerMesh.Coordinator.json, parsed): each base cell's live family, the live children
    a kill may pick, and the store's own size (entries, retired entries, the fenced set, aborted ids);
  - the central's kit log, its operation, policy and supervisor lines read as the status file's `auto` lines with
    their stamps (central_stamped: a restarted central logs its recovery's lines, an abort among them, before the rig's
    command watcher is attached, so the status file never sees them): every operation from
    its `requested` line to its end (done, aborted, ended, dropped; frozen only when nothing ended it), the policy's
    lines (planned, refused, deferred, flap, measured) and the supervisor's;
  - every cell's `Layout <pair> applied` lines: who owns what at each pair (no rectangle with two owners), and the
    owned rectangles riglayout.timeline derives for the done operations' pairs;
  - samples (t, value) of a process's memory or the store: whether the series is flat once warmed up.
The load schedule and the bots' lines are rigsoakload's. Nothing here touches a file or a process
(python -m unittest Tools/ServerMeshing/test_rigsoak.py).
"""
import collections
import re

import rigauto
import rigcentral
import riglayout
import rigsplit
import rigsplitcrash
import rigtools

PENDING, ACTIVE, DRAINING, RETIRED = 1, 2, 3, 4   # MeshCellState
BASES = tuple(riglayout.TEST_MAP_RECTS)            # the test map's base cells


# ------------------------------------------------------------------ the store
def _entries(store, map_id):
    return [e for e in (store.get("broadcast") or {}).get("entries") or [] if e.get("mapId") == map_id]


def families(store, map_id, bases=BASES, pending=False):
    """{base: [live ids]} of the store's broadcast layout: a base with no entry is a family of itself; a live id is an
    entry Active or Draining (Pending too with `pending`: a child whose split is still running, which may yet abort),
    the base first, then the others by id."""
    out = {b: [b] for b in bases}
    live = (ACTIVE, DRAINING, PENDING) if pending else (ACTIVE, DRAINING)
    for e in _entries(store, map_id):
        base, cell = str(e.get("baseId") or ""), str(e.get("id") or "")
        if base not in out or e.get("state") not in live or cell == base:
            continue
        out[base].append(cell)
    for base in out:
        out[base] = [base] + sorted(c for c in out[base] if c != base)
    return out


def children(store, map_id):
    """The ids of the Active dynamic children (an id other than its base) in the store's broadcast layout."""
    return sorted(str(e.get("id")) for e in _entries(store, map_id)
                  if e.get("state") == ACTIVE and e.get("id") and e.get("id") != e.get("baseId"))


def store_stats(store, size_bytes):
    """{bytes, entries, retired, fenced, fenced_max, aborted, operation} of a store: its broadcast entries (all maps),
    the Retired ones among them, the fenced set (and the most any one cell holds), the aborted ids, an operation."""
    entries = (store.get("broadcast") or {}).get("entries") or []
    per_cell = collections.Counter(str(f.get("cellId") or "").lower() for f in store.get("fencedIncarnations") or [])
    return {"bytes": int(size_bytes), "entries": len(entries), "retired": sum(1 for e in entries if e.get("state") == RETIRED),
            "fenced": sum(per_cell.values()), "fenced_max": max(per_cell.values(), default=0),
            "aborted": len(store.get("abortedIds") or []), "operation": bool(store.get("hasOperation"))}


# ------------------------------------------------------------------ the operations
MIRRORED = ("Split ", "Merge ", "Layout op ", "Supervisor:", "Policy: ", "Unfreeze ")   # ServerMeshLayoutCommandFile's


def central_stamped(lines):
    """[(epoch seconds, 'auto <text>')] of the central's log lines (every severity, every lifetime) whose text the status
    file would mirror, in stamp order (lines of one second keep the order they were read in)."""
    out = []
    for line in lines:
        at = rigtools.stamp_datetime(line)
        text = rigcentral.payload(line)
        if at is not None and text.startswith(MIRRORED):
            out.append((at.timestamp(), f"auto {text}"))
    out.sort(key=lambda p: p[0])
    return out

SPLIT_REQUESTED = re.compile(r"^requested (\S+) ([xz]) (-?[\d.]+) ([+-]) child (\S+)$")
MERGE_REQUESTED = re.compile(r"^requested (\S+) (\S+)( collapse)?$")
STEP_PAIR = re.compile(r"^(e[123]|tidy|reverted|retired|settle) broadcast ([0-9a-f]+/\d+)$")
SETTLE = rigsplitcrash.SETTLE
RESUMED = re.compile(r"^resumed at (\S+) after a central restart$")
ABORTED_RESTART = re.compile(r"^aborted the central restarted before (\S+)$")
DROPPED = re.compile(r"^dropped after the restart: (.*)$")
FROZEN_OP = re.compile(r"^(Split|Merge) (\S+): frozen\b")             # an operation the supervisor froze (2d decision 9)
OP_REQUESTED = re.compile(r"^(Split|Merge) (\S+): requested (\S+) ")
OPERATOR_UNFROZEN = re.compile(r"Unfreeze (\S+): family (\S+) of map (\S+) unfrozen by the operator")


def new_op(kind, op, t):
    return {"kind": kind, "op": op, "requested": None, "a": None, "b": None, "cut": None, "collapse": False,
            "states": [], "pairs": {}, "outcome": None, "frozen": False, "resumed": [], "seen_at": t, "ended_at": None}


def _apply_state(rec, state, t):
    rec["states"].append(state)
    m = STEP_PAIR.match(state) or SETTLE.match(state)
    if m:
        step, pair = (m.group(1), m.group(2)) if m.re is STEP_PAIR else ("settle", m.group(1))
        rec["pairs"][step] = pair
    if state.startswith("requested "):
        rec["requested"] = state[len("requested "):]
        sm, mm = SPLIT_REQUESTED.match(state), MERGE_REQUESTED.match(state)
        if rec["kind"] == "Split" and sm:
            rec["a"], rec["b"], rec["cut"] = sm.group(1), sm.group(5), (sm.group(2), float(sm.group(3)), sm.group(4))
        elif rec["kind"] == "Merge" and mm:
            rec["a"], rec["b"], rec["collapse"] = mm.group(1), mm.group(2), bool(mm.group(3))
    if RESUMED.match(state):
        rec["resumed"].append(RESUMED.match(state).group(1))
    if state.startswith("frozen"):
        rec["frozen"] = True
    outcome = ("done" if state == "done" else "aborted" if state.startswith("aborted") or ABORTED_RESTART.match(state)
               else "ended" if rigsplit.ENDED.match(state) else "dropped" if DROPPED.match(state) else None)
    if outcome and rec["outcome"] is None:
        rec["outcome"], rec["ended_at"] = outcome, t


def operations(stamped):
    """[op] of the `auto` lines of the status file, `stamped` = [(wall time, raw line)] in file order. An op starts at
    its `requested` line (an id seen again after its op ended, a restarted central's numbering, starts a new one); a
    line of an id with no live op starts one too. Each op: {kind, op, requested, a, b (split: parent, child; merge:
    survivor, retiring), cut (axis, at, side), collapse, states, pairs {step: pair}, outcome (done, aborted, ended,
    dropped or None), frozen (a frozen state was seen), resumed [steps], seen_at, ended_at}."""
    out, live = [], {}
    for t, raw in stamped:
        texts = rigauto.status_texts([raw])
        if not texts:
            continue
        m = rigsplit.OP_STATE.match(texts[0])
        if not m:
            continue
        kind, op, state = m.groups()
        rec = live.get((kind, op))
        if rec is None or state.startswith("requested ") and rec["states"]:
            rec = new_op(kind, op, t)
            live[(kind, op)] = rec
            out.append(rec)
        _apply_state(rec, state, t)
    return out


def op_counts(ops):
    """{splits, merges, collapses, aborted, ended, dropped, open, frozen_open} of a list of operations."""
    c = collections.Counter()
    for op in ops:
        if op["outcome"] == "done":
            c["splits" if op["kind"] == "Split" else "collapses" if op["collapse"] else "merges"] += 1
        elif op["outcome"]:
            c[op["outcome"]] += 1
        else:
            c["frozen_open" if op["frozen"] else "open"] += 1
    return {k: c.get(k, 0) for k in ("splits", "merges", "collapses", "aborted", "ended", "dropped", "open", "frozen_open")}


def stuck(ops, now, limit_s):
    """The ops with no outcome, or one that took longer than limit_s from its first line (op ids with their age)."""
    out = []
    for op in ops:
        end = op["ended_at"] if op["ended_at"] is not None else now
        if op["outcome"] is None or end - op["seen_at"] > limit_s:
            out.append((f"{op['kind']} {op['op']}", round(end - op["seen_at"]), op["outcome"] or "open"))
    return out


# ------------------------------------------------------------------ the policy's and the supervisor's lines
def policy_counts(lines):
    """{planned_splits, planned_merges, refused, deferred, flaps, measured, not_measured, coordinator_refused} of the
    `auto` lines."""
    texts = rigauto.policy_texts(lines)
    return {"planned_splits": sum(1 for t in texts if rigauto.PLANNED.match(t)),
            "planned_merges": sum(1 for t in texts if rigauto.MERGE_PLANNED.match(t)),
            "refused": sum(1 for t in texts if rigauto.REFUSED.match(t)),
            "deferred": sum(1 for t in texts if rigauto.DEFERRED.match(t)),
            "flaps": sum(1 for t in texts if rigauto.FLAP.match(t)),
            "measured": sum(1 for t in texts if rigauto.SPLIT_MEASURED.match(t) or rigauto.MERGE_MEASURED.match(t)),
            "not_measured": sum(1 for t in texts if rigauto.NOT_MEASURED.match(t)),
            "coordinator_refused": len(rigauto.refusals(lines))}


def supervisor_counts(lines):
    """{down, hung, back, failed, collapse, cannot, killed, other} of the `auto` supervisor lines."""
    c = collections.Counter(e["event"] for e in rigsplitcrash.parse_supervisor(lines, rigauto.AUTO_SEQ))
    return {k: c.get(k, 0) for k in ("down", "hung", "back", "failed", "collapse", "cannot", "killed", "other")}


# ------------------------------------------------------------------ who owns what at each pair
def applies_by_pair(cell_lines):
    """{pair: {cell: [owned rect, ...]}} of every 'Layout <pair> applied' line of every cell ({cell: lines})."""
    out = {}
    for cell, lines in cell_lines.items():
        for line in lines:
            a = rigsplit.parse_apply(line)
            if a:
                out.setdefault(f"{a['incarnation']}/{a['version']}", {}).setdefault(a["cell"], []).append(a["owned"])
    return out


def intersection(a, b, slack=rigsplit.RECT_TOLERANCE_M):
    """The (width, depth) two rectangles share past the print slack, or None when they do not overlap."""
    if a is rigsplit.NOTHING or b is rigsplit.NOTHING:
        return None
    w = min(a[0] + a[2], b[0] + b[2]) - max(a[0], b[0])
    d = min(a[1] + a[3], b[1] + b[3]) - max(a[1], b[1])
    return (w, d) if w > slack and d > slack else None


def two_owners(by_pair):
    """[(pair, cell a, cell b, rect a, rect b)] wherever two cells own overlapping rectangles at one pair (a cell
    applying a pair twice, once per process lifetime, is compared with every other cell's applies of it)."""
    out = []
    for pair, cells in sorted(by_pair.items()):
        ids = sorted(cells)
        for i, a in enumerate(ids):
            for b in ids[i + 1:]:
                hit = next(((ra, rb) for ra in cells[a] for rb in cells[b] if intersection(ra, rb)), None)
                if hit:
                    out.append((pair, a, b, hit[0], hit[1]))
    return out


def layout_ops(ops):
    """The riglayout ops of the done splits and merges in order (aborted, ended and open ones change nothing in the end
    and their pairs are not derived), and the ops skipped for a requested line that did not parse."""
    out, skipped = [], []
    for op in ops:
        if op["outcome"] != "done":
            continue
        if op["a"] is None or op["b"] is None:
            skipped.append(f"{op['kind']} {op['op']}")
        elif op["kind"] == "Split":
            out.append(riglayout.split_op(op["a"], op["b"], *op["cut"], pairs=op["pairs"]))
        else:
            pairs = {k: v for k, v in op["pairs"].items() if k in ("e1", "e2", "e3", "tidy")}
            out.append(riglayout.merge_op(op["a"], op["b"], pairs, dead=op["collapse"]))
    return out, skipped


def expected_by_pair(rects, lops):
    """{pair: {cell: owned}} riglayout.timeline derives for the ops' pairs (ValueError for a sequence the map cannot make)."""
    out = {}
    for cell, applies in riglayout.timeline(rects, lops).items():
        for owned, _, pair in applies:
            if pair:
                out.setdefault(pair, {})[cell] = owned
    return out


def mismatches(by_pair, expected):
    """[(pair, cell, applied, expected)] of every apply at a derived pair that owns another rectangle than derived, or
    by a cell the derivation has no entry for; and how many apply lines were at pairs not derived."""
    bad, underived = [], 0
    for pair, cells in by_pair.items():
        want = expected.get(pair)
        if want is None:
            underived += sum(len(r) for r in cells.values())
            continue
        for cell, rects in cells.items():
            for rect in rects:
                if cell not in want or not rigsplit.same_rect(rect, want[cell]):
                    bad.append((pair, cell, rect, want.get(cell, "no entry")))
    return bad, underived


# ------------------------------------------------------------------ growth
Flat = collections.namedtuple("Flat", "ok samples first_max second_max growth slope_per_h")


def flatness(series, warmup_s, abs_tol, rel_tol, min_span_s):
    """Whether a series [(t, value)] stays flat after `warmup_s` from its first sample: the peak of its second half at
    most abs_tol + rel_tol x the peak of its first half (a ratchet such as a GC heap reaching its high-water mark early
    passes; a leak does not). None when the warmed-up series spans less than min_span_s. slope_per_h is the least-squares
    slope over the warmed-up samples, a trend to report."""
    if not series:
        return None
    t0 = series[0][0] + warmup_s
    warm = [(t, v) for t, v in series if t >= t0]
    if len(warm) < 4 or warm[-1][0] - warm[0][0] < min_span_s:
        return None
    mid = warm[0][0] + (warm[-1][0] - warm[0][0]) / 2.0
    first = max(v for t, v in warm if t < mid)
    second = max(v for t, v in warm if t >= mid)
    n = len(warm)
    mt, mv = sum(t for t, _ in warm) / n, sum(v for _, v in warm) / n
    var = sum((t - mt) ** 2 for t, _ in warm)
    slope = (sum((t - mt) * (v - mv) for t, v in warm) / var * 3600.0) if var > 0 else 0.0
    growth = second - first
    return Flat(growth <= abs_tol + rel_tol * abs(first), n, first, second, growth, slope)
