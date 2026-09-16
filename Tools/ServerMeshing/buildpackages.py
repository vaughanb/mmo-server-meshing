"""Build the Server Meshing .unitypackage files without the Unity editor.

Mirrors Server Meshing > Build Packages (ServerMeshPackageBuilder.cs):

  Build/Packages/ServerMeshing-<version>.unitypackage          Assets/ServerMeshing/Mesh (runtime + Editor), README.md,
                                                               Documentation/{SETUP.md, DESIGN.md} (snapshot of Docs/ServerMeshing)
  Build/Packages/ServerMeshing-TestRig-<version>.unitypackage  Assets/ServerMeshing/TestMap plus TestMap/Tools/*.py
                                                               (snapshot of Tools/ServerMeshing)

A .unitypackage is a gzip tar with one directory per asset named by its guid, holding `pathname`, `asset.meta` and,
for files, `asset`. Existing .meta files are used as they are; snapshot files get deterministic generated metas.

Usage: python Tools/ServerMeshing/buildpackages.py [--version 0.58.0] [--out Build/Packages]
"""
import argparse, io, os, re, sys, tarfile, time, uuid

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ADDON = "Assets/ServerMeshing"


def read_version():
    src = open(os.path.join(ROOT, ADDON, "Mesh", "Editor", "ServerMeshPackageBuilder.cs"), encoding="utf-8").read()
    m = re.search(r'Version = "([^"]+)"', src)
    return m.group(1) if m else "0.0.0"


def guid_of(meta_text):
    m = re.search(r"^guid: ([0-9a-f]{32})", meta_text, re.M)
    return m.group(1) if m else None


def generated_meta(asset_path, is_folder):
    guid = uuid.uuid5(uuid.NAMESPACE_URL, "servermeshing/" + asset_path).hex
    if is_folder:
        return guid, f"fileFormatVersion: 2\nguid: {guid}\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
    if asset_path.lower().endswith(".md"):
        return guid, f"fileFormatVersion: 2\nguid: {guid}\nTextScriptImporter:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
    return guid, f"fileFormatVersion: 2\nguid: {guid}\nDefaultImporter:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"


class Package:
    def __init__(self, path):
        self.tar = tarfile.open(path, "w:gz")
        self.seen = set()

    def _add_bytes(self, name, data):
        info = tarfile.TarInfo(name)
        info.size = len(data)
        info.mtime = int(time.time())
        info.mode = 0o644
        self.tar.addfile(info, io.BytesIO(data))

    def add(self, asset_path, file_bytes, meta_text, is_folder):
        guid = guid_of(meta_text)
        if guid in self.seen:
            return
        self.seen.add(guid)
        self._add_bytes(f"{guid}/pathname", (asset_path + "\n").encode("utf-8"))
        self._add_bytes(f"{guid}/asset.meta", meta_text.encode("utf-8"))
        if not is_folder:
            self._add_bytes(f"{guid}/asset", file_bytes)

    def add_tree(self, asset_dir):
        """Every folder and file under an Assets path that has a .meta next to it (the repository's own assets)."""
        base = os.path.join(ROOT, asset_dir)
        self.add_existing(asset_dir)
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames[:] = sorted(d for d in dirnames if not d.endswith("~") and not d.startswith("."))
            for d in dirnames:
                self.add_existing(os.path.relpath(os.path.join(dirpath, d), ROOT).replace("\\", "/"))
            for f in sorted(filenames):
                if f.endswith(".meta"):
                    continue
                self.add_existing(os.path.relpath(os.path.join(dirpath, f), ROOT).replace("\\", "/"))

    def add_existing(self, asset_path):
        full = os.path.join(ROOT, asset_path)
        meta = full + ".meta"
        if not os.path.exists(meta):
            raise SystemExit(f"missing meta for {asset_path}")
        is_folder = os.path.isdir(full)
        self.add(asset_path, None if is_folder else open(full, "rb").read(), open(meta, encoding="utf-8").read(), is_folder)

    def add_snapshot(self, folder_asset_path, files):
        """Files copied from outside Assets (docs, tools) under a generated folder, with generated metas."""
        guid, meta = generated_meta(folder_asset_path, True)
        self.add(folder_asset_path, None, meta, True)
        for name, source in files:
            asset_path = folder_asset_path + "/" + name
            guid, meta = generated_meta(asset_path, False)
            self.add(asset_path, open(source, "rb").read(), meta, False)

    def close(self):
        self.tar.close()


def build_addon(out_dir, version):
    path = os.path.join(out_dir, f"ServerMeshing-{version}.unitypackage")
    p = Package(path)
    p.add_existing(ADDON)
    p.add_tree(ADDON + "/Mesh")
    p.add_existing(ADDON + "/README.md")
    docs = os.path.join(ROOT, "Docs", "ServerMeshing")
    p.add_snapshot(ADDON + "/Documentation", [(n, os.path.join(docs, n)) for n in ("SETUP.md", "DESIGN.md")])
    p.close()
    return path


def build_rig(out_dir, version):
    path = os.path.join(out_dir, f"ServerMeshing-TestRig-{version}.unitypackage")
    p = Package(path)
    p.add_existing(ADDON)
    p.add_tree(ADDON + "/TestMap")
    tools = os.path.join(ROOT, "Tools", "ServerMeshing")
    p.add_snapshot(ADDON + "/TestMap/Tools", [(n, os.path.join(tools, n)) for n in sorted(os.listdir(tools)) if n.endswith(".py") and n != "buildpackages.py"])
    p.close()
    return path


def list_package(path):
    with tarfile.open(path, "r:gz") as t:
        return sorted(t.extractfile(m).read().decode("utf-8").strip() for m in t.getmembers() if m.name.endswith("/pathname"))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--version", default=read_version())
    ap.add_argument("--out", default=os.path.join("Build", "Packages"))
    args = ap.parse_args()
    out_dir = os.path.join(ROOT, args.out)
    os.makedirs(out_dir, exist_ok=True)
    for path in (build_addon(out_dir, args.version), build_rig(out_dir, args.version)):
        paths = list_package(path)
        outside = [x for x in paths if not x.startswith(ADDON)]
        print(f"{os.path.relpath(path, ROOT)}: {os.path.getsize(path) // 1024} KB, {len(paths)} entries, {len(outside)} outside {ADDON}")
        for x in paths:
            print("   ", x)
    return 0


if __name__ == "__main__":
    sys.exit(main())
