"""DESIGN 1.47's shop and workbench scenarios for regress.py, past what `social` reaches:

    python Tools/ServerMeshing/regress.py market splitvend

market: the test map Static, no tester in the editor. The fixture meshbot opens a shop of five items on E a metre past
the C/E line (-meshSocialVendNow -meshSocialVendCount 5); meshbot2 on C (the buyer, -meshSocialSteps
vendhop,bench,buyrefund,buy,buyquit) buys an entry and walks over the line at once, twice, while E holds its answers
(MESH_TEST_VENDING_ANSWER_DELAY 4 then 13 s): the first purchase holds the buyer's hop until the item arrives; the
second outlasts the escrow's 10 s, the gold goes back, the buyer crosses, and the answer reaches it on E, the vendor's
own cell, where the price is taken again. Then meshbot2 places a TestBench-CE (a public crafting queue) two metres on
C's side, queues 8 crafts on it and walks over the line within its 5 m reach: E's copy of the bench crafts the rest.
DESIGN 1.48, three more purchases from C (MESH_TEST_VENDING_DROP sold3): E drops its third answer, so the gold
goes back, E asks after 15 s, C says the item never came and E puts it back in the vendor's bag; C drops every word on
the fourth purchase for 75 s (MESH_TEST_VENDING_BLACKOUT 3:75, DESIGN 1.51), so E keeps asking, releases the vendor at
a minute, and pays it when the word comes through; and the buyer quits as soon as its fifth purchase is in
escrow (E holds that answer 3 s): C gives the escrow back before the save and, when the item comes, tells E it was not
delivered.
splitvend: `split`'s cut of C at x = 0 with meshbot2 (one of the eight stand bots) six metres into the child's half
with a shop of two items; once the split is done meshbot10 buys one from the kept half across the new line, the merge
brings the vendor back to C, and meshbot11 buys the last one there (the shop crossed with the handoff both times).

trade (DESIGN 1.49, 1.51): the test map Static. The fixture meshbot (the smaller character id, so its cell, E, begins
every exchange and keeps the escrow) stands a metre into E and accepts every trade; meshbot2 on C trades with it five
times (-meshSocialSteps trade,tradefail,trade,tradelate,trade: one of each first bag stack and 5 gold each way). E drops
its second commit (MESH_TEST_TRADE_DROP commit2), so E asks after 10 s, C answers failed and ends meshbot2's trade, and
E gives the escrow back; C drops its second committed answer (committed2), so E asks and C answers committed again; C
drops every answer to the fourth trade for 75 s (MESH_TEST_TRADE_BLACKOUT 3:75), so E releases the fixture at a minute
and settles the exchange on it when the answers come through; and the fixture logs out (the kit's safe disconnect, no
despawn delay) as both have confirmed the fifth trade (-meshSocialQuitOnTrade 5 -meshSocialQuitSafe, C holding that
answer 3 s): E asks at once, C answers, and E mails the fixture what it received.

tradestop (DESIGN 1.52): `trade`'s two traders, two trades. C drops every answer to the second for 45 s
(MESH_TEST_TRADE_BLACKOUT 2:45), so its escrow stays open on E, and E quits cleanly 5 s into it
(MESH_FAULT_QUIT_WITH_EXCHANGE E:5): the kit's quit path saves the fixture, E writes its exchanges journal, the
spawner starts E again, and the new E takes the journal up and asks; once C answers, the exchange is settled on the
fixture (if it logged in again) or mailed to it. The bags, the gold and the mail are read from the database.

DESIGN 1.53, the paths 1.51-1.52 built and no scenario ran:
vendleave: market's layout, one sale; C drops its word on it for 40 s (MESH_TEST_VENDING_BLACKOUT 1:40) and the vendor
logs out as its shop sells (-meshSocialQuitAfterSales 1 -meshSocialQuitSafe); E keeps asking and, when the word comes,
mails the vendor its gold.
buystop: one purchase; E holds its answer 8 s and C quits cleanly 3 s into the escrow (MESH_FAULT_QUIT_WITH_EXCHANGE
C:3): C's journal has the purchase, its next process refunds it by mail and refuses the sale, and E puts the item back
in the vendor's bag.
salestop: one purchase; C drops its word for 30 s and E quits cleanly 5 s into the sale (E:5): E's journal has the sale,
its next process asks again and pays the vendor (on E, or by mail) when the word comes.
sleephold: sleeping cells (MESH_SLEEP, no awake ring, 30 s); one trade, C's answer dropped for 150 s (the central put C
to sleep about 80 s after the trade in the first rig run, after a 60 s blackout had ended), and both traders
leave (the fixture by the safe disconnect): E holds its open escrow and stays up when told to sleep, C (only an answer
left) sleeps and writes it to its journal, E asks the central to wake C, the woken C answers from its journal, E mails
the fixture and then sleeps.

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

SCENARIOS = ("market", "splitvend", "trade", "tradestop", "vendleave", "buystop", "salestop", "sleephold")
SLEEP_SCENARIOS = ("sleephold",)
SLEEPHOLD_ZONES = {"C", "E"}   # sleeping cells with no awake ring: only the traders' cells start
PERSIST_REASON = regress_split.PERSIST_REASON
EXPECTED_ZONES = {"sleephold": SLEEPHOLD_ZONES}

PRICE = 7                     # ServerMeshAutoClient.socialVendPrice
ANSWER_DELAYS = "4,13,0,0,3"  # MESH_TEST_VENDING_ANSWER_DELAY for market: inside the escrow, past it, none, none, 3 s
VENDING_DROPS = "sold3"        # MESH_TEST_VENDING_DROP for market: E's third answer
VENDING_BLACKOUT = "3:75"      # MESH_TEST_VENDING_BLACKOUT for market: C's third word (the fourth sale) and all after it for 75 s
SALES = 5                     # market's purchases: 2 hops, a refund, one asked about, one the buyer quit on
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
MARKET_VENDOR_ARGS = ["-meshSocialAccept", "-meshSocialVend", "-meshSocialVendNow", "-meshSocialVendCount", str(SALES)]
# Six stacks, so five shop entries whatever the kit lets be dealt: the fixtures' bag and the tester's buildings.
MARKET_VENDOR_BAG = provision_rows.COMMON_BAG + [provision_rows.Item(provision_rows.ITEM_BUILDING, amount=3),
                                                 provision_rows.Item(provision_rows.ITEM_STORAGE), provision_rows.Item(provision_rows.ITEM_DOOR)]


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


def bag_amount(rig, name):
    rows = rig.db_rows("select sum(i.amount) from characteritem i join characters c on c.id = i.characterId "
                       "where c.characterName=%s and i.inventoryType=%s", (name, provision_rows.INVENTORY_BAG))
    return int(rows[0][0] or 0) if rows else None


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
    if name == "trade":
        run_trade(rep, rig, start, day, bots)
        return None
    if name == "tradestop":
        run_tradestop(rep, rig, start, day, bots)
        return None
    if name in ("vendleave", "buystop", "salestop"):
        run_vendfault(name, rep, rig, start, day, bots)
        return None
    if name == "sleephold":
        run_sleephold(rep, rig, start, day, bots)
        return "sleephold"
    return run_splitvend(rep, rig, start, day, bots)


def grade_layout(rep, rig, start, day, result):
    if result is None or isinstance(result, str):
        rig.grade_layout(rep, start, day, EXPECTED_ZONES.get(result) if isinstance(result, str) else None)
    else:
        regress_merge.grade_layout(rep, rig, start, day, result)


# ------------------------------------------------------------------ market

def run_market(rep, rig, start, day, bots):
    rig.db_reset(MARKET_BUYER_AT, MARKET_VENDOR_AT, tester_party=0, bot_party=0, clear_buildings=True)
    buyer_char, vendor_char = f"{MARKET_BUYER}Char", provision.BOT.character_name
    provision.reset_bot_positions(2, 1, [MARKET_BUYER_AT], status_path=rig.STATUS)
    setup_bags(rig, {buyer_char: ([provision_rows.Item(ITEM_BENCH)], BUYER_GOLD),
                     vendor_char: (MARKET_VENDOR_BAG, 0)})
    vendor_bag = sum(item.amount for item in MARKET_VENDOR_BAG)
    rig.request_play("stand", MARKET_BUYER_AT[0], MARKET_BUYER_AT[1], enabled=False, line_x=MARKET_LINE,
                     env={"MESH_NO_MONSTER_HANDOFF": "1", "MESH_NO_MONSTERS": "1", "MESH_TEST_VENDING_ANSWER_DELAY": ANSWER_DELAYS,
                          "MESH_TEST_VENDING_DROP": VENDING_DROPS, "MESH_TEST_VENDING_BLACKOUT": VENDING_BLACKOUT})
    rig.wait_play(60)
    time.sleep(20)
    vendor_log = rig.launch_bot("market", provision.BOT.username,
                                ["-meshAutoMode", "stand", "-meshWalkX", f"{MARKET_VENDOR_AT[0]:g}", "-meshWalkZ", f"{MARKET_VENDOR_AT[1]:g}"] + MARKET_VENDOR_ARGS,
                                observe_seconds=420)
    bots.append(("stand", vendor_log))
    time.sleep(10)
    buyer_log = rig.launch_bot("market", MARKET_BUYER,
                               ["-meshAutoMode", "social", "-meshWalkX", f"{MARKET_BUYER_AT[0]:g}", "-meshWalkZ", f"{MARKET_BUYER_AT[1]:g}",
                                "-meshSocialTarget", vendor_char, "-meshSocialSteps", "vendhop,bench,buyrefund,buy,buyquit"], line_x=MARKET_LINE)
    bots.append(("social", buyer_log))
    result = rig.wait_for_bot(buyer_log, "social", 420)
    rep.check("-> PASS" in result, f"buyer: {result[:240] or 'no RESULT'}")
    for step in ("vendhop1", "vendhop2", "bench", "buyrefund", "buy"):
        rep.check(f"{step}=ok" in result, f"the buyer's {step} step went through")
    rep.check("across=True" in result, "the buyer and the vendor stood on either side of the line (C and E)")

    # The buyer quit with its fifth purchase on its way, and E asks about the fourth 15 s after it: the vendor's stand
    # window (and its save) outlasts both; its RESULT is the end of the market.
    rig.wait_for_bot(vendor_log, "stand", 480)
    time.sleep(8)
    c_lines, e_lines = rig.zone_lines("C", start, day), rig.zone_lines("E", start, day)
    b, v = re.escape(buyer_char), re.escape(vendor_char)
    for zone, lines, pattern, want, what in (
            ("C", c_lines, rf"Vending buy: {b}'s {PRICE} gold in escrow", SALES, f"took the price into escrow {SALES} times"),
            ("C", c_lines, rf"Handoff of {b} held: a purchase from {v}'s shop is on its way", 2, "held the buyer's hop for both purchases"),
            ("E", e_lines, r"the answer \(VendingSold\) to \S+ held 4 s", 1, "held its first answer 4 s"),
            ("E", e_lines, r"the answer \(VendingSold\) to \S+ held 13 s", 1, "held its second answer 13 s"),
            ("C", c_lines, rf"Vending bought here: {b} received", 2, "gave the buyer the first item (before its hop) and the fourth"),
            ("C", c_lines, rf"Vending escrow of {b} returned \(no answer from {v}'s cell in 10 s\)", 2, "gave the second and third prices back at the timeout"),
            ("E", e_lines, rf"{b} crossed onto the vendor's cell before the answer \(VendingSold\); handled here", 1, "handled the late answer itself"),
            ("E", e_lines, rf"Vending sold to {b} after its escrow went back: {PRICE} gold taken again", 1, "took the price again with the late item"),
            ("E", e_lines, rf"Crafting: {b}'s queue carried with the handoff, 0 own entries, 1 on workbenches, 0 dropped", 1, "put the bench's entry on its copy of the bench"),
            # DESIGN 1.48
            ("E", e_lines, r"the answer \(VendingSold\) to \S+ dropped \(MESH_TEST_VENDING_DROP sold3\)", 1, "dropped its third answer"),
            ("E", e_lines, rf"Vending: sale \S+ of {v} to {b} released after 60 s without a word", 1, "released the vendor from the fourth sale at a minute"),
            ("C", c_lines, rf"asked about sale \S+ for {b}, whose item never came here; not paid", 1, "said the third item never came"),

            ("C", c_lines, rf"Vending escrow of {b} returned \(left the cell before the answer\)", 1, "gave the fifth price back as the buyer left"),
            ("C", c_lines, r"the item of sale \S+ reached this cell after \S+ left it; not paid", 1, "told E the fifth item found no buyer"),
            ("E", e_lines, rf"Vending: sale \S+ of {v} to {b} not delivered \(the buyer's cell gave no item\)", 2, "put the third and fifth items back in the vendor's bag"),
            ("E", e_lines, rf"Vending paid here: {v} received {PRICE} gold for sale", 3, "paid the vendor for the three items delivered")):
        n = rig.count(lines, pattern)
        rep.check(n == want, f"{zone} {what} ({n}, expected {want})")
    problems = hops_during_escrows(c_lines, buyer_char)
    rep.check(not problems, "no hop of the buyer while a purchase was on its way" + (f" ({problems[0]})" if problems else ""))
    n = rig.count(e_lines, rf"no word on sale \S+ \({v} to {b}\) after \d+ s; asking the buyer's cell")
    rep.check(n >= 4, f"E asked about the third sale and kept asking about the fourth ({n})")
    n = rig.count(c_lines, r"the word on sale \S+ \(paid\) dropped \(MESH_TEST_VENDING_BLACKOUT")
    rep.check(n >= 2, f"C dropped its word on the fourth sale and its answers to E's questions ({n})")
    n = rig.count(c_lines, r"asked about sale \S+ for \S+; this cell's word was paid")
    rep.check(n >= 1, f"C said paid when asked about the fourth ({n})")
    rep.check(rig.count(c_lines + e_lines, r"Vending: VendingSold for .* who moved on; passed to its owner") == 0,
              "no answer had to be passed on (the buyer was where the answer looked for it)")
    rep.check(rig.count(c_lines + e_lines, r"no word from the buyer's cell in \d+ s|are lost") == 0,
              "every sale was settled on the buyer's cell's word, nothing lost")
    # The database once both bots are gone (each saves on its way out): 3 items delivered and 8 crafts from 100 gold,
    # the vendor paid 3 x 7, and 2 of its 5 items back in its bag.
    buyer_gold, vendor_gold = gold_of(rig, buyer_char), gold_of(rig, vendor_char)
    rep.check(buyer_gold == BUYER_GOLD - 3 * PRICE - CRAFTS, f"the buyer's gold in the database {buyer_gold}, expected {BUYER_GOLD - 3 * PRICE - CRAFTS}")
    rep.check(vendor_gold == 3 * PRICE, f"the vendor's gold in the database {vendor_gold}, expected {3 * PRICE}")
    bag = bag_amount(rig, vendor_char)
    rep.check(bag == vendor_bag - 3, f"the vendor's bag in the database holds {bag} items, expected {vendor_bag - 3} ({vendor_bag} less the 3 delivered)")


# ------------------------------------------------------------------ trade

TRADE_LINE = 100.0
TRADE_DRIVER, TRADE_DRIVER_AT = "meshbot2", (99.4, 52.0)   # clear of market's layout (z 56-58) and harvest's tree
TRADE_ACCEPTOR_AT = (100.6, 52.0)
TRADE_DROPS = "commit2,committed2"   # MESH_TEST_TRADE_DROP: E's second commit, C's second committed answer
TRADE_DELAYS = "0,0,0,3"             # MESH_TEST_TRADE_ANSWER_DELAY: C's answers to the commits it handles (the fifth trade's)
TRADE_BLACKOUT = "3:75"              # MESH_TEST_TRADE_BLACKOUT: C's third commit handled (the fourth trade) answered after 75 s
# The pause lets the fourth trade settle on the fixture before the fifth, on which it logs out: the driver's side of the
# fourth ends at once, the fixture's is released at 60 s and answered at 90 s (the first rig runs of DESIGN 1.51 started
# the fifth at the release, so both were mailed).
TRADE_STEPS = "trade,tradefail,trade,tradelate,pause110,trade,quit"
TRADES = 5
TRADE_OFFER_GOLD = 5                 # ServerMeshAutoClient.socialGold
MESH_MAIL_SENDER = "Server Meshing"  # MeshMapNetworkManager.MeshMailSender
ITEM_SKILL = provision_rows.ITEM_SKILL
TRADE_GOLD = 100


def item_count(rig, name, data_id):
    rows = rig.db_rows("select sum(i.amount) from characteritem i join characters c on c.id = i.characterId "
                       "where c.characterName=%s and i.inventoryType=%s and i.dataId=%s", (name, provision_rows.INVENTORY_BAG, data_id))
    return int(rows[0][0] or 0) if rows else None


def mails_to(rig, character_name, since):
    """The add-on's mails to a character's account since `since` (epoch seconds): [(gold, items string)]."""
    rows = rig.db_rows("select m.gold, m.items from mail m join characters c on c.userId = m.receiverId "
                       "where c.characterName=%s and m.senderName=%s and m.sentTimestamp >= %s",
                       (character_name, MESH_MAIL_SENDER, int(since)))
    return [(int(g or 0), items or "") for g, items in rows]


def mailed_amount(mails, data_id):
    """How many of an item the mails hold (the kit writes items as `dataId:amount:...` separated by `;`)."""
    total = 0
    for _, items in mails:
        for entry in items.split(";"):
            fields = entry.split(":")
            if len(fields) >= 2 and fields[0].lstrip("-").isdigit() and int(fields[0]) == data_id:
                total += int(fields[1])
    return total


def run_trade(rep, rig, start, day, bots):
    driver_char, acceptor_char = f"{TRADE_DRIVER}Char", provision.BOT.character_name
    rig.db_reset(TRADE_DRIVER_AT, TRADE_ACCEPTOR_AT, tester_party=0, bot_party=0)
    provision.reset_bot_positions(2, 1, [TRADE_DRIVER_AT], status_path=rig.STATUS)
    setup_bags(rig, {driver_char: ([provision_rows.Item(ITEM_BENCH, amount=5)], TRADE_GOLD),
                     acceptor_char: (provision_rows.COMMON_BAG, TRADE_GOLD)})
    conn = rig.db_connect()
    try:
        with conn.cursor() as cur:   # the add-on's mails of earlier runs
            cur.execute("delete from mail where senderName=%s and receiverId in (select userId from characters where characterName=%s)",
                        (MESH_MAIL_SENDER, acceptor_char))
        conn.commit()
    finally:
        conn.close()
    begun = 0
    rig.request_play("stand", TRADE_DRIVER_AT[0], TRADE_DRIVER_AT[1], enabled=False, line_x=TRADE_LINE,
                     env={"MESH_NO_MONSTER_HANDOFF": "1", "MESH_NO_MONSTERS": "1", "MESH_TEST_TRADE_DROP": TRADE_DROPS,
                          "MESH_TEST_TRADE_ANSWER_DELAY": TRADE_DELAYS, "MESH_TEST_TRADE_BLACKOUT": TRADE_BLACKOUT})
    rig.wait_play(60)
    time.sleep(20)
    acceptor_log = rig.launch_bot("trade", provision.BOT.username,
                                  ["-meshAutoMode", "stand", "-meshWalkX", f"{TRADE_ACCEPTOR_AT[0]:g}", "-meshWalkZ", f"{TRADE_ACCEPTOR_AT[1]:g}",
                                   "-meshSocialAccept", "-meshSocialQuitOnTrade", str(TRADES), "-meshSocialQuitSafe"], observe_seconds=480)
    bots.append(("stand", acceptor_log))
    time.sleep(10)
    driver_log = rig.launch_bot("trade", TRADE_DRIVER,
                                ["-meshAutoMode", "social", "-meshWalkX", f"{TRADE_DRIVER_AT[0]:g}", "-meshWalkZ", f"{TRADE_DRIVER_AT[1]:g}",
                                 "-meshSocialTarget", acceptor_char, "-meshSocialSteps", TRADE_STEPS], line_x=TRADE_LINE)
    bots.append(("social", driver_log))
    result = rig.wait_for_bot(driver_log, "social", 600)
    rep.check("-> PASS" in result, f"driver: {result[:300] or 'no RESULT'}")
    for step in ("trade1", "tradefail2", "trade3", "tradelate4", "trade5"):
        rep.check(f"{step}=ok" in result, f"the driver's {step} went as expected")
    rep.check("across=True" in result, "the two traders stood on either side of the line (C and E)")
    # The fixture logged out on the fifth trade (the kit's safe disconnect: saved and destroyed at once), the driver quit
    # at the end; the kit's save scheduler writes a character at most every 20 s (characterSaveMinIntervalSeconds).
    time.sleep(30)
    c_lines, e_lines = rig.zone_lines("C", start, day), rig.zone_lines("E", start, day)
    d, a = re.escape(driver_char), re.escape(acceptor_char)
    for zone, lines, pattern, want, what in (
            ("E", e_lines, rf"Trade commit: {a}'s offer .* in escrow", TRADES, f"took the fixture's offer into escrow {TRADES} times"),
            ("E", e_lines, rf"Trade commit \S+ of {a} dropped \(MESH_TEST_TRADE_DROP commit2\)", 1, "dropped its second commit"),
            ("C", c_lines, rf"Trade \S+ asked about for {d}, whose cell never saw it: failed", 1, "answered the dropped exchange failed"),
            ("E", e_lines, rf"Trade escrow of {a} returned \(refused by the other cell", 1, "gave the second escrow back"),
            ("C", c_lines, r"the answer \(committed\) dropped \(MESH_TEST_TRADE_DROP committed2\)", 1, "dropped its second committed answer"),
            # DESIGN 1.51: the fourth trade's answers are dropped for 75 s, past the fixture's release at 60 s
            ("C", c_lines, r"Trade \S+: every answer dropped for 75 s \(MESH_TEST_TRADE_BLACKOUT", 1, "blacked out the fourth trade's answers"),
            ("E", e_lines, rf"Trade \S+ of {a} released after 60 s without an answer", 1, "released the fixture from the fourth trade at a minute"),
            ("E", e_lines, rf"Trade committed here: {a} received .* s after the commit\)", 1, "settled the fourth trade on the fixture after its release"),
            # the fifth: the fixture logs out with the exchange in escrow, and its answer is mailed
            ("E", e_lines, rf"Trade \S+ of {a}: the trader left the cell with its exchange in escrow; asking the other cell", 1, "asked at once when the fixture left"),
            ("E", e_lines, rf"Trade \S+ committed for {a}, who is not on this cell: mailed to {a}", 1, "mailed the fifth trade's result to the fixture"),
            ("E", e_lines, r"Trade \S+ committed for \S+ again \(a repeated answer\); settled already", 1, "took the held answer as a repeat"),
            ("E", e_lines, rf"Trade committed here: {a} received", TRADES - 2, f"gave {TRADES - 2} exchanges to the fixture on the cell"),
            ("C", c_lines, rf"Trade committed here: {d} gave", TRADES - 1, f"did the driver's half {TRADES - 1} times")):
        n = rig.count(lines, pattern)
        rep.check(n == want, f"{zone} {what} ({n}, expected {want})")
    n = rig.count(c_lines, r"the answer \(committed\) dropped \(MESH_TEST_TRADE_BLACKOUT")
    rep.check(n >= 2, f"C dropped the fourth trade's answer and its answers to E's questions ({n})")
    n = rig.count(c_lines, rf"Trade \S+ asked about for {d}: the same answer \(committed\)")
    rep.check(n >= 3, f"C answered committed again when asked (the third, the fourth and the fifth: {n})")
    rep.check(rig.count(c_lines + e_lines, r"with no escrow here|no answer from the other cell in \d+ s|are lost") == 0,
              "every exchange was settled on the other cell's answer, nothing lost")
    # The database: each exchange moved one bench to the fixture and one scroll to the driver (the fifth bench and its
    # 5 gold by mail); the driver's gold is unchanged, the fixture's short of the mailed 5.
    exchanges = TRADES - 1
    mails = mails_to(rig, acceptor_char, begun)
    rep.check(len(mails) == 1, f"one mail to the fixture's account ({len(mails)})")
    mailed_bench, mailed_gold = mailed_amount(mails, ITEM_BENCH), sum(g for g, _ in mails)
    rep.check(mailed_bench == 1 and mailed_gold == TRADE_OFFER_GOLD, f"the mail holds the fifth trade's bench and gold ({mailed_bench} bench, {mailed_gold} gold)")
    for name, data_id, want, what in ((acceptor_char, ITEM_BENCH, exchanges - 1, "benches in its bag"), (driver_char, ITEM_SKILL, exchanges, "scrolls"),
                                      (driver_char, ITEM_BENCH, 5 - exchanges, "benches left"), (acceptor_char, ITEM_SKILL, 20 - exchanges, "scrolls left")):
        n = item_count(rig, name, data_id)
        rep.check(n == want, f"{name} has {n} {what} in the database, expected {want}")
    ga, gd = gold_of(rig, acceptor_char), gold_of(rig, driver_char)
    rep.check(gd == TRADE_GOLD and ga == TRADE_GOLD - TRADE_OFFER_GOLD,
              f"the traders' gold in the database: driver {gd} (expected {TRADE_GOLD}), fixture {ga} (expected {TRADE_GOLD - TRADE_OFFER_GOLD}, the rest in the mail)")


# ------------------------------------------------------------------ tradestop

TRADESTOP_STEPS = "trade,trade,quit"
TRADESTOP_BLACKOUT = "2:45"         # C's second commit handled: answered after 45 s
TRADESTOP_QUIT = "E:5"              # E quits cleanly once an exchange has been open 5 s
# The cells' exchange journals: <persistentDataPath>/ServerMeshing of the player build (MeshMapNetworkManager.exchangeJournalFolder).
JOURNAL_FOLDER = os.path.join(os.path.expanduser("~"), "AppData", "LocalLow", "DefaultCompany", "kit-ce-project", "ServerMeshing")


def clear_journals():
    """No journal left by an earlier run may be taken up by this one's cells."""
    if not os.path.isdir(JOURNAL_FOLDER):
        return 0
    removed = 0
    for name in os.listdir(JOURNAL_FOLDER):
        if name.startswith("ServerMesh.Exchanges.") and name.endswith(".bin"):
            os.remove(os.path.join(JOURNAL_FOLDER, name))
            removed += 1
    return removed


def run_tradestop(rep, rig, start, day, bots):
    driver_char, acceptor_char = f"{TRADE_DRIVER}Char", provision.BOT.character_name
    rig.db_reset(TRADE_DRIVER_AT, TRADE_ACCEPTOR_AT, tester_party=0, bot_party=0)
    provision.reset_bot_positions(2, 1, [TRADE_DRIVER_AT], status_path=rig.STATUS)
    setup_bags(rig, {driver_char: ([provision_rows.Item(ITEM_BENCH, amount=5)], TRADE_GOLD),
                     acceptor_char: (provision_rows.COMMON_BAG, TRADE_GOLD)})
    conn = rig.db_connect()
    try:
        with conn.cursor() as cur:
            cur.execute("delete from mail where senderName=%s and receiverId in (select userId from characters where characterName=%s)",
                        (MESH_MAIL_SENDER, acceptor_char))
        conn.commit()
    finally:
        conn.close()
    stale = clear_journals()
    if stale:
        rep.notes.append(f"   {stale} exchanges journal(s) of an earlier run removed")
    rig.request_play("stand", TRADE_DRIVER_AT[0], TRADE_DRIVER_AT[1], enabled=False, line_x=TRADE_LINE,
                     env={"MESH_NO_MONSTER_HANDOFF": "1", "MESH_NO_MONSTERS": "1", "MESH_TEST_TRADE_BLACKOUT": TRADESTOP_BLACKOUT,
                          "MESH_FAULT_QUIT_WITH_EXCHANGE": TRADESTOP_QUIT})
    rig.wait_play(60)
    time.sleep(20)
    acceptor_log = rig.launch_bot("tradestop", provision.BOT.username,
                                  ["-meshAutoMode", "stand", "-meshWalkX", f"{TRADE_ACCEPTOR_AT[0]:g}", "-meshWalkZ", f"{TRADE_ACCEPTOR_AT[1]:g}",
                                   "-meshSocialAccept"], observe_seconds=300)
    bots.append(("stand", acceptor_log))
    time.sleep(10)
    driver_log = rig.launch_bot("tradestop", TRADE_DRIVER,
                                ["-meshAutoMode", "social", "-meshWalkX", f"{TRADE_DRIVER_AT[0]:g}", "-meshWalkZ", f"{TRADE_DRIVER_AT[1]:g}",
                                 "-meshSocialTarget", acceptor_char, "-meshSocialSteps", TRADESTOP_STEPS], line_x=TRADE_LINE)
    bots.append(("social", driver_log))
    result = rig.wait_for_bot(driver_log, "social", 420)
    rep.check("-> PASS" in result, f"driver: {result[:300] or 'no RESULT'}")
    rep.check("across=True" in result, "the two traders stood on either side of the line (C and E)")
    # E restarts and asks about the second trade (released: every 30 s) after C's 45 s; then the saves (20 s at most).
    e_end = time.time() + 150
    while time.time() < e_end:
        e_lines = rig.zone_lines("E", start, day)
        if rig.count(e_lines, r"Trade committed here: .*s after the commit\)|committed for \S+, who is not on this cell: mailed") >= 1:
            break
        time.sleep(5)
    time.sleep(30)
    c_lines, e_lines = rig.zone_lines("C", start, day), rig.zone_lines("E", start, day)
    a = re.escape(acceptor_char)
    for zone, lines, pattern, want, what in (
            ("E", e_lines, rf"Trade commit: {a}'s offer .* in escrow", 2, "took the fixture's offer into escrow twice"),
            ("C", c_lines, r"Trade \S+: every answer dropped for 45 s \(MESH_TEST_TRADE_BLACKOUT", 1, "blacked out the second trade's answers"),
            ("E", e_lines, r"FAULT: quitting on an exchange open \d+ s \(MESH_FAULT_QUIT_WITH_EXCHANGE\)", 1, "quit cleanly with the second trade open"),
            ("E", e_lines, r"Exchanges journal written as the cell stops: 1 trade escrow\(s\)", 1, "wrote the open escrow to its journal"),
            ("E", e_lines, r"Exchanges journal taken up: 1 trade escrow\(s\)", 1, "took the journal up after its restart")):
        n = rig.count(lines, pattern)
        rep.check(n == want, f"{zone} {what} ({n}, expected {want})")
    applied = rig.count(e_lines, rf"Trade committed here: {a} received .* s after the commit\)")
    mailed = rig.count(e_lines, rf"Trade \S+ committed for {a}, who is not on this cell: mailed to {a}")
    rep.check(applied + mailed == 1, f"E settled the second trade after its restart ({'on the fixture' if applied else 'by mail' if mailed else 'not at all'})")
    rep.check(rig.count(c_lines + e_lines, r"with no escrow here|no answer from the other cell in \d+ s|are lost") == 0,
              "every exchange was settled on the other cell's answer, nothing lost")
    # The database: two exchanges, the second one's bench and 5 gold in the fixture's bag or in its mail.
    mails = mails_to(rig, acceptor_char, 0)
    benches = item_count(rig, acceptor_char, ITEM_BENCH) + mailed_amount(mails, ITEM_BENCH)
    rep.check(benches == 2, f"the fixture has 2 benches in its bag and mail ({benches}; {len(mails)} mail(s))")
    for name, data_id, want, what in ((driver_char, ITEM_SKILL, 2, "scrolls"), (driver_char, ITEM_BENCH, 3, "benches left"),
                                      (acceptor_char, ITEM_SKILL, 18, "scrolls left")):
        n = item_count(rig, name, data_id)
        rep.check(n == want, f"{name} has {n} {what} in the database, expected {want}")
    ga, gd = gold_of(rig, acceptor_char), gold_of(rig, driver_char)
    gm = sum(g for g, _ in mails)
    rep.check(gd == TRADE_GOLD and ga + gm == TRADE_GOLD, f"the traders' gold: driver {gd} (expected {TRADE_GOLD}), fixture {ga} + {gm} mailed (expected {TRADE_GOLD})")


# ------------------------------------------------------------------ vendleave, buystop, salestop

VENDFAULT = {
    # name: (the cells' environment, the vendor's extra arguments, the buyer's steps)
    "vendleave": ({"MESH_TEST_VENDING_BLACKOUT": "1:40"}, ["-meshSocialQuitAfterSales", "1", "-meshSocialQuitSafe"], "buy,quit"),
    "buystop": ({"MESH_TEST_VENDING_ANSWER_DELAY": "8", "MESH_FAULT_QUIT_WITH_EXCHANGE": "C:3"}, [], "buy"),
    "salestop": ({"MESH_TEST_VENDING_BLACKOUT": "1:30", "MESH_FAULT_QUIT_WITH_EXCHANGE": "E:5"}, [], "buy,quit"),
}


def clear_mail(rig, *character_names):
    conn = rig.db_connect()
    try:
        with conn.cursor() as cur:
            for name in character_names:
                cur.execute("delete from mail where senderName=%s and receiverId in (select userId from characters where characterName=%s)",
                            (MESH_MAIL_SENDER, name))
        conn.commit()
    finally:
        conn.close()


def wait_lines(rig, zone, start, day, pattern, timeout):
    end = time.time() + timeout
    while time.time() < end:
        if rig.count(rig.zone_lines(zone, start, day), pattern) >= 1:
            return True
        time.sleep(5)
    return False


def run_vendfault(name, rep, rig, start, day, bots):
    env, vendor_args, steps = VENDFAULT[name]
    buyer_char, vendor_char = f"{MARKET_BUYER}Char", provision.BOT.character_name
    rig.db_reset(MARKET_BUYER_AT, MARKET_VENDOR_AT, tester_party=0, bot_party=0)
    provision.reset_bot_positions(2, 1, [MARKET_BUYER_AT], status_path=rig.STATUS)
    setup_bags(rig, {buyer_char: ([], BUYER_GOLD), vendor_char: (provision_rows.COMMON_BAG, 0)})
    clear_mail(rig, buyer_char, vendor_char)
    clear_journals()
    vendor_bag = sum(item.amount for item in provision_rows.COMMON_BAG)
    rig.request_play("stand", MARKET_BUYER_AT[0], MARKET_BUYER_AT[1], enabled=False, line_x=MARKET_LINE,
                     env=dict({"MESH_NO_MONSTER_HANDOFF": "1", "MESH_NO_MONSTERS": "1"}, **env))
    rig.wait_play(60)
    time.sleep(20)
    vendor_log = rig.launch_bot(name, provision.BOT.username,
                                ["-meshAutoMode", "stand", "-meshWalkX", f"{MARKET_VENDOR_AT[0]:g}", "-meshWalkZ", f"{MARKET_VENDOR_AT[1]:g}",
                                 "-meshSocialAccept", "-meshSocialVend", "-meshSocialVendNow", "-meshSocialVendCount", "1"] + vendor_args,
                                observe_seconds=300)
    bots.append(("stand", vendor_log))
    time.sleep(10)
    buyer_log = rig.launch_bot(name, MARKET_BUYER,
                               ["-meshAutoMode", "social", "-meshWalkX", f"{MARKET_BUYER_AT[0]:g}", "-meshWalkZ", f"{MARKET_BUYER_AT[1]:g}",
                                "-meshSocialTarget", vendor_char, "-meshSocialSteps", steps], line_x=MARKET_LINE)
    bots.append(("social", buyer_log))
    b, v = re.escape(buyer_char), re.escape(vendor_char)
    if name == "buystop":
        # The buyer's cell stops under it: its RESULT is not this scenario's; the sale's end is. Either the item goes back to
        # the vendor (the buyer away when the late answer comes), or the buyer, logged in again on the vendor's own cell
        # (its own was down), receives it there and pays again (the first rig run): the restarted C refunded the escrow.
        settled = wait_lines(rig, "E", start, day, rf"Vending: sale \S+ of {v} to {b} not delivered|Vending paid here: {v} received {PRICE} gold", 180)
    else:
        result = rig.wait_for_bot(buyer_log, "social", 300)
        rep.check("buy=ok" in result, f"the buyer bought the shop's item: {result[:240] or 'no RESULT'}")
        settled = wait_lines(rig, "E", start, day, rf"Vending paid here: {v} received {PRICE} gold|paid for {v}, who is not on this cell: mailed", 180)
    rep.check(settled, "E settled the sale")
    time.sleep(30)   # the kit's save scheduler writes a character at most every 20 s
    c_lines, e_lines = rig.zone_lines("C", start, day), rig.zone_lines("E", start, day)
    checks = [("E", e_lines, rf"Vending sold here: {v} sold .* to {b} across a line", 1, "sold the item")]
    if name == "vendleave":
        checks += [("E", e_lines, rf"Vending: sale \S+ paid for {v}, who is not on this cell: mailed to {v}", 1, "mailed the vendor its gold")]
    elif name == "buystop":
        checks += [("C", c_lines, r"FAULT: quitting on an exchange open \d+ s \(MESH_FAULT_QUIT_WITH_EXCHANGE\)", 1, "quit cleanly with the purchase in escrow"),
                   ("C", c_lines, r"Exchanges journal written as the cell stops: 0 trade escrow\(s\), 1 purchase\(s\)", 1, "wrote the purchase to its journal"),
                   ("C", c_lines, r"Exchanges journal taken up: .* 1 purchase\(s\) refunded", 1, "took the journal up after its restart"),
                   ("C", c_lines, rf"Vending: purchase \S+ of {b} refunded after the restart: mailed to {b}", 1, "mailed the buyer its gold")]
    else:
        checks += [("E", e_lines, r"FAULT: quitting on an exchange open \d+ s \(MESH_FAULT_QUIT_WITH_EXCHANGE\)", 1, "quit cleanly with the sale open"),
                   ("E", e_lines, r"Exchanges journal written as the cell stops: 0 trade escrow\(s\), 0 purchase\(s\), 1 sale\(s\)", 1, "wrote the sale to its journal"),
                   ("E", e_lines, r"Exchanges journal taken up: 0 trade escrow\(s\) and 1 sale\(s\)", 1, "took the journal up after its restart")]
    for zone, lines, pattern, want, what in checks:
        n = rig.count(lines, pattern)
        rep.check(n == want, f"{zone} {what} ({n}, expected {want})")
    if name != "buystop":
        n = rig.count(c_lines, r"the word on sale \S+ \(paid\) dropped \(MESH_TEST_VENDING_BLACKOUT")
        rep.check(n >= 1, f"C dropped its word on the sale ({n})")
    rep.check(rig.count(c_lines + e_lines, r"are lost|no word from the buyer's cell in \d+ s") == 0, "nothing lost, the sale settled on the buyer's cell's word")
    # The database: the gold in the bags and the mails, and the item with the buyer or back with the vendor.
    vendor_mails, buyer_mails = mails_to(rig, vendor_char, 0), mails_to(rig, buyer_char, 0)
    vg, bg = gold_of(rig, vendor_char), gold_of(rig, buyer_char)
    vm, bm = sum(g for g, _ in vendor_mails), sum(g for g, _ in buyer_mails)
    if name == "buystop":
        bought = item_count(rig, buyer_char, ITEM_SKILL)
        returned = rig.count(e_lines, rf"Vending: sale \S+ of {v} to {b} not delivered")
        rep.check(bought + returned == 1 and bag_amount(rig, vendor_char) + bought == vendor_bag,
                  f"the item with the buyer ({bought}) or back in the vendor's bag ({bag_amount(rig, vendor_char)} of {vendor_bag}), exactly one of them")
        rep.check(vg + vm == PRICE * bought and bg + bm == BUYER_GOLD - PRICE * bought,
                  f"one price per item held: vendor {vg}+{vm} mailed, buyer {bg}+{bm} mailed (the buyer holds {bought} item(s))")
    else:
        rep.check(vg + vm == PRICE and bg == BUYER_GOLD - PRICE, f"one sale: vendor {vg}+{vm} mailed (expected {PRICE}), buyer {bg} (expected {BUYER_GOLD - PRICE})")
        rep.check(bag_amount(rig, vendor_char) == vendor_bag - 1 and item_count(rig, buyer_char, ITEM_SKILL) == 1,
                  f"the item with the buyer (vendor's bag {bag_amount(rig, vendor_char)} of {vendor_bag})")


# ------------------------------------------------------------------ sleephold

def run_sleephold(rep, rig, start, day, bots):
    driver_char, acceptor_char = f"{TRADE_DRIVER}Char", provision.BOT.character_name
    rig.db_reset(TRADE_DRIVER_AT, TRADE_ACCEPTOR_AT, tester_party=0, bot_party=0)
    provision.reset_bot_positions(2, 1, [TRADE_DRIVER_AT], status_path=rig.STATUS)
    setup_bags(rig, {driver_char: ([provision_rows.Item(ITEM_BENCH, amount=5)], TRADE_GOLD),
                     acceptor_char: (provision_rows.COMMON_BAG, TRADE_GOLD)})
    clear_mail(rig, acceptor_char)
    clear_journals()
    rig.request_play("stand", TRADE_DRIVER_AT[0], TRADE_DRIVER_AT[1], enabled=False, line_x=TRADE_LINE,
                     env={"MESH_NO_MONSTER_HANDOFF": "1", "MESH_NO_MONSTERS": "1", "MESH_SLEEP": "1", "MESH_SLEEP_RINGS": "0",
                          "MESH_SLEEP_AFTER": "30", "MESH_TEST_TRADE_BLACKOUT": "1:150"})
    rig.wait_play(60)
    time.sleep(20)
    acceptor_log = rig.launch_bot("sleephold", provision.BOT.username,
                                  ["-meshAutoMode", "stand", "-meshWalkX", f"{TRADE_ACCEPTOR_AT[0]:g}", "-meshWalkZ", f"{TRADE_ACCEPTOR_AT[1]:g}",
                                   "-meshSocialAccept", "-meshSocialQuitOnTrade", "1", "-meshSocialQuitSafe"], observe_seconds=300)
    bots.append(("stand", acceptor_log))
    time.sleep(15)
    driver_log = rig.launch_bot("sleephold", TRADE_DRIVER,
                                ["-meshAutoMode", "social", "-meshWalkX", f"{TRADE_DRIVER_AT[0]:g}", "-meshWalkZ", f"{TRADE_DRIVER_AT[1]:g}",
                                 "-meshSocialTarget", acceptor_char, "-meshSocialSteps", "trade,quit"], line_x=TRADE_LINE)
    bots.append(("social", driver_log))
    result = rig.wait_for_bot(driver_log, "social", 300)
    rep.check("trade1=ok" in result, f"the driver's trade: {result[:240] or 'no RESULT'}")
    a = re.escape(acceptor_char)
    mailed = wait_lines(rig, "E", start, day, rf"Trade \S+ committed for {a}, who is not on this cell: mailed to {a}", 300)
    rep.check(mailed, "E settled the trade by mail")
    slept = wait_lines(rig, "E", start, day, r"Sleeping: cell E has nobody near it", 120)
    time.sleep(30)
    c_lines, e_lines = rig.zone_lines("C", start, day), rig.zone_lines("E", start, day)
    for zone, lines, pattern, what in (
            ("C", c_lines, r"Sleeping: cell C has nobody near it", "slept once the driver left"),
            ("C", c_lines, r"Exchanges journal written as the cell stops: 0 trade escrow\(s\), 0 purchase\(s\), 0 sale\(s\), 1 answer\(s\)", "wrote its answer to its journal as it slept"),
            ("E", e_lines, r"Sleep asked for cell E but 1 exchange\(s\) across a line are open here: staying up", "stayed up while the exchange was open"),
            ("E", e_lines, r"Exchange: zone C is not linked; asking the central to wake it", "asked the central to wake C"),
            ("C", c_lines, r"Exchanges journal taken up: .* 1 answer\(s\) kept", "answered from its journal once woken")):
        rep.check(rig.count(lines, pattern) >= 1, f"{zone} {what}")
    mail_at = next((rigtools.stamp_of(line) for line in e_lines if re.search(rf"committed for {a}, who is not on this cell: mailed", line)), None)
    sleep_at = next((rigtools.stamp_of(line) for line in reversed(e_lines) if "Sleeping: cell E has nobody near it" in line), None)
    rep.check(slept and mail_at is not None and sleep_at is not None and sleep_at >= mail_at, f"E slept after the exchange settled (mailed {mail_at}, slept {sleep_at})")
    rep.check(rig.count(c_lines + e_lines, r"with no escrow here|no answer from the other cell in \d+ s|are lost") == 0, "nothing lost")
    mails = mails_to(rig, acceptor_char, 0)
    benches = item_count(rig, acceptor_char, ITEM_BENCH) + mailed_amount(mails, ITEM_BENCH)
    ga, gm = gold_of(rig, acceptor_char), sum(g for g, _ in mails)
    rep.check(benches == 1 and ga + gm == TRADE_GOLD, f"the fixture has the bench and its gold in its bag and mail ({benches} bench, {ga}+{gm} gold)")
    rep.check(item_count(rig, driver_char, ITEM_SKILL) == 1 and gold_of(rig, driver_char) == TRADE_GOLD, "the driver has the fixture's scroll and its gold")


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
