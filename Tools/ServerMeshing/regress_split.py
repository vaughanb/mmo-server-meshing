"""Slice 2b's manual-split scenarios for regress.py (Docs/ServerMeshing/slices/slice2b_design.md decisions 15-19):

    python Tools/ServerMeshing/regress.py split splitc2

split: the test map runs Dynamic; eight bots (meshbot2..9) stand in C's two halves, the fixture meshbot harvests in
the far half R and 40 synthetic players wander C; once every bot stands, the harness writes the LayoutCommand
'<seq> split C x 0 + C.1' and waits for the op's status file to say done and for C to apply interest == C minus R.
splitc2: the same cut of E at x = 200, whose child E.1 touches C2 (map 2) at x = 300.

regress.py hands itself in as `rig` (db_reset, request_play, launch_bot, zone_lines, ...), so this module never imports
it and the import graph keeps no cycle; the unit tests hand in a fake. The graders are rigsplit's (pure).
"""
import collections
import os
import time

import provision
import rigsplit
import rigtools

SCENARIOS = ("split", "splitc2")
TWO_HOST_REASON = ("starts a child cell through the central's spawner request; the other machine runs no spawner, so "
                   "a child of its cell could not start there")
PERSIST_REASON = "runs the test map Dynamic through the environment, which a persisted remote cell never gets"

OBSERVE_S = 300          # each bot's stand window (-meshObserveSeconds), from its "Standing at"
STAND_TIMEOUT_S = 180    # every bot logged in and standing
SPLIT_TIMEOUT_S = 240    # the command's status says done (spawn, pre-warm, drain and three broadcasts)
APPLY_TIMEOUT_S = 30     # after done: the base applied interest == P minus R
RESULT_SLACK_S = 90      # past the last bot's stand window, for its RESULT
MAX_GAP_MS = 1000.0
STAND_BOTS = 8           # meshbot2..9 (provision.reset_bot_positions(2, 8, ...))
LOAD_PLAYERS = 40

Spec = collections.namedtuple("Spec", "base base_rect at child channel tester harvest load_plan beside_c2 monster_areas")
# The test map's cells are 200 m squares; C is centred on the origin and E east of it (regress.ZONES).
SPECS = {
    "split": Spec(base="C", base_rect=(-100.0, -100.0, 200.0, 200.0), at=0.0, child="C.1", channel="C",
                  tester=(-50.0, 82.0), harvest=(70.0, -80.0), load_plan=f"C:{LOAD_PLAYERS}", beside_c2=False,
                  monster_areas="Spawn_Interior_C"),
    "splitc2": Spec(base="E", base_rect=(100.0, -100.0, 200.0, 200.0), at=200.0, child="E.1", channel="E",
                    tester=(150.0, 82.0), harvest=None, load_plan=None, beside_c2=True, monster_areas=None),
}
AXIS, SIDE = "x", "+"                          # both scenarios cut along x and give the high side to the child
OFFSETS = (20.0, 40.0, 60.0, 80.0)             # metres from the cut: x in [10, 90) on either side
ACROSS = (75.0, -75.0, 90.0, -90.0)            # |z| 70-95: outside Spawn_Interior_C's 60 m and off E's z = 0 line
C2_OFFSET_X = -400.0                           # map 2's frame sits 400 m east of the test map's


def split_of(name, map_id):
    """The rigsplit.Split a scenario performs."""
    spec = SPECS[name]
    return rigsplit.Split(map_id, spec.base, spec.base_rect, AXIS, spec.at, SIDE, spec.child)


def stand_points(at):
    """(far, near): four (x, z) points in the child's half R and four in the kept half, at OFFSETS from the cut x = at
    and ACROSS it. meshbot2..5 take the far ones, meshbot6..9 the near ones."""
    far = [(at + d, z) for d, z in zip(OFFSETS, ACROSS)]
    near = [(at - d, z) for d, z in zip(OFFSETS, ACROSS)]
    return far, near


Bot = collections.namedtuple("Bot", "user character point side harvest")


def plan_bots(name):
    """The scenario's headless bots: meshbot2..9 standing (four per half), plus for split the fixture meshbot as the
    harvest bot in R."""
    spec = SPECS[name]
    far, near = stand_points(spec.at)
    bots = [Bot(f"meshbot{2 + i}", f"meshbot{2 + i}Char", p, "+" if i < len(far) else "-", False)
            for i, p in enumerate(far + near)]
    if spec.harvest is not None:
        bots.append(Bot("meshbot", provision.BOT.character_name, spec.harvest, "+", True))
    return bots


def read_lines(path):
    """A file's lines (a bot log, the status file), [] when it does not exist yet."""
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return f.read().splitlines()
    except FileNotFoundError:
        return []


def write_command(path, line):
    """Writes the LayoutCommand whole (a temporary file renamed over it), so the play runner never reads half a line."""
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(line + "\n")
    os.replace(tmp, path)


def wait_standing(logs, timeout):
    """{log: (x, z)} once every bot log holds 'Standing at', or as far as it got when the timeout passes."""
    end = time.time() + timeout
    while True:
        found = {log: rigsplit.standing_at(read_lines(log)) for log in logs}
        if all(found.values()) or time.time() >= end:
            return found
        time.sleep(3)


def wait_op(status_path, seq, timeout):
    """The command's parsed status once it has ended (done, refused, aborted or frozen), or as it stood at the timeout."""
    end = time.time() + timeout
    while True:
        status = rigsplit.parse_op_status(read_lines(status_path), seq)
        if rigsplit.op_outcome(status) or time.time() >= end:
            return status
        time.sleep(2)


def wait_applied(rig, split, start, day, timeout):
    """The base's apply line whose owned and interest rects are both P minus R, or None at the timeout."""
    kept, _ = rigsplit.split_rects(split.base_rect, split.axis, split.at, split.side)
    end = time.time() + timeout
    while True:
        for line in rig.zone_lines(split.base, start, day, split.map_id, suffixes=("info",)):
            a = rigsplit.parse_apply(line)
            if a and a["cell"] == split.base and rigsplit.same_rect(a["owned"], kept) \
                    and rigsplit.same_rect(a["interest"], kept):
                return line
        if time.time() >= end:
            return None
        time.sleep(2)


def wait_results(logs, timeout):
    """{log: RESULT stand (or harvest) line or ''} once every log has one, or at the timeout."""
    end = time.time() + timeout
    while True:
        found = {}
        for log in logs:
            found[log] = next((line[line.find("RESULT"):].strip() for line in read_lines(log)
                               if "RESULT stand:" in line or "RESULT harvest:" in line), "")
        if all(found.values()) or time.time() >= end:
            return found
        time.sleep(3)


def run(name, rep, rig, start, day, bots):
    """Runs one split scenario against the rig (regress) and records its checks on rep; appends ('stand', log) for
    every bot to `bots` (regress grades their logs). Returns the rigsplit.Split for grade_layout."""
    spec, split = SPECS[name], split_of(name, rig.MAP)
    plan = plan_bots(name)
    harvest = next((b for b in plan if b.harvest), None)
    stand = [b for b in plan if not b.harvest]
    rig.db_reset(spec.tester, harvest.point if harvest else spec.tester)
    provision.reset_bot_positions(2, STAND_BOTS, [b.point for b in stand], status_path=rig.STATUS)
    env = {"MESH_CELL_MODE": f"{rig.MAP}:Dynamic"}
    if spec.load_plan:
        env["MESH_LOAD_PLAN"] = spec.load_plan
    if spec.monster_areas:
        # The test map saves C's monster areas inactive; this one straddles the cut, so monsters stand on both sides.
        env["MESH_MONSTER_AREAS"] = spec.monster_areas
    rig.request_play("stand", spec.tester[0], spec.tester[1], env=env)
    rig.wait_play(60)
    time.sleep(20)
    logs = {}
    for b in plan:
        # The harvest variant is the harvest mode with -meshHarvestAfterMigration; it logs "Standing at" too.
        mode = "harvest" if b.harvest else "stand"
        args = ["-meshAutoMode", mode, "-meshWalkX", f"{b.point[0]:g}", "-meshWalkZ", f"{b.point[1]:g}"]
        logs[b.user] = rig.launch_bot(name, b.user, args, channel=spec.channel, observe_seconds=OBSERVE_S,
                                      line_x=spec.at, harvest_after_migration=b.harvest)
        bots.append((mode, logs[b.user]))
    standing = wait_standing(list(logs.values()), STAND_TIMEOUT_S)
    for b in plan:
        at = standing[logs[b.user]]
        rep.check(at is not None and rigsplit.side_of(at, AXIS, spec.at) == b.side,
                  f"{b.user} stood in the {'child' if b.side == SIDE else 'kept'} half before the split "
                  f"(at {at}, planned {b.point})")
    if not all(standing.values()):
        rep.check(False, "split: not sent, a bot never logged 'Standing at'")
        return split

    seq = str(int(time.time()))
    write_command(rig.LAYOUT_COMMAND, rigsplit.command_line(seq, spec.base, AXIS, spec.at, SIDE, spec.child))
    status = wait_op(rig.LAYOUT_COMMAND_STATUS, seq, SPLIT_TIMEOUT_S)
    grade_status(rep, status, spec)
    applied = wait_applied(rig, split, start, day, APPLY_TIMEOUT_S) if status["done"] else None
    kept = rigsplit.format_rect(rigsplit.split_rects(split.base_rect, AXIS, spec.at, SIDE)[0])
    rep.check(applied is not None, f"{spec.base} applied a layout owning and interested in {kept} only (e3)")

    results = wait_results(list(logs.values()), OBSERVE_S + RESULT_SLACK_S)
    grade_bots(rep, rig, plan, logs, results, spec, start, day)
    grade_entities(rep, rig, spec, start, day, status["pairs"].get("e2"), status["pairs"].get("e3"))
    if spec.beside_c2:
        grade_c2(rep, rig, plan, standing, logs, status, spec, start, day)
    return split


def grade_status(rep, status, spec):
    """The op's own record (decision 17): accepted, ended done, no abort, freeze or straggler."""
    rep.check(status["requested"] is not None and not status["refused"],
              f"split {spec.base} at x={spec.at:g} accepted ({status['op'] or 'no op'}"
              + (f"; refused: {status['refused']}" if status["refused"] else "") + ")")
    rep.check(status["done"], f"split done (states: {', '.join(status['states'][-4:]) or 'none'})")
    rep.check(not status["aborted"] and not status["frozen"],
              f"split neither aborted nor frozen ({status['aborted'] or status['frozen'] or 'clean'})")
    rep.check(status["drain"] == "open" and not status["stragglers"],
              f"P's drain evidence reached 0 left (drain {status['drain']}, {status['stragglers'] or 0} straggler(s))")
    for text in status["other"][:3]:
        rep.notes.append(f"   status: {text[:160]}")


def grade_bots(rep, rig, plan, logs, results, spec, start, day):
    """Decision 18's migration checks: R-bots migrated once, ready, gap under a second, handed off by one START each;
    P-bots never migrated; the harvest bot's yield and pickup."""
    base_lines = rig.zone_lines(spec.base, start, day, rig.MAP)
    for b in plan:
        result = rigsplit.parse_result(results[logs[b.user]])
        expected = 1 if b.side == SIDE else 0
        problems = rigsplit.migration_problems(result, expected, MAX_GAP_MS)
        rep.check(not problems, f"{b.user}: {expected} migration(s) with the split"
                  + (f" ({'; '.join(problems)})" if problems else ""))
        if b.side == SIDE:
            n = rig.count(base_lines, rigsplit.handoff_start(b.character, spec.base, spec.child).pattern)
            rep.check(n == 1, f"{spec.base} logged {n} Handoff START {b.character} zone {spec.base}->{spec.child}, expected 1")
        if b.harvest:
            rep.check(result.get("yield", 0) >= 1 and result.get("pickup") is True,
                      f"{b.user} harvested on {spec.child} and picked up a drop "
                      f"(yield={result.get('yield')} pickup={result.get('pickup')})")


def grade_entities(rep, rig, spec, start, day, e2_pair=None, e3_pair=None):
    """What else crosses with a split of C: monsters promoted in place, statics handed over, synthetics adopted; the
    pre-warm's worst frame as a note; the Dynamic map's kinematic puppet default. The monster check covers the split
    itself, up to the child's e3 apply, and fails only a monster that existed during the pre-warm yet was spawned by
    handoff: P keeps its spawn area and respawns monsters inside R, handing them over before the child has a puppet
    (those are a note, rigsplit.monster_spawns)."""
    base_lines = rig.zone_lines(spec.base, start, day, rig.MAP)
    child_lines = rig.zone_lines(spec.child, start, day, rig.MAP)
    rep.check(len(child_lines) > 0, f"{spec.child} logged since the scenario started")
    if not rig._play_env.get("MESH_PUPPET_MOTION"):
        for cell, lines in ((spec.base, base_lines), (spec.child, child_lines)):
            rep.check(rig.count(lines, rigsplit.PUPPET_MOTION_DEFAULT.pattern) >= 1,
                      f"{cell}: puppets kinematic by the Dynamic map default")
    frame = rigsplit.prewarm_max_frame(child_lines)
    rep.notes.append(f"   {spec.child} pre-warm max frame: {'none logged' if frame is None else f'{frame:g} ms'}")
    if spec.beside_c2:
        return    # splitc2 adds C2's checks to split's migration checks only (decision 19)
    during = rigsplit.until_applied(child_lines, e3_pair)
    prewarm = rigsplit.until_applied(child_lines, e2_pair)
    promoted = rigsplit.count_kind(during, rigsplit.MONSTER_HANDOFF, "promoted", spec.base, spec.child)
    existing, respawned = rigsplit.monster_spawns(during, prewarm, spec.base, spec.child)
    rep.check(promoted >= 1 and not existing,
              f"{spec.child}: {promoted} monster(s) promoted and {len(existing)} pre-warmed one(s) spawned by handoff "
              f"{spec.base}->{spec.child} up to its e3 apply" + (f" ({', '.join(existing[:3])})" if existing else ""))
    later = rigsplit.count_kind(child_lines[len(during):], rigsplit.MONSTER_HANDOFF, "spawned", spec.base, spec.child)
    rep.notes.append(f"   {spec.child}: {len(respawned)} monster(s) respawned in R and spawned by handoff before e3, "
                     f"{later} after (from {spec.base}'s areas)")
    sent = rigsplit.count_kind(base_lines, rigsplit.STATIC_HANDOFF, "sent", spec.base, spec.child)
    taken = sum(rigsplit.count_kind(child_lines, rigsplit.STATIC_HANDOFF, k, spec.base, spec.child)
                for k in ("promoted", "spawned"))
    failed = sum(rig.count(lines, r"Static handoff \S+ zone \S+: failed") for lines in (base_lines, child_lines))
    rep.check(sent >= 1 and taken >= 1 and failed == 0,
              f"statics: {sent} sent by {spec.base}, {taken} taken by {spec.child}, {failed} failed")
    adopted = rigsplit.adoptions(child_lines, spec.base, spec.child)
    rep.check(len(adopted) >= 1, f"{spec.child} adopted {len(adopted)} synthetic player(s) from {spec.base}")


def grade_c2(rep, rig, plan, standing, logs, status, spec, start, day):
    """Decision 19: C2 links the child with map 2's frame offset, sees an R-bot's puppet at its translated position,
    and E is out of C2's band from e3 on."""
    c2 = rig.zone_lines("C2", start, day, rig.MAP2)
    child = spec.child.replace(".", r"\.")
    # The offset is on the link line, or (E.1's hello can reach C2 before C2 applies the layout naming it) on the
    # line of the apply that sets it.
    rep.check(rig.count(c2, rf"Neighbour zone {child} (linked .*)?frame offset \({C2_OFFSET_X:g}") >= 1,
              f"C2 linked {spec.child} with the frame offset ({C2_OFFSET_X:g}")
    seen = []
    for b in plan:
        at = standing.get(logs[b.user]) or b.point
        if b.side == SIDE and rigsplit.ghost_at(c2, b.character, spec.child, at[0] + C2_OFFSET_X, at[1]):
            seen.append(b.user)
    rep.check(bool(seen), f"C2 sees an R-bot's puppet from {spec.child} at its translated position ({', '.join(seen) or 'none'})")
    e3 = status["pairs"].get("e3")
    bands = [ids for pair, _, ids in rigsplit.neighbour_bands(c2, "C2") if pair == e3]
    rep.check(bool(bands) and spec.base not in bands[-1] and spec.child in bands[-1],
              f"C2's band after e3 ({e3}) holds {spec.child} and not {spec.base}: {bands[-1] if bands else 'no apply'}")


def grade_layout(rep, rig, start, day, split):
    """regress.grade_layout for a split scenario: the split's map by rectangles (rigsplit.split_apply_violations), every
    other map as 2a grades it (C2: no own-cell layout, Static-neighbour applies allowed); no retire, merge, abort or
    freeze line and no layout gate warning on any cell."""
    try:
        dynamic = rigtools.dynamic_maps(rig._play_env.get("MESH_CELL_MODE", ""),
                                        rigtools.asset_cell_modes(rig.RIG_REGISTRY), {m for _, m in rig.ZONES})
    except (OSError, ValueError) as ex:
        rep.check(False, f"layout: cannot tell which maps are Dynamic: {ex}")
        return
    if split is None or split.map_id not in dynamic:
        rep.check(False, "layout: the split's map is not Dynamic in this Play")
        return
    expected = {z for z, m in rig.ZONES if m == split.map_id} | {split.child}
    for zone, map_id in rig.graded_zones(day):
        info = rig.zone_lines(zone, start, day, map_id, suffixes=("info",))
        if map_id == split.map_id:
            if not info and zone not in expected:
                continue    # a dynamic cell of an earlier run today
            problems = rigsplit.split_apply_violations(zone, info, split)
            how = "split map: three extra layouts by rectangle"
        else:
            problems = rigtools.layout_apply_violations(zone, info, map_id in dynamic)
            how = "Dynamic: layout applied before any player" if map_id in dynamic else "Static: no own-cell layout"
        everything = rig.zone_lines(zone, start, day, map_id)
        ops = sum(1 for line in everything if rigsplit.SPLIT_OP_PROBLEM.search(line))
        gate = sum(1 for line in everything if rigtools.LAYOUT_GATE_WARNING.search(line))
        if ops:
            problems.append(f"{ops} retire/merge/abort/freeze line(s)")
        if gate:
            problems.append(f"{gate} layout gate warning(s)")
        rep.check(not problems, f"zone {zone}: {how}" + (f" ({'; '.join(problems[:2])})" if problems else ""))
