"""Unit tests for provision (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import tempfile
import os
import unittest

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


if __name__ == "__main__":
    unittest.main()
