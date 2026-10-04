"""Phase 0 step 8a rig acceptance, item 5 of step8a_design.md "Acceptance (8a)": one smoke run per new subcommand,
taken from the real plan builders (p0plans0b) and graded by the block's own checks (p0blocks.run_rows: p0checks), plus
the park sleep pair read from the cell's Ships windows. The rows go to Logs/p0/accept8a/ (never Logs/p0/*.jsonl, so a
smoke run never counts as a planned run for --resume). Writes Logs/p0/accept_step8a.txt.

    python Tools/ServerMeshing/phase0/accept_step8a.py            # every smoke, about 25 Play sessions
    python Tools/ServerMeshing/phase0/accept_step8a.py prewarm park
Smokes: observed (O kinematic eyes 50; M owned 200 monsters), fanout (char-kit V 100, steps 50 -> 100 at 40 s), targets
(F2, S-T), syncrpc (D 250, R 2,100), packets (saturation, steps 100 -> 200 at 40 s), prewarm (kinematic, ship, range,
group), park (200 parked ships, MESH_SHIP_PARK_SLEEP 0 then 1: parked sleeping > 0 then 0).
"""
import datetime
import json
import os
import sys

TOOLS = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
sys.path.insert(0, TOOLS)
import p0blocks  # noqa: E402 -- the harness folder goes on sys.path first
import p0plans0b  # noqa: E402
import p0ships  # noqa: E402
import regress as r  # noqa: E402
import rigrun  # noqa: E402

OUT = os.path.join(TOOLS, "..", "..", "Logs", "p0")
SMOKE_HOLD = 40
GROUPS = ("observed", "fanout", "targets", "syncrpc", "packets", "prewarm", "park")


def pick(specs, case, arm, **point):
    """The first repeat-1 spec of a case and arm whose point holds every given value."""
    for spec in specs:
        if spec["case"] == case and spec["arm"] == arm and spec["repeat"] == 1 \
                and all(spec["point"].get(k) == v for k, v in point.items()):
            return spec
    raise LookupError(f"no planned run {case}/{arm} {point}")


def shortened(spec, steps):
    """A stepped spec cut to the given steps at SMOKE_HOLD s each (the plan's own measure formula)."""
    spec = json.loads(json.dumps(spec))
    spec["env"]["MESH_PHANTOM_STEPS"] = f"{','.join(str(p) for p in steps)}@{SMOKE_HOLD}"
    spec["stepped"] = {"steps": list(steps), "hold": SMOKE_HOLD}
    spec["measure"] = p0plans0b.stepped_measure(list(steps), SMOKE_HOLD)
    return spec


def smokes(groups):
    """[(label, spec)] for the chosen groups."""
    out = []
    if "observed" in groups:
        out.append(("observed O kinematic eyes 50", pick(p0plans0b.observed_plan("O"), "O", "puppets-kinematic",
                                                         eyes=50)))
        out.append(("observed M owned", pick(p0plans0b.observed_plan("M"), "M", "owned")))
    if "fanout" in groups:
        out.append(("fanout char-kit V 100 steps 50,100", shortened(pick(p0plans0b.fanout_plan(), "main", "char-kit",
                                                                         V=100), [50, 100])))
    if "targets" in groups:
        out.append(("targets F2 30 fps", pick(p0plans0b.targets_plan(), "F2", "kit", rates="30/0.02")))
        out.append(("targets S-T", pick(p0plans0b.targets_plan(), "S-T", "kit")))
    if "syncrpc" in groups:
        out.append(("syncrpc D 250 R 2100", pick(p0plans0b.syncrpc_plan(), "S-T", "kit", dirty=250, rpc=2100)))
    if "packets" in groups:
        out.append(("packets saturation steps 100,200", shortened(pick(p0plans0b.packets_plan(), "saturation",
                                                                        "kit-character-eyes"), [100, 200])))
    if "prewarm" in groups:
        plan = p0plans0b.prewarm_plan(45)
        for case, arm in (("burst", "kinematic"), ("burst", "ship"), ("range", "eyes"), ("group", "eyes")):
            point = {"N": 160} if case == "range" else {"N": 100}
            out.append((f"prewarm {case} {arm}", pick(plan, case, arm, **point)))
    if "park" in groups:
        for value in (0, 1):
            out.append((f"park sleep {value}", pick(p0plans0b.observed_plan("S"), "S", "parked", park_sleep=value)))
    return out


def parked_sleeping(row):
    """[(sleeping, total)] of cell C's Ships windows inside the row's window ([] for a run without one)."""
    if not row.get("window_local"):
        return []
    start, end = (datetime.datetime.fromisoformat(row["window_local"][k]) for k in ("start", "end"))
    out = []
    for line in r.zone_lines("C", start.strftime("%H:%M:%S"), start.strftime("%Y-%m-%d")):
        w = p0ships.parse_ships_window(line)
        if w and w.get("end") and start <= w["end"] <= end and w.get("parked_total") is not None:
            out.append((w["parked_sleeping"], w["parked_total"]))
    return out


def main(argv):
    groups = argv or list(GROUPS)
    unknown = [g for g in groups if g not in GROUPS]
    if unknown:
        raise SystemExit(f"unknown group(s) {', '.join(unknown)}; known: {', '.join(GROUPS)}")
    stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    rep = rigrun.Report(f"Phase 0 step 8a smoke acceptance {stamp} on {rigrun.host_context().get('host')}")
    problems = rigrun.start_problems()
    if problems:
        rep.note(f"refused: {problems}")
        return 2
    context = rigrun.host_context()
    os.makedirs(os.path.join(OUT, "accept8a"), exist_ok=True)
    rows_path = os.path.join(OUT, "accept8a", f"accept8a_{stamp}.jsonl")
    failed, park = 0, {}
    for label, spec in smokes(groups):
        rep.note(f"--- {label}: {spec['family']} {spec['case']}/{spec['arm']} {spec['point']}")
        pairs = p0blocks.run_rows(spec, rep, context)
        with open(rows_path, "a", encoding="utf-8") as f:
            for row, _ in pairs:
                f.write(json.dumps(row, sort_keys=True) + "\n")
        for row, _ in pairs:
            ok = row["accepted"]
            failed += not ok
            step = f" step {row['point'].get('step')}" if "step" in row["point"] else ""
            rep.note(f"  [{'ok' if ok else 'FAIL'}] {label}{step}: {'accepted' if ok else row['problems']}")
        if label.startswith("park sleep"):
            park[label[-1]] = parked_sleeping(pairs[0][0])
            rep.note(f"    parked sleeping per window {park[label[-1]]}")
    if set(park) == {"0", "1"}:
        zero, one = park["0"], park["1"]
        ok = bool(zero) and bool(one) and max(s for s, _ in zero) > 0 and all(s == 0 for s, _ in one)
        failed += not ok
        rep.note(f"  [{'ok' if ok else 'FAIL'}] park: sleeping > 0 with the key 0 and 0 in every window with the key 1")
    verdict = "PASS" if failed == 0 else f"FAIL ({failed})"
    rep.note(f"step 8a smoke acceptance: {verdict}; rows in {rows_path}")
    with open(os.path.join(OUT, "accept_step8a.txt"), "w", encoding="utf-8") as f:
        f.write(rep.text() + "\n")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
