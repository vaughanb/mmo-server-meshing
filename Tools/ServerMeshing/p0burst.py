"""Phase 0 step 9's database burst around one rig run (step9_design.md decisions 1, 3 and 4), on machine A only: the
burst rows written before Play, the drain stop_when, and the check that no burst row changes once Play has stopped.
p0peer.window_session composes it with B's PeerSession for every spec that sets burst_rows; it never imports p0peer.

    burst = p0burst.BurstSession(spec, rep)       # spec["burst_rows"] = N (meshburst0001..N, provision_burst)
    burst.before_play()                           # rows reset out of Play (ProvisionError if refused); Editor.log marked
    burst.stop_when(play_start, start)            # True once the drain is empty after `DB burst: done` (or 120 s on)
    burst.after_play() / burst.after_stop()       # updateAt read before and after the stop; burst.problems
    p0burst.drain_state(done, windows, now)       # the pure decision behind stop_when

Where the lines are: the burst runs in a cell (its `DB burst: done` line is in the cells' kit logs, read by
regress.zone_lines like p0peer.outcomes_stop), and the database manager runs in the editor, whose kit logger has no
file (MMOServerInstance only calls EnableLogger for a server started from the command line): its `DB saves window`
lines, ` INFO Mesh [yyyy-MM-dd HH:mm:ss] - DB saves window ...`, reach the Unity Editor.log (EDITOR_LOG, or
MESH_EDITOR_LOG). That file is over 1 GB, so before_play records its size and every read starts there. A done line is
looked for in both sources. The stamps are A's local clock, whole seconds: a saves window counts only when stamped
after the done line's second.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import datetime
import os
import re
import time

import provision_burst
import regress as r
import rigtools

EDITOR_LOG = os.environ.get("MESH_EDITOR_LOG") or os.path.join(os.environ.get("LOCALAPPDATA", ""), "Unity", "Editor",
                                                                "Editor.log")
DRAIN_BOUND_S = 120     # decision 4: the drain is waited for at most 120 s after the last arrival settles
AFTER_STOP_S = 5        # after_stop: how long after the stop the second updateAt read waits
SAVES_WINDOW = re.compile(r"DB saves window \d+(?:\.\d+)? s: pending (\d+) enqueued")
BURST_DONE = re.compile(r"DB burst: done, (\d+) ready (\d+) failed")


def editor_size(path=EDITOR_LOG):
    """The editor log's size in bytes (0 when there is none): where this run's lines will start."""
    try:
        return os.path.getsize(path)
    except OSError:
        return 0


def editor_lines(offset, path=EDITOR_LOG):
    """(the complete lines the editor log gained since byte offset, the offset after them). A log shorter than offset
    (the editor restarted and began a new one) is read from its start; a missing log has no lines."""
    try:
        with open(path, "rb") as f:
            f.seek(0, os.SEEK_END)
            if f.tell() < offset:
                offset = 0
            f.seek(offset)
            data = f.read()
    except OSError:
        return [], offset
    data = data[:data.rfind(b"\n") + 1]
    return data.decode("utf-8", errors="replace").splitlines(), offset + len(data)


def saves_windows(lines):
    """[(stamp, pending)] of the `DB saves window` lines that carry a kit stamp, in log order."""
    found = []
    for line in lines:
        m = SAVES_WINDOW.search(line)
        stamp = rigtools.stamp_datetime(line) if m else None
        if stamp is not None:
            found.append((stamp, int(m.group(1))))
    return found


def done_at(lines):
    """The stamp of the first stamped `DB burst: done, N ready M failed` line, else None."""
    return next((rigtools.stamp_datetime(line) for line in lines
                 if BURST_DONE.search(line) and rigtools.stamp_datetime(line) is not None), None)


def drain_state(done, windows, now, bound_s=DRAIN_BOUND_S):
    """Where the drain is: {"state": "waiting" (no done line yet) | "draining" | "drained" | "timed out", "stop":
    whether the measurement may end, "pending": the newest pending after the done line (None before one), "empty_at":
    the stamp of the first window after the done line with pending 0}. Drained wins over timed out."""
    after = [(stamp, pending) for stamp, pending in windows if done is not None and stamp > done]
    empty_at = next((stamp for stamp, pending in after if pending == 0), None)
    pending = after[-1][1] if after else None
    if done is None:
        state = "waiting"
    elif empty_at is not None:
        state = "drained"
    elif (now - done).total_seconds() >= bound_s:
        state = "timed out"
    else:
        state = "draining"
    return {"state": state, "stop": state in ("drained", "timed out"), "pending": pending, "empty_at": empty_at}


class BurstSession:
    """The burst's part of one run on A (decision 4). before_play: the N burst rows reset (provision_burst.burst,
    out of Play; what it refuses is raised) and the editor log marked. stop_when: drain_state over the cells' and the
    editor's lines since Play began; a read that fails answers False. after_play (Play still up, the measurement
    over): the rows' updateAt read; after_stop (once Play stopped, AFTER_STOP_S later): read again, and any row that
    changed is a problem (a save left for after Play). problems make the run's rows not accepted (p0peer.mark_rows);
    stop_reason is always None (nothing is left running)."""

    def __init__(self, spec, rep, connect=None, editor_log=EDITOR_LOG, now=datetime.datetime.now):
        self.n, self.cells, self.rep = int(spec["burst_rows"]), list(spec.get("cells_read") or ["C"]), rep
        self.connect, self.editor_log, self.now = connect, editor_log, now
        self.problems, self.stop_reason = [], None
        self.offset, self.drain, self.before = None, None, None
        self._editor = []

    def before_play(self):
        provision_burst.burst(self.n, log=self.rep.note)
        self.offset = editor_size(self.editor_log)

    def stop_when(self, play_start, start):
        try:
            lines, self.offset = editor_lines(self.offset or 0, self.editor_log)
            self._editor += lines
            hms, day, maps = play_start.strftime("%H:%M:%S"), play_start.strftime("%Y-%m-%d"), dict(r.ZONES)
            cell_lines = [line for cell in self.cells for line in r.zone_lines(cell, hms, day, maps[cell])]
            self.drain = drain_state(done_at(cell_lines + self._editor), saves_windows(self._editor), self.now())
        except Exception:  # a mid-run look that fails only means the run is not cut short
            return False
        if self.drain["state"] == "timed out":
            self.rep.note(f"    burst: the save drain was not empty {DRAIN_BOUND_S} s after the burst was done "
                          f"(pending {self.drain['pending']})")
        return self.drain["stop"]

    def after_play(self):
        if self.drain is not None and self.drain["state"] == "timed out":
            self.problems.append(f"burst: pending {self.drain['pending']} saves {DRAIN_BOUND_S} s after the burst was "
                                 "done (the drain never emptied)")
        try:
            self.before = provision_burst.snapshot(self.n, self.connect)
        except Exception as ex:  # the check cannot be made; the row says so
            self.problems.append(f"burst: updateAt not read before the stop: {ex}")

    def after_stop(self, settle_s=AFTER_STOP_S):
        """The second updateAt read, once Play has stopped (run_window returned); nothing without the first."""
        if self.before is None:
            return
        time.sleep(settle_s)
        try:
            after = provision_burst.snapshot(self.n, self.connect)
        except Exception as ex:  # the check cannot be made; the row says so
            self.problems.append(f"burst: updateAt not read after the stop: {ex}")
            return
        changed = provision_burst.changed_rows(self.before, after)
        if changed:
            self.problems.append(f"burst: {len(changed)} row(s) changed after Play stopped: "
                                 + ", ".join(changed[:5]) + (" ..." if len(changed) > 5 else ""))
