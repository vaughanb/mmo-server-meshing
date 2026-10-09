"""Phase 0 step 8b rig acceptance, item 3 of step8b_design.md "Acceptance (8b)": one smoke run per new subcommand,
taken from the real plan builders (p0plans8b) and graded by the block's own checks (p0blocks.run_rows: p0checks), with
machine B's bots and sink started and stopped by p0peer. The rows go to Logs/p0/accept8b/ (never Logs/p0/*.jsonl, so
a smoke run never counts as a planned run for --resume). Needs B (deployed, registered bots, bots_calibration.json).
Writes Logs/p0/accept_step8b.txt.

    python Tools/ServerMeshing/phase0/accept_step8b.py              # every smoke, about 9 Play sessions
    python Tools/ServerMeshing/phase0/accept_step8b.py crossings probe
Smokes: acceptb (part A virtual and sink, part B a bot in the eye group), calibrate (the kit arm's bot and phantom
runs), cap (ship-sender-28 on the sink, packet cap 1,020 B), probe (eyes on the sink, P 100), crossings (20 bots walk
across the halves' cut, neighbour awake).
"""
import datetime
import json
import os
import sys

TOOLS = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
sys.path.insert(0, TOOLS)
import p0blocks  # noqa: E402 -- the harness folder goes on sys.path first
import p0peer  # noqa: E402
import p0plans8b  # noqa: E402
import rigrun  # noqa: E402

OUT = os.path.join(TOOLS, "..", "..", "Logs", "p0")
GROUPS = ("acceptb", "calibrate", "cap", "probe", "crossings")


def pick(specs, case, arm, **point):
    """The first repeat-1 spec of a case and arm whose point holds every given value."""
    for spec in specs:
        if spec["case"] == case and spec["arm"] == arm and spec["repeat"] == 1 \
                and all(spec["point"].get(k) == v for k, v in point.items()):
            return spec
    raise LookupError(f"no planned run {case}/{arm} {point}")


def smokes(groups, calibration):
    """[(label, spec)] for the chosen groups."""
    out = []
    if "acceptb" in groups:
        plan = p0plans8b.accept_b_plan()
        out += [("accept-b A virtual", pick(plan, "A", "virtual")), ("accept-b A sink", pick(plan, "A", "sink")),
                ("accept-b B bot in the eye group", pick(plan, "B", "eyes-bot"))]
    if "calibrate" in groups:
        plan = p0plans8b.calibrate_plan(calibration)
        out += [("calibrate kit, 50 bots", pick(plan, "J-lite", "kit", load="bots")),
                ("calibrate kit, 50 phantoms", pick(plan, "J-lite", "kit", load="phantoms"))]
    if "cap" in groups:
        out.append(("packets sink cap 1020 B", pick(p0plans8b.packets_sink_plan(), "cap-sink", "ship-sender-28-tiered",
                                                    packet_bytes=1020)))
    if "probe" in groups:
        out.append(("packets probe P 100", pick(p0plans8b.packets_sink_plan(), "probe", "char-sender-tiered", P=100)))
    if "crossings" in groups:
        out.append(("crossings 20 bots walk, awake", pick(p0plans8b.crossings_plan(), "walk", "awake", bots=20)))
    return out


def figures(row):
    """A few of the row's step 8b metrics for the report line (name stat value), or ''."""
    keep = ("latency_ms{handoff_ready}", "handoff_outcomes", "bots_in_map", "bot_upload_pkt_per_s", "sink_in_kBps",
            "bot_players_seen", "work_ms_per_s", "thread_ms_per_s{litenetlib}", "datagrams_per_s")
    out = [f"{m['name']} {m['stat']} {m['value']:.1f} ({m.get('cell')})" for m in row.get("metrics") or []
           if m["name"].split("{")[0] in {k.split("{")[0] for k in keep} and m["stat"] in ("mean", "p95", "n", "total",
                                                                                         "ack", "fail", "rollback")
           and isinstance(m.get("value"), (int, float))]
    return "; ".join(out[:12])


def main(argv):
    groups = argv or list(GROUPS)
    unknown = [g for g in groups if g not in GROUPS]
    if unknown:
        raise SystemExit(f"unknown group(s) {', '.join(unknown)}; known: {', '.join(GROUPS)}")
    stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    rep = rigrun.Report(f"Phase 0 step 8b smoke acceptance {stamp}")
    problems = rigrun.start_problems() + p0peer.start_problems()
    if problems:
        rep.note(f"refused: {problems}")
        return 2
    context = rigrun.host_context(peer=True)
    calibration = p0peer.read_calibration(context["build"], context.get("peer_host"))
    os.makedirs(os.path.join(OUT, "accept8b"), exist_ok=True)
    rows_path = os.path.join(OUT, "accept8b", f"accept8b_{stamp}.jsonl")
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
    rep.note(f"step 8b smoke acceptance: {verdict}; rows in {rows_path}")
    with open(os.path.join(OUT, "accept_step8b.txt"), "w", encoding="utf-8") as f:
        f.write(rep.text() + "\n")
    return 0 if verdict == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
