#!/usr/bin/env python3
"""Run the package-only, offline SDK consumer gate.

The runner deliberately consumes a package supplied by the caller.  It never
packs the SDK and it never changes the checkout: the sample consumer is copied
to a new directory outside the source tree, then restored from a one-package
local feed with a fresh NuGet cache.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import base64
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import time
from typing import Any, Iterator
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parents[1]
SAMPLE_ROOT = ROOT / "samples" / "MeowChan.PluginSdk.Consumer"
SAMPLE_PROJECT = SAMPLE_ROOT / "MeowChan.PluginSdk.Consumer.csproj"
# Keep process supervision in the shared helper.  Adding its directory rather
# than duplicating its implementation also works when this file is imported by
# the lightweight tests through importlib.
if str(ROOT / "scripts") not in sys.path:
    sys.path.insert(0, str(ROOT / "scripts"))
from legacy102_process import (  # noqa: E402
    CommandFailed as ProcessCommandFailed,
    CommandTimedOut as ProcessCommandTimedOut,
    CommandUnavailable as ProcessCommandUnavailable,
    MAX_TAIL_BYTES,
    run_bounded_command,
)
PACKAGE_ID = "MeowChan.PluginSdk"
PACKAGE_ASSEMBLY = "lib/net10.0/MeowChan.PluginSdk.dll"
FULL_SHA_RE = re.compile(r"^[0-9a-f]{40}$")
DEFAULT_TIMEOUT_SECONDS = 600
MAX_TIMEOUT_SECONDS = 1800
SCHEMA_VERSION = 1
RESULT_NAME = "RESULT.json"

# The parent release lane sets this to the shared heavy lock.  The fallback is
# intentionally not a user/profile path and is only used when the lane did not
# provide one (for example by a local maintainer running the gate manually).
DEFAULT_HEAVY_LOCK = Path(os.environ["OCHAN_HEAVY_LOCK"]) if os.environ.get("OCHAN_HEAVY_LOCK") else (
    Path("/home/tomka/.cache/claude-ssd-tmp/heavy.lock") if os.name != "nt"
    else Path(tempfile.gettempdir()) / "meowchan-heavy.lock"
)

FORBIDDEN_PATH_PARTS = {".ochan", "authstore"}
FORBIDDEN_DB_SUFFIXES = {".db", ".sqlite", ".sqlite3"}
GENERATED_CONSUMER_DIRS = {"bin", "obj"}


class GateError(RuntimeError):
    """A preflight, isolation, command, or evidence failure."""


class DeadlineExceeded(GateError):
    """The single timeout budget, including heavy-lock wait, expired."""


class CommandFailed(GateError):
    """A restore, build, or consumer command exited unsuccessfully."""


class Deadline:
    def __init__(self, seconds: int):
        if seconds < 1 or seconds > MAX_TIMEOUT_SECONDS:
            raise GateError("--timeout-seconds must be between 1 and 1800 seconds.")
        self.started = time.monotonic()
        self.ends = self.started + seconds

    def remaining(self) -> float:
        remaining = self.ends - time.monotonic()
        if remaining <= 0:
            raise DeadlineExceeded("The total timeout budget expired.")
        return remaining


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def sha512_base64(value: bytes) -> str:
    return base64.b64encode(hashlib.sha512(value).digest()).decode("ascii")


def _normalise_absolute(value: Path | str, label: str) -> Path:
    path = Path(value)
    if not path.is_absolute():
        raise GateError(f"{label} must be an absolute path: {path}")
    # Do not resolve symlinks here.  Existing components are checked explicitly
    # so an alias cannot pass as a safe path, and an output can be checked before
    # it is created.
    return Path(os.path.abspath(os.fspath(path)))


def _path_has_forbidden_component(path: Path) -> bool:
    return any(part.casefold() in FORBIDDEN_PATH_PARTS for part in path.parts) or (
        path.suffix.casefold() in FORBIDDEN_DB_SUFFIXES
    )


def _check_existing_components(path: Path, label: str) -> None:
    """Reject profile/database paths, symlink aliases, and special files."""
    if _path_has_forbidden_component(path):
        raise GateError(f"{label} must not address ~/.oChan, authstore, or a database path: {path}")
    current = Path(path.anchor)
    for part in path.parts[1:]:
        current /= part
        try:
            info = current.lstat()
        except FileNotFoundError:
            break
        if stat.S_ISLNK(info.st_mode):
            raise GateError(f"{label} contains a symlink/reparse component: {current}")
        if not (stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode)):
            raise GateError(f"{label} contains a special path component: {current}")


def _assert_regular(path: Path, label: str) -> None:
    _check_existing_components(path, label)
    try:
        info = path.lstat()
    except FileNotFoundError as error:
        raise GateError(f"{label} does not exist: {path}") from error
    if stat.S_ISLNK(info.st_mode):
        raise GateError(f"{label} is a symlink/reparse alias: {path}")
    if not stat.S_ISREG(info.st_mode):
        raise GateError(f"{label} is not a regular file: {path}")


def _assert_directory(path: Path, label: str) -> None:
    _check_existing_components(path, label)
    try:
        info = path.lstat()
    except FileNotFoundError as error:
        raise GateError(f"{label} does not exist: {path}") from error
    if stat.S_ISLNK(info.st_mode):
        raise GateError(f"{label} is a symlink/reparse alias: {path}")
    if not stat.S_ISDIR(info.st_mode):
        raise GateError(f"{label} is not a real directory: {path}")


def _walk_files(root: Path, label: str, *, skip_generated: bool = False) -> Iterator[Path]:
    """Yield regular files while refusing aliases and special files."""
    _assert_directory(root, label)
    pending = [root]
    while pending:
        directory = pending.pop()
        with os.scandir(directory) as entries:
            for entry in sorted(entries, key=lambda item: item.name):
                if skip_generated and entry.name.casefold() in GENERATED_CONSUMER_DIRS:
                    continue
                path = Path(entry.path)
                if _path_has_forbidden_component(path):
                    raise GateError(f"{label} contains a forbidden profile/database path: {path}")
                info = entry.stat(follow_symlinks=False)
                if stat.S_ISLNK(info.st_mode):
                    raise GateError(f"{label} contains a symlink/reparse alias: {path}")
                if stat.S_ISDIR(info.st_mode):
                    pending.append(path)
                elif stat.S_ISREG(info.st_mode):
                    yield path
                else:
                    raise GateError(f"{label} contains a special file: {path}")


def inventory_tree(root: Path, label: str, *, skip_generated: bool = False) -> dict[str, Any]:
    root = _normalise_absolute(root, label)
    files = []
    for path in _walk_files(root, label, skip_generated=skip_generated):
        relative = path.relative_to(root).as_posix()
        files.append({"path": relative, "bytes": path.stat().st_size, "sha256": sha256_file(path)})
    files.sort(key=lambda item: item["path"])
    tree_payload = "\n".join(f"{item['sha256']}  {item['path']}" for item in files).encode("utf-8")
    return {
        "root": str(root),
        "files": files,
        "fileCount": len(files),
        "treeSha256": sha256_bytes(tree_payload),
    }


def _safe_archive_member(name: str) -> None:
    """Reject ZIP names that could escape an extraction/feed boundary."""
    if not name or "\x00" in name or "\\" in name:
        raise GateError(f"Package contains an unsafe archive path: {name!r}")
    path = PurePosixPath(name)
    if path.is_absolute() or name.startswith("/") or (len(name) >= 2 and name[1] == ":"):
        raise GateError(f"Package contains an absolute archive path: {name!r}")
    if any(part in {"", ".", ".."} for part in path.parts):
        raise GateError(f"Package contains an archive path traversal: {name!r}")


def _local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _child_text(element: ET.Element, name: str, label: str) -> str:
    matches = [child for child in element if _local_name(child.tag) == name]
    if len(matches) != 1 or not (matches[0].text or "").strip():
        raise GateError(f"{label} must contain exactly one non-empty {name}.")
    return (matches[0].text or "").strip()


def _metadata(root: ET.Element) -> ET.Element:
    matches = [item for item in root if _local_name(item.tag) == "metadata"]
    if len(matches) != 1:
        raise GateError("Package NuSpec must contain exactly one metadata element.")
    return matches[0]


def product_version(source_root: Path = ROOT) -> tuple[str, str]:
    """Read the product version from the repository's canonical props file."""
    props = source_root / "Directory.Build.props"
    _assert_regular(props, "Directory.Build.props")
    try:
        root = ET.fromstring(props.read_text(encoding="utf-8"))
    except (ET.ParseError, UnicodeError) as error:
        raise GateError(f"Cannot parse product props: {props}") from error
    versions = [(item.text or "").strip() for item in root.iter() if _local_name(item.tag) == "Version"]
    versions = [item for item in versions if item]
    if len(versions) != 1:
        raise GateError("Directory.Build.props must define exactly one non-empty product Version.")
    return versions[0], str(props)


def inspect_package(package: Path, expected_version: str, source_commit: str) -> dict[str, Any]:
    """Validate package identity, provenance, contents, and archive safety."""
    package = _normalise_absolute(package, "--package")
    _assert_regular(package, "--package")
    if package.suffix.casefold() != ".nupkg":
        raise GateError(f"--package must point to a .nupkg file: {package}")
    package_bytes = package.read_bytes()
    try:
        with zipfile.ZipFile(package) as archive:
            names = archive.namelist()
            if len(names) != len(set(names)):
                raise GateError("Package contains duplicate ZIP member names.")
            for name in names:
                _safe_archive_member(name)
            nuspecs = [name for name in names if name.casefold().endswith(".nuspec")]
            if len(nuspecs) != 1:
                raise GateError("Package must contain exactly one NuSpec.")
            try:
                root = ET.fromstring(archive.read(nuspecs[0]))
            except (ET.ParseError, UnicodeError) as error:
                raise GateError("Package NuSpec is not valid XML.") from error
            metadata = _metadata(root)
            package_id = _child_text(metadata, "id", "Package NuSpec")
            version = _child_text(metadata, "version", "Package NuSpec")
            if package_id != PACKAGE_ID:
                raise GateError(f"Unexpected package ID: {package_id!r}; expected {PACKAGE_ID!r}.")
            if version != expected_version:
                raise GateError(f"Package version {version!r} does not match product Version {expected_version!r}.")
            repositories = [item for item in metadata.iter() if _local_name(item.tag) == "repository"]
            if len(repositories) != 1:
                raise GateError("Package must contain exactly one repository provenance element.")
            repository = repositories[0]
            repository_commit = (repository.attrib.get("commit") or "").strip()
            if repository_commit != source_commit:
                raise GateError(
                    f"Package repository commit {repository_commit!r} does not match source pin {source_commit}."
                )
            if repository.attrib.get("type", "").casefold() != "git":
                raise GateError("Package repository provenance must declare type='git'.")
            dependencies = [
                item.attrib.get("id", "")
                for item in metadata.iter()
                if _local_name(item.tag) == "dependency"
            ]
            if dependencies:
                raise GateError(f"SDK package must have no runtime package dependencies: {dependencies}.")
            assemblies = [name for name in names if name.casefold().endswith(".dll")]
            if assemblies != [PACKAGE_ASSEMBLY]:
                raise GateError(f"Package must contain exactly {PACKAGE_ASSEMBLY}: {assemblies}.")
            assembly_bytes = archive.read(PACKAGE_ASSEMBLY)
            return {
                "id": package_id,
                "version": version,
                "repositoryCommit": repository_commit,
                "repositoryUrl": repository.attrib.get("url"),
                "sha256": sha256_bytes(package_bytes),
                "sha512": sha512_base64(package_bytes),
                "bytes": len(package_bytes),
                "archiveEntries": sorted(names),
                "assembly": PACKAGE_ASSEMBLY,
                "assemblySha256": sha256_bytes(assembly_bytes),
                "path": str(package),
            }
    except zipfile.BadZipFile as error:
        raise GateError(f"Package is not a readable ZIP archive: {package}") from error


def verify_source_pin(source_root: Path, source_commit: str) -> dict[str, Any]:
    source_root = _normalise_absolute(source_root, "source checkout")
    if not FULL_SHA_RE.fullmatch(source_commit):
        raise GateError("--source-commit must be a full lowercase 40-character SHA.")
    _assert_directory(source_root, "source checkout")

    def run_git(arguments: list[str]) -> str:
        result = subprocess.run(
            ["git", *arguments], cwd=source_root, check=False, shell=False,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
        )
        if result.returncode:
            raise GateError(f"Git metadata command failed: git {' '.join(arguments)}")
        return result.stdout.strip()

    top = Path(run_git(["rev-parse", "--show-toplevel"])).resolve()
    if top != source_root.resolve():
        raise GateError(f"Git source root does not match this runner checkout: {top}")
    head = run_git(["rev-parse", "HEAD"])
    if head != source_commit:
        raise GateError(f"Source HEAD {head} is not the requested --source-commit {source_commit}.")
    status = run_git(["status", "--porcelain=v1", "--untracked-files=all"])
    if status:
        raise GateError("Source checkout is dirty; refusing an unpinned package consumer.")
    return {"commit": source_commit, "headExact": True, "clean": True}


def inspect_consumer_project(project: Path) -> dict[str, Any]:
    """Require the supplied sample to be a PackageReference-only consumer."""
    project = _normalise_absolute(project, "consumer project")
    _assert_regular(project, "consumer project")
    try:
        tree = ET.parse(project)
    except (ET.ParseError, UnicodeError) as error:
        raise GateError(f"Consumer project is not valid XML: {project}") from error
    forbidden = []
    package_references = []
    for item in tree.iter():
        name = _local_name(item.tag)
        if name in {"ProjectReference", "Reference", "COMReference"}:
            forbidden.append(name)
        if name == "PackageReference":
            include = (item.attrib.get("Include") or item.attrib.get("Update") or "").strip()
            package_references.append({"include": include, "version": item.attrib.get("Version", "")})
    if forbidden:
        raise GateError(f"Consumer has a forbidden direct/project reference: {sorted(set(forbidden))}.")
    if len(package_references) != 1 or package_references[0]["include"].casefold() != PACKAGE_ID.casefold():
        raise GateError(f"Consumer must have exactly one PackageReference to {PACKAGE_ID}.")
    version_expression = package_references[0]["version"]
    if "MeowChanPluginSdkVersion" not in version_expression:
        raise GateError("Consumer SDK PackageReference must use the explicit MeowChanPluginSdkVersion property.")
    return {"project": str(project), "packageReferences": package_references, "packageOnly": True}


def copy_tree(source: Path, destination: Path, label: str, *, skip_generated: bool = False) -> None:
    source = _normalise_absolute(source, label)
    _assert_directory(source, label)
    if destination.exists() or os.path.lexists(destination):
        raise GateError(f"Refusing to overwrite isolated destination: {destination}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.mkdir()
    pending = [(source, destination)]
    while pending:
        current_source, current_destination = pending.pop()
        with os.scandir(current_source) as entries:
            for entry in sorted(entries, key=lambda item: item.name):
                if skip_generated and entry.name.casefold() in GENERATED_CONSUMER_DIRS:
                    continue
                source_path = Path(entry.path)
                destination_path = current_destination / entry.name
                info = entry.stat(follow_symlinks=False)
                if stat.S_ISLNK(info.st_mode):
                    raise GateError(f"{label} contains a symlink/reparse alias: {source_path}")
                if stat.S_ISDIR(info.st_mode):
                    destination_path.mkdir()
                    pending.append((source_path, destination_path))
                elif stat.S_ISREG(info.st_mode):
                    shutil.copy2(source_path, destination_path)
                else:
                    raise GateError(f"{label} contains a special file: {source_path}")


def _private_environment(root: Path) -> dict[str, str]:
    """Return an environment with no personal profile/cache inputs."""
    root = _normalise_absolute(root, "private environment root")
    root.mkdir(parents=True, exist_ok=True)
    folders = {
        "HOME": root / "home",
        "TMPDIR": root / "tmp",
        "TEMP": root / "tmp",
        "TMP": root / "tmp",
        "DOTNET_CLI_HOME": root / "dotnet-home",
        "XDG_CONFIG_HOME": root / "xdg-config",
        "XDG_CACHE_HOME": root / "xdg-cache",
        "XDG_DATA_HOME": root / "xdg-data",
        "NUGET_PACKAGES": root / "nuget-packages",
        "NUGET_HTTP_CACHE_PATH": root / "nuget-http-cache",
        "NUGET_PLUGINS_CACHE_PATH": root / "nuget-plugins-cache",
    }
    for path in folders.values():
        path.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ)
    for key in (
        "MEOWCHAN_PROFILE", "OCHAN_PROFILE", "NUGET_FALLBACK_PACKAGES", "NUGET_CONFIG_FILE",
        "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "DOTNET_ROOT", "DOTNET_ROOT(x86)",
    ):
        environment.pop(key, None)
    environment.update({key: str(path) for key, path in folders.items()})
    environment.update({
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
        "MSBUILDDISABLENODEREUSE": "1",
    })
    return environment


def write_nuget_config(path: Path, feed: Path) -> None:
    """Write a config with exactly one source and an exact package mapping."""
    path = _normalise_absolute(path, "NuGet.Config")
    feed = _normalise_absolute(feed, "local package feed")
    _assert_directory(feed, "local package feed")
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="candidate", value=str(feed))
    mapping = ET.SubElement(config, "packageSourceMapping")
    ET.SubElement(mapping, "clear")
    source = ET.SubElement(mapping, "packageSource", key="candidate")
    ET.SubElement(source, "package", pattern=PACKAGE_ID)
    path.parent.mkdir(parents=True, exist_ok=True)
    audit_sources = ET.SubElement(config, "auditSources")
    ET.SubElement(audit_sources, "clear")
    ET.ElementTree(config).write(path, encoding="utf-8", xml_declaration=True)


def persist_file_evidence(source: Path, destination: Path, label: str) -> dict[str, Any]:
    """Copy one verified artifact out of the disposable execution tree."""
    source = _normalise_absolute(source, label)
    destination = _normalise_absolute(destination, f"{label} evidence")
    _assert_regular(source, label)
    if destination.exists() or os.path.lexists(destination):
        raise GateError(f"Refusing to overwrite existing {label} evidence: {destination}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, destination)
    _assert_regular(destination, f"{label} evidence")
    source_hash = sha256_file(source)
    evidence_hash = sha256_file(destination)
    if source_hash != evidence_hash:
        raise GateError(f"Copied {label} evidence differs from the executed artifact.")
    return {
        "sourcePath": str(source),
        "evidencePath": str(destination),
        "bytes": destination.stat().st_size,
        "sha256": evidence_hash,
    }


def consumer_assembly_path(project: Path) -> Path:
    project = _normalise_absolute(project, "consumer project")
    return project.parent / "bin" / "Release" / "net10.0" / f"{project.stem}.dll"


def _path_within(path: Path, root: Path, label: str) -> Path:
    try:
        resolved = path.resolve(strict=True)
        if not resolved.is_relative_to(root.resolve(strict=True)):
            raise GateError(f"{label} resolved outside its isolated root: {path}")
        return resolved
    except FileNotFoundError as error:
        raise GateError(f"{label} does not exist: {path}") from error


def _restored_package_hash(restored_directory: Path, candidate: dict[str, Any], entry: dict[str, Any]) -> str:
    archives = []
    for path in restored_directory.glob("*.nupkg"):
        _assert_regular(path, "restored package archive")
        archives.append(path)
    if len(archives) == 1:
        restored_hash = sha256_file(archives[0])
        if restored_hash != candidate["sha256"]:
            raise GateError("Restored package archive differs from the supplied package bytes.")
        return restored_hash
    if archives:
        raise GateError("Restored package directory contains an ambiguous set of package archives.")
    # Some NuGet versions remove the archive after extraction but retain the
    # package's SHA-512 in project.assets.json.  Accept that only as the exact
    # cryptographic package identity; a missing identity is not a pass.
    if entry.get("sha512") not in {candidate["sha512"], candidate["sha512"].rstrip("=")}:
        raise GateError("Restored SDK package has no exact archive or matching SHA-512 identity.")
    return candidate["sha256"]


def validate_assets(assets: dict[str, Any], cache: Path, candidate: dict[str, Any]) -> dict[str, Any]:
    """Prove that restore resolved one package, not a project or direct DLL."""
    if not isinstance(assets, dict):
        raise GateError("project.assets.json must contain a JSON object.")
    cache = _normalise_absolute(cache, "fresh NuGet package cache")
    _assert_directory(cache, "fresh NuGet package cache")
    folders = assets.get("packageFolders")
    if not isinstance(folders, dict) or set(folders) != {str(cache) + os.sep} and set(folders) != {str(cache)}:
        # NuGet normally emits a trailing separator on packageFolders keys.  A
        # normalized comparison keeps the check platform-neutral while still
        # allowing only this one fresh cache.
        normalized_folders = {str(Path(item).resolve()) for item in folders} if isinstance(folders, dict) else set()
        if normalized_folders != {str(cache.resolve())}:
            raise GateError("Restore did not use only the fresh isolated package cache.")
    libraries = assets.get("libraries")
    if not isinstance(libraries, dict) or len(libraries) != 1:
        raise GateError("SDK consumer assets must resolve exactly one package library.")
    resolved = []
    for name, entry in libraries.items():
        if not isinstance(name, str) or "/" not in name or not isinstance(entry, dict):
            raise GateError("Malformed SDK library entry in project.assets.json.")
        package_id, version = name.rsplit("/", 1)
        if entry.get("type") != "package":
            raise GateError(f"Consumer assets contain a forbidden non-package dependency: {name}.")
        if package_id != candidate["id"] or version != candidate["version"]:
            raise GateError(f"Consumer assets resolved an unexpected package: {name}.")
        relative_path = entry.get("path")
        if (
            not isinstance(relative_path, str)
            or not relative_path
            or "\\" in relative_path
            or Path(relative_path).is_absolute()
            or any(part in {"", ".", ".."} for part in PurePosixPath(relative_path).parts)
        ):
            raise GateError("Resolved SDK package path must be a safe relative path in the fresh cache.")
        restored_directory = _path_within(cache / relative_path, cache, "restored SDK package")
        restored_hash = _restored_package_hash(restored_directory, candidate, entry)
        assembly = _path_within(restored_directory / candidate["assembly"], restored_directory, "restored SDK assembly")
        if sha256_file(assembly) != candidate["assemblySha256"]:
            raise GateError("Restored SDK assembly differs from the supplied package assembly.")
        resolved.append({
            "id": package_id,
            "version": version,
            "packageSha256": restored_hash,
            "assemblySha256": sha256_file(assembly),
            "restoredPath": str(restored_directory),
        })
    targets = assets.get("targets")
    if not isinstance(targets, dict) or not targets:
        raise GateError("project.assets.json has no target graph.")
    target_libraries = []
    for target in targets.values():
        if not isinstance(target, dict):
            raise GateError("Malformed target graph in project.assets.json.")
        for name, entry in target.items():
            if not isinstance(name, str) or not isinstance(entry, dict):
                raise GateError("Malformed target library in project.assets.json.")
            if entry.get("type") == "project":
                raise GateError(f"Target graph contains a forbidden project path: {name}.")
            target_libraries.append(name)
    expected_name = candidate["id"] + "/" + candidate["version"]
    if expected_name not in target_libraries:
        raise GateError("Target graph does not identify the exact SDK package.")
    return {"package": resolved[0], "targetLibraries": sorted(set(target_libraries))}


def _record_command(
    commands: list[dict[str, Any]], arguments: list[str], cwd: Path, log_path: Path,
    label: str, started: float, exit_code: int | None, *, timed_out: bool = False,
) -> None:
    commands.append({
        "label": label,
        "argv": arguments,
        "cwd": str(cwd),
        "exitCode": exit_code,
        "timedOut": timed_out,
        "durationSeconds": round(time.monotonic() - started, 3),
        "log": str(log_path),
        "boundedLog": True,
        "maxLogBytes": MAX_TAIL_BYTES,
    })


def run_command(
    arguments: list[str], cwd: Path, environment: dict[str, str], log_path: Path,
    deadline: Deadline, label: str, commands: list[dict[str, Any]],
) -> str:
    """Run one command through the shared bounded process helper."""
    if not arguments or any(not isinstance(item, str) for item in arguments):
        raise TypeError("child command must be a non-empty string argument list")
    started = time.monotonic()
    try:
        output = run_bounded_command(
            arguments, cwd, environment, log_path, deadline.remaining(), label,
        )
    except ProcessCommandTimedOut as error:
        _record_command(commands, arguments, cwd, log_path, label, started, None, timed_out=True)
        raise DeadlineExceeded(str(error)) from error
    except ProcessCommandFailed as error:
        _record_command(commands, arguments, cwd, log_path, label, started, error.returncode)
        raise CommandFailed(str(error)) from error
    except ProcessCommandUnavailable as error:
        _record_command(commands, arguments, cwd, log_path, label, started, None)
        raise GateError(str(error)) from error
    except KeyboardInterrupt as error:
        # The shared helper has already performed its bounded process-group
        # cleanup before propagating the interrupt.
        _record_command(commands, arguments, cwd, log_path, label, started, None, timed_out=True)
        raise DeadlineExceeded(f"{label} was interrupted before completion.") from error
    else:
        _record_command(commands, arguments, cwd, log_path, label, started, 0)
        return output


@contextmanager
def heavy_lock(path: Path, deadline: Deadline) -> Iterator[None]:
    """Acquire the shared heavy lock with the same bounded deadline as children."""
    path = _normalise_absolute(path, "heavy lock")
    if _path_has_forbidden_component(path):
        raise GateError(f"heavy lock path is not safe: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a+b") as handle:
        if os.name == "nt":
            import msvcrt
            if handle.tell() == 0:
                handle.write(b"\0")
                handle.flush()
            handle.seek(0)
            while True:
                try:
                    msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
                    break
                except OSError:
                    time.sleep(min(0.1, deadline.remaining()))
        else:
            import fcntl
            while True:
                try:
                    fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                    break
                except BlockingIOError:
                    time.sleep(min(0.1, deadline.remaining()))
        try:
            yield
        finally:
            if os.name == "nt":
                handle.seek(0)
                msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(handle.fileno(), fcntl.LOCK_UN)


def atomic_json(path: Path, value: dict[str, Any]) -> None:
    """Write a report atomically and leave no temporary report behind."""
    path = _normalise_absolute(path, "report")
    temporary = path.with_name(path.name + ".tmp")
    data = (json.dumps(value, indent=2, sort_keys=True) + "\n").encode("utf-8")
    with temporary.open("wb") as handle:
        handle.write(data)
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(temporary, path)
    try:
        directory_fd = os.open(path.parent, os.O_RDONLY)
        try:
            os.fsync(directory_fd)
        finally:
            os.close(directory_fd)
    except OSError:
        # Directory fsync is not available on every supported platform.  The
        # atomic replace still guarantees readers see either old or new JSON.
        pass


def _new_report(source_commit: str, package: Path, output: Path, timeout_seconds: int) -> dict[str, Any]:
    stage_names = ["preflight", "isolation", "restore", "assets", "build", "consumer", "hashes", "finalize"]
    return {
        "schemaVersion": SCHEMA_VERSION,
        "gate": "sdk-package-consumer",
        "passed": False,
        "completed": False,
        "declaredSourceCommit": source_commit,
        "package": str(package),
        "output": str(output),
        "timeoutSeconds": timeout_seconds,
        "stages": [{"name": name, "status": "pending"} for name in stage_names],
        "commands": [],
        "inputs": {"before": {}, "after": {}},
    }


@contextmanager
def report_stage(report: dict[str, Any], name: str) -> Iterator[None]:
    stage = next(item for item in report["stages"] if item["name"] == name)
    stage["status"] = "running"
    stage["startedUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    try:
        yield
    except BaseException as error:
        stage["status"] = "failed"
        stage["error"] = {"type": type(error).__name__, "message": str(error)}
        raise
    else:
        stage["status"] = "passed"
        stage["finishedUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def _mark_unrun_stages(report: dict[str, Any]) -> None:
    for stage in report["stages"]:
        if stage["status"] == "pending":
            stage["status"] = "skipped"


def _prepare_output(value: Path | str, source_root: Path) -> Path:
    output = _normalise_absolute(value, "--output")
    _check_existing_components(output, "--output")
    if output.exists() or os.path.lexists(output):
        raise GateError(f"--output must be a new path; refusing an existing collision: {output}")
    source_root = source_root.resolve()
    # The consumer and feed must never land in the checkout: a report created
    # there would invalidate the clean source pin half way through the gate.
    if output == source_root or output.is_relative_to(source_root) or source_root.is_relative_to(output):
        raise GateError("--output must be an external directory disjoint from the source checkout.")
    output.parent.mkdir(parents=True, exist_ok=True)
    _check_existing_components(output.parent, "--output parent")
    output.mkdir()
    return output


def _load_assets(path: Path) -> dict[str, Any]:
    _assert_regular(path, "project.assets.json")
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, UnicodeError) as error:
        raise GateError("project.assets.json is not valid JSON.") from error
    if not isinstance(value, dict):
        raise GateError("project.assets.json must contain a JSON object.")
    return value


def run_gate(
    package: Path | str, source_commit: str, output: Path | str,
    timeout_seconds: int = DEFAULT_TIMEOUT_SECONDS, *, source_root: Path = ROOT,
    heavy_lock_path: Path = DEFAULT_HEAVY_LOCK,
) -> dict[str, Any]:
    """Execute the gate and atomically write a finalized report dictionary."""
    if not isinstance(timeout_seconds, int) or isinstance(timeout_seconds, bool) or not 1 <= timeout_seconds <= MAX_TIMEOUT_SECONDS:
        raise GateError("--timeout-seconds must be between 1 and 1800 seconds.")
    source_root = _normalise_absolute(source_root, "source checkout")
    package_path = _normalise_absolute(package, "--package")
    output_path = _prepare_output(output, source_root)
    report = _new_report(source_commit, package_path, output_path, timeout_seconds)
    deadline = Deadline(timeout_seconds)
    try:
        with report_stage(report, "preflight"):
            source_info = verify_source_pin(source_root, source_commit)
            version, props_path = product_version(source_root)
            project_info = inspect_consumer_project(SAMPLE_PROJECT)
            candidate = inspect_package(package_path, version, source_commit)
            sample_inventory = inventory_tree(SAMPLE_ROOT, "SDK consumer sample", skip_generated=True)
            report["source"] = source_info
            report["productVersion"] = {"value": version, "path": props_path}
            report["consumer"] = project_info
            report["candidate"] = candidate
            report["inputs"]["before"] = {
                "packageSha256": candidate["sha256"],
                "packageSha512": candidate["sha512"],
                "sampleTreeSha256": sample_inventory["treeSha256"],
            }

        with tempfile.TemporaryDirectory(prefix="sdk-package-consumer-", dir=output_path) as temporary_name:
            isolated = Path(temporary_name)
            workspace = isolated / "workspace"
            feed = isolated / "feed"
            private = isolated / "private"
            config = isolated / "NuGet.Config"
            logs = output_path / "logs"
            with report_stage(report, "isolation"):
                workspace.mkdir()
                copied_sample = workspace / SAMPLE_ROOT.name
                copy_tree(SAMPLE_ROOT, copied_sample, "SDK consumer sample", skip_generated=True)
                isolated_project = copied_sample / SAMPLE_PROJECT.name
                inspect_consumer_project(isolated_project)
                # Keep SDK selection independent from the source checkout's
                # parent directories while not copying any product source.
                shutil.copy2(source_root / "global.json", workspace / "global.json")
                feed.mkdir()
                package_in_feed = feed / package_path.name
                shutil.copyfile(package_path, package_in_feed)
                _assert_regular(package_in_feed, "local SDK package feed candidate")
                if sha256_file(package_in_feed) != candidate["sha256"]:
                    raise GateError("Local feed copy differs from the supplied package.")
                write_nuget_config(config, feed)
                environment = _private_environment(private)
                report["isolation"] = {
                    "workspace": str(workspace),
                    "feed": str(feed),
                    "nugetConfig": str(config),
                    "freshPackageCache": environment["NUGET_PACKAGES"],
                    "networkSources": [str(feed)],
                    "consumerInputBefore": inventory_tree(copied_sample, "isolated SDK consumer", skip_generated=True),
                }

            version_property = f"-p:MeowChanPluginSdkVersion={candidate['version']}"
            restore_arguments = [
                "dotnet", "restore", str(isolated_project), "--configfile", str(config),
                "--packages", environment["NUGET_PACKAGES"], "--disable-parallel",
                "-p:NuGetAudit=false", "-warnaserror:NU1900", "-nodeReuse:false", "-m:1", version_property,
            ]
            build_arguments = [
                "dotnet", "build", str(isolated_project), "-c", "Release", "--no-restore",
                "--nologo", "-nodeReuse:false", "-m:1", version_property,
            ]
            run_arguments = [
                "dotnet", "run", "--project", str(isolated_project), "-c", "Release",
                "--no-restore", "--no-build", version_property,
            ]
            assets_path = copied_sample / "obj" / "project.assets.json"
            with heavy_lock(heavy_lock_path, deadline):
                with report_stage(report, "restore"):
                    run_command(restore_arguments, workspace, environment, logs / "restore.log", deadline, "restore", report["commands"])
                with report_stage(report, "assets"):
                    assets = _load_assets(assets_path)
                    report["assets"] = validate_assets(assets, Path(environment["NUGET_PACKAGES"]), candidate)
                    report["assets"]["path"] = str(assets_path)
                    report["assets"]["sha256"] = sha256_file(assets_path)
                    report["assets"]["evidence"] = persist_file_evidence(
                        assets_path, output_path / "assets" / "project.assets.json", "project.assets.json",
                    )
                with report_stage(report, "build"):
                    run_command(build_arguments, workspace, environment, logs / "build.log", deadline, "build", report["commands"])
                with report_stage(report, "consumer"):
                    run_command(run_arguments, workspace, environment, logs / "consumer.log", deadline, "consumer", report["commands"])

            with report_stage(report, "hashes"):
                consumer_assembly = consumer_assembly_path(isolated_project)
                report["consumer"]["assembly"] = persist_file_evidence(
                    consumer_assembly, output_path / "consumer-evidence" / consumer_assembly.name,
                    "executed consumer assembly",
                )
                consumer_after = inventory_tree(copied_sample, "isolated SDK consumer", skip_generated=True)
                if consumer_after["treeSha256"] != report["isolation"]["consumerInputBefore"]["treeSha256"]:
                    raise GateError("Consumer source inputs changed during restore/build/run.")
                sample_after = inventory_tree(SAMPLE_ROOT, "SDK consumer sample", skip_generated=True)
                if sample_after["treeSha256"] != sample_inventory["treeSha256"]:
                    raise GateError("SDK consumer sample inputs changed while the gate was running.")
                package_after = sha256_file(package_path)
                if package_after != candidate["sha256"]:
                    raise GateError("Supplied package changed while the gate was running.")
                report["inputs"]["after"] = {
                    "packageSha256": package_after,
                    "packageSha512": sha512_base64(package_path.read_bytes()),
                    "sampleTreeSha256": sample_after["treeSha256"],
                    "isolatedConsumerTreeSha256": consumer_after["treeSha256"],
                    "feedPackageSha256": sha256_file(feed / package_path.name),
                }
                report["hashesMatch"] = (
                    report["inputs"]["before"]["packageSha256"] == report["inputs"]["after"]["packageSha256"]
                    and report["inputs"]["after"]["feedPackageSha256"] == candidate["sha256"]
                    and report["inputs"]["after"]["isolatedConsumerTreeSha256"] == report["isolation"]["consumerInputBefore"]["treeSha256"]
                )
                if not report["hashesMatch"]:
                    raise GateError("Input hash manifest did not remain identical.")

        with report_stage(report, "finalize"):
            report["passed"] = True
            report["completed"] = True
    except BaseException as error:
        report["passed"] = False
        report["completed"] = True
        report["error"] = {"type": type(error).__name__, "message": str(error)}
        _mark_unrun_stages(report)
        raise
    finally:
        report.setdefault("completed", True)
        if not report.get("passed"):
            _mark_unrun_stages(report)
        report["finishedUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        atomic_json(output_path / RESULT_NAME, report)
    return report


def _timeout_argument(value: str) -> int:
    try:
        seconds = int(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError("timeout must be an integer") from error
    if not 1 <= seconds <= MAX_TIMEOUT_SECONDS:
        raise argparse.ArgumentTypeError("timeout must be between 1 and 1800 seconds")
    return seconds


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", required=True, type=Path, help="Absolute SDK .nupkg supplied by the parent lane.")
    parser.add_argument("--source-commit", required=True, help="Exact lowercase 40-character source HEAD.")
    parser.add_argument("--output", required=True, type=Path, help="New absolute evidence directory.")
    parser.add_argument("--timeout-seconds", required=True, type=_timeout_argument,
                        help="Whole-gate deadline, 1..1800 seconds.")
    parser.add_argument("--heavy-lock", type=Path, default=DEFAULT_HEAVY_LOCK, help=argparse.SUPPRESS)
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = _build_parser()
    args = parser.parse_args(argv)
    try:
        report = run_gate(args.package, args.source_commit, args.output, args.timeout_seconds,
                          heavy_lock_path=args.heavy_lock)
    except GateError as error:
        # run_gate writes an atomic failure report after creating --output.  Do
        # not turn a failed gate into a traceback or a successful-looking JSON.
        output = _normalise_absolute(args.output, "--output")
        print(json.dumps({"passed": False, "error": str(error), "report": str(output / RESULT_NAME)}), file=sys.stderr)
        return 1
    except (OSError, ValueError, zipfile.BadZipFile) as error:
        output = _normalise_absolute(args.output, "--output")
        print(json.dumps({"passed": False, "error": type(error).__name__ + ": " + str(error),
                          "report": str(output / RESULT_NAME)}), file=sys.stderr)
        return 1
    print(json.dumps({"passed": report["passed"], "completed": report["completed"],
                      "report": str(Path(args.output).resolve() / RESULT_NAME)}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
