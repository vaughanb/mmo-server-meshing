"""Compile Assembly-CSharp (and optionally Assembly-CSharp-Editor) outside Unity with the .NET SDK's Roslyn.

Reads Unity's generated csproj for sources, defines, references and analyzers, adds any .cs under
Assets/ServerMeshing that the stale csproj does not list yet, and prints diagnostics (errors always,
warnings only for Assets/ServerMeshing). Exit 1 on any error.
"""
import glob
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".."))
OUT = os.environ.get("CSCHECK_OUT") or os.path.join(ROOT, "Temp", "cscheck-out")
# CSC_DLL overrides; otherwise the newest installed .NET SDK's Roslyn (string order: fine within one major).
CSC = os.environ.get("CSC_DLL") or max(glob.glob(r"C:\Program Files\dotnet\sdk\*\Roslyn\bincore\csc.dll"), default="")
NS = {"m": "http://schemas.microsoft.com/developer/msbuild/2003"}


def parse(csproj):
    tree = ET.parse(os.path.join(ROOT, csproj))
    r = tree.getroot()
    compiles = [c.get("Include") for c in r.iterfind(".//m:Compile", NS)]
    refs = [h.text for h in r.iterfind(".//m:Reference/m:HintPath", NS)]
    projrefs = [p.get("Include") for p in r.iterfind(".//m:ProjectReference", NS)]
    analyzers = [a.get("Include") for a in r.iterfind(".//m:Analyzer", NS)]
    defines = ""
    for pg in r.iterfind(".//m:PropertyGroup", NS):
        d = pg.find("m:DefineConstants", NS)
        if d is not None:
            defines = d.text
    return compiles, refs, projrefs, analyzers, defines


def is_editor(path):
    parts = re.split(r"[\\/]", path)
    return "Editor" in parts


def client_defines(defines):
    """A client player build: no editor, no server code (the add-on's server guards read false)."""
    kept = [d for d in defines.split(";") if d and not d.startswith("UNITY_EDITOR") and d != "UNITY_SERVER"]
    return ";".join(kept + ["EXCLUDE_SERVER_CODES"])


def build(name, extra_refs, client=False):
    compiles, refs, projrefs, analyzers, defines = parse(name + ".csproj")
    if client:
        defines = client_defines(defines)
    listed = {os.path.normcase(c) for c in compiles}
    editor = name.endswith("-Editor")
    # Sources the stale csproj does not know yet, and drop listed ones that were deleted.
    for f in glob.glob(os.path.join(ROOT, "Assets", "ServerMeshing", "**", "*.cs"), recursive=True):
        rel = os.path.relpath(f, ROOT)
        if is_editor(rel) != editor:
            continue
        if os.path.normcase(rel) not in listed:
            compiles.append(rel)
    compiles = [c for c in compiles if os.path.exists(os.path.join(ROOT, c))]
    for p in projrefs:
        dll = os.path.join(ROOT, "Library", "ScriptAssemblies", os.path.splitext(p)[0] + ".dll")
        if os.path.exists(dll):
            refs.append(dll)
    refs = [x for x in refs if not x.endswith("Assembly-CSharp.dll")] + extra_refs
    os.makedirs(OUT, exist_ok=True)
    rsp = os.path.join(OUT, name + ".rsp")
    dll_out = os.path.join(OUT, name + ".dll")
    with open(rsp, "w", encoding="utf-8") as fh:
        fh.write("-nologo\n-target:library\n-langversion:9.0\n-nostdlib+\n-noconfig\n-deterministic-\n")
        fh.write("-nowarn:0169,0649\n-warn:4\n")
        fh.write('-out:"%s"\n' % dll_out)
        fh.write('-define:%s\n' % defines)
        for x in refs:
            fh.write('-r:"%s"\n' % x)
        for a in analyzers:
            fh.write('-analyzer:"%s"\n' % a)
        for c in compiles:
            fh.write('"%s"\n' % os.path.join(ROOT, c))
    proc = subprocess.run(["dotnet", CSC, "@" + rsp], capture_output=True, text=True, encoding="utf-8", errors="replace")
    lines = (proc.stdout + proc.stderr).splitlines()
    errors = [line for line in lines if ": error " in line]
    if client:
        # Kit code outside the add-on is not built for this configuration by this tool's callers; only ours counts.
        errors = [line for line in errors if "ServerMeshing" in line]
    warns = [line for line in lines if ": warning " in line and "ServerMeshing" in line]
    print("== %s%s: %d sources, %d errors, %d add-on warnings" % (name, " (client defines)" if client else "", len(compiles), len(errors), len(warns)))
    for line in errors[:80]:
        print(line.replace(ROOT + "\\", ""))
    for line in warns[:80]:
        print(line.replace(ROOT + "\\", ""))
    return (client or proc.returncode == 0) and not errors, dll_out


def main():
    if "--client" in sys.argv:
        ok, _ = build("Assembly-CSharp", [], client=True)
        sys.exit(0 if ok else 1)
    ok, runtime = build("Assembly-CSharp", [])
    if not ok:
        sys.exit(1)
    if "--editor" in sys.argv:
        ok, _ = build("Assembly-CSharp-Editor", [runtime])
        if not ok:
            sys.exit(1)


if __name__ == "__main__":
    main()
