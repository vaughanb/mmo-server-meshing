"""Slice 7's on-demand link scenarios for regress.py (Docs/ServerMeshing/slices/slice7_design.md decisions 13 and 14):

    python Tools/ServerMeshing/regress.py linkcap partyfar
    python Tools/ServerMeshing/regress.py partyfar --env MESH_LINKS=all:OnDemand

linkcap: the test map cut into a Static 4x4 of 150 m cells (L<col><row>) beside C2, every map on demand and every link
server capped at 10 connections, which all-zones links (15 or 16 per cell) would overrun; the bot walks across three
lines and back over one. Every cell must link with each of its touching neighbours, refuse nobody, and keep its links
near its neighbour count.
partyfar: `party` plus meshbot2 in the test party, standing on W at (-250, 0), beyond ghost range of the fighting cells:
the kills' shares reach it as a remote member, through the central with MESH_LINKS on demand, by broadcast without.

regress.py hands itself in as `rig` (db_reset, request_play, wait_play, launch_bot, wait_for_bot, wait_status,
zone_lines, count, MAP, MAP2, ...), so this module never imports it; the graders are riglinks' (pure).
"""
import re
import time

import provision
import riglinks

SCENARIOS = ("linkcap", "partyfar")
TWO_HOST_REASON = "re-cuts the test map or places a third bot through this machine's database and spawner; the other machine serves fixed cells"
PERSIST_REASON = "runs its own layout and link toggles through the environment, which a persisted remote cell never gets"

LINK_CAP = 10
LINKCAP_TESTER = (-100.0, 100.0)      # L12
LINKCAP_BOT = (-200.0, 75.0)          # L02; walks east at z 75 to x 160 (three lines) and back to x 100 (one)
LINKCAP_WALK = ("160", "100")
LINKCAP_MIGRATIONS = 4
LINKCAP_ENV = {"MESH_LAYOUT_OVERRIDE": riglinks.override_value(), "MESH_LINKS": "all:OnDemand",
               "MESH_LINK_CAP": str(LINK_CAP), "MESH_NO_MONSTERS": "1"}
EXPECTED_ZONES = {"linkcap": set(riglinks.grid()) | {"C2"}}

FAR_MEMBER = ("meshbot2", 2, (-250.0, 0.0))   # user, bot number, its stand point on W
FIGHT_S = 300
STAND_S = 300


def env_on_demand(rig):
    """MESH_LINKS asked for this Play (the scenario's own environment or --env)."""
    return bool((getattr(rig, "_play_env", {}) or {}).get("MESH_LINKS", "").strip())


def run(name, rep, rig, start, day, bots):
    """Runs one of SCENARIOS against the rig and records its checks on rep; appends every bot to `bots`."""
    if name == "linkcap":
        run_linkcap(rep, rig, start, day, bots)
    elif name == "partyfar":
        run_partyfar(rep, rig, start, day, bots)
    else:
        raise ValueError(f"unknown scenario {name}")


def cell_lines(rig, start, day):
    """{cell: its lines since the scenario started} for linkcap's cells and C2."""
    lines = {cid: rig.zone_lines(cid, start, day) for cid in riglinks.grid()}
    lines["C2"] = rig.zone_lines("C2", start, day, rig.MAP2)
    return lines


def run_linkcap(rep, rig, start, day, bots):
    rig.db_reset(LINKCAP_TESTER, LINKCAP_BOT)
    rig.request_play("stand", LINKCAP_TESTER[0], LINKCAP_TESTER[1], env=dict(LINKCAP_ENV))
    rig.wait_play(120)
    time.sleep(30)
    channel = riglinks.cell_at(*LINKCAP_BOT)
    log = rig.launch_bot("linkcap", "meshbot", ["-meshAutoMode", "walk", "-meshWalkX", LINKCAP_WALK[0],
                                                "-meshReturnX", LINKCAP_WALK[1]], channel=channel)
    bots.append(("walk", log))
    walk = rig.wait_for_bot(log, "walk", 300)
    time.sleep(12)   # one more stats interval (10 s) on every cell
    grade_linkcap(rep, walk, cell_lines(rig, start, day))


def grade_linkcap(rep, walk, lines):
    """The checks of decision 13 on the bot's RESULT and every cell's lines."""
    rep.check("-> PASS" in walk and f"migrations={LINKCAP_MIGRATIONS}" in walk,
              f"bot crossed {LINKCAP_MIGRATIONS} lines: {walk[:120]}")
    acks = sum(riglinks.count(cell, re.compile(r"Handoff ACK meshbotChar by zone ")) for cell in lines.values())
    rep.check(acks >= LINKCAP_MIGRATIONS, f"{acks} handoff ACK(s) for the bot (want {LINKCAP_MIGRATIONS})")
    neighbours = riglinks.touching()
    worst_all_zones = max(len(riglinks.grid()) - 1 + (1 if "C2" in neighbours[cid] else 0) for cid in riglinks.grid())
    for cell in sorted(lines):
        problems = riglinks.link_problems(cell, lines[cell], neighbours.get(cell, set()), LINK_CAP)
        links = riglinks.last_links(lines[cell])
        how = f"{links['out']} out, peak in {links['peak_in']}" if links else "no links line"
        rep.check(not problems, f"{cell}: linked with its {len(neighbours.get(cell, ()))} touching neighbours, {how} "
                                f"(cap {LINK_CAP}; all-zones would need up to {worst_all_zones})"
                  + (f" ({'; '.join(problems[:3])})" if problems else ""))


def run_partyfar(rep, rig, start, day, bots):
    user, number, point = FAR_MEMBER
    rig.db_reset((86.5, 0), (94, 58))
    try:
        provision.place_bot_in_party(number, point, provision.PARTY_ID)
    except provision.ProvisionError as ex:
        rep.check(False, f"partyfar: {user} could not join the party: {ex}")
        return
    rig.request_play("fight", 99, 60, env={"MESH_NO_MONSTER_HANDOFF": "1"})
    rig.wait_play(60)
    time.sleep(10)
    far = rig.launch_bot("partyfar", user, ["-meshAutoMode", "stand", "-meshWalkX", f"{point[0]:g}", "-meshWalkZ", f"{point[1]:g}"],
                         channel="W", observe_seconds=STAND_S)
    bots.append(("stand", far))
    time.sleep(10)
    bots.append(("stand", rig.launch_bot("partyfar", "meshbot", ["-meshAutoMode", "stand", "-meshWalkX", "104", "-meshWalkZ", "58"],
                                         observe_seconds=STAND_S)))
    st = rig.wait_status(lambda st: st.get("finished") == "True", FIGHT_S, "the fighter")
    lines = {z: rig.zone_lines(z, start, day) for z in ("C", "E", "W")}
    grade_partyfar(rep, st.get("result", ""), lines, env_on_demand(rig), user + "Char")


def grade_partyfar(rep, result, lines, on_demand, member):
    """The checks of decision 14 on the fighter's RESULT and C's, E's and W's lines."""
    rep.check("-> PASS" in result, f"fighter: {result[:140]}")
    both = lines.get("C", []) + lines.get("E", [])
    rep.check(riglinks.count(both, re.compile(r"Party \d+: shared exp with \d+ here \+ \d+ on other cells \+ [1-9]\d* remote")) >= 1,
              "a kill was split with a remote party member")
    rep.check(riglinks.count(lines.get("W", []), re.compile(r"Party share applied to " + re.escape(member))) >= 1,
              f"W applied a share to {member}")
    relayed = riglinks.count(both, re.compile(r"relayed through the central"))
    broadcast = sum(riglinks.count(cell, re.compile(r"Party share for remote member .* broadcast to")) for cell in lines.values())
    if on_demand:
        rep.check(relayed >= 1 and broadcast == 0, f"remote shares went through the central ({relayed} relayed, {broadcast} broadcast)")
    else:
        rep.check(broadcast >= 1 and relayed == 0, f"remote shares were broadcast over the links ({broadcast} broadcast, {relayed} relayed)")
