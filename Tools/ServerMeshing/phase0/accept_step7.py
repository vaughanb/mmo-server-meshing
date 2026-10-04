"""Phase 0 step 7 rig acceptance: ship stand-ins, in-process puppets, sync/RPC load and the scale factor
(step7_design.md "Acceptance", runs 2-9; run 1 runs through regress.py in the rig chain, run 10 is the unit gates).
"Coarse C" is one 600 m cell with no monsters; "space rates" add MESH_RATES=30/0.02. No editor client in any run.

  sizes     run 2: scale 1, 20 ship phantoms, 30 parked load ships, meter count, record sizes on; arms legacy54,
            header56 (kit) and compact28 (MESH_SENDER=on, compact far, R0 1 m). legacy54 sizes min 54 max 54 with
            top 54:n; header56 min 56 max 56; compact28 only 56 and 28 with 28 >= 80 % and Sender payload full 56/56
            compact 28/28; framed - payload mean in [5, 9] in every arm (in [5, 6] reported)
  once      run 3: run 2's header56 setup with MESH_SENDER=on and sources direct: Sender writes = builds x direct in
            every window whose direct held, direct 50, errors 0 dropped 0; Send 119 records = Sender u + r within
            +-0.5 % over the span; refusals <= 50 in the first window and 0 after; Ships notInSlot 0
  bots      run 4: 40 mirror and 10 parked load ships, MESH_RIG_SHIPS=1, bot meshbot standing at (0, 0); arms scale 0.5
            legacy54, header56, compact28 (sender on, far, R0 1 m), header56 with MESH_SHIP_MIRROR_VELOCITY=real
            (reported), and scale 0.03 header56 with MESH_LOAD_AREA=0,0,30. Every measured Bot ships window: seen >= 45,
            path p99 <= 0.5 x scale m and max <= 1.0 x scale m, parked max <= 0.001 m (0.01 m for compact28),
            badLength 0, unknownPath 0; the bot log free of the registration and decode faults; lag p50 reported
  ai        run 5: scale 0.03, 100 AI ships, 180 s, instantiate then pooled: ships 100 in every window, patrol p50
            1.504 m/s +-5 % (1.600 means force-after-damping: stop), speed max <= 3.84 m/s, outside 0 in >= 90 % of
            windows, shots/bolts/hits > 0, bolts = 2 x shots +-1 %, pooled gc0 <= instantiate gc0
  pilots    run 6: run 5 at 20 ships with pilots, sender on, meter count, record sizes: pilots 20 seated 20, ships 20,
            Sender kit >= 20 and direct 20, the sizes show the ship length and another (the pilots'); then with 4 ship
            phantoms too: seated stays 20
  g1s       run 7: 64 ship phantoms and 64 AI ships at scale 0.03, meter count; arms MESH_SHIP_MIRROR_VELOCITY zero and
            real: every phantom bound, uploads/s within +-10 % of 1280, Ships uploads + stale + bad within +-2 % of the
            Phantom windows' uploads over the span, bad 0, stale <= 2 x respawns; t119 bytes per record reported
  feeder    run 8: MESH_PUPPET_FEEDER ship:200@20 and character:200@20, each without and with MESH_SENDER=on:
            snapshots/s within +-2 % of 4000, ship gc0 0, Sender direct 200 with the sender; us/snapshot reported
  syncload  run 9: run 7 (zero) plus MESH_SYNC_LOAD=dirty:100,rpc:200: writes/s +-1 % of 2000, rpc/s +-1 % of 200,
            Send t2 (RPC) calls within +-5 % of Sync load rpcSends + Ships rpcSends; alloc B/rpc reported

Every run fails a check on a cell C line holding Exception, an "error:" prefix of step 7 (Ships, Feeder, Sync load,
Bot ships), Phantoms error:, State sender error:, Send meter error: or Too Big Packet. rigrun.check_env runs before
every run. The attended b1 sink arm (machine B: sink:20:ship:C, header56, remoteb.py sink-start --sink-sizes; Sink
sizes min 56 max 56) is not run here.

    python accept_step7.py                     # every group, in the order above (about 19 Play sessions, ~90 min)
    python accept_step7.py sizes bots          # only these groups
Writes Logs/p0/accept_step7.txt.
"""
import datetime
import math
import os
import statistics
import sys
import time

TOOLS = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
sys.path.insert(0, TOOLS)
import p0phantom  # noqa: E402 -- the harness folder goes on sys.path first
import p0sender  # noqa: E402
import p0ships  # noqa: E402
import regress as r  # noqa: E402
import rigrun  # noqa: E402

WARMUP, MEASURE, AI_MEASURE = 45, 60, 180
COARSE = {"MESH_LAYOUT_OVERRIDE": "ServerMeshTestMap=C@-300,-300,600,600", "MESH_SPAWN_CELLS": "C",
          "MESH_NO_MONSTERS": "1"}
SPACE = dict(COARSE, MESH_RATES="30/0.02")
SIZES_BASE = dict(SPACE, MESH_PHANTOM="virtual:20:ship:C", MESH_LOAD_SHIPS="C:30:parked", MESH_METER_SENDS="count",
                  MESH_METER_RECORD_SIZES="1")
COMPACT = {"MESH_SHIP_WIRE": "compact28", "MESH_SENDER": "on", "MESH_SENDER_COMPACT": "far", "MESH_SENDER_R0": "1"}
BOT_BASE = dict(COARSE, MESH_LOAD_SHIPS="C:40:mirror,C:10:parked", MESH_RIG_SHIPS="1")
BOT_USER = "meshbot"
BOT_ARGS = ["-meshAutoMode", "stand", "-meshWalkX", "0", "-meshWalkZ", "0", "-meshBotNetSeconds", "5"]
G1S = dict(SPACE, **p0ships.scaled_env(0.03), MESH_PHANTOM="virtual:64:ship:C", MESH_LOAD_SHIPS="C:64:ai",
           MESH_METER_SENDS="count")
BAD = ("Exception", "Ships error:", "Feeder error:", "Sync load error:", "Bot ships error:", "Phantoms error:",
       "State sender error:", "Send meter error:", "Too Big Packet")
BOT_BAD = ("is not registered", "Unable to spawn object", "invalid state length", "error occurs while reading",
           "Exception", "Bot ships error:")
RPC_TYPE = 2                              # LiteNetLibManager GameMsgTypes.RPC
# The space AI patrols by chasing a point that circles 400 m x scale at 0.05 rad/s (ShipAIPilot.cs:243-249), so its
# speed lies between that point's 20 m/s and the 40 % throttle terminal 50.13 m/s, x scale 0.03: never a steady cruise.
PATROL_LOW = 0.05 * 400 * 0.03 * 0.9      # 0.54 m/s
PATROL_HIGH = 0.4 * 125.33 * 0.03 * 1.05  # 1.58 m/s
TERMINAL = 125.33 * 0.03                  # 3.76 m/s: full-thrust terminal speed, (F/drag - F dt)/mass at scale 0.03
OUTSIDE_SHARE, OUTSIDE_RUN = 0.05, 6      # at most 5 % of the AI in the inset margin; none there over 6 windows (30 s)
GC0_PER_MINUTE = 1                        # the process's own gen-0 rate in every run here, feeder or not
GROUPS = ("sizes", "once", "bots", "ai", "pilots", "g1s", "feeder", "syncload")


def windowed(rows, start, end):
    return [x for x in rows if x and x.get("end") is not None and start <= x["end"] <= end]


def collect(play_start, start, end):
    """What cell C logged in one run: the windows inside [start, end], every Sender window since Play began, and the
    fault and one-off lines."""
    c = r.zone_lines("C", play_start.strftime("%H:%M:%S"), play_start.strftime("%Y-%m-%d"))
    senders = [x for x in (p0sender.parse_sender_window(line) for line in c) if x]
    return {
        "start": start, "end": end,
        "send": windowed([p0phantom.parse_send_window(line) for line in c], start, end),
        "phantom": windowed([p0phantom.parse_phantom_window(line) for line in c], start, end),
        "sender": windowed(senders, start, end), "sender_all": senders,
        "ships": windowed([p0ships.parse_ships_window(line) for line in c], start, end),
        "feeder": windowed([p0ships.parse_feeder_window(line) for line in c], start, end),
        "sync": windowed([p0ships.parse_sync_load_window(line) for line in c], start, end),
        "bad": [line.strip() for line in c if any(mark in line for mark in BAD)],
        "ship_lines": [line.strip() for line in c if "Ships:" in line or "Feeder:" in line or "Sync load:" in line],
    }


def run(env, rep, measure=MEASURE):
    rigrun.check_env(env)
    play_start, start, end = rigrun.run_window(env, WARMUP, measure, rep)
    return collect(play_start, start, end)


def run_window_with_bots(env, warmup, measure, rep, bots, bot_args):
    """rigrun.run_window's calls in the same order, with the bots launched right after Play is up and killed before
    Play stops. Returns (play start, window start, window end, {user: log path}); a failure is raised only after Play
    stopped."""
    r.kill_bots()
    leftover = r.kit_process_count()
    if leftover:
        still = r.kill_all_kit_processes()
        rep.note(f"    {leftover} leftover rig process(es) killed before the run")
        if still:
            raise RuntimeError(f"{still} leftover rig process(es) survived being killed")
    if os.path.exists(r.HOLD):
        os.remove(r.HOLD)
    play_start = rigrun.now()
    logs = {}
    try:
        r.db_reset(rigrun.PARK, (0, 0), clear_buildings=True)
        r.request_play("none", 0, 0, env=env, enabled=False)
        r.wait_play(120)
        for user in bots:
            logs[user] = r.launch_bot("step7", user, bot_args)
        time.sleep(warmup)
        start = rigrun.now()
        time.sleep(measure)
        end = rigrun.now()
        if not r.play_still_running():
            raise RuntimeError("Play ended, or another Play replaced it, during the measurement")
    finally:
        try:
            r.kill_bots()
        finally:
            survivors = r.stop_play()
    if survivors:
        raise RuntimeError(f"{survivors} rig process(es) outlived Play")
    return play_start, start, end, logs


def read_bot_log(path, start, end):
    """A bot's Bot ships windows timed inside [start, end] and its fault counts. Bot ships lines carry no time, so each
    takes the UTC time of the Bot net window logged before it (both every 5 s); one with none before it is warm-up."""
    if not os.path.exists(path):
        return {"exists": False, "ships": [], "all": [], "bad": {mark: 0 for mark in BOT_BAD}}
    with open(path, encoding="utf-8", errors="replace") as f:
        lines = f.read().splitlines()
    lo, hi = start.astimezone(datetime.timezone.utc), end.astimezone(datetime.timezone.utc)
    at, timed, every = None, [], []
    for line in lines:
        net = p0phantom.parse_bot_net_window(line)
        if net:
            at = net["at"]
            continue
        ships = p0ships.parse_bot_ships(line)
        if ships:
            every.append(ships)
            if at is not None and lo <= at <= hi:
                timed.append(ships)
    return {"exists": True, "ships": timed, "all": every,
            "bad": {mark: sum(mark in line for line in lines) for mark in BOT_BAD}}


def run_bots(env, rep):
    rigrun.check_env(env)
    bot_args = BOT_ARGS + p0ships.rig_ship_bot_args(env)
    play_start, start, end, logs = run_window_with_bots(env, WARMUP, MEASURE, rep, (BOT_USER,), bot_args)
    got = collect(play_start, start, end)
    got["bot"] = read_bot_log(logs[BOT_USER], start, end)
    return got


# ------------------------------------------------------------------ figures
def mean(values):
    values = list(values)
    return statistics.fmean(values) if values else float("nan")


def total(rows, key):
    return sum(x[key] for x in rows)


def type_group(s, type_id):
    group = [g for g in s["types"] if g["type"] == type_id]
    return group[0] if group else None


def records_of(s):
    rec = s["records"]
    return rec["ro"] + rec["u"] + rec["other"] if rec else 0


def lengths(sizes):
    return {length: count for length, count in sizes["top"] if count > 0}


def longest_run(values):
    """The longest run of consecutive non-zero values."""
    best = current = 0
    for v in values:
        current = current + 1 if v else 0
        best = max(best, current)
    return best


def within_pct(value, expected, limit):
    return value == value and expected and abs(value - expected) / abs(expected) * 100 <= limit


# ------------------------------------------------------------------ the runner
class Acceptance:
    def __init__(self):
        self.rep = rigrun.Report("Phase 0 step 7 rig acceptance (ship stand-ins, puppets, sync/RPC load, scale)")
        self.failed = 0

    def check(self, ok, text):
        self.rep.note(f"  [{'ok' if ok else 'FAIL'}] {text}")
        self.failed += 0 if ok else 1

    def common(self, got, label):
        self.check(not got["bad"], f"{label}: no fault line ({got['bad'][:3]})")
        for line in got["ship_lines"][:3]:
            self.rep.note(f"    {label}: {line[line.find('- ') + 2:] if '- ' in line else line}")

    def framing(self, label, sizes):
        gaps = [s["framed"] - s["payload"] for s in sizes if s["n"] > 0]
        self.check(bool(gaps) and all(5 <= g <= 9 for g in gaps),
                   f"{label}: framed - payload mean in [5, 9] in every window ({[round(g, 2) for g in gaps]})")
        self.rep.note(f"    {label}: framed - payload in [5, 6] (every object id below 2288) in "
                      f"{sum(5 <= g <= 6 for g in gaps)} of {len(gaps)} windows")

    # -------------------------------------------------------------- gates
    def sizes(self):
        self.rep.note("--- sizes (run 2): 20 ship phantoms, 30 parked ships, scale 1, record sizes on")
        for arm, extra in (("legacy54", {"MESH_SHIP_WIRE": "legacy54"}), ("header56", {"MESH_SHIP_WIRE": "header56"}),
                           ("compact28", COMPACT)):
            got = run(dict(SIZES_BASE, **extra), self.rep)
            label = f"sizes {arm}"
            self.common(got, label)
            sizes = [s["sizes"] for s in got["send"] if s["sizes"]]
            self.check(bool(sizes) and len(sizes) == len(got["send"]),
                       f"{label}: every Send window carries the sizes group ({len(sizes)} of {len(got['send'])})")
            if arm != "compact28":
                want = p0ships.UPLOAD_BYTES[arm]
                self.check(bool(sizes) and all(s["min"] == want and s["max"] == want and s["top"][0] == (want, s["n"])
                                               for s in sizes),
                           f"{label}: sizes min {want} max {want} top {want}:n in every window "
                           f"({[(s['min'], s['max'], s['top'][0], s['n']) for s in sizes][:4]})")
            else:
                ok = bool(sizes) and all(set(lengths(s)) <= {28, 56} and sum(lengths(s).values()) == s["n"]
                                         and s["over"] == 0 for s in sizes)
                self.check(ok, f"{label}: only lengths 56 and 28 ({[s['top'] for s in sizes][:3]})")
                share = sum(lengths(s).get(28, 0) for s in sizes) / max(1, sum(s["n"] for s in sizes))
                self.check(share >= 0.80, f"{label}: 28 B records {share:.1%} of all (>= 80 %, expected ~98 %)")
                pay = [s["payload"] for s in got["sender"] if s["payload"] and s["payload"]["full_n"]
                       and s["payload"]["compact_n"]]
                self.check(bool(pay) and all((p["full_min"], p["full_max"], p["compact_min"], p["compact_max"])
                                             == (56, 56, 28, 28) for p in pay),
                           f"{label}: Sender payload full 56/56 compact 28/28 ({pay[:2]})")
            self.framing(label, sizes)
            ph = got["phantom"]
            self.rep.note(f"    {label}: phantom uploads {mean(p['uploads_per_s'] for p in ph):.0f}/s; an upload is "
                          f"{p0ships.UPLOAD_BYTES[extra['MESH_SHIP_WIRE']]} B plus a 5-16 B header (decision 1)")
        self.rep.note("    b1 (attended, machine B): sink:20:ship:C, header56, sink-start --sink-sizes: Sink sizes "
                      "min 56 max 56 -- not run here")

    def once(self):
        self.rep.note("--- once (run 3): run 2's header56 setup, MESH_SENDER=on, sources direct")
        env = dict(SIZES_BASE, MESH_SHIP_WIRE="header56", MESH_SENDER="on", MESH_SENDER_SOURCES="direct")
        got = run(env, self.rep)
        self.common(got, "once")
        sw = got["sender"]
        self.check(bool(sw), f"once: Sender windows logged ({len(sw)})")
        held = [s for prev, s in zip(sw, sw[1:]) if prev["direct"] == s["direct"]]
        self.check(bool(held) and all(s["writes"] == s["builds"] * s["direct"] for s in held),
                   f"once: writes = builds x direct in every window whose direct held "
                   f"({[(s['writes'], s['builds'], s['direct']) for s in held if s['writes'] != s['builds'] * s['direct']][:3]})")
        self.check(bool(sw) and all(s["direct"] == 50 and s["errors"] == 0 and s["dropped"] == 0 for s in sw),
                   f"once: direct 50, errors 0, dropped 0 ({sorted({(s['direct'], s['errors'], s['dropped']) for s in sw})})")
        send = sum(records_of(s) for s in got["send"])
        sent = total(sw, "records_u") + total(sw, "records_r")
        self.check(within_pct(send, sent, 0.5), f"once: Send 119 records {send} within +-0.5 % of Sender u + r {sent}")
        every = got["sender_all"]
        self.check(bool(every) and every[0]["refusals"] <= 50 and all(s["refusals"] == 0 for s in every[1:]),
                   f"once: refusals <= 50 in the first window and 0 after ({[s['refusals'] for s in every][:6]})")
        self.check(bool(got["ships"]) and all(s["not_in_slot"] == 0 for s in got["ships"]),
                   f"once: Ships notInSlot 0 ({[s['not_in_slot'] for s in got['ships']]})")

    def bots(self):
        self.rep.note("--- bots (run 4): 40 mirror and 10 parked ships, bot meshbot at (0, 0) with -meshRigShips")
        arms = (("0.5 legacy54", 0.5, {"MESH_SHIP_WIRE": "legacy54"}),
                ("0.5 header56", 0.5, {"MESH_SHIP_WIRE": "header56"}),
                ("0.5 compact28", 0.5, COMPACT),
                ("0.5 header56 real velocity", 0.5, {"MESH_SHIP_WIRE": "header56", "MESH_SHIP_MIRROR_VELOCITY": "real"}),
                ("0.03 header56", 0.03, {"MESH_SHIP_WIRE": "header56", "MESH_LOAD_AREA": "0,0,30"}))
        for label, scale, extra in arms:
            env = dict(BOT_BASE, **p0ships.scaled_env(scale))
            env.update(extra)
            got = run_bots(env, self.rep)
            label = f"bots {label}"
            self.common(got, label)
            bot = got["bot"]
            if not bot["exists"]:
                self.check(False, f"{label}: the bot log exists")
                continue
            w = bot["ships"]
            self.check(not any(bot["bad"].values()), f"{label}: bot log free of {', '.join(BOT_BAD)} ({bot['bad']})")
            self.check(bool(w) and all(x["seen"] >= 45 for x in w),
                       f"{label}: seen >= 45 in every measured Bot ships window ({[x['seen'] for x in w]}; "
                       f"{len(bot['all'])} windows in the log)")
            self.check(bool(w) and all(x["path_p99"] <= 0.5 * scale and x["path_max"] <= 1.0 * scale for x in w),
                       f"{label}: path p99 <= {0.5 * scale:g} m and max <= {1.0 * scale:g} m "
                       f"({[(x['path_p99'], x['path_max']) for x in w][:4]})")
            parked = 0.01 if "compact28" in label else 0.001
            self.check(bool(w) and all(x["parked_max"] <= parked for x in w),
                       f"{label}: parked max <= {parked:g} m ({[x['parked_max'] for x in w][:4]})")
            self.check(bool(w) and all(x["bad_length"] == 0 and x["unknown_path"] == 0 for x in w),
                       f"{label}: badLength 0 and unknownPath 0")
            lag = [x["lag_p50_ms"] for x in w]
            note = "" if scale != 0.5 or not lag or 20 <= statistics.median(lag) <= 60 else " (outside 20-60 ms: investigate)"
            self.rep.note(f"    reported: {label} lag p50 {lag} ms{note}")

    def ai_run(self, ships, extra, measure=AI_MEASURE):
        env = dict(SPACE, **p0ships.scaled_env(0.03), MESH_LOAD_SHIPS=f"C:{ships}:ai")
        env.update(extra)
        return run(env, self.rep, measure)

    def ai(self):
        self.rep.note("--- ai (run 5): scale 0.03, 100 AI ships, 180 s, instantiate then pooled")
        gc0 = {}
        for mode in ("instantiate", "pooled"):
            got = self.ai_run(100, {"MESH_SHIP_PROJECTILES": mode})
            label = f"ai {mode}"
            self.common(got, label)
            w = got["ships"]
            self.check(bool(w) and all(x["ships"] == 100 and x["ai"] == 100 for x in w),
                       f"{label}: ships 100 in every window ({sorted({x['ships'] for x in w})})")
            patrol = statistics.median(x["patrol_p50"] for x in w) if w else float("nan")
            self.check(PATROL_LOW <= patrol <= PATROL_HIGH,
                       f"{label}: patrol p50 {patrol:.3f} m/s between the patrol point's {PATROL_LOW:.2f} and the 40 % "
                       f"throttle terminal {PATROL_HIGH:.2f}")
            top = max((x["speed_max"] for x in w), default=0.0)
            self.check(within_pct(top, TERMINAL, 2),
                       f"{label}: speed max {top:.2f} m/s within 2 % of the full-thrust terminal {TERMINAL:.2f} "
                       f"({TERMINAL * 133.33 / 125.33:.2f} would mean force after damping)")
            outside = [x["outside"] for x in w]
            self.check(bool(w) and max(outside) <= OUTSIDE_SHARE * 100 and longest_run(outside) <= OUTSIDE_RUN,
                       f"{label}: outside <= {OUTSIDE_SHARE * 100:.0f} and back to 0 within {OUTSIDE_RUN} windows "
                       f"({outside})")
            shots, bolts, hits = total(w, "shots"), total(w, "bolts"), total(w, "hits")
            self.check(shots > 0 and bolts > 0 and hits > 0, f"{label}: shots {shots}, bolts {bolts}, hits {hits} > 0")
            self.check(within_pct(bolts, 2 * shots, 1), f"{label}: bolts {bolts} = 2 x shots {shots} +-1 %")
            gc0[mode] = total(w, "gc0")
            seconds = total(w, "seconds") or float("nan")
            self.rep.note(f"    reported: {label} ms/s pilot {mean(x['pilot_ms_per_s'] for x in w):.2f} engine "
                          f"{mean(x['engine_ms_per_s'] for x in w):.2f} combat {mean(x['combat_ms_per_s'] for x in w):.2f} "
                          f"bolts {mean(x['bolts_ms_per_s'] for x in w):.2f}; scans/s {total(w, 'scans') / seconds:.0f} "
                          f"(2 x AI = 200), casts/s {total(w, 'casts') / seconds:.0f} (~30 x AI = 3000); gc0 {gc0[mode]}")
        if len(gc0) == 2:
            self.check(gc0["pooled"] <= gc0["instantiate"],
                       f"ai: pooled gc0 {gc0['pooled']} <= instantiate gc0 {gc0['instantiate']}")

    def pilots(self):
        self.rep.note("--- pilots (run 6): 20 AI ships with pilots, sender on, meter count, record sizes")
        extra = {"MESH_LOAD_SHIP_PILOTS": "1", "MESH_SENDER": "on", "MESH_METER_SENDS": "count",
                 "MESH_METER_RECORD_SIZES": "1"}
        # Records go only to subscribers, so 4 character phantoms stand among the ships, which the area keeps within
        # about 57 m of each other, inside every range at scale 0.03.
        receivers = {"MESH_LOAD_AREA": "0,0,20", "MESH_PHANTOM": "virtual:4:character:C",
                     "MESH_PHANTOM_AREA": "disc:0,0,20"}
        got = self.ai_run(20, dict(extra, **receivers), MEASURE)
        self.common(got, "pilots")
        w = got["ships"]
        self.check(bool(w) and all(x["pilots"] == 20 and x["seated"] == 20 and x["ships"] == 20 for x in w),
                   f"pilots: pilots 20 seated 20 ships 20 ({sorted({(x['pilots'], x['seated'], x['ships']) for x in w})})")
        sw = got["sender"]
        self.check(bool(sw) and all(s["kit"] >= 20 and s["direct"] == 20 for s in sw),
                   f"pilots: Sender kit >= 20 and direct 20 ({sorted({(s['kit'], s['direct']) for s in sw})})")
        sizes = [s["sizes"] for s in got["send"] if s["sizes"]]
        ship_len = p0ships.UPLOAD_BYTES[p0ships.DEFAULT_WIRE]
        ship_records = sum(lengths(s).get(ship_len, 0) for s in sizes)
        sized_records = sum(s["n"] for s in sizes)
        sender_records = sum(s["records_u"] + s["records_r"] for s in sw)
        self.check(bool(sizes) and ship_records > 0 and any(len(lengths(s)) >= 2 for s in sizes),
                   f"pilots: sizes show the ship length {ship_len} and the characters' apart ({[s['top'] for s in sizes][:3]})")
        # The sender also carries the kit sources (pilots and phantom characters), so its records are every 119 record.
        self.check(within_pct(sized_records, sender_records, 0.5),
                   f"pilots: sized records {sized_records} = Sender records u+r {sender_records} +-0.5 %")
        self.rep.note(f"    reported: {ship_len} B records {ship_records} of {sized_records} "
                      f"({100 * ship_records / sized_records if sized_records else 0:.1f} %, the ships' share of pairs)")
        got = self.ai_run(20, dict(extra, MESH_PHANTOM="virtual:4:ship:C"), MEASURE)
        self.common(got, "pilots with ship phantoms")
        w = got["ships"]
        self.check(bool(w) and all(x["seated"] == 20 and x["phantom"] == 4 for x in w),
                   f"pilots with ship phantoms: seated 20 with 4 phantom ships, none seated on them "
                   f"({sorted({(x['seated'], x['phantom']) for x in w})})")

    def g1s(self):
        self.rep.note("--- g1s (run 7): 64 ship phantoms and 64 AI ships at scale 0.03, meter count")
        bytes_per_record = {}
        for arm in ("zero", "real"):
            got = run(dict(G1S, MESH_SHIP_MIRROR_VELOCITY=arm), self.rep)
            label = f"g1s {arm}"
            self.common(got, label)
            self.g1s_checks(label, got)
            groups = [g for g in (type_group(s, 119) for s in got["send"]) if g]
            records = sum(records_of(s) for s in got["send"])
            bytes_per_record[arm] = sum(g["bytes"] for g in groups) / records if records else float("nan")
        self.rep.note(f"    reported: t119 bytes per record {bytes_per_record} (sizes identical by design)")

    def g1s_checks(self, label, got):
        ph, w = got["phantom"], got["ships"]
        self.check(bool(ph) and all(p["bound_virtual"] == 64 for p in ph),
                   f"{label}: every phantom bound ({sorted({p['bound_virtual'] for p in ph})})")
        rate = mean(p["uploads_per_s"] for p in ph)
        self.check(within_pct(rate, 1280, 10), f"{label}: uploads {rate:.0f}/s within +-10 % of 1280")
        phantom_uploads = sum(p["uploads_per_s"] * p["seconds"] for p in ph)
        ship_uploads = total(w, "uploads") + total(w, "stale") + total(w, "bad")
        self.check(within_pct(ship_uploads, phantom_uploads, 2),
                   f"{label}: Ships uploads + stale + bad {ship_uploads} within +-2 % of Phantom uploads "
                   f"{phantom_uploads:.0f}")
        self.check(bool(w) and total(w, "bad") == 0, f"{label}: bad 0 ({total(w, 'bad')})")
        self.check(total(w, "stale") <= 2 * total(w, "respawns"),
                   f"{label}: stale {total(w, 'stale')} <= 2 x respawns {total(w, 'respawns')}")

    def feeder(self):
        self.rep.note("--- feeder (run 8): 200 ship and 200 character puppets at 20 Hz, without and with the sender")
        for kind in ("ship", "character"):
            for sender in (False, True):
                env = dict(SPACE, MESH_PUPPET_FEEDER=f"{kind}:200@20")
                if sender:
                    env["MESH_SENDER"] = "on"
                got = run(env, self.rep)
                label = f"feeder {kind}{' sender on' if sender else ''}"
                self.common(got, label)
                w = got["feeder"]
                rates = [x["snapshots"] / x["seconds"] for x in w if x["seconds"] > 0]
                self.check(bool(rates) and all(within_pct(x, 4000, 2) for x in rates),
                           f"{label}: snapshots/s within +-2 % of 4000 ({[round(x) for x in rates]})")
                if kind == "ship":
                    minutes = total(w, "seconds") / 60 or float("nan")
                    self.check(bool(w) and total(w, "gc0") <= GC0_PER_MINUTE * math.ceil(minutes),
                               f"{label}: gc0 {total(w, 'gc0')} <= the process's own {GC0_PER_MINUTE}/min "
                               f"({[x['gc0'] for x in w]}; GC.CollectionCount counts the whole process)")
                if sender:
                    sw = got["sender"]
                    self.check(bool(sw) and all(s["direct"] == 200 for s in sw),
                               f"{label}: Sender direct 200 ({sorted({s['direct'] for s in sw})})")
                self.rep.note(f"    reported: {label} us/snapshot {mean(x['us_per_snapshot'] for x in w):.3f}, "
                              f"total ms/s {mean(x['total_ms_per_s'] for x in w):.2f}, gc0 {total(w, 'gc0')}")

    def syncload(self):
        self.rep.note("--- syncload (run 9): run 7 (zero) plus MESH_SYNC_LOAD=dirty:100,rpc:200")
        got = run(dict(G1S, MESH_SHIP_MIRROR_VELOCITY="zero", MESH_SYNC_LOAD="dirty:100,rpc:200"), self.rep)
        self.common(got, "syncload")
        w, ships = got["sync"], got["ships"]
        seconds = total(w, "seconds") or float("nan")
        self.check(within_pct(total(w, "writes") / seconds, 2000, 1),
                   f"syncload: writes {total(w, 'writes') / seconds:.1f}/s within +-1 % of 2000")
        self.check(within_pct(total(w, "rpc") / seconds, 200, 1),
                   f"syncload: rpc {total(w, 'rpc') / seconds:.1f}/s within +-1 % of 200")
        groups = [type_group(s, RPC_TYPE) for s in got["send"]]
        expected = total(w, "rpc_sends") + total(ships, "rpc_sends")
        if groups and all(groups):
            calls = sum(g["calls"] for g in groups)
            self.check(within_pct(calls, expected, 5),
                       f"syncload: Send t2 RPC calls {calls} within +-5 % of Sync load + Ships rpcSends {expected}")
        else:
            self.check(False, f"syncload: t2 is listed in every Send window (it folded into other in "
                              f"{sum(1 for g in groups if not g)} of {len(groups)})")
        self.rep.note(f"    reported: alloc {mean(x['alloc_bytes_per_rpc'] for x in w):.0f} B/rpc, gc0 {total(w, 'gc0')}")


def main(argv):
    groups = argv or list(GROUPS)
    unknown = [g for g in groups if g not in GROUPS]
    if unknown:
        raise SystemExit(f"unknown group(s) {', '.join(unknown)}; known: {', '.join(GROUPS)}")
    problems = rigrun.start_problems()
    if problems:
        raise SystemExit("\n".join(problems))
    acc = Acceptance()
    acc.rep.note(f"groups: {', '.join(groups)}; warmup {WARMUP} s, measure {MEASURE} s per run ({AI_MEASURE} s for ai)")
    try:
        for group in groups:
            try:
                getattr(acc, group)()
            except Exception as ex:  # a broken run fails its group; the other groups and the report still happen
                acc.check(False, f"{group}: the group stopped: {type(ex).__name__}: {ex}")
    finally:
        acc.rep.note(f"=== {acc.failed} check(s) failed")
        out = os.path.join(TOOLS, "..", "..", "Logs", "p0", "accept_step7.txt")
        os.makedirs(os.path.dirname(out), exist_ok=True)
        with open(out, "w", encoding="utf-8") as f:
            f.write(acc.rep.text() + "\n")
    return 1 if acc.failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
