"""Unit tests for step 8a decision 1, machine keying on (build, host), across p0results and p0report, and the
"build alone" grep over the harness (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py").
p0blocks' resume, canary and verdict keying are in test_p0blocks (MachineTests).
"""
import ast
import glob
import os
import unittest

import p0report
import p0results
import test_p0model
import test_p0report
import test_p0results

canary_row, block_row = test_p0results.canary_row, test_p0results.block_row


class ResultsTests(unittest.TestCase):
    """Step 8a decision 1: A and B rows of one build never judge, resume or pool each other."""

    def test_cpu_problems(self):
        rows = [{"host": "A", "cpu": "X"}, {"host": "B", "cpu": "X"}, {"host": "A", "cpu": None}]
        self.assertEqual(p0results.cpu_problems(rows + [{"host": "A", "cpu": "Y"}]),
                         ["host A ran on 2 CPUs (X and Y): give each machine its own host name"])
        self.assertEqual(p0results.cpu_problems(rows), [])

    def test_a_canary_is_judged_against_its_own_host(self):
        rows = [canary_row("baseline", "09:00:00", 100.0, host="A"), canary_row("baseline", "09:00:00", 120.0, host="B"),
                block_row("10:00:00", host="A"), block_row("10:00:00", host="B"),
                canary_row("check", "10:10:00", 110.0, host="A"), canary_row("check", "10:20:00", 121.0, host="B")]
        out = p0results.apply_drift(rows)
        self.assertEqual(p0results.canary_baselines(rows), {("b1", "A"): 100.0, ("b1", "B"): 120.0})
        self.assertEqual([out[4]["drift"], out[5]["drift"]], [True, False])
        self.assertEqual([(row["accepted"], row["excluded_reason"]) for row in out[2:4]],
                         [(False, "drift"), (True, None)])

    def test_summaries_keep_the_hosts_apart(self):
        runs = [p0results.run_row(test_p0results.spec(key=f"k{host}"), test_p0results.a_run(),
                                  dict(test_p0results.CONTEXT, host=host), [], None) for host in ("A", "B")]
        summaries = p0results.summary_rows(runs)
        self.assertEqual([(s["host"], s["n"], s["plan_keys"]) for s in summaries], [("A", 1, ["kA"]), ("B", 1, ["kB"])])
        with self.assertRaisesRegex(ValueError, r"rows from 2 hosts \(A, B\) are never pooled"):
            p0results.summary_row(runs, "C")


# The harness functions allowed a comparison on a build alone: they compare build ids among rows already kept to one
# host, never a row's build against a machine's.
BUILD_ALONE_ALLOWED = {("p0report.py", "_check_builds")}


def _reads(node, names):
    """Whether an expression reads one of the names: X.get("name"), X["name"], X.name or the variable name."""
    for sub in ast.walk(node):
        if (isinstance(sub, ast.Call) and isinstance(sub.func, ast.Attribute) and sub.func.attr == "get" and sub.args
                and isinstance(sub.args[0], ast.Constant) and sub.args[0].value in names):
            return True
        if ((isinstance(sub, ast.Subscript) and isinstance(sub.slice, ast.Constant) and sub.slice.value in names)
                or (isinstance(sub, ast.Attribute) and sub.attr in names)
                or (isinstance(sub, ast.Name) and sub.id in names)):
            return True
    return False


def build_alone(source, name):
    """[(name, line, function)] of each comparison on a build (not an `is None` test) that compares no host itself and
    sits in no `and` with a comparison that does: the "build alone" grep of step 8a decision 1."""
    tree = ast.parse(source, name)
    parents = {child: node for node in ast.walk(tree) for child in ast.iter_child_nodes(node)}
    found = []
    for node in ast.walk(tree):
        if (not isinstance(node, ast.Compare) or not _reads(node, {"build"}) or _reads(node, {"host"})
                or all(isinstance(op, (ast.Is, ast.IsNot)) for op in node.ops)):
            continue
        child, parent, paired = node, parents.get(node), False
        while isinstance(parent, ast.BoolOp):
            paired |= isinstance(parent.op, ast.And) and any(_reads(v, {"host"}) for v in parent.values
                                                                if v is not child)
            child, parent = parent, parents.get(parent)
        scope = node
        while scope in parents and not isinstance(scope, (ast.FunctionDef, ast.AsyncFunctionDef)):
            scope = parents[scope]
        if not paired:
            found.append((name, node.lineno, getattr(scope, "name", "<module>")))
    return found


class BuildAloneTests(unittest.TestCase):
    def test_the_grep_finds_a_comparison_on_build_alone(self):
        cases = {
            "a row's build": ('def f(row, b):\n    return row.get("build") == b\n', [("x.py", 2, "f")]),
            "a membership test": ("def f(build, d):\n    return build not in d\n", [("x.py", 2, "f")]),
            "a subscript in a filter": ('rows = [r for r in rows if r["build"] == b]\n', [("x.py", 1, "<module>")]),
            "an attribute": ("class S:\n    def ours(self, r):\n        return r.build == self.build\n",
                             [("x.py", 3, "ours")]),
            "a tuple with the host": ('def f(r, m):\n    return (r.get("build"), r.get("host")) == m\n', []),
            "an and with the host": ('def f(r, b, h):\n    return r.get("build") == b and r.get("host") == h\n', []),
            "an or inside an and with the host": ('ok = (b is None or r["build"] == b) and (h is None or '
                                                  'r["host"] == h)\n', []),
            "an or with the host": ('def f(r, b, h):\n    return r.get("build") == b or r.get("host") == h\n',
                                    [("x.py", 2, "f")]),
            "a None test": ("def f(build):\n    return build is None\n", []),
        }
        for name, (source, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(build_alone(source, "x.py"), expected)

    def test_the_harness_compares_no_build_alone(self):
        here = os.path.dirname(os.path.abspath(__file__))
        found = []
        for path in sorted(glob.glob(os.path.join(here, "*.py")) + glob.glob(os.path.join(here, "phase0", "*.py"))):
            if not os.path.basename(path).startswith("test_"):
                with open(path, encoding="utf-8") as f:
                    found += build_alone(f.read(), os.path.basename(path))
        self.assertEqual([hit for hit in found if hit[::2] not in BUILD_ALONE_ALLOWED], [])


class ReportTests(unittest.TestCase):
    def test_hosts_never_pool(self):
        floor = test_p0report.with_game(test_p0model.floor_rows(), "neutral")
        other = [dict(r, host="B") for r in floor]
        cases = {
            "two hosts without host": (floor + other, {}, r"rig run rows from 2 hosts \(B, None\) are never pooled: "
                                                          r"report one host at a time"),
            "a host naming none": (floor + other, {"host": "C"}, r"host C names none of the rig hosts \(B, None\)"),
            "a host on two CPUs": (other + [dict(other[0], cpu="Other CPU")], {},
                                   r"host B ran on 2 CPUs \(Other CPU and Test CPU\)"),
        }
        for name, (rows, kwargs, message) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(p0report.ReportError, message):
                test_p0report.report(rows, **kwargs)
        result = test_p0report.report(floor + other, host="B")
        self.assertIn("4 rig run rows of other builds or hosts left out (--build None, host B)", result["markdown"])
        self.assertEqual({s["host"] for s in result["summary"]}, {"B"})
        self.assertIn("Builds b1; host B; CPU Test CPU.", result["markdown"])
        bridged = [dict(r, host="A") for r in floor] + [
            dict(floor[0], build="b2", host="A", plan_key="b2-run"),
            dict(test_p0report.canary("baseline", 100.0, "2026-09-28T09:00:00"), host="A"),
            dict(test_p0report.canary("baseline", 100.0, "2026-09-28T09:00:00"), host="B"),
            dict(test_p0report.canary("check", 104.0, "2026-09-28T12:00:00", "b2"), host="B")]
        with self.assertRaisesRegex(p0report.ReportError, "build b2 has no accepted canary check on host A"):
            test_p0report.report(bridged, bridge="b1", host="A")

    def test_latest_per_plan_key_keeps_hosts_and_steps(self):
        rows = [{"plan_key": "k", "accepted": True, "family": "fanout", "build": "b1", "host": host,
                 "point": {"step": step}, "window_local": {"end": end}}
                for host, step, end in (("A", 50, "10:00"), ("A", 100, "10:01"), ("B", 50, "10:02"),
                                        ("A", 50, "11:00"))]
        kept, dropped = p0report.latest_per_plan_key(rows)
        self.assertEqual((kept, dropped), (rows[1:], 1))


if __name__ == "__main__":
    unittest.main()
