"""Integrator's stray-reference check (read-only).

1. The builder's own regex over every .cs under Assets/ServerMeshing, for the Mesh and TestRig packages (exact emulation).
2. A looser declaration regex (attributes, any word modifiers such as new/extern/file, nested types) over TestMap/** and
   Tests/**: every declared type grepped as a whole word in Mesh/**, and every Tests/** type grepped in TestMap/**.
"""
import glob
import os
import re

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "Assets", "ServerMeshing"))
BUILDER = re.compile(
    r"^\s*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|ref|unsafe)\s+)*"
    r"(?:(?:record\s+(?:class\s+|struct\s+)?|class\s+|struct\s+|enum\s+|interface\s+)([A-Za-z_]\w*)"
    r"|delegate\s+[^(){};=]+?\s+([A-Za-z_]\w*)\s*[(<])",
    re.M)
LOOSE = re.compile(
    r"^\s*(?:\[[^\]\n]*\]\s*)*(?:[a-z]+\s+)*"
    r"(?:(?:record\s+(?:class\s+|struct\s+)?|class\s+|struct\s+|enum\s+|interface\s+)([A-Za-z_]\w*)"
    r"|delegate\s+[^(){};=]+?\s+([A-Za-z_]\w*)\s*[(<])",
    re.M)

scripts = {}
for f in glob.glob(os.path.join(ROOT, "**", "*.cs"), recursive=True):
    rel = "Assets/ServerMeshing/" + os.path.relpath(f, ROOT).replace("\\", "/")
    with open(f, encoding="utf-8-sig") as fh:
        scripts[rel] = fh.read()


def under(prefix):
    return lambda p: p.startswith("Assets/ServerMeshing/" + prefix + "/")


def builder_check(name, carried):
    left = {}
    for path, text in scripts.items():
        if not carried(path):
            for m in BUILDER.finditer(text):
                left[m.group(1) or m.group(2)] = path
    strays = []
    for path, text in scripts.items():
        if carried(path):
            for t, where in left.items():
                if re.search(r"\b" + re.escape(t) + r"\b", text):
                    strays.append(f"{name}: {path} uses {t} ({where})")
    return strays


def declared(pred, rx):
    out = {}
    for path, text in scripts.items():
        if pred(path):
            for m in rx.finditer(text):
                t = m.group(1) or m.group(2)
                if t in ("where", "new"):
                    continue
                out.setdefault(t, set()).add(path)
    return out


def hits(types, pred):
    found = []
    for path, text in scripts.items():
        if pred(path):
            lines = text.splitlines()
            for t in sorted(types):
                rx = re.compile(r"\b" + re.escape(t) + r"\b")
                for i, line in enumerate(lines, 1):
                    if rx.search(line):
                        found.append(f"{path}:{i}: {t} (declared in {', '.join(sorted(types[t]))}): {line.strip()[:140]}")
    return found


mesh, testmap, tests = under("Mesh"), under("TestMap"), under("Tests")
def rig(p):
    return mesh(p) or testmap(p)


print("== builder emulation")
b = builder_check("Mesh", mesh) + builder_check("TestRig", rig)
print("\n".join(b) if b else "no strays")

tm_types = declared(testmap, LOOSE)
ts_types = declared(tests, LOOSE)
tm_b = declared(testmap, BUILDER)
ts_b = declared(tests, BUILDER)
print(f"== TestMap types: {len(tm_types)} (builder regex {len(tm_b)}); Tests types: {len(ts_types)} (builder regex {len(ts_b)})")
extra = sorted((set(tm_types) - set(tm_b)) | (set(ts_types) - set(ts_b)))
print("types only the loose regex sees:", extra or "-")
print("== Mesh/** mentions of TestMap/** or Tests/** types")
both = dict(tm_types)
for t, p in ts_types.items():
    both.setdefault(t, set()).update(p)
h = hits(both, mesh)
print("\n".join(h) if h else "none")
print("== TestMap/** mentions of Tests/** types")
h = hits(ts_types, testmap)
print("\n".join(h) if h else "none")
print("== step-2 type list")
step2 = [p for p in scripts if "/Phase0/" in p or p.endswith(("MeshTickScheduleCopy.cs", "MeshTickWindowLineTests.cs",
         "ServerMeshAoiCountdownTests.cs", "ServerMeshRatesSettingTests.cs", "ServerMeshThreadClassTests.cs",
         "ServerMeshTickBurnSettingTests.cs", "MeshMeasurementTests.cs"))]
for p in sorted(step2):
    names = sorted({m.group(1) or m.group(2) for m in LOOSE.finditer(scripts[p])})
    print(p, names)
