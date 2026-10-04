"""Unit tests for regress's status waits (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import unittest
from unittest import mock

import regress


class FakeClock:
    """time.time/time.sleep for regress: sleeping advances the clock, and the status age follows a script."""

    def __init__(self, ages, ready_at=None):
        self.now = 1000.0
        self.start = self.now
        self.ages = ages            # elapsed seconds -> status age, the last entry at or before now applies
        self.ready_at = ready_at    # elapsed seconds after which the predicate holds; None never

    def time(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds

    def elapsed(self):
        return self.now - self.start

    def age(self):
        current = None
        for at, age in sorted(self.ages.items()):
            if self.elapsed() >= at:
                current = age
        return current

    def ready(self, _status):
        return self.ready_at is not None and self.elapsed() >= self.ready_at


class WaitStatusTests(unittest.TestCase):
    def run_wait(self, clock, timeout, busy_cap):
        with mock.patch.object(regress.time, "time", clock.time), \
                mock.patch.object(regress.time, "sleep", clock.sleep), \
                mock.patch.object(regress, "status_age", clock.age), \
                mock.patch.object(regress, "read_status", lambda: {}):
            return regress.wait_status(clock.ready, timeout, "Play", busy_cap=busy_cap)

    def test_times_out(self):
        cases = {
            "idle editor, never ready": (FakeClock({0: 1.0}), 60, 120, 60),
            "no status file at all": (FakeClock({0: None}), 60, 120, 60),
            "busy but no cap": (FakeClock({0: 30.0}), 60, 0, 60),
            "busy past the cap": (FakeClock({0: 30.0}), 60, 120, 180),
            "busy for 40 s, then idle": (FakeClock({0: 1.0, 10: 20.0, 50: 1.0}), 60, 120, 100),
        }
        for name, (clock, timeout, cap, gave_up_at) in cases.items():
            with self.subTest(name):
                with self.assertRaises(TimeoutError):
                    self.run_wait(clock, timeout, cap)
                self.assertAlmostEqual(clock.elapsed(), gave_up_at, delta=2)

    def test_returns(self):
        cases = {
            "ready at once": (FakeClock({0: 1.0}, ready_at=0), 60, 120),
            "ready inside the timeout": (FakeClock({0: 1.0}, ready_at=40), 60, 120),
            "slow reload, ready after the timeout": (FakeClock({0: 1.0, 4: 40.0, 44: 1.0}, ready_at=90), 60, 120),
        }
        for name, (clock, timeout, cap) in cases.items():
            with self.subTest(name):
                self.assertEqual(self.run_wait(clock, timeout, cap), {})
                self.assertAlmostEqual(clock.elapsed(), clock.ready_at, delta=2)


if __name__ == "__main__":
    unittest.main()
