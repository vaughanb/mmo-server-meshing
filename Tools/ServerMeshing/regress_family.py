"""The three-cell family scenarios for regress.py (DESIGN 0.88; slices/slice3_design.md decisions 11 and 14):

    python Tools/ServerMeshing/regress.py mergechildren collapsesibling

Both start as regress_split's `split` of C at x = 0 (child C.1, no synthetics, no tester), then, once the family's
cooldown has passed, `split C.1 z 0 + C.2`: C.1 keeps x [0, 100) z [-100, 0) and C.2 takes x [0, 100) z [0, 100), so
the two children share a full edge and neither shares one with C.
mergechildren: two bots in C.1, three in C.2, three in C; `merge C.1 C.2` merges the two children and the one with more
  owned players (C.2) survives: C.1's bots migrate once more, C.1 drains and exits for good, the store keeps C and C.2.
collapsesibling: four bots in C.1, four in C, C.2 empty; C.2 runs with MESH_FAULT_DIE_ON_HELLO=C.2:1 behind a marker
  file, so its first process lives; after the second split the marker becomes a directory (every later incarnation
  then dies on its first hello) and C.2 is killed. The supervisor's three restarts fail, it queues the collapse, and
  since C shares no full edge with C.2 the collapse merges it into its sibling C.1.

regress.py hands itself in as `rig` (db_reset, request_play, launch_bot, zone_lines, ps, EXE, TEMP, ...), so this
module never imports it; the unit tests hand in a fake. The readers are rigfamily's and riglayout's.
"""
import collections
import json
import os
import shutil
import time

import regress_merge
import regress_split
import rigcentral
import rigfamily
import riglayout
import rigmerge
import rigsplit
import rigsplitcrash

SCENARIOS = ("mergechildren", "collapsesibling")
TWO_HOST_REASON = regress_split.TWO_HOST_REASON
PERSIST_REASON = regress_split.PERSIST_REASON
EXPECTED_ZONES = {name: {"SW", "S", "SE", "W", "C", "E", "NW", "N", "NE", "C2", rigfamily.FIRST, rigfamily.SECOND}
                  for name in SCENARIOS}

COOLDOWN_S = regress_merge.COOLDOWN_S + regress_merge.COOLDOWN_SLACK_S   # after each op's done, before the next
RETRY_WAIT_S = regress_merge.RETRY_WAIT_S
SPLIT_TIMEOUT_S = regress_split.SPLIT_TIMEOUT_S
APPLY_TIMEOUT_S = regress_split.APPLY_TIMEOUT_S
MERGE_TIMEOUT_S = regress_merge.MERGE_TIMEOUT_S
WINDOW_GUARD_S = regress_merge.WINDOW_GUARD_S
KILL_AFTER_DONE_S = 10
COLLAPSE_TIMEOUT_S = 540   # the kill to the collapse's done: down (20 s), three failed restarts (up to 70 s each), the collapse
STAY_GONE_S = regress_merge.STAY_GONE_S
SAMPLE_EVERY_S = regress_merge.SAMPLE_EVERY_S
RESULT_SLACK_S = regress_split.RESULT_SLACK_S
MAX_GAP_MS = regress_split.MAX_GAP_MS

Case = collections.namedtuple("Case", "kind observe_s env")
CASES = {
    "mergechildren": Case("merge", 600, {"MESH_MONSTER_AREAS": "only:Spawn_Interior_C"}),
    "collapsesibling": Case("collapse", 900, {"MESH_NO_MONSTERS": "1",
                                              rigfamily.HELLO_FAULT: rigfamily.hello_fault(rigfamily.SECOND)}),
}
# What run() hands grade_layout: the first split's SplitRun, the second split's seq and status, and the merge's (or
# collapse's) status with its survivor and retiring cell (None until each was sent).
FamilyRun = collections.namedtuple("FamilyRun", "name case r seq2 second merge survivor retiring")


def second_split():
    """The rigsplit.Split of `split C.1 z 0 + C.2` (C.1 owning x [0, 100) z [-100, 100) by then)."""
    given = rigsplit.split_rects(riglayout.TEST_MAP_RECTS[rigfamily.BASE], *rigfamily.FIRST_CUT)[1]
    return rigsplit.Split(None, rigfamily.FIRST, given, *rigfamily.SECOND_CUT, rigfamily.SECOND)


def wait_until(t):
    if time.time() < t:
        time.sleep(t - time.time())


def send(rep, rig, line_of, wait, previous=None):
    """A command with one retry (a new seq) when the coordinator refuses it for the family's cooldown. `line_of(seq)`
    is the command line, `wait(seq)` its parsed status. Returns (seq, status)."""
    seq = regress_merge.new_seq(previous)
    regress_split.write_command(rig.LAYOUT_COMMAND, line_of(seq))
    status = wait(seq)
    if status["refused"] and "cooldown" in status["refused"]:
        rep.notes.append(f"   {line_of(seq)} refused ({status['refused'][:80]}); retrying once")
        time.sleep(RETRY_WAIT_S)
        seq = regress_merge.new_seq(seq)
        regress_split.write_command(rig.LAYOUT_COMMAND, line_of(seq))
        status = wait(seq)
    return seq, status


def run(name, rep, rig, start, day, bots):
    """Runs one family scenario against the rig and records its checks on rep; appends every bot to `bots`. Returns a
    FamilyRun for grade_layout."""
    case = CASES[name]
    marker = os.path.join(rig.TEMP, rigfamily.marker_name(rigfamily.SECOND))
    if case.kind == "collapse":
        os.makedirs(rig.TEMP, exist_ok=True)
        clear_marker(marker)
        with open(marker, "w", encoding="utf-8") as f:
            f.write("armed later by regress_family\n")   # C.2's first process must live: the fault has 'fired'
    try:
        r = regress_split.start_split(name, rep, rig, start, day, bots, spec_name="split", observe_s=case.observe_s,
                                      plan=rigfamily.PLANS[name], load_plan="", enabled=False, env=dict(case.env))
        fr = FamilyRun(name, case, r, None, None, None, None, None)
        if not r.sent or r.applied is None:
            rep.check(False, f"{name}: the first split did not reach C's e3 apply; nothing more is sent")
            return fr
        fr = split_child(rep, rig, fr, start, day)
        if fr.second is None or not fr.second["done"]:
            return fr
        if case.kind == "merge":
            return flow_merge(rep, rig, fr, start, day)
        return flow_collapse(rep, rig, fr, marker, start, day)
    finally:
        if case.kind == "collapse":
            clear_marker(marker)


def clear_marker(path):
    if os.path.isdir(path):
        shutil.rmtree(path, ignore_errors=True)
    elif os.path.exists(path):
        os.remove(path)


def split_child(rep, rig, fr, start, day):
    """`split C.1 z 0 + C.2` once the family's cooldown has passed after the first split's done: accepted, done with an
    open drain and no supervision, and C.1 applying its kept half (e3)."""
    wait_until(fr.r.done_at + COOLDOWN_S)
    parent, child = rigfamily.FIRST, rigfamily.SECOND
    axis, at, side = rigfamily.SECOND_CUT
    seq, status = send(rep, rig, lambda s: rigsplit.command_line(s, parent, axis, at, side, child),
                       lambda s: regress_split.wait_op(rig.LAYOUT_COMMAND_STATUS, s, SPLIT_TIMEOUT_S), fr.r.seq)
    rep.check(status["requested"] is not None and not status["refused"],
              f"split {parent} at {axis}={at:g} accepted ({status['op'] or 'no op'}"
              + (f"; refused: {status['refused']}" if status["refused"] else "") + ")")
    rep.check(status["done"] and not status["aborted"] and not status["frozen"],
              f"split of {parent} done (states: {', '.join(status['states'][-4:]) or 'none'})")
    rep.check(status["drain"] == "open" and not status["stragglers"],
              f"{parent}'s drain evidence reached 0 left (drain {status['drain']}, {status['stragglers'] or 0} straggler(s))")
    supervision = rigsplit.supervision_lines(status)
    rep.check(not supervision, f"no supervision during the split of {parent}" + (f" ({supervision[0][:120]})" if supervision else ""))
    fr = fr._replace(seq2=seq, second=status)
    if status["done"]:
        split = second_split()._replace(map_id=rig.MAP)
        applied = regress_split.wait_applied(rig, split, start, day, APPLY_TIMEOUT_S)
        kept = rigsplit.split_rects(split.base_rect, split.axis, split.at, split.side)[0]
        rep.check(applied is not None, f"{parent} applied a layout owning and interested in {rigsplit.format_rect(kept)} only (e3)")
        fr = fr._replace(second=dict(status, done_at=time.time()))
    return fr


def merge_states_check(rep, status, survivor, retiring, wanted, label):
    problems = rigfamily.state_problems(status["states"], wanted, survivor, retiring)
    rep.check(not problems, f"{label} states in order" + (f" ({'; '.join(problems[:3])})" if problems else ""))


# ------------------------------------------------------------------ mergechildren
def flow_merge(rep, rig, fr, start, day):
    """Decision 11: `merge C.1 C.2`; the child with more owned players survives; the retiring child drains and exits for
    good; the store keeps the family's two live cells; every bot's migrations and handoffs."""
    r, plan = fr.r, fr.r.plan
    survivor = rigfamily.expected_survivor(plan)
    retiring = rigfamily.FIRST if survivor == rigfamily.SECOND else rigfamily.SECOND
    wait_until(fr.second["done_at"] + COOLDOWN_S)
    left = regress_merge.window_left(r, fr.case.observe_s)
    if left < WINDOW_GUARD_S:
        rep.check(False, f"merge: not sent, stand window too short ({left:.0f} s left, needs {WINDOW_GUARD_S:g})")
        return fr
    rep.notes.append(f"   merge sent with {left:.0f} s left of the earliest R-bot's stand window")
    counts = {cell: regress_merge.count_processes(rig, cell) for cell in (rigfamily.FIRST, rigfamily.SECOND)}
    rep.check(all(n == 1 for n in counts.values()), f"positive control: one process each of C.1 and C.2 before the merge ({counts})")
    seq, status = send(rep, rig, lambda s: rigmerge.command_line(s, rigfamily.FIRST, rigfamily.SECOND),
                       lambda s: regress_merge.wait_merge(rig.LAYOUT_COMMAND_STATUS, s, MERGE_TIMEOUT_S), fr.seq2)
    fr = fr._replace(merge=status, survivor=survivor, retiring=retiring)
    rep.check(status["requested"] == (survivor, retiring) and not status["collapse"] and not status["refused"],
              f"merge C.1 C.2 accepted with {survivor} (more owned players: {rigfamily.owned_players(plan)}) surviving "
              f"({' '.join(status['requested'] or ()) or 'no request'}" + (f"; refused: {status['refused']}" if status["refused"] else "") + ")")
    merge_states_check(rep, status, survivor, retiring, rigfamily.MERGE_STATES, "merge e1 .. e3, done, no tidy:")
    bad = rigmerge.bad_states(status)
    rep.check(not bad, "merge neither aborted, frozen, reverted, exited before drained nor straggling" + (f" ({'; '.join(bad[:3])})" if bad else ""))
    supervision = rigsplit.supervision_lines(status)
    rep.check(not supervision, "no supervision during the merge" + (f" ({supervision[0][:120]})" if supervision else ""))
    if not status["done"]:
        return fr
    union = riglayout.final_owned(riglayout.TEST_MAP_RECTS, rigfamily.ops(fr.name, merge_pairs={"e1": None},
                                                                          survivor=survivor, retiring=retiring))
    tidy = regress_merge.wait_tidy(rig, rigsplit.Split(rig.MAP, survivor, union[survivor], None, None, None, None),
                                   status["pairs"].get("e3"), start, day, regress_merge.TIDY_APPLY_TIMEOUT_S)
    rep.check(tidy is not None, f"{survivor} applied the merge's e3 {status['pairs'].get('e3')} owning and interested in {rigsplit.format_rect(union[survivor])}")
    regress_merge.grade_exit(rep, rig, retiring, start, day)
    grade_store(rep, rig, {c: union[c] for c in (rigfamily.BASE, survivor)}, [retiring])
    results = wait_results(fr)
    grade_bots(rep, rig, fr, results, start, day)
    regress_merge.grade_retiring(rep, rig, retiring, start, day)
    regress_merge.grade_prunes(rep, rig, retiring, status["pairs"].get("e3"), start, day)
    return fr


def wait_results(fr):
    r = fr.r
    stands = [r.stood[log] for log in r.logs.values() if log in r.stood] or [time.time()]
    return regress_split.wait_results(list(r.logs.values()), max(10.0, max(stands) + fr.case.observe_s + RESULT_SLACK_S - time.time()))


def grade_bots(rep, rig, fr, results, start, day):
    """Every bot: the migrations its hops make (rigfamily.expected_hops), ready, under a second, no fallback, no
    session lost; each hop's Handoff START logged once by the cell it left."""
    cells = {cell: rig.zone_lines(cell, start, day, rig.MAP) for cell in (rigfamily.BASE, rigfamily.FIRST, rigfamily.SECOND)}
    for b in fr.r.plan:
        log = fr.r.logs[b.user]
        hops = rigfamily.expected_hops(fr.name, b.point, fr.survivor or rigfamily.SECOND)
        result = rigsplit.parse_result(results.get(log, ""))
        lines = regress_split.read_lines(log)
        problems = rigsplit.migration_problems(result, len(hops), MAX_GAP_MS) + rigcentral.bot_problems(result, None, 0, lines)
        hops_back = rigmerge.fell_back(lines).count(True)
        if hops_back:
            problems.append(f"{hops_back} migration(s) with fellBack=True")
        for src, dst in hops:
            n = rig.count(cells[src], rigsplit.handoff_start(b.character, src, dst).pattern)
            if n != 1:
                problems.append(f"{src} logged {n} Handoff START zone {src}->{dst}, expected 1")
        route = " -> ".join([rigfamily.BASE] + [d for _, d in hops]) if hops else "stays on C"
        rep.check(not problems, f"{b.user}: {len(hops)} migration(s) ({route})" + (f" ({'; '.join(problems)})" if problems else ""))


def read_store(rep, rig):
    try:
        with open(rig.COORDINATOR_STORE, encoding="utf-8-sig") as f:
            return json.load(f)
    except (OSError, ValueError) as ex:
        rep.check(False, f"coordinator store unreadable: {ex}")
        return None


def grade_store(rep, rig, live, gone, fenced=None):
    """The store keeps the family's live cells (Active, owning their rectangles), the gone ones Retired or absent, no
    operation; `fenced` (collapsesibling) a cell the store must fence."""
    store = read_store(rep, rig)
    if store is None:
        return
    problems = rigfamily.store_problems(store, rig.MAP, live, gone)
    rep.check(not problems, f"the store keeps {', '.join(sorted(live))} and not {', '.join(gone)}" + (f" ({'; '.join(problems[:3])})" if problems else ""))
    if fenced:
        n = len(rigfamily.fenced(store, fenced))
        rep.check(n >= 1, f"the store fences {fenced} ({n} incarnation(s))")


# ------------------------------------------------------------------ collapsesibling
def flow_collapse(rep, rig, fr, marker, start, day):
    """Decision 14: C.2 killed with its restarts failing; three restarts requested and failed, the collapse queued and
    run into C.1 (C shares no full edge with C.2); C.2 never serves again; the store, the bots."""
    cell, sibling = rigfamily.SECOND, rigfamily.FIRST
    wait_until(fr.second["done_at"] + KILL_AFTER_DONE_S)
    try:
        os.remove(marker)
        os.makedirs(marker)   # every C.2 started from now on dies on its first hello
    except OSError as ex:
        rep.check(False, f"the fault marker could not be armed ({ex})")
        return fr
    try:
        killed = rigmerge.parse_count(rig.ps(rigsplitcrash.kill_script(rig.EXE, cell)))
    except (RuntimeError, ValueError) as ex:
        rep.check(False, f"{cell}: the kill failed ({str(ex)[:120]})")
        return fr
    kill_at = time.time()
    rep.check(killed == 1, f"killed {killed} process(es) with '{rigmerge.channel_filter(cell)}', expected 1")
    samples, end = [], kill_at + COLLAPSE_TIMEOUT_S
    while True:
        samples.append(regress_merge.count_processes(rig, cell))
        status = rigmerge.parse_merge_status(regress_split.read_lines(rig.LAYOUT_COMMAND_STATUS), fr.seq2)
        if rigmerge.merge_outcome(status) or time.time() >= end:
            break
        time.sleep(SAMPLE_EVERY_S)
    fr = fr._replace(merge=status, survivor=sibling, retiring=cell)
    rep.check(status["done"], f"the collapse of {cell} reached done within {COLLAPSE_TIMEOUT_S} s of the kill ({time.time() - kill_at:.0f} s)")
    events = rigsplitcrash.parse_supervisor(regress_split.read_lines(rig.LAYOUT_COMMAND_STATUS), fr.seq2)
    problems = rigfamily.supervisor_problems(events, cell)
    rep.check(not problems, f"supervisor: {cell} restart 1..3 requested and failed, then its collapse queued"
              + (f" ({'; '.join(problems)})" if problems else ""))
    reasons = rigfamily.failure_reasons(events, cell)
    if reasons:
        rep.notes.append(f"   {cell}'s restarts failed: {'; '.join(r[:60] for r in reasons)}")
    rep.check(status["requested"] == (sibling, cell) and status["collapse"],
              f"collapse requested {sibling} {cell} (the sibling, C shares no full edge) ({' '.join(status['requested'] or ()) or 'no request'})")
    merge_states_check(rep, status, sibling, cell, rigfamily.COLLAPSE_STATES, "collapse e1 .. e3, done, no tidy:")
    bad = [s for s in (status["aborted"], status["frozen"]) if s] + (["reverted broadcast"] if status["reverted"] else [])
    rep.check(not bad, "collapse neither aborted, frozen nor reverted" + (f" ({bad[0]})" if bad else ""))
    rep.check(status["exited_before"] == cell, f"the collapse saw {cell} gone before drained ({status['exited_before'] or 'not logged'})")
    rep.notes.append(f"   {cell}: process counts from the kill to the collapse's done {rigsplitcrash.process_sequence(samples)}")
    if not status["done"]:
        return fr
    after = regress_merge.sample_exit(rig, cell, time.time())
    rep.check(not after["error"] and after["gone_at"] is not None and not after["came_back"],
              f"{cell}: no process for {STAY_GONE_S:g} s after the collapse" + (" (one came back)" if after["came_back"] else ""))
    lives = rigsplit.lifetimes(rig.zone_lines(cell, start, day, rig.MAP, suffixes=("info",)), cell)
    rep.check(len(lives) == 1, f"{cell}: {len(lives)} process lifetime(s) that applied a layout, expected 1 (no restart served)")
    union = riglayout.final_owned(riglayout.TEST_MAP_RECTS, rigfamily.ops(fr.name, merge_pairs={"e1": None}))
    grade_store(rep, rig, {c: union[c] for c in (rigfamily.BASE, sibling)}, [cell], fenced=cell)
    results = wait_results(fr)
    grade_bots(rep, rig, fr, results, start, day)
    prunes = {zone: rigmerge.prunes(rig.zone_lines(zone, start, day, map_id), cell) for zone, map_id in rig.graded_zones(day)
              if zone != cell}
    rep.notes.append(f"   prunes of {cell} (records released per cell): {dict((z, n) for z, n in prunes.items() if n)}")
    return fr


# ------------------------------------------------------------------ the layout grade
def grade_layout(rep, rig, start, day, fr):
    """regress.grade_layout for a family scenario: every cell of the test map by rectangle against riglayout.timeline
    of the ops that ran (pairs from the statuses), the op lines rigmerge allows (and, for the collapse, C.2's FAULT
    lines), C2 as a Static map, no policy line."""
    r = fr.r
    pairs1 = (r.status or {}).get("pairs", {})
    pairs2 = (fr.second or {}).get("pairs", {})
    merge_pairs = (fr.merge or {}).get("pairs", {}) if fr.merge and fr.merge.get("op") else {}
    ops = rigfamily.ops(fr.name, pairs1, pairs2 if fr.second else None, merge_pairs,
                        survivor=fr.survivor or rigfamily.SECOND, retiring=fr.retiring or rigfamily.FIRST)
    if not fr.second:
        ops = ops[:1]
    expected = riglayout.timeline(riglayout.TEST_MAP_RECTS, ops)
    retiring = fr.retiring or rigfamily.FIRST
    children = {rigfamily.FIRST, rigfamily.SECOND}

    def applies(zone, info):
        if zone not in expected:
            return [f"{zone}: a cell the scenario never made"]
        return riglayout.apply_violations(zone, info, expected[zone], child=zone in children,
                                          own_rect=riglayout.TEST_MAP_RECTS.get(zone), label=f"the {fr.case.kind} case")

    def op_lines(zone, lines):
        found = rigmerge.disallowed_op_lines(zone, lines, retiring)
        if fr.case.kind == "collapse" and zone == rigfamily.SECOND:
            found = [line for line in found if not rigsplitcrash.FAULT.search(line)]
        return len(found)

    regress_split.grade_layout(rep, rig, start, day, r.split, applies=applies, ops=op_lines,
                               how_split=f"family map ({fr.case.kind} case): the layouts of {len(ops)} op(s) by rectangle")
