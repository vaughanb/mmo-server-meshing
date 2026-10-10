"""Machine B as Phase 0's peer session drives it (step8b_design.md decision 8, step9_design.md decision 12), split out
of p0peer (the ~500-line rule) so that p0peer and p0calibration share it without importing each other: the Stopped
exception, RemoteB (remoteb's and remotebots' functions over one set of connection settings) and quiet_b. p0peer
re-exports every name here; p0blocks.Stopped is this Stopped.

    p0remote.quiet_b(rep)          # B's bots, sink and clumsy stopped; Stopped when any survives or B does not answer
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py" (test_p0peer)
"""
import remoteb
import remotebots
import remotessh

POLL_S = 5                  # how often a wait for B (or a log line) looks again


class Stopped(Exception):
    """Stopped ends an invocation early: its exit code (2: refused, B unreachable; 3: B kept processes, a canary) and
    the reason printed. p0blocks.Stopped is this class."""

    def __init__(self, code, reason):
        super().__init__(reason)
        self.code, self.reason = code, reason


class RemoteB:
    """Machine B as the peer session drives it: remoteb's and remotebots' functions over one set of connection
    settings (remotessh.defaults() unless given)."""

    def __init__(self, args=None):
        self.args = args or remotessh.defaults()

    def state(self):
        return remoteb.state(self.args)

    def deployed_matches(self):
        return remoteb.deployed_matches(self.args)

    def stop_bots(self):
        return remotebots.stop_bots(self.args)

    def stop_sink(self):
        return remoteb.stop_sink(self.args)

    def start_sink(self, port, peers, base=None, sizes=False):
        return remoteb.start_sink(self.args, port, peers, base, sizes)

    def start_bots(self, first, n, mode, points, bot_net=None, rig_ships=None, stagger=1):
        return remotebots.start_bots(self.args, first, n, mode, points, bot_net, rig_ships, stagger)

    def fetch_bots(self):
        return remotebots.fetch_bots(self.args)

    def fetch(self):
        return remoteb.fetch(self.args)

    def clumsy_count(self):
        return remoteb.clumsy_count(self.args)

    def stop_impair(self):
        return remoteb.stop_impair(self.args)

    def start_impair(self, profile):
        return remoteb.start_impair(self.args, profile)


def plural(count, word):
    return f"{count} {word}{'' if count == 1 else 's'}"


def quiet_b(rep, b=None):
    """Stops B's bots and sink, and any clumsy (step 9 decision 12: clumsy outlives the SSH session and a crashed
    harness, so no run may start over a degraded link), and checks none is left: Stopped(3) when one survives,
    Stopped(2) when B does not answer. Before every peer run and every canary of a block with peers (B idle while A's
    canary measures)."""
    b = b or RemoteB()
    try:
        bots, sinks, found = b.stop_bots(), b.stop_sink(), b.clumsy_count()
        if found:
            rep.note(f"    B: {found} clumsy process(es) running (an earlier run's impairment); stopping them")
        clumsy = b.stop_impair() if found else 0
    except remotessh.Unreachable as ex:
        raise Stopped(2, f"B is unreachable: {ex}") from ex
    if bots or sinks or clumsy:
        left = " and ".join(plural(c, w) for c, w in ((bots, "bot"), (sinks, "sink")) if c)
        left = " and ".join(part for part in (left, f"{clumsy} clumsy process(es)" if clumsy else "") if part)
        raise Stopped(3, f"B: {left} still running after being stopped")
