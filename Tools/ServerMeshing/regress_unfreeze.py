"""The operator's unfreeze on the split-process topology (DESIGN 0.85, Docs/ServerMeshing/slices/unfreeze_design.md
decisions 10-11), the flow of regress_central's `splitcentral_unfreeze`:

    python Tools/ServerMeshing/regress.py splitcentral_unfreeze

`split C x 0 + C.1` with every stand bot in the kept half (none in R, no tester); 10 s after done the central is killed
and, while it is down, C.1's process too (the kit's spawner never restarts a requested child). The restarted central
adopts the store's layout at the ceiling and freezes family C; `<seq> unfreeze C` declares C.1 dead and collapses it into
C; a bot logging in into R afterwards is routed to C. Graded: the lines, C.1 never back, C never owning R before the
collapse's e2, the logins, the store.

regress_central hands itself in as `rc` (its lifetimes, results and bot graders) and regress.py as `rig`; the unit tests
hand in fakes. The graders are rigcentral's.
"""
import datetime
import json
import re
import time

import regress_merge
import regress_procs
import regress_split
import rigcentral
import rigmerge
import rigsplit
import rigsplitcrash
import rigtools

CEILING_S = 60          # the registry's recoveryCeilingSeconds: the adoption and the freeze land together at it
FROZEN_TIMEOUT_S = 90 + CEILING_S   # the restarted central's adoption (ADOPT_TIMEOUT_S) plus the ceiling it waits out
UNFREEZE_END_S = 180    # the collapse's done after the command
SAMPLE_S = 2.0
# The collapse's info-level states in order; its exit "before drained" is a warning (the kit's warn file), graded apart:
# lines of one second from two files have no order between them.
COLLAPSE_STATES = ("requested {b} {c} collapse", "e1 broadcast", "e1 gate open", "e2 staged", "e2 broadcast",
                   "channel {c} removed", "e3 broadcast", "tidy broadcast", "done")


def kept_plan():
    """The eight stand bots of `split`, all in the kept half: meshbot6..9 at their own points, meshbot2..5 10 m further
    from the cut (killing C.1 would kick a bot in R, and its re-login would grade the login fallback, not the escape)."""
    bots = [b for b in regress_split.plan_bots("split") if not b.harvest]
    _, near = regress_split.stand_points(regress_split.SPECS["split"].at)
    out = []
    for i, b in enumerate(bots):
        x, z = near[i % len(near)]
        out.append(b._replace(point=(x - (10.0 if i < len(near) else 0.0), z), side="-"))
    return out


def wait_sampling(rc, rig, start, day, index, pred, timeout, sample):
    """rc.wait_lifetime with `sample()` called every SAMPLE_S meanwhile."""
    end = time.time() + timeout
    while True:
        lives = rc.lifetimes(rig, start, day)
        if len(lives) > index and pred(lives[index]):
            return lives[index]
        sample()
        if time.time() >= end:
            return None
        time.sleep(SAMPLE_S)


def collapse_done(lines):
    return any(s == "done" for _, s in rigcentral.op_states(lines, "Merge"))


def grade_store(rep, rig, split):
    """The store after the collapse: no entry of the family (regress_merge.grade_store), no operation, the child fenced."""
    regress_merge.grade_store(rep, rig, split)
    try:
        with open(rig.COORDINATOR_STORE, encoding="utf-8-sig") as f:
            store = json.load(f)
    except (OSError, ValueError) as ex:
        rep.check(False, f"coordinator store unreadable after the unfreeze: {ex}")
        return
    fenced = [f.get("incarnation") for f in store.get("fencedIncarnations") or [] if (f.get("cellId") or "").lower() == split.child.lower()]
    rep.check(not store.get("hasOperation") and fenced, f"the store holds no operation and fences {split.child} ({len(fenced)} fence(s), operation {bool(store.get('hasOperation'))})")


def owner_violations(base_lines, split, kept, e2_pair, after):
    """C's applies after `after` (a datetime) that own more than its kept half before the collapse's e2 pair."""
    out = []
    e2 = int(e2_pair.split("/")[1]) if e2_pair else None
    for line in base_lines:
        t = rigtools.stamp_datetime(line)
        a = rigsplit.parse_apply(line)
        if a is None or t is None or t < after or rigsplit.same_rect(a["owned"], kept):
            continue
        if e2 is None or a["version"] < e2:
            out.append(f"v{a['version']} owns {rigsplit.format_rect(a['owned'])}")
    return out


def flow(rep, rig, rc, cr, start, day, bots):
    """Decisions 10-11. Returns the CentralRun with the collapse's pairs for rc.grade_layout."""
    r, spec, split = cr.r, cr.r.spec, cr.r.split
    samples = []

    def sample():
        samples.append(regress_merge.count_processes(rig, spec.child))
    time.sleep(max(0.0, r.done_at + rc.KILL_AFTER_DONE_S - time.time()))
    try:
        killed = regress_procs.kill_central(rig)
        kill_at = time.time()
        child_killed = rigmerge.parse_count(rig.ps(rigsplitcrash.kill_script(rig.EXE, spec.child)))
    except (RuntimeError, ValueError) as ex:
        rep.check(False, f"the central and {spec.child} could not be killed ({str(ex)[:120]})")
        return cr
    rep.check(killed == 1 and child_killed == 1, f"killed {killed} central and {child_killed} {spec.child} process(es) while the central is down, expected 1 and 1")
    kill = datetime.datetime.fromtimestamp(kill_at).replace(microsecond=0)
    time.sleep(rc.CENTRAL_DOWN_S)
    sample()
    regress_procs.start_central(rig, cr.name)
    if not regress_procs.wait_central(rig):
        rep.check(False, "the restarted central did not bind its login port")
        return cr._replace(kill_at=kill_at)
    second = wait_sampling(rc, rig, start, day, 1, lambda ls: any(f[0] == spec.base for f in rigcentral.frozen_families(ls)), FROZEN_TIMEOUT_S, sample)
    rep.check(second is not None, f"the restarted central froze family {spec.base} within {FROZEN_TIMEOUT_S} s of its start")
    if second is None:
        return cr._replace(kill_at=kill_at)
    adoption = next((a for a in rigcentral.adoptions(second) if a["map"] == rig.MAP), None)
    frozen = [f for f in rigcentral.frozen_families(second) if f[0] == spec.base]
    rep.check(adoption is not None and adoption["source"] == "the store" and sorted(adoption["ids"]) == [spec.base, spec.child],
              f"adopted {rig.MAP}'s layout from the store with {spec.base} and {spec.child} ({adoption})")
    rep.check(frozen[0][1] == rig.MAP and frozen[0][2].startswith(f"no hello from {spec.child} after"), f"family {spec.base} frozen: {frozen[0][2]}")
    map_frozen = [line for line in second if rigcentral.MAP_FROZEN.search(line)]
    rep.check(not map_frozen, "only the family froze, not the map" + (f" ({rigcentral.payload(map_frozen[0])[:100]})" if map_frozen else ""))

    seq = regress_merge.new_seq()
    regress_split.write_command(rig.LAYOUT_COMMAND, f"{seq} unfreeze {spec.base}")
    unfrozen_at = time.time()
    second = wait_sampling(rc, rig, start, day, 1, collapse_done, UNFREEZE_END_S, sample)
    rep.check(second is not None, f"the collapse after `unfreeze {spec.base}` reached done within {UNFREEZE_END_S} s ({time.time() - unfrozen_at:.0f} s)")
    second = second or rc.lifetimes(rig, start, day)[1]
    texts = [t for _, t in rigcentral.unfreeze_lines(second, spec.base)]
    wanted = [rf"^{re.escape(spec.child)} declared dead \(no hello \d+ s after the central started\); incarnations started before [0-9a-f]+ fenced$",
              rf"^family {re.escape(spec.base)} of map {re.escape(rig.MAP)} unfrozen by the operator: {re.escape(spec.child)} declared dead$",
              rf"^{re.escape(spec.child)} collapses into {re.escape(spec.base)}$"]
    missing = [w for w in wanted if not any(re.match(w, t) for t in texts)]   # one second, info and warn files: no order
    rep.check(not missing and len(texts) == 3, f"the unfreeze lines: declared dead, unfrozen, collapses into the base ({texts})")
    refused = rigcentral.refusals(second)
    rep.check(not refused, "no 'Layout op refused' after the restart" + (f" ({refused[0][:100]})" if refused else ""))
    states = [s for _, s in rigcentral.op_states(second, "Merge")]
    wanted = [w.format(b=spec.base, c=spec.child) for w in COLLAPSE_STATES]
    problems = rigsplitcrash.order_problems(states, [rf"^{re.escape(w)}" for w in wanted])
    rep.check(not problems, "the collapse's states requested, e1, e1 gate, e2 staged, e2, channel removed, e3, tidy, done in order"
              + (f" ({'; '.join(problems[:3])})" if problems else ""))
    rep.check(f"{spec.child} exited before drained" in states, f"the collapse saw {spec.child} gone before drained")
    merge_pairs = {}
    for s in states:
        step = rigmerge.STEP_PAIR.match(s)
        if step:
            merge_pairs[step.group(1)] = step.group(2)

    # Logins route: a bot whose row is in R logs in after the collapse and lands on the base.
    user, x, z = rc.LATE_BOT
    log = rig.launch_bot(cr.name, user, ["-meshAutoMode", "stand", "-meshWalkX", f"{x:g}", "-meshWalkZ", f"{z:g}"],
                         channel=spec.channel, observe_seconds=rc.LATE_BOT_OBSERVE_S, line_x=spec.at)
    bots.append(("stand", log))
    standing = regress_split.wait_standing([log], rc.LATE_STAND_TIMEOUT_S)
    rep.check(standing[log] is not None, f"the late bot {user} logged in and stood after the unfreeze ({standing[log]})")
    second = rc.lifetimes(rig, start, day)[1]
    # The login logs a routing line only when it changes the channel: the bot asks for C, so none, or one to C.
    routes = rigcentral.login_routes(second, user + "Char")
    rep.check(all(ch == spec.base for _, ch in routes), f"the late bot's login was routed to {spec.base} ({routes or 'asked for it, no re-route'})")
    registered = [zone for zone in (spec.base, spec.child)
                  if any(f"player registered {user}Char " in line and (rigtools.stamp_datetime(line) or kill) >= kill for line in rig.zone_lines(zone, start, day, rig.MAP))]
    rep.check(registered == [spec.base], f"{user}Char registered on {spec.base} only after the restart ({registered})")

    # No two owners: C.1 never came back, and C owned only its kept half until the collapse's e2.
    sample()
    seen = [n for n in samples if n is not None]
    rep.check(seen and max(seen) == 0, f"{spec.child} never came back: process counts {rigsplitcrash.process_sequence(samples)}")
    lives = rigsplit.lifetimes(rig.zone_lines(spec.child, start, day, rig.MAP, suffixes=("info",)), spec.child)
    rep.check(len(lives) == 1, f"{spec.child}: {len(lives)} process lifetime(s), expected 1")
    kept, _ = rigsplit.split_rects(split.base_rect, split.axis, split.at, split.side)
    base_lines = rig.zone_lines(spec.base, start, day, rig.MAP, suffixes=("info",))
    early = owner_violations(base_lines, split, kept, merge_pairs.get("e2"), kill)
    rep.check(merge_pairs.get("e2") and not early, f"{spec.base} owned only its kept half after the kill until the collapse's e2 {merge_pairs.get('e2')}"
              + (f" ({', '.join(early[:3])})" if early else ""))
    fences = [line for zone, map_id in rig.ZONES for line in rig.zone_lines(zone, start, day, map_id) if rigsplitcrash.FENCE.search(line)]
    rep.check(not fences, "no cell fenced" + (f" ({rigcentral.payload(fences[0])[:100]})" if fences else ""))

    results = rc.wait_results(r, [log])
    rc.grade_bots(rep, rig, r, results, 0, 0)
    late = rigsplit.parse_result(results.get(log, ""))
    problems = rigcentral.bot_problems(late, 0, 0, regress_split.read_lines(log))
    rep.check(not problems, f"{user}: no migration, no re-login" + (f" ({'; '.join(problems)})" if problems else ""))
    grade_store(rep, rig, split)
    return cr._replace(merge_pairs=merge_pairs, adopted=adoption["pair"] if adoption else None, kill_at=kill_at)
