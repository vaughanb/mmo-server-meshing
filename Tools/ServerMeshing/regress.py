#!/usr/bin/env python3
"""Server Meshing regression harness (DESIGN.md §16 / changelog 0.43).

Drives the 2x2 rig end to end and grades each scenario from the logs:

    python Tools/ServerMeshing/regress.py                 # the default set: pet party crash harvest build
    python Tools/ServerMeshing/regress.py crash build     # a subset
    python Tools/ServerMeshing/regress.py --list

Requirements (all already true on the rig machine):
  * the Unity editor is open on this project with BaseInit-CE (any scene that is not dirty) and the
    ServerMeshPlayRunner watching Temp/ServerMesh.PlayRequest.txt (it does whenever the editor is up);
  * a fresh player build at Build/Server/kit-ce-project.exe (the zone servers and the headless bots);
  * the MySQL database named in Config/mySqlConfig.json, with the two test characters (MeshTester2, meshbotChar).

Each scenario: resets the test characters in the database, asks the editor to enter Play with the auto
client in a given mode (plus environment variables the zone servers inherit), launches headless bots from
the build, waits for the auto client's RESULT (and the bots'), stops Play, and grades: zero exceptions on
the four zone logs since the scenario started, the expected RESULT lines, and scenario-specific log lines.
Exit code 1 when any scenario fails. Bot logs land in Logs/regress_<scenario>_<user>.log.
"""
import argparse, datetime, json, os, re, subprocess, sys, time, uuid

import pymysql   # pip install pymysql

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DB_CONFIG = os.path.join(ROOT, "Config", "mySqlConfig.json")
EXE = os.path.join(ROOT, "Build", "Server", "kit-ce-project.exe")
TEMP = os.path.join(ROOT, "Temp")
REQUEST = os.path.join(TEMP, "ServerMesh.PlayRequest.txt")
STOP = os.path.join(TEMP, "ServerMesh.StopRequest.txt")
STATUS = os.path.join(TEMP, "ServerMesh.PlayStatus.txt")
LOGS = os.path.join(ROOT, "Logs")
MAP = "ServerMeshTestMap"
MAP2 = "ServerMeshTestMap2"   # placed 400 m east of MAP in the world registry; its only cell C2 touches E
ZONES = [("C", MAP), ("E", MAP), ("N", MAP), ("NE", MAP), ("C2", MAP2)]
TESTER = "493cd2454f4949ebbf2f09748fe78af8"   # MeshTester2 (the editor client)
BOT = "8cb502da59bf43b4a654875dc2934c11"      # meshbotChar (the headless bot)
ITEM_BUILDING, ITEM_YIELD, ITEM_SKILL = 1802134084, 970799895, 357861288
ITEM_BOW = -137752354      # TestBowItem-CE, equipped in both test characters' right hand since 0.30
ENV_KEYS = ["MESH_NO_MONSTER_HANDOFF", "MESH_FAULT_DIE_ON_NOTICE", "MESH_GHOST_LEAD", "MESH_LOAD_PLAYERS", "MESH_LOAD_CELL", "MESH_LOAD_UNCAPPED", "MESH_SLEEP", "MESH_SLEEP_RINGS", "MESH_SLEEP_AFTER", "MESH_SPAWN_CELLS"]
EXTRA_ENV = {}   # --env KEY=VALUE from the command line, applied to every Play request of the run


# ------------------------------------------------------------------ rig plumbing
def zone_log(zone, suffix, day, map_id=MAP):
    return os.path.join(LOGS, f"Log_Map({map_id})-Ch({zone})-Alloc(False)-Instance()_{day}.{suffix}.log")


def read_status():
    st = {}
    try:
        for line in open(STATUS, encoding="utf-8", errors="replace"):
            if "=" in line:
                k, v = line.rstrip("\n").split("=", 1)
                st[k] = v
    except FileNotFoundError:
        pass
    return st


def wait_status(pred, timeout, what):
    end = time.time() + timeout
    while time.time() < end:
        st = read_status()
        if pred(st):
            return st
        time.sleep(2)
    raise TimeoutError(f"timed out waiting for {what}")


def ps(script):
    return subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True).stdout.strip()


def kill_bots():
    ps("Get-Process kit-ce-project -ErrorAction SilentlyContinue | Where-Object { (Get-CimInstance Win32_Process -Filter \"ProcessId=$($_.Id)\").CommandLine -like '*-meshAutoClient*' } | Stop-Process -Force")


def kit_process_count():
    out = ps("(Get-Process kit-ce-project -ErrorAction SilentlyContinue | Measure-Object).Count")
    return int(out or 0)


def launch_bot(scenario, user, args):
    log = os.path.join(LOGS, f"regress_{scenario}_{user}.log")
    if os.path.exists(log):
        os.remove(log)
    cmd = [EXE, "-batchmode", "-nographics", "-meshAutoClient", "-meshUser", user, "-meshChannel", "C"] + args + ["-logFile", log]
    subprocess.Popen(cmd, creationflags=getattr(subprocess, "DETACHED_PROCESS", 0) | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return log


def request_play(mode, walk_x, walk_z, pet=False, mount=False, env=None):
    os.makedirs(TEMP, exist_ok=True)
    lines = [f"mode={mode}", "channel=C", "user=meshtest", f"walkX={walk_x}", f"walkZ={walk_z}", f"pet={1 if pet else 0}", f"mount={1 if mount else 0}", "enabled=1"]
    env = dict(EXTRA_ENV, **(env or {}))
    for key in ENV_KEYS:
        lines.append(f"env.{key}={env.get(key, '')}")   # every key set explicitly: an empty value clears it
    with open(REQUEST, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


def stop_play():
    open(STOP, "w").close()
    try:
        wait_status(lambda st: st.get("isPlaying") == "False", 60, "Play to stop")
    except TimeoutError:
        print("  ! Play did not stop in time")
    kill_bots()
    end = time.time() + 15
    while time.time() < end and kit_process_count() > 0:
        time.sleep(1)


# ------------------------------------------------------------------ database
def db_connect():
    """The game database the editor's database manager uses (backend chosen by the server prefab's databaseOptionIndex)."""
    with open(DB_CONFIG, encoding="utf-8-sig") as f:
        c = json.load(f)
    return pymysql.connect(
        host=c.get("mySqlAddress") or "127.0.0.1", port=int(c.get("mySqlPort") or 3306),
        user=c.get("mySqlUsername") or "root", password=c.get("mySqlPassword") or "",
        database=c.get("mySqlDbName") or "mmorpg_kit", charset="utf8mb4", autocommit=True)


def db_reset(tester_pos, bot_pos, tester_party=1, bot_party=1, building_items=None, drop_yield=False, clear_buildings=False):
    conn = db_connect()
    cur = conn.cursor()
    move = ("update characters set currentHp=500,currentMp=100,currentChannel='C',currentMapName='ServerMeshTestMap',"
            "currentPositionX=%s,currentPositionY=0,currentPositionZ=%s,partyId=%s where characterName=%s")
    cur.execute(move, (tester_pos[0], tester_pos[1], tester_party, "MeshTester2"))
    cur.execute(move, (bot_pos[0], bot_pos[1], bot_party, "meshbotChar"))
    if drop_yield:
        cur.execute("delete from characteritem where characterId=%s and inventoryType=0 and dataId=%s", (TESTER, ITEM_YIELD))
    if building_items:
        # The kit stores the bag as one row per slot (empty slots have dataId 0). Reuse the building item's own row,
        # else an empty slot, else clone any occupied row (the skill item used to be the template; the fighter consumes it).
        cur.execute("select idx from characteritem where characterId=%s and inventoryType=0 and dataId=%s", (TESTER, ITEM_BUILDING))
        row = cur.fetchone()
        if row is None:
            cur.execute("select idx from characteritem where characterId=%s and inventoryType=0 and dataId=0 order by idx", (TESTER,))
            row = cur.fetchone()
        if row is not None:
            cur.execute("update characteritem set dataId=%s, amount=%s, level=1, equipSlotIndex=0, durability=0, exp=0, ammo=0"
                        " where characterId=%s and inventoryType=0 and idx=%s", (ITEM_BUILDING, building_items, TESTER, row[0]))
        else:
            cur.execute("show columns from characteritem")
            cols = [r[0] for r in cur.fetchall()]
            cur.execute("select * from characteritem where characterId=%s and inventoryType=0 and dataId<>0", (TESTER,))
            template = cur.fetchone()
            if template is None:
                raise RuntimeError("no bag row to use as a template for the building item")
            d = dict(zip(cols, template))
            d.update(dataId=ITEM_BUILDING, amount=building_items, level=1)
            if "id" in d:
                d["id"] = uuid.uuid4().hex
            cur.execute("select idx from characteritem where characterId=%s and inventoryType=0", (TESTER,))
            used = {r[0] for r in cur.fetchall()}
            d["idx"] = next(i for i in range(1, 200) if i not in used)
            cur.execute("insert into characteritem (%s) values (%s)" % (",".join("`%s`" % k for k in d), ",".join(["%s"] * len(d))), list(d.values()))
    if clear_buildings:
        cur.execute("delete from buildings where channel='E'")
    cur.close()
    conn.close()


# ------------------------------------------------------------------ grading
STAMP = re.compile(r"\[(\d{4}-\d\d-\d\d) (\d\d:\d\d:\d\d)\]")


def zone_lines(zone, start, day, map_id=MAP):
    """Every line of the zone's info/warn/error logs stamped at or after `start` (HH:MM:SS)."""
    out = []
    for suffix in ("info", "warn", "error"):
        path = zone_log(zone, suffix, day, map_id)
        if not os.path.exists(path):
            continue
        for line in open(path, encoding="utf-8", errors="replace"):
            m = STAMP.search(line[:60])
            if m and m.group(2) >= start:
                out.append(line.rstrip("\n"))
    return out


def count(lines, pattern):
    rx = re.compile(pattern)
    return sum(1 for l in lines if rx.search(l))


class Report:
    def __init__(self, name):
        self.name, self.checks, self.notes = name, [], []

    def check(self, ok, label):
        self.checks.append((bool(ok), label))
        return ok

    @property
    def passed(self):
        return all(ok for ok, _ in self.checks)


def grade_common(rep, start, day):
    for z, map_id in ZONES:
        lines = zone_lines(z, start, day, map_id)
        ex = [l for l in lines if "Exception" in l]
        rep.check(len(ex) == 0, f"zone {z}: {len(ex)} exception lines")
        for l in ex[:3]:
            rep.notes.append(f"   {z}: {l[:160]}")


def hms(t):
    h, m, s2 = t.split(":")
    return int(h) * 3600 + int(m) * 60 + int(s2)


def bot_result(log, kind):
    try:
        for line in open(log, encoding="utf-8", errors="replace"):
            if f"RESULT {kind}:" in line:
                return line.strip()[line.find("RESULT"):]
    except FileNotFoundError:
        pass
    return ""


# ------------------------------------------------------------------ scenarios
def run_scenario(name, rep):
    day = datetime.date.today().isoformat()
    start = datetime.datetime.now().strftime("%H:%M:%S")
    for marker in os.listdir(TEMP) if os.path.isdir(TEMP) else []:
        if marker.startswith("ServerMesh.FaultFired."):
            os.remove(os.path.join(TEMP, marker))
    bots = []

    if name == "pet":
        # The pet round trip: an observer 12 m from the line watches the bot cross with its pet and come back.
        db_reset((86.5, 0), (0, 0))
        request_play("observe", 88, 0)
        wait_status(lambda st: st.get("isPlaying") == "True", 60, "Play")
        time.sleep(20)
        bots.append(("walk", launch_bot(name, "meshbot", ["-meshAutoMode", "walk", "-meshWalkX", "130", "-meshWalkZ", "0", "-meshPet"])))
        st = wait_status(lambda st: st.get("finished") == "True", 240, "the observer")
        walk = wait_for_bot(bots[0][1], "walk", 240)
        rep.check("-> PASS" in st.get("result", ""), f"observer: {st.get('result', '')[:120]}")
        rep.check("playerSwaps=0 absences=0" in st.get("result", ""), "observer saw no swap and no absence")
        rep.check("-> PASS" in walk and "pet=seen" in walk, f"bot: {walk[:120]}")
        c_lines, e_lines = zone_lines("C", start, day), zone_lines("E", start, day)
        rep.check(count(e_lines, r"Summon .* promoted in place") >= 1, "E promoted the pet in place")
        rep.check(count(c_lines, r"Summon .* promoted in place") >= 1, "C promoted the pet in place on the way back")
        rep.check(count(c_lines, r"Handoff ACK .*promoted=True") >= 1 and count(e_lines, r"Handoff ACK .*promoted=True") >= 1, "both handoffs acked as promoted")

    elif name == "party":
        # Fight at the line with a party member standing inside E: shares cross the line both ways. The member
        # starts a few metres from the line so it is inside E before the first kill (from the map origin its walk
        # took longer than the fighter's first kills and the shares stayed on C).
        db_reset((86.5, 0), (94, 58))
        request_play("fight", 99, 60, env={"MESH_NO_MONSTER_HANDOFF": "1"})
        wait_status(lambda st: st.get("isPlaying") == "True", 60, "Play")
        time.sleep(20)
        bots.append(("stand", launch_bot(name, "meshbot", ["-meshAutoMode", "stand", "-meshWalkX", "104", "-meshWalkZ", "58"])))
        st = wait_status(lambda st: st.get("finished") == "True", 300, "the fighter")
        rep.check("-> PASS" in st.get("result", ""), f"fighter: {st.get('result', '')[:140]}")
        m = re.search(r"questKills\+(\d+)", st.get("result", ""))
        rep.check(m is not None and int(m.group(1)) >= 1, "the fighter's quest kill counter advanced for kills on the neighbour's monsters (kill credit forwarded)")
        # Projectiles: the tester fights with the test bow (right hand), so its hits on E's monsters are arrows that struck
        # puppets on C and were forwarded; E replays the shots on the tester's puppet so its players see them.
        conn = db_connect()
        cur = conn.cursor()
        cur.execute("select dataId from characteritem where characterId=%s and inventoryType=2", (TESTER,))
        weapon = cur.fetchone()
        cur.close()
        conn.close()
        rep.check(weapon is not None and weapon[0] == ITEM_BOW, "the fighter's right hand holds the test bow (projectile weapon)")
        c_lines = zone_lines("C", start, day)
        rep.check(count(c_lines, r"Hit on ghost .* forwarded to zone E") >= 1, "arrow hits on the neighbour's monster puppets were forwarded to E")
        rep.check(count(zone_lines("E", start, day), r"Ghost attack replayed on MeshTester2") >= 1, "E replayed the fighter's shots on its puppet")
        c_lines, e_lines = zone_lines("C", start, day), zone_lines("E", start, day)
        rep.check(count(c_lines, r"Party 1: shared") >= 1, "C split a kill with the ghost member")
        # Either direction counts: the fighter's kills on C forward to the member on E, and monsters the member kills
        # back on E forward to the fighter on C (the grading used to read only the E-to-C direction).
        both = c_lines + e_lines
        rep.check(count(both, r"Party share for .* forwarded to zone") >= 1 and count(both, r"Party share applied to") >= 1, "a share crossed the line")

    elif name == "seam":
        # Across the seam between two maps: the bot walks from E into C2 (map ServerMeshTestMap2, whose frame sits 400 m
        # east, so x=301 on E is x=-99 there) and back; the tester stands on E 8 m from the seam, so C2 must see it as a
        # ghost at the translated position (-108) and the bot's row is saved in C2's frame on the way over.
        db_reset((292, 0), (280, 0))
        request_play("stand", 292, 0)
        wait_status(lambda st: st.get("isPlaying") == "True", 60, "Play")
        time.sleep(20)
        bots.append(("walk", launch_bot(name, "meshbot", ["-meshAutoMode", "walk", "-meshWalkX", "310", "-meshReturnX", "-110", "-meshStopOnMigration"])))
        walk = wait_for_bot(bots[0][1], "walk", 240)
        rep.check("-> PASS" in walk and "migrations=2" in walk, f"bot: {walk[:120]}")
        e_lines, c2_lines = zone_lines("E", start, day), zone_lines("C2", start, day, MAP2)
        rep.check(count(e_lines, r"Handoff SENT meshbotChar -> zone C2") >= 1, "E handed the bot to C2 (another map)")
        rep.check(count(e_lines, r"frame offset \(-400") >= 1, "the save went into C2's frame (offset -400)")
        rep.check(count(e_lines, r"Handoff ACK .*promoted=True") >= 1, "C2 acked the arrival on its promoted ghost")
        rep.check(count(c2_lines, r"Handoff SENT meshbotChar -> zone E") >= 1, "C2 handed the bot back to E")
        rep.check(count(c2_lines, r"Ghost spawned Player MeshTester2 .* at \(-108") >= 1, "C2 sees the standing tester at the translated position (-108)")
        rep.check(count(c2_lines, r"Neighbour zone E linked .*frame offset \(-400") >= 1, "C2's link from E carries the frame offset")
        bot_lines = open(bots[0][1], encoding="utf-8", errors="replace").read().splitlines()
        rep.check(count(bot_lines, r"Seam: pre-loaded scene ServerMeshTestMap2 ") >= 1, "the bot pre-loaded map 2's scene in the band")
        rep.check(count(bot_lines, r"Seam: swapped to scene ServerMeshTestMap2 ") >= 1, "the bot swapped frames into map 2's scene at the hop")
        rep.check(count(bot_lines, r"Seam: swapped to scene ServerMeshTestMap ") >= 1, "the bot swapped back into map 1's scene on the return")

    elif name == "seamcold":
        # The same crossing without the client pre-load: the kit reloads the scene at the seam (loading screen). The
        # hop must still complete both ways without exceptions; no frame swap may be logged.
        db_reset((292, 0), (280, 0))
        request_play("stand", 292, 0)
        wait_status(lambda st: st.get("isPlaying") == "True", 60, "Play")
        time.sleep(20)
        bots.append(("walk", launch_bot(name, "meshbot", ["-meshAutoMode", "walk", "-meshWalkX", "310", "-meshReturnX", "-110", "-meshStopOnMigration", "-meshSeamPreload", "0"])))
        walk = wait_for_bot(bots[0][1], "walk", 240)
        rep.check("migrations=2" in walk, f"bot: {walk[:120]}")
        e_lines, c2_lines = zone_lines("E", start, day), zone_lines("C2", start, day, MAP2)
        rep.check(count(e_lines, r"Handoff SENT meshbotChar -> zone C2") >= 1, "E handed the bot to C2 (another map)")
        rep.check(count(c2_lines, r"Handoff SENT meshbotChar -> zone E") >= 1, "C2 handed the bot back to E")
        bot_lines = open(bots[0][1], encoding="utf-8", errors="replace").read().splitlines()
        rep.check(count(bot_lines, r"Seam pre-load distance set to 0") >= 1, "the bot ran without the seam pre-load")
        rep.check(count(bot_lines, r"Seam: (pre-loaded|swapped)") == 0, "no pre-load or frame swap happened")
        # The kit's scene load ends with Resources.UnloadUnusedAssets(), which Unity logs; one per hop after the walk began.
        walk_start = next((i for i, l in enumerate(bot_lines) if "Walking from" in l), 0)
        rep.check(count(bot_lines[walk_start:], r"Unloading \d+ unused Assets") >= 2, "the kit reloaded the scene at each crossing")

    elif name == "sleep":
        # Sleeping cells: nothing starts at boot. The tester's login into C wakes C, whose ring (1 on the rig) wakes E, N
        # and NE; C2 stays asleep. The bot logs in on E's west half (which wakes C2, 100 m ahead of the seam), walks across the
        # seam into C2 and back — seamless. Then the bot leaves; with only the tester on C, C2 is unwanted and exits after the
        # grace period.
        # The bot logs in on E's west half: its login wakes C2 (E's ring), 90 m ahead of the seam. Not at (200, 0): the test
        # map's WarpPortal_E_to_W stands there and warps whoever spawns inside it to (-200, 1, 0) (DESIGN 0.55).
        db_reset((20, 0), (210, 0))
        request_play("stand", 20, 0, env={"MESH_SLEEP": "1", "MESH_SLEEP_RINGS": "1", "MESH_SLEEP_AFTER": "30"})
        wait_status(lambda st: st.get("isPlaying") == "True", 60, "Play")
        time.sleep(25)
        c2_before = zone_lines("C2", start, day, MAP2)
        rep.check(count(c2_before, r"Own cell from channel id: C2") == 0, "C2 was not started at boot (asleep)")
        rep.check(count(zone_lines("C", start, day), r"Own cell from channel id: C") >= 1, "C woke for the tester's login")
        bots.append(("walk", launch_bot(name, "meshbot", ["-meshAutoMode", "walk", "-meshWalkX", "310", "-meshReturnX", "-110", "-meshStopOnMigration"])))
        walk = wait_for_bot(bots[0][1], "walk", 300)
        rep.check("-> PASS" in walk and "migrations=2" in walk, f"bot: {walk[:120]}")
        e_lines, c2_lines = zone_lines("E", start, day), zone_lines("C2", start, day, MAP2)
        rep.check(count(c2_lines, r"Own cell from channel id: C2") >= 1, "C2 woke once the bot was on E")
        woke = next((STAMP.search(l[:60]).group(2) for l in c2_lines if "Own cell from channel id: C2" in l), None)
        entered_e = next((STAMP.search(l[:60]).group(2) for l in e_lines if "player registered meshbotChar" in l), None)
        hop = next((STAMP.search(l[:60]).group(2) for l in e_lines if "Handoff START meshbotChar" in l), None)
        rep.check(woke is not None and entered_e is not None and entered_e <= woke, f"C2 woke after the bot arrived on E (arrived {entered_e}, serving {woke})")
        rep.check(woke is not None and hop is not None and hms(hop) - hms(woke) >= 5, f"C2 was serving at least 5 s before the bot's crossing (serving {woke}, crossing {hop})")
        rep.check(count(e_lines, r"Handoff ACK meshbotChar by zone C2:.*promoted=") >= 1, "the crossing into the woken cell was acked")
        kill_bots()
        time.sleep(75)
        c2_lines = zone_lines("C2", start, day, MAP2)
        rep.check(count(c2_lines, r"Sleeping: cell C2 has nobody near it") >= 1, "C2 went to sleep once nobody was near")
        rep.check(count(zone_lines("E", start, day), r"Sleeping: cell E") == 0, "E stayed up (in the tester's ring)")

    elif name == "crash":
        # The destination dies on the handoff notice: rollback, client fallback, MapSpawn restart, re-registration.
        db_reset((86.5, 0), (0, 0))
        request_play("observe", 88, 0, env={"MESH_FAULT_DIE_ON_NOTICE": "E"})
        wait_status(lambda st: st.get("isPlaying") == "True", 60, "Play")
        time.sleep(20)
        bots.append(("walk", launch_bot(name, "meshbot", ["-meshAutoMode", "walk", "-meshWalkX", "130", "-meshWalkZ", "0", "-meshPet"])))
        st = wait_status(lambda st: st.get("finished") == "True", 240, "the observer")
        walk = wait_for_bot(bots[0][1], "walk", 300)
        rep.check("-> PASS" in walk, f"bot: {walk[:120]}")
        rep.check("fellBack=True" in open(bots[0][1], encoding="utf-8", errors="replace").read(), "the client fell back to the origin")
        rep.check("playerSwaps=0" in st.get("result", ""), f"observer: {st.get('result', '')[:120]}")
        c_lines, e_lines = zone_lines("C", start, day), zone_lines("E", start, day)
        rep.check(count(e_lines, r"FAULT: dying on handoff notice") == 1, "E died once on the notice")
        rep.check(count(c_lines, r"Handoff ROLLBACK ") >= 1, "C rolled the handoff back")
        rep.check(count(c_lines, r"ROLLBACK complete") >= 1, "the client's return promoted the held puppet")
        rep.check(count(e_lines, r"Cluster registration accepted") >= 1, "the restarted E re-registered with the cluster")
        rep.check(count(c_lines, r"Handoff ACK .*promoted=") >= 1, "a later handoff to the restarted E was acked")

    elif name == "harvest":
        db_reset((86.5, 62), (0, 0), drop_yield=True)
        request_play("harvest", 99, 62, env={"MESH_NO_MONSTER_HANDOFF": "1"})
        wait_status(lambda st: st.get("isPlaying") == "True", 60, "Play")
        st = wait_status(lambda st: st.get("finished") == "True", 240, "the harvester")
        rep.check("-> PASS" in st.get("result", "") and f"itemsGained=[{ITEM_YIELD}x" in st.get("result", ""), f"harvester: {st.get('result', '')[:140]}")
        c_lines, e_lines = zone_lines("C", start, day), zone_lines("E", start, day)
        rep.check(count(e_lines, r"Forwarded hit applied to static") >= 1, "E applied forwarded swings to its node")
        rep.check(count(c_lines, r"GiveItems applied") >= 1, "the yield reached the real character on C")

    elif name == "build":
        db_reset((86.5, 66), (0, 0), building_items=3, clear_buildings=True)
        request_play("build", 99, 66, env={"MESH_NO_MONSTER_HANDOFF": "1"})
        wait_status(lambda st: st.get("isPlaying") == "True", 60, "Play")
        st = wait_status(lambda st: st.get("finished") == "True", 240, "the builder")
        rep.check("-> PASS" in st.get("result", "") and "appeared=True" in st.get("result", ""), f"builder: {st.get('result', '')[:140]}")
        c_lines, e_lines = zone_lines("C", start, day), zone_lines("E", start, day)
        rep.check(count(c_lines, r"belongs to zone E") >= 1, "C handed the placement to E")
        rep.check(count(e_lines, r"creating here") >= 1, "E created the building")
        rep.check(count(c_lines, r"Building ghost spawned") >= 1, "the building's ghost appeared on C")
        rep.check(count(e_lines, r"Forwarded hit applied to static") >= 1, "E applied forwarded swings to the wall")

    elif name == "pvp":
        # A party member's ghost must not be attackable; the map's PvP mode must be Pvp for the stranger half,
        # so this scenario only checks the ally refusal under the map's current rules.
        db_reset((86.5, 60), (0, 0))
        request_play("pvp", 99, 60, env={"MESH_NO_MONSTER_HANDOFF": "1"})
        wait_status(lambda st: st.get("isPlaying") == "True", 60, "Play")
        time.sleep(20)
        bots.append(("stand", launch_bot(name, "meshbot", ["-meshAutoMode", "stand", "-meshWalkX", "103", "-meshWalkZ", "60"])))
        st = wait_status(lambda st: st.get("finished") == "True", 300, "the attacker")
        rep.check("targetHurt=False" in st.get("result", ""), f"attacker: {st.get('result', '')[:140]}")
        c_lines = zone_lines("C", start, day)
        rep.check(count(c_lines, r"Hit on ghost meshbotChar") == 0, "no hit on the party member's ghost was forwarded")

    else:
        raise ValueError(f"unknown scenario {name}")

    grade_common(rep, start, day)
    for kind, log in bots:
        try:
            txt = open(log, encoding="utf-8", errors="replace").read()
            n = txt.count("Exception")
            rep.check(n == 0, f"bot log {os.path.basename(log)}: {n} exception lines")
        except FileNotFoundError:
            rep.check(False, f"bot log {os.path.basename(log)} missing")


def wait_for_bot(log, kind, timeout):
    end = time.time() + timeout
    while time.time() < end:
        r = bot_result(log, kind)
        if r:
            return r
        time.sleep(3)
    return ""


DEFAULT = ["pet", "party", "crash", "harvest", "build", "seam", "seamcold", "sleep"]
ALL = DEFAULT + ["pvp"]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("scenarios", nargs="*", help=f"any of {', '.join(ALL)} (default: {' '.join(DEFAULT)})")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--env", action="append", default=[], metavar="KEY=VALUE", help=f"environment for the zones, any of {', '.join(ENV_KEYS)}")
    args = ap.parse_args()
    for kv in args.env:
        k, _, v = kv.partition("=")
        if k not in ENV_KEYS:
            print(f"unknown env key {k}; known: {', '.join(ENV_KEYS)}"); return 2
        EXTRA_ENV[k] = v
    if args.list:
        print("\n".join(ALL)); return 0
    names = args.scenarios or DEFAULT
    for n in names:
        if n not in ALL:
            print(f"unknown scenario {n}; known: {', '.join(ALL)}"); return 2
    if not os.path.exists(EXE):
        print(f"no player build at {EXE}"); return 2
    if read_status().get("isPlaying") == "True":
        print("the editor is already in Play mode; stop it first"); return 2
    kill_bots()
    reports = []
    for n in names:
        rep = Report(n)
        t0 = time.time()
        print(f"=== {n}")
        try:
            run_scenario(n, rep)
        except Exception as ex:  # a harness failure is a failed scenario, never a crash of the run
            rep.check(False, f"harness: {ex}")
        finally:
            stop_play()
        rep.notes.append(f"   {time.time() - t0:0.0f} s")
        for ok, label in rep.checks:
            print(f"  [{'ok' if ok else 'FAIL'}] {label}")
        for note in rep.notes:
            print(note)
        reports.append(rep)
        time.sleep(5)  # the zone processes exit with Play; give the ports a moment
    print("\n=== summary")
    for rep in reports:
        print(f"  {rep.name:8s} {'PASS' if rep.passed else 'FAIL'}  ({sum(1 for ok, _ in rep.checks if ok)}/{len(rep.checks)} checks)")
    return 0 if all(r.passed for r in reports) else 1


if __name__ == "__main__":
    sys.exit(main())
