"""How many players does ONE MACHINE hold across the whole mesh?

loadtest.py answers a different question: it loads one cell and leaves the others idle, which measures a cell. The
launch topology is every cell on one box, so the number that matters is the aggregate -- and idle cells do not
compete for CPU, memory or the link the way busy ones do.

Each step loads EVERY cell with N synthetic players (MESH_LOAD_CELL=all), warms up, then averages each cell's own
Mesh stats over a measurement window and adds the machine's totals beside them:

    frame mean ms       per cell. CAPPED (the default) a cell reads 16.7 ms whenever it is keeping up, so the
                        number only says saturated or not; --uncapped removes the cap so frame time IS the work per
                        frame, which is what a threshold can be interpolated from.
    cpu %               per cell, of ONE core. Summed across cells this is cores in use.
    ws MB               per cell working set; summed, what the box needs.
    owned               what the cell actually holds. NOT the stats line's "players=", which counts CLIENT-
                        connected players and is 0 here: synthetic players have no client. Reading the wrong one
                        reported "held 0" for a run that was plainly loaded (2026-09-17).

Usage: python Tools/ServerMeshing/aggload.py [--steps 50,100,150,200,250] [--warmup 45] [--measure 60]
"""
import argparse, os, re, subprocess, sys, time
sys.path.insert(0, os.path.dirname(__file__))
import regress as r

COST = re.compile(r"frame mean ([\d.]+) ms max ([\d.]+) ms cpu (\d+)% mem (\d+) MB")
PLAYERS = re.compile(r"players=(\d+) owned=(\d+)")
LINK = re.compile(r"link out ([\d.]+) kB/s")


def cell_rows(zone, map_id, start, end, day):
    """(frame mean, frame max, cpu, mem, client players, owned entities, link out) per Mesh stats line."""
    rows = []
    for line in r.zone_lines(zone, start, day, map_id):
        if "Mesh stats" not in line:
            continue
        m = r.STAMP.search(line[:60])
        if not m or not (start <= m.group(2) <= end):
            continue
        cost, players, link = COST.search(line), PLAYERS.search(line), LINK.search(line)
        if cost and players:
            rows.append((float(cost.group(1)), float(cost.group(2)), int(cost.group(3)), int(cost.group(4)),
                         int(players.group(1)), int(players.group(2)), float(link.group(1)) if link else 0.0))
    return rows


def mean(xs):
    return sum(xs) / len(xs) if xs else 0.0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--steps", default="50,100,150,200,250", help="synthetic players PER CELL at each step")
    ap.add_argument("--warmup", type=int, default=45)
    ap.add_argument("--measure", type=int, default=60)
    ap.add_argument("--repeats", type=int, default=1, help="runs per step; the spread across them is the only "
                                                          "honest guide to whether two steps really differ")
    ap.add_argument("--uncapped", action="store_true",
                    help="uncap the cells' frame rate so frame time measures WORK, not the 60 fps cap")
    args = ap.parse_args()
    steps = [int(x) for x in args.steps.split(",") if x.strip()]

    if not os.path.exists(r.EXE):
        print(f"no player build at {r.EXE}"); return 2
    if r.read_status().get("isPlaying") == "True":
        print("the editor is already in Play mode; stop it first"); return 2

    out_lines = [f"=== aggregate load, every cell on this machine ({time.strftime('%Y-%m-%d %H:%M')})", ""]
    header = (f"{'per cell':<9} {'total':<7} {'owned':<6} {'frame mean':<11} {'spread':<8} "
              f"{'cores':<7} {'mem MB':<9} {'link kB/s':<10} runs")
    out_lines += [header, "-" * len(header)]

    for n in steps:
        per_run = []
        for attempt in range(1, args.repeats + 1):
            print(f"\n########## {n} players per cell, run {attempt}/{args.repeats}", flush=True)
            r.kill_bots()
            r.db_reset((86.5, 0), (0, 0))
            env = {"MESH_LOAD_PLAYERS": str(n), "MESH_LOAD_CELL": "all"}
            if args.uncapped:
                env["MESH_LOAD_UNCAPPED"] = "1"
            r.request_play("none", 0, 0, env=env)
            r.wait_status(lambda st: st.get("isPlaying") == "True", 90, "Play")
            day = time.strftime("%Y-%m-%d")
            time.sleep(args.warmup)
            start_t = time.strftime("%H:%M:%S")
            time.sleep(args.measure)
            end_t = time.strftime("%H:%M:%S")

            held, fmean, fmax, cpu, mem, link = 0, [], [], 0, 0, 0.0
            for zone, map_id in r.ZONES:
                rows = cell_rows(zone, map_id, start_t, end_t, day)
                if not rows:
                    print(f"   {zone}: no stats in the window", flush=True)
                    continue
                held += int(mean([x[5] for x in rows]))
                fmean.append(mean([x[0] for x in rows]))
                fmax.append(max(x[1] for x in rows))
                cpu += int(mean([x[2] for x in rows]))
                mem += int(mean([x[3] for x in rows]))
                link += mean([x[6] for x in rows])
            if fmean:
                per_run.append((held, mean(fmean), max(fmax), cpu, mem, link))
                print(f"   run {attempt}: owned={held} frame mean={mean(fmean):.1f} ms "
                      f"cores={cpu / 100:.1f} mem={mem} MB link={link:.0f} kB/s", flush=True)
            r.stop_play()

        if not per_run:
            out_lines.append(f"{n:<9} {n * len(r.ZONES):<7} (no data)")
            continue
        frames = [x[1] for x in per_run]
        out_lines.append(f"{n:<9} {n * len(r.ZONES):<7} {int(mean([x[0] for x in per_run])):<6} "
                         f"{mean(frames):<11.1f} {min(frames):.1f}-{max(frames):<7.1f} "
                         f"{mean([x[3] / 100.0 for x in per_run]):<7.1f} "
                         f"{int(mean([x[4] for x in per_run])):<9} "
                         f"{mean([x[5] for x in per_run]):<10.0f} {len(per_run)}")

    text = "\n".join(out_lines)
    print("\n" + text, flush=True)
    path = os.path.join(r.LOGS, "aggload.txt")
    with open(path, "w", encoding="utf-8") as f:
        f.write(text + "\n")
    print("\nwritten to", path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
