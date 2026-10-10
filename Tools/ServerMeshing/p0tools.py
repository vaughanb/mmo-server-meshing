"""Phase 0 instrument helpers (steps 2 to 7): parsers for the lines the map servers log, the toggles that switch them
on, the Phase 0 keys not built yet, and a mirror of the C# MESH_RATES grammar. Pure: nothing here reads files or the
environment.

    p0tools.parse_work_ticks(line)       # the "Work ticks" line after each Work window line, as a dict, else None
    p0tools.parse_work_bins(line)        # the "Work bins" line after it (MESH_WORK_BINS=1): five 0.05 ms histograms
    p0tools.merge_bins(hists)            # histograms summed exactly
    p0tools.percentile_us(hist, 950000)  # p95 in integer µs, ranked as MeshWorkHistogram ranks it
    p0tools.pair_work_lines(lines)       # (Work window/ticks/bins triples, unpaired-line problems) of a cell's log
    p0tools.bins_problems(triple)        # where a Work bins line contradicts its Work ticks line
    p0tools.parse_rates(line)            # the "Rates: fps ..." line ServerMeshRates logs once it applies
    p0tools.parse_rates_cell(line)       # its "Rates: cell X uncapped (nominal N fps)" line
    p0tools.parse_threads_window(line)   # the "Threads window" line ServerMeshThreadCpu logs every 5 s
    p0tools.parse_puppets_window(line)   # the "Puppets window" line a map server logs with Mesh stats when puppets
                                         # are kinematic (MESH_PUPPET_MOTION=kinematic)
    p0tools.parse_band(line)             # and parse_load_motion, parse_kinematic_walk, parse_server_animation,
                                         # parse_aoi_timing, parse_tick_burn, parse_spawned: one-off marker lines
    p0tools.rates_setting("u30/0.02")    # (-1, 30, 0.02, None), or (None, None, None, error)

ENV_REGISTRY holds the implemented Phase 0 keys; PENDING_KEYS the build sheet's §0.16 keys that later steps build, and
DROPPED_KEYS the ones it no longer needs. Every parser works on a bare message or a whole log line; "end" is the line's
stamp, or None when it has none. The step 5 lines (Send, Phantom, Sink and Bot net windows) and the MESH_PHANTOM,
MESH_PHANTOM_AREA and MESH_METER_SENDS grammars are p0phantom's; the step 6 lines (Sender window, CE-12 check) and the
MESH_SENDER and MESH_SENDER_TIERS grammars are p0sender's; the step 7 lines (Ships, Feeder, Sync load, Bot ships), the
ship grammars and the twelve step 7 key meanings are p0ships'; step 8a's lines and three keys are p0stage0b's; step 9's
lines and seven keys are p0cells0c's.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import re

import p0cells0c
import p0ships
import p0stage0b
import rigtools

# The Phase 0 toggles (6 from step 2, 4 from step 3, 10 from step 5, 10 from step 6, 12 from step 7, 3 from step 8a,
# 7 from step 9), each also in regress.ENV_KEYS. Step 7's meanings live in p0ships, step 8a's in p0stage0b, step 9's in
# p0cells0c (the ~500-line rule).
ENV_REGISTRY = {
    "MESH_WORK_BINS": "1: every Work ticks line is followed by a Work bins line listing the non-empty 0.05 ms bins",
    "MESH_RATES": "<fps>/<step>, u/<step> or u<fps>/<step>: every cell's frame cap (u: none, budgeted at <fps> or "
                  "the kit's cap) and physics step in seconds",
    "MESH_UNCAPPED_CELLS": "comma list of cell ids that run without a frame cap, budgeted at the MESH_RATES fps or "
                           "the kit's cap",
    "MESH_AOI_TIMING": "1: the interest manager is replaced by a timed copy that tags AOI passes and times their "
                       "late completion",
    "MESH_THREAD_CPU": "1: a Threads window line every 5 s with the CPU of each thread class (Windows only)",
    "MESH_PROBE_BURN_TICK_MS": "[cell:]ms: busy-wait this long in every kit tick, on one cell or on every cell; "
                               "refused beside MESH_PROBE_BURN_MS",
    "MESH_PUPPET_MOTION": "steered or kinematic: how player and monster puppets follow their owners' snapshots "
                          "(kinematic adds the Puppets window line); unset keeps the inspector's value, steered by "
                          "default",
    "MESH_RIG_SERVER_ANIMATION": "0 or 1: whether map servers evaluate character animation (the kit's "
                                 "updateAnimationAtServer, 1 in the demo)",
    "MESH_LOAD_MOTION": "steered, kinematic or inert: synthetic players walk with the brain through the kit's "
                        "movement, walk by transform with the movement component off, or stand with no brain "
                        "(default steered)",
    "MESH_LOAD_FIGHT": "0 or 1: whether steered synthetic players look for monsters in reach and attack them "
                       "(default 1)",
    "MESH_METER_SENDS": "off, count or time: meter server sends into a Send window line (time also times them)",
    "MESH_PHANTOM": "<virtual|sink>:<P>:<character|eye|ship>:<cell[,cell..]|all>: P phantom clients on each listed cell",
    "MESH_PHANTOM_VIEW": "V movers per phantom (character >= 1; eye 0 follows existing movers); needed unless AREA",
    "MESH_PHANTOM_ARRIVAL_PER_S": "phantom and filler binds per second, above 0 (default 50)",
    "MESH_PHANTOM_UPLOAD_HZ": "1 to 60: uploads per phantom per second (default 20)",
    "MESH_PHANTOM_PAYLOAD": "transform or input: what a character phantom uploads (default transform; input not with eyes)",
    "MESH_PHANTOM_STEPS": "<P1,..,Pn>@<s>: step the bound eye phantoms through these counts, s seconds each",
    "MESH_PHANTOM_AREA": "<cx>,<cz>,<w>,<h> or disc:<cx>,<cz>,<r>: character phantoms walk here, no groups or filler",
    "MESH_PHANTOM_STAND": "share 0 to 1 of the character phantoms, chosen by seed, that stand (default 0)",
    "MESH_PHANTOM_FIGHT": "share 0 to 1 of the character phantoms, chosen by seed, that fight on the server (default 0)",
    "MESH_SENDER": "off, measure or on: the state sender writes entity state after the kit and counts it (measure), "
                   "or sends it in place of the kit's loop (on), with a Sender window line (default off)",
    "MESH_SENDER_SOURCES": "kit, direct or kit,direct: what the sender sends, wrapped kit handlers and/or registered "
                           "direct sources (default both)",
    "MESH_SENDER_TIERS": "near/mid/far[/owner] Hz, each 0, 1, 2, 5, 10 or 20: the distance tiers (owner default 20; "
                         "unset 20/20/20/20)",
    "MESH_SENDER_R0": "metres above 0: the tier radius (near within half, mid within it; default the visible range)",
    "MESH_SENDER_COMPACT": "off or far: whether far pairs get compact records from sources that have them (default off)",
    "MESH_SENDER_SPREAD": "0 or 1: defer every second source's routine unreliable records to the next frame (default 0)",
    "MESH_SENDER_XZ": "0 or 1: tier distances on the X/Z plane instead of in 3D (default 0)",
    "MESH_SENDER_CLASSIFY_BUILDS": "1 to 1000: builds between re-classifications of a pair, spread evenly (default 4)",
    "MESH_SENDER_PACKET_BYTES": "1020, 510 or 255: the sender's packet cap in bytes (default 1020)",
    "MESH_CE12_CHECK": "1: each serving cell spawns a hidden character, classifies its kit handler's one-shot flags "
                       "and logs one CE-12 check line",
}
ENV_REGISTRY.update(p0ships.STEP7_REGISTRY)
ENV_REGISTRY.update(p0stage0b.STEP8_REGISTRY)
ENV_REGISTRY.update(p0cells0c.STEP9_REGISTRY)

# The build sheet's other §0.16 keys: nothing reads them yet, so a plan that sets one is refused (rigrun.check_env).
PENDING_KEYS = {"MESH_SHIP_JUMP_SECONDS": "stage 0c, step 9"}
DROPPED_KEYS = {
    "MESH_LOAD_COLLIDE": "dropped in step 3 (decision 4): the kit already ignores every character pair",
    "MESH_PHANTOM_REGISTER": "dropped in step 5 (decision 3): phantoms anchor through their owned object, and "
                             "registering sends central traffic and makes them handoff candidates whose handoff writes "
                             "a fake row",
    "MESH_SENDER_RESIDUAL_PROBE": "dropped in step 6 (decision 13): the Work ticks move part already times the kit's "
                                  "movement loop under MESH_AOI_TIMING=1",
}

UNCAPPED = -1   # the frame rate of a setting, or a Rates line, without a cap
MIN_FPS, MAX_FPS = 1, 1000
MAX_STEP_SECONDS = 1.0

NUM = r"\d+(?:\.\d+)?"

WORK_TICKS = re.compile(
    rf"Work ticks (?P<seconds>{NUM}) s: ticks (?P<ticks>\d+) expected (?P<expected>\d+) dropped (?P<dropped>\d+) "
    rf"cap (?P<cap>\d+) unclosed (?P<unclosed>\d+) "
    rf"\| frames (?P<frames>\d+) t0 (?P<t0>\d+) t1 (?P<t1>\d+) t2 (?P<t2>\d+) "
    rf"\| tick frames mean (?P<tick_frame_mean>{NUM}) ms p95 (?P<tick_frame_p95>{NUM}) ms "
    rf"p99 (?P<tick_frame_p99>{NUM}) ms max (?P<tick_frame_max>{NUM}) ms "
    rf"\| nonaoi p95 (?P<nonaoi_p95>{NUM}) ms p99 (?P<nonaoi_p99>{NUM}) ms "
    rf"\| aoi passes (?P<aoi_passes>\d+) frames (?P<aoi_frames>\d+) mean (?P<aoi_frame_mean>{NUM}) ms "
    rf"max (?P<aoi_frame_max>{NUM}) ms "
    rf"\| other mean (?P<other_mean>{NUM}) ms p95 (?P<other_p95>{NUM}) ms "
    rf"\| kit mean (?P<kit_mean>{NUM}) ms p95 (?P<kit_p95>{NUM}) ms p99 (?P<kit_p99>{NUM}) ms max (?P<kit_max>{NUM}) ms "
    rf"\| parts (?P<parts>\d+) sync (?P<sync_mean>{NUM}) ms aoi (?P<aoi_schedule_mean>{NUM}) ms "
    rf"move (?P<move_mean>{NUM}) ms "
    rf"\| late (?P<late_calls>\d+) mean (?P<late_mean>{NUM}) ms max (?P<late_max>{NUM}) ms "
    rf"(?P<late_ms_per_s>{NUM}) ms/s "
    rf"\| send (?P<send_samples>\d+) mean (?P<send_mean>{NUM}) ms p95 (?P<send_p95>{NUM}) ms p99 (?P<send_p99>{NUM}) ms "
    rf"\| physics (?P<physics_per_s>{NUM})/s mean (?P<physics_mean>{NUM}) max (?P<physics_max>\d+) f2 (?P<physics_f2>\d+) "
    rf"\| work (?P<work_ms_per_s>{NUM}) ms/s busy (?P<busy_percent>{NUM})% "
    rf"\| budget (?P<budget>{NUM}) ms \| gc0 (?P<gc0>\d+) \| outside (?P<outside>\d+)")
WORK_TICKS_INTS = {"ticks", "expected", "dropped", "cap", "unclosed", "frames", "t0", "t1", "t2", "aoi_passes",
                   "aoi_frames", "parts", "late_calls", "send_samples", "physics_max", "physics_f2", "gc0", "outside"}

RATES = re.compile(
    rf"Rates: fps (?P<fps>u\d*|\d+) \((?P<source>MESH_RATES(?: unset)?); kit (?P<kit>u\d*|\d+)\) vSync (?P<vsync>\d+) "
    rf"\| tick (?P<tick_hz>\d+) Hz \(updateFps unchanged\) "
    rf"\| physics (?P<step>{NUM}) s \(project (?P<project_step>{NUM})\) \| maxDeltaTime (?P<max_delta>{NUM}) "
    rf"\| probe budget (?P<budget>{NUM}) ms \| uncapped cells (?P<cells>\S+)")
RATES_CELL = re.compile(r"Rates: cell (?P<cell>\S+) uncapped \(nominal (?P<nominal>\d+) fps\)")

THREADS_WINDOW = re.compile(
    rf"Threads window (?P<seconds>{NUM}) s: main (?P<main>{NUM}) litenetlib (?P<litenetlib>{NUM}) jobs (?P<jobs>{NUM}) "
    rf"unnamed (?P<unnamed>{NUM}) other (?P<other>{NUM}) total (?P<total>{NUM}) process (?P<process>{NUM}) ms/s "
    rf"\| threads (?P<threads>\d+) sampler (?P<sampler_ms_per_s>{NUM}) ms/s "
    rf"clock (?:cycles (?P<ghz>{NUM}) GHz|(?P<ticks>ticks)) "
    rf"names pre-start=(?P<names_pre_start>present|absent|unavailable)")
THREADS_WINDOW_FLOATS = ("seconds", "main", "litenetlib", "jobs", "unnamed", "other", "total", "process",
                         "sampler_ms_per_s")

PUPPETS_WINDOW = re.compile(
    rf"Puppets window (?P<seconds>{NUM}) s: kinematic (?P<kinematic>\d+) steered (?P<steered>\d+) "
    rf"\| fallback mounted (?P<fallback_mounted>\d+) summoned (?P<fallback_summoned>\d+) "
    rf"handler (?P<fallback_handler>\d+) grid (?P<fallback_grid>\d+) install (?P<fallback_install>\d+) "
    rf"\| snaps (?P<snaps>\d+) "
    rf"\| observed ticks (?P<observed_ticks_per_s>{NUM})/s unobserved poses (?P<unobserved_poses_per_s>{NUM})/s "
    rf"\| steer kinematic (?P<steer_kinematic_samples>\d+) mean (?P<steer_kinematic_mean>{NUM}) m "
    rf"p95 (?P<steer_kinematic_p95>{NUM}) m max (?P<steer_kinematic_max>{NUM}) m "
    rf"\| steer steered (?P<steer_steered_samples>\d+) mean (?P<steer_steered_mean>{NUM}) m "
    rf"p95 (?P<steer_steered_p95>{NUM}) m max (?P<steer_steered_max>{NUM}) m")
PUPPETS_WINDOW_INTS = {"kinematic", "steered", "fallback_mounted", "fallback_summoned", "fallback_handler",
                       "fallback_grid", "fallback_install", "snaps", "steer_kinematic_samples", "steer_steered_samples"}

BIN_US = 50            # the Work bins bin width in microseconds (MeshWorkMeter's TickBinMs, 0.05 ms)
BIN_COUNT = 4000       # bins below the overflow: 200 ms (MeshWorkMeter's TickBinCount)
HISTOGRAMS = ("tick", "nonaoi", "other", "kit", "send")   # the Work bins groups, in line order
PPM = 1_000_000        # a percentile rank's denominator: 950000 is p95

# "Work bins 5.00 s: tick 50:3 51:7 over:0 | nonaoi over:0 | other ... | kit ... | send ..." (MeshWorkMeter.BinsLine):
# every group in that order, each non-empty bin as index:count, then the overflow count, and nothing after.
WORK_BINS = re.compile(
    rf"Work bins (?P<seconds>{NUM}) s: "
    + r" \| ".join(rf"{name}(?P<{name}>(?: \d+:\d+)*) over:(?P<{name}_over>\d+)" for name in HISTOGRAMS) + "$")

BAND = re.compile(rf"\bMESH_BAND=(?P<setting>[^:]*): ghost band and visible range set to (?P<band>{NUM}) m")
LOAD_MOTION = re.compile(r"Load generator: motion (?P<motion>steered|kinematic|inert) \(MESH_LOAD_MOTION\), "
                         r"fight (?P<fight>on|off) \(MESH_LOAD_FIGHT\)")
KINEMATIC_WALK = re.compile(
    rf"Kinematic walk: (?P<walking>\d+) synthetic players walk by transform at (?P<speed_rate>{NUM}) x move speed; "
    rf"(?P<steered>\d+) stay steered \(no grounded kit movement within (?P<timeout_s>{NUM}) s\)")
SERVER_ANIMATION = re.compile(r"Server animation: updateAnimationAtServer (?P<value>[01]) "
                              r"\(MESH_RIG_SERVER_ANIMATION; kit (?P<kit>[01])\)")
AOI_TIMING = re.compile(rf"AOI timing: installed on cell (?P<cell>\S+) \((?P<manager>.*?)\): cellSize {NUM} "
                        rf".* visibleRange (?P<visible_range>{NUM}) \|")
TICK_BURN = re.compile(rf"Tick burn: (?P<ms>{NUM}) ms per tick on (?P<where>every cell|\S+)$")
SPAWNED = re.compile(
    r"Load generator: spawned (?P<spawned>\d+) synthetic players on cell (?P<cell>\S+) "
    r"over x \[(?P<x_min>-?\d+), (?P<x_max>-?\d+)\] z \[(?P<z_min>-?\d+), (?P<z_max>-?\d+)\] "
    r"\(seed (?P<seed>-?\d+), planned (?P<planned>\d+)\)")

RATES_SHAPES = "<fps>/<step>, u/<step> or u<fps>/<step>"
FRAME_COUNT = re.compile(r"[0-9]+")
# What C#'s float.TryParse(NumberStyles.Float, invariant culture) takes, less NaN and the infinities, which it refuses too.
STEP_NUMBER = re.compile(r"[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?")


def _numbers(match, ints):
    return {name: int(text) if name in ints else float(text) for name, text in match.groupdict().items()}


def parse_work_ticks(line):
    """The Work ticks line's fields (counts as int, milliseconds, rates and percentages as float), else None."""
    m = WORK_TICKS.search(line)
    if not m:
        return None
    fields = _numbers(m, WORK_TICKS_INTS)
    fields["end"] = rigtools.stamp_datetime(line)
    return fields


def _frame_rate(token):
    """A Rates line's frame-rate token: (rate, nominal); "u30" is (-1, 30), a bare "u" (-1, 0), "30" (30, 0)."""
    if token.startswith("u"):
        return UNCAPPED, int(token[1:]) if len(token) > 1 else 0
    return int(token), 0


def parse_rates(line):
    """The Rates line ServerMeshRates logs once it applies, else None.

    fps is the frame cap, or -1 without one; nominal is the frame rate an uncapped process is budgeted at (0 for none
    or when capped); from_env says whether MESH_RATES set the rates; kit is the cap the kit had set (-1 for none).
    """
    m = RATES.search(line)
    if not m:
        return None
    fps, nominal = _frame_rate(m["fps"])
    kit, _ = _frame_rate(m["kit"])
    cells = m["cells"]
    return {"end": rigtools.stamp_datetime(line), "fps": fps, "nominal": nominal, "from_env": m["source"] == "MESH_RATES",
            "kit": kit, "vsync": int(m["vsync"]), "tick_hz": int(m["tick_hz"]), "step": float(m["step"]),
            "project_step": float(m["project_step"]), "max_delta": float(m["max_delta"]), "budget": float(m["budget"]),
            "uncapped_cells": [] if cells == "-" else cells.split(",")}


def parse_rates_cell(line):
    """The line a cell listed in MESH_UNCAPPED_CELLS logs when it drops its cap, else None."""
    m = RATES_CELL.search(line)
    if not m:
        return None
    return {"end": rigtools.stamp_datetime(line), "cell": m["cell"], "nominal": int(m["nominal"])}


def parse_threads_window(line):
    """The Threads window line, else None: the window's seconds and the CPU milliseconds per second of each thread
    class, the total, the process and the sampler thread itself (float); threads (int); clock, "cycles" when CPU was
    counted in cycles converted at ghz (float), or "ticks" (ghz None) when it fell back to 15.625 ms scheduler ticks;
    and whether a thread named before its start keeps its name ("present" or "absent"; "unavailable" when the probe
    could not tell)."""
    m = THREADS_WINDOW.search(line)
    if not m:
        return None
    fields = {name: float(m[name]) for name in THREADS_WINDOW_FLOATS}
    fields.update({"end": rigtools.stamp_datetime(line), "threads": int(m["threads"]),
                   "clock": "ticks" if m["ticks"] else "cycles", "ghz": None if m["ticks"] else float(m["ghz"]),
                   "names_pre_start": m["names_pre_start"]})
    return fields


def parse_puppets_window(line):
    """The Puppets window line, else None: the window's seconds; the kinematic and steered puppet counts, the fallbacks
    to steering by reason (mounted, summoned, handler, grid, install) and the snaps (int); the observed ticks and
    unobserved pose applications per second; and the steer error (the distance from the pose a moving puppet shows, a
    kinematic puppet's pose on its track, to each arriving snapshot) of kinematic and steered puppets apart: the sample
    count (int) and the mean, p95 and max in metres (float)."""
    m = PUPPETS_WINDOW.search(line)
    if not m:
        return None
    fields = _numbers(m, PUPPETS_WINDOW_INTS)
    fields["end"] = rigtools.stamp_datetime(line)
    return fields


def _bins(text):
    """ " 50:3 51:7" as {50: 3, 51: 7}, else None: indices strictly increasing and below BIN_COUNT, counts at least 1."""
    bins, last = {}, -1
    for pair in text.split():
        index, count = (int(part) for part in pair.split(":"))
        if index <= last or index >= BIN_COUNT or count < 1:
            return None
        bins[index] = count
        last = index
    return bins


def parse_work_bins(line):
    """The Work bins line (MESH_WORK_BINS=1), else None: "end", "seconds", and per histogram in HISTOGRAMS
    {"bins": {index: count}, "over": n}, bin i holding values in [i, i + 1) x 50 µs. None when an index is not
    strictly increasing or reaches BIN_COUNT, a count is below 1, a group is missing, mislabelled or out of order, or
    text follows the send group."""
    m = WORK_BINS.search(line.rstrip())
    if not m:
        return None
    fields = {"end": rigtools.stamp_datetime(line), "seconds": float(m["seconds"])}
    for name in HISTOGRAMS:
        bins = _bins(m[name])
        if bins is None:
            return None
        fields[name] = {"bins": bins, "over": int(m[f"{name}_over"])}
    return fields


def merge_bins(hists):
    """The histograms summed exactly: {"bins": {index: count} in index order, "over": n} ({} and 0 for none)."""
    merged, over = {}, 0
    for hist in hists:
        for index, count in hist["bins"].items():
            merged[index] = merged.get(index, 0) + count
        over += hist["over"]
    return {"bins": dict(sorted(merged.items())), "over": over}


def percentile_us(hist, ppm, max_us=None):
    """A histogram's percentile as MeshWorkHistogram.Percentile ranks it, in integer µs: rank = max(1, (ppm x count +
    999999) // 1000000) with ppm in parts per million (950000 is p95); a rank inside bin i gives its upper edge
    (i + 1) x 50, never clamped (the C# clamp to the window's maximum cannot be reproduced for histograms whose maximum
    is not logged); a rank in the overflow gives max_us, None when the maximum is unknown (at least 200 ms); an empty
    histogram gives 0. Raises ValueError for a ppm that is not an integer from 0 to 1000000."""
    if isinstance(ppm, bool) or not isinstance(ppm, int) or not 0 <= ppm <= PPM:
        raise ValueError(f"percentile {ppm!r} ppm is not an integer from 0 to {PPM}")
    count = sum(hist["bins"].values()) + hist["over"]
    if count == 0:
        return 0
    rank = max(1, (ppm * count + PPM - 1) // PPM)
    seen = 0
    for index in sorted(hist["bins"]):
        seen += hist["bins"][index]
        if seen >= rank:
            return (index + 1) * BIN_US
    return max_us


def _clock(stamp):
    return stamp.strftime("%H:%M:%S") if stamp is not None else "(no stamp)"


def _follows(earlier, later):
    """Whether `later` is the next line of `earlier`'s window: the same seconds, stamped the same second or the next
    (the two Logging.Log calls can straddle a second boundary)."""
    if earlier["end"] is None or later["end"] is None or earlier["seconds"] != later["seconds"]:
        return False
    return 0 <= (later["end"] - earlier["end"]).total_seconds() <= 1


def _unpaired(item, kind, partner):
    return {"end": item["end"], "seconds": item["seconds"],
            "text": f"{kind} at {_clock(item['end'])} has no {partner}"}


def pair_work_lines(lines):
    """(triples, problems) from a cell's log lines in file order, reading only the Work window, Work ticks and Work bins
    lines. A Work ticks line pairs with the immediately preceding unpaired Work window line, and a Work bins line with
    the immediately preceding Work ticks line, when both print the same seconds and the later one is stamped the same
    second or the next. Triple: {"end": the Work window's stamp, "seconds", "window", "ticks", "bins": dict or None}.
    Each unpaired line is one problem {"end", "seconds", "text"}, e.g. "Work ticks at 10:00:05 has no Work window";
    a Work bins line that follows an unpaired Work ticks line is its partner, so it adds no problem of its own."""
    triples, problems = [], []
    pending_window = None   # a Work window line still waiting for its Work ticks line
    open_ticks = None       # (triple or None, Work ticks) still waiting for its Work bins line
    for line in lines:
        window = rigtools.parse_work_window(line)
        ticks = None if window else parse_work_ticks(line)
        bins = None if window or ticks else parse_work_bins(line)
        if not (window or ticks or bins):
            continue
        if ticks and pending_window and _follows(pending_window, ticks):
            triple = {"end": pending_window["end"], "seconds": pending_window["seconds"], "window": pending_window,
                      "ticks": ticks, "bins": None}
            triples.append(triple)
            pending_window, open_ticks = None, (triple, ticks)
            continue
        if bins and open_ticks and _follows(open_ticks[1], bins):
            if open_ticks[0] is not None:
                open_ticks[0]["bins"] = bins
            open_ticks = None
            continue
        if pending_window:
            problems.append(_unpaired(pending_window, "Work window", "Work ticks"))
        pending_window, open_ticks = window, None
        if ticks:
            problems.append(_unpaired(ticks, "Work ticks", "Work window"))
            open_ticks = (None, ticks)
        elif bins:
            problems.append(_unpaired(bins, "Work bins", "Work ticks"))
    if pending_window:
        problems.append(_unpaired(pending_window, "Work window", "Work ticks"))
    return triples, problems


def histogram_total(hist):
    """The number of values in a histogram, the overflow included."""
    return sum(hist["bins"].values()) + hist["over"]


def bins_problems(triple):
    """What a triple's Work bins line contradicts in its Work ticks line, as messages (empty when they agree): the tick
    total must be t1 + t2, nonaoi t1 + t2 - aoi frames, other t0, send the send samples, and kit within `unclosed` of
    ticks - unclosed. A triple without a Work bins line gives one problem saying so (call this only when
    MESH_WORK_BINS=1)."""
    at = _clock(triple["end"])
    wb, wt = triple["bins"], triple["ticks"]
    if wb is None:
        return [f"Work ticks at {at} has no Work bins line"]
    totals = {name: histogram_total(wb[name]) for name in HISTOGRAMS}
    expected = (("tick", wt["t1"] + wt["t2"], "t1 + t2"),
                ("nonaoi", wt["t1"] + wt["t2"] - wt["aoi_frames"], "t1 + t2 - aoi frames"),
                ("other", wt["t0"], "t0"),
                ("send", wt["send_samples"], "send samples"))
    problems = [f"Work bins at {at}: {name} total {totals[name]} is not {what} = {want}"
                for name, want, what in expected if totals[name] != want]
    closed = wt["ticks"] - wt["unclosed"]
    if abs(totals["kit"] - closed) > wt["unclosed"]:
        problems.append(f"Work bins at {at}: kit total {totals['kit']} is not within {wt['unclosed']} of "
                        f"ticks - unclosed = {closed}")
    return problems


def parse_band(line):
    """The line a cell logs when MESH_BAND applies, else None: "end", "band" (metres, float) and "setting" (the
    variable's text as set)."""
    m = BAND.search(line)
    if not m:
        return None
    return {"end": rigtools.stamp_datetime(line), "band": float(m["band"]), "setting": m["setting"]}


def parse_load_motion(line):
    """The load generator's motion line (logged only when MESH_LOAD_MOTION or MESH_LOAD_FIGHT is not the default),
    else None: "end", "motion" (steered, kinematic or inert) and "fight" (bool)."""
    m = LOAD_MOTION.search(line)
    if not m:
        return None
    return {"end": rigtools.stamp_datetime(line), "motion": m["motion"], "fight": m["fight"] == "on"}


def parse_kinematic_walk(line):
    """The Kinematic walk line (every walking synthetic player started or fell back), else None: "end", "walking" and
    "steered" (the players that stayed with the brain; int), "speed_rate" and "timeout_s" (float)."""
    m = KINEMATIC_WALK.search(line)
    if not m:
        return None
    return {"end": rigtools.stamp_datetime(line), "walking": int(m["walking"]), "speed_rate": float(m["speed_rate"]),
            "steered": int(m["steered"]), "timeout_s": float(m["timeout_s"])}


def parse_server_animation(line):
    """The Server animation line (MESH_RIG_SERVER_ANIMATION applied), else None: "end", "value" and "kit" (0 or 1)."""
    m = SERVER_ANIMATION.search(line)
    if not m:
        return None
    return {"end": rigtools.stamp_datetime(line), "value": int(m["value"]), "kit": int(m["kit"])}


def parse_aoi_timing(line):
    """The AOI timing line of a cell whose interest manager was replaced by the timed copy, else None: "end", "cell",
    "manager" (the object path it was installed on) and "visible_range" (metres, float)."""
    m = AOI_TIMING.search(line)
    if not m:
        return None
    return {"end": rigtools.stamp_datetime(line), "cell": m["cell"], "manager": m["manager"],
            "visible_range": float(m["visible_range"])}


def parse_tick_burn(line):
    """The Tick burn line (MESH_PROBE_BURN_TICK_MS applied), else None: "end", "ms" (float) and "cell", the burning
    cell, or None when the burn is on every cell."""
    m = TICK_BURN.search(line.rstrip())
    if not m:
        return None
    where = m["where"]
    return {"end": rigtools.stamp_datetime(line), "ms": float(m["ms"]), "cell": None if where == "every cell" else where}


def parse_spawned(line):
    """The load generator's spawn line, else None: "end", "spawned", "cell", the placement rectangle's "x_min",
    "x_max", "z_min", "z_max" (whole metres), "seed" and "planned" (int)."""
    m = SPAWNED.search(line)
    if not m:
        return None
    fields = {name: int(m[name]) for name in ("spawned", "x_min", "x_max", "z_min", "z_max", "seed", "planned")}
    fields.update({"end": rigtools.stamp_datetime(line), "cell": m["cell"]})
    return fields


def _frame_count(text):
    if not FRAME_COUNT.fullmatch(text):
        return None
    value = int(text)
    return value if MIN_FPS <= value <= MAX_FPS else None


def _setting_frame_rates(part):
    """The fps part of a MESH_RATES value as (fps, nominal), else None."""
    if part[:1] in ("u", "U"):
        rest = part[1:].strip()
        if not rest:
            return UNCAPPED, 0
        nominal = _frame_count(rest)
        return None if nominal is None else (UNCAPPED, nominal)
    fps = _frame_count(part)
    return None if fps is None else (fps, fps)


def rates_setting(text):
    """MESH_RATES read as ServerMeshRatesSetting.TryParse reads it: (fps, nominal, step, None), or
    (None, None, None, error) with the same error text naming the failing part.

    fps is the cap, or -1 for u; nominal is the rate the run is budgeted at: the cap itself, the number after u, or 0
    for a bare u (the kit's cap when it is applied); step is the physics step in seconds.
    """
    if text is None or not text.strip():
        return None, None, None, "is empty; expected " + RATES_SHAPES
    parts = text.split("/")
    if len(parts) != 2:
        return None, None, None, f"'{text.strip()}' is not {RATES_SHAPES}"
    fps_part, step_part = parts[0].strip(), parts[1].strip()
    rates = _setting_frame_rates(fps_part)
    if rates is None:
        return None, None, None, f"fps part '{fps_part}' is not an integer from 1 to 1000, u, or u and such an integer"
    if not STEP_NUMBER.fullmatch(step_part):
        return None, None, None, f"step part '{step_part}' is not a number of seconds"
    step = float(step_part)
    if not 0 < step <= MAX_STEP_SECONDS:
        return None, None, None, f"step part '{step_part}' is not above 0 and at most 1 second"
    fps, nominal = rates
    return fps, nominal, step, None
