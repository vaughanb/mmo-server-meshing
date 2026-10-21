"""Slice 5's central-restart scenarios for regress.py (Docs/ServerMeshing/slices/slice5_design.md decision 16), on the
split-process topology (regress_procs: the central, login and database manager one build process, the spawner another,
the editor a client only):

    python Tools/ServerMeshing/regress.py splitcentral splitcentral_merge splitcentral_base

splitcentral: regress_split's `split` of C at x = 0 (child C.1) with the eight stand bots and the tester in R; 10 s after
  the split's done the central is killed and started again: it adopts the stored layout, answers every hello with it,
  re-adds C.1's channel, and a bot logging in afterwards is routed to C.1; nobody is kicked.
splitcentral_merge: the same split, then `merge C C.1`; the moment e2 is broadcast the central is killed and started
  again: the merge resumes at its drain gate (or retire) and runs to done under the new central.
splitcentral_base: the same split with no tester and MESH_FAULT_DIE_ON_HELLO=C:2; the central is killed and started
  again, and C dies on its hello to the new central (while the map still waits): the kit's spawner restarts C, which
  comes back gated and is answered with its kept half; it never re-owns R.

regress.py hands itself in as `rig` (as for regress_split and regress_splitcrash: request_play, launch_bot, zone_lines,
central_lines, ps, EXE, ...), so this module never imports it; the unit tests hand in a fake. The graders are rigcentral's.
"""
import collections
import datetime
import json
import re
import time

import regress_merge
import regress_procs
import regress_split
import regress_splitcrash
import rigcentral
import rigmerge
import rigsplit
import rigsplitcrash
import rigtools

SCENARIOS = ("splitcentral", "splitcentral_merge", "splitcentral_base")
TWO_HOST_REASON = ("runs the central and the spawner as build processes on this host and kills and restarts the central; "
                   "the other machine runs no spawner and its cell could not follow a central restarted here")
PERSIST_REASON = regress_split.PERSIST_REASON
ZONE_IDS = {"SW", "S", "SE", "W", "C", "E", "NW", "N", "NE", "C2"}
EXPECTED_ZONES = {name: ZONE_IDS | {"C.1"} for name in SCENARIOS}

MONSTER_AREAS = regress_splitcrash.MONSTER_AREAS
PLANNED_MONSTERS = regress_splitcrash.PLANNED_MONSTERS
OBSERVE_S = 600            # every bot's and the tester's stand window
TESTER = ("stand", 60.0, 70.0)
LATE_BOT = ("meshbot", 60.0, -60.0)   # splitcentral: logs in after the restart, in R (its row sits at the tester's point)
LATE_BOT_OBSERVE_S = 120
KILL_AFTER_DONE_S = 10
CENTRAL_DOWN_S = 8         # past LiteNetLib's 5 s disconnect timeout: every cell sees the drop (decision 16, D)
ADOPT_TIMEOUT_S = 90       # the restarted central's adoption line (expected 25-40 s after the kill)
DB_RECONNECT_S = 60        # every cell's database connection re-opened within this of the restart
MERGE_E2_TIMEOUT_S = regress_merge.MERGE_TIMEOUT_S
MERGE_E2_POLL_S = 0.5
MERGE_END_S = 300          # the resumed merge's done (the database outage stalls the drain's handoffs for up to 40 s)
ANSWER_TIMEOUT_S = 120     # base: C's second lifetime applying the central's answer
RESULT_SLACK_S = 90
STAND_TIMEOUT_S = 60
LATE_STAND_TIMEOUT_S = 120  # the late bot's stand wait; its login retries are reported (0 since DESIGN 0.84's connect fix)
LOGIN_RETRY = "Login timed out (attempt"

Case = collections.namedtuple("Case", "kind enabled env")
CASES = {
    "splitcentral": Case("split", True, {}),
    "splitcentral_merge": Case("merge", True, {}),
    "splitcentral_base": Case("base", False, {"MESH_FAULT_DIE_ON_HELLO": "C:2"}),
}
# What run() hands grade_layout: the split phase's SplitRun, the case, the merge's pairs (e1, e2 from the status file;
# e3, tidy from the central's second lifetime), the adopted pair and the kill time.
CentralRun = collections.namedtuple("CentralRun", "name case r merge_pairs adopted kill_at")


def env_of(case):
    return {"MESH_MONSTER_AREAS": MONSTER_AREAS, **case.env}


def run(name, rep, rig, start, day, bots):
    """Runs one central-restart scenario against the rig (regress) and records its checks on rep; appends every bot to
    `bots`. Returns a CentralRun for grade_layout."""
    case = CASES[name]
    # The tester stands as long as the bots (OBSERVE_S): the runner's default 100 s ended before the merge variant's
    # drain handed it back (its RESULT counted the split's migration only).
    kwargs = {"tester": TESTER, "observe_seconds": OBSERVE_S} if case.enabled else {}
    r = regress_split.start_split(name, rep, rig, start, day, bots, spec_name="split", observe_s=OBSERVE_S,
                                  plan=regress_splitcrash.stand_plan(), load_plan="", enabled=case.enabled, env=env_of(case),
                                  **kwargs)
    cr = CentralRun(name, case, r, {}, None, None)
    if not r.sent or r.applied is None:
        rep.check(False, f"{name}: the split did not reach {r.spec.base}'s e3 apply; the central is not restarted")
        return cr
    return {"split": flow_split, "merge": flow_merge, "base": flow_base}[case.kind](rep, rig, cr, start, day, bots)


# ------------------------------------------------------------------ the central's lifetimes
def lifetimes(rig, start, day):
    """The central's kit log lines (every severity) since the scenario start, in stamp order, split into lifetimes."""
    return rigcentral.lifetimes(regress_splitcrash.by_stamp(rig.central_lines(start, day)))


def wait_lifetime(rig, start, day, index, pred, timeout):
    """The central's lifetime `index` (0-based) once `pred(lines)` holds for it, or None at the timeout."""
    end = time.time() + timeout
    while True:
        lives = lifetimes(rig, start, day)
        if len(lives) > index and pred(lives[index]):
            return lives[index]
        if time.time() >= end:
            return None
        time.sleep(2)


def wait_adoption(rig, start, day, timeout):
    """The second lifetime's adoption of the test map, as rigcentral.adoptions reads it, or None."""
    lines = wait_lifetime(rig, start, day, 1, lambda ls: any(a["map"] == rig.MAP for a in rigcentral.adoptions(ls)), timeout)
    if lines is None:
        return None
    return next(a for a in rigcentral.adoptions(lines) if a["map"] == rig.MAP)


def restart(rep, rig, cr, start, day):
    """Kill the central, wait CENTRAL_DOWN_S, start it again; the adoption line of the second lifetime. Returns (adoption, kill_at)."""
    try:
        killed, lifetime, kill_at = regress_procs.restart_central(rig, cr.name, CENTRAL_DOWN_S)
    except (RuntimeError, ValueError) as ex:
        rep.check(False, f"the central could not be killed and restarted ({str(ex)[:120]})")
        return None, time.time()
    rep.check(killed == 1, f"killed {killed} central process(es), expected 1; lifetime {lifetime} started after {CENTRAL_DOWN_S} s")
    adoption = wait_adoption(rig, start, day, ADOPT_TIMEOUT_S)
    rep.check(adoption is not None, f"the restarted central adopted {rig.MAP}'s layout within {ADOPT_TIMEOUT_S} s"
              + (f" ({adoption['pair']} from {adoption['source']}, {adoption['entries']} entries, {time.time() - kill_at:.0f} s after the kill)" if adoption else ""))
    return adoption, kill_at


def grade_second_lifetime(rep, rig, second, allow_ops=False, allow_no_server=False):
    """Decision 16: the second lifetime's summary line names the map (it is written at Start, before any hello, so a cut
    map is Waiting there; the adoption line is graded by `restart`), nothing froze or re-opened, no refusal; without
    `allow_ops` no operation line either."""
    summary = rigcentral.summary(second)
    rep.check(summary is not None and any(f"{rig.MAP} {o}" in summary[1] for o in ("Waiting", "AdoptStored")),
              f"the restarted central's summary says {rig.MAP} Waiting or AdoptStored ({summary[1] if summary else 'no summary line'})")
    frozen = rigcentral.frozen_lines(second)
    reopened = [line for line in second if rigcentral.REOPENS.search(line)]
    rep.check(not frozen and not reopened, "nothing frozen and no re-opened recovery after the restart"
              + (f" ({rigcentral.payload((frozen + reopened)[0])[:120]})" if frozen or reopened else ""))
    refused = rigcentral.refusals(second)
    rep.check(not refused, "no 'Layout op refused' after the restart" + (f" ({refused[0][:100]})" if refused else ""))
    if not allow_ops:
        ops = rigcentral.op_states(second, "Split") + rigcentral.op_states(second, "Merge")
        rep.check(not ops, "no operation line after the restart (the split was done)" + (f" ({ops[0]})" if ops else ""))
    no_server = [line for line in second if rigcentral.LOGIN_NO_SERVER.search(line)]
    if no_server and not allow_no_server:
        rep.check(False, f"{len(no_server)} 'Login routing: no server for cell' line(s) after the restart ({rigcentral.payload(no_server[0])[:100]})")


def grade_cells(rep, rig, cr, adopted, kill_at, start, day):
    """Every cell of the test map and C2: two hellos (one per central), the adopted pair answered once as already
    applied, the database connection lost and re-opened after the kill, one process lifetime."""
    kill = datetime.datetime.fromtimestamp(kill_at)
    deadline = kill_at + CENTRAL_DOWN_S + DB_RECONNECT_S
    time.sleep(max(0.0, deadline - time.time()))
    for zone, map_id in rig.ZONES:
        if cr.case.kind == "base" and zone == cr.r.spec.base:
            continue   # the base dies and comes back without a layout: its lines are the base case's own grades
        lines = rig.zone_lines(zone, start, day, map_id)
        hellos = rigcentral.hellos_sent(lines, zone)
        answered = rigcentral.answers(lines, zone)
        rep.check(len(hellos) == 2, f"{zone}: {len(hellos)} LayoutHello sent (one per central incarnation), expected 2")
        rep.check(answered.count(adopted) == 1, f"{zone}: 'Layout answer {adopted} on {zone}: already applied' once ({answered or 'none'})")
        lost, opened = rigcentral.database_reconnect(lines, after=kill)
        rep.check(lost >= 1 and opened >= 1, f"{zone}: database connection lost ({lost}) and re-opened ({opened}) after the kill, within {DB_RECONNECT_S} s of the restart")
    child = cr.r.spec.child
    child_lines = rig.zone_lines(child, start, day, rig.MAP)
    rep.check(len(rigcentral.hellos_sent(child_lines, child)) == 2, f"{child}: 2 LayoutHello sent")
    rep.check(rigcentral.answers(child_lines, child).count(adopted) == 1, f"{child}: the adopted pair answered once as already applied")


def grade_store_adopted(rep, rig, adopted, split):
    """After the restart the store holds the adopted pair with the family's two entries and no operation."""
    try:
        with open(rig.COORDINATOR_STORE, encoding="utf-8-sig") as f:
            store = json.load(f)
    except (OSError, ValueError) as ex:
        rep.check(False, f"coordinator store unreadable after the restart: {ex}")
        return
    pair = store.get("broadcast", {}).get("pair", {})
    text = f"{int(pair.get('coordinatorIncarnation', 0)):x}/{int(pair.get('version', 0))}"
    ids = sorted(rigmerge.family_entries(store, split.map_id, split.base, [split.base, split.child]))
    rep.check(text == adopted and ids == sorted([split.base, split.child]) and not store.get("hasOperation"),
              f"the store holds the adopted {adopted} with {split.base} and {split.child} and no operation ({text}, {ids}, operation {bool(store.get('hasOperation'))})")


def results_of(r, results):
    return {b.user: (b, rigsplit.parse_result(results.get(r.logs[b.user], "")), regress_split.read_lines(r.logs[b.user])) for b in r.plan}


def wait_results(r, extra_logs=()):
    stands = [r.stood[log] for log in r.logs.values() if log in r.stood] or [time.time()]
    logs = list(r.logs.values()) + list(extra_logs)
    return regress_split.wait_results(logs, max(10.0, max(stands) + OBSERVE_S + RESULT_SLACK_S - time.time()))


def grade_bots(rep, rig, r, results, migrations_r, migrations_p, relogins_p=0):
    for user, (b, result, lines) in results_of(r, results).items():
        far = b.side == regress_split.SIDE
        problems = rigcentral.bot_problems(result, migrations_r if far else migrations_p, 0 if far else relogins_p, lines)
        rep.check(not problems, f"{user}: migrations {migrations_r if far else migrations_p}, relogins {0 if far else relogins_p}, never kicked"
                  + (f" ({'; '.join(problems)})" if problems else ""))


def grade_tester(rep, rig, migrations):
    """The editor's tester: its RESULT passed with the migrations expected and no re-login."""
    try:
        st = rig.wait_status(lambda s: s.get("finished") == "True", RESULT_SLACK_S, "the tester")
    except TimeoutError:
        rep.check(False, "the tester wrote no RESULT")
        return
    result = rigsplit.parse_result(st.get("result", ""))
    problems = rigsplitcrash.bot_problems(result, migrations, 0)
    rep.check(result.get("passed") and not problems, f"tester: RESULT passed with migrations={migrations} relogins=0"
              + (f" ({'; '.join(problems) or st.get('result', '')[:100]})" if not result.get("passed") or problems else ""))


# ------------------------------------------------------------------ splitcentral
def flow_split(rep, rig, cr, start, day, bots):
    r, spec = cr.r, cr.r.spec
    time.sleep(max(0.0, r.done_at + KILL_AFTER_DONE_S - time.time()))
    adoption, kill_at = restart(rep, rig, cr, start, day)
    expected = r.status.get("pairs", {}).get("e3")
    settle = rigsplitcrash.crash_status(regress_split.read_lines(rig.LAYOUT_COMMAND_STATUS), r.seq).get("settle")
    expected = settle or expected
    if adoption is None:
        return cr._replace(kill_at=kill_at)
    rep.check(adoption["pair"] == expected and adoption["source"] == "the store" and sorted(adoption["ids"]) == [spec.base, spec.child],
              f"adopted {adoption['pair']} from {adoption['source']} with entries {adoption['ids']} (expected {expected} from the store, {spec.base} and {spec.child})")
    second = lifetimes(rig, start, day)[1]
    grade_second_lifetime(rep, rig, second)
    # A login after the restart routes to the child.
    user, x, z = LATE_BOT
    log = rig.launch_bot(cr.name, user, ["-meshAutoMode", "stand", "-meshWalkX", f"{x:g}", "-meshWalkZ", f"{z:g}"],
                         channel=spec.channel, observe_seconds=LATE_BOT_OBSERVE_S, line_x=spec.at)
    bots.append(("stand", log))
    standing = regress_split.wait_standing([log], LATE_STAND_TIMEOUT_S)
    retries = sum(LOGIN_RETRY in line for line in regress_split.read_lines(log))
    rep.check(standing[log] is not None, f"the late bot {user} logged in and stood after the restart ({standing[log]}"
              + f", {retries} login retr{'y' if retries == 1 else 'ies'} after a request timeout)")
    second = lifetimes(rig, start, day)[1]
    routes = rigcentral.login_routes(second, user + "Char")
    rep.check(routes and routes[-1][1] == spec.child, f"the late bot's login was routed to {spec.child} ({routes or 'no routing line'})")
    kill = datetime.datetime.fromtimestamp(kill_at)
    on_child = [line for line in rig.zone_lines(spec.child, start, day, rig.MAP) if rigtools.PLAYER_REGISTERED.search(line) and f"player registered {user}Char " in line and (rigtools.stamp_datetime(line) or kill) >= kill]
    on_base = [line for line in rig.zone_lines(spec.base, start, day, rig.MAP) if rigtools.PLAYER_REGISTERED.search(line) and f"player registered {user}Char " in line and (rigtools.stamp_datetime(line) or kill) >= kill]
    rep.check(on_child and not on_base, f"{user}Char registered on {spec.child} ({len(on_child)}) and never on {spec.base} ({len(on_base)}) after the restart")
    grade_cells(rep, rig, cr, adoption["pair"], kill_at, start, day)
    results = wait_results(r, [log])
    grade_bots(rep, rig, r, results, 1, 0)
    late = rigsplit.parse_result(results.get(log, ""))
    problems = rigcentral.bot_problems(late, 0, 0, regress_split.read_lines(log))
    rep.check(not problems, f"{user}: no migration, no re-login" + (f" ({'; '.join(problems)})" if problems else ""))
    grade_tester(rep, rig, 1)
    for zone, map_id in rig.ZONES:
        lives = len(rigsplit.lifetimes(rig.zone_lines(zone, start, day, map_id, suffixes=("info",))))
        rep.check(lives == 1, f"{zone}: {lives} process lifetime(s), expected 1")
    grade_store_adopted(rep, rig, adoption["pair"], r.split)
    return cr._replace(adopted=adoption["pair"], kill_at=kill_at)


# ------------------------------------------------------------------ splitcentral_merge
def wait_e2(rig, seq, timeout):
    """The merge command's status polled every MERGE_E2_POLL_S until e2 is broadcast (or the op ended), or the timeout."""
    end = time.time() + timeout
    while True:
        status = rigmerge.parse_merge_status(regress_split.read_lines(rig.LAYOUT_COMMAND_STATUS), seq)
        if status["pairs"].get("e2") or rigmerge.merge_outcome(status) or time.time() >= end:
            return status
        time.sleep(MERGE_E2_POLL_S)


def flow_merge(rep, rig, cr, start, day, bots):
    r, spec = cr.r, cr.r.spec
    m = regress_merge.MergeSpec("split", OBSERVE_S, False)
    regress_merge.wait_before_merge(r, m)
    if not regress_merge.guard_merge(rep, rig, r, m):
        return cr
    seq = regress_merge.new_seq()
    regress_split.write_command(rig.LAYOUT_COMMAND, rigmerge.command_line(seq, spec.base, spec.child))
    status = wait_e2(rig, seq, MERGE_E2_TIMEOUT_S)
    if status["refused"] and "cooldown" in status["refused"]:
        time.sleep(regress_merge.RETRY_WAIT_S)
        seq = regress_merge.new_seq(seq)
        regress_split.write_command(rig.LAYOUT_COMMAND, rigmerge.command_line(seq, spec.base, spec.child))
        status = wait_e2(rig, seq, MERGE_E2_TIMEOUT_S)
    rep.check(status["op"] is not None and not status["refused"] and status["pairs"].get("e2") is not None,
              f"merge {spec.base} {spec.child} accepted and e2 broadcast ({status['op'] or 'no Merge op'}, pairs {status['pairs']}"
              + (f"; refused: {status['refused']}" if status["refused"] else "") + ")")
    if not status["pairs"].get("e2"):
        return cr
    adoption, kill_at = restart(rep, rig, cr, start, day)
    if adoption is None:
        return cr._replace(kill_at=kill_at)
    rep.check(sorted(adoption["ids"]) == [spec.base, spec.child] and adoption["pair"] == status["pairs"]["e2"],
              f"adopted the merge's e2 {status['pairs']['e2']} with {spec.base} and {spec.child} ({adoption['pair']}, {adoption['ids']}, from {adoption['source']})")
    second = wait_lifetime(rig, start, day, 1, lambda ls: rigcentral.resumed(ls), 30) or lifetimes(rig, start, day)[1]
    resumed = rigcentral.resumed(second)
    rep.check(resumed and resumed[0][0] == "Merge" and resumed[0][2] in ("DrainGate", "Retiring"),
              f"the merge resumed at its drain gate or retire after the restart ({resumed or 'no resumed line'})")
    done = wait_lifetime(rig, start, day, 1, lambda ls: ("done" in [s for _, s in rigcentral.op_states(ls, 'Merge')]), MERGE_END_S)
    rep.check(done is not None, f"the resumed merge reached done within {MERGE_END_S} s")
    second = lifetimes(rig, start, day)[1]
    states = [s for _, s in rigcentral.op_states(second, "Merge")]
    wanted = ["retire sent", f"{spec.child} exited", f"channel {spec.child} removed", "e3 broadcast", "tidy broadcast", "done"]
    problems = rigsplitcrash.order_problems(states, [rf"^{re.escape(w)}" for w in wanted])
    rep.check(not problems, "the resumed merge's states retire sent, exited, channel removed, e3, tidy, done in order" + (f" ({'; '.join(problems[:3])})" if problems else ""))
    straggling = [s for s in states if rigmerge.DRAIN_STRAGGLING.match(s) or rigmerge.RETIRE_STRAGGLING.match(s)]
    if straggling:
        rep.notes.append(f"   straggling under the database outage: {straggling[0][:100]}")
    merge_pairs = dict(status["pairs"])
    for s in states:
        step = rigmerge.STEP_PAIR.match(s)
        if step:
            merge_pairs[step.group(1)] = step.group(2)
    grade_second_lifetime(rep, rig, second, allow_ops=True)
    regress_merge.grade_exit(rep, rig, spec.child, start, day)
    regress_merge.grade_prunes(rep, rig, spec.child, merge_pairs.get("e3"), start, day)
    regress_merge.grade_store(rep, rig, r.split)
    results = wait_results(r)
    grade_bots(rep, rig, r, results, 2, 0)
    hops = sum(rigmerge.fell_back(regress_split.read_lines(log)).count(True) for log in r.logs.values())
    if hops:
        rep.notes.append(f"   {hops} migration(s) with fellBack=True (a handoff rolled back during the database outage; a note on this variant)")
    grade_tester(rep, rig, 2)
    return cr._replace(merge_pairs=merge_pairs, adopted=adoption["pair"], kill_at=kill_at)


# ------------------------------------------------------------------ splitcentral_base
def flow_base(rep, rig, cr, start, day, bots):
    r, spec, split = cr.r, cr.r.spec, cr.r.split
    time.sleep(max(0.0, r.done_at + KILL_AFTER_DONE_S - time.time()))
    adoption, kill_at = restart(rep, rig, cr, start, day)
    kill_second = datetime.datetime.fromtimestamp(kill_at).replace(microsecond=0)
    if adoption is None:
        return cr._replace(kill_at=kill_at)
    rep.check(sorted(adoption["ids"]) == [spec.base, spec.child], f"adopted {adoption['pair']} with {adoption['ids']} from {adoption['source']}")
    answer = regress_splitcrash.wait_answer(rig, split, start, day, ANSWER_TIMEOUT_S)
    rep.check(answer is not None, f"{spec.base} came back and applied the central's answer within {ANSWER_TIMEOUT_S} s of the kill")
    if answer is not None:
        time.sleep(max(regress_splitcrash.MONSTER_WINDOW_S + regress_splitcrash.PAIR_TOLERANCE_S, kill_at + regress_splitcrash.RELEASE_WITHIN_S - time.time()))
    second = lifetimes(rig, start, day)[1]
    grade_second_lifetime(rep, rig, second, allow_no_server=True)
    base_lines = regress_splitcrash.by_stamp(rig.zone_lines(spec.base, start, day, rig.MAP))
    child_lines = regress_splitcrash.by_stamp(rig.zone_lines(spec.child, start, day, rig.MAP))
    faults = rigcentral.hello_faults(base_lines)
    rep.check(faults == [2], f"{spec.base}: FAULT on its second layout hello ({faults})")
    kept, _ = rigsplit.split_rects(split.base_rect, split.axis, split.at, split.side)
    lives = rigsplit.lifetimes(rig.zone_lines(spec.base, start, day, rig.MAP, suffixes=("info",)))
    owning_p = [a for line in (lives[1] if len(lives) > 1 else []) for a in [rigsplit.parse_apply(line)] if a and rigsplit.same_rect(a["owned"], split.base_rect)]
    rep.check(len(lives) == 2 and not owning_p, f"{spec.base}: 2 process lifetimes and no apply of the second owning the whole base ({len(lives)} lifetime(s), {len(owning_p)} owning P)")
    back = [line for line in child_lines if (rigtools.stamp_datetime(line) or kill_second) >= kill_second
            and any(rigsplit.handoff_start(b.character, spec.child, spec.base).search(line) for b in r.plan if b.side == regress_split.SIDE)]
    rep.check(not back, f"{spec.child} handed no R-bot back to {spec.base} after the kill ({len(back)})")
    planned = [int(m.group(2)) for line in base_lines for m in [rigsplitcrash.SPAWN_AREAS.search(line)]
               if m and m.group(1) == spec.base and (rigtools.stamp_datetime(line) or kill_second) > kill_second]
    rep.check(planned and planned[-1] == PLANNED_MONSTERS, f"{spec.base} planned {PLANNED_MONSTERS} monster(s) in the base rectangle after its restart ({planned or 'none'})")
    head = rigsplitcrash.incarnation(rigsplitcrash.before(base_lines, kill_second), spec.base)
    head = f"{spec.base}:{head}" if head else None
    held = rigsplitcrash.held_monsters(rigsplitcrash.before(child_lines, kill_second), spec.base, spec.child, head)
    if held:
        missing = rigsplitcrash.unreleased(held, child_lines, spec.base, spec.child, kill_second, regress_splitcrash.RELEASE_WITHIN_S)
        rep.check(not missing, f"{spec.child} released the {len(held)} monster(s) of {spec.base}'s old incarnation within {regress_splitcrash.RELEASE_WITHIN_S} s of the kill"
                  + (f" (not: {', '.join(missing[:4])})" if missing else ""))
    else:
        rep.notes.append(f"   {spec.child} held no monster of {spec.base} at the kill (about 1 run in 32)")
    if answer is not None:
        answered = rigtools.stamp_datetime(answer)
        window_end = answered + datetime.timedelta(seconds=regress_splitcrash.MONSTER_WINDOW_S)
        tol = datetime.timedelta(seconds=regress_splitcrash.PAIR_TOLERANCE_S)
        second_base = [line for line in base_lines if (rigtools.stamp_datetime(line) or kill_second) >= answered]
        pairs = rigsplitcrash.monster_pairs(rigsplitcrash.windows(second_base, answered, window_end),
                                            rigsplitcrash.windows(child_lines, answered - tol, window_end + tol), regress_splitcrash.PAIR_TOLERANCE_S)
        problems = rigsplitcrash.monster_sum_problems([s for _, s in pairs], PLANNED_MONSTERS, regress_splitcrash.SUM_RUN)
        rep.check(not problems, f"owned monsters {spec.base} + {spec.child} within {regress_splitcrash.MONSTER_WINDOW_S} s of the answer"
                  + (f" ({'; '.join(problems)})" if problems else f": {PLANNED_MONSTERS} for {regress_splitcrash.SUM_RUN} pairs, never over"))
    grade_cells(rep, rig, cr, adoption["pair"], kill_at, start, day)
    results = wait_results(r)
    registered = [line for line in base_lines if rigtools.PLAYER_REGISTERED.search(line) and (rigtools.stamp_datetime(line) or kill_second) > kill_second]
    for user, (b, result, lines) in results_of(r, results).items():
        if b.side == regress_split.SIDE:
            problems = rigcentral.bot_problems(result, 1, 0, lines)
            rep.check(not problems, f"{user}: the split's migration, no re-login" + (f" ({'; '.join(problems)})" if problems else ""))
            continue
        problems = rigsplitcrash.bot_problems(result, {0, 1}, 1)
        if not rigsplitcrash.routed(lines):
            problems.append("no 'Re-login routed to' line")
        if not any(f" S player registered {b.character} " in line for line in registered):
            problems.append(f"not registered on {spec.base}'s second lifetime")
        rep.check(not problems, f"{user}: re-logged in once and ended on {spec.base}" + (f" ({'; '.join(problems)})" if problems else ""))
    return cr._replace(adopted=adoption["pair"], kill_at=kill_at)


def grade_layout(rep, rig, start, day, cr):
    """regress.grade_layout for a central-restart scenario: the split's map per process lifetime against
    rigcentral.expected_lives (a second coordinator incarnation allowed), the map-side lines the case allows."""
    r, kind = cr.r, cr.case.kind
    split, child = r.split, r.split.child
    status = rigsplitcrash.crash_status(regress_split.read_lines(rig.LAYOUT_COMMAND_STATUS), r.seq) if r.seq else {"pairs": {}, "settle": None}
    regress_split.grade_layout(
        rep, rig, start, day, split,
        applies=lambda zone, info: rigcentral.lifetime_apply_violations(
            zone, info, split, lambda own: rigcentral.expected_lives(kind, split, zone, own, status["pairs"], cr.merge_pairs, cr.adopted, status.get("settle")),
            label=f"the {kind} case"),
        ops=lambda zone, lines: len(rigcentral.disallowed_lines(zone, lines, retiring=child if kind == "merge" else None)),
        how_split=f"split map ({kind} case): layouts by rectangle per process lifetime, a second coordinator incarnation allowed")
