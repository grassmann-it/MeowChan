#!/usr/bin/env python3
"""Create a deterministic, offline Mac source overlay bound to actual binary ZIPs.

Only committed application files are included. No downloads, signing or publication.
The large immutable source base is verified and referenced, never copied/repacked.
"""
from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import stat
import subprocess
import tarfile
import zipfile


INPUT_MANIFEST = "docs/native-video-macos-source-manifest.json"
RIDS = ("osx-arm64", "osx-x64")
REQUIRED = (
    INPUT_MANIFEST, "docs/native-video-macos-sources.md",
    "docs/native-video-macos-COPYING", "docs/native-video-licenses.txt",
    "docs/native-video-macos-overlay.schema.json",
    "docs/native-video-source-manifest.json", "THIRD-PARTY-NOTICES.md", "LICENSE",
    "scripts/package-macos.py", "scripts/macos-signing.py", "scripts/check-desktop-package.py",
    "scripts/package-macos-sources.py", "global.json", "MeowChan.csproj",
    "packages.release.osx-arm64.lock.json", "packages.release.osx-x64.lock.json",
    "scripts/native-video/package-nuget-mac.sh", "scripts/native-video/README.md",
)


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def file_record(path: Path) -> dict:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return {"file": path.name, "bytes": path.stat().st_size, "sha256": digest.hexdigest()}


def json_bytes(value: object) -> bytes:
    return (json.dumps(value, sort_keys=True, indent=2, ensure_ascii=False) + "\n").encode()


def assignments(values: list[str]) -> dict[str, Path]:
    result = {}
    for value in values:
        rid, separator, name = value.partition("=")
        if not separator or rid not in RIDS or not name or rid in result:
            raise ValueError("Expected one RID=PATH per selected architecture.")
        result[rid] = Path(name)
    return result


def committed_sources(repository: Path, commit: str) -> tuple[str, bytes, dict[str, bytes]]:
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Supply an exact 40-character committed source SHA.")
    exact = subprocess.check_output(
        ["git", "-C", str(repository), "rev-parse", "--verify", commit + "^{commit}"], text=True
    ).strip()
    archive = subprocess.check_output(["git", "-C", str(repository), "archive", "--format=tar", exact])
    files = {}
    with tarfile.open(fileobj=io.BytesIO(archive)) as source:
        for member in source:
            path = PurePosixPath(member.name)
            if path.is_absolute() or ".." in path.parts:
                raise ValueError("Unsafe committed source path.")
            if member.isfile():
                files[member.name] = source.extractfile(member).read()
    missing = sorted(set(REQUIRED) - files.keys())
    if missing:
        raise ValueError("Source commit lacks required source/notices: " + ", ".join(missing))
    return exact, archive, files


def inventory_zip(path: Path, rid: str, inputs: dict, version: str) -> dict:
    """Hash the final ZIP itself and each original native path after packaging/signing."""
    prefix = f"MeowChan.app/Contents/Resources/application/libvlc/{rid}/"
    expected = {row["path"]: row for row in inputs["architectures"][rid]["original_files"]}
    rows = []
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError("Duplicate ZIP paths are not allowed.")
        for name in names:
            posix = PurePosixPath(name)
            if posix.is_absolute() or ".." in posix.parts:
                raise ValueError("Unsafe ZIP path.")
        import plistlib
        metadata = plistlib.loads(archive.read("MeowChan.app/Contents/Info.plist"))
        if metadata.get("CFBundleShortVersionString") != version:
            raise ValueError("ZIP bundle version does not match --version.")
        for other in RIDS:
            if other != rid and any(f"/libvlc/{other}/" in name for name in names):
                raise ValueError("ZIP includes the other native architecture.")
        actual = {name[len(prefix):] for name in names if name.startswith(prefix) and not name.endswith("/")}
        if actual != expected.keys():
            raise ValueError(f"{rid}: native path set differs: missing={sorted(expected.keys() - actual)}, extra={sorted(actual - expected.keys())}")
        for relative, original in sorted(expected.items()):
            name = prefix + relative
            info = archive.getinfo(name)
            if stat.S_ISLNK(info.external_attr >> 16):
                raise ValueError("Native source inventory requires regular ZIP payload files.")
            data = archive.read(info)
            modified = sha256(data)
            # Packaging only rewrites/signs Mach-O libraries. Data must stay exact.
            if not relative.endswith(".dylib") and modified != original["sha256"]:
                raise ValueError("Unexpected modified native data file: " + name)
            rows.append({"original_path": relative, "original_sha256": original["sha256"],
                         "original_bytes": original["bytes"], "official_app_path": original["official_app_path"],
                         "zip_path": name, "packaged_sha256": modified, "packaged_bytes": len(data),
                         "bytes_changed": modified != original["sha256"]})
        for notice in ("docs/native-video-macos-sources.md", "docs/native-video-macos-COPYING", INPUT_MANIFEST,
                       "docs/native-video-licenses.txt", "THIRD-PARTY-NOTICES.md"):
            name = "MeowChan.app/Contents/Resources/application/" + notice
            if name not in names:
                raise ValueError("Binary ZIP lacks required Mac notices: " + name)
    return {"rid": rid, "signed_zip": file_record(path), "native_files": rows,
            "signature_validation": "Not inferred from hashing; see separately supplied native validation evidence.",
            "native_changes": "Selected LC_LOAD_DYLIB @rpath/libvlccore.dylib references repaired to relative @loader_path; Mach-O ad-hoc signatures replaced. No VLC source changes or plugin removals."}


def generate(repository: Path, commit: str, base: Path, binaries: dict[str, Path],
             reports: dict[str, Path], version: str, output: Path) -> dict:
    if not re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)", version):
        raise ValueError("Version must have three numeric components.")
    if not binaries or not reports.keys() <= binaries.keys():
        raise ValueError("Supply binary ZIPs; validation reports must refer to selected RIDs.")
    exact, application_archive, sources = committed_sources(repository, commit)
    inputs = json.loads(sources[INPUT_MANIFEST])
    base_record = file_record(base)
    if any(base_record[key] != inputs["source_base"][key] for key in ("bytes", "sha256")):
        raise ValueError("Immutable source base size/hash mismatch.")
    entries = {"application-source.tar": application_archive}
    for name in REQUIRED:
        entries["release-source/" + name] = sources[name]
    inventories = {}
    for rid, binary in sorted(binaries.items()):
        inventory = inventory_zip(binary, rid, inputs, version)
        # Ensure the binary's source notices are the exact committed ones offered here.
        with zipfile.ZipFile(binary) as archive:
            for name in (INPUT_MANIFEST, "docs/native-video-macos-sources.md", "docs/native-video-macos-COPYING",
                         "docs/native-video-licenses.txt", "THIRD-PARTY-NOTICES.md"):
                if archive.read("MeowChan.app/Contents/Resources/application/" + name) != sources[name]:
                    raise ValueError("Binary notice differs from source commit: " + name)
        if rid in reports:
            report = reports[rid].read_bytes()
            json.loads(report)  # Require structured evidence, retain without interpreting its claims.
            report_name = f"binary-inventories/{rid}-native-validation.json"
            entries[report_name] = report
            inventory["native_validation_report"] = {"path": report_name, "sha256": sha256(report)}
        inventories[rid] = inventory
        entries[f"binary-inventories/{rid}.json"] = json_bytes(inventory)
    manifest = {"schema": "meowchan.macos-source-overlay.v1", "version": version,
                "application_source_commit": exact, "application_source_archive_sha256": sha256(application_archive),
                "source_base": inputs["source_base"], "source_input_manifest_sha256": sha256(sources[INPUT_MANIFEST]),
                "architectures": {rid: {"signed_zip": value["signed_zip"], "inventory": f"binary-inventories/{rid}.json"}
                                  for rid, value in inventories.items()},
                "application_source_files": {name: sha256(data) for name, data in sorted(sources.items())},
                "source_coverage": inputs["coverage"],
                "rebuild_provenance_uncertainties": inputs["rebuild_provenance_uncertainties"],
                "scope": "Corresponding-source packaging record; no legal certification, native signature verification, playback acceptance or public release claim."}
    entries["manifest.json"] = json_bytes(manifest)
    entries["README.txt"] = (f"MeowChan {version} macOS source overlay\n\n"
        f"Application source commit: {exact}\n"
        f"Use together with unchanged {inputs['source_base']['file']}\n"
        f"SHA256 {inputs['source_base']['sha256']}\n"
        "Both source assets must be available alongside the matching binary ZIPs.\n"
        "Extract application-source.tar into a separate source directory.\n"
        "Read release-source/docs/native-video-macos-sources.md for build/replacement\n"
        "instructions and per-component notices. manifest.json binds the source commit\n"
        "and base to exact per-RID ZIP hashes and native original-to-packaged inventories.\n"
        "Hashing signed binaries does not verify Apple signatures. Native validation\n"
        "evidence, when supplied, is retained separately without expanding its claims.\n").encode()
    entries["SHA256SUMS"] = "".join(f"{sha256(data)}  {name}\n" for name, data in sorted(entries.items())).encode()
    if output.exists():
        raise ValueError("Output already exists; choose a new output path.")
    output.parent.mkdir(parents=True, exist_ok=True)
    # All metadata, member order, gzip timestamp and gzip filename are fixed.
    with output.open("xb") as target:
        with gzip.GzipFile(filename="", fileobj=target, mode="wb", mtime=0) as compressed:
            with tarfile.open(fileobj=compressed, mode="w", format=tarfile.PAX_FORMAT) as archive:
                for name, data in sorted(entries.items()):
                    member = tarfile.TarInfo(name)
                    member.size, member.mode, member.mtime = len(data), 0o644, 0
                    archive.addfile(member, io.BytesIO(data))
    result = file_record(output)
    result["application_source_commit"] = exact
    result["architectures"] = list(sorted(binaries))
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--source-commit", required=True)
    parser.add_argument("--base", required=True, type=Path)
    parser.add_argument("--binary-zip", required=True, action="append", metavar="RID=PATH")
    parser.add_argument("--validation-report", action="append", default=[], metavar="RID=PATH")
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    try:
        result = generate(args.repository, args.source_commit, args.base, assignments(args.binary_zip),
                          assignments(args.validation_report), args.version, args.output)
    except (ValueError, OSError, KeyError, zipfile.BadZipFile, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Source overlay failed: {error}\n")
    print(json.dumps(result, sort_keys=True, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
