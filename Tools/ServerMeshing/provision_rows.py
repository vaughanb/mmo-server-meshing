"""The kit's tables as the rig writes them (DESIGN.md 0.71), split out of provision (the ~500-line rule): the data ids,
the fixtures' accounts and characters as rows, the connection to the editor's database, the low-level writes (insert,
delete_character) and the Play guard. provision (the fixtures, the bots' positions, the command line) and
provision_burst (step 9's burst rows) build on it, and provision re-exports every name here. Nothing here runs at
import or opens Config/mySqlConfig.json before db_connect is called.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py" (test_provision)
"""
import hashlib
import json
import os
import uuid

import rigtools

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DB_CONFIG = os.path.join(ROOT, "Config", "mySqlConfig.json")
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


def editor_is_playing(status_path=PLAY_STATUS):
    return rigtools.read_status_file(status_path).get("isPlaying") == "True"
