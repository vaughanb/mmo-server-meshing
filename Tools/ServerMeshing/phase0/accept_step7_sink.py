"""Step 7 b1 sink arm across A and B (step7_design.md acceptance run 2, attended part; step7b_design.md: on the new
build; run from A with B up: python Tools/ServerMeshing/phase0/accept_step7_sink.py). Cell C on A (coarse, space rates, scale 1) with 20 sink ship phantoms, header56, meter count and record sizes;
the sink runs on B with -meshPhantomSinkSizes 1. Pass: every full Sink window's sizes read min 56 max 56, no error
lines. Writes Logs/p0/p0sink7_<stamp>.txt. Pattern: rigrun.run_window on a thread, sink work on the main thread."""
import datetime
import os
import subprocess
import sys
import threading
import time

TOOLS = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
sys.path.insert(0, TOOLS)
import p0phantom  # noqa: E402
import p0stage0b  # noqa: E402
import regress as r  # noqa: E402
import rigrun  # noqa: E402

PEERS = 20
WARMUP, MEASURE = 30, 300
ENV = {"MESH_LAYOUT_OVERRIDE": "ServerMeshTestMap=C@-300,-300,600,600", "MESH_SPAWN_CELLS": "C", "MESH_NO_MONSTERS": "1",
       "MESH_RATES": "30/0.02", "MESH_PHANTOM": f"sink:{PEERS}:ship:C", "MESH_SHIP_WIRE": "header56",
       "MESH_METER_SENDS": "count", "MESH_METER_RECORD_SIZES": "1"}
SSH = ["ssh", "-i", os.path.expanduser("~/.ssh/meshtest_ed25519"), "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
       "brend@192.168.50.211"]
BAD = ("Exception", "error:", "Too Big Packet")


def remoteb(*args):
    out = subprocess.run([sys.executable, os.path.join(TOOLS, "remoteb.py"), *args], capture_output=True, text=True)
    return (out.stdout + out.stderr).strip()


def main():
    stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    rep = rigrun.Report(f"Step 7 b1 sink arm (A cell C, B sink {PEERS} ship peers, header56) {stamp}")
    problems = rigrun.check_env(ENV)
    if problems:
        rep.note(f"refused: {problems}")
        return 2
    result = {}

    def play():
        try:
            result["window"] = rigrun.run_window(ENV, WARMUP, MEASURE, rep)
        except Exception as ex:  # reported after the join; Play is stopped by run_window
            result["error"] = ex

    t = threading.Thread(target=play)
    t.start()
    try:
        since = datetime.datetime.now()
        port = None
        while port is None and t.is_alive() and (datetime.datetime.now() - since).total_seconds() < 180:
            for line in r.zone_lines("C", since.strftime("%H:%M:%S"), since.strftime("%Y-%m-%d")):
                setup = p0stage0b.parse_phantoms_port(line)
                if setup and setup["cell"] == "C":
                    port = setup["port"]
            time.sleep(2)
        if port is None:
            rep.note("FAIL: cell C never logged its sink port")
            return 1
        rep.note(f"cell C sink port {port}; starting the sink on B")
        rep.note(remoteb("sink-start", "--sink-port", str(port), "--sink-count", str(PEERS), "--sink-sizes"))
        time.sleep(WARMUP + MEASURE - 40)
        log = subprocess.run(SSH + [r"type C:\MeshTest\Server\Logs\sink.log"], capture_output=True, text=True,
                             encoding="utf-8", errors="replace").stdout
    finally:
        rep.note(remoteb("sink-stop"))
        t.join()
    if "error" in result:
        rep.note(f"FAIL: the Play run raised: {result['error']}")
    windows = [w for w in (p0phantom.parse_sink_window(x) for x in log.splitlines()) if w]
    full = [w for w in windows if w["bound"] == PEERS and w["sizes"]]
    bad = [x.strip() for x in log.splitlines() if any(b in x for b in BAD)]
    cell = r.zone_lines("C", since.strftime("%H:%M:%S"), since.strftime("%Y-%m-%d"))
    cell_bad = [x.strip() for x in cell if any(b in x for b in BAD)]
    sizes = [(w["sizes"]["min"], w["sizes"]["max"], w["sizes"]["n"]) for w in full]
    ok_sizes = bool(full) and all(mn == 56 and mx == 56 for mn, mx, _ in sizes)
    rep.note(f"Sink windows {len(windows)}, with all {PEERS} bound and sizes {len(full)}; sizes (min, max, n) {sizes}")
    rep.note(f"[{'ok' if ok_sizes else 'FAIL'}] Sink sizes min 56 max 56 in every full window")
    rep.note(f"[{'ok' if not bad else 'FAIL'}] sink log free of {BAD} ({bad[:3]})")
    rep.note(f"[{'ok' if not cell_bad else 'FAIL'}] cell C free of {BAD} ({cell_bad[:3]})")
    passed = ok_sizes and not bad and not cell_bad and "error" not in result
    rep.note(f"b1 sink arm: {'PASS' if passed else 'FAIL'}")
    with open(os.path.join(TOOLS, "..", "..", "Logs", "p0", f"p0sink7_{stamp}.txt"), "w",
              encoding="utf-8") as f:
        f.write(rep.text() + "\n")
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
