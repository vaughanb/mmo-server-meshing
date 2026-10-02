#!/usr/bin/env python3
"""Offline re-grade of saved abload reports: the same text again from the cells' saved logs, with no editor or rig.

    python Tools/ServerMeshing/abregrade.py replay [--logs DIR] [REPORT ...]
    python Tools/ServerMeshing/abregrade.py fixture --out DIR [--logs DIR] REPORT ...

replay   For each report (with none: every abload_*.txt in DIR whose header is abload's 0.71 ab or probe shape, the
         others listed as skipped), rebuild its arguments from the text with abload's own parser, find each run's Play
         session in the cells' logs in DIR (default Logs/), run the UNCHANGED abload.ab_command / probe_command with
         two seams replaced, and compare the text it produces with the report line by line:
           rigrun.run_window   returns the recorded (since, start, end) of the next run instead of driving the editor
           regress.zone_lines  reads a cell's logs bounded to that run's session [since, until), not to end of file
         Prints '<report>: IDENTICAL, exit N; <verdict line>' or '<report>: K line(s) differ, exit N' with the first
         five differences. Exit 1 when any replayed report differs or cannot be replayed (skipped ones do not count).
fixture  Copy the reports into DIR with, per cell log file their replay reads, only the stamped lines inside their
         sessions that grading reads (Work window, Own cell from, Load generator: spawned, Neighbour zone ... linked,
         Exception); a file with no such line is not written. Every report must first replay IDENTICAL from --logs.
         Tools/ServerMeshing/fixtures/abload_chain3_20260925 holds the five chain3 reports written this way.

Sessions: the k-th session is the k-th 'Own cell from' line of cell C (a probe: its cell) after the report's start
time; since = that line - 3 s, until = the next one - 3 s (the last: + 10 min). A usable ab run's window start is on
its run line (HH:MM:SS, on the Own line's date, or the next day when it reads earlier than the Own line); a discarded
run's, and every probe run's, is estimated as the Own line + warmup - 2 s (the line lands about 2 s after wait_play
returns); end = start + measure. Work windows are 5 s apart, so the estimate selects the same windows. Each day's logs
are read with the next day's, so a report that runs over midnight replays like any other. Reports written with phase 0 flags (third line '=== phase 0: ...') are skipped: their JSONL rows
carry window_local, which a later re-grade can use exactly.
"""
import argparse
import contextlib
import datetime
import glob
import io
import os
import re
import shutil
import sys
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import abload
import regress as r
import rigrun
import rigtools

HEADER_TIME = re.compile(r"\((\d{4}-\d\d-\d\d \d\d:\d\d)\)$")
AB_HEADER = re.compile(r"=== static A/B, capped, load (\S+), band (.+?), (\d+) repeat\(s\) \(seeds (\d+)\.\.\d+\), "
                       r"warmup (\d+)s measure (\d+)s$")
PROBE_HEADER = re.compile(r"=== probe acceptance on cell (\S+), .*; warmup (\d+)s measure (\d+)s$")
PHASE0_HEADER = "=== phase 0:"
RUN = re.compile(r"--- (\d+) players, monsters (\w+), repeat (\d+) \(seed \d+\), (\w+) \(\d+/\d+\): plan \S+$")
PEAK_START = re.compile(r"^    peak .* \| (\d\d:\d\d:\d\d)(?: SATURATED \S+)?$")
PROBE_ROW = re.compile(r"^(\d+(?:\.\d+)?)\s")
VERDICT_PREFIXES = ("go/no-go", "no go/no-go", "probe acceptance")
KEEP = re.compile(r"Work window|Own cell from|Load generator: spawned|Neighbour zone \S+ linked|Exception")
SUFFIXES = ("info", "warn", "error")
SESSION_MARGIN = datetime.timedelta(seconds=3)
LAST_SESSION = datetime.timedelta(minutes=10)
OWN_LAG = 2   # seconds from wait_play returning to the cell's 'Own cell from' line
# What run_window would call to drive the rig: refused during a replay, so a seam that stops working shows up as a
# failed run in the text instead of reaching the editor, the database or the processes.
RIG_CALLS = ("ps", "db_reset", "request_play", "wait_play", "stop_play", "kill_all_kit_processes")


class NotAReport(ValueError):
    """The text is not an abload 0.71 ab or probe report."""


class SkipReport(ValueError):
    """An abload report this tool does not replay (written with phase 0 flags)."""


def read_stamped(path):
    """[(stamp, line)] of a kit log file's stamped lines, in file order; [] when the file does not exist."""
    if not os.path.exists(path):
        return []
    out = []
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            stamp = rigtools.stamp_datetime(line)
            if stamp is not None:
                out.append((stamp, line.rstrip("\n")))
    return out


class CellLogs:
    """The cells' saved kit logs in one folder: per (cell, map, day) the stamped lines of its info, warn and error
    files sorted by stamp, read once. `touched` lists every (cell, map, day) asked for."""

    def __init__(self, logs_dir):
        self.logs_dir = logs_dir
        self._lines = {}

    @property
    def touched(self):
        return sorted(self._lines)

    def path(self, cell, suffix, day, map_id):
        """The path of one of the cell's log files in this folder (regress.zone_log's file name)."""
        return os.path.join(self.logs_dir, os.path.basename(r.zone_log(cell, suffix, day, map_id)))

    def lines(self, cell, map_id, day):
        """[(stamp, line)] of the cell's stamped lines on that day, all three files merged in stamp order."""
        key = (cell, map_id, day)
        if key not in self._lines:
            out = []
            for suffix in SUFFIXES:
                out.extend(read_stamped(self.path(cell, suffix, day, map_id)))
            out.sort(key=lambda pair: pair[0])
            self._lines[key] = out
        return self._lines[key]

    def span_lines(self, cell, map_id, day):
        """[(stamp, line)] of the cell's stamped lines on that day and the next, in stamp order: the kit writes one file
        per day, so a session that crosses midnight carries on in the next day's files (regress.zone_lines reads both
        days the same way)."""
        next_day = (datetime.date.fromisoformat(day) + datetime.timedelta(days=1)).isoformat()
        return sorted(self.lines(cell, map_id, day) + self.lines(cell, map_id, next_day), key=lambda pair: pair[0])

    def own_cell_stamps(self, cell, map_id, day, after):
        """The stamps of the cell's 'Own cell from' lines at or after `after` on that day and the next, in time order."""
        return [t for t, line in self.span_lines(cell, map_id, day) if t >= after and rigtools.OWN_CELL.search(line)]


def read_report(path):
    """A saved report's lines, without line ends."""
    with open(path, encoding="utf-8") as f:
        return [line.rstrip("\n") for line in f]


def parse_report(lines):
    """(command, argparse.Namespace, [recorded window start 'HH:MM:SS' or None per run]) rebuilt from a report's text
    with abload.build_parser, so every flag newer than the report has its default. Raises SkipReport for a report
    written with phase 0 flags and NotAReport (a ValueError) for any other text that is not a 0.71 ab or probe report."""
    if len(lines) > 2 and lines[2].startswith(PHASE0_HEADER):
        raise SkipReport("written with phase 0 flags; its JSONL rows carry window_local")
    header = lines[1] if len(lines) > 1 else ""
    m = AB_HEADER.match(header)
    if m:
        load, band_text, repeats, seed, warmup, measure = m.groups()
        players, monsters, layouts, starts = [], [], [], []
        for i, line in enumerate(lines):
            run = RUN.match(line)
            if not run:
                continue
            n, mon, repeat, layout = int(run.group(1)), run.group(2), int(run.group(3)), run.group(4)
            if n not in players:
                players.append(n)
            if mon not in monsters:
                monsters.append(mon)
            if repeat == 1 and n == players[0] and mon == monsters[0]:
                layouts.append(layout)
            peak = PEAK_START.match(lines[i + 1]) if i + 1 < len(lines) else None
            starts.append(peak.group(1) if peak else None)
        if not starts:
            raise NotAReport("an abload ab report with no run line")
        band = "0" if band_text.startswith("as configured") else band_text
        argv = ["ab", f"--players={','.join(map(str, players))}", f"--monsters={','.join(monsters)}",
                f"--layouts={','.join(layouts)}", f"--repeats={repeats}", f"--seed={seed}", f"--load={load}",
                f"--band={band}", f"--warmup={warmup}", f"--measure={measure}"]
        return "ab", abload.build_parser().parse_args(argv), starts
    m = PROBE_HEADER.match(header)
    if m:
        cell, warmup, measure = m.groups()
        burns = [row.group(1) for row in (PROBE_ROW.match(line) for line in lines[3:]) if row]
        argv = ["probe", f"--burns={','.join(burns)}", f"--cell={cell}", f"--warmup={warmup}", f"--measure={measure}"]
        return "probe", abload.build_parser().parse_args(argv), [None] * len(burns)
    raise NotAReport(f"not an abload 0.71 ab or probe report: {header!r}")


def session_cell(args):
    """(cell, map id) whose Own lines mark a report's Play sessions: C for ab (every layout holds it), else the
    probe's cell."""
    if args.command != "probe":
        return "C", r.MAP
    zones = {z.lower(): (z, m) for z, m in r.ZONES}
    if args.cell.lower() not in zones:
        raise ValueError(f"the probe report names cell {args.cell}, which the rig does not have")
    return zones[args.cell.lower()]


def sessions(logs, lines, args, starts):
    """(since, until, start, end) per run, from the session cell's Play sessions after the report's start time.
    Raises ValueError when the first line has no start time or the cell has fewer sessions than the report has runs."""
    m = HEADER_TIME.search(lines[0]) if lines else None
    if m is None:
        raise ValueError(f"the report's first line has no start time: {lines[0] if lines else ''!r}")
    after = datetime.datetime.strptime(m.group(1), "%Y-%m-%d %H:%M")
    cell, map_id = session_cell(args)
    own = logs.own_cell_stamps(cell, map_id, after.strftime("%Y-%m-%d"), after)
    if len(own) < len(starts):
        raise ValueError(f"cell {cell} has {len(own)} Play session(s) after {after} in {logs.logs_dir}, "
                         f"the report has {len(starts)} run(s)")
    out = []
    for k, recorded in enumerate(starts):
        since = own[k] - SESSION_MARGIN
        until = own[k + 1] - SESSION_MARGIN if k + 1 < len(own) else own[k] + LAST_SESSION
        if recorded:
            clock = datetime.time.fromisoformat(recorded)
            start = datetime.datetime.combine(own[k].date(), clock)
            if clock < own[k].time():   # the window started after midnight, its Own line before it
                start += datetime.timedelta(days=1)
        else:
            start = own[k] + datetime.timedelta(seconds=args.warmup - OWN_LAG)
        out.append((since, until, start, start + datetime.timedelta(seconds=args.measure)))
    return out


def replay(saved, logs):
    """(exit code, replayed lines, sessions) of a saved report's text, replayed through the unchanged abload command
    with rigrun.run_window and regress.zone_lines patched; every rig-driving regress call raises meanwhile."""
    command, args, starts = parse_report(saved)
    runs = sessions(logs, saved, args, starts)
    queue = list(runs)
    bound = {}

    def replay_run_window(env, warmup, measure, rep):
        if not queue:
            raise RuntimeError("the replay asked for more runs than the report has")
        since, until, start, end = queue.pop(0)
        bound["until"] = until
        return since, start, end

    def bounded_zone_lines(zone, start, day, map_id=r.MAP, suffixes=SUFFIXES):
        since = datetime.datetime.fromisoformat(f"{day} {start}")
        return [line for t, line in logs.span_lines(zone, map_id, day) if since <= t < bound["until"]]

    def refuse_rig(*call_args, **call_kwargs):
        raise RuntimeError("the offline re-grade never drives the rig")

    rep = rigrun.Report(saved[0])
    with contextlib.ExitStack() as stack:
        stack.enter_context(mock.patch.object(rigrun, "run_window", replay_run_window))
        stack.enter_context(mock.patch.object(r, "zone_lines", bounded_zone_lines))
        for name in RIG_CALLS:
            stack.enter_context(mock.patch.object(r, name, refuse_rig))
        stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
        code = abload.ab_command(args, rep) if command == "ab" else abload.probe_command(args, rep)
    return code, rep.lines, runs


def diff_lines(saved, replayed):
    """[(0-based line number, saved, replayed)] where the texts differ; a length difference is one more entry."""
    diffs = [(i, a, b) for i, (a, b) in enumerate(zip(saved, replayed)) if a != b]
    if len(saved) != len(replayed):
        diffs.append((min(len(saved), len(replayed)), f"<{len(saved)} lines>", f"<{len(replayed)} lines>"))
    return diffs


def regrade(path, logs):
    """(exit code, [(0-based line number, saved, replayed)]) of one saved report replayed from logs (a CellLogs)."""
    saved = read_report(path)
    code, replayed, _ = replay(saved, logs)
    return code, diff_lines(saved, replayed)


def verdict_line(lines):
    """A report's last go/no-go or probe acceptance line, or '?' when it has none."""
    return next((line for line in reversed(lines) if line.startswith(VERDICT_PREFIXES)), "?")


def replay_reports(logs_dir, paths=None):
    """(printed lines, exit code) of re-grading reports against the cells' logs in logs_dir. With no paths: every
    abload_*.txt in logs_dir, a text that is not a 0.71 report listed as skipped; with paths, such a text cannot be
    replayed. A phase 0 report is always skipped. Exit 1 when any replayed report differs or cannot be replayed."""
    explicit = bool(paths)
    if not explicit:
        paths = sorted(glob.glob(os.path.join(logs_dir, "abload_*.txt")))
    logs = CellLogs(logs_dir)
    out, failed = [], 0
    for path in paths:
        name = os.path.basename(path)
        saved = read_report(path)
        try:
            code, replayed, _ = replay(saved, logs)
        except SkipReport as ex:
            out.append(f"{name}: skipped ({ex})")
            continue
        except NotAReport as ex:
            if not explicit:
                out.append(f"{name}: skipped ({ex})")
                continue
            out.append(f"{name}: cannot replay: {ex}")
            failed += 1
            continue
        except ValueError as ex:
            out.append(f"{name}: cannot replay: {ex}")
            failed += 1
            continue
        diffs = diff_lines(saved, replayed)
        if not diffs:
            out.append(f"{name}: IDENTICAL, exit {code}; {verdict_line(saved)}")
            continue
        failed += 1
        out.append(f"{name}: {len(diffs)} line(s) differ, exit {code}")
        for i, a, b in diffs[:5]:
            out += [f"    line {i + 1}", f"      saved:    {a}", f"      replayed: {b}"]
    return out, 1 if failed else 0


def fixture(out_dir, report_paths, logs_dir):
    """Writes a replayable fixture of the reports into out_dir and returns the written paths: the reports, and per cell
    log file their replay reads, only its stamped lines inside a report's [first since, last until) that KEEP matches
    (file names kept; a file with no kept line is not written). Raises ValueError when out_dir is logs_dir or a report
    does not replay IDENTICAL from logs_dir; nothing is written then."""
    if os.path.normcase(os.path.abspath(out_dir)) == os.path.normcase(os.path.abspath(logs_dir)):
        raise ValueError(f"the fixture folder {out_dir} is the logs folder; it would overwrite the logs it trims")
    logs = CellLogs(logs_dir)
    spans = []
    for path in report_paths:
        saved = read_report(path)
        _, replayed, runs = replay(saved, logs)
        diffs = diff_lines(saved, replayed)
        if diffs:
            raise ValueError(f"{os.path.basename(path)} does not replay IDENTICAL from {logs_dir} "
                             f"({len(diffs)} line(s) differ), so it cannot be a fixture")
        spans.append((runs[0][0], runs[-1][1]))
    os.makedirs(out_dir, exist_ok=True)
    written = []
    for path in report_paths:
        target = os.path.join(out_dir, os.path.basename(path))
        shutil.copyfile(path, target)
        written.append(target)
    for cell, map_id, day in logs.touched:
        for suffix in SUFFIXES:
            source = logs.path(cell, suffix, day, map_id)
            kept = [line for t, line in read_stamped(source)
                    if KEEP.search(line) and any(since <= t < until for since, until in spans)]
            if not kept:
                continue
            target = os.path.join(out_dir, os.path.basename(source))
            with open(target, "w", encoding="utf-8", newline="\n") as f:
                f.write("\n".join(kept) + "\n")
            written.append(target)
    return written


def build_parser():
    """The command line's parser (replay and fixture subcommands)."""
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="command", required=True)
    rp = sub.add_parser("replay", help="re-grade saved reports against the cells' saved logs")
    rp.add_argument("--logs", default=r.LOGS, help="folder of the cells' logs (and, with no REPORT, of the reports)")
    rp.add_argument("reports", nargs="*", metavar="REPORT")
    fx = sub.add_parser("fixture", help="copy reports and their trimmed cell logs into a folder")
    fx.add_argument("--out", required=True, help="the fixture folder")
    fx.add_argument("--logs", default=r.LOGS, help="folder of the cells' logs the reports were graded from")
    fx.add_argument("reports", nargs="+", metavar="REPORT")
    return ap


def main():
    """Runs one subcommand and returns its exit code (fixture: 1 when a report cannot become a fixture)."""
    args = build_parser().parse_args()
    if args.command == "replay":
        lines, code = replay_reports(args.logs, args.reports)
        for line in lines:
            print(line)
        return code
    try:
        written = fixture(args.out, args.reports, args.logs)
    except ValueError as ex:
        print(f"no fixture written: {ex}")
        return 1
    size = sum(os.path.getsize(path) for path in written)
    print(f"wrote {len(written)} file(s), {size / 1024:.0f} KB, to {args.out}")
    for path in written:
        print(f"  {os.path.basename(path)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
