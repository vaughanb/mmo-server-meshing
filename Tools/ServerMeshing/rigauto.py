"""Pure readers for Slice 3's automatic policy (Docs/ServerMeshing/slices/slice3_design.md decisions 16-19): the
`Policy: ...` lines the play runner mirrors under the pseudo-seq `auto` while no command is live, the automatic
operations' own status lines under that seq (rigsplit's and rigmerge's parsers take any seq), and the cells' work
windows the benefit grades read. No rig access: regress_auto hands lines in; test_rigauto exercises every reader.
"""
import datetime
import re

import rigmerge
import rigsplit
import rigtools

AUTO_SEQ = "auto"
POLICY = re.compile(r"^Policy: (.*)$")
PLANNED = re.compile(r"^split of (\S+) planned: ([xz]) (-?[\d.]+) ([+-]) child (\S+), util ([\d.]+) -> peak ([\d.]+) "
                     r"\((\S+) ([\d.]+), (\S+) ([\d.]+)\), (\d+) cut\(s\) tried$")
REFUSED_PEAK = re.compile(r"^split of (\S+) refused: best peak ([\d.]+) over ([\d.]+) x util ([\d.]+)$")
REFUSED = re.compile(r"^split of (\S+) refused: (.*)$")
DEFERRED = re.compile(r"^split of (\S+) deferred: host (\S+) at ([\d.]+)/(\d+) cores$")
MERGE_PLANNED = re.compile(r"^merge of (\S+) (\S+) planned: predicted ([\d.]+) \((\S+) ([\d.]+), (\S+) ([\d.]+)\)$")
SPLIT_MEASURED = re.compile(r"^split (\S+) measured: predicted peak ([\d.]+), measured ([\d.]+) "
                            r"\((\S+) ([\d.]+), (\S+) ([\d.]+)\), relief (-?\d+) %$")
MERGE_MEASURED = re.compile(r"^merge (\S+) measured: predicted ([\d.]+), measured ([\d.]+) \((\S+)\)$")
NOT_MEASURED = re.compile(r"^(split|merge) (\S+) not measured \((.*)\)$")
FLAP = re.compile(r"^flap on (\S+): ")
SUPERVISOR = re.compile(r"^Supervisor: ")


def status_texts(lines, seq=AUTO_SEQ):
    """The texts of the status file's lines under `seq`, in order."""
    out = []
    for raw in lines:
        parts = raw.strip().split(None, 1)
        if len(parts) == 2 and parts[0] == str(seq):
            out.append(parts[1])
    return out


def policy_texts(lines, seq=AUTO_SEQ):
    """The `Policy: ` lines under `seq`, without the prefix."""
    return [m.group(1) for text in status_texts(lines, seq) for m in [POLICY.match(text)] if m]


def refusals(lines, seq=AUTO_SEQ):
    """The `Layout op refused: ...` rules under `seq` (a request the coordinator turned down)."""
    return [m.group(1) for text in status_texts(lines, seq) for m in [rigsplit.REFUSED.match(text)] if m]


def supervisor_texts(lines, seq=AUTO_SEQ):
    return [text for text in status_texts(lines, seq) if SUPERVISOR.match(text)]


def parse_planned(text):
    """{cell, axis, at, side, child, util, peak, parent_util, child_util, tried} of a planned-split line, or None."""
    m = PLANNED.match(text)
    if not m:
        return None
    g = m.groups()
    return {"cell": g[0], "axis": g[1], "at": float(g[2]), "side": g[3], "child": g[4], "util": float(g[5]),
            "peak": float(g[6]), "parent_util": float(g[8]), "child_util": float(g[10]), "tried": int(g[11])}


def parse_refusal(text):
    """{cell, reason, peak, ratio, util} of a refused-split line (peak, ratio and util None unless it was the gate), or None."""
    m = REFUSED_PEAK.match(text)
    if m:
        return {"cell": m.group(1), "reason": text[text.index("refused: ") + 9:], "peak": float(m.group(2)),
                "ratio": float(m.group(3)), "util": float(m.group(4))}
    m = REFUSED.match(text)
    if m:
        return {"cell": m.group(1), "reason": m.group(2), "peak": None, "ratio": None, "util": None}
    return None


def parse_merge_planned(text):
    m = MERGE_PLANNED.match(text)
    if not m:
        return None
    g = m.groups()
    return {"a": g[0], "b": g[1], "predicted": float(g[2]), "utils": {g[3]: float(g[4]), g[5]: float(g[6])}}


def parse_split_measured(text):
    m = SPLIT_MEASURED.match(text)
    if not m:
        return None
    g = m.groups()
    return {"op": g[0], "predicted": float(g[1]), "measured": float(g[2]), "utils": {g[3]: float(g[4]), g[5]: float(g[6])},
            "relief": int(g[7])}


def parse_merge_measured(text):
    m = MERGE_MEASURED.match(text)
    if not m:
        return None
    return {"op": m.group(1), "predicted": float(m.group(2)), "measured": float(m.group(3)), "survivor": m.group(4)}


def first(texts, parse):
    """The first parse(text) that is not None, else None."""
    for text in texts:
        found = parse(text)
        if found is not None:
            return found
    return None


def disallowed_policy(texts, allowed_prefixes):
    """The policy texts that start with none of the allowed prefixes (decision 17: a refused, deferred, flap,
    steady-again or not-measured line fails a scenario that expects none)."""
    return [t for t in texts if not any(t.startswith(p) for p in allowed_prefixes)]


def split_status(lines, seq=AUTO_SEQ):
    return rigsplit.parse_op_status(lines, seq, "Split")


def merge_status(lines, seq=AUTO_SEQ):
    return rigmerge.parse_merge_status(lines, seq)


def split_count(lines, seq=AUTO_SEQ):
    """How many distinct split operations have a `requested` line under `seq`."""
    ops = set()
    for text in status_texts(lines, seq):
        m = rigsplit.OP_STATE.match(text)
        if m and m.group(1) == "Split" and m.group(3).startswith("requested "):
            ops.add(m.group(2))
    return len(ops)


def has_merge_line(lines, seq=AUTO_SEQ):
    return any(rigsplit.OP_STATE.match(t) and rigsplit.OP_STATE.match(t).group(1) == "Merge" for t in status_texts(lines, seq))


# ------------------------------------------------------------------ the cells' work windows (decision 18)
def windows(lines):
    """The parsed Work window lines of a cell, in time order."""
    out = [w for line in lines for w in [rigtools.parse_work_window(line)] if w]
    out.sort(key=lambda w: w["end"])
    return out


def util_after(lines, after):
    """The utilisation (work mean over budget) of the first work window stamped at or after `after` (a datetime), or None."""
    for w in windows(lines):
        if w["end"] >= after and w["budget"] > 0:
            return w["work_mean"] / w["budget"]
    return None


def owned_pairs(lines_a, lines_b, after, tolerance_s=5.0):
    """(owned players of a, owned players of b) per window of a stamped at or after `after`, each paired with b's
    latest window within tolerance_s; windows of a with no partner are left out."""
    b_windows = [w for w in windows(lines_b)]
    pairs = []
    for wa in windows(lines_a):
        if wa["end"] < after:
            continue
        partner = None
        for wb in b_windows:
            if abs((wb["end"] - wa["end"]).total_seconds()) <= tolerance_s:
                partner = wb
        if partner is not None:
            pairs.append((wa["owned_players"], partner["owned_players"]))
    return pairs


def consecutive(pairs, total, run=3):
    """Whether `run` consecutive pairs sum to exactly `total`."""
    streak = 0
    for a, b in pairs:
        streak = streak + 1 if a + b == total else 0
        if streak >= run:
            return True
    return False


def local_datetime(wall):
    """A wall time (time.time()) as the naive local datetime the kit's log stamps use."""
    return datetime.datetime.fromtimestamp(wall)
