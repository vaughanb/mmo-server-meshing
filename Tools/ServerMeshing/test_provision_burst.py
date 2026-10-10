"""Unit tests for provision_burst, step 9's burst rows (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py").

No test opens a database or Config/mySqlConfig.json: queries go to test_provision's FakeCursor and FakeConn stand-ins.
"""
import os
import sys
import tempfile
import unittest
from unittest import mock

import provision as p
import provision_burst as pb
import test_provision as tp


class BurstCursor(tp.FakeCursor):
    """Step 9's burst rows: accounts {user: [(id, email)]}, characters [(id, userId, name)], stamps {name: updateAt};
    an update matches `update_rows` rows, an insert one."""

    def __init__(self, accounts=None, characters=(), stamps=None, update_rows=1):
        super().__init__([])
        self.accounts, self.characters, self.stamps = accounts or {}, list(characters), stamps or {}
        self.update_rows, self.rowcount = update_rows, 1

    def execute(self, sql, params=None):
        self.statements.append((sql, params))
        self.rowcount = self.update_rows if sql.startswith("update") else 1
        if "from userlogin where username" in sql:
            self._rows = list(self.accounts.get(params[0], []))
        elif "where characterName=%s or id=%s" in sql:
            self._rows = [(c, u) for c, u, name in self.characters if name == params[0] or c == params[1]]
        elif "information_schema.columns" in sql:
            self._rows = [("characteritem",), ("characterskill",)]
        elif "select id from characters where userId" in sql:
            self._rows = [(c,) for c, u, _ in self.characters if u == params[0]]
        elif "characterName like" in sql:
            self._rows = list(self.stamps.items())
        else:
            self._rows = []

    def close(self):
        pass


def burst_account(k, email=None):
    user = pb.burst_user(k)
    return user, [(f"u{k}", email or pb.burst_email(user))]


class BurstTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.status = os.path.join(folder.name, "PlayStatus.txt")

    def burst(self, n, cursor, playing=False):
        with open(self.status, "w", encoding="utf-8") as f:
            f.write(f"isPlaying={playing}\n")
        conn, logged = tp.FakeConn(cursor), []
        try:
            return pb.burst(n, connect=lambda: conn, status_path=self.status, log=logged.append), conn, logged
        except p.ProvisionError as ex:
            return ex, conn, logged

    def test_names_and_points(self):
        self.assertEqual((pb.burst_user(1), pb.burst_name(800), pb.burst_email("meshburst0007")),
                         ("meshburst0001", "meshburst0800Char", "meshburst0007@meshburst.invalid"))
        ids = {pb.burst_character_id(k) for k in range(1, 801)}
        self.assertEqual(len(ids), 800)
        self.assertTrue(all(len(i) == 32 and int(i, 16) >= 0 for i in ids))
        self.assertEqual(pb.burst_character_id(3), pb.burst_character_id(3))
        points = pb.burst_points(800)
        self.assertEqual(pb.burst_points(5), points[:5])
        self.assertEqual(len(set(points)), 800)
        self.assertTrue(all(-290 <= x <= -10 and -290 <= z <= 290 for x, z in points))

    def test_guard_account(self):
        cases = {  # failure first: (user, rows), error or the account id
            "not a burst name": (("meshbot2", []), "meshbot2 is not a burst account name"),
            "five digits": (("meshburst00001", []), "is not a burst account name"),
            "two accounts": (("meshburst0001", [("a", "x"), ("b", "y")]), "2 accounts named meshburst0001"),
            "another email": (("meshburst0001", [("a", "someone@example.com")]),
                              "account meshburst0001 has email 'someone@example.com', not meshburst0001@meshburst"),
            "the rig's account": (("meshburst0001", [("a", "meshburst0001@meshburst.invalid")]), "a"),
            "a new account": (("meshburst0002", []), None),
        }
        for name, ((user, rows), expected) in cases.items():
            with self.subTest(name):
                if expected in (None, "a"):
                    self.assertEqual(pb.guard_account(user, rows), expected)
                else:
                    with self.assertRaisesRegex(p.ProvisionError, expected):
                        pb.guard_account(user, rows)

    def test_burst_refuses(self):
        rig = dict([burst_account(1), burst_account(2)])
        chars = [(pb.burst_character_id(1), "u1", pb.burst_name(1))]
        cases = {  # failure first: (n, cursor, playing), message
            "no rows": ((0, BurstCursor(), False), "0 burst rows: from 1 to 9999"),
            "five digits": ((10000, BurstCursor(), False), "10000 burst rows"),
            "in Play": ((2, BurstCursor(rig), True), "the editor is still in Play"),
            "a meshburst name with another email": (
                (2, BurstCursor(dict([burst_account(1), burst_account(2, "player@example.com")])), False),
                "account meshburst0002 has email 'player@example.com'"),
            "a burst character of another account": (
                (2, BurstCursor(rig, chars + [("x", "u-player", pb.burst_name(2))]), False),
                "character x \\(meshburst0002Char\\) belongs to account u-player, which is not meshburst0002"),
            "a burst character id elsewhere": (
                (2, BurstCursor(rig, [(pb.burst_character_id(2), "u1", "renamed")]), False),
                "belongs to account u1, which is not meshburst0002"),
            "a character before its account": (
                (2, BurstCursor(dict([burst_account(1)]), [("y", "u9", pb.burst_name(2))]), False),
                "character y \\(meshburst0002Char\\) belongs to account u9"),
            "an account update that matched nothing": ((2, BurstCursor(rig, update_rows=0), False),
                                                       "the account meshburst0001 \\(u1\\) matched 0 rows"),
        }
        for name, ((n, cursor, playing), message) in cases.items():
            with self.subTest(name):
                error, conn, logged = self.burst(n, cursor, playing)
                self.assertIsInstance(error, p.ProvisionError)
                self.assertRegex(str(error), message)
                self.assertNotIn("commit", conn.calls)
                self.assertEqual(logged, [])
                writes = [sql for sql, _ in cursor.statements if not sql.lstrip().startswith("select")]
                self.assertLessEqual(len(writes), 1 if "matched nothing" in name else 0)

    def test_burst(self):
        old = "an-old-character"
        cursor = BurstCursor(dict([burst_account(1)]), [(pb.burst_character_id(1), "u1", pb.burst_name(1)),
                                                        (old, "u1", "leftover")])
        count, conn, logged = self.burst(3, cursor)
        self.assertEqual((count, conn.calls), (3, ["begin", "commit", "close"]))
        self.assertEqual(logged, ["burst rows: meshburst0001..meshburst0003 (2 account(s) created, "
                                  "2 character(s) replaced)"])
        sqls = [sql for sql, _ in cursor.statements]
        self.assertEqual(sum(sql.startswith("update userlogin") for sql in sqls), 1)
        accounts = [params for sql, params in cursor.statements if sql.startswith("insert into `userlogin`")]
        self.assertEqual([(row[1], row[3]) for row in accounts],
                         [("meshburst0002", "meshburst0002@meshburst.invalid"),
                          ("meshburst0003", "meshburst0003@meshburst.invalid")])
        deleted = {params[0] for sql, params in cursor.statements if sql.startswith("delete from characters")}
        self.assertEqual(deleted, {old, pb.burst_character_id(1)})
        inserts = [sql for sql in sqls if sql.startswith("insert into `characters`")]
        self.assertEqual(len(inserts), 3)
        columns = inserts[0][inserts[0].index("(") + 1:inserts[0].index(")")].replace("`", "").split(",")
        rows = [dict(zip(columns, params)) for sql, params in cursor.statements if sql.startswith("insert into `characters`")]
        points = pb.burst_points(3)
        self.assertEqual([(r["id"], r["characterName"], r["userId"]) for r in rows][0],
                         (pb.burst_character_id(1), "meshburst0001Char", "u1"))
        self.assertEqual([(r["currentPositionX"], r["currentPositionZ"]) for r in rows], points)
        self.assertTrue(all((r["currentChannel"], r["currentMapName"], r["partyId"]) == ("C", p.MAP, 0) for r in rows))
        self.assertFalse(any("meshtest" in str(params) or "meshbot" in str(params) for _, params in cursor.statements))

    def test_snapshots(self):
        stamps = {"meshburst0001Char": "2026-10-09 10:00:00", "meshburst0002Char": "2026-10-09 10:00:01",
                  "meshburst0009Char": "2026-10-09 10:00:02"}
        cursor = BurstCursor(stamps=stamps)
        conn = tp.FakeConn(cursor)
        before = pb.snapshot(2, connect=lambda: conn)
        self.assertEqual(before, {"meshburst0001Char": "2026-10-09 10:00:00", "meshburst0002Char": "2026-10-09 10:00:01"})
        self.assertEqual(conn.calls, ["close"])
        self.assertTrue(all(sql.startswith("select") for sql, _ in cursor.statements))
        cases = {  # failure first: after, the changed names
            "one row rewritten": (dict(before, meshburst0002Char="2026-10-09 10:00:09"), ["meshburst0002Char"]),
            "one row gone": ({"meshburst0001Char": "2026-10-09 10:00:00"}, ["meshburst0002Char"]),
            "nothing changed": (dict(before), []),
        }
        for name, (after, changed) in cases.items():
            with self.subTest(name):
                self.assertEqual(pb.changed_rows(before, after), changed)

    def test_the_command_line(self):
        calls = []
        with mock.patch.object(sys, "argv", ["provision.py", "--burst", "50"]), \
                mock.patch.object(pb, "burst", lambda n, connect: calls.append(n)), \
                mock.patch.object(p, "db_connect", mock.Mock(side_effect=AssertionError("connected"))):
            self.assertEqual(p.run(p.parse_args()), 0)
        self.assertEqual(calls, [50])


if __name__ == "__main__":
    unittest.main()
