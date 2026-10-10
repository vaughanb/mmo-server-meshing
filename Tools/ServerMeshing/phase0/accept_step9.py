"""Phase 0 step 9 rig acceptance, item 3 of step9_design.md "Acceptance (step 9)": one smoke run per new subcommand,
taken from the real plan builders (p0plans9) and graded by the block's own checks (p0blocks.run_rows: p0checks). The
rows go to Logs/p0/accept9/ (never Logs/p0/*.jsonl). dbburst provisions the 800 burst rows out of Play (its
before_play); loss needs B (deployed; clumsy needs admin there). Writes Logs/p0/accept_step9.txt.

    python Tools/ServerMeshing/phase0/accept_step9.py               # every smoke, 6 Play sessions
    python Tools/ServerMeshing/phase0/accept_step9.py dbburst spaces
Smokes: dbburst (a 50 burst and a 5/s sustained run), workerab (the coarse probe at 200 and one halves A/B run at
200), spaces (stack, S 4, 100 players), loss (loss5 with B).
"""
import datetime
import json
import os
import sys

TOOLS = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
sys.path.insert(0, TOOLS)
import p0blocks  # noqa: E402 -- the harness folder goes on sys.path first
import p0peer  # noqa: E402
import p0plans9  # noqa: E402
import rigrun  # noqa: E402

OUT = os.path.join(TOOLS, "..", "..", "Logs", "p0")
GROUPS = ("dbburst", "workerab", "spaces", "loss")


def pick(specs, case, arm, **point):
    """The first repeat-1 spec of a case and arm whose point holds every given value."""
    for spec in specs:
        if spec["case"] == case and spec["arm"] == arm and spec["repeat"] == 1 \
                and all(spec["point"].get(k) == v for k, v in point.items()):
            return spec
    raise LookupError(f"no planned run {case}/{arm} {point}")


def smokes(groups, calibration):
    """[(label, spec)] for the chosen groups (calibration unused)."""
    out = []
    if "dbburst" in groups:
        plan = p0plans9.dbburst_plan()
        out += [("dbburst 50 at once", pick(plan, "burst", "at-once", N=50)),
                ("dbburst 5/s for 40 s", pick(plan, "sustained", "rate", R=5))]
    if "workerab" in groups:
        plan = p0plans9.workerab_plan()
        out += [("workerab coarse probe at 200", pick(plan, "probe", "coarse", players=200)),
                ("workerab halves at 200", pick(plan, "ab", "halves", players=200))]
    if "spaces" in groups:
        out.append(("spaces stack S 4, 100 players", pick(p0plans9.spaces_plan(), "stack", "100", S=4)))
    if "loss" in groups:
        out.append(("loss 5 %", pick(p0plans9.loss_plan(), "loss", "loss5")))
    return out


def figures(row):
    """A few of the row's step 9 metrics for the report line (name stat value), or ''."""
    keep = ("latency_ms{db_ready}", "db_", "drain", "pending", "forwarded", "awake", "spaces", "seen", "resent",
            "net_", "work_ms_per_s", "datagrams_per_s", "sink_in_kBps")
    out = [f"{m['name']} {m['stat']} {m['value']:.1f} ({m.get('cell')})" for m in row.get("metrics") or []
           if any(k in m["name"] for k in keep) and isinstance(m.get("value"), (int, float))]
    extra = [f"{k} {row[k]}" for k in ("awake_monsters",) if row.get(k) is not None]
    return "; ".join(out[:20] + extra)


def main(argv):
    groups = argv or list(GROUPS)
    unknown = [g for g in groups if g not in GROUPS]
    if unknown:
        raise SystemExit(f"unknown group(s) {', '.join(unknown)}; known: {', '.join(GROUPS)}")
    stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    rep = rigrun.Report(f"Phase 0 step 9 smoke acceptance {stamp}")
    needs_b = "loss" in groups
    problems = rigrun.start_problems() + (p0peer.start_problems() if needs_b else [])
    if problems:
        rep.note(f"refused: {problems}")
        return 2
    context = rigrun.host_context(peer=needs_b)
    calibration = None
    os.makedirs(os.path.join(OUT, "accept9"), exist_ok=True)
    rows_path = os.path.join(OUT, "accept9", f"accept9_{stamp}.jsonl")
    failed, stopped = 0, None
    try:
        for label, spec in smokes(groups, calibration):
            rep.note(f"--- {label}: {spec['family']} {spec['case']}/{spec['arm']} {spec['point']}")
            pairs = p0blocks.run_rows(spec, rep, context)
            for row, _ in pairs:
                stopped = row.pop(p0peer.PEER_STOP, None) or stopped
            with open(rows_path, "a", encoding="utf-8") as f:
                for row, _ in pairs:
                    f.write(json.dumps(row, sort_keys=True, default=str) + "\n")
            for row, _ in pairs:
                ok = row["accepted"]
                failed += not ok
                rep.note(f"  [{'ok' if ok else 'FAIL'}] {label}: "
                         f"{'accepted' if ok else row['problems'] + [row.get('excluded_reason')]}")
                rep.note(f"    {figures(row)}")
            if stopped:
                rep.note(f"stopped: {stopped}")
                break
    except p0peer.Stopped as stop:
        rep.note(f"stopped: {stop.reason}")
        failed += 1
    verdict = "PASS" if failed == 0 and not stopped else f"FAIL ({failed})"
    rep.note(f"step 9 smoke acceptance: {verdict}; rows in {rows_path}")
    with open(os.path.join(OUT, "accept_step9.txt"), "w", encoding="utf-8") as f:
        f.write(rep.text() + "\n")
    return 0 if verdict == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
