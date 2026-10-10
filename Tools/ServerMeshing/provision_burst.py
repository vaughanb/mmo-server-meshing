"""Phase 0 step 9's database-burst rows (step9_design.md decisions 1 and 4), split out of provision (~500-line rule).

    python Tools/ServerMeshing/provision.py --burst 800   # meshburst0001..meshburst0800, each with one character

    provision_burst.burst(n)                     # create or reset the n burst accounts and characters; returns n
    provision_burst.snapshot(n)                  # {character name: updateAt} of those characters (read only)
    provision_burst.changed_rows(before, after)  # the names whose updateAt differs between two snapshots

Account k (1-based, 4 digits) is meshburst<kkkk> with email meshburst<kkkk>@meshburst.invalid and the fixtures'
password; its one character is meshburst<kkkk>Char (id: burst_character_id(k), the same on every reset), level 20 of
the demo class, no items, no party, on ServerMeshTestMap channel C at burst_points' point k: the west half of the
coarse layout (x -290..-10, z -290..290), seeded, so a point never moves between resets.

Safety, as provision's: the rows are written only out of Play (the database manager in the editor keeps its own cache)
and in one transaction. Every account touched must be a rig account by name (meshburst + 4 digits) AND by email (its
own @meshburst.invalid address): an existing meshburst name with another email, a burst character name or id owned by
another account, or two accounts of one name stop the run before anything changes. The burst's in-Play writes go
through the database manager (the C# burst), never through here.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py" (fake cursors only).
"""
import hashlib
import random
import re

from provision_rows import (PASSWORD, PLAY_STATUS, Fixture, Placement, ProvisionError, character_row, character_tables,
                            db_connect, delete_character, editor_is_playing, hashed_password, insert, new_id)

MAX_ROWS = 9999                         # four digits
USER_PATTERN = re.compile(r"meshburst\d{4}")
EMAIL_DOMAIN = "meshburst.invalid"      # RFC 2606: never a real mailbox
SEED = 9                                # step 9
WEST_X, ALL_Z = (-290.0, -10.0), (-290.0, 290.0)   # the coarse ground is -300..300; 10 m clear of its edge and the cut


def burst_user(k):
    """Account k's username: meshburst0001 for k = 1."""
    return f"meshburst{k:04d}"


def burst_email(user):
    return f"{user}@{EMAIL_DOMAIN}"


def burst_name(k):
    return burst_user(k) + "Char"


def burst_character_id(k):
    """Character k's id: md5 hex of its name (32 hex characters, like the kit's ids), the same on every reset."""
    return hashlib.md5(burst_name(k).encode("utf-8")).hexdigest()


def burst_points(n, seed=SEED):
    """n seeded (x, z) points in the west half of the coarse layout; point k depends on k and the seed only."""
    rng = random.Random(seed)
    return [(round(rng.uniform(*WEST_X), 2), round(rng.uniform(*ALL_Z), 2)) for _ in range(n)]


def check_count(n):
    if not 1 <= n <= MAX_ROWS:
        raise ProvisionError(f"{n} burst rows: from 1 to {MAX_ROWS}")


def guard_account(user, rows):
    """The rig-account rule for one touched account: its name must match USER_PATTERN and, when it exists (rows:
    [(id, email)] of that username), be one account with the rig's email. Returns its id (None when it is new);
    ProvisionError otherwise."""
    if not USER_PATTERN.fullmatch(user):
        raise ProvisionError(f"{user} is not a burst account name (meshburst + 4 digits)")
    if len(rows) > 1:
        raise ProvisionError(f"{len(rows)} accounts named {user}; not touching them")
    if rows and rows[0][1] != burst_email(user):
        raise ProvisionError(f"account {user} has email {rows[0][1]!r}, not {burst_email(user)}: it is not "
                             "a rig account; not touching it")
    return rows[0][0] if rows else None


def guard(cur, n):
    """{user: account id or None} for meshburst0001..n; ProvisionError (before any write) for an account
    guard_account refuses, or a burst character name or id held by any other account. Reads only."""
    accounts = {}
    for k in range(1, n + 1):
        user = burst_user(k)
        cur.execute("select id, email from userlogin where username=%s", (user,))
        account = guard_account(user, list(cur.fetchall()))
        cur.execute("select id, userId from characters where characterName=%s or id=%s",
                    (burst_name(k), burst_character_id(k)))
        for character_id, owner in cur.fetchall():
            if account is None or owner != account:
                raise ProvisionError(f"character {character_id} ({burst_name(k)}) belongs to account "
                                     f"{owner}, which is not {user}; not touching it")
        accounts[user] = account
    return accounts


def reset_one(cur, k, account, point, tables):
    """Writes account k (its password and email reset, or created) and its one character at point; the account's
    other characters are deleted. Returns how many characters were deleted."""
    user = burst_user(k)
    if account is None:
        account = new_id()
        insert(cur, "userlogin", {"id": account, "username": user,
                                  "password": hashed_password(PASSWORD),
                                  "email": burst_email(user), "authType": 1})
    else:
        cur.execute("update userlogin set password=%s, email=%s, authType=1 where id=%s",
                    (hashed_password(PASSWORD), burst_email(user), account))
        if cur.rowcount != 1:
            raise ProvisionError(f"the account {user} ({account}) matched {cur.rowcount} rows")
    cur.execute("select id from characters where userId=%s", (account,))
    old = [row[0] for row in cur.fetchall()]
    for character_id in old:
        delete_character(cur, character_id, tables)
    fixture = Fixture(user, burst_character_id(k), burst_name(k), [])
    insert(cur, "characters", character_row(fixture, account, Placement(*point, party=0)))
    return len(old)


def burst(n, connect=None, status_path=PLAY_STATUS, log=print):
    """Creates or resets the burst accounts meshburst0001..meshburst<n> and their characters in one transaction
    (decision 1); returns n. Refuses (ProvisionError) while the editor is in Play (as regress.db_reset), for n
    outside 1..9999, and anything guard refuses, before any write. connect opens the database (default
    db_connect); the connection is closed."""
    check_count(n)
    if editor_is_playing(status_path):
        raise ProvisionError("the editor is still in Play; the burst rows can only be written while it is "
                             "stopped")
    conn = (connect or db_connect)()
    cur = conn.cursor()
    try:
        conn.begin()
        accounts = guard(cur, n)
        tables = character_tables(cur)
        points = burst_points(n)
        replaced = sum(reset_one(cur, k, accounts[burst_user(k)], points[k - 1], tables) for k in range(1, n + 1))
        conn.commit()
    except BaseException:
        conn.rollback()
        raise
    finally:
        cur.close()
        conn.close()
    created = sum(1 for account in accounts.values() if account is None)
    log(f"burst rows: {burst_user(1)}..{burst_user(n)} ({created} account(s) created, "
        f"{replaced} character(s) replaced)")
    return n


def read_only_connect(database=None):
    """db_connect with the session set read only: a write through it fails in MySQL itself."""
    conn = db_connect(database)
    with conn.cursor() as cur:
        cur.execute("set session transaction read only")
    return conn


def snapshot(n, connect=None):
    """{character name: updateAt as text} of the burst characters 1..n that exist (read only; connect default
    read_only_connect; the connection is closed)."""
    conn = (connect or read_only_connect)()
    try:
        cur = conn.cursor()
        try:
            cur.execute("select characterName, updateAt from characters where characterName like %s",
                        ("meshburst____Char",))
            rows = cur.fetchall()
        finally:
            cur.close()
    finally:
        conn.close()
    names = {burst_name(k) for k in range(1, n + 1)}
    return {name: str(stamp) for name, stamp in rows if name in names}


def changed_rows(before, after):
    """The names, sorted, whose updateAt differs between two snapshots (a row in one only counts as changed)."""
    return sorted(name for name in set(before) | set(after) if before.get(name) != after.get(name))
