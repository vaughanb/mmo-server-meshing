"""Unit tests for provision and botpoints (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py").

No test opens a database or Config/mySqlConfig.json: queries go to FakeCursor stand-ins.
"""
import tempfile
import os
import unittest

import botpoints
import provision as p


class FakeCursor:
    """FakeCursor answers each query from a list of (sql fragment, rows) pairs and records every statement."""

    def __init__(self, answers):
        self.answers = answers
        self.statements = []
        self._rows = []

    def execute(self, sql, params=None):
        self.statements.append((sql, params))
        self._rows = next((rows for fragment, rows in self.answers if fragment in sql), [])

    def fetchall(self):
        return list(self._rows)

    def fetchone(self):
        return self._rows[0] if self._rows else None


class HashTests(unittest.TestCase):
    def test_generate_hash_id(self):
        cases = {
            "empty": ("", 0),
            "file name is not the id": ("TestBowItem-CE", -1287922232),
            "stops at a nul": ("ab\0cd", p.generate_hash_id("ab")),
            "player class": ("BaseCharacter-CE", p.PLAYER_CLASS),
            "player entity": ("b59acfa1bf90f734ca5c88514180177a", p.PLAYER_ENTITY),
            "bow": ("testBow", p.ITEM_BOW),
            "skill item": ("testSkillItem", p.ITEM_SKILL),
            "pet": ("testPetItem", p.ITEM_PET),
            "mount": ("testMountItem", p.ITEM_MOUNT),
            "building item": ("TestBuildingItem-CE", p.ITEM_BUILDING),
            "yield": ("DefaultWeaponItem-CE", p.ITEM_YIELD),
            "debuff skill": ("TestDebuffSkill", p.SKILL_DEBUFF),
            "heal skill": ("TestHealSkill", p.SKILL_HEAL),
            "kill quest": ("testKillQuest", p.QUEST_KILL),
        }
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p.generate_hash_id(text), expected)

    def test_hashed_password(self):
        cases = {
            "empty": ("", "d41d8cd98f00b204e9800998ecf8427e"),
            "abc": ("abc", "900150983cd24fb0d6963f7d28e17f72"),
        }
        for name, (password, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p.hashed_password(password), expected)


class RowTests(unittest.TestCase):
    def test_item_rows(self):
        cases = {
            "tester": (p.TESTER, [p.ITEM_SKILL, p.ITEM_PET, p.ITEM_MOUNT, p.ITEM_BUILDING]),
            "bot": (p.BOT, [p.ITEM_SKILL, p.ITEM_PET, p.ITEM_MOUNT]),
        }
        for name, (fixture, bag) in cases.items():
            with self.subTest(name):
                rows = p.item_rows(fixture)
                right = [r for r in rows if r["inventoryType"] == p.INVENTORY_WEAPON_RIGHT]
                self.assertEqual([(r["dataId"], r["idx"], r["amount"]) for r in right], [(p.ITEM_BOW, 0, 1)])
                in_bag = [r for r in rows if r["inventoryType"] == p.INVENTORY_BAG]
                self.assertEqual([r["dataId"] for r in in_bag], bag)
                self.assertEqual([r["idx"] for r in in_bag], list(range(len(bag))))
                self.assertEqual(len({r["id"] for r in rows}), len(rows))
                for r in rows:
                    self.assertEqual(r["characterId"], fixture.character_id)
                    self.assertEqual(r["sockets"], "")
                    self.assertEqual(r["version"], p.ITEM_VERSION)
                    self.assertGreater(r["amount"], 0)

    def test_item_ids_differ_between_calls(self):
        first, second = p.item_rows(p.TESTER), p.item_rows(p.TESTER)
        self.assertFalse({r["id"] for r in first} & {r["id"] for r in second})

    def test_bag_contents(self):
        cases = {
            "skill stack": (p.ITEM_SKILL, 20, 1),
            "pet at level 10": (p.ITEM_PET, 1, 10),
            "three building items": (p.ITEM_BUILDING, 3, 1),
        }
        rows = {r["dataId"]: r for r in p.item_rows(p.TESTER)}
        for name, (data_id, amount, level) in cases.items():
            with self.subTest(name):
                self.assertEqual((rows[data_id]["amount"], rows[data_id]["level"]), (amount, level))

    def test_keyed_rows(self):
        cases = {
            "skills": ([r["id"] for r in p.skill_rows(p.BOT)],
                       [f"{p.BOT.character_id}_{p.SKILL_DEBUFF}", f"{p.BOT.character_id}_{p.SKILL_HEAL}"]),
            "quest": ([p.quest_row(p.BOT)["id"]], [f"{p.BOT.character_id}_{p.QUEST_KILL}"]),
        }
        for name, (ids, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(ids, expected)

    def test_quest_text_columns_are_supplied(self):
        row = p.quest_row(p.TESTER)
        self.assertEqual((row["killedMonsters"], row["completedTasks"]), ("", ""))

    def test_character_row(self):
        cases = {
            "default placement": (p.Placement(), (0.0, 0.0, p.PARTY_ID)),
            "placed, no party": (p.Placement(86.5, -12, 0), (86.5, -12, 0)),
        }
        for name, (placement, (x, z, party)) in cases.items():
            with self.subTest(name):
                row = p.character_row(p.TESTER, "user", placement)
                self.assertEqual((row["currentPositionX"], row["currentPositionZ"], row["partyId"]), (x, z, party))
                self.assertEqual((row["id"], row["userId"], row["characterName"]),
                                 (p.TESTER.character_id, "user", p.TESTER.character_name))
                self.assertEqual((row["dataId"], row["entityId"], row["currentMapName"]), (p.PLAYER_CLASS, p.PLAYER_ENTITY, p.MAP))
                self.assertEqual(row["level"], p.FIXTURE_LEVEL)

    def test_party_row(self):
        self.assertEqual(p.party_row(), {"id": p.PARTY_ID, "shareExp": 1, "shareItem": 1, "leaderId": p.TESTER.character_id})

    def test_fixture_emails_are_unique(self):
        self.assertEqual(len({f.email for f in p.FIXTURES}), len(p.FIXTURES))


class RefuseForeignOwnersTests(unittest.TestCase):
    RIG = {"meshtest": "u-tester", "meshbot": "u-bot"}

    def test_refuse_foreign_owners(self):
        cases = {
            "fixture name held by another account": (
                [("from characters", [("other-id", "u-someone")])], "not a rig account"),
            "party led by another character": (
                [("from characters", []), ("from party", [("someone",)])], "not a rig character"),
            "rig characters and no party": ([("from characters", [(p.TESTER.character_id, "u-tester")])], None),
            "party led by the bot": ([("from characters", []), ("from party", [(p.BOT.character_id,)])], None),
        }
        for name, (answers, error) in cases.items():
            with self.subTest(name):
                cur = FakeCursor(answers)
                if error:
                    with self.assertRaisesRegex(p.ProvisionError, error):
                        p.refuse_foreign_owners(cur, self.RIG)
                else:
                    p.refuse_foreign_owners(cur, self.RIG)
                self.assertTrue(all(sql.lstrip().lower().startswith("select") for sql, _ in cur.statements))


class InsertTests(unittest.TestCase):
    def test_insert_checks_rowcount(self):
        cases = {"no row written": (0, True), "one row written": (1, False)}
        for name, (rowcount, raises) in cases.items():
            with self.subTest(name):
                cur = FakeCursor([])
                cur.rowcount = rowcount
                if raises:
                    with self.assertRaises(p.ProvisionError):
                        p.insert(cur, "party", p.party_row())
                else:
                    p.insert(cur, "party", p.party_row())
                sql, params = cur.statements[0]
                self.assertIn("insert into `party` (`id`,`shareExp`,`shareItem`,`leaderId`)", sql)
                self.assertEqual(params, [p.PARTY_ID, 1, 1, p.TESTER.character_id])


class SplitSqlTests(unittest.TestCase):
    def test_split_sql(self):
        cases = {
            "unterminated statement": ("CREATE TABLE a (\n  x int\n)", ValueError),
            "empty": ("", []),
            "comments only": ("-- a\n--\n\n", []),
            "one per line": ("SET a = 1;\nSTART TRANSACTION;", ["SET a = 1", "START TRANSACTION"]),
            "multi-line with comments between": ("-- t\nCREATE TABLE `t` (\n  `x` int\n);\n\n-- i\nINSERT INTO `t` VALUES\n(1),\n(2);",
                                                 ["CREATE TABLE `t` (\n  `x` int\n)", "INSERT INTO `t` VALUES\n(1),\n(2)"]),
        }
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                if expected is ValueError:
                    with self.assertRaises(ValueError):
                        p.split_sql(text)
                else:
                    self.assertEqual(p.split_sql(text), expected)

    def test_kit_schema_script_splits(self):
        with open(p.SCHEMA_SQL, encoding="utf-8-sig") as f:
            statements = p.split_sql(f.read())
        creates = [s for s in statements if s.startswith("CREATE TABLE")]
        self.assertGreaterEqual(len(creates), 30)
        self.assertTrue(any(s.startswith("INSERT INTO `__migrations`") for s in statements))


class CreateSchemaTests(unittest.TestCase):
    def test_refuses_unsafe_names(self):
        cases = {"backtick": "a`b", "dot": "a.b", "space": "a b", "empty": "", "too long": "x" * 65}
        for name, database in cases.items():
            with self.subTest(name):
                with self.assertRaises(p.ProvisionError):
                    p.create_schema(None, database)


class PlayStatusTests(unittest.TestCase):
    def test_editor_is_playing(self):
        cases = {
            "no status file": (None, False),
            "stopped": ("isPlaying=False\n", False),
            "playing": ("time=1\nisPlaying=True\n", True),
        }
        for name, (content, expected) in cases.items():
            with self.subTest(name), tempfile.TemporaryDirectory() as d:
                path = os.path.join(d, "PlayStatus.txt")
                if content is not None:
                    with open(path, "w", encoding="utf-8") as f:
                        f.write(content)
                self.assertEqual(p.editor_is_playing(path), expected)


class BotPointsTests(unittest.TestCase):
    def test_bot_points_refuses(self):
        cases = {"an unknown placement": (5, "east", "placement 'east' is not one of"),
                 "no bots, spread": (0, "spread", "0 bots"), "no bots, a column": (0, "west", "0 bots")}
        for name, (n, placement, message) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(ValueError, message):
                p.bot_points(n, placement)

    def test_bot_points(self):
        cases = {
            "one bot spread": ((1, "spread"), [(0.0, 0.0)]),
            "four bots spread": ((4, "spread"), [(-25.0, -25.0), (25.0, -25.0), (-25.0, 25.0), (25.0, 25.0)]),
            "three bots spread": ((3, "spread"), [(-25.0, -25.0), (25.0, -25.0), (-25.0, 25.0)]),
            "west": ((3, "west"), [(-30.0, -40.0), (-30.0, 0.0), (-30.0, 40.0)]),
            "pace across the cut": ((2, "pace-cut"), [(-4.0, -40.0), (-4.0, 40.0)]),
            "pace in the west": ((1, "pace-west"), [(-44.0, 0.0)]),
            "two bots at the centre": ((2, "centre"), [(0.0, -2.0), (0.0, 2.0)]),
            "one bot at the eye group's centre (moved there at Play)": ((1, "eye-centre"), [(0.0, 0.0)]),
        }
        for name, ((n, placement), expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p.bot_points(n, placement), expected)

    def test_a_hundred_spread_bots_fill_the_square(self):
        points = p.bot_points(100, "spread")
        self.assertEqual(len(set(points)), 100)
        self.assertEqual((min(points), max(points)), ((-45.0, -45.0), (45.0, 45.0)))
        self.assertTrue(all(-50 < x < 50 and -50 < z < 50 for x, z in p.bot_points(50, "spread")))

    def test_points_files(self):
        cases = {
            "a word": ("a,b\n", "points line 1: 'a,b' is not x,z"),
            "one number": ("1\n", "points line 1: '1' is not x,z"),
            "four numbers": ("1,2\n1,2,3,4\n", "points line 2"),
            "not finite": ("nan,0\n", "points line 1"),
        }
        for name, (text, message) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(ValueError, message):
                botpoints.parse_points(text)
        points = [(-30.0, -40.0), (30.0, 12.5, -30.0)]
        self.assertEqual(botpoints.format_points(points), "-30,-40\n30,12.5,-30\n")
        self.assertEqual(botpoints.parse_points("# walk\n\n" + botpoints.format_points(points)), points)


class FakeConn:
    """FakeConn hands out one FakeCursor and records begin, commit, rollback and close."""

    def __init__(self, cursor):
        self.cur, self.calls = cursor, []

    def cursor(self):
        return self.cur

    def __getattr__(self, name):
        if name in ("begin", "commit", "rollback", "close"):
            return lambda: self.calls.append(name)
        raise AttributeError(name)


class BotCursor(FakeCursor):
    """Accounts and characters of the bots by name: accounts {user: [ids]}, characters {name: [(id, userId, map)]};
    every update matches `rowcount` rows."""

    def __init__(self, accounts, characters, rowcount=1):
        super().__init__([])
        self.accounts, self.characters, self.rowcount = accounts, characters, rowcount

    def execute(self, sql, params=None):
        self.statements.append((sql, params))
        if "from userlogin" in sql:
            self._rows = [(i,) for i in self.accounts.get(params[0], [])]
        elif "from characters" in sql:
            self._rows = list(self.characters.get(params[0], []))
        else:
            self._rows = []

    def close(self):
        pass


def bots(first, n, map_name=p.MAP):
    accounts = {f"meshbot{k}": [f"u{k}"] for k in range(first, first + n)}
    characters = {f"meshbot{k}Char": [(f"c{k}", f"u{k}", map_name)] for k in range(first, first + n)}
    return accounts, characters


class ResetBotPositionsTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.status = os.path.join(folder.name, "PlayStatus.txt")

    def reset(self, first, n, points, cursor, playing=False):
        with open(self.status, "w", encoding="utf-8") as f:
            f.write(f"isPlaying={playing}\n")
        conn = FakeConn(cursor)
        try:
            return p.reset_bot_positions(first, n, points, connect=lambda: conn, status_path=self.status), conn
        except p.ProvisionError as ex:
            return ex, conn

    def test_reset_bot_positions_refuses(self):
        two = [(-30.0, -40.0), (-30.0, 40.0)]
        accounts, characters = bots(2, 2)
        cases = {
            "the regress fixture": ((1, 2, two, BotCursor(*bots(1, 2))), "meshbot1: step 8b's bots are meshbot2 and up"),
            "too few points": ((2, 2, two[:1], BotCursor(accounts, characters)), "1 points for 2 bots"),
            "in Play": ((2, 2, two, BotCursor(accounts, characters), True), "the editor is still in Play"),
            "an unregistered bot": ((2, 2, two, BotCursor({"meshbot2": ["u2"]},
                                                          {"meshbot2Char": characters["meshbot2Char"]})),
                                    "0 account\\(s\\) meshbot3 and 0 character\\(s\\) meshbot3Char; register"),
            "a character before its account": ((2, 2, two, BotCursor({"meshbot2": ["u2"]}, characters)),
                                               "character c3 \\(meshbot3Char\\) belongs to account u3"),
            "a bot without its character": ((2, 2, two, BotCursor(accounts, {"meshbot2Char": characters["meshbot2Char"]})),
                                            "1 account\\(s\\) meshbot3 and 0 character\\(s\\)"),
            "a name owned by another account": (
                (2, 2, two, BotCursor(accounts, dict(characters, meshbot3Char=[("x", "u-player", p.MAP)]))),
                "character x \\(meshbot3Char\\) belongs to account u-player, which is not meshbot3"),
            "a bot on another map": ((2, 2, two, BotCursor(*bots(2, 2, "ServerMeshTestMap2"))),
                                     "meshbot2Char is on map ServerMeshTestMap2"),
            "an update that matched nothing": ((2, 2, two, BotCursor(accounts, characters, rowcount=0)),
                                               "the position of meshbot2Char \\(c2\\) matched 0 rows"),
        }
        for name, (call, message) in cases.items():
            with self.subTest(name):
                error, conn = self.reset(*call)
                self.assertIsInstance(error, p.ProvisionError)
                self.assertRegex(str(error), message)
                self.assertNotIn("commit", conn.calls)
                writes = [sql for sql, _ in call[3].statements if sql.lstrip().startswith("update")]
                self.assertLessEqual(len(writes), 1 if "matched nothing" in name else 0)

    def test_reset_bot_positions(self):
        cursor = BotCursor(*bots(5, 2))
        count, conn = self.reset(5, 2, [(-4.0, -40.0), (-4.0, 40.0, 4.0)], cursor)
        self.assertEqual(count, 2)
        self.assertEqual(conn.calls, ["begin", "commit", "close"])
        updates = [(sql, params) for sql, params in cursor.statements if sql.startswith("update")]
        self.assertEqual([params for _, params in updates], [(-4.0, -40.0, "c5", p.MAP), (-4.0, 40.0, "c6", p.MAP)])
        self.assertTrue(all("update characters set currentPositionX=%s" in sql and "currentMapName=%s" in sql
                            for sql, _ in updates))
        self.assertTrue(all(sql.startswith(("select", "update")) for sql, _ in cursor.statements))


if __name__ == "__main__":
    unittest.main()
