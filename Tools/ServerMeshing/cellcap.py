"""What ONE cell holds, with nothing else competing for the box.

With every cell loaded, each cell also holds puppets of its loaded neighbours' band, so a loaded cell costs more
than the same players alone (DESIGN 0.69: 3.2 ms at 100 players with idle neighbours against 7.6 ms with three loaded
neighbours). An earlier version of this note called that difference a contention artefact; 0.69 retracted it.

So: load ONE cell (C, the centre, with 8 neighbours -- the pessimistic case), leave the other nine idle, uncap it
so frame time is work per frame, and sweep occupancy. That is the number to compare against a tick budget.
"""
import os
import re
import sys
import time
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "Tools", "ServerMeshing"))
import regress as r  # noqa: E402 -- the harness folder goes on sys.path first

COST = re.compile(r"frame mean ([\d.]+) ms max ([\d.]+) ms cpu (\d+)% mem (\d+) MB")
OWNED = re.compile(r"players=\d+ owned=(\d+)")
LINK = re.compile(r"link out ([\d.]+) kB/s")
CELL = "C"
REPEATS = 2


def measure(day, start, end):
    frames, cpus, owned, links, maxes = [], [], [], [], []
    for line in r.zone_lines(CELL, start, day, r.MAP):
        if "Mesh stats" not in line:
            continue
        m = r.STAMP.search(line)
        if not m or not (start <= m.group(2) <= end):
            continue
        c, o, link = COST.search(line), OWNED.search(line), LINK.search(line)
        if c and o:
            frames.append(float(c.group(1)))
            maxes.append(float(c.group(2)))
            cpus.append(int(c.group(3)))
            owned.append(int(o.group(1)))
            links.append(float(link.group(1)) if link else 0.0)
    if not frames:
        return None
    def avg(xs):
        return sum(xs) / len(xs)
    return (avg(frames), max(maxes), avg(cpus), avg(owned), avg(links))


def main():
    os.chdir(ROOT)
    steps = [int(x) for x in (sys.argv[1] if len(sys.argv) > 1 else "100,200,300,400,500").split(",")]
    rows = []
    for players in steps:
        runs = []
        for attempt in range(1, REPEATS + 1):
            print(f"\n########## {CELL} alone, {players} players, run {attempt}/{REPEATS}", flush=True)
            r.kill_bots()
            r.db_reset((86.5, 0), (0, 0))
            r.request_play("none", 0, 0, env={"MESH_LOAD_PLAYERS": str(players), "MESH_LOAD_CELL": CELL,
                                              "MESH_LOAD_UNCAPPED": "1"})
            r.wait_play(90)
            day = time.strftime("%Y-%m-%d")
            time.sleep(35)
            start = time.strftime("%H:%M:%S")
            time.sleep(40)
            end = time.strftime("%H:%M:%S")
            got = measure(day, start, end)
            if got:
                runs.append(got)
                print(f"   frame={got[0]:.1f} ms (max {got[1]:.0f}) cpu={got[2]:.0f}% owned={got[3]:.0f} "
                      f"link={got[4]:.0f} kB/s", flush=True)
            else:
                print("   no stats in the window", flush=True)
            r.stop_play()
        if runs:
            rows.append((players, runs))

    print()
    header = f"{'players':<9} {'owned':<7} {'work/frame':<11} {'spread':<12} {'frame max':<10} {'cpu %':<7} {'link kB/s'}"
    lines = ["=== one cell (C, 8 neighbours), nothing else loaded, uncapped", "", header, "-" * len(header)]
    for players, runs in rows:
        f = [x[0] for x in runs]
        def avg(i):
            return sum(x[i] for x in runs) / len(runs)
        lines.append(f"{players:<9} {avg(3):<7.0f} {sum(f) / len(f):<11.1f} {min(f):.1f}-{max(f):<7.1f} "
                     f"{max(x[1] for x in runs):<10.0f} {avg(2):<7.0f} {avg(4):.0f}")
    text = "\n".join(lines)
    print(text)
    with open(os.path.join(r.LOGS, "cellcap.txt"), "w", encoding="utf-8") as fh:
        fh.write(text + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
