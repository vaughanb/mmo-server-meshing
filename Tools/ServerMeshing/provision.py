#!/usr/bin/env python3
"""Provision the rig's test accounts and characters in the kit's MySQL database (DESIGN.md 0.71).

    python Tools/ServerMeshing/provision.py           # create the fixtures, or put them back to their initial state
    python Tools/ServerMeshing/provision.py --check   # report whether they are present and complete; change nothing
    python Tools/ServerMeshing/provision.py --remove  # take the two rig accounts and their characters out again
    python Tools/ServerMeshing/provision.py --create-schema mmorpg_kit_rig   # a new, empty database from the kit's script

--database NAME works on that database instead of the one Config/mySqlConfig.json names.

The scenarios play two accounts: meshtest (the editor's auto client, character MeshTester2) and meshbot (the
headless bot, character meshbotChar), password meshtest123. Each character is level 1 of the demo class, holds the
test bow in its right hand, carries the skill item stack, the pet, the mount (and, for the tester, the building
item), has learned both test skills, has the kill quest, and both are in party 1 with the tester as leader. The
load-test bots (meshbot2..N) register and create their own characters on first login and need nothing here.

regress.py's db_reset calls provision() before every scenario, so each one starts from this state rather than from
whatever the previous scenario left behind (a consumed stack, a looted yield, a saved summon).

Phase 0 step 8b's bots (meshbot2..N) are not fixtures: reset_bot_positions(first, n, points) only moves their
characters to a run's points (bot_points(n, placement), from botpoints), and refuses one it does not recognise.

Safety: only the two rig accounts and their characters are ever written or deleted. A fixture name owned by another
account, or party 1 led by someone else, stops the run before anything changes. Everything happens in one
transaction. It refuses while the editor is in Play, because the database manager there keeps its own cache and
would overwrite (or ignore) rows written underneath it.
"""
import argparse
import hashlib
import json
import os
import re
import sys
import uuid

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rigtools
from botpoints import bot_points  # noqa: F401 -- step 8b's placements (decision 6), kept beside the reset

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DB_CONFIG = os.path.join(ROOT, "Config", "mySqlConfig.json")
SCHEMA_SQL = os.path.join(ROOT, "Assets", "OpenMMORPG", "MMO", "SQLs", "mysql_main.sql")
PLAY_STATUS = os.path.join(ROOT, "Temp", "ServerMesh.PlayStatus.txt")
PASSWORD = "meshtest123"   # ServerMeshAutoClient.password
MAP = "ServerMeshTestMap"
PARTY_ID = 1

# Data ids are the kit's GenerateHashId over the asset's Id (its `id` field, or the file name when that is empty):
# BaseGameData.MakeDataId. Several test assets set an id that differs from the file name, so the Id strings are
# listed here and test_provision.py checks each constant against the hash.
PLAYER_CLASS = 1329388283     # BaseCharacter-CE (id empty: the name)
PLAYER_ENTITY = 1691305646    # BaseCharacter-CE.prefab, LiteNetLibIdentity assetId b59acfa1bf90f734ca5c88514180177a
ITEM_BOW = -137752354         # TestBowItem-CE, id "testBow"
ITEM_SKILL = 357861288        # TestSkillItem-CE, id "testSkillItem" (casts TestDebuffSkill)
ITEM_PET = -667740944         # TestPetItem-CE, id "testPetItem"
ITEM_MOUNT = 1275161172       # TestMountItem-CE, id "testMountItem"
ITEM_BUILDING = 1802134084    # TestBuildingItem-CE, id "TestBuildingItem-CE"
ITEM_YIELD = 970799895        # DefaultWeaponItem-CE (id empty: the name), the harvestable's only yield
SKILL_DEBUFF = 2091623501     # TestDebuffSkill-CE, id "TestDebuffSkill"
SKILL_HEAL = 254850431        # TestHealSkill-CE, id "TestHealSkill"
QUEST_KILL = -1176535496      # TestKillQuest-CE, id "testKillQuest" (kill BaseEnemy-CE x100)

# The kit's hit chance is 2 x attacker level / (attacker level + target level), clamped to 5-95%
# (DefaultGameplayRule.GetHitChance). The test maps' monsters are level 1, so they hit a level-1 character 95% of the
# time: the party scenario's bait, standing among E's monsters, died before the fighter's first kill. At level 20 they
# hit about 9.5% of the time. The characters the rig used before 0.71 had levelled far past 1 over its runs (a party
# run is worth a level); the demo class gains no HP per level, so the level changes nothing else about them.
FIXTURE_LEVEL = 20
INVENTORY_BAG, INVENTORY_WEAPON_RIGHT = 0, 2   # the kit's InventoryType
ITEM_VERSION = 2                               # CharacterItem.CURRENT_VERSION
BASE_HP, BASE_STAMINA = 500, 10                # BaseCharacter-CE's base stats (mp, food and water are 0)


class Item:
    """Item is one stack the fixture puts in a character's bag."""

    def __init__(self, data_id, amount=1, level=1):
        self.data_id, self.amount, self.level = data_id, amount, level


class Fixture:
    """Fixture is one rig account and its only character."""

    def __init__(self, username, character_id, character_name, bag):
        self.username = username
        self.character_id = character_id
        self.character_name = character_name
        self.bag = bag

    @property
    def email(self):
        return f"{self.username}@example.com"


class Placement:
    """Placement is where db_reset puts a character for a scenario, and which party it is in (0: none)."""

    def __init__(self, x=0.0, z=0.0, party=PARTY_ID):
        self.x, self.z, self.party = x, z, party


COMMON_BAG = [Item(ITEM_SKILL, amount=20), Item(ITEM_PET, level=10), Item(ITEM_MOUNT)]
# The ids are the ones the characters had before 0.71, so older logs and notes still name the same characters.
TESTER = Fixture("meshtest", "493cd2454f4949ebbf2f09748fe78af8", "MeshTester2", COMMON_BAG + [Item(ITEM_BUILDING, amount=3)])
BOT = Fixture("meshbot", "8cb502da59bf43b4a654875dc2934c11", "meshbotChar", COMMON_BAG)
FIXTURES = [TESTER, BOT]
LEADER = TESTER


def db_connect(database=None):
    """db_connect opens the game database the editor's database manager uses (Config/mySqlConfig.json), or another
    database on the same server when one is named.

    An UPDATE's rowcount is the number of rows it matched, not the number it changed, so a caller can check that it
    found its row even when the values were already in place.
    """
    try:
        import pymysql   # pip install pymysql; imported here so --unit and --build run without it
        from pymysql.constants import CLIENT
    except ImportError as ex:
        raise RuntimeError("the harness needs pymysql for the test characters: pip install pymysql") from ex
    with open(DB_CONFIG, encoding="utf-8-sig") as f:
        c = json.load(f)
    return pymysql.connect(
        host=c.get("mySqlAddress") or "127.0.0.1", port=int(c.get("mySqlPort") or 3306),
        user=c.get("mySqlUsername") or "root", password=c.get("mySqlPassword") or "",
        database=database or c.get("mySqlDbName") or "mmorpg_kit", charset="utf8mb4", autocommit=True,
        client_flag=CLIENT.FOUND_ROWS)


def _i32(value):
    value &= 0xFFFFFFFF
    return value - 0x100000000 if value & 0x80000000 else value


def generate_hash_id(text):
    """generate_hash_id is the kit's GenericUtils.GenerateHashId (data ids over an asset's Id, entity ids over a prefab's assetId)."""
    if not text:
        return 0
    hash1 = hash2 = 5381
    for i in range(0, len(text), 2):
        if text[i] == "\0":
            break
        hash1 = _i32(_i32(hash1 * 33) ^ ord(text[i]))
        if i == len(text) - 1 or text[i + 1] == "\0":
            break
        hash2 = _i32(_i32(hash2 * 33) ^ ord(text[i + 1]))
    return _i32(hash1 + hash2 * 1566083941)


def hashed_password(password):
    """hashed_password is what DefaultDatabaseUserLogin stores: lowercase hex MD5 of the UTF-8 text (the kit sets no salt)."""
    return hashlib.md5(password.encode("utf-8")).hexdigest()


def new_id():
    return uuid.uuid4().hex


def character_row(fixture, user_id, placement):
    return {
        "id": fixture.character_id, "userId": user_id, "dataId": PLAYER_CLASS, "entityId": PLAYER_ENTITY, "factionId": 0,
        "characterName": fixture.character_name, "level": FIXTURE_LEVEL, "exp": 0,
        "currentHp": BASE_HP, "currentMp": 100, "currentStamina": BASE_STAMINA, "currentFood": 0, "currentWater": 0,
        "equipWeaponSet": 0, "partyId": placement.party, "guildId": 0,
        "currentChannel": "C", "currentMapName": MAP,
        "currentPositionX": placement.x, "currentPositionY": 0, "currentPositionZ": placement.z,
        "respawnMapName": MAP, "respawnPositionX": 0, "respawnPositionY": 1, "respawnPositionZ": 0,
    }


def item_row(character_id, inventory_type, idx, item):
    return {
        "id": new_id(), "idx": idx, "inventoryType": inventory_type, "characterId": character_id,
        "dataId": item.data_id, "level": item.level, "amount": item.amount, "equipSlotIndex": 0, "durability": 0,
        "exp": 0, "lockRemainsDuration": 0, "expireTime": 0, "randomSeed": 0, "ammo": 0, "sockets": "",
        "version": ITEM_VERSION,
    }


def item_rows(fixture):
    """item_rows are the bow in the right hand of weapon set 0 (idx is the set) and the bag, one row per slot."""
    rows = [item_row(fixture.character_id, INVENTORY_WEAPON_RIGHT, 0, Item(ITEM_BOW))]
    rows += [item_row(fixture.character_id, INVENTORY_BAG, idx, item) for idx, item in enumerate(fixture.bag)]
    return rows


def skill_rows(fixture):
    # The kit keys a skill row as '<characterId>_<dataId>' (MySQLDatabase_CharacterSkill). The demo class cannot learn
    # these skills; nothing in the project calls ValidateCharacterData, so the rows are kept on load.
    return [{"id": f"{fixture.character_id}_{skill}", "characterId": fixture.character_id, "dataId": skill, "level": 1}
            for skill in (SKILL_DEBUFF, SKILL_HEAL)]


def quest_row(fixture):
    return {"id": f"{fixture.character_id}_{QUEST_KILL}", "characterId": fixture.character_id, "dataId": QUEST_KILL,
            "randomTasksIndex": 0, "isComplete": 0, "completeTime": 0, "isTracking": 0,
            "killedMonsters": "", "completedTasks": ""}


def party_row():
    return {"id": PARTY_ID, "shareExp": 1, "shareItem": 1, "leaderId": LEADER.character_id}


class ProvisionError(RuntimeError):
    """ProvisionError is a state provisioning will not touch, or a write that did not land."""


def insert(cur, table, row):
    cols = list(row)
    cur.execute("insert into `%s` (%s) values (%s)" % (table, ",".join("`%s`" % c for c in cols), ",".join(["%s"] * len(cols))),
                [row[c] for c in cols])
    if cur.rowcount != 1:
        raise ProvisionError(f"insert into {table} wrote {cur.rowcount} rows, expected 1: {row.get('id')}")


def character_tables(cur):
    """character_tables are the tables with a characterId column: a character's relates data, whatever the kit version adds."""
    cur.execute("select table_name from information_schema.columns where table_schema=database() and column_name='characterId'")
    return sorted(r[0] for r in cur.fetchall())


def delete_character(cur, character_id, tables):
    """delete_character removes a character and everything keyed by it, like the kit's DeleteCharacter."""
    for table in tables:
        cur.execute("delete from `%s` where characterId=%%s" % table, (character_id,))
    cur.execute("delete from friend where characterId1=%s or characterId2=%s", (character_id, character_id))
    cur.execute("delete from storage_reservation where reserverId=%s", (character_id,))
    cur.execute("delete from charactermount where id=%s", (character_id,))
    cur.execute("delete from character_pk where id=%s", (character_id,))
    cur.execute("delete from characters where id=%s", (character_id,))


def upsert_account(cur, fixture):
    """upsert_account returns the account's id, creating it or resetting its password and email. Matched by username."""
    cur.execute("select id from userlogin where username=%s", (fixture.username,))
    rows = cur.fetchall()
    if len(rows) > 1:
        raise ProvisionError(f"{len(rows)} accounts named {fixture.username}")
    if rows:
        user_id = rows[0][0]
        cur.execute("update userlogin set password=%s, email=%s, authType=1 where id=%s",
                    (hashed_password(PASSWORD), fixture.email, user_id))
        return user_id
    user_id = new_id()
    insert(cur, "userlogin", {"id": user_id, "username": fixture.username, "password": hashed_password(PASSWORD),
                              "email": fixture.email, "authType": 1})
    return user_id


def refuse_foreign_owners(cur, user_ids):
    """Stop before any write when a fixture's name or id, or party 1, belongs to someone outside the rig accounts."""
    rig = set(user_ids.values())
    for fixture in FIXTURES:
        cur.execute("select id, userId from characters where characterName=%s or id=%s",
                    (fixture.character_name, fixture.character_id))
        for character_id, user_id in cur.fetchall():
            if user_id not in rig:
                raise ProvisionError(f"character {character_id} ({fixture.character_name}) belongs to account {user_id},"
                                     " which is not a rig account; not touching it")
    cur.execute("select leaderId from party where id=%s", (PARTY_ID,))
    row = cur.fetchone()
    fixture_ids = {f.character_id for f in FIXTURES}
    if row is not None and row[0] not in fixture_ids:
        raise ProvisionError(f"party {PARTY_ID} is led by {row[0]}, not a rig character; not touching it")


def provision(conn, placements=None, log=print):
    """
    provision puts both rig accounts and characters into their initial state inside one transaction, and returns
    {character name: character id}. placements maps a character name to its Placement (default: the map origin, party 1).
    Other characters of the rig accounts are deleted (a character the auto client created, for instance: it would be
    picked instead of the fixture when it was saved more recently).
    """
    placements = placements or {}
    cur = conn.cursor()
    try:
        conn.begin()
        user_ids = {f.username: upsert_account(cur, f) for f in FIXTURES}
        refuse_foreign_owners(cur, user_ids)
        tables = character_tables(cur)
        fixture_ids = {f.character_id for f in FIXTURES}
        for fixture in FIXTURES:
            cur.execute("select id, characterName from characters where userId=%s", (user_ids[fixture.username],))
            for character_id, name in cur.fetchall():
                if character_id not in fixture_ids:
                    log(f"deleting {name} ({character_id}): the only character of {fixture.username} is {fixture.character_name}")
                delete_character(cur, character_id, tables)
        cur.execute("delete from party where id=%s or leaderId in (%s, %s)", (PARTY_ID, TESTER.character_id, BOT.character_id))
        for fixture in FIXTURES:
            insert(cur, "characters", character_row(fixture, user_ids[fixture.username],
                                                    placements.get(fixture.character_name, Placement())))
            for row in item_rows(fixture):
                insert(cur, "characteritem", row)
            for row in skill_rows(fixture):
                insert(cur, "characterskill", row)
            insert(cur, "characterquest", quest_row(fixture))
        insert(cur, "party", party_row())
        problems = check(cur)
        if problems:
            raise ProvisionError("provisioned rows do not check out: " + "; ".join(problems))
        conn.commit()
    except BaseException:
        conn.rollback()
        raise
    finally:
        cur.close()
    return {f.character_name: f.character_id for f in FIXTURES}


def check(cur):
    """check returns what is missing or wrong in the fixtures ([] when they are complete)."""
    problems = []
    for fixture in FIXTURES:
        cur.execute("select id, password from userlogin where username=%s", (fixture.username,))
        account = cur.fetchone()
        if account is None:
            problems.append(f"no account {fixture.username}")
            continue
        if account[1] != hashed_password(PASSWORD):
            problems.append(f"{fixture.username}'s password is not {PASSWORD}")
        cur.execute("select id, characterName, dataId, entityId, currentMapName, level from characters where userId=%s", (account[0],))
        characters = cur.fetchall()
        mine = [c for c in characters if c[0] == fixture.character_id]
        if not mine:
            problems.append(f"{fixture.username} has no character {fixture.character_name} ({fixture.character_id})")
            continue
        others = [c[1] for c in characters if c[0] != fixture.character_id]
        if others:
            problems.append(f"{fixture.username} has other characters {others}")
        _, name, data_id, entity_id, map_name, level = mine[0]
        if name != fixture.character_name or data_id != PLAYER_CLASS or entity_id != PLAYER_ENTITY:
            problems.append(f"{fixture.character_id} is {name} class {data_id} entity {entity_id}")
        if level < FIXTURE_LEVEL:
            problems.append(f"{name} is level {level}, below {FIXTURE_LEVEL}")
        if map_name not in (MAP, "ServerMeshTestMap2"):
            problems.append(f"{name} is on map {map_name}")
        cur.execute("select dataId from characteritem where characterId=%s and inventoryType=%s",
                    (fixture.character_id, INVENTORY_WEAPON_RIGHT))
        if [r[0] for r in cur.fetchall()] != [ITEM_BOW]:
            problems.append(f"{name}'s right hand is not exactly the test bow")
        cur.execute("select dataId from characteritem where characterId=%s and inventoryType=%s and amount>0",
                    (fixture.character_id, INVENTORY_BAG))
        bag = {r[0] for r in cur.fetchall()}
        missing = [item.data_id for item in fixture.bag if item.data_id not in bag]
        if missing:
            problems.append(f"{name}'s bag lacks {missing}")
        cur.execute("select dataId from characterskill where characterId=%s", (fixture.character_id,))
        skills = {r[0] for r in cur.fetchall()}
        if not {SKILL_DEBUFF, SKILL_HEAL} <= skills:
            problems.append(f"{name} has not learned both test skills")
        cur.execute("select count(*) from characterquest where characterId=%s and dataId=%s", (fixture.character_id, QUEST_KILL))
        if cur.fetchone()[0] != 1:
            problems.append(f"{name} does not have the kill quest")
    cur.execute("select leaderId, shareExp, shareItem from party where id=%s", (PARTY_ID,))
    if cur.fetchone() != (LEADER.character_id, 1, 1):
        problems.append(f"party {PARTY_ID} is missing or not led by {LEADER.character_name} with shared exp and items")
    return problems


def remove(conn, log=print):
    """remove deletes the rig accounts, their characters and a party one of them leads, in one transaction."""
    cur = conn.cursor()
    try:
        conn.begin()
        tables = character_tables(cur)
        for fixture in FIXTURES:
            cur.execute("select id from userlogin where username=%s", (fixture.username,))
            for (user_id,) in cur.fetchall():
                cur.execute("select id, characterName from characters where userId=%s", (user_id,))
                for character_id, name in cur.fetchall():
                    log(f"deleting character {name} ({character_id})")
                    delete_character(cur, character_id, tables)
                cur.execute("delete from userlogin where id=%s", (user_id,))
                log(f"deleted account {fixture.username}")
        cur.execute("delete from party where leaderId in (%s, %s)", (TESTER.character_id, BOT.character_id))
        conn.commit()
    except BaseException:
        conn.rollback()
        raise
    finally:
        cur.close()


def split_sql(text):
    """split_sql cuts an SQL dump into statements: '--' comment lines dropped, a statement ends with ';' at a line end."""
    statements, current = [], []
    for line in text.splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith("--"):
            continue
        current.append(line)
        if stripped.endswith(";"):
            statements.append("\n".join(current).strip()[:-1].strip())
            current = []
    if current:
        raise ValueError("the script ends inside a statement: " + " ".join(current)[:120])
    return statements


def create_schema(conn, name, sql_path=SCHEMA_SQL, log=print):
    """create_schema makes an empty database and loads the kit's schema script into it (every migration included)."""
    if not re.fullmatch(r"[A-Za-z0-9_]{1,64}", name):
        raise ProvisionError(f"{name!r} is not a plain database name")
    with open(sql_path, encoding="utf-8-sig") as f:
        statements = split_sql(f.read())
    with conn.cursor() as cur:
        cur.execute("select count(*) from information_schema.tables where table_schema=%s", (name,))
        if cur.fetchone()[0] > 0:
            raise ProvisionError(f"database {name} already has tables; not loading the schema over them")
        cur.execute("create database if not exists `%s` character set utf8mb3 collate utf8mb3_unicode_ci" % name)
        cur.execute("use `%s`" % name)
        for statement in statements:
            cur.execute(statement)
        cur.execute("select count(*) from information_schema.tables where table_schema=%s", (name,))
        log(f"created database {name}: {cur.fetchone()[0]} tables from {os.path.basename(sql_path)}")


def editor_is_playing(status_path=PLAY_STATUS):
    return rigtools.read_status_file(status_path).get("isPlaying") == "True"


def bot_characters(cur, first, n):
    """[(user, character id)] of meshbot<first>..meshbot<first+n-1>'s characters (<user>Char, each bot's own, on
    ServerMeshTestMap); ProvisionError before any write for an unregistered bot, a missing character, a character of
    that name owned by another account (refuse_foreign_owners' rule) or one on another map. Reads only."""
    found = []
    for user in (f"meshbot{k}" for k in range(first, first + n)):
        cur.execute("select id from userlogin where username=%s", (user,))
        accounts = [row[0] for row in cur.fetchall()]
        cur.execute("select id, userId, currentMapName from characters where characterName=%s", (user + "Char",))
        rows = cur.fetchall()
        for character_id, user_id, _ in rows:
            if user_id not in accounts[:1]:
                raise ProvisionError(f"character {character_id} ({user}Char) belongs to account {user_id}, which is "
                                     f"not {user}; not touching it")
        if len(accounts) != 1 or len(rows) != 1:
            raise ProvisionError(f"{len(accounts)} account(s) {user} and {len(rows)} character(s) {user}Char; "
                                 "register the bots first (p0load register-bots)")
        if rows[0][2] != MAP:
            raise ProvisionError(f"{user}Char is on map {rows[0][2]}, not {MAP}; not touching it")
        found.append((user, rows[0][0]))
    return found


def reset_bot_positions(first, n, points, connect=None, status_path=PLAY_STATUS):
    """Saves step 8b's bots meshbot<first>..meshbot<first+n-1> at the run's points ((x, z[, ...]) each, on cell C's
    channel) in one transaction, so a bot never starts where the last run left it (decision 6); returns n. Refuses
    (ProvisionError) while the editor is in Play (as regress.db_reset), for first below 2 (meshbot is the regress
    fixture), a point count other than n, and anything bot_characters refuses; only those characters are written.
    connect opens the database (default db_connect); the connection is closed."""
    if first < 2:
        raise ProvisionError(f"meshbot{first}: step 8b's bots are meshbot2 and up (meshbot is the regress fixture)")
    if n < 1 or len(points) != n:
        raise ProvisionError(f"{len(points)} points for {n} bots")
    if editor_is_playing(status_path):
        raise ProvisionError("the editor is still in Play; the bots' positions can only be reset while it is stopped")
    conn = (connect or db_connect)()
    cur = conn.cursor()
    try:
        conn.begin()
        for (user, character_id), point in zip(bot_characters(cur, first, n), points):
            cur.execute("update characters set currentPositionX=%s, currentPositionY=0, currentPositionZ=%s, "
                        "currentChannel='C' where id=%s and currentMapName=%s",
                        (float(point[0]), float(point[1]), character_id, MAP))
            if cur.rowcount != 1:
                raise ProvisionError(f"the position of {user}Char ({character_id}) matched {cur.rowcount} rows")
        conn.commit()
    except BaseException:
        conn.rollback()
        raise
    finally:
        cur.close()
        conn.close()
    return n


def main():
    try:
        return run(parse_args())
    except ProvisionError as ex:
        print(f"provision: {ex}", file=sys.stderr)
        return 1


def parse_args():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    mode = ap.add_mutually_exclusive_group()
    mode.add_argument("--check", action="store_true", help="report what is missing; change nothing")
    mode.add_argument("--remove", action="store_true", help="delete the rig accounts and their characters")
    mode.add_argument("--create-schema", metavar="NAME", help="create an empty database and load the kit's schema into it")
    ap.add_argument("--database", metavar="NAME", help="the database to work on (default: the one in Config/mySqlConfig.json)")
    return ap.parse_args()


def run(args):
    if args.create_schema:
        conn = db_connect(args.database)
        try:
            create_schema(conn, args.create_schema)
        finally:
            conn.close()
        return 0
    conn = db_connect(args.database)
    try:
        if args.check:
            cur = conn.cursor()
            problems = check(cur)
            cur.close()
            for p in problems:
                print("MISSING:", p)
            print("fixtures complete" if not problems else f"{len(problems)} problem(s); run provision.py to fix them")
            return 1 if problems else 0
        if editor_is_playing():
            print("the editor is in Play: stop it first (its database manager would overwrite these rows)", file=sys.stderr)
            return 2
        if args.remove:
            remove(conn)
            return 0
        ids = provision(conn)
        for name, character_id in ids.items():
            print(f"provisioned {name} ({character_id})")
        return 0
    finally:
        conn.close()


if __name__ == "__main__":
    sys.exit(main())
