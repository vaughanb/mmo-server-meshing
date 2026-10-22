"""Pure readers of the automatic policy past one hot cell (regress_policy.py, DESIGN 0.88; slices/slice3_design.md
decisions 6, 8 and 13): two cells hot at once, where the second waits for the one op slot, and a split deferred while
its host has no headroom. They read the `auto` lines of the status file as rigauto does, per operation (two splits
run under the one pseudo-seq), and compose the burn file's comma list (MESH_PROBE_BURN_MS=@<path>, DESIGN 0.88). No
rig access (python -m unittest Tools/ServerMeshing/test_rigpolicy.py).
"""
import rigauto
import rigsplit


def burn_text(burns):
    """The burn file's text for {cell: ms} ('C:14,E:7'; cells in the order given, a 0 ms cell left out)."""
    return ",".join(f"{cell}:{ms:g}" for cell, ms in burns.items() if ms > 0)


def split_ops(lines, seq=rigauto.AUTO_SEQ):
    """The split op ids under `seq` in the order their first line came."""
    out = []
    for text in rigauto.status_texts(lines, seq):
        m = rigsplit.OP_STATE.match(text)
        if m and m.group(1) == "Split" and m.group(2) not in out:
            out.append(m.group(2))
    return out


def op_status(lines, op, seq=rigauto.AUTO_SEQ):
    """rigsplit.parse_op_status of one split op among several under `seq` (only that op's lines, no refusal lines)."""
    mine = []
    for raw in lines:
        parts = raw.strip().split(None, 1)
        if len(parts) == 2 and parts[0] == str(seq) and parts[1].startswith(f"Split {op}: "):
            mine.append(raw)
    return rigsplit.parse_op_status(mine, seq)


def planned(lines, seq=rigauto.AUTO_SEQ):
    """[rigauto.parse_planned] of every planned-split line under `seq`, in order."""
    return [p for p in (rigauto.parse_planned(t) for t in rigauto.policy_texts(lines, seq)) if p is not None]


def deferrals(lines, seq=rigauto.AUTO_SEQ):
    """[{cell, host, projected, cores}] of every deferred-split line under `seq`, in order."""
    out = []
    for text in rigauto.policy_texts(lines, seq):
        m = rigauto.DEFERRED.match(text)
        if m:
            out.append({"cell": m.group(1), "host": m.group(2), "projected": float(m.group(3)), "cores": int(m.group(4))})
    return out


def measured(lines, seq=rigauto.AUTO_SEQ):
    """{op: rigauto.parse_split_measured} of every split measured line under `seq`."""
    return {m["op"]: m for m in (rigauto.parse_split_measured(t) for t in rigauto.policy_texts(lines, seq)) if m}


def index_of(lines, pred, seq=rigauto.AUTO_SEQ):
    """The index (among the texts under `seq`) of the first text pred(text) accepts, or None."""
    return next((i for i, t in enumerate(rigauto.status_texts(lines, seq)) if pred(t)), None)


def is_done(op):
    return lambda text: text == f"Split {op}: done"


def is_planned(cell):
    return lambda text: text.startswith(f"Policy: split of {cell} planned:")


def split_of(map_id, plan, base_rect):
    """The rigsplit.Split a planned line describes (its axis, cut and side parsed, not assumed)."""
    return rigsplit.Split(map_id, plan["cell"], base_rect, plan["axis"], plan["at"], plan["side"], plan["child"])


def headroom_cores(cold, hot, share=0.8):
    """The smallest whole MESH_HOST_CORES whose threshold (share x cores) lies above `cold` projected cores, preferring
    the one nearest the middle of [cold, hot]; None when no whole number lands strictly between them."""
    middle = (cold + hot) / 2.0
    best = None
    for cores in range(1, 1025):
        threshold = share * cores
        if cold < threshold < hot and (best is None or abs(threshold - middle) < abs(share * best - middle)):
            best = cores
    return best
