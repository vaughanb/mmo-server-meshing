"""Capacity test for one cell (DESIGN.md Q4).

For each step N the rig starts as usual (editor in Play with the tester standing on C, five zones), cell C spawns N
synthetic players (server-owned player entities with a wander/fight brain — ServerMeshLoadGenerator, driven by
MESH_LOAD_PLAYERS / MESH_LOAD_CELL) and a few real bots pace on C for the client-facing cost. After a warm-up the
Mesh stats lines of C (loaded) and NE (idle) are averaged over a measurement window:

    frame mean/max ms   the server's frame time (the demo targets 120 fps = 8.3 ms; a mean above that is saturation)
    cpu %               of one core, the whole process
    ws MB               working set
    clientOut kB/s      the game transport's send rate to the real clients (per client = / clients)
    ghosts / link       what the mesh adds: ghosts held, link traffic

Usage: python Tools/ServerMeshing/loadtest.py [--steps 0,50,100,200,300,400,500] [--bots 3] [--warmup 45] [--measure 60]
"""
import argparse, os, re, sys, time
sys.path.insert(0, os.path.dirname(__file__))
import regress as r

COST = re.compile(r"frame mean ([\d.]+) ms max ([\d.]+) ms cpu (\d+)% mem (\d+) MB clients=(\d+) clientOut=([\d.]+) kB/s \((\d+) pkt/s\)")
PLAYERS = re.compile(r"players=(\d+) owned=(\d+)")
GHOSTS = re.compile(r"ghosts=(\d+)p/(\d+)m")


def stats_rows(zone, map_id, start, end, day):
    rows = []
    for line in r.zone_lines(zone, start, day, map_id):
        if "Mesh stats" not in line:
            continue
        m = r.STAMP.search(line[:60])
        if not m or m.group(2) > end:
            continue
        c = COST.search(line)
        if not c:
            continue
        p = PLAYERS.search(line)
        g = GHOSTS.search(line)
        rows.append(dict(frame=float(c.group(1)), fmax=float(c.group(2)), cpu=int(c.group(3)), ws=int(c.group(4)), clients=int(c.group(5)),
                         cout=float(c.group(6)), pkt=int(c.group(7)), players=int(p.group(1)) if p else -1, owned=int(p.group(2)) if p else -1,
                         ghosts=(int(g.group(1)) + int(g.group(2))) if g else -1))
    return rows


def mean(rows, key):
    return sum(x[key] for x in rows) / len(rows) if rows else float("nan")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--steps", default="0,50,100,200,300,400,500")
    ap.add_argument("--bots", type=int, default=3)
    ap.add_argument("--warmup", type=int, default=45)
    ap.add_argument("--measure", type=int, default=60)
    ap.add_argument("--cell", default="C")
    ap.add_argument("--capped", action="store_true", help="keep the loaded cell at its target frame rate (default: uncapped, so frame time = work per frame)")
    args = ap.parse_args()
    steps = [int(x) for x in args.steps.split(",") if x.strip()]
    if not os.path.exists(r.EXE):
        print(f"no player build at {r.EXE}"); return 2
    if r.read_status().get("isPlaying") == "True":
        print("the editor is already in Play mode; stop it first"); return 2
    out = os.path.join(r.LOGS, f"loadtest_{time.strftime('%Y%m%d_%H%M%S')}.txt")
    results = []
    for n in steps:
        r.kill_bots()
        day = time.strftime("%Y-%m-%d")
        start = time.strftime("%H:%M:%S")
        print(f"=== {n} synthetic players on {args.cell} (+{args.bots} bots)")
        r.db_reset((20, 0), (30, 0))
        r.request_play("stand", 20, 0, env={"MESH_LOAD_PLAYERS": str(n) if n > 0 else "", "MESH_LOAD_CELL": args.cell, "MESH_LOAD_UNCAPPED": "" if args.capped else "1"})
        try:
            r.wait_status(lambda st: st.get("isPlaying") == "True", 60, "Play")
            time.sleep(20)
            for i in range(args.bots):
                r.launch_bot(f"load{n}", f"meshbot{i + 1}" if i > 0 else "meshbot", ["-meshAutoMode", "pace", "-meshWalkX", str(30 + 10 * i)])
            time.sleep(args.warmup)
            t_measure = time.strftime("%H:%M:%S")
            time.sleep(args.measure)
            t_end = time.strftime("%H:%M:%S")
            loaded = stats_rows(args.cell, r.MAP, t_measure, t_end, day)
            idle = stats_rows("NE", r.MAP, t_measure, t_end, day)
            spawned = r.count(r.zone_lines(args.cell, start, day), r"Load generator: spawned (\d+)")
            spawn_line = [l for l in r.zone_lines(args.cell, start, day) if "Load generator: spawned" in l]
            spawned_n = int(re.search(r"spawned (\d+)", spawn_line[0]).group(1)) if spawn_line else 0
            exceptions = sum(1 for l in r.zone_lines(args.cell, start, day) if "Exception" in l)
            row = dict(n=n, spawned=spawned_n, windows=len(loaded), frame=mean(loaded, "frame"), fmax=max((x["fmax"] for x in loaded), default=float("nan")),
                       cpu=mean(loaded, "cpu"), ws=mean(loaded, "ws"), clients=mean(loaded, "clients"), cout=mean(loaded, "cout"), pkt=mean(loaded, "pkt"),
                       owned=mean(loaded, "owned"), ghosts=mean(loaded, "ghosts"), idle_cpu=mean(idle, "cpu"), idle_frame=mean(idle, "frame"), idle_ws=mean(idle, "ws"), exceptions=exceptions)
            results.append(row)
            print(f"  spawned={spawned_n} windows={len(loaded)} frame mean {row['frame']:.1f} ms max {row['fmax']:.1f} ms cpu {row['cpu']:.0f}% ws {row['ws']:.0f} MB "
                  f"clients {row['clients']:.0f} clientOut {row['cout']:.1f} kB/s owned {row['owned']:.0f} ghosts {row['ghosts']:.0f} | idle NE cpu {row['idle_cpu']:.0f}% frame {row['idle_frame']:.1f} ms | exceptions {exceptions}")
        finally:
            r.stop_play()
            r.kill_bots()
        time.sleep(8)
    header = f"{'N':>4} {'spawned':>7} {'frame ms':>9} {'max ms':>7} {'cpu%':>5} {'mem MB':>6} {'clients':>7} {'kB/s':>6} {'per client':>10} {'ghosts':>6} {'idle cpu%':>9} {'idle ms':>7} {'exc':>4}"
    lines = [header]
    for x in results:
        per_client = x["cout"] / x["clients"] if x["clients"] else float("nan")
        lines.append(f"{x['n']:>4} {x['spawned']:>7} {x['frame']:>9.1f} {x['fmax']:>7.1f} {x['cpu']:>5.0f} {x['ws']:>6.0f} {x['clients']:>7.0f} {x['cout']:>6.1f} {per_client:>10.2f} {x['ghosts']:>6.0f} {x['idle_cpu']:>9.0f} {x['idle_frame']:>7.1f} {x['exceptions']:>4}")
    print("\n=== capacity table (cell C loaded, NE idle)")
    print("\n".join(lines))
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(f"written to {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
