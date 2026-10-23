"""The automatic policy past one hot cell, for regress.py (DESIGN 0.88; slices/slice3_design.md decisions 6, 8 and 13):

    python Tools/ServerMeshing/regress.py autoqueue autodefer

Both run the test map Dynamic with automatic splits on (autosplit's triggers, a 600 s cooldown so no family splits
twice, no monsters, no bots and no client) and drive the cells' load with the switchable burn file's comma list:
autoqueue: C and E hot at once (`C:14,E:14`). One operation runs world-wide, so the hotter is split first and the other
  waits: its dwell is cleared while the op runs and it is planned only after that split's done, never requested into
  the busy slot (no `Layout op refused`). Each split's cell is eased to 7 ms once it is done, so both are measured.
autodefer: MESH_HOST_CORES set so the host's headroom (0.8 x cores) lies between this host's cores in use with C hot
  alone and with eight more cells burning 24 ms (all the test map's other cells). C is deferred, and stays deferred,
  until the other cells' burn is removed; then it is planned and split.

regress.py hands itself in as `rig` (db_reset, request_play, wait_play, TEMP, MAP, ZONES, ...), so this module never
imports it; the unit tests hand in a fake. The readers are rigpolicy's and rigauto's.
"""
import collections
import time

import regress_auto
import regress_split
import rigauto
import riglayout
import rigpolicy
import rigsplit

SCENARIOS = ("autoqueue", "autodefer")
# DESIGN 0.90: autodefer's MESH_HOST_CORES is calibrated to this machine (host-headroom calibration); across two
# hosts C's family is judged by the other machine's cores in use.
TWO_HOST_SKIP = {"autodefer": "its MESH_HOST_CORES is calibrated to this machine's cores in use; across two hosts "
                              "C's family is judged by the other machine's"}
PERSIST_REASON = regress_split.PERSIST_REASON
ZONE_IDS = {"SW", "S", "SE", "W", "C", "E", "NW", "N", "NE", "C2"}
EXPECTED_ZONES = {"autoqueue": ZONE_IDS | {"C.1", "E.1"}, "autodefer": ZONE_IDS | {"C.1"}}

HOT_MS, WARM_MS, BUSY_MS = 14, 7, 24   # util about 0.9 / about 0.46 (autosplit's); a full core on a cell over its frame
SPLIT_UTIL, DWELL_S = 0.6, 20
COMMON_ENV = {"MESH_AUTO_OPS": "split", "MESH_SPLIT_UTIL": str(SPLIT_UTIL), "MESH_SPLIT_DWELL": str(DWELL_S),
              "MESH_SPLIT_BENEFIT": "1", "MESH_MONSTER_WEIGHT": "0", "MESH_NO_MONSTERS": "1", "MESH_OP_COOLDOWN": "600"}
QUEUE_CELLS = ("C", "E")
# Machine A (BRENDOAIBOX, 32 logical cores) on 2026-10-21, the editor and its ten cells in Play: the policy projected
# 5.7 cores with C burning 14 ms and 12.8 with all ten at 14 ms (0.8 core per burner, Logs/regress_gaps_calib.txt).
# With C at 14 and the eight other test-map cells at 24 ms (a core each) that is about 13.7 projected; cores are chosen
# so the threshold sits near the middle (rigpolicy.headroom_cores).
DEFER_COLD, DEFER_HOT = 6.1, 13.7
HOST_CORES = rigpolicy.headroom_cores(DEFER_COLD, DEFER_HOT)
BUSY_CELLS = ("SW", "S", "SE", "W", "E", "NW", "N", "NE")
IDLE_S = 20              # after Play: nothing planned while nothing burns
PLAN_TIMEOUT_S = 120     # the burn to a planned (or deferred) line: the dwell, the next report
SPLIT_TIMEOUT_S = regress_auto.SPLIT_TIMEOUT_S
MEASURED_TIMEOUT_S = regress_auto.MEASURED_TIMEOUT_S
SECOND_TIMEOUT_S = 120   # the first split's done to the second's planned line
DEFER_HOLD_S = 70        # deferred while the host is busy: a second deferral line (once a minute)
POLL_SLACK_S = 4         # the status is polled every 2 s
RELIEF_MIN_PCT = regress_auto.RELIEF_MIN_PCT
PARK = (-50.0, 82.0)
# What run() leaves for grade_layout: the splits that ran, in order (rigsplit.Split each) and their statuses.
PolicyRun = collections.namedtuple("PolicyRun", "name splits statuses")


def run(name, rep, rig, start, day, bots):
    if name == "autoqueue":
        return flow_queue(rep, rig, start, day)
    if name == "autodefer":
        return flow_defer(rep, rig, start, day)
    raise ValueError(f"unknown scenario {name}")


def play(rig, extra=None):
    """The Dynamic Play with the policy's triggers, no client, the burn file empty; returns the burn file's path."""
    rig.db_reset(PARK, PARK)
    burn = regress_auto.burn_path(rig)
    regress_auto.write_burn(burn, "", rig)
    env = dict(COMMON_ENV, MESH_CELL_MODE=f"{rig.MAP}:Dynamic", MESH_PROBE_BURN_MS="@" + burn, **(extra or {}))
    rig.request_play("none", PARK[0], PARK[1], env=env, enabled=False)
    rig.wait_play(60)
    time.sleep(IDLE_S)
    return burn


def wait_lines(rig, done, timeout):
    return regress_auto.wait_lines(rig, done, timeout)


def lines_now(rig):
    return regress_auto.status_lines(rig)


def grade_idle(rep, rig):
    lines = lines_now(rig)
    rep.check(not rigpolicy.split_ops(lines) and not rigpolicy.planned(lines) and not rigpolicy.deferrals(lines),
              "nothing planned, deferred or split before the burn")


def grade_split(rep, status, plan):
    """One automatic split's record: requested, done with an open drain, never aborted, frozen or supervised."""
    cell = plan["cell"] if plan else "?"
    rep.check(status["requested"] is not None and status["done"] and not status["aborted"] and not status["frozen"],
              f"split of {cell} done (states: {', '.join(status['states'][-4:]) or 'none'})")
    rep.check(status["drain"] == "open" and not status["stragglers"],
              f"{cell}'s drain evidence reached 0 left (drain {status['drain']}, {status['stragglers'] or 0} straggler(s))")


def grade_policy(rep, lines, allowed):
    texts = rigauto.policy_texts(lines)
    extra = rigauto.disallowed_policy(texts, allowed)
    rep.check(not extra, "policy lines only " + ", ".join(allowed) + (f" ({extra[0][:100]})" if extra else ""))
    refused = rigauto.refusals(lines)
    rep.check(not refused, "no request of the policy refused by the coordinator" + (f" ({refused[0][:100]})" if refused else ""))
    sup = rigauto.supervisor_texts(lines)
    rep.check(not sup, "no supervision line" + (f" ({sup[0][:100]})" if sup else ""))


def grade_measured(rep, lines, op, cell):
    m = rigpolicy.measured(lines).get(op)
    rep.check(m is not None and m["relief"] >= RELIEF_MIN_PCT,
              f"split {op} of {cell} measured with at least {RELIEF_MIN_PCT} % relief ({m or 'no measured line'})")


# ------------------------------------------------------------------ autoqueue (decisions 6, 13)
def flow_queue(rep, rig, start, day):
    burn = play(rig)
    grade_idle(rep, rig)
    burns = {cell: HOT_MS for cell in QUEUE_CELLS}
    regress_auto.write_burn(burn, rigpolicy.burn_text(burns), rig)
    lines = wait_lines(rig, lambda ls: any(rigsplit.op_outcome(rigpolicy.op_status(ls, op)) for op in rigpolicy.split_ops(ls)), PLAN_TIMEOUT_S + SPLIT_TIMEOUT_S)
    ops = rigpolicy.split_ops(lines)
    plans = rigpolicy.planned(lines)
    if not ops or not plans:
        rep.check(False, f"the policy planned and split a hot cell ({plans or 'no planned line'}, ops {ops})")
        return PolicyRun("autoqueue", [], [])
    first_op, first = ops[0], plans[0]
    first_done_at = time.time()
    rep.check(len(plans) == 1 and len(ops) == 1, f"one split while the first ran ({len(plans)} planned, ops {ops})")
    burns[first["cell"]] = WARM_MS
    regress_auto.write_burn(burn, rigpolicy.burn_text(burns), rig)
    other = next(c for c in QUEUE_CELLS if c != first["cell"])
    lines = wait_lines(rig, lambda ls: rigpolicy.index_of(ls, rigpolicy.is_planned(other)) is not None, SECOND_TIMEOUT_S)
    second_planned_at = time.time()
    done1, planned2 = rigpolicy.index_of(lines, rigpolicy.is_done(first_op)), rigpolicy.index_of(lines, rigpolicy.is_planned(other))
    rep.check(done1 is not None and planned2 is not None and planned2 > done1,
              f"{other} was planned after {first['cell']}'s split {first_op} was done (status lines {done1} and {planned2})")
    waited = second_planned_at - first_done_at
    rep.check(planned2 is not None and waited >= DWELL_S - POLL_SLACK_S,
              f"{other}'s dwell ran again after the slot came free: planned {waited:.0f} s after the first done, dwell {DWELL_S} s")
    lines = wait_lines(rig, lambda ls: len(rigpolicy.split_ops(ls)) > 1 and rigsplit.op_outcome(rigpolicy.op_status(ls, rigpolicy.split_ops(ls)[1])) is not None,
                       SPLIT_TIMEOUT_S)
    ops, plans = rigpolicy.split_ops(lines), rigpolicy.planned(lines)
    burns[other] = WARM_MS
    regress_auto.write_burn(burn, rigpolicy.burn_text(burns), rig)
    lines = wait_lines(rig, lambda ls: len(rigpolicy.measured(ls)) >= 2, MEASURED_TIMEOUT_S)
    statuses = [rigpolicy.op_status(lines, op) for op in ops]
    rep.check(len(ops) == 2 and sorted(p["cell"] for p in plans) == sorted(QUEUE_CELLS),
              f"two splits, one of each hot cell (ops {ops}, planned {[p['cell'] for p in plans]})")
    for status, plan in zip(statuses, plans):
        rep.check(plan["child"] == f"{plan['cell']}.1" and plan["util"] >= SPLIT_UTIL,
                  f"{plan['cell']} planned hot (util {plan['util']:g}) with child {plan['child']}")
        grade_split(rep, status, plan)
    for op, plan in zip(ops, plans):
        grade_measured(rep, lines, op, plan["cell"])
    grade_policy(rep, lines, tuple(f"split of {c} planned" for c in QUEUE_CELLS) + tuple(f"split {op} measured" for op in ops))
    busy = [r for r in rigauto.refusals(lines) if "in progress" in r]
    rep.check(not busy, "the waiting cell was never requested into the busy slot" + (f" ({busy[0][:80]})" if busy else ""))
    splits = [rigpolicy.split_of(rig.MAP, p, riglayout.TEST_MAP_RECTS[p["cell"]]) for p in plans]
    return PolicyRun("autoqueue", splits, statuses)


# ------------------------------------------------------------------ autodefer (decision 8)
def flow_defer(rep, rig, start, day):
    burn = play(rig, {"MESH_HOST_CORES": str(HOST_CORES)})
    grade_idle(rep, rig)
    busy = dict({"C": HOT_MS}, **{cell: BUSY_MS for cell in BUSY_CELLS})
    regress_auto.write_burn(burn, rigpolicy.burn_text(busy), rig)
    lines = wait_lines(rig, lambda ls: any(d["cell"] == "C" for d in rigpolicy.deferrals(ls)) or rigpolicy.planned(ls), PLAN_TIMEOUT_S)
    first = next((d for d in rigpolicy.deferrals(lines) if d["cell"] == "C"), None)
    rep.check(first is not None and first["cores"] == HOST_CORES and first["projected"] > 0.8 * HOST_CORES,
              f"the split of C was deferred with the host over 0.8 x {HOST_CORES} cores ({first or 'no deferral'})")
    hold_end = time.time() + DEFER_HOLD_S
    lines = wait_lines(rig, lambda ls: time.time() >= hold_end or bool(rigpolicy.planned(ls)), DEFER_HOLD_S + 5)
    deferred = [d for d in rigpolicy.deferrals(lines) if d["cell"] == "C"]
    rep.check(len(deferred) >= 2 and not rigpolicy.planned(lines) and not rigpolicy.split_ops(lines),
              f"C stayed deferred while the host was busy ({len(deferred)} deferral line(s) in {DEFER_HOLD_S} s, nothing planned)")
    regress_auto.write_burn(burn, rigpolicy.burn_text({"C": HOT_MS}), rig)
    released = len(rigauto.status_texts(lines))
    lines = wait_lines(rig, lambda ls: rigpolicy.planned(ls), PLAN_TIMEOUT_S)
    plans = rigpolicy.planned(lines)
    at = rigpolicy.index_of(lines, rigpolicy.is_planned("C"))
    rep.check(plans and plans[0]["cell"] == "C" and at is not None and at >= released,
              f"C was planned once the other cells' burn was removed ({plans[0] if plans else 'no planned line'})")
    if not plans:
        return PolicyRun("autodefer", [], [])
    lines = wait_lines(rig, lambda ls: rigpolicy.split_ops(ls) and rigsplit.op_outcome(rigpolicy.op_status(ls, rigpolicy.split_ops(ls)[0])) is not None,
                       SPLIT_TIMEOUT_S)
    ops = rigpolicy.split_ops(lines)
    status = rigpolicy.op_status(lines, ops[0]) if ops else rigsplit.parse_op_status([], "auto")
    regress_auto.write_burn(burn, rigpolicy.burn_text({"C": WARM_MS}), rig)
    grade_split(rep, status, plans[0])
    lines = wait_lines(rig, lambda ls: rigpolicy.measured(ls), MEASURED_TIMEOUT_S)
    rep.check(len(rigpolicy.split_ops(lines)) == 1, f"exactly one split ({rigpolicy.split_ops(lines)})")
    if ops:
        grade_measured(rep, lines, ops[0], "C")
    last = deferred[-1] if deferred else first
    if last:
        rep.notes.append(f"   host headroom: deferred at {last['projected']:g}/{HOST_CORES} cores (threshold {0.8 * HOST_CORES:g}); "
                         f"{len(rigpolicy.deferrals(lines))} deferral line(s) in all")
    grade_policy(rep, lines, ("split of C planned", "split split-1 measured")
                 + tuple(f"split of {c} deferred" for c in ("C",) + BUSY_CELLS))
    return PolicyRun("autodefer", [rigpolicy.split_of(rig.MAP, plans[0], riglayout.TEST_MAP_RECTS["C"])], [status])


# ------------------------------------------------------------------ the layout grade
def grade_layout(rep, rig, start, day, pr):
    """Every cell of the test map by rectangle against riglayout.timeline of the splits that ran (pairs from their
    statuses), no merge, retire, abort or freeze line, C2 as a Static map; policy lines allowed."""
    if not pr.splits:
        rep.check(False, "layout: no split to grade")
        return
    ops = [riglayout.split_op(s.base, s.child, s.axis, s.at, s.side, st.get("pairs", {})) for s, st in zip(pr.splits, pr.statuses)]
    expected = riglayout.timeline(riglayout.TEST_MAP_RECTS, ops)
    children = {s.child for s in pr.splits}

    def applies(zone, info):
        if zone not in expected:
            return [f"{zone}: a cell the scenario never made"]
        return riglayout.apply_violations(zone, info, expected[zone], child=zone in children,
                                          own_rect=riglayout.TEST_MAP_RECTS.get(zone), label=pr.name)

    regress_split.grade_layout(rep, rig, start, day, pr.splits[0], applies=applies,
                               ops=lambda zone, lines: sum(1 for line in lines if rigsplit.SPLIT_OP_PROBLEM.search(line)),
                               how_split=f"{len(ops)} automatic split(s) by rectangle", allow_policy=True)
