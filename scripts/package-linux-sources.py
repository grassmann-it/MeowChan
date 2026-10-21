#!/usr/bin/env python3
"""Collect exact Ubuntu sources and pinned static AppImage-runtime sources in CI.

The existing Windows/Mac source base is neither downloaded nor repacked here.
This collector needs deb-src indices for each installed source package version.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile

SPEC = importlib.util.spec_from_file_location("linux_packaging_sources_common", Path(__file__).with_name("package-linux.py"))
common = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(common)

RUNTIME_INPUTS = (
    {"file": "type2-runtime-" + common.RUNTIME_COMMIT + ".tar.gz",
     "url": "https://codeload.github.com/AppImage/type2-runtime/tar.gz/" + common.RUNTIME_COMMIT,
     "commit": common.RUNTIME_COMMIT, "kind": "runtime-application-and-build-recipes"},
    {"file": "fuse-3.15.0.tar.xz", "url": "https://github.com/libfuse/libfuse/releases/download/fuse-3.15.0/fuse-3.15.0.tar.xz",
     "sha256": "70589cfd5e1cff7ccd6ac91c86c01be340b227285c5e200baa284e401eea2ca0", "kind": "static-LGPL-library"},
    {"file": "squashfuse-0.5.2.tar.gz", "url": "https://github.com/vasi/squashfuse/archive/0.5.2.tar.gz",
     "sha256": "db0238c5981dabbd80ee09ae15387f390091668ca060a7bc38047912491443d3", "kind": "static-runtime-library"},
)
BINDING_INPUT = {
    "file": "libvlcsharp-3.10.2.tar.gz",
    "url": "https://code.videolan.org/videolan/LibVLCSharp/-/archive/3.10.2/libvlcsharp-3.10.2.tar.gz",
    "sha256": "262bc5bbaadc127ded92c2e7155c4f07c0c4b1cfedb5b4f02d53dba93a0b6878",
    "kind": "LGPL-managed-binding-and-Avalonia-adapter",
}


def assignments(values: list[str]) -> dict[str, Path]:
    result = {}
    for value in values:
        rid, separator, filename = value.partition("=")
        if not separator or rid not in common.RIDS or rid in result or not filename:
            raise ValueError("Expected unique linux-x64|linux-arm64=runtime-manifest.json assignments.")
        result[rid] = Path(filename)
    return result


def dsc_files(path: Path, source: str, version: str) -> dict[str, str]:
    text = path.read_text()
    if not re.search(r"^Source: " + re.escape(source) + r"$", text, re.M) or not re.search(r"^Version: " + re.escape(version) + r"$", text, re.M):
        raise ValueError("Downloaded .dsc does not match the exact installed source version.")
    section = re.search(r"^Checksums-Sha256:\n((?:[ \t].*\n)+)", text, re.M)
    if not section:
        raise ValueError("Source descriptor lacks SHA-256 checksums.")
    files = {}
    for line in section[1].splitlines():
        checksum, size, filename = line.split()
        if not re.fullmatch(r"[0-9a-f]{64}", checksum) or not size.isdecimal() or Path(filename).name != filename or filename in (".", ".."):
            raise ValueError("Unsafe or malformed .dsc source entry.")
        item = path.parent / filename
        if not item.is_file() or item.stat().st_size != int(size) or common.digest(item) != checksum:
            raise ValueError(f"Source input checksum/size mismatch: {filename}")
        files[filename] = checksum
    if not files:
        raise ValueError("Source descriptor is empty.")
    files[path.name] = common.digest(path)
    return files


def source_packages(manifests: dict[str, Path]) -> tuple[dict, dict]:
    selected = {}
    metadata = {}
    for rid, path in manifests.items():
        data = json.loads(path.read_text())
        if data.get("schema") != "meowchan.linux-runtime.v1" or data.get("runtime") != rid:
            raise ValueError("Source inventory RID/schema mismatch.")
        metadata[rid] = {"sha256": common.digest(path), "native_runtime_files": data["files"]}
        for package in data["packages"]:
            name, version = package["source"], package["source_version"]
            if not re.fullmatch(r"[a-z0-9][a-z0-9+.-]*", name) or not re.fullmatch(r"[A-Za-z0-9.+:~\-]+", version):
                raise ValueError("Unsafe distro source package/version.")
            selected[(name, version)] = {"source": name, "version": version}
    return selected, metadata


def collect(manifests: dict[str, Path], output: Path, cache: Path, commit: str) -> dict:
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Source binding requires an exact committed SHA.")
    if output.exists():
        raise ValueError("Source output must be fresh.")
    selected, metadata = source_packages(manifests)
    cache.mkdir(parents=True, exist_ok=True)
    output.parent.mkdir(parents=True, exist_ok=True)
    records = []
    # Archive existing downloaded source inputs by streaming from cache; no second
    # multi-gigabyte copy of the source tree is staged.
    import gzip
    with output.open("xb") as raw, gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as compressed, tarfile.open(fileobj=compressed, mode="w|") as archive:
        def add(path: Path, name: str) -> None:
            # gzip can be slightly larger than incompressible source inputs.
            common.reserve(output, path.stat().st_size + 1024 * 1024)
            info = archive.gettarinfo(str(path), arcname=name)
            info.uid = info.gid = 0
            info.uname = info.gname = ""
            info.mtime = 0
            info.mode = 0o644
            with path.open("rb") as stream:
                archive.addfile(info, stream)

        for (name, version), record in sorted(selected.items()):
            # Versions can include epochs; the digest gives a short, portable cache key.
            import hashlib
            folder = cache / (name + "-" + hashlib.sha256(version.encode()).hexdigest()[:12])
            # Never delete/reuse pre-existing caller cache contents.
            folder.mkdir()
            common.reserve(cache, 512 * 1024 ** 2)
            command = ["apt-get", "source", "--download-only", "--only-source", "--yes", f"{name}={version}"]
            uris = common.run(*command[:2], "--print-uris", *command[2:], cwd=folder)
            sizes = re.findall(r"^'[^']+'\s+\S+\s+(\d+)\s+\S+\s*$", uris, re.M)
            if not sizes:
                raise ValueError(f"Could not bound exact source download size for {name}={version}.")
            common.reserve(cache, sum(map(int, sizes)) * 2 + 16 * 1024 ** 2)
            log = common.run(*command, cwd=folder)
            (folder / "download.log").write_text(log)
            descriptors = list(folder.glob("*.dsc"))
            if len(descriptors) != 1:
                raise ValueError(f"Expected one exact source descriptor for {name}={version}.")
            files = dsc_files(descriptors[0], name, version)
            for filename in files:
                add(folder / filename, "distro/" + folder.name + "/" + filename)
            add(folder / "download.log", "distro/" + folder.name + "/download.log")
            records.append({**record, "inputs": files, "directory": "distro/" + folder.name, "fetch_command": command})
            # A source shared by other binaries/architectures is already archived.
            # Remove only this collector-owned cache folder to bound CI disk use.
            shutil.rmtree(folder)

        runtime_records = []
        for item in (*RUNTIME_INPUTS, BINDING_INPUT):
            path = cache / item["file"]
            common.reserve(cache, 32 * 1024 ** 2)
            common.download(item["url"], path, item.get("sha256"))
            if item.get("commit"):
                with tarfile.open(path) as source_archive:
                    expected = "type2-runtime-" + common.RUNTIME_COMMIT + "/"
                    if any(not (member.name + "/").startswith(expected) for member in source_archive):
                        raise ValueError("Runtime archive does not match its pinned source tree.")
            prefix = "managed-binding/" if item == BINDING_INPUT else "appimage-runtime/"
            add(path, prefix + path.name)
            record = {**item, "sha256": common.digest(path), "bytes": path.stat().st_size}
            if item == BINDING_INPUT:
                binding_record = record
            else:
                runtime_records.append(record)
        repository = Path(__file__).resolve().parents[1]
        for relative in ("scripts/package-linux.py", "scripts/package-linux-sources.py", "scripts/check-video-native.py", "docs/native-video-appimage-runtime-LICENSE", "docs/native-video-linux-sources.md"):
            add(repository / relative, "packaging/" + relative)
        for rid, path in manifests.items():
            add(path, "inventories/" + rid + "-runtime-manifest.json")
            licenses = path.parent / "licenses"
            for license_file in sorted(licenses.rglob("*")):
                if license_file.is_file():
                    add(license_file, "licenses/" + rid + "/" + license_file.relative_to(licenses).as_posix())
        status = {
            "distro": "Exact source versions, original archives and distro patches verified against their .dsc SHA-256 inventories.",
            "runtime": "Pinned MIT runtime application and build/patch recipes; exact recipe-pinned LGPL libfuse3.15.0 and squashfuse0.5.2 sources included for relinking.",
            "permissive_build_dependencies": "Upstream builds with Alpine3.21 musl/zlib/zstd packages; exact historic apk revisions are not asserted. The pinned recipes retain the upstream build environment/instructions. No bit-identical rebuild claim.",
            "base": "Independent Linux source companion; does not rely on the Windows/Mac VLC source base.",
            "managed_binding": "Exact LibVLCSharp3.10.2 core and Avalonia-adapter source archive included independently of the larger Windows/Mac source base.",
        }
        manifest = {"schema": "meowchan.linux-sources.v1", "source_commit": commit, "runtimes": metadata,
                    "distro_sources": records, "appimage_runtime": runtime_records,
                    "managed_binding": binding_record, "source_status": status}
        with tempfile.TemporaryDirectory(prefix="linux-source-manifest-", dir=cache) as temporary:
            final = Path(temporary) / "manifest.json"
            common.write_json(final, manifest)
            add(final, "manifest.json")
    common.write_json(output.with_name(output.name + ".json"), {
        "schema": "meowchan.linux-source-artifact.v1", "source_commit": commit,
        "file": output.name, "sha256": common.digest(output), "bytes": output.stat().st_size,
        "runtimes": sorted(manifests), "source_status": status})
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", action="append", required=True, help="RID=runtime-manifest.json; repeat for both architectures")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--download-dir", type=Path, required=True)
    parser.add_argument("--source-commit", required=True)
    args = parser.parse_args()
    result = collect(assignments(args.manifest), args.output, args.download_dir, args.source_commit)
    print(json.dumps({"output": str(args.output), "distro_sources": len(result["distro_sources"])}))


if __name__ == "__main__":
    main()
