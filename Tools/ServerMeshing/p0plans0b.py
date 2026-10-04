"""Phase 0 stage 0b run plans (step 8a, decisions 6-11): the observed, fanout, targets, syncrpc, packets and prewarm
blocks as data. Pure: every input is an argument; nothing here runs the rig. Split out of p0plans (the ~500-line rule),
whose _spec, plan_key, block_order, stepped and _blocked these use; p0load runs the plans and p0dryrun prints them.

    p0plans0b.observed_plan("O")                          # a block's RunSpecs in run order (parts O, R, SA, M, S);
                                                          # also fanout_plan, targets_plan, syncrpc_plan, packets_plan
                                                          # and prewarm_plan
    p0plans0b.stepped_measure((50, 100, 200, 400), 90)    # 388: a stepped run's measured seconds (decision 4)

A stage 0b RunSpec is p0plans' (its docstring lists the fields) plus: game (fantasy or space, per arm; the jsonl name
uses it), kind (the load observed: character, monster or ship), payload_B (a record's bytes: 28 for a kit character
record, the ship wire's 54, 56 or 28), scale (MESH_SHIP_SCALE, else 1.0), stepped ({"steps": [P as run in this repeat],
"hold": s}, or None), expected_v (the V a row's Phantom window V move mean must reach 0.95 of, or None), eyes
(MESH_PHANTOM binds eyes), placed (the monsters MESH_MONSTER_PLACE places, else 0) and N (a prewarm burst's N, else
None). Every spec also sets MESH_METER_SENDS=count (the Send window the 0b readers and prewarm's spawn check read) and
MESH_LOAD_SEED=repeat (load players, placed monsters and prewarm points are seeded by it), and passes rigrun.check_env
when it is built. A stepped spec has warm-up 10 s and measure stepped_measure(steps, hold); its plan point is the
run's, and its rows add point.step.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import math

import p0phantom
import p0plans
import p0ships

FAMILIES = ("observed", "fanout", "targets", "syncrpc", "packets", "prewarm")   # step 8a's stage 0b families
OBSERVED_PARTS = ("O", "R", "SA", "M", "S")
FANTASY_RATES, SPACE_RATES = "60/0.03", "30/0.02"
METER = {"MESH_METER_SENDS": "count"}
KINEMATIC_PUPPETS = {"MESH_PUPPET_MOTION": "kinematic"}
PARITY, TIERED = "20/20/20", "20/10/5/20"     # sender tiers near/mid/far[/owner] Hz
KIT_RECORD_B = 28                             # a kit EntityState character record (step 5 decision 7)
WIRE_B = {"legacy54": 54, "header56": 56, "compact28": 28}
STEP_WARMUP, BINDS_PER_S, STEP_MARGIN_S = 10, 50, 20
HALVES = ("C", "E")
# b3 (observed O, R, SA): eyes on E over a4's owners on C; client-driven owners as character phantoms seeing 50 movers
OBSERVED_PLAYERS, OBSERVED_EYES, OWNER_POINTS, OWNER_VIEW = 200, (50, 200), (200, 400), 50
# b4 (observed M): 200 placed monsters on C inside a4's band, 10 m from the halves cut
M_EYES, M_MONSTERS, M_DISC, M_RATES = 50, 200, "-60,0,50", (SPACE_RATES, FANTASY_RATES)
# b5 (observed S)
S_SCALE, S_DENSE_SCALE, FEEDER_HZ, PARKED = 0.03, 1.0, 20, 200
# b6 (fanout): every eye's host within range of every other host (2 x sqrt(2) x 25 = 71 m < 80 m; x 15 = 42 m < 60 m)
FANOUT_V, TIERED_V, FANOUT_EYES, FANOUT_SCALE = (100, 300, 500), (300, 500), 400, 0.03
FANOUT_STEPS, FANOUT_HOLD, CLASSIFY_BUILDS, SUBSET_V = (50, 100, 200, 400), 90, 10, 300
CHARACTER_AREA, SHIP_AREA = "0,0,25", "0,0,15"
# b6's targets and sync/RPC, b7 (packets): ship arms at scale 0.5, where a 1,000 m range covers the cell's diagonal
TARGET_SCALE, SYNC_POINTS = 0.5, ((0, 0), (0, 2100), (250, 0), (250, 2100))
PACKET_P, PACKET_V, PACKET_BYTES = 400, 500, (1020, 510, 255)
CAP_P, SATURATION_STEPS, SATURATION_HOLD = (100, 400), (100, 200, 400, 800), 60
# b8 (prewarm): a burst 10 s into the measured window
PREWARM_N, PREWARM_AFTER_WARMUP, PREWARM_EYES, PREWARM_PLAYERS = (100, 300, 650), 10, 100, 200
PACED_N, PACED_PER_FRAME, RANGE_POINTS, GROUP_POINTS = 650, 50, (160, 300), (100, 300)
# The group arm's eyes sit on 100 players, not 200: its N arriving server characters come on top, and at 60 fps 200 + 100
# saturated C on A before the burst (smoke run 2026-10-04), which leaves the burst unmeasurable.
GROUP_PREWARM_PLAYERS = 100


def stepped_measure(steps, hold, per_s=BINDS_PER_S, margin=STEP_MARGIN_S):
    """A stepped run's measured seconds (decision 4), rounded up: the holds, the bind ramps at per_s binds/s (0 to the
    first step, then each increase; a decrease unbinds at once) and the margin. Refuses no steps, a step below 1 or a
    hold not above 0."""
    if not steps or min(steps) < 1 or not hold > 0:
        raise ValueError(f"a stepped run needs steps of at least 1 and a hold above 0; got {list(steps)} @ {hold}")
    ramps = sum(max(0, later - earlier) for earlier, later in zip((0,) + tuple(steps), steps))
    return math.ceil(len(steps) * hold + ramps / per_s + margin)


def _merged(*parts):
    """The dicts merged left to right; refuses a key two of them set, so no arm overrides another's key unseen."""
    env = {}
    for part in parts:
        twice = sorted(set(env) & set(part))
        if twice:
            raise ValueError(f"{', '.join(twice)} set twice in one spec")
        env.update(part)
    return env


def _eyes(count, cells, view=0):
    """Virtual eyes on the cells; view 0 is host mode (each eye sits on one of the cell's existing movers)."""
    return {"MESH_PHANTOM": f"virtual:{count}:eye:{cells}", "MESH_PHANTOM_VIEW": str(view)}


def _placed(text):
    return sum(int(part.split("@")[0]) for part in text.split(";")) if text else 0


def _phantom_check(env, stepped):
    """Whether the spec binds eyes; refuses what ServerMeshPhantomSettings would: a bad MESH_PHANTOM, a key beside a
    ship phantom, steps on another kind or above P."""
    _, count, kind, _, error = p0phantom.phantom_setting(env.get("MESH_PHANTOM"))
    refusal = p0phantom.ship_phantom_refusal(env)
    if error or refusal:
        raise ValueError(f"MESH_PHANTOM {env.get('MESH_PHANTOM')!r}: {error or refusal}")
    if stepped and (kind != "eye" or max(stepped["steps"]) > count):
        raise ValueError(f"MESH_PHANTOM_STEPS {stepped['steps']} needs eyes binding at least {max(stepped['steps'])}; "
                         f"MESH_PHANTOM is {env.get('MESH_PHANTOM')!r}")
    return kind == "eye"


def _spec0b(family, case, arm, point, repeat, game, kind, env, rates, layout="coarse", started=("C",), plan=None,
            band=None, area=None, expect=None, steps=None, hold=None, warmup=p0plans.LONG[0], expected_v=None, n=None,
            saturation_probe=False):
    """One stage 0b RunSpec through p0plans._spec (monsters off, INSTR), with METER, the seed and its 0b fields; a
    stepped spec (steps, hold) runs its steps reversed on even repeats. Cells read are the cells started. A
    saturation probe (packets' saturation arm) is meant to saturate its cell: its rows are reported, never excluded."""
    env = _merged(METER, {"MESH_LOAD_SEED": str(repeat)}, env)
    stepped, timing = None, (warmup, p0plans.LONG[1])
    if steps:
        stepped = {"steps": p0plans.stepped(steps, repeat), "hold": hold}
        env = _merged(env, {"MESH_PHANTOM_STEPS": f"{','.join(str(p) for p in stepped['steps'])}@{hold:g}"})
        timing = (STEP_WARMUP, stepped_measure(stepped["steps"], hold))
    eyes = _phantom_check(env, stepped)
    spec = p0plans._spec(family, case, arm, point, repeat, layout, started, started, plan, env, rates=rates, band=band,
                         expect=expect, timing=timing, area=area)
    wire = env.get("MESH_SHIP_WIRE") or p0ships.DEFAULT_WIRE
    spec.update(game=game, kind=kind, payload_B=WIRE_B[wire] if kind == "ship" else KIT_RECORD_B,
                scale=float(env.get("MESH_SHIP_SCALE", 1.0)), stepped=stepped, expected_v=expected_v, eyes=eyes,
                placed=_placed(env.get("MESH_MONSTER_PLACE")), N=n, saturation_probe=saturation_probe)
    return spec


def _observed_o(repeats):
    """O: a4's halves, band and owners (C:200, kinematic), puppets on E, eyes on E in host mode."""
    def arm(name, extra):
        def make(eyes, repeat):
            point = {"eyes": eyes, "players": OBSERVED_PLAYERS, "rates": FANTASY_RATES}
            env = _merged(extra, _eyes(eyes, "E"))
            return _spec0b("observed", "O", name, point, repeat, "fantasy", "character", env, FANTASY_RATES, "halves",
                           HALVES, {"C": OBSERVED_PLAYERS}, band=p0plans.PRESENCE_BAND,
                           expect={"puppets": {"E": OBSERVED_PLAYERS}})
        return repeats, OBSERVED_EYES, make
    return [arm("puppets-steered", p0plans.KINEMATIC), arm("puppets-kinematic", _merged(p0plans.KINEMATIC,
                                                                                         KINEMATIC_PUPPETS))]


def _owners(case, arm, extra):
    """R and SA: N character phantoms on coarse C, each seeing 50 movers (its group)."""
    def entries(repeats):
        def make(owners, repeat):
            env = _merged(extra, {"MESH_PHANTOM": f"virtual:{owners}:character:C", "MESH_PHANTOM_VIEW": str(OWNER_VIEW)})
            return _spec0b("observed", case, arm, {"owners": owners, "view": OWNER_VIEW, "rates": FANTASY_RATES},
                           repeat, "fantasy", "character", env, FANTASY_RATES)
        return [(repeats, OWNER_POINTS, make)]
    return entries


def _observed_m(repeats):
    """M: 200 placed monsters on C (scene monsters and monster handoff off), eyes in host mode on C (owned) or on C and
    E (puppets: C's eyes keep the monsters awake, E's watch their puppets), at both rate sets."""
    place = {"MESH_MONSTER_PLACE": f"{M_MONSTERS}@{M_DISC}", "MESH_NO_MONSTER_HANDOFF": "1"}

    def arm(name, cells, extra):
        def make(rates, repeat):
            point = {"monsters": M_MONSTERS, "eyes": M_EYES, "eye_cells": cells, "rates": rates}
            return _spec0b("observed", "M", name, point, repeat, "fantasy", "monster",
                           _merged(place, extra, _eyes(M_EYES, cells)), rates, "halves", HALVES,
                           band=p0plans.PRESENCE_BAND)
        return repeats, M_RATES, make
    return [arm("owned", "C", {}), arm("puppets-steered", "C,E", {}),
            arm("puppets-kinematic", "C,E", KINEMATIC_PUPPETS)]


def _observed_s(repeats):
    """S: ships on coarse C at 30/0.02 and scale 0.03 (the dense row 1.0): AI, mirror, feeder ship and character
    puppets, parked ships with and without the park pin."""
    def arm(name, points, kind, extra_of, scale=S_SCALE):
        def make(value, repeat):
            extra, point = extra_of(value)
            return _spec0b("observed", "S", name, dict(point, scale=scale, rates=SPACE_RATES), repeat, "space", kind,
                           _merged(p0ships.scaled_env(scale), extra), SPACE_RATES)
        return repeats, points, make

    def ships(authority):
        return lambda n: ({"MESH_LOAD_SHIPS": f"C:{n}:{authority}"}, {"ships": n})

    def feeder(kind):
        return lambda n: ({"MESH_PUPPET_FEEDER": f"{kind}:{n}@{FEEDER_HZ}"}, {"puppets": n})

    def parked(sleep):
        return ({"MESH_LOAD_SHIPS": f"C:{PARKED}:parked", "MESH_SHIP_PARK_SLEEP": str(sleep)},
                {"ships": PARKED, "park_sleep": sleep})
    return [arm("ai", (100, 200, 400), "ship", ships("ai")),
            arm("mirror", (100, 200, 400, 800), "ship", ships("mirror")),
            arm("feeder-ship", (100, 200, 400), "ship", feeder("ship")),
            arm("feeder-character", (200,), "character", feeder("character")),
            arm("dense-ai", (100,), "ship", ships("ai"), S_DENSE_SCALE), arm("parked", (0, 1), "ship", parked)]


def observed_plan(part, repeats=3):
    """b3-b5 (family observed), one part per invocation: O 12, R 6, SA 6, M 18 and S 42 runs by default (decision 6).
    Refuses an unknown part."""
    builders = {"O": _observed_o, "R": _owners("R", "client-driven", {}),
                "SA": _owners("SA", "server-authoritative", {"MESH_PHANTOM_PAYLOAD": "input"}), "M": _observed_m,
                "S": _observed_s}
    chosen = str(part).strip().upper()
    if chosen not in builders:
        raise ValueError(f"observed has parts {', '.join(OBSERVED_PARTS)}; got {part!r}")
    return p0plans._blocked(builders[chosen](repeats), repeats)


SENDER_ON = {"MESH_SENDER": "on"}
FANOUT_CHARACTER = (("char-kit", {}, FANOUT_V),
                    ("char-sender-parity", dict(SENDER_ON, MESH_SENDER_TIERS=PARITY), FANOUT_V),
                    ("char-sender-tiered", dict(SENDER_ON, MESH_SENDER_TIERS=TIERED), TIERED_V))
FANOUT_SHIP = (("ship-kit-56", {"MESH_SHIP_WIRE": "header56"}, FANOUT_V),
               ("ship-sender-56-parity", dict(SENDER_ON, MESH_SHIP_WIRE="header56", MESH_SENDER_TIERS=PARITY), FANOUT_V),
               ("ship-sender-56-tiered", dict(SENDER_ON, MESH_SHIP_WIRE="header56", MESH_SENDER_TIERS=TIERED), TIERED_V),
               ("ship-sender-28-tiered", dict(SENDER_ON, MESH_SHIP_WIRE="compact28", MESH_SENDER_COMPACT="far",
                                              MESH_SENDER_TIERS=TIERED), TIERED_V))
SHIP_EXTRA = {arm: extra for arm, extra, _ in FANOUT_SHIP}


def _fanout_character(case, arm, extra, rates):
    """V kinematic load players crowded in CHARACTER_AREA on coarse C, 400 eyes in host mode stepped over them."""
    def make(v, repeat):
        env = _merged(p0plans.KINEMATIC, extra, _eyes(FANOUT_EYES, "C"))
        return _spec0b("fanout", case, arm, {"V": v, "rates": rates}, repeat, "fantasy", "character", env, rates,
                       plan={"C": v}, area=CHARACTER_AREA, steps=FANOUT_STEPS, hold=FANOUT_HOLD, expected_v=v)
    return make


def _fanout_ship(case, arm, extra, point_extra=None):
    """V mirror load ships in SHIP_AREA on coarse C at scale 0.03 and 30/0.02, 400 eyes in host mode stepped."""
    def make(v, repeat):
        env = _merged(p0ships.scaled_env(FANOUT_SCALE), extra, {"MESH_LOAD_SHIPS": f"C:{v}:mirror"},
                      _eyes(FANOUT_EYES, "C"))
        point = dict({"V": v, "rates": SPACE_RATES, "scale": FANOUT_SCALE}, **(point_extra or {}))
        return _spec0b("fanout", case, arm, point, repeat, "space", "ship", env, SPACE_RATES, area=SHIP_AREA,
                       steps=FANOUT_STEPS, hold=FANOUT_HOLD, expected_v=v)
    return make


def fanout_plan(repeats=3):
    """b6 (family fanout; decision 7): the seven arms (character arms at 30/0.02, ship arms at 30/0.02 and scale 0.03)
    at V 100, 300, 500 (tiered arms 300, 500), eyes stepped 50, 100, 200, 400 at 90 s; classify every 10 builds on
    ship-sender-56 parity at V 300; the character arms at 60/0.03 and V 300. 66 runs by default."""
    arms = [(repeats, points, _fanout_character("main", arm, extra, SPACE_RATES))
            for arm, extra, points in FANOUT_CHARACTER]
    arms += [(repeats, points, _fanout_ship("main", arm, extra)) for arm, extra, points in FANOUT_SHIP]
    parity = dict(SHIP_EXTRA["ship-sender-56-parity"], MESH_SENDER_CLASSIFY_BUILDS=str(CLASSIFY_BUILDS))
    arms.append((repeats, (SUBSET_V,), _fanout_ship("classify", "ship-sender-56-parity", parity,
                                                    {"classify_builds": CLASSIFY_BUILDS})))
    arms += [(repeats, (SUBSET_V,), _fanout_character("60fps", arm, extra, FANTASY_RATES))
             for arm, extra, _ in FANOUT_CHARACTER]
    return p0plans._blocked(arms, repeats)


def _ship_target(target, extra=None):
    """S-T (64 ship phantoms + 64 AI ships, V 128) or S-stress (400 + 100, V 500) at scale 0.5 on the header56 wire."""
    phantoms, ai, v = {"S-T": (64, 64, 128), "S-stress": (400, 100, 500)}[target]
    env = _merged(p0ships.scaled_env(TARGET_SCALE), {"MESH_PHANTOM": f"virtual:{phantoms}:ship:C",
                                                     "MESH_LOAD_SHIPS": f"C:{ai}:ai", "MESH_SHIP_WIRE": "header56"},
                  extra or {})
    return env, v


FIGHTS = {"F1": ({"MESH_PHANTOM": "virtual:150:character:C", "MESH_PHANTOM_AREA": "0,0,100,100",
                  "MESH_PHANTOM_STAND": "0.7", "MESH_MONSTER_PLACE": "30@0,0,50:passive"}, 150),
          "F2": ({"MESH_PHANTOM": "virtual:100:character:C", "MESH_PHANTOM_AREA": "disc:0,0,50",
                  "MESH_PHANTOM_FIGHT": "1", "MESH_MONSTER_PLACE": "100@0,0,50"}, 100)}
TARGET_POINTS = (("S-T", SPACE_RATES), ("S-stress", SPACE_RATES), ("F1", FANTASY_RATES), ("F1", SPACE_RATES),
                 ("F2", FANTASY_RATES), ("F2", SPACE_RATES))


def targets_plan(repeats=3):
    """b6's targets (family targets; decision 8): S-T and S-stress at 30/0.02 and scale 0.5, F1 and F2 (placed
    monsters, monster handoff off) at both rate sets, each in arms kit and tiered sender. 36 runs by default."""
    def arm(name, extra):
        def make(point, repeat):
            target, rates = point
            if target in FIGHTS:
                env, v = _merged(FIGHTS[target][0], {"MESH_NO_MONSTER_HANDOFF": "1"}, extra), FIGHTS[target][1]
                game, kind = "fantasy", "character"
            else:
                (env, v), game, kind = _ship_target(target, extra), "space", "ship"
            return _spec0b("targets", target, name, {"target": target, "rates": rates}, repeat, game, kind, env, rates,
                           expected_v=v)
        return repeats, TARGET_POINTS, make
    return p0plans._blocked([arm("kit", {}), arm("tiered", dict(SENDER_ON, MESH_SENDER_TIERS=TIERED))], repeats)


def syncrpc_plan(repeats=3):
    """b6's sync/RPC (family syncrpc; decision 9): S-T (kit, scale 0.5, 30/0.02) with MESH_SYNC_LOAD dirty D {0, 250}
    and rpc R {0, 2100}/s. 12 runs by default."""
    def make(point, repeat):
        dirty, rpc = point
        env, v = _ship_target("S-T", {"MESH_SYNC_LOAD": f"dirty:{dirty},rpc:{rpc}"})
        return _spec0b("syncrpc", "S-T", "kit", {"dirty": dirty, "rpc": rpc, "rates": SPACE_RATES}, repeat, "space",
                       "ship", env, SPACE_RATES, expected_v=v)
    return p0plans._blocked([(repeats, SYNC_POINTS, make)], repeats)


def packets_plan(repeats=3):
    """b7's virtual part (family packets; decision 10), at 30/0.02: kit, 400 character phantoms seeing 500 movers;
    cap, ship-sender-28 tiered at scale 0.5 with P ship phantoms + 500 - P AI ships, P {100, 400} x
    MESH_SENDER_PACKET_BYTES {1020, 510, 255}; saturation, eyes seeing 500 movers stepped 100, 200, 400, 800 at 60 s
    with MESH_THREAD_CPU=1. 24 runs by default."""
    def kit(p, repeat):
        env = {"MESH_PHANTOM": f"virtual:{p}:character:C", "MESH_PHANTOM_VIEW": str(PACKET_V)}
        return _spec0b("packets", "kit", "kit-character", {"P": p, "V": PACKET_V, "rates": SPACE_RATES}, repeat,
                       "fantasy", "character", env, SPACE_RATES)

    def cap(point, repeat):
        p, size = point
        env = _merged(p0ships.scaled_env(TARGET_SCALE), SHIP_EXTRA["ship-sender-28-tiered"],
                      {"MESH_PHANTOM": f"virtual:{p}:ship:C", "MESH_LOAD_SHIPS": f"C:{PACKET_V - p}:ai",
                       "MESH_SENDER_PACKET_BYTES": str(size)})
        return _spec0b("packets", "cap", "ship-sender-28-tiered", {"P": p, "V": PACKET_V, "packet_bytes": size,
                                                             "rates": SPACE_RATES, "scale": TARGET_SCALE},
                       repeat, "space", "ship", env, SPACE_RATES)

    def saturation(p, repeat):
        env = _merged(_eyes(p, "C", PACKET_V), {"MESH_THREAD_CPU": "1"})
        return _spec0b("packets", "saturation", "kit-character-eyes", {"P": p, "V": PACKET_V, "rates": SPACE_RATES},
                       repeat, "fantasy", "character", env, SPACE_RATES, steps=SATURATION_STEPS, hold=SATURATION_HOLD,
                       saturation_probe=True)
    caps = tuple((p, size) for p in CAP_P for size in PACKET_BYTES)
    return p0plans._blocked([(repeats, (PACKET_P,), kit), (repeats, caps, cap),
                             (repeats, (SATURATION_STEPS[-1],), saturation)], repeats)


def prewarm_plan(warmup=p0plans.LONG[0], repeats=3):
    """b8 (family prewarm; decision 11): bursts MESH_PREWARM=<kind>:<N>@<t>[/<perFrame>] at t = warmup + 10 s on coarse
    C: steered, kinematic (MESH_PUPPET_MOTION to match) and ship N {100, 300, 650} all at once; 650 kinematic at 50
    per frame; 650 kinematic with 100 eyes in host mode on 200 kinematic load players crowded at the burst disc; range
    80 -> 160 and 300 m with the eyes on 200 players spread over the cell; group arrival N {100, 300} with the eyes on
    players at the burst disc. Ship bursts at 30/0.02, the rest at 60/0.03. 45 runs by default."""
    at = warmup + PREWARM_AFTER_WARMUP

    def arm(case, name, points, kind, extra=None, per_frame=None, eyes_area=False, spread=False,
            players=PREWARM_PLAYERS):
        game, rates = ("space", SPACE_RATES) if kind == "ship" else ("fantasy", FANTASY_RATES)
        watched = eyes_area or spread
        motion = (extra or {}).get("MESH_PUPPET_MOTION") or "steered"
        if kind in ("steered", "kinematic") and motion != kind:
            raise ValueError(f"a {kind} prewarm needs MESH_PUPPET_MOTION {kind}; the arm sets {motion}")

        def make(n, repeat):
            burst = f"{kind}:{n}@{at}" + (f"/{per_frame}" if per_frame else "")
            env = _merged({"MESH_PREWARM": burst}, extra or {},
                          _merged(p0plans.KINEMATIC, _eyes(PREWARM_EYES, "C")) if watched else {})
            point = dict({"N": n, "at": at, "rates": rates}, **({"per_frame": per_frame} if per_frame else {}),
                         **({"eyes": PREWARM_EYES, "players": players} if watched else {}))
            return _spec0b("prewarm", case, name, point, repeat, game, "ship" if kind == "ship" else "character", env,
                           rates, plan={"C": players} if watched else None,
                           area=CHARACTER_AREA if eyes_area else None, warmup=warmup, n=n)
        return repeats, points, make
    return p0plans._blocked([arm("burst", "steered", PREWARM_N, "steered"),
                             arm("burst", "kinematic", PREWARM_N, "kinematic", KINEMATIC_PUPPETS),
                             arm("burst", "ship", PREWARM_N, "ship"),
                             arm("burst", "kinematic-paced", (PACED_N,), "kinematic", KINEMATIC_PUPPETS,
                                 PACED_PER_FRAME),
                             arm("burst", "kinematic-eyes", (PACED_N,), "kinematic", KINEMATIC_PUPPETS, eyes_area=True),
                             arm("range", "eyes", RANGE_POINTS, "range", spread=True),
                             arm("group", "eyes", GROUP_POINTS, "group", eyes_area=True,
                                 players=GROUP_PREWARM_PLAYERS)], repeats)
