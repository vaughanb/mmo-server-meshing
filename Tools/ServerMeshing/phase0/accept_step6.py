"""Phase 0 step 6 rig acceptance: the state sender, measure mode and the CE-12 check (step6_design.md "Acceptance",
runs 2-6 and 8; gates 1 and 7 run through regress.py in the rig chain). Coarse C (one 600 m cell), no editor client.

  measure   gate 2: 50 character phantoms at V 49, meter count; arms off (A) and MESH_SENDER=measure (M), 3
            interleaved pairs. M's t119 records/s, bytes/record and u records/s within +-1 % of A's, ro records/s
            within +-1 % or +-20/s; refused and tooBig 0 in both; M's Sender windows say measure, kit 50, builds ~20/s
  parity    gate 3: the same plus MESH_AOI_TIMING=1; arms A and MESH_SENDER=on (S) at the default tiers, 3 pairs. S's
            records/s within +-2 % of A's; viewErr p50 and p95 within 0.10 m of A's; posErr p95 <= 0.50 m in S;
            S's Sender windows stuckReliable 0, errors 0, rewraps <= 1, hz 20 = pairs. Work ticks move/send and the
            Sender window ms/s reported
  eyes      gate 4: 50 eye phantoms over 50 filler characters (V 50), meter count; arms A and S, 2 pairs: records/s
            within +-2 %, viewErr p95 within 0.10 m
  monsters  gate 5: the NavMesh path, MESH_MONSTER_AREAS=all (about 294 monsters), 50 character phantoms at V 49,
            meter count; arms A and S, 2 pairs: records/s within +-3 %; S's kit >= owned - players (phantoms included), refusals 0,
            stuckReliable 0, errors 0
  bots      gate 6: 50 synthetic players crowded on (0,0) r 25, two real bots (meshbot, meshbot2) standing at (0,0),
            meter count; one unmeasured registration run, then arms A and S, 2 pairs: records/s within +-2 %; bot logs
            free of invalid state length, error occurs while reading and Exception, entity movement not found no more
            often than in A; players seen >= 50 in every Bot net window; Bot net in kB/s reported
  ce12      gate 8: MESH_CE12_CHECK=1 without and with MESH_SENDER=on: C logs exactly one CE-12 check line, shape=pass

Every run fails a check on a zone line holding Exception, State sender error:, Phantoms error:, Send meter error: or
Too Big Packet, and every kit-arm run on any State sender or Sender window line (default off). rigrun.check_env runs
before every run (request_play silently drops a key outside regress.ENV_KEYS).

    python accept_step6.py                     # every group, in the order above
    python accept_step6.py parity ce12         # only these groups
Writes Logs/p0/accept_step6.txt.
"""
import datetime
import os
import statistics
import sys
import time

TOOLS = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
sys.path.insert(0, TOOLS)
import abload  # noqa: E402 -- the harness folder goes on sys.path first
import p0phantom  # noqa: E402
import p0sender  # noqa: E402
import p0tools  # noqa: E402
import regress as r  # noqa: E402
import rigrun  # noqa: E402
import rigtools  # noqa: E402

WARMUP, MEASURE = 45, 60
CE12_WARMUP, CE12_MEASURE = 40, 15        # the check needs at most 300 frames of retries after the cell is ready
REGISTER_WARMUP, REGISTER_MEASURE = 40, 10
COARSE = {"MESH_LAYOUT_OVERRIDE": "ServerMeshTestMap=C@-300,-300,600,600", "MESH_SPAWN_CELLS": "C",
          "MESH_RATES": "60/0.03"}
QUIET = dict(COARSE, MESH_NO_MONSTERS="1")
CHARS = {"MESH_PHANTOM": "virtual:50:character:C", "MESH_PHANTOM_VIEW": "49", "MESH_METER_SENDS": "count"}
EYES = {"MESH_PHANTOM": "virtual:50:eye:C", "MESH_PHANTOM_VIEW": "50", "MESH_METER_SENDS": "count"}
BOT_LOAD = {"MESH_LOAD_PLAYERS": "50", "MESH_LOAD_CELL": "C", "MESH_LOAD_AREA": "0,0,25", "MESH_METER_SENDS": "count"}
ARMS = {"A": {}, "M": {"MESH_SENDER": "measure"}, "S": {"MESH_SENDER": "on"}}
BOTS = ("meshbot", "meshbot2")
BOT_ARGS = ["-meshAutoMode", "stand", "-meshWalkX", "0", "-meshWalkZ", "0", "-meshBotNetSeconds", "5"]
BAD = ("Exception", "State sender error:", "Phantoms error:", "Send meter error:", "Too Big Packet")
SENDER_MARKS = ("State sender", "Sender window", "CE-12 check")
BOT_BAD = ("invalid state length", "error occurs while reading", "Exception")
BOT_MOVEMENT = "entity movement not found"
EXPECTED_PARITY_RECORDS = 50 * 50 * 20    # 50 phantoms each seeing 50 characters at the kit's 20 Hz
GROUPS = ("measure", "parity", "eyes", "monsters", "bots", "ce12")


def windowed(rows, start, end):
    return [x for x in rows if x and x.get("end") is not None and start <= x["end"] <= end]


def collect(play_start, start, end):
    """What cell C logged in one run: the windows inside [start, end], and the one-off lines since Play began."""
    c = r.zone_lines("C", play_start.strftime("%H:%M:%S"), play_start.strftime("%Y-%m-%d"))
    stats = []
    for line in c:
        s = rigtools.parse_stats_line(line)
        if s and "owned" in s:
            stats.append(dict(s, end=rigtools.stamp_datetime(line)))
    return {
        "start": start, "end": end,
        "send": windowed([p0phantom.parse_send_window(line) for line in c], start, end),
        "phantom": windowed([p0phantom.parse_phantom_window(line) for line in c], start, end),
        "sender": windowed([p0sender.parse_sender_window(line) for line in c], start, end),
        "ticks": windowed([p0tools.parse_work_ticks(line) for line in c], start, end),
        "stats": windowed(stats, start, end),
        "ce12": [x for x in (p0sender.parse_ce12_check(line) for line in c) if x],
        "bad": [line.strip() for line in c if any(mark in line for mark in BAD)],
        "marks": [line.strip() for line in c if any(mark in line for mark in SENDER_MARKS)],
        "setting": [line.strip() for line in c if "State sender:" in line][:2],
    }


def run(env, rep, warmup=WARMUP, measure=MEASURE):
    rigrun.check_env(env)
    play_start, start, end = abload.run_window(env, warmup, measure, rep)
    return collect(play_start, start, end)


def run_window_with_bots(env, warmup, measure, rep, bots):
    """rigrun.run_window's calls in the same order, with the bots launched right after Play is up (run_window kills
    bots at its start and blocks to its end) and killed before Play stops. Returns (play start, window start, window
    end, {user: log path}); a failure is raised only after Play stopped."""
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
            logs[user] = r.launch_bot("step6", user, BOT_ARGS)
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
    """A bot's Bot net windows inside [start, end] (local times; the lines carry UTC) and its health counts."""
    if not os.path.exists(path):
        return {"exists": False, "net": [], "bad": {mark: 0 for mark in BOT_BAD}, "movement": 0}
    with open(path, encoding="utf-8", errors="replace") as f:
        lines = f.read().splitlines()
    lo, hi = start.astimezone(datetime.timezone.utc), end.astimezone(datetime.timezone.utc)
    net = [w for w in (p0phantom.parse_bot_net_window(line) for line in lines) if w and lo <= w["at"] <= hi]
    return {"exists": True, "net": net, "bad": {mark: sum(mark in line for line in lines) for mark in BOT_BAD},
            "movement": sum(BOT_MOVEMENT in line for line in lines)}


def run_bots(env, rep, warmup=WARMUP, measure=MEASURE):
    rigrun.check_env(env)
    play_start, start, end, logs = run_window_with_bots(env, warmup, measure, rep, BOTS)
    got = collect(play_start, start, end)
    got["bots"] = {user: read_bot_log(path, start, end) for user, path in logs.items()}
    return got


# ------------------------------------------------------------------ figures
def mean(values):
    values = list(values)
    return statistics.fmean(values) if values else float("nan")


def records(s):
    """A Send window's EntityState records (ro, u, other), 0 when it printed no records group."""
    rec = s["records"]
    return (rec["ro"], rec["u"], rec["other"]) if rec else (0, 0, 0)


def t119(s):
    group = [g for g in s["types"] if g["type"] == p0phantom.ENTITY_STATE]
    return group[0] if group else {"calls": 0, "bytes": 0}


def send_figures(got):
    """Per-run means over the Send windows: records/s, ro and u records/s, t119 bytes per record, refused, tooBig."""
    send = [s for s in got["send"] if s["seconds"] > 0]
    total_records = sum(sum(records(s)) for s in send)
    return {
        "records_per_s": mean(sum(records(s)) / s["seconds"] for s in send),
        "ro_per_s": mean(records(s)[0] / s["seconds"] for s in send),
        "u_per_s": mean(records(s)[1] / s["seconds"] for s in send),
        "bytes_per_record": sum(t119(s)["bytes"] for s in send) / total_records if total_records else float("nan"),
        "unread": sum(s["records"]["unread"] for s in send if s["records"]),
        "refused": sum(s["refused"] for s in send), "too_big": sum(s["too_big"] for s in send), "windows": len(send),
    }


def view_figures(got):
    ph = [p for p in got["phantom"] if p["view_err_n"]]
    return {"view_p50": mean(p["view_err_p50"] for p in ph), "view_p95": mean(p["view_err_p95"] for p in ph),
            "view_windows": len(ph), "pos_p95": [p["pos_err_p95"] for p in got["phantom"] if p["pos_err_n"]]}


def tick_figures(got):
    return {"move": mean(t["move_mean"] for t in got["ticks"]), "send": mean(t["send_mean"] for t in got["ticks"]),
            "parts": sum(t["parts"] for t in got["ticks"])}


def pct(a, b):
    return (b - a) / a * 100 if a else float("nan")


def within_pct(a, b, limit):
    return a == a and b == b and a != 0 and abs(pct(a, b)) <= limit


# ------------------------------------------------------------------ the runner
class Acceptance:
    def __init__(self):
        self.rep = abload.Report("Phase 0 step 6 rig acceptance (state sender, measure mode, CE-12 check)")
        self.failed = 0

    def check(self, ok, text):
        self.rep.note(f"  [{'ok' if ok else 'FAIL'}] {text}")
        self.failed += 0 if ok else 1

    def common(self, arm, got, label):
        """The checks every run gets: no fault line, and a kit-arm run shows no sender line at all."""
        self.check(not got["bad"], f"{label}: no Exception/State sender error/Phantoms error/Send meter error/"
                                   f"Too Big Packet line ({got['bad'][:3]})")
        if arm == "A":
            self.check(not got["marks"], f"{label}: kit arm logs no State sender, Sender window or CE-12 line "
                                         f"({got['marks'][:2]})")
        elif got["setting"]:
            self.rep.note(f"    {label}: {got['setting'][0][got['setting'][0].find('State sender:'):]}")

    def pairs(self, name, base, arms, count, runner=None):
        """count interleaved pairs of the two arms (the order alternates per pair); returns {arm: [run, ...]}."""
        runner = runner or run
        runs = {arm: [] for arm in arms}
        for pair in range(count):
            order = arms if pair % 2 == 0 else tuple(reversed(arms))
            for arm in order:
                got = runner(dict(base, **ARMS[arm]), self.rep)
                fig = send_figures(got)
                self.rep.note(f"    {name} pair {pair + 1} {arm}: records {fig['records_per_s']:.0f}/s "
                              f"(ro {fig['ro_per_s']:.0f} u {fig['u_per_s']:.0f}) {fig['bytes_per_record']:.2f} B/record, "
                              f"{fig['windows']} Send windows, {len(got['sender'])} Sender windows")
                self.common(arm, got, f"{name} pair {pair + 1} {arm}")
                runs[arm].append(got)
        return runs

    def arm_means(self, runs, figures):
        return {arm: {key: mean(figures(got)[key] for got in got_runs) for key in figures(got_runs[0])
                      if isinstance(figures(got_runs[0])[key], (int, float))}
                for arm, got_runs in runs.items()}

    def compare(self, name, means, base, other, key, limit, unit="/s"):
        a, b = means[base][key], means[other][key]
        ok = within_pct(a, b, limit)
        self.check(ok, f"{name}: {other} {key} {b:.2f}{unit} within +-{limit:g} % of {base} {a:.2f}{unit} "
                       f"({pct(a, b):+.2f} %)")
        return ok

    def sender_windows(self, got_runs):
        return [s for got in got_runs for s in got["sender"]]

    # -------------------------------------------------------------- gates
    def measure(self):
        self.rep.note("--- measure (gate 2): 50 character phantoms at V 49, arms off (A) and measure (M), 3 pairs")
        runs = self.pairs("measure", dict(QUIET, **CHARS), ("A", "M"), 3)
        means = self.arm_means(runs, send_figures)
        for key in ("records_per_s", "u_per_s"):
            self.compare("measure", means, "A", "M", key, 1)
        self.compare("measure", means, "A", "M", "bytes_per_record", 1, unit=" B")
        ro_a, ro_m = means["A"]["ro_per_s"], means["M"]["ro_per_s"]
        self.check(within_pct(ro_a, ro_m, 1) or abs(ro_m - ro_a) <= 20,
                   f"measure: M ro {ro_m:.1f}/s within +-1 % or +-20/s of A {ro_a:.1f}/s")
        for arm in ("A", "M"):
            figs = [send_figures(got) for got in runs[arm]]
            self.check(all(f["refused"] == 0 and f["too_big"] == 0 for f in figs),
                       f"measure: {arm} refused and tooBig 0 ({[(f['refused'], f['too_big']) for f in figs]})")
        sw = self.sender_windows(runs["M"])
        self.check(bool(sw) and all(s["setting"] == "measure" for s in sw),
                   f"measure: M Sender windows say measure ({sorted({s['setting'] for s in sw})})")
        self.check(bool(sw) and all(s["kit"] == 50 for s in sw), f"measure: M kit 50 ({sorted({s['kit'] for s in sw})})")
        rates = [s["builds"] / s["seconds"] for s in sw if s["seconds"] > 0]
        self.check(bool(rates) and all(19 <= x <= 21 for x in rates),
                   f"measure: M builds ~20/s ({[round(x, 1) for x in rates]})")
        self.rep.note(f"    M Sender ms/s total mean {mean(s['total_ms_per_s'] for s in sw):.2f}, records u "
                      f"{mean(s['records_u'] / s['seconds'] for s in sw):.0f}/s r "
                      f"{mean(s['records_r'] / s['seconds'] for s in sw):.1f}/s (encoded, not sent; r underestimates)")

    def parity(self):
        self.rep.note("--- parity (gate 3): 50 character phantoms at V 49, AOI timing, arms kit (A) and on (S), 3 pairs")
        runs = self.pairs("parity", dict(QUIET, **CHARS, MESH_AOI_TIMING="1"), ("A", "S"), 3)
        means = self.arm_means(runs, send_figures)
        self.compare("parity", means, "A", "S", "records_per_s", 2)
        self.rep.note(f"    expected about {EXPECTED_PARITY_RECORDS}/s; A {means['A']['records_per_s']:.0f}/s, "
                      f"S {means['S']['records_per_s']:.0f}/s")
        self.view_gate("parity", runs, ("view_p50", "view_p95"))
        pos = [x for got in runs["S"] for x in view_figures(got)["pos_p95"]]
        self.check(bool(pos) and max(pos) <= 0.50, f"parity: S posErr p95 <= 0.50 m in every window ({pos})")
        sw = self.sender_windows(runs["S"])
        self.check(bool(sw), f"parity: S logs Sender windows ({len(sw)})")
        self.check(all(s["stuck_reliable"] == 0 and s["errors"] == 0 for s in sw),
                   f"parity: S stuckReliable 0 and errors 0 ({[(s['stuck_reliable'], s['errors']) for s in sw if s['stuck_reliable'] or s['errors']]})")
        self.check(all(s["rewraps"] <= 1 for s in sw), f"parity: S rewraps <= 1 per window "
                                                       f"({sorted({s['rewraps'] for s in sw})})")
        self.check(all(s["hz"][20] == s["pairs"] for s in sw),
                   f"parity: S hz 20 equals pairs ({[(s['hz'][20], s['pairs']) for s in sw if s['hz'][20] != s['pairs']][:5]})")
        ticks = {arm: [tick_figures(got) for got in got_runs] for arm, got_runs in runs.items()}
        self.check(all(t["parts"] > 0 for figs in ticks.values() for t in figs),
                   "parity: Work ticks carry parts in every run (MESH_AOI_TIMING=1)")
        self.rep.note(f"    reported: A Work ticks move {mean(t['move'] for t in ticks['A']):.3f} ms, S move (residual) "
                      f"{mean(t['move'] for t in ticks['S']):.3f} ms, S send (residual + sender) "
                      f"{mean(t['send'] for t in ticks['S']):.3f} ms, A send {mean(t['send'] for t in ticks['A']):.3f} ms; "
                      f"S Sender ms/s build {mean(s['build_ms_per_s'] for s in sw):.2f} fanout "
                      f"{mean(s['fanout_ms_per_s'] for s in sw):.2f} send {mean(s['send_ms_per_s'] for s in sw):.2f} "
                      f"total {mean(s['total_ms_per_s'] for s in sw):.2f}, us/packet {mean(s['us_per_packet'] for s in sw):.2f}")

    def view_gate(self, name, runs, keys):
        for arm, got_runs in runs.items():
            counts = [view_figures(got)["view_windows"] for got in got_runs]
            self.check(all(counts), f"{name}: {arm} Phantom windows carry viewErr in every run ({counts})")
        means = self.arm_means(runs, view_figures)
        for key in keys:
            a, s = means["A"][key], means["S"][key]
            self.check(a == a and s == s and abs(s - a) <= 0.10,
                       f"{name}: S {key} {s:.3f} m within 0.10 m of A {a:.3f} m ({s - a:+.3f})")

    def eyes(self):
        self.rep.note("--- eyes (gate 4): 50 eye phantoms over 50 filler characters (V 50), arms A and S, 2 pairs")
        runs = self.pairs("eyes", dict(QUIET, **EYES), ("A", "S"), 2)
        means = self.arm_means(runs, send_figures)
        self.compare("eyes", means, "A", "S", "records_per_s", 2)
        self.view_gate("eyes", runs, ("view_p95",))

    def monsters(self):
        self.rep.note("--- monsters (gate 5): MESH_MONSTER_AREAS=all, 50 character phantoms at V 49, arms A and S, 2 pairs")
        runs = self.pairs("monsters", dict(COARSE, MESH_MONSTER_AREAS="all", **CHARS), ("A", "S"), 2)
        means = self.arm_means(runs, send_figures)
        self.compare("monsters", means, "A", "S", "records_per_s", 3)
        for i, got in enumerate(runs["S"]):
            sw = got["sender"]
            monsters = [s["owned"] - s["players"] for s in got["stats"]]
            kit = statistics.median(s["kit"] for s in sw) if sw else 0
            want = (statistics.median(monsters) if monsters else 0)
            self.check(bool(sw) and bool(monsters) and kit >= want,
                       f"monsters: S run {i + 1} kit {kit:g} >= kit characters {want:g} (Mesh stats owned - players, which already counts the 50 phantoms)")
            self.check(all(s["refusals"] == 0 and s["stuck_reliable"] == 0 and s["errors"] == 0 for s in sw),
                       f"monsters: S run {i + 1} refusals, stuckReliable and errors 0 "
                       f"({[(s['refusals'], s['stuck_reliable'], s['errors']) for s in sw][:5]})")

    def bots(self):
        env = dict(QUIET, **BOT_LOAD)
        self.rep.note("--- bots (gate 6): 50 synthetic players on (0,0) r 25, bots meshbot and meshbot2 standing at "
                      "(0,0); registration run, then arms A and S, 2 pairs")
        got = run_bots(env, self.rep, REGISTER_WARMUP, REGISTER_MEASURE)
        seen = {user: len(b["net"]) for user, b in got["bots"].items()}
        self.rep.note(f"    registration run (unmeasured): Bot net windows per bot {seen}")
        runs = self.pairs("bots", env, ("A", "S"), 2, runner=run_bots)
        means = self.arm_means(runs, send_figures)
        self.compare("bots", means, "A", "S", "records_per_s", 2)
        movement = {arm: sum(b["movement"] for got in got_runs for b in got["bots"].values())
                    for arm, got_runs in runs.items()}
        for arm, got_runs in runs.items():
            for i, got in enumerate(got_runs):
                for user in BOTS:
                    b = got["bots"].get(user)
                    label = f"bots: {arm} run {i + 1} {user}"
                    if b is None or not b["exists"]:
                        self.check(False, f"{label}: log exists")
                        continue
                    self.check(not any(b["bad"].values()), f"{label}: free of {', '.join(BOT_BAD)} ({b['bad']})")
                    players = [w["players_seen"] for w in b["net"]]
                    self.check(bool(players) and min(players) >= 50,
                               f"{label}: players seen >= 50 in every Bot net window ({players})")
        self.check(movement["S"] <= movement["A"],
                   f"bots: '{BOT_MOVEMENT}' in S {movement['S']} times, A {movement['A']} (S no more than A)")
        for arm, got_runs in runs.items():
            nets = [w for got in got_runs for b in got["bots"].values() for w in b["net"]]
            self.rep.note(f"    reported: {arm} Bot net in {mean(w['in_kb_per_s'] for w in nets):.1f} kB/s "
                          f"{mean(w['in_packets_per_s'] for w in nets):.0f} pkt/s over {len(nets)} windows")

    def ce12(self):
        self.rep.note("--- ce12 (gate 8): MESH_CE12_CHECK=1 without and with MESH_SENDER=on")
        for arm in ("A", "S"):
            env = dict(QUIET, MESH_CE12_CHECK="1", **ARMS[arm])
            got = run(env, self.rep, CE12_WARMUP, CE12_MEASURE)
            lines = got["ce12"]
            label = f"ce12 {'with' if arm == 'S' else 'without'} the sender"
            self.check(len(lines) == 1 and lines[0]["cell"] == "C" and lines[0]["shape"] == "pass",
                       f"{label}: C logs exactly one CE-12 check line with shape=pass "
                       f"({[(x['cell'], x['shape'], x['handler']) for x in lines]})")
            self.check(not got["bad"], f"{label}: no fault line ({got['bad'][:3]})")


def main(argv):
    groups = argv or list(GROUPS)
    unknown = [g for g in groups if g not in GROUPS]
    if unknown:
        raise SystemExit(f"unknown group(s) {', '.join(unknown)}; known: {', '.join(GROUPS)}")
    problems = rigrun.start_problems()
    if problems:
        raise SystemExit("\n".join(problems))
    acc = Acceptance()
    acc.rep.note(f"groups: {', '.join(groups)}; warmup {WARMUP} s, measure {MEASURE} s per run")
    try:
        for group in groups:
            try:
                getattr(acc, group)()
            except Exception as ex:  # a broken run fails its group; the other groups and the report still happen
                acc.check(False, f"{group}: the group stopped: {type(ex).__name__}: {ex}")
    finally:
        acc.rep.note(f"=== {acc.failed} check(s) failed")
        out = os.path.join(TOOLS, "..", "..", "Logs", "p0", "accept_step6.txt")
        os.makedirs(os.path.dirname(out), exist_ok=True)
        with open(out, "w", encoding="utf-8") as f:
            f.write(acc.rep.text() + "\n")
    return 1 if acc.failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
