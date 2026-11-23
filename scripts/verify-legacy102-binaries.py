#!/usr/bin/env python3
"""Run the isolated, offline binary probes for the published 1.0.2 inputs.

This runner is deliberately narrower than an upgrade or release gate.  It builds
four small, local-reference-only probe projects in copies outside the checkout,
then runs only those probes with private process state.  The old consumers are
always built against ``--legacy-dir``; a candidate runtime is only selected when
that already-built old executable is run for the admission-rejection check.

The command is bounded as a whole, including the shared heavy lock.  It is not
executed by the lightweight unit tests in ``tests/test_verify_legacy102_binaries.py``.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET


# Also supports loading this script with importlib from the stdlib unit tests.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from legacy102_process import (  # noqa: E402
    CommandFailed, CommandTimedOut, CommandUnavailable, run_bounded_command,
)

ROOT = Path(__file__).resolve().parents[1]
EXPECTED_LEGACY_TAG = "eec6c0169f8d52f7481ea0bf7febfd20981e97c3"
FULL_SHA_RE = re.compile(r"^[0-9a-f]{40}$")
HEX_SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
DEFAULT_TIMEOUT_SECONDS = 180
MAX_TIMEOUT_SECONDS = 1800
DEFAULT_HEAVY_LOCK = Path(os.environ["OCHAN_HEAVY_LOCK"]) if os.environ.get("OCHAN_HEAVY_LOCK") else (
    Path(tempfile.gettempdir()) / "meowchan-heavy.lock"
)

SDK_ASSEMBLY = "MeowChan.PluginSdk.dll"
CORE_ASSEMBLY = "libmeowchan.dll"
SDK_LEGACY_ASSEMBLY = "LegacySdk102Plugin.dll"
SDK_CANDIDATE_ASSEMBLY = "LegacySdk102CandidateProbe.dll"
CORE_LEGACY_ASSEMBLY = "LegacyCore102Consumer.dll"
CORE_MIGRATED_ASSEMBLY = "MigratedCore110Consumer.dll"

SDK_PROBE_ROOT = ROOT / "samples" / "MeowChan.PluginSdk.Legacy102Probe"
CORE_PROBE_ROOT = ROOT / "samples" / "LibMeowChan.Legacy102Probe"
PROJECTS = {
    "sdkLegacy": SDK_PROBE_ROOT / "LegacyPlugin" / "LegacyPlugin.csproj",
    "sdkCandidate": SDK_PROBE_ROOT / "CandidateProbe" / "CandidateProbe.csproj",
    "coreLegacy": CORE_PROBE_ROOT / "LegacyConsumer" / "LegacyConsumer.csproj",
    "coreMigrated": CORE_PROBE_ROOT / "MigratedConsumer" / "MigratedConsumer.csproj",
}

_FORBIDDEN_PATH_PARTS = {".ochan", "authstore"}
_FORBIDDEN_DB_SUFFIXES = {".db", ".sqlite", ".sqlite3"}


class ProbeError(RuntimeError):
    """A preflight, build, execution, or evidence error."""


class UnsupportedEnvironment(ProbeError):
    """The requested offline gate cannot run in this environment."""


class DeadlineExceeded(ProbeError):
    """The single runner budget expired, including lock wait."""


class ChildFailed(ProbeError):
    """A child command failed or was killed."""


class Deadline:
    def __init__(self, seconds: int):
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


def _normalise_absolute(value: Path | str, label: str) -> Path:
    path = Path(value)
    if not path.is_absolute():
        raise ProbeError(f"{label} must be an absolute path: {path}")
    # abspath normalises '..' without resolving symlink components.  Symlink
    # components are checked separately so a path alias cannot pass as real.
    return Path(os.path.abspath(os.fspath(path)))


def _path_has_forbidden_component(path: Path) -> bool:
    for part in path.parts:
        lowered = part.casefold()
        if lowered in _FORBIDDEN_PATH_PARTS:
            return True
    return path.suffix.casefold() in _FORBIDDEN_DB_SUFFIXES


def _check_existing_components(path: Path, label: str) -> None:
    """Reject symlink/reparse components up to the first missing component."""
    if _path_has_forbidden_component(path):
        raise ProbeError(f"{label} must not address ~/.oChan, authstore, or a database path: {path}")
    current = Path(path.anchor)
    for part in path.parts[1:]:
        current /= part
        try:
            info = current.lstat()
        except FileNotFoundError:
            break
        if stat.S_ISLNK(info.st_mode):
            raise ProbeError(f"{label} contains a symlink/reparse component: {current}")
        if not (stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode)):
            raise ProbeError(f"{label} contains a special path component: {current}")


def _assert_regular_or_directory(path: Path, label: str) -> None:
    info = path.lstat()
    if stat.S_ISLNK(info.st_mode):
        raise ProbeError(f"{label} is a symlink/reparse alias: {path}")
    if not (stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode)):
        raise ProbeError(f"{label} is a special file: {path}")


def _walk_tree(root: Path, label: str):
    """Yield regular files while refusing every alias and special file."""
    _check_existing_components(root, label)
    try:
        root_info = root.lstat()
    except FileNotFoundError as error:
        raise ProbeError(f"{label} does not exist: {root}") from error
    if stat.S_ISLNK(root_info.st_mode):
        raise ProbeError(f"{label} is a symlink/reparse alias: {root}")
    if not stat.S_ISDIR(root_info.st_mode):
        raise ProbeError(f"{label} must be a real directory: {root}")

    pending = [root]
    while pending:
        directory = pending.pop()
        with os.scandir(directory) as entries:
            for entry in sorted(entries, key=lambda item: item.name):
                entry_path = Path(entry.path)
                if _path_has_forbidden_component(entry_path):
                    raise ProbeError(f"{label} contains a forbidden profile/database path: {entry_path}")
                info = entry.stat(follow_symlinks=False)
                if stat.S_ISLNK(info.st_mode):
                    raise ProbeError(f"{label} contains a symlink/reparse alias: {entry_path}")
                if stat.S_ISDIR(info.st_mode):
                    pending.append(entry_path)
                elif stat.S_ISREG(info.st_mode):
                    yield entry_path
                else:
                    raise ProbeError(f"{label} contains a special file: {entry_path}")


def inventory_tree(root: Path, label: str) -> dict:
    root = _normalise_absolute(root, label)
    files = []
    for path in _walk_tree(root, label):
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


def _ensure_input_roots(legacy_dir: Path, candidate_dir: Path, output: Path) -> tuple[Path, Path, Path]:
    legacy = _normalise_absolute(legacy_dir, "--legacy-dir")
    candidate = _normalise_absolute(candidate_dir, "--candidate-dir")
    evidence = _normalise_absolute(output, "--output")
    _check_existing_components(legacy, "--legacy-dir")
    _check_existing_components(candidate, "--candidate-dir")
    _check_existing_components(evidence, "--output")
    for label, path in (("--legacy-dir", legacy), ("--candidate-dir", candidate)):
        _assert_regular_or_directory(path, label)
        if not path.is_dir():
            raise ProbeError(f"{label} must be a directory: {path}")
    if legacy == candidate or legacy.is_relative_to(candidate) or candidate.is_relative_to(legacy):
        raise ProbeError("--legacy-dir and --candidate-dir must be disjoint real directories.")
    if evidence.exists() or os.path.lexists(evidence):
        raise ProbeError(f"--output must be a new path; refusing an existing collision: {evidence}")
    if evidence == legacy or evidence.is_relative_to(legacy) or legacy.is_relative_to(evidence):
        raise ProbeError("--output overlaps --legacy-dir.")
    if evidence == candidate or evidence.is_relative_to(candidate) or candidate.is_relative_to(evidence):
        raise ProbeError("--output overlaps --candidate-dir.")
    # Parent components are checked above; create only after all collision guards.
    evidence.parent.mkdir(parents=True, exist_ok=True)
    _check_existing_components(evidence.parent, "--output parent")
    evidence.mkdir()
    return legacy, candidate, evidence


def _required_binary(root: Path, name: str, label: str) -> Path:
    path = root / name
    _check_existing_components(path, label)
    try:
        info = path.lstat()
    except FileNotFoundError as error:
        raise ProbeError(f"Missing required {name} in {label}: {path}") from error
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISREG(info.st_mode):
        raise ProbeError(f"Required {name} in {label} is not a regular file: {path}")
    return path


def validate_input_dir(root: Path, label: str) -> dict:
    inventory = inventory_tree(root, label)
    _required_binary(root, SDK_ASSEMBLY, label)
    _required_binary(root, CORE_ASSEMBLY, label)
    return inventory


def _run_git(arguments: list[str]) -> str:
    if not isinstance(arguments, list) or any(not isinstance(item, str) for item in arguments):
        raise TypeError("git arguments must be an argument list")
    result = subprocess.run(
        ["git", *arguments], cwd=ROOT, check=False, shell=False,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
    )
    if result.returncode:
        raise ProbeError(f"Git metadata command failed: git {' '.join(arguments)}")
    return result.stdout.strip()


def verify_source_pin(source_commit: str) -> dict:
    if not FULL_SHA_RE.fullmatch(source_commit):
        raise ProbeError("--source-commit must be a full lowercase 40-character SHA.")
    top = Path(_run_git(["rev-parse", "--show-toplevel"])).resolve()
    if top != ROOT.resolve():
        raise ProbeError(f"Git source root does not match this runner checkout: {top}")
    head = _run_git(["rev-parse", "HEAD"])
    if head != source_commit:
        raise ProbeError(f"Source HEAD {head} is not the requested --source-commit {source_commit}.")
    status = _run_git(["status", "--porcelain=v1", "--untracked-files=all"])
    if status:
        raise ProbeError("Source checkout is dirty; refusing an unpinned binary probe.")
    return {"commit": source_commit, "headExact": True, "clean": True}


def _copy_tree(source: Path, destination: Path, label: str, *, skip_build_dirs: bool = False) -> None:
    source = _normalise_absolute(source, label)
    _check_existing_components(source, label)
    _assert_regular_or_directory(source, label)
    if not source.is_dir():
        raise ProbeError(f"{label} must be a directory: {source}")
    if destination.exists() or os.path.lexists(destination):
        raise ProbeError(f"Refusing to overwrite an isolated destination: {destination}")
    destination.mkdir(parents=True)
    pending = [(source, destination)]
    while pending:
        current_source, current_destination = pending.pop()
        with os.scandir(current_source) as entries:
            for entry in sorted(entries, key=lambda item: item.name):
                if skip_build_dirs and entry.name in {"bin", "obj"}:
                    continue
                source_path = Path(entry.path)
                destination_path = current_destination / entry.name
                info = entry.stat(follow_symlinks=False)
                if stat.S_ISLNK(info.st_mode):
                    raise ProbeError(f"{label} contains a symlink/reparse alias: {source_path}")
                if stat.S_ISDIR(info.st_mode):
                    destination_path.mkdir()
                    pending.append((source_path, destination_path))
                elif stat.S_ISREG(info.st_mode):
                    shutil.copy2(source_path, destination_path)
                else:
                    raise ProbeError(f"{label} contains a special file: {source_path}")


def _tree_hashes(root: Path, label: str) -> dict[str, dict]:
    hashes = {}
    for path in _walk_tree(root, label):
        relative = path.relative_to(root).as_posix()
        hashes[relative] = {"bytes": path.stat().st_size, "sha256": sha256_file(path)}
    return dict(sorted(hashes.items()))


def _project_text(project: Path, label: str) -> str:
    _assert_regular_or_directory(project, label)
    if not project.is_file():
        raise ProbeError(f"Project path is not a file: {project}")
    return project.read_text(encoding="utf-8")


def inspect_project(project: Path, property_name: str, expected_output: str, label: str) -> dict:
    project = _normalise_absolute(project, label)
    text = _project_text(project, label)
    try:
        root = ET.fromstring(text)
    except ET.ParseError as error:
        raise ProbeError(f"Invalid project XML in {label}: {project}") from error
    local_names = [item.tag.rsplit("}", 1)[-1] for item in root.iter()]
    if "ProjectReference" in local_names:
        raise ProbeError(f"{label} contains a forbidden ProjectReference.")
    if "PackageReference" in local_names:
        raise ProbeError(f"{label} contains a forbidden PackageReference/network dependency.")
    if property_name not in text:
        raise ProbeError(f"{label} does not consume the explicit {property_name} property.")
    if "net10.0" not in text:
        raise ProbeError(f"{label} is not explicitly a net10.0 probe project.")
    assembly_names = [item.text.strip() for item in root.iter()
                      if item.tag.rsplit("}", 1)[-1] == "AssemblyName" and item.text]
    if assembly_names != [Path(expected_output).stem]:
        raise ProbeError(f"{label} does not declare expected output {expected_output}.")
    return {"project": str(project), "property": property_name, "output": expected_output}


def _write_nuget_config(path: Path, empty_feed: Path) -> None:
    configuration = ET.Element("configuration")
    package_sources = ET.SubElement(configuration, "packageSources")
    ET.SubElement(package_sources, "clear")
    ET.SubElement(package_sources, "add", key="empty-local-feed", value=str(empty_feed))
    mapping = ET.SubElement(configuration, "packageSourceMapping")
    ET.SubElement(mapping, "clear")
    source = ET.SubElement(mapping, "packageSource", key="empty-local-feed")
    ET.SubElement(source, "package", pattern="*")
    ET.ElementTree(configuration).write(path, encoding="utf-8", xml_declaration=True)


def _private_environment(sandbox: Path) -> dict[str, str]:
    home = sandbox / "home"
    tmp = sandbox / "tmp"
    xdg = sandbox / "xdg"
    dotnet_home = sandbox / "dotnet-cli-home"
    packages = sandbox / "empty-packages"
    for directory in (home, tmp, xdg, dotnet_home, packages,
                      xdg / "config", xdg / "cache", xdg / "data", xdg / "state"):
        directory.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ)
    for key in (
        "MEOWCHAN_PROFILE", "OCHAN_PROFILE", "OCHAN_HOME", "OCHAN_AUTHSTORE",
        "NUGET_FALLBACK_PACKAGES", "NUGET_HTTP_CACHE_PATH", "MSBuildSDKsPath",
        "MSBUILD_EXE_PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64",
    ):
        environment.pop(key, None)
    environment.update({
        "HOME": str(home), "USERPROFILE": str(home), "APPDATA": str(home / "AppData" / "Roaming"),
        "LOCALAPPDATA": str(home / "AppData" / "Local"), "TMPDIR": str(tmp),
        "TMP": str(tmp), "TEMP": str(tmp), "DOTNET_CLI_HOME": str(dotnet_home),
        "NUGET_PACKAGES": str(packages), "XDG_CONFIG_HOME": str(xdg / "config"),
        "XDG_CACHE_HOME": str(xdg / "cache"), "XDG_DATA_HOME": str(xdg / "data"),
        "XDG_STATE_HOME": str(xdg / "state"), "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1", "DOTNET_NOLOGO": "1",
        "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
        "MSBUILDDISABLENODEREUSE": "1", "NUGET_ENABLE_EXPERIMENTAL_HTTP_RETRY": "false",
    })
    return environment


def _dotnet_executable() -> str:
    executable = shutil.which("dotnet")
    if not executable:
        raise UnsupportedEnvironment("dotnet SDK/runtime is unavailable; binary probe is unsupported here.")
    return executable


def run_command(arguments: list[str], cwd: Path, environment: dict[str, str], log: Path,
                deadline: Deadline, label: str) -> str:
    try:
        return run_bounded_command(arguments, cwd, environment, log, deadline.remaining(), label)
    except CommandTimedOut as error:
        raise DeadlineExceeded(f"{label} exceeded the total timeout budget.") from error
    except CommandFailed as error:
        raise ChildFailed(str(error)) from error
    except CommandUnavailable as error:
        raise UnsupportedEnvironment(str(error)) from error


@contextmanager
def heavy_lock(path: Path, deadline: Deadline):
    path = _normalise_absolute(path, "heavy lock")
    _check_existing_components(path, "heavy lock")
    if path.exists() and not path.is_file():
        raise ProbeError(f"heavy lock is not a regular file: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    handle = path.open("a+b")
    acquired = False
    try:
        if os.name == "nt":
            import msvcrt
            if handle.seek(0, os.SEEK_END) == 0:
                handle.write(b"\0")
                handle.flush()
            handle.seek(0)
            while True:
                try:
                    msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
                    acquired = True
                    break
                except OSError:
                    deadline.remaining()
                    time.sleep(min(0.1, max(0.01, deadline.remaining())))
        else:
            import fcntl
            while True:
                try:
                    fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                    acquired = True
                    break
                except BlockingIOError:
                    deadline.remaining()
                    time.sleep(min(0.1, max(0.01, deadline.remaining())))
        yield
    finally:
        if acquired:
            if os.name == "nt":
                import msvcrt
                handle.seek(0)
                msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                fcntl.flock(handle.fileno(), fcntl.LOCK_UN)
        handle.close()


def _probe_json(output: str, label: str) -> dict:
    lines = [line.strip() for line in output.splitlines() if line.strip()]
    if not lines:
        raise ProbeError(f"{label} produced no final JSON line.")
    try:
        value = json.loads(lines[-1])
    except json.JSONDecodeError as error:
        raise ProbeError(f"{label} final stdout line is not JSON.") from error
    if not isinstance(value, dict):
        raise ProbeError(f"{label} final JSON value is not an object.")
    return value


def _required_bool(value: dict, name: str, label: str) -> bool:
    actual = value.get(name)
    if type(actual) is not bool:
        raise ProbeError(f"{label} JSON field {name} must be a boolean.")
    return actual


def validate_sdk_probe(value: dict, plugin_hash: str) -> dict:
    label = "SDK candidate probe"
    if value.get("probe") != "sdk102":
        raise ProbeError(f"{label} reported the wrong probe identifier.")
    if value.get("legacySdkVersion") != "1.0.0":
        raise ProbeError(f"{label} did not report the embedded 1.0.0 legacy SDK declaration.")
    if value.get("candidateSdkVersion") != "1.1.0":
        raise ProbeError(f"{label} did not report the current 1.1.0 SDK version.")
    reported_hash = value.get("legacyAssemblySha256")
    if not isinstance(reported_hash, str) or not HEX_SHA256_RE.fullmatch(reported_hash):
        raise ProbeError(f"{label} reported an invalid legacyAssemblySha256.")
    if value.get("legacyAssemblySha256") != plugin_hash:
        raise ProbeError(f"{label} reported a plugin hash different from the copied old binary.")
    for name in ("legacyLoaded", "sourceContractOk", "jsonRoundTripOk"):
        if not _required_bool(value, name, label):
            raise ProbeError(f"{label} observation {name} was false.")
    return value


def validate_core_probe(value: dict, expected_probe: str, *, rejected: bool, label: str) -> dict:
    if value.get("probe") != expected_probe:
        raise ProbeError(f"{label} reported the wrong probe identifier.")
    started = _required_bool(value, "started", label)
    admission_rejected = _required_bool(value, "startupAdmissionRejected", label)
    exception = value.get("exceptionType")
    if exception is not None and (not isinstance(exception, str) or len(exception) > 128 or
                                   "\n" in exception or "\r" in exception or "/" in exception or "\\" in exception):
        raise ProbeError(f"{label} exceptionType is not a bounded type name.")
    if rejected:
        if started or not admission_rejected or exception != "System.InvalidOperationException":
            raise ProbeError(f"{label} did not prove the documented old RestoreAsync admission rejection.")
    else:
        if not started or admission_rejected or exception not in (None, ""):
            raise ProbeError(f"{label} did not prove a successful startup without rejection.")
    return value


def _build_stage(spec: dict, source_root: Path, output: Path, build_root: Path,
                 dependency_root: Path, deadline: Deadline,
                 dotnet: str, stage_label: str) -> dict:
    project = Path(spec["project"])
    project_info = inspect_project(project, spec["property"], spec["assembly"], stage_label)
    stage = build_root / spec["name"]
    project_copy = stage / "project"
    build_output = stage / "output"
    stage.mkdir(parents=True, exist_ok=False)
    _copy_tree(project.parent, project_copy, stage_label, skip_build_dirs=True)
    if (ROOT / "global.json").is_file():
        shutil.copy2(ROOT / "global.json", stage / "global.json")
    build_output.mkdir()
    feed = stage / "empty-local-feed"
    feed.mkdir()
    nuget_config = stage / "NuGet.Config"
    _write_nuget_config(nuget_config, feed)
    stage_environment = _private_environment(stage / "private")
    intermediate_property = f"-p:BaseIntermediateOutputPath={stage / 'intermediate'}{os.sep}"
    properties = [
        f"-p:{spec['property']}={source_root / spec['assemblyInput']}",
        f"-p:ProbeDependencyDirectory={dependency_root}",
        intermediate_property,
    ]
    restore_arguments = [
        dotnet, "restore", str(project_copy / project.name), "--configfile", str(nuget_config),
        "--packages", str(stage / "private" / "empty-packages"), "--no-cache",
        "--ignore-failed-sources", "--disable-parallel", "--nologo", "-p:NuGetAudit=false",
        *properties,
    ]
    run_command(restore_arguments, stage, stage_environment, stage / "restore.log", deadline, f"{stage_label} restore")
    build_arguments = [
        dotnet, "build", str(project_copy / project.name), "--no-restore", "--nologo",
        "--configuration", "Release", "--output", str(build_output),
        "-p:GenerateDocumentationFile=false", "-p:UseSharedCompilation=false", "-p:BuildProjectReferences=false",
        *properties,
    ]
    run_command(build_arguments, stage, stage_environment, stage / "build.log", deadline, f"{stage_label} build")
    assembly = build_output / spec["assembly"]
    if not assembly.is_file() or assembly.is_symlink():
        raise ProbeError(f"{stage_label} did not produce {spec['assembly']} in isolated output.")
    return {
        **project_info,
        "name": spec["name"],
        "referenceRoot": spec["sourceLabel"],
        "assemblyPath": str(assembly),
        "assemblySha256": sha256_file(assembly),
        "outputHashes": _tree_hashes(build_output, f"{stage_label} build output"),
        "restore": {"localEmptyFeed": True, "networkSources": [], "packageInstallation": False},
    }


def _runtime_run_directory(source_root: Path, build_output: Path, destination: Path, label: str,
                           *, executable_name: str) -> None:
    # Build outputs contain copy-local references, including the OLD Core. They
    # must never replace the deliberately selected candidate runtime. Only the
    # probe's own artifacts are copied on top of the input runtime closure.
    if Path(executable_name).name != executable_name or not executable_name.endswith(".dll"):
        raise ProbeError("The isolated probe executable must be a simple DLL filename.")
    stem = executable_name[:-4]
    required = {executable_name, stem + ".deps.json", stem + ".runtimeconfig.json"}
    owned = required | {stem, stem + ".exe", stem + ".pdb", stem + ".xml"}
    files = {path.name: path for path in _walk_tree(build_output, f"{label} build output")
             if path.parent == build_output and path.name in owned}
    if missing := required - files.keys():
        raise ProbeError(f"Missing executable artifacts for {label}: {', '.join(sorted(missing))}")
    _copy_tree(source_root, destination, label)
    for name, path in sorted(files.items()):
        target = destination / name
        if target.exists() or os.path.lexists(target):
            raise ProbeError(f"Runtime/probe copy collision for {label}: {target}")
        shutil.copy2(path, target)


def _run_private_probe(dotnet: str, executable: Path, arguments: list[str],
                       run_root: Path, deadline: Deadline, label: str, *,
                       with_profile: bool) -> tuple[dict, dict]:
    if not run_root.is_dir():
        raise ProbeError(f"Isolated probe run directory is missing: {run_root}")
    profile = run_root / "profile"
    if with_profile:
        profile.mkdir()
    environment = _private_environment(run_root / "private")
    command = [dotnet, str(executable), *arguments]
    if with_profile:
        command.extend(["--profile", str(profile)])
    stdout = run_command(command, run_root, environment, run_root / "probe.log", deadline, label)
    result = _probe_json(stdout, label)
    return result, {"command": command, "profileRelative": "profile" if with_profile else None,
                    "stdoutSha256": sha256_bytes(stdout.encode("utf-8"))}


def _atomic_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    encoded = (json.dumps(value, indent=2, sort_keys=True) + "\n").encode("utf-8")
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, prefix=f".{path.name}.", suffix=".tmp",
                                         mode="wb", delete=False) as handle:
            temporary = Path(handle.name)
            handle.write(encoded)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
        try:
            directory_fd = os.open(path.parent, os.O_RDONLY)
        except OSError:
            directory_fd = None
        if directory_fd is not None:
            try:
                os.fsync(directory_fd)
            finally:
                os.close(directory_fd)
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()


def _stage_specs(legacy_dir: Path, candidate_dir: Path) -> list[dict]:
    return [
        {"name": "sdk-legacy-plugin", "project": PROJECTS["sdkLegacy"], "property": "SdkAssemblyPath",
         "assembly": SDK_LEGACY_ASSEMBLY, "assemblyInput": SDK_ASSEMBLY, "source": legacy_dir, "sourceLabel": "legacy"},
        {"name": "sdk-candidate-probe", "project": PROJECTS["sdkCandidate"], "property": "SdkAssemblyPath",
         "assembly": SDK_CANDIDATE_ASSEMBLY, "assemblyInput": SDK_ASSEMBLY, "source": candidate_dir, "sourceLabel": "candidate"},
        {"name": "core-legacy-consumer", "project": PROJECTS["coreLegacy"], "property": "CoreAssemblyPath",
         "assembly": CORE_LEGACY_ASSEMBLY, "assemblyInput": CORE_ASSEMBLY, "source": legacy_dir, "sourceLabel": "legacy"},
        {"name": "core-migrated-consumer", "project": PROJECTS["coreMigrated"], "property": "CoreAssemblyPath",
         "assembly": CORE_MIGRATED_ASSEMBLY, "assemblyInput": CORE_ASSEMBLY, "source": candidate_dir, "sourceLabel": "candidate"},
    ]


def execute(args, legacy_dir: Path, candidate_dir: Path, output: Path, source_pin: dict,
            report: dict | None = None) -> dict:
    deadline = Deadline(args.timeout_seconds)
    legacy_inventory = validate_input_dir(legacy_dir, "legacy input")
    candidate_inventory = validate_input_dir(candidate_dir, "candidate input")
    result = report if report is not None else {}
    result.update({
        "schema": "meowchan.legacy102.binary-probes.v1",
        "passed": False,
        "scope": "offline SDK/Core 1.0.2 binary probes; not host/worker, AP04, or release approval",
        "ap04": "not_claimed",
        "release": "not_claimed",
        "networkPolicy": "local NuGet inputs only; OS network requests are not counted by this runner",
        "source": source_pin,
        "legacyProvenance": {
            "expectedReleaseTag": EXPECTED_LEGACY_TAG,
            "tagProven": False,
            "claim": "hash inventory only; an extracted directory cannot prove that it came from the expected Git tag",
            "inventory": legacy_inventory,
        },
        "candidateInventory": candidate_inventory,
        "stages": {},
        "observations": {},
        "timeoutSeconds": args.timeout_seconds,
        "heavyLock": str(Path(args.heavy_lock).absolute()),
    })
    dotnet = shutil.which("dotnet")
    result["dependencies"] = {
        "dotnet": dotnet,
        "required": [".NET 10 SDK/runtime", "four local net10.0 probe projects",
                     "legacy and candidate SDK/Core sibling DLL inputs"],
        "missing": [] if dotnet else ["dotnet SDK/runtime"],
        "packageInstallation": False, "networkSources": [],
    }
    if not dotnet:
        raise UnsupportedEnvironment("dotnet SDK/runtime is unavailable; binary probe is unsupported here.")
    work = output / "work"
    build_root = work / "builds"
    runs_root = work / "runs"
    build_root.mkdir(parents=True)
    runs_root.mkdir()
    (work / "global.json").unlink(missing_ok=True)
    if (ROOT / "global.json").is_file():
        shutil.copy2(ROOT / "global.json", work / "global.json")

    with heavy_lock(Path(args.heavy_lock), deadline):
        specs = _stage_specs(legacy_dir, candidate_dir)
        built = {}
        for spec in specs:
            built[spec["name"]] = _build_stage(
                {**spec, "project": str(spec["project"])}, spec["source"], output,
                build_root, spec["source"], deadline, dotnet,
                spec["name"],
            )
            result["stages"][spec["name"]] = built[spec["name"]]

        old_plugin = Path(built["sdk-legacy-plugin"]["assemblyPath"])
        candidate_probe_output = Path(built["sdk-candidate-probe"]["assemblyPath"]).parent
        sdk_run = runs_root / "sdk-candidate"
        _runtime_run_directory(candidate_dir, candidate_probe_output, sdk_run, "SDK candidate runtime",
                               executable_name=SDK_CANDIDATE_ASSEMBLY)
        plugin_copy = sdk_run / SDK_LEGACY_ASSEMBLY
        if plugin_copy.exists():
            raise ProbeError("SDK probe runtime already contained the old plugin name.")
        shutil.copy2(old_plugin, plugin_copy)
        old_plugin_hash = sha256_file(old_plugin)
        if sha256_file(plugin_copy) != old_plugin_hash:
            raise ProbeError("Copied old plugin bytes changed before execution.")
        sdk_value, sdk_run_meta = _run_private_probe(
            dotnet, sdk_run / SDK_CANDIDATE_ASSEMBLY,
            ["--plugin", str(plugin_copy)], sdk_run, deadline, "SDK candidate probe",
            with_profile=False,
        )
        validate_sdk_probe(sdk_value, old_plugin_hash)
        result["observations"]["sdk"] = {**sdk_run_meta, "result": sdk_value,
                                           "legacyPluginSha256": old_plugin_hash,
                                           "legacyPluginUnchanged": sha256_file(plugin_copy) == old_plugin_hash}

        old_core = Path(built["core-legacy-consumer"]["assemblyPath"])
        old_core_hash = sha256_file(old_core)
        old_runtime = legacy_dir
        candidate_runtime = candidate_dir
        baseline_run = runs_root / "core-legacy-baseline"
        _runtime_run_directory(old_runtime, Path(old_core).parent, baseline_run, "legacy Core baseline runtime",
                               executable_name=CORE_LEGACY_ASSEMBLY)
        baseline_executable = baseline_run / CORE_LEGACY_ASSEMBLY
        if sha256_file(baseline_executable) != old_core_hash:
            raise ProbeError("Legacy Core baseline executable bytes changed while copying.")
        baseline_value, baseline_meta = _run_private_probe(
            dotnet, baseline_executable, [], baseline_run, deadline, "legacy Core baseline probe",
            with_profile=True,
        )
        validate_core_probe(baseline_value, "core102", rejected=False, label="legacy Core baseline probe")

        candidate_run = runs_root / "core-legacy-candidate"
        _runtime_run_directory(candidate_runtime, Path(old_core).parent, candidate_run, "legacy Core candidate runtime",
                               executable_name=CORE_LEGACY_ASSEMBLY)
        candidate_executable = candidate_run / CORE_LEGACY_ASSEMBLY
        if sha256_file(candidate_executable) != old_core_hash:
            raise ProbeError("The legacy Core executable bytes differ between runtime runs.")
        candidate_value, candidate_meta = _run_private_probe(
            dotnet, candidate_executable,
            ["--expect-startup-admission-rejection"], candidate_run, deadline,
            "legacy Core candidate admission probe", with_profile=True,
        )
        validate_core_probe(candidate_value, "core102", rejected=True,
                            label="legacy Core candidate admission probe")

        migrated_output = Path(built["core-migrated-consumer"]["assemblyPath"]).parent
        migrated_run = runs_root / "core-migrated-candidate"
        _runtime_run_directory(candidate_runtime, migrated_output, migrated_run, "migrated Core runtime",
                               executable_name=CORE_MIGRATED_ASSEMBLY)
        migrated_executable = migrated_run / CORE_MIGRATED_ASSEMBLY
        migrated_value, migrated_meta = _run_private_probe(
            dotnet, migrated_executable, [], migrated_run, deadline,
            "migrated Core probe", with_profile=True,
        )
        validate_core_probe(migrated_value, "core110", rejected=False, label="migrated Core probe")

        result["observations"]["core"] = {
            "legacyBaseline": baseline_value, "legacyCandidate": candidate_value,
            "migrated": migrated_value,
            "legacyExecutableSha256": old_core_hash,
            "sameLegacyExecutableBytes": sha256_file(baseline_executable) == sha256_file(candidate_executable),
            "baseline": baseline_meta, "candidate": candidate_meta, "migratedRun": migrated_meta,
        }
        if sha256_file(old_plugin) != old_plugin_hash or sha256_file(plugin_copy) != old_plugin_hash:
            raise ProbeError("The legacy plugin bytes changed during the probes.")
        if any(sha256_file(path) != old_core_hash
               for path in (old_core, baseline_executable, candidate_executable)):
            raise ProbeError("The legacy Core executable bytes changed during the probes.")
        for root, original, label in ((legacy_dir, legacy_inventory, "legacy"),
                                       (candidate_dir, candidate_inventory, "candidate")):
            if inventory_tree(root, label + " input after run")["files"] != original["files"]:
                raise ProbeError(f"The {label} runtime inputs changed during the probes.")
        if verify_source_pin(args.source_commit) != source_pin:
            raise ProbeError("Source pin changed during the probes.")
        result["inputHashesUnchanged"] = True
        result["passed"] = True
    return result


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--legacy-dir", type=Path, required=True, help="Absolute released-1.0.2 runtime input directory.")
    parser.add_argument("--candidate-dir", type=Path, required=True, help="Absolute current candidate runtime input directory.")
    parser.add_argument("--source-commit", required=True, help="Full lowercase 40-character source SHA for this checkout.")
    parser.add_argument("--output", type=Path, required=True, help="New absolute evidence/output directory.")
    parser.add_argument("--timeout-seconds", type=int, default=DEFAULT_TIMEOUT_SECONDS,
                        help=f"Single budget including heavy.lock wait (1-{MAX_TIMEOUT_SECONDS}, default {DEFAULT_TIMEOUT_SECONDS}).")
    parser.add_argument("--heavy-lock", type=Path, default=DEFAULT_HEAVY_LOCK,
                        help="Shared lock path (defaults to OCHAN_HEAVY_LOCK or tempdir/meowchan-heavy.lock).")
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = _parser()
    args = parser.parse_args(argv)
    if args.timeout_seconds < 1 or args.timeout_seconds > MAX_TIMEOUT_SECONDS:
        parser.error(f"--timeout-seconds must be between 1 and {MAX_TIMEOUT_SECONDS}.")

    result = {
        "schema": "meowchan.legacy102.binary-probes.v1", "passed": False,
        "scope": "offline SDK/Core 1.0.2 binary probes; not host/worker, AP04, or release approval",
        "ap04": "not_claimed", "release": "not_claimed", "sourceCommitRequested": args.source_commit,
        "dependencies": {
            "required": [".NET 10 SDK/runtime", "four local net10.0 probe projects",
                         "legacy and candidate SDK/Core sibling DLL inputs"],
            "missing": [], "packageInstallation": False, "networkSources": [],
        },
        "platformLimits": [
            "POSIX uses non-blocking flock; Windows uses msvcrt locking.",
            "The runner does not prove legacy Git-tag provenance from directory hashes.",
            "The probes are not AP04, host/worker, GUI, provider, upgrade, or release acceptance.",
        ],
    }
    output = None
    try:
        legacy, candidate, output = _ensure_input_roots(args.legacy_dir, args.candidate_dir, args.output)
        result["source"] = verify_source_pin(args.source_commit)
        result["inputRoots"] = {"legacy": str(legacy), "candidate": str(candidate)}
        result = execute(args, legacy, candidate, output, result["source"], result)
        result["exitCode"] = 0
        _atomic_json(output / "RESULT.json", result)
        print(json.dumps({"passed": True, "scope": result["scope"], "evidence": str(output / "RESULT.json")}, sort_keys=True))
        return 0
    except (ProbeError, OSError, ValueError, ET.ParseError) as error:
        result["error"] = {"type": type(error).__name__, "message": str(error)}
        result["exitCode"] = 2 if isinstance(error, UnsupportedEnvironment) else 1
        if output is not None:
            try:
                _atomic_json(output / "RESULT.json", result)
            except OSError as write_error:
                result["reportWriteError"] = type(write_error).__name__ + ": " + str(write_error)
        print(json.dumps({"passed": False, "scope": result["scope"], "error": result["error"]}, sort_keys=True), file=sys.stderr)
        return result["exitCode"]
    except KeyboardInterrupt:
        result["error"] = {"type": "KeyboardInterrupt", "message": "interrupted"}
        result["exitCode"] = 130
        if output is not None:
            _atomic_json(output / "RESULT.json", result)
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
