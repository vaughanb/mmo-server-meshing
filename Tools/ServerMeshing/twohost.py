"""Drive this machine's half of the two-machine test (DESIGN Q4 follow-up; see Docs/ServerMeshing/MULTIHOST.md).

This machine (A) runs the editor: central, login, database manager, map spawner, and the cells named by --cells.
The other machine (B) runs the remaining cells by hand. Nothing here touches B; it only prepares, watches and grades
A's side, and tells you what B should be doing.

    python Tools/ServerMeshing/twohost.py up            reset the test characters, start Play with A's cells only,
                                                        wait until they serve, then wait for B's cell to appear
    python Tools/ServerMeshing/twohost.py walk           send a bot from C across the line into E and back, and grade it
    python Tools/ServerMeshing/twohost.py status         what is registered and linked right now
    python Tools/ServerMeshing/twohost.py down           stop Play and kill any bots

The cell the other machine owns is --remote (default E). A must not start it, which is what --cells controls.
"""
import argparse, os, re, sys, time
sys.path.insert(0, os.path.dirname(__file__))
import regress as r

STAMP = r.STAMP


def zone_up(cell, start, day, map_id=r.MAP):
    return r.count(r.zone_lines(cell, start, day, map_id), r"Own cell from channel id: " + cell + r"\b") >= 1


def peers_of(cell, start, day):
    """Cell ids this cell has registered as zone peers, with the address it was given."""
    out = {}
    for line in r.zone_lines(cell, start, day):
        m = re.search(r"Zone peer registered: ([A-Za-z0-9_]+)_(\S+) -> (\S+)", line)
        if m:
            out[m.group(1)] = m.group(3)
    return out


def linked_of(cell, start, day):
    out = set()
    for line in r.zone_lines(cell, start, day):
        m = re.search(r"Neighbour zone (\S+) linked", line)
        if m:
            out.add(m.group(1))
    return out


def cmd_up(args):
    day = time.strftime("%Y-%m-%d")
    start = time.strftime("%H:%M:%S")
    r.kill_bots()
    # The tester stands on C a little way from the C/E line, so B's cell has something of ours to ghost.
    r.db_reset((86.5, 0), (20, 0))
    r.request_play("stand", 86.5, 0, env={"MESH_SPAWN_CELLS": ",".join(args.cells)})
    r.wait_status(lambda st: st.get("isPlaying") == "True", 60, "Play")
    print(f"Play started; this machine serves {', '.join(args.cells)} and must NOT start {args.remote}.")

    deadline = time.time() + 120
    while time.time() < deadline:
        up = [c for c in args.cells if zone_up(c, start, day)]
        if len(up) == len(args.cells):
            break
        time.sleep(3)
    print("local cells serving:", ", ".join(c for c in args.cells if zone_up(c, start, day)) or "none")
    if zone_up(args.remote, start, day):
        print(f"WARNING: {args.remote} started HERE too. MESH_SPAWN_CELLS did not take; stop and check the spawner.")
        return 1

    print(f"\nNow start {args.remote} on the other machine. It should run, from its copy of the server build:")
    print(f"  .\\kit-ce-project.exe -batchmode -nographics -startMapServer \\\n"
          f"    -mapName {r.MAP} -channelId {args.remote} -mapPort {args.remote_port} -publicAddress <B-IP> \\\n"
          f"    -centralAddress {args.host} -centralPort 6010 \\\n"
          f"    -databaseManagerAddress {args.host} -databaseManagerPort 6100 \\\n"
          f"    -logFile C:\\MeshTest\\Logs\\{args.remote}.log")

    print(f"\nwaiting for {args.remote} to register and link ...")
    deadline = time.time() + args.wait
    while time.time() < deadline:
        peers = peers_of(args.cells[0], start, day)
        links = linked_of(args.cells[0], start, day)
        if args.remote in peers and args.remote in links:
            print(f"  {args.cells[0]} sees {args.remote} at {peers[args.remote]} and the link is up.")
            if peers[args.remote].startswith("127."):
                print("  WARNING: that is a loopback address. B's -publicAddress is wrong; the handoff will fail.")
            print(f"\nReady. Run:  python {os.path.relpath(__file__, r.ROOT)} walk")
            return 0
        time.sleep(3)
    peers = peers_of(args.cells[0], start, day)
    print(f"  timed out. {args.cells[0]} knows peers: {peers or 'none'}; links: {linked_of(args.cells[0], start, day) or 'none'}")
    print("  peer but no link  -> the link port (map port + 10000) is blocked")
    print("  no peer at all    -> B cannot reach the cluster on 6010, or it is using a different map/cell id")
    return 1


def cmd_walk(args):
    day = time.strftime("%Y-%m-%d")
    start = time.strftime("%H:%M:%S")
    log = r.launch_bot("twohost", "meshbot", ["-meshAutoMode", "walk", "-meshWalkX", "140", "-meshReturnX", "-40"])
    result = r.wait_for_bot(log, "walk", 240)
    print("bot:", result[:160] if result else "(no result)")
    local = args.cells[0]
    lines = r.zone_lines(local, start, day)
    remote_lines = []   # B's logs live on B; grade what this side can see
    sent = r.count(lines, r"Handoff SENT \S+ -> zone " + args.remote)
    ack = [l for l in lines if re.search(r"Handoff ACK .* by zone " + args.remote, l)]
    back = r.count(lines, r"Arrival \S+ .*promoted in place|handoff arrival")
    ok = True
    def check(cond, label):
        nonlocal ok
        ok = ok and bool(cond)
        print(f"  [{'ok' if cond else 'FAIL'}] {label}")
    check(result and "-> PASS" in result, "the bot completed its round trip")
    check(result and "migrations=2" in result, "it hopped twice (out and back)")
    check(sent >= 1, f"{local} handed the bot to {args.remote} on the other machine")
    check(len(ack) >= 1, f"{args.remote} acked the arrival")
    for l in ack[:2]:
        m = re.search(r"registered (\d+) ms after the notice, (\d+) ms after START \(promoted=(\w+)\)", l)
        if m:
            print(f"        ack {m.group(2)} ms after START, promoted={m.group(3)}  (200-500 ms on one machine)")
    check(back >= 1, "the bot came back and was promoted here")
    check(sum(1 for l in lines if "Exception" in l) == 0, f"{local}: no exceptions")
    for line in lines[::-1]:
        if "ghostSteer=" in line:
            m = re.search(r"ghostSteer=(\d+) samples, mean ([\d.]+) m, max ([\d.]+) m", line)
            if m:
                print(f"        ghost steering error: mean {m.group(2)} m, max {m.group(3)} m  (0.02-0.2 m on one machine)")
            break
    print("\nAlso check B's own log for: 'Arrival ... ghost promoted in place' and 0 exceptions.")
    return 0 if ok else 1


def cmd_status(args):
    day = time.strftime("%Y-%m-%d")
    start = "00:00:00"
    for cell in args.cells:
        print(f"{cell}: serving={zone_up(cell, start, day)} peers={peers_of(cell, start, day)} links={sorted(linked_of(cell, start, day))}")
    return 0


def cmd_down(args):
    r.stop_play(); r.kill_bots()
    print("Play stopped, bots killed. Stop B's cell on the other machine.")
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=["up", "walk", "status", "down"])
    ap.add_argument("--cells", default="C,N,NE", help="cells THIS machine serves (default C,N,NE)")
    ap.add_argument("--remote", default="E", help="cell the other machine serves (default E)")
    ap.add_argument("--remote-port", type=int, default=8001, help="map port B should use for its cell")
    ap.add_argument("--host", default="192.168.50.83", help="this machine's LAN address, as B should address it")
    ap.add_argument("--wait", type=int, default=300, help="seconds to wait for B's cell")
    args = ap.parse_args()
    args.cells = [c.strip() for c in args.cells.split(",") if c.strip()]
    return {"up": cmd_up, "walk": cmd_walk, "status": cmd_status, "down": cmd_down}[args.command](args)


if __name__ == "__main__":
    sys.exit(main())
