"""The dynamic-cell soak's load schedule and its bots' and processes' readers (regress_soak.py, DESIGN 0.91).

BurnPlan moves the switchable burn file (MESH_PROBE_BURN_MS=@<file>, a comma list `C:14,E:7`) from cell to cell so the
automatic policy keeps splitting and merging: one episode at a time, each over cells whose families are whole (one
cell), in four phases: heat (the targets at HOT_MS until each split; a `deep` episode then heats the new child too,
its parent warm, until the child splits again), warm (every member of the targets' families at WARM_MS for hold_s: not
hot, and the pair too warm to merge), cool (no burn until the families are whole again) and the next episode. Phases
that do not end in time are cut short and counted. step() is called with the store's families on every tick and gives
the burn for the file. The bots' lines carry no stamps, so regress_soak stamps each line when it first reads it; the
readers here take those stamps. Nothing here touches a file or a process (python -m unittest
Tools/ServerMeshing/test_rigsoakload.py).
"""
import collections
import re

import rigprocs
import rigsplit
import rigsplitcrash
import rigtools

HOT_MS, WARM_MS = 14, 7     # util about 0.9 and about 0.46 at the 60 fps budget (regress_auto's)
Episode = collections.namedtuple("Episode", "kind targets")
# C carries the synthetic players and two bots, E and N a bot each, so every kind of cut moves someone.
EPISODES = (Episode("single", ("C",)), Episode("pair", ("E", "N")), Episode("deep", ("C",)), Episode("single", ("NE",)),
            Episode("single", ("E",)), Episode("deep", ("E",)), Episode("pair", ("C", "NE")), Episode("single", ("N",)))


class BurnPlan:
    """The soak's burn schedule (see the module docstring). `events` collects (time, text) for the run's log."""

    def __init__(self, episodes=EPISODES, hold_s=150.0, heat_s=300.0, cool_s=360.0):
        self.episodes, self.hold_s, self.heat_s, self.cool_s = tuple(episodes), hold_s, heat_s, cool_s
        self.index, self.episode, self.phase, self.since, self.stage = 0, None, "pick", 0.0, 0
        self.draining, self.started, self.completed, self.timed_out = False, 0, 0, 0
        self._late = False
        self.events = []

    def drain(self, now):
        """No more episodes: cool everything until the run ends."""
        self.draining, self.episode, self.phase, self.since = True, None, "cool", now
        self.events.append((now, "drain: burn off"))

    def _goal(self):
        return 3 if self.episode.kind == "deep" and self.stage == 2 else 2

    def _heat(self, fams):
        """The targets hot; a target whose family already split (a pair's first) warm, so it is not split again."""
        burns = {}
        for base in self.episode.targets:
            members = fams.get(base, [base])
            if len(members) >= self._goal():
                burns.update({m: WARM_MS for m in members})
            elif self.episode.kind == "deep" and self.stage == 2 and len(members) > 1:
                burns[base] = WARM_MS
                burns[members[1]] = HOT_MS
            else:
                burns[base] = HOT_MS
        return burns

    def _members(self, fams):
        return {m: WARM_MS for base in self.episode.targets for m in fams.get(base, [base])}

    def _pick(self, now, fams):
        for _ in range(len(self.episodes)):
            ep = self.episodes[self.index % len(self.episodes)]
            self.index += 1
            if all(len(fams.get(t, [t])) == 1 for t in ep.targets):
                self.episode, self.phase, self.since, self.stage, self._late = ep, "heat", now, 1, False
                self.started += 1
                self.events.append((now, f"episode {self.started} {ep.kind} {'+'.join(ep.targets)}: heat"))
                return self._heat(fams)
        self.episode, self.phase, self.since = None, "cool", now
        self.events.append((now, "no whole family to heat: cooling everything"))
        return {}

    def step(self, now, fams):
        """The burns ({cell: ms}) for this tick, given the store's families ({base: [members]}, rigsoak.families)."""
        if self.phase == "pick":
            return {} if self.draining else self._pick(now, fams)
        if self.phase == "heat":
            sizes = [len(fams.get(t, [t])) for t in self.episode.targets]
            if all(n >= self._goal() for n in sizes):
                if self.episode.kind == "deep" and self.stage == 1:
                    self.stage, self.since = 2, now
                    self.events.append((now, f"episode {self.started}: {self.episode.targets[0]} split, heating its child"))
                    return self._heat(fams)
                self.phase, self.since = "warm", now
                self.events.append((now, f"episode {self.started}: split, warm for {self.hold_s:g} s"))
                return self._members(fams)
            if now - self.since > self.heat_s:
                self.timed_out += 1
                self._late = True
                self.phase, self.since = "cool", now
                self.events.append((now, f"episode {self.started}: no split within {self.heat_s:g} s (sizes {sizes}); cooling"))
                return {}
            return self._heat(fams)
        if self.phase == "warm":
            if now - self.since >= self.hold_s:
                self.phase, self.since = "cool", now
                self.events.append((now, f"episode {self.started}: cool"))
                return {}
            return self._members(fams)
        if self.draining:
            return {}
        bases = self.episode.targets if self.episode else tuple(fams)
        whole = all(len(fams.get(b, [b])) == 1 for b in bases)
        if whole or now - self.since > self.cool_s:
            if self.episode is not None and not self._late:
                self.completed += 1 if whole else 0
            if not whole:
                split = {b: fams.get(b) for b in bases if len(fams.get(b, [b])) > 1}
                self.timed_out += 1
                self.events.append((now, f"cool: not whole within {self.cool_s:g} s ({split})"))
            self.phase, self.since = "pick", now
        return {}


# ------------------------------------------------------------------ the bots
PATROL_LEG = re.compile(r"Patrol leg (\d+) to x=(-?[\d.]+): (reached|missed) at")
SESSION_LOST = rigsplitcrash.SESSION_LOST
RELOGIN_RETRY = re.compile(r"Re-login \d+: (.*); trying again in")
RELOGIN_GAVE_UP = re.compile(r"Re-login \d+ gave up after")


def bot_lines_summary(lines):
    """{migrations, legs, missed, losses [why], retries, gave_up, result} of a bot's lines (MIGRATION, Patrol leg, Session
    lost, a re-login's retries and its giving up, RESULT)."""
    legs = [m.group(3) for line in lines for m in [PATROL_LEG.search(line)] if m]
    result = next((line[line.find("RESULT"):].strip() for line in lines if "RESULT " in line), "")
    return {"migrations": len(rigsplit.parse_migrations(lines)), "legs": len(legs), "missed": legs.count("missed"),
            "losses": [m.group(1) for line in lines for m in [SESSION_LOST.search(line)] if m],
            "retries": sum(1 for line in lines if RELOGIN_RETRY.search(line)),
            "gave_up": sum(1 for line in lines if RELOGIN_GAVE_UP.search(line)), "result": result}


def unexplained_relogins(losses, kills, window_s):
    """The session losses [(t, bot, why)] with no child kill in the window_s before them (kills: [t])."""
    return [(t, bot, why) for t, bot, why in losses if not any(0 <= t - k <= window_s for k in kills)]


# ------------------------------------------------------------------ processes and memory
CHANNEL = re.compile(r"-channelId (\S+)")
BOT_USER = re.compile(r"-meshUser (\S+)")
MEM_STATS = re.compile(r"Mesh stats: .*\bmem (\d+) MB")


def role_of(command_line):
    """'central', 'spawner', 'cell:<id>', 'bot:<user>' or None for a rig process's command line."""
    text = command_line or ""
    if rigprocs.command_line_has_flag(text, rigprocs.CENTRAL_FLAG):
        return "central"
    if rigprocs.command_line_has_flag(text, rigprocs.SPAWNER_FLAG):
        return "spawner"
    m = CHANNEL.search(text)
    if m:
        return f"cell:{m.group(1)}"
    m = BOT_USER.search(text)
    if m and "-meshAutoClient" in text:
        return f"bot:{m.group(1)}"
    return None


def parse_process_rows(text):
    """[(pid, role, private MB, working set MB)] of the sampler's rows ('pid<TAB>private bytes<TAB>working set<TAB>
    command line'); rows of no rig role or that do not parse are left out."""
    out = []
    for row in (text or "").splitlines():
        parts = row.split("\t", 3)
        if len(parts) != 4 or not parts[0].strip().isdigit():
            continue
        role = role_of(parts[3])
        try:
            private, ws = int(parts[1]), int(parts[2])
        except ValueError:
            continue
        if role:
            out.append((int(parts[0]), role, private / 1048576.0, ws / 1048576.0))
    return out


def process_series(samples):
    """{(role, pid): [(t, private MB)]} of [(t, [(pid, role, private, ws)])] samples."""
    out = {}
    for t, rows in samples:
        for pid, role, private, _ in rows:
            out.setdefault((role, pid), []).append((t, private))
    return out


def stats_memory(cell, info_lines):
    """[[(epoch seconds, MB)] per process lifetime] of a cell's Mesh stats 'mem' figures (a child's lifetimes start at
    its 'Own cell from layout' line, rigsplit.lifetimes)."""
    out = []
    for life in rigsplit.lifetimes(info_lines, cell if "." in cell else None):
        series = []
        for line in life:
            m = MEM_STATS.search(line)
            at = rigtools.stamp_datetime(line) if m else None
            if at is not None:
                series.append((at.timestamp(), float(m.group(1))))
        if series:
            out.append(series)
    return out
