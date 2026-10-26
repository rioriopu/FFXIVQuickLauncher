#!/usr/bin/env python3
"""公式ハッシュで検証してから、Dalamud の依存ファイルをミラーへ公開する。"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import tempfile
import urllib.request
from urllib.parse import quote
import zipfile

BASE = "https://kamori.goats.dev/Dalamud"


def fetch(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "EstellDependencyMirror/1"}), timeout=60) as response:
        return response.read()


def load_json(data):
    return json.loads(data)


def child(root, name):
    relative = PurePosixPath(name.replace("\\", "/"))
    if relative.is_absolute() or ".." in relative.parts or ":" in name:
        raise ValueError("保存先の外を指すファイル名です")
    target = root.joinpath(*relative.parts)
    if not target.resolve().is_relative_to(root.resolve()):
        raise ValueError("保存先の外には書き込めません")
    return target


def extract(archive, root):
    with zipfile.ZipFile(archive) as package:
        for entry in package.infolist():
            target = child(root, entry.filename)
            if entry.is_dir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with package.open(entry) as source, target.open("wb") as output:
                    shutil.copyfileobj(source, output)


def digest(path, algorithm):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, algorithm).hexdigest()


def verify(root, entries, algorithm):
    if not entries:
        raise ValueError("検証用のファイル一覧が空です")
    for name, expected in entries.items():
        path = child(root, name)
        if not path.is_file() or (expected and digest(path, algorithm).lower() != expected.lower()):
            raise ValueError(f"公式ハッシュと一致しません: {name}")


def atomic_file(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as stream:
        temporary = Path(stream.name)
        stream.write(data)
    try:
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def publish_tree(source, target, work):
    target.parent.mkdir(parents=True, exist_ok=True)
    backup = work / (target.name + "-previous")
    if target.exists():
        os.replace(target, backup)
    try:
        os.replace(source, target)
    except Exception:
        if backup.exists():
            os.replace(backup, target)
        raise


def sync_runtime(root, version, work):
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("ランタイム版が不正です")
    hashes_data = fetch(f"{BASE}/Release/Runtime/Hashes/{version}")
    hashes = load_json(hashes_data)
    if not isinstance(hashes, dict) or not hashes:
        raise ValueError("ランタイムのハッシュ一覧が空です")
    target = root / "runtime" / version
    try:
        manifest = load_json((target / "manifest.json").read_bytes())
        if set(manifest["sha256"]) == {"dotnet.zip", "windowsdesktop.zip"} and load_json((target / "hashes.json").read_bytes()) == hashes and all(
                digest(target / name, "sha256") == value for name, value in manifest["sha256"].items()):
            print(f"runtime {version}: 検証済みキャッシュを使用", flush=True)
            return
    except (OSError, ValueError, KeyError):
        pass
    stage = work / ("runtime-" + version)
    stage.mkdir()
    unpacked = work / ("runtime-unpacked-" + version)
    unpacked.mkdir()
    checksums = {}
    for component, name in (("DotNet", "dotnet.zip"), ("WindowsDesktop", "windowsdesktop.zip")):
        archive = stage / name
        archive.write_bytes(fetch(f"{BASE}/Release/Runtime/{component}/{version}"))
        extract(archive, unpacked)
        checksums[name] = digest(archive, "sha256")
    (unpacked / "version").write_text(version, encoding="utf-8")
    verify(unpacked, hashes, "md5")
    (stage / "hashes.json").write_text(json.dumps(hashes), encoding="utf-8")
    (stage / "manifest.json").write_text(json.dumps({"version": version, "sha256": checksums}), encoding="utf-8")
    publish_tree(stage, target, work)
    print(f"runtime {version}: 公式ハッシュ{len(hashes)}件を検証して公開", flush=True)


def sync_assets(root, public_base, work):
    data = fetch(f"{BASE}/Asset/Meta")
    original = load_json(data)
    version = original["version"]
    if not isinstance(version, int) or version <= 0 or not original["assets"]:
        raise ValueError("アセット一覧が不正です")
    expected = {entry["fileName"]: entry.get("hash") for entry in original["assets"]}
    target = root / "assets" / str(version)
    cached = False
    try:
        old = load_json((target / "upstream-meta.json").read_bytes())
        manifest = load_json((target / "manifest.json").read_bytes())
        cached = old == original and digest(target / "package.zip", "sha256") == manifest["sha256"]
        if cached:
            verify(target / "files", expected, "sha1")
    except (OSError, ValueError, KeyError):
        cached = False
    if not cached:
        stage = work / ("assets-" + str(version))
        stage.mkdir()
        archive = stage / "package.zip"
        archive.write_bytes(fetch(original["packageUrl"]))
        extract(archive, stage / "files")
        verify(stage / "files", expected, "sha1")
        (stage / "upstream-meta.json").write_bytes(data)
        (stage / "manifest.json").write_text(json.dumps({"version": version, "sha256": digest(archive, "sha256")}), encoding="utf-8")
        mirrored = dict(original)
        mirrored["originalPackageUrl"] = original["packageUrl"]
        mirrored["packageUrl"] = f"{public_base}/assets/{version}/package.zip"
        mirrored["assets"] = [dict(entry, url=f"{public_base}/assets/{version}/files/{quote(entry['fileName'])}") for entry in original["assets"]]
        (stage / "meta.json").write_text(json.dumps(mirrored, ensure_ascii=False), encoding="utf-8")
        publish_tree(stage, target, work)
    # 一覧は版別の本体を検証・公開した後にだけ切り替える。
    atomic_file(root / "assets" / "meta.json", (target / "meta.json").read_bytes())
    print(f"assets {version}: 公式ハッシュ{len(expected)}件を検証して公開", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--public-base", default="https://estelldprereleaserepo.net/estell")
    parser.add_argument("--runtime-version", action="append", default=[])
    args = parser.parse_args()
    root = args.root.resolve()
    root.mkdir(parents=True, exist_ok=True)
    # 静的公開先の外に作業領域を置き、同時同期を防ぐ。
    work_root = root.parent / "estell-dependency-work"
    work_root.mkdir(exist_ok=True)
    with (work_root / "sync.lock").open("w") as lock:
        if os.name == "posix":
            import fcntl
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        versions = set(args.runtime_version)
        for track in ("release", "stg"):
            try:
                metadata = load_json(fetch(f"{BASE}/Release/VersionInfo?track={track}"))
                value = metadata.get("RuntimeVersion", metadata.get("runtimeVersion"))
                if value:
                    versions.add(value)
            except Exception as ex:
                print(f"{track}: 最新版の照会に失敗、保存済み配布情報を使用: {ex}", flush=True)
        for path in [root / "dalamud" / "version", *root.glob("kamori/Release/VersionInfo.*.json")]:
            try:
                metadata = load_json(path.read_bytes())
                value = metadata.get("RuntimeVersion", metadata.get("runtimeVersion"))
                if value:
                    versions.add(value)
            except (OSError, ValueError):
                pass
        failures = []
        with tempfile.TemporaryDirectory(prefix="sync-", dir=work_root) as temporary:
            work = Path(temporary)
            for version in sorted(versions):
                try:
                    sync_runtime(root, version, work)
                except Exception as ex:
                    failures.append(f"runtime {version}: {ex}")
            try:
                sync_assets(root, args.public_base.rstrip("/"), work)
            except Exception as ex:
                failures.append(f"assets: {ex}")
        if failures:
            for failure in failures:
                print(failure, flush=True)
            return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
