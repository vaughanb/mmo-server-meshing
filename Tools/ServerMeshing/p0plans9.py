"""Phase 0 stage 0c run plans (step 9, step9_design.md decisions 4, 9, 11, 13): c1 database burst (dbburst), c2 fantasy
worker A/B (workerab), c3 isolated-spaces floor (spaces) and c4 packet loss (packets --loss) as data. Pure: nothing
here runs the rig, the database or machine B. Every spec is p0plans8b._spec8b's (a stage 0b RunSpec with peer and
timing); p0load9 runs the plans and p0dryrun prints them.

    p0plans9.dbburst_plan()            # c1: 21 runs (burst 9, sustained 8, calibration 4 with B); parts selectable
    p0plans9.workerab_plan()           # c2: 14 runs (2 coarse probes, then coarse and halves x {100, 200} x 3)
    p0plans9.spaces_plan()             # c3: 18 runs (stack S {1, 4, 16, 64} x {observers, +100 players} x 2, grid)
    p0plans9.loss_plan()               # c4: 9 runs (b7's cap-sink point x loss {0, 1, 5} % x 3, arms rotated)

Step 9 spec fields (relied on by p0blocks and p0peer): burst_rows (provision's meshburst rows a dbburst run needs, made
by an A-only before_play), stop_after_drain (the run ends once the editor's DB saves window reads pending 0 after the
burst's done line, bounded at DRAIN_BOUND_S after the last arrival), peer["impair"] (a remoteb impair profile B runs
under clumsy, the measurement opening after 'impairment on'), and peer["bots"]["stagger"] (0: the calibration's bots
enter at once). A burst's t counts from when the cell can instantiate, as MESH_PREWARM's does (decision 2).
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import p0plans
import p0plans0b
import p0plans8b

FAMILIES = ("dbburst", "workerab", "spaces")
RATES = p0plans0b.SPACE_RATES     # 30/0.02 (decisions 9 and 11; c1 the same, the canary's rates)
# c1 (decision 4)
DB_PARTS = ("burst", "sustained", "calibration")
BURST_N, SUSTAINED_R, SUSTAINED_S, CALIBRATION_N, CALIBRATION_PAIRS = (50, 100, 200), (5, 10, 15, 20), 40, 25, 2
BURST_ROWS, BURST_AFTER_S, DRAIN_BOUND_S = 800, 10, 120
SAVES_PROBE = {"MESH_DB_SAVES_PROBE": "1"}
# c2 (decision 9): half the players character phantoms (a fight share, a 50-mover view), half load players
WORKER_LOADS, WORKER_FALLBACK, WORKER_FIGHT, MONSTER_CELL = (100, 200), (50, 100), 0.5, "E"
# c2's players: connected character phantoms only, walking a rect inside C's half (x -250..-50) in both arms. Load
# players have no connection, so in coarse they never wake a monster while in halves their ghosts on E do (the mesh's
# ghost-watch rule): the 2026-10-09 smoke read 10 awake against 63. VIEW groups also could not keep clear of C's
# keep-out points in the narrower half, so the phantoms use an area.
WORKER_AREA = "-150,0,200,400"
WORKER_COMMON = {"MESH_FIGHT_GHOSTS": "1", "MESH_FORWARD_TIMING": "1", "MESH_MONSTER_AREAS": "all", "MESH_MONSTER_AWAKE": "1"}
# c3 (decision 11): 1 observer per space (standing character phantoms) and, in arm 100, 100 load players
SPACE_COUNTS, SPACES_LOAD, GRID_S = (1, 4, 16, 64), 100, 16
STACK_DISC, STACK_AREA = "disc:0,0,30", "0,0,30"          # observers' disc and the load's square (half side 30 m)
GRID_RECT, GRID_AREA = "0,0,600,600", "0,0,300"           # the 4 x 4 grid of 150 m on the 600 m ground
# c4 (decision 13): no impairment, then remoteb's loss1 and loss5 profiles
LOSS_ARMS = (("loss0", 0, None), ("loss1", 1, "loss1"), ("loss5", 5, "loss5"))
NET_STATS = {"MESH_NET_STATS": "1"}


def burst_measure(seconds=0):
    """A dbburst run's measured seconds: from the warm-up's end to the burst (BURST_AFTER_S), the burst's span and the
    drain's bound (decision 4: stop_after_drain usually ends it sooner)."""
    return BURST_AFTER_S + seconds + DRAIN_BOUND_S


def _parts(parts, known, what):
    chosen = [part.strip().lower() for part in (parts.split(",") if isinstance(parts, str) else parts) if part.strip()]
    unknown = sorted(set(chosen) - set(known))
    if unknown or not chosen:
        raise ValueError(f"{what} has parts {', '.join(known)}; got {', '.join(unknown) or 'none'}")
    return chosen


def _burst_spec(case, arm, point, repeat, setting, seconds=0, peer=None):
    """One burst run on coarse C with no load: MESH_DB_BURST=setting at warm-up + 10 s, the saves probe, burst_rows and
    stop_after_drain."""
    env = dict(SAVES_PROBE, MESH_DB_BURST=setting)
    spec = p0plans8b._spec8b("dbburst", case, arm, dict(point, rates=RATES), repeat, "neutral", "character", env, RATES,
                             peer, timing=(p0plans.LONG[0], burst_measure(seconds)))
    spec.update(burst_rows=BURST_ROWS, stop_after_drain=True)
    return spec


def _at():
    return p0plans.LONG[0] + BURST_AFTER_S


def _calibration():
    """2 pairs of 25 real bots on B entering at once (stagger 0) and a 25 burst, order alternating by pair."""
    specs = []
    for pair in range(1, CALIBRATION_PAIRS + 1):
        for arm in ("bots", "burst") if pair % 2 else ("burst", "bots"):
            point = {"N": CALIBRATION_N}
            if arm == "burst":
                specs.append(_burst_spec("calibration", "burst", point, pair, f"{CALIBRATION_N}@{_at()}"))
                continue
            crowd = dict(p0plans8b.bots(CALIBRATION_N, "stand", "centre"), stagger=0)
            specs.append(p0plans8b._spec8b("dbburst", "calibration", "bots", dict(point, rates=RATES), pair, "neutral",
                                           "character", {}, RATES, p0plans8b._peer(bot_part=crowd),
                                           timing=(p0plans8b.bot_warmup(CALIBRATION_N, 0), p0plans.LONG[1])))
    return specs


def dbburst_plan(parts=DB_PARTS):
    """c1 dbburst (family dbburst, game neutral; decision 4) for the chosen parts, in part order: burst N {50, 100,
    200} at once x 3 and sustained R {5, 10, 15, 20}/s for 40 s x 2 as one block (arms rotate by repeat, sustained
    sits out repeat 3), then calibration's 2 pairs (25 bots on B at once vs a 25 burst). Every burst run: coarse C, no
    load, MESH_DB_SAVES_PROBE=1, the burst at warm-up + 10 s, burst_rows 800, stop_after_drain. 21 runs. Refuses an
    unknown part."""
    chosen = _parts(parts, DB_PARTS, "dbburst")
    arms = []
    if "burst" in chosen:
        arms.append((3, BURST_N, lambda n, repeat: _burst_spec("burst", "at-once", {"N": n}, repeat,
                                                              f"{n}@{_at()}")))
    if "sustained" in chosen:
        arms.append((2, SUSTAINED_R, lambda r, repeat: _burst_spec(
            "sustained", "rate", {"R": r, "seconds": SUSTAINED_S}, repeat, f"{r}/s@{_at()}+{SUSTAINED_S}",
            SUSTAINED_S)))
    specs = p0plans._blocked(arms, 3) if arms else []
    return p0plans._numbered(specs + (_calibration() if "calibration" in chosen else []))


def _monsters_all(spec):
    """The spec with the scene's every monster area (MESH_MONSTER_AREAS=all) in place of _spec0b's no monsters."""
    spec["env"].pop("MESH_NO_MONSTERS", None)
    return spec


def _worker_spec(case, arm, load, repeat):
    """One workerab run at load players, all connected character phantoms on C in WORKER_AREA (MESH_PHANTOM_FIGHT
    0.5), the same in both arms: coarse (C alone owns every monster) or halves (C and E, MESH_MONSTER_CELL=E: E owns
    every monster and the players stay on C, the worker model)."""
    halves = arm == "halves"
    cells = p0plans0b.HALVES if halves else ("C",)
    env = dict(WORKER_COMMON, MESH_PHANTOM=f"virtual:{load}:character:C", MESH_PHANTOM_AREA=WORKER_AREA,
               MESH_PHANTOM_FIGHT=f"{WORKER_FIGHT:g}", **({"MESH_MONSTER_CELL": MONSTER_CELL} if halves else {}))
    point = {"players": load, "phantoms": load, "rates": RATES}
    spec = p0plans8b._spec8b("workerab", case, arm, point, repeat, "fantasy", "character", env, RATES,
                             layout="halves" if halves else "coarse", started=cells)
    return _monsters_all(spec)


def workerab_plan(fallback=False, repeats=3):
    """c2 workerab (family workerab, game fantasy; decision 9) at 30/0.02 with every scene monster, MESH_FIGHT_GHOSTS=1
    and MESH_FORWARD_TIMING=1 in both arms: first one coarse probe per load (case probe, ascending), then arms coarse
    and halves (+ MESH_MONSTER_CELL=E) at loads {100, 200} ({50, 100} with fallback, when coarse 200 saturates) x 3,
    arms rotating by repeat. 14 runs."""
    loads = WORKER_FALLBACK if fallback else WORKER_LOADS
    probes = [_worker_spec("probe", "coarse", load, 1) for load in loads]
    main = p0plans._blocked([(repeats, loads, lambda load, repeat, arm=arm: _worker_spec("ab", arm, load, repeat))
                             for arm in ("coarse", "halves")], repeats)
    return p0plans._numbered(probes + main)


def _spaces_spec(s, layout, players, repeat):
    """One spaces run: S observers (1 per space: ServerMeshSpaces gives an entity the space of its spawn ordinal mod S,
    observers and load counted apart), standing character phantoms crowded where the spaces sit (stack: a 30 m disc at
    the centre, inside the 80 m range of each space's 5 content objects on a 4 m ring; grid: the 600 m ground the
    150 m grid covers, each entity joining its nearest centre), and players load players there with fighting off."""
    stack = layout == "stack"
    env = {"MESH_SPACES": f"{s}:{layout}", "MESH_PHANTOM": f"virtual:{s}:character:C", "MESH_PHANTOM_STAND": "1",
           "MESH_PHANTOM_AREA": STACK_DISC if stack else GRID_RECT}
    if players:
        env.update(MESH_LOAD_FIGHT="0")
    point = {"S": s, "layout": layout, "observers": s, "players": players, "rates": RATES}
    spec = p0plans8b._spec8b("spaces", layout, "100" if players else "per-space", point, repeat, "neutral", "character",
                             env, RATES, plan={"C": players} if players else None,
                             area=(STACK_AREA if stack else GRID_AREA) if players else None)
    spec["model"] = "spaces"
    return spec


def spaces_plan(repeats=2):
    """c3 spaces (family spaces, game neutral, model spaces; decision 11) on coarse C at 30/0.02, no monsters, no
    fighting: stack S {1, 4, 16, 64}, each with 1 observer per space alone (arm per-space) or with 100 load players
    split evenly over the spaces (arm 100), then grid S 16 with 100, x 2 each, arms rotating by repeat. Observers are
    standing character phantoms (MESH_PHANTOM_STAND=1) in MESH_PHANTOM_AREA; load players stand in MESH_LOAD_AREA.
    18 runs."""
    stack = [(repeats, SPACE_COUNTS, lambda s, repeat: _spaces_spec(s, "stack", 0, repeat)),
             (repeats, SPACE_COUNTS, lambda s, repeat: _spaces_spec(s, "stack", SPACES_LOAD, repeat))]
    grid = [(repeats, (GRID_S,), lambda s, repeat: _spaces_spec(s, "grid", SPACES_LOAD, repeat))]
    return p0plans._numbered(p0plans._blocked(stack, repeats) + p0plans._blocked(grid, repeats))


def loss_plan(repeats=3):
    """c4 packets --loss (family packets, case loss; decisions 12, 13): b7's cap-sink point (p0plans8b.cap_spec at 1,020
    B, 100 sink ship phantoms on B, 400 AI ships, V 500, scale 0.5, 30/0.02) with MESH_NET_STATS=1, at loss 0 (no
    impairment), loss1 and loss5 (peer["impair"]: clumsy on B), x 3 with the arms' order rotated by repeat. 9 runs."""
    def arm(name, loss, profile):
        def make(size, repeat):
            return p0plans8b.cap_spec("sink", size, repeat, "loss", name, NET_STATS, {"loss_pct": loss},
                                      {"impair": profile})
        return repeats, (p0plans0b.PACKET_BYTES[0],), make
    return p0plans._blocked([arm(*entry) for entry in LOSS_ARMS], repeats)
