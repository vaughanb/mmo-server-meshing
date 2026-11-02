"""DESIGN 1.47's shop and workbench scenarios for regress.py, past what `social` reaches:

    python Tools/ServerMeshing/regress.py market splitvend

market: the test map Static, no tester in the editor. The fixture meshbot opens a shop of two items on E a metre past
the C/E line (-meshSocialVendNow -meshSocialVendCount 2); meshbot2 on C (the buyer, -meshSocialSteps vendhop,bench)
buys an entry and walks over the line at once, twice, while E holds its answers (MESH_TEST_VENDING_ANSWER_DELAY 4 then
13 s): the first purchase holds the buyer's hop until the item arrives; the second outlasts the escrow's 10 s, the gold
goes back, the buyer crosses, and the answer reaches it on E, the vendor's own cell, where the price is taken again.
Then meshbot2 places a TestBench-CE (a public crafting queue) two metres on C's side, queues 8 crafts on it and walks
over the line within its 5 m reach: E's copy of the bench crafts the rest.
splitvend: `split`'s cut of C at x = 0 with meshbot2 (one of the eight stand bots) six metres into the child's half
with a shop of two items; once the split is done meshbot10 buys one from the kept half across the new line, the merge
brings the vendor back to C, and meshbot11 buys the last one there (the shop crossed with the handoff both times).

regress.py hands itself in as `rig`; the graders read the cells' logs and the database.
"""
import os
import re
import time

import provision
import provision_rows
import regress_merge
import regress_split
import rigtools

SCENARIOS = ("market", "splitvend")
PERSIST_REASON = regress_split.PERSIST_REASON
EXPECTED_ZONES = {}

PRICE = 7                     # ServerMeshAutoClient.socialVendPrice
ANSWER_DELAYS = "4,13"        # MESH_TEST_VENDING_ANSWER_DELAY for market: inside the 10 s escrow, then past it
CRAFTS = 8                    # ServerMeshAutoClient.socialCraftAmount, 1 gold each
ITEM_BENCH = 418037898        # TestBenchItem-CE, id "TestBenchItem-CE" (a QueuedWorkbenchEntity, 'Mesh Test Craft')
BUYER_GOLD = 100

MARKET_LINE = 100.0
# The buyer stands 1.5 m along z from the vendor: its walks along x would run into the vendor's body (the first rig run
# stopped at x = 100.87, behind it). Both stand at z 56-57.5, clear of harvest's tree at (101.9, 61.8), which stopped a
# walk along z = 61.5 at x = 100.83, short of the hop.
MARKET_BUYER, MARKET_BUYER_AT = "meshbot2", (99.4, 57.5)
MARKET_VENDOR_AT = (100.6, 56.0)

SPLIT_VENDOR, SPLIT_VENDOR_AT = "meshbot2", (6.0, 75.0)
SPLIT_BUYERS, SPLIT_BUYER_AT = ("meshbot10", "meshbot11"), (-6.0, 75.0)
SPLITVEND_OBSERVE_S = 480     # the stand window: the split, the first purchase, the cooldown and the merge fit in it
BUYER_TIMEOUT_S = 240
VENDOR_ARGS = ["-meshSocialAccept", "-meshSocialVend", "-meshSocialVendNow", "-meshSocialVendCount", "2"]


def set_bag(cur, character_id, items, gold):
    """A bot's bag is exactly `items` (provision_rows.Item) and its gold `gold`: the bots outside the fixtures keep
    what earlier runs left them otherwise."""
    cur.execute("delete from characteritem where characterId=%s and inventoryType=%s",
                (character_id, provision_rows.INVENTORY_BAG))
    for idx, item in enumerate(items):
        provision_rows.insert(cur, "characteritem", provision_rows.item_row(character_id, provision_rows.INVENTORY_BAG, idx, item))
    cur.execute("update characters set gold=%s where id=%s", (gold, character_id))


def setup_bags(rig, bags):
    """{character name: (items, gold)} written in one transaction; the ids by name."""
    ids = {name: rig.character_id(name) for name in bags}
    conn = rig.db_connect()
    try:
        with conn.cursor() as cur:
            for name, (items, gold) in bags.items():
                set_bag(cur, ids[name], items, gold)
        conn.commit()
    finally:
        conn.close()
    return ids


def gold_of(rig, name):
    rows = rig.db_rows("select gold from characters where characterName=%s", (name,))
    return rows[0][0] if len(rows) == 1 else None


def stamped(lines, first=None):
    """(stamp 'date time', line) for every stamped line, by stamp (the info and warn files are read one after the other);
    within one second the lines `first` matches come before the others."""
    out = []
    for line in lines:
        s = rigtools.stamp_of(line)
        if s:
            out.append((f"{s[0]} {s[1]}", 0 if first is not None and re.search(first, line) else 1, line))
    return [(stamp, line) for stamp, _, line in sorted(out, key=lambda t: (t[0], t[1]))]


def hops_during_escrows(lines, buyer):
    """Problems: a Handoff START of `buyer` stamped after one of its escrows began and before that escrow ended (the
    item received, or the gold given back), on one cell's lines. Stamps are to the second, so a START in the very
    second the escrow ended passes."""
    problems, begun = [], None
    ended = rf"Vending bought here: {re.escape(buyer)} received|Vending escrow of {re.escape(buyer)} returned"
    for stamp, line in stamped(lines, first=ended):   # the end (a warning when the gold goes back) before a START
        if re.search(rf"Vending buy: {re.escape(buyer)}'s \d+ gold in escrow", line):
            begun = stamp
        elif re.search(ended, line):
            begun = None
        elif begun is not None and re.search(rf"Handoff START {re.escape(buyer)} ", line) and stamp > begun:
            problems.append(line.strip()[:160])
    return problems


def run(name, rep, rig, start, day, bots):
    if name == "market":
        run_market(rep, rig, start, day, bots)
        return None
    return run_splitvend(rep, rig, start, day, bots)


def grade_layout(rep, rig, start, day, result):
    if result is None:
        rig.grade_layout(rep, start, day, None)
    else:
        regress_merge.grade_layout(rep, rig, start, day, result)


# ------------------------------------------------------------------ market

def run_market(rep, rig, start, day, bots):
    rig.db_reset(MARKET_BUYER_AT, MARKET_VENDOR_AT, tester_party=0, bot_party=0, clear_buildings=True)
    buyer_char, vendor_char = f"{MARKET_BUYER}Char", provision.BOT.character_name
    provision.reset_bot_positions(2, 1, [MARKET_BUYER_AT], status_path=rig.STATUS)
    setup_bags(rig, {buyer_char: ([provision_rows.Item(ITEM_BENCH)], BUYER_GOLD),
                     vendor_char: (provision_rows.COMMON_BAG, 0)})
    rig.request_play("stand", MARKET_BUYER_AT[0], MARKET_BUYER_AT[1], enabled=False, line_x=MARKET_LINE,
                     env={"MESH_NO_MONSTER_HANDOFF": "1", "MESH_NO_MONSTERS": "1", "MESH_TEST_VENDING_ANSWER_DELAY": ANSWER_DELAYS})
    rig.wait_play(60)
    time.sleep(20)
    vendor_log = rig.launch_bot("market", provision.BOT.username,
                                ["-meshAutoMode", "stand", "-meshWalkX", f"{MARKET_VENDOR_AT[0]:g}", "-meshWalkZ", f"{MARKET_VENDOR_AT[1]:g}"] + VENDOR_ARGS,
                                observe_seconds=300)
    bots.append(("stand", vendor_log))
    time.sleep(10)
    buyer_log = rig.launch_bot("market", MARKET_BUYER,
                               ["-meshAutoMode", "social", "-meshWalkX", f"{MARKET_BUYER_AT[0]:g}", "-meshWalkZ", f"{MARKET_BUYER_AT[1]:g}",
                                "-meshSocialTarget", vendor_char, "-meshSocialSteps", "vendhop,bench"], line_x=MARKET_LINE)
    bots.append(("social", buyer_log))
    result = rig.wait_for_bot(buyer_log, "social", 420)
    rep.check("-> PASS" in result, f"buyer: {result[:240] or 'no RESULT'}")
    for step in ("vendhop1", "vendhop2", "bench"):
        rep.check(f"{step}=ok" in result, f"the buyer's {step} step went through")
    rep.check("across=True" in result, "the buyer and the vendor stood on either side of the line (C and E)")

    c_lines, e_lines = rig.zone_lines("C", start, day), rig.zone_lines("E", start, day)
    b, v = re.escape(buyer_char), re.escape(vendor_char)
    for zone, lines, pattern, want, what in (
            ("C", c_lines, rf"Vending buy: {b}'s {PRICE} gold in escrow", 2, "took the price into escrow twice"),
            ("C", c_lines, rf"Handoff of {b} held: a purchase from {v}'s shop is on its way", 2, "held the buyer's hop for both purchases"),
            ("E", e_lines, r"the answer \(VendingSold\) to \S+ held 4 s", 1, "held its first answer 4 s"),
            ("E", e_lines, r"the answer \(VendingSold\) to \S+ held 13 s", 1, "held its second answer 13 s"),
            ("C", c_lines, rf"Vending bought here: {b} received", 1, "gave the buyer the first item, before its hop"),
            ("C", c_lines, rf"Vending escrow of {b} returned \(no answer from {v}'s cell in 10 s\)", 1, "gave the second price back at the timeout"),
            ("E", e_lines, rf"{b} crossed onto the vendor's cell before the answer \(VendingSold\); handled here", 1, "handled the late answer itself"),
            ("E", e_lines, rf"Vending sold to {b} after its escrow went back: {PRICE} gold taken again", 1, "took the price again with the late item"),
            ("E", e_lines, rf"Crafting: {b}'s queue carried with the handoff, 0 own entries, 1 on workbenches, 0 dropped", 1, "put the bench's entry on its copy of the bench")):
        n = rig.count(lines, pattern)
        rep.check(n == want, f"{zone} {what} ({n}, expected {want})")
    problems = hops_during_escrows(c_lines, buyer_char)
    rep.check(not problems, "no hop of the buyer while a purchase was on its way" + (f" ({problems[0]})" if problems else ""))
    rep.check(rig.count(c_lines + e_lines, r"Vending: the item .* reached this cell after it moved on") == 0,
              "no answer had to be passed on (the buyer was where the answer looked for it)")
    # The database once both bots are gone (each saves on its way out): 2 purchases and 8 crafts from 100 gold, and the
    # vendor's 2 x 7.
    rig.wait_for_bot(vendor_log, "stand", 360)
    time.sleep(8)
    buyer_gold, vendor_gold = gold_of(rig, buyer_char), gold_of(rig, vendor_char)
    rep.check(buyer_gold == BUYER_GOLD - 2 * PRICE - CRAFTS, f"the buyer's gold in the database {buyer_gold}, expected {BUYER_GOLD - 2 * PRICE - CRAFTS}")
    rep.check(vendor_gold == 2 * PRICE, f"the vendor's gold in the database {vendor_gold}, expected {2 * PRICE}")


# ------------------------------------------------------------------ splitvend

def splitvend_plan():
    """split's eight stand bots, meshbot2 moved next to the cut (still in the child's half R), no harvest bot."""
    plan = [b for b in regress_split.plan_bots("split") if not b.harvest]
    return [b._replace(point=SPLIT_VENDOR_AT) if b.user == SPLIT_VENDOR else b for b in plan]


def wait_line(log, text, timeout):
    end = time.time() + timeout
    while time.time() < end:
        if any(text in line for line in regress_split.read_lines(log)):
            return True
        time.sleep(1)
    return False


def buy(rep, rig, bots, user, vendor_char, what):
    """One buyer bot from the kept half buys the first entry of the vendor's shop; its RESULT."""
    log = rig.launch_bot("splitvend", user,
                         ["-meshAutoMode", "social", "-meshWalkX", f"{SPLIT_BUYER_AT[0]:g}", "-meshWalkZ", f"{SPLIT_BUYER_AT[1]:g}",
                          "-meshSocialTarget", vendor_char, "-meshSocialSteps", "vend"], line_x=0)
    bots.append(("social", log))
    result = rig.wait_for_bot(log, "social", BUYER_TIMEOUT_S)
    rep.check("-> PASS" in result, f"{user} {what}: {result[:240] or 'no RESULT'}")
    return result


def run_splitvend(rep, rig, start, day, bots):
    vendor_char = f"{SPLIT_VENDOR}Char"
    buyers = [f"{u}Char" for u in SPLIT_BUYERS]
    # Before start_split's own reset and Play: the buyers' places (they log in later, in the kept half) and the bags.
    provision.reset_bot_positions(10, len(SPLIT_BUYERS), [SPLIT_BUYER_AT] * len(SPLIT_BUYERS), status_path=rig.STATUS)
    setup_bags(rig, {vendor_char: (provision_rows.COMMON_BAG, 0), **{c: ([], BUYER_GOLD) for c in buyers}})
    vendor_log = os.path.join(rig.LOGS, f"regress_splitvend_{SPLIT_VENDOR}.log")

    def shop_open():
        # The vendor opens its shop once it stands; the split must find it open (the shop crosses with the drain).
        opened = wait_line(vendor_log, "social: opening a shop", 60)
        rep.check(opened, f"{SPLIT_VENDOR} opened its shop before the split")
        time.sleep(3)
        return opened

    r = regress_split.start_split(
        "splitvend", rep, rig, start, day, bots, spec_name="split", observe_s=SPLITVEND_OBSERVE_S, plan=splitvend_plan(),
        load_plan="", bot_args={SPLIT_VENDOR: VENDOR_ARGS}, before_command=shop_open)
    spec = r.spec
    if not r.sent or r.applied is None:
        rep.check(False, f"splitvend: the split did not reach {spec.base}'s e3 apply; no purchase, no merge")
        return regress_merge.MergeRun("splitvend", r, None, None)
    v = re.escape(vendor_char)
    child_lines = rig.zone_lines(spec.child, start, day)
    rep.check(rig.count(child_lines, rf"Vending: {v}'s shop 'MeshShop' carried with the handoff, 2 item\(s\)") == 1,
              f"{spec.child} took the vendor's shop over with the split (2 items)")

    buy(rep, rig, bots, SPLIT_BUYERS[0], vendor_char, f"bought across the new line ({spec.base} | {spec.child})")
    b = re.escape(buyers[0])
    base_lines, child_lines = rig.zone_lines(spec.base, start, day), rig.zone_lines(spec.child, start, day)
    for zone, lines, pattern, what in (
            (spec.base, base_lines, rf"Vending buy: {b}'s {PRICE} gold in escrow .* of {v}'s shop", "took the price into escrow"),
            (spec.child, child_lines, rf"Vending sold here: {v} sold .* to {b} across a line", "did the vendor's half"),
            (spec.base, base_lines, rf"Vending bought here: {b} received .*its escrow is spent", "gave the buyer the item")):
        rep.check(rig.count(lines, pattern) == 1, f"{zone} {what}")

    m = regress_merge.MergeSpec("split", SPLITVEND_OBSERVE_S, False)
    regress_merge.wait_before_merge(r, m)
    seq, status = regress_merge.send_merge(rep, rig, r, m)
    if status is None:
        return regress_merge.MergeRun("splitvend", r, seq, None)
    regress_merge.grade_status(rep, status, spec)
    if not status["done"]:
        return regress_merge.MergeRun("splitvend", r, seq, status)
    tidy = regress_merge.wait_tidy(rig, r.split, status["pairs"].get("tidy"), start, day, regress_merge.TIDY_APPLY_TIMEOUT_S)
    rep.check(tidy is not None, f"{spec.base} applied the tidy layout after the merge")
    base_lines = rig.zone_lines(spec.base, start, day)
    rep.check(rig.count(base_lines, rf"Vending: {v}'s shop 'MeshShop' carried with the handoff, 1 item\(s\)") == 1,
              f"{spec.base} took the vendor's shop back with the merge (1 item left)")
    buy(rep, rig, bots, SPLIT_BUYERS[1], vendor_char, f"bought the last item on {spec.base} after the merge")
    rep.check(rig.count(rig.zone_lines(spec.base, start, day) + rig.zone_lines(spec.child, start, day),
                        r"Vending escrow of .* returned") == 0, "no purchase's gold went back")
    return regress_merge.MergeRun("splitvend", r, seq, status)
