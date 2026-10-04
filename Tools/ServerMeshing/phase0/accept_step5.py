"""Phase 0 step 5 rig acceptance (virtual phantoms; the sink run on machine B is separate). Coarse C (one 600 m cell),
no clients unless noted. Runs and checks (step5_design.md "Acceptance"):
  chars    200 character phantoms, V 100, meter on: all bound and ready, V_move within 10 % of 100, posErr p95 <= 0.50 m
           in every measured window, Send window lines parse, no exception or "Phantoms error:"/"Send meter error:" line
  eyes     50 eyes, V 200: all bound, eyeSeen 0 in every window (no eye has a subscriber but its owner), V_move within
           10 % of 200
  fight    40 standing character phantoms on a 30 m disc among the cell's monsters, FIGHT 1, against the same without
           FIGHT: fighting 40, and monster kills land (Mesh stats drops above the control's)
  overhead 5 interleaved pairs, 400 eyes at V 100, arm B adds MESH_METER_SENDS=count: the upper end of the 95 % CI of
           C's paired work difference is under 1 % of arm A's work (p0accept.overhead); Send window calls/s reported
"""
import os
import re
import statistics
import sys

TOOLS = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
sys.path.insert(0, TOOLS)
import abload  # noqa: E402 -- the harness folder goes on sys.path first
import p0accept  # noqa: E402
import p0phantom  # noqa: E402
import p0tools  # noqa: E402
import regress as r  # noqa: E402

WARMUP, MEASURE = 45, 60
COARSE = {"MESH_LAYOUT_OVERRIDE": "ServerMeshTestMap=C@-300,-300,600,600", "MESH_SPAWN_CELLS": "C",
          "MESH_RATES": "60/0.03"}
QUIET = dict(COARSE, MESH_NO_MONSTERS="1")
STATS = re.compile(r"Mesh stats: .*?drops=(\d+)")


def window(rows, start, end):
    return [x for x in rows if x and x.get("end") is not None and start <= x["end"] <= end]


def measure(env, rep):
    play_start, start, end = abload.run_window(env, WARMUP, MEASURE, rep)
    c = r.zone_lines("C", play_start.strftime("%H:%M:%S"), play_start.strftime("%Y-%m-%d"))
    return {
        "phantom": window([p0phantom.parse_phantom_window(line) for line in c], start, end),
        "send": window([p0phantom.parse_send_window(line) for line in c], start, end),
        "ticks": window([p0tools.parse_work_ticks(line) for line in c], start, end),
        "drops": [int(m.group(1)) for m in (STATS.search(line) for line in c) if m],
        "bad": [line.strip() for line in c if "Exception" in line or "Phantoms error:" in line
                or "Send meter error:" in line or "Too Big Packet" in line],
        "fighters": [line for line in c if "Phantom fighters" in line and start.strftime("%H:%M:%S") <= line[line.find("[") + 12:line.find("[") + 20] <= end.strftime("%H:%M:%S")],
        "setup": [line.strip() for line in c if "Phantoms: cell" in line or "Phantom area:" in line][:3],
    }


def work(run):
    return statistics.fmean(t["work_ms_per_s"] for t in run["ticks"]) if run["ticks"] else float("nan")


def main():
    rep = abload.Report("Phase 0 step 5 rig acceptance (virtual)")
    failed = 0

    def check(ok, text):
        nonlocal failed
        rep.note(f"  [{'ok' if ok else 'FAIL'}] {text}")
        failed += 0 if ok else 1

    rep.note("--- chars: 200 character phantoms, V 100, meter count")
    run = measure(dict(QUIET, MESH_PHANTOM="virtual:200:character:C", MESH_PHANTOM_VIEW="100",
                       MESH_METER_SENDS="count"), rep)
    ph = run["phantom"]
    rep.note(f"    {run['setup']}; C work {work(run):.1f} ms/s; {len(ph)} Phantom windows, {len(run['send'])} Send windows")
    check(bool(ph) and all(p["bound_virtual"] == 200 and p["ready"] == 200 for p in ph[1:] or ph),
          f"chars: 200 bound and ready ({[(p['bound_virtual'], p['ready']) for p in ph]})")
    vmove = [p["vmove_mean"] for p in ph if p["vmove_n"]]
    check(bool(vmove) and all(90 <= v <= 110 for v in vmove), f"chars: V_move within 10 % of 100 ({vmove})")
    pos = [p["pos_err_p95"] for p in ph if p["pos_err_n"]]
    check(bool(pos) and max(pos) <= 0.50, f"chars: posErr p95 <= 0.50 m in every window ({pos})")
    check(bool(run["send"]), f"chars: Send window lines parse ({len(run['send'])})")
    check(not run["bad"], f"chars: no exception or error lines ({run['bad'][:3]})")

    rep.note("--- eyes: 50 eyes, V 200")
    run = measure(dict(QUIET, MESH_PHANTOM="virtual:50:eye:C", MESH_PHANTOM_VIEW="200"), rep)
    ph = run["phantom"]
    rep.note(f"    {run['setup']}; C work {work(run):.1f} ms/s")
    check(bool(ph) and all(p["bound_virtual"] == 50 for p in ph), f"eyes: 50 bound ({[p['bound_virtual'] for p in ph]})")
    check(bool(ph) and all(p["eye_seen"] == 0 for p in ph), f"eyes: eyeSeen 0 ({[p['eye_seen'] for p in ph]})")
    vmove = [p["vmove_mean"] for p in ph if p["vmove_n"]]
    check(bool(vmove) and all(180 <= v <= 220 for v in vmove), f"eyes: V_move within 10 % of 200 ({vmove})")
    check(not run["bad"], f"eyes: no exception or error lines ({run['bad'][:3]})")

    fight_env = dict(COARSE, MESH_MONSTER_AREAS="all", MESH_PHANTOM="virtual:40:character:C",
                     MESH_PHANTOM_AREA="disc:0,0,40", MESH_PHANTOM_FIGHT="1")
    rep.note("--- fight: 40 walking phantoms on a 40 m disc over the central monster area, FIGHT 1")
    run = measure(fight_env, rep)
    ph = run["phantom"]
    fighting = [p["fighting"] for p in ph]
    lines = run["fighters"]
    validated = sum(int(re.search(r"validated (\d+)", x).group(1)) for x in lines)
    kills = sum(int(re.search(r"kills (\d+)", x).group(1)) for x in lines)
    rep.note(f"    {run['setup']}; fighter windows {len(lines)}, hits validated {validated}, kills {kills}")
    check(bool(ph) and all(f == 40 for f in fighting), f"fight: fighting 40 ({fighting})")
    check(validated > 0 and kills > 0, f"fight: hits validated ({validated}) and monsters killed ({kills})")
    check(not run["bad"], f"fight: no exception or error lines ({run['bad'][:3]})")

    rep.note("--- overhead: 5 interleaved pairs, 400 eyes at V 100")
    base = dict(QUIET, MESH_PHANTOM="virtual:400:eye:C", MESH_PHANTOM_VIEW="100")
    pairs, calls = [], []
    for pair in range(5):
        order = ("A", "B") if pair % 2 == 0 else ("B", "A")
        got = {}
        for arm in order:
            run = measure(dict(base, MESH_METER_SENDS="count") if arm == "B" else base, rep)
            got[arm] = work(run)
            if arm == "B":
                calls += [s["calls_per_s"] for s in run["send"]]
            rep.note(f"    pair {pair + 1} {arm}: C work {got[arm]:.1f} ms/s")
        pairs.append((got["A"], got["B"]))
    gate = 0.01 * statistics.fmean(a for a, _ in pairs)
    verdict = p0accept.overhead(pairs, gate)
    rep.note(f"    meter: {verdict['mean']:.2f} [{verdict['lo']:.2f}, {verdict['hi']:.2f}] ms/s against {gate:.2f} "
             f"(1 % of A); Send window calls/s mean {statistics.fmean(calls) if calls else float('nan'):.0f}")
    check(verdict["hi"] < gate, f"overhead: upper CI {verdict['hi']:.2f} ms/s under 1 % of A ({gate:.2f})")

    rep.note(f"=== {failed} check(s) failed")
    out = os.path.join(TOOLS, "..", "..", "Logs", "p0", "accept_step5.txt")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8") as f:
        f.write(rep.text() + "\n")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
