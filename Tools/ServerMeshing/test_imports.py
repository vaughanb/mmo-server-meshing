"""Import tests for the harness modules (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py").

Every module in this folder (tests excepted) imports alone in a fresh `python -B -W error` process, in dependency order
and in reverse, printing nothing; one process more imports them all in each order. The import graph (every
`import x` of a folder module, function-level ones included) must have no cycle, so a cycle or an import-time side
effect fails the suite.
"""
import ast
import os
import subprocess
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
# The modules of the step 4 contract's import graph, step 5's p0phantom, step 6's p0sender and step 7's p0ships: each
# must exist (a missing one would silently leave the tests).
PHASE0 = ("p0base", "p0tools", "p0phantom", "p0sender", "p0ships", "p0cells", "p0geo", "p0model", "p0rates", "p0results", "p0markdown",
          "p0checks", "p0accept", "p0plans", "p0dryrun", "rigrun", "abgrade", "abload", "p0report", "p0gates", "p0load",
          "p0blocks", "abregrade", "regress", "rigtools")
# The contract's pure modules never import the ones that drive the rig or write files (rigrun is allowed: its
# constants, layout_override, check_env and run_problems do no I/O). p0rates, p0markdown, p0accept, p0dryrun,
# p0phantom, p0sender and p0ships are split out of p0model, p0results, p0checks, p0plans and p0tools (the ~500-line rule)
# and stay pure like them.
PURE = ("p0base", "p0tools", "p0phantom", "p0sender", "p0ships", "p0cells", "p0checks", "p0accept", "p0model", "p0rates", "p0geo",
        "p0results", "p0markdown", "p0plans", "p0dryrun", "abgrade", "p0report", "p0gates")
IO = ("abload", "p0load", "p0blocks", "abregrade")
TIMEOUT_S = 120


def folder_modules():
    """The module names of this folder's .py files, tests excepted."""
    return sorted(name[:-3] for name in os.listdir(HERE) if name.endswith(".py") and not name.startswith("test_"))


def imports_of(module, modules):
    """The folder modules a module imports anywhere in its source (import x, import x as y, from x import y)."""
    with open(os.path.join(HERE, f"{module}.py"), encoding="utf-8") as f:
        tree = ast.parse(f.read(), f"{module}.py")
    found = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            found.update(alias.name.split(".")[0] for alias in node.names)
        elif isinstance(node, ast.ImportFrom) and node.module and node.level == 0:
            found.add(node.module.split(".")[0])
    return sorted(found & set(modules) - {module})


def dependency_order(graph):
    """The modules with every module after the ones it imports (ties by name). Raises ValueError naming a cycle."""
    order, state = [], {}

    def visit(module, path):
        if state.get(module) == "done":
            return
        if state.get(module) == "visiting":
            cycle = path[path.index(module):] + [module]
            raise ValueError(f"import cycle: {' -> '.join(cycle)}")
        state[module] = "visiting"
        for imported in graph[module]:
            visit(imported, path + [module])
        state[module] = "done"
        order.append(module)

    for module in sorted(graph):
        visit(module, [])
    return order


def python(code):
    """(exit code, stdout, stderr) of `python -B -W error -c code` in this folder."""
    done = subprocess.run([sys.executable, "-B", "-W", "error", "-c", code], cwd=HERE, capture_output=True, text=True,
                          timeout=TIMEOUT_S)
    return done.returncode, done.stdout, done.stderr


class ImportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        modules = folder_modules()
        cls.graph = {module: imports_of(module, modules) for module in modules}

    def test_dependency_order_refuses_a_cycle(self):
        cases = {
            "two modules": ({"a": ["b"], "b": ["a"]}, "import cycle: a -> b -> a"),
            "three modules": ({"a": ["b"], "b": ["c"], "c": ["a"]}, "import cycle: a -> b -> c -> a"),
        }
        for name, (graph, message) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(ValueError, message):
                dependency_order(graph)
        self.assertEqual(dependency_order({"a": ["b", "c"], "b": ["c"], "c": []}), ["c", "b", "a"])

    def test_the_phase0_modules_exist(self):
        for module in PHASE0:
            with self.subTest(module):
                self.assertIn(module, self.graph)

    def test_pure_modules_import_no_io_module(self):
        for module in PURE:
            with self.subTest(module):
                self.assertEqual(sorted(set(self.graph[module]) & set(IO)), [])

    def test_the_import_graph_has_no_cycle(self):
        order = dependency_order(self.graph)
        for module, imported in self.graph.items():
            with self.subTest(module):
                self.assertTrue(all(order.index(other) < order.index(module) for other in imported))

    def test_every_module_imports_alone_in_both_orders(self):
        order = dependency_order(self.graph)
        for direction, modules in (("dependency order", order), ("reverse", list(reversed(order)))):
            for module in modules:
                with self.subTest(f"{direction}: {module}"):
                    code, out, err = python(f"import {module}")
                    self.assertEqual(code, 0, err)
                    self.assertEqual(out, "", f"importing {module} printed")

    def test_all_modules_import_together_in_both_orders(self):
        order = dependency_order(self.graph)
        for direction, modules in (("dependency order", order), ("reverse", list(reversed(order)))):
            with self.subTest(direction):
                code, out, err = python("; ".join(f"import {module}" for module in modules))
                self.assertEqual(code, 0, err)
                self.assertEqual(out, "")


if __name__ == "__main__":
    unittest.main()
