#!/usr/bin/env python3
"""Run the isolated, offline legacy 1.0.2 Host/Worker boundary probe.

This runner consumes an unchanged, already-built legacy plugin DLL.  It copies
only the current Host/SDK probe sample outside the checkout, restores/builds
that sample against explicitly supplied local candidate assemblies, stages the
current worker in a separate directory, and runs the real CLI probe.  It is not
an SDK/Core binary gate, AP04 gate, reauthorization gate, or release gate.
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
    CommandFailed,
    CommandTimedOut,
    CommandUnavailable,
    run_bounded_command,
)


ROOT = Path(__file__).resolve().parents[1]
PROBE_ROOT = ROOT / "samples" / "MeowChan.PluginHost.Legacy102Probe"
PROBE_PROJECT = PROBE_ROOT / "MeowChan.PluginHost.Legacy102Probe.csproj"
PROBE_ASSEMBLY = "MeowChan.PluginHost.Legacy102Probe.dll"
HOST_ASSEMBLY = "MeowChan.PluginHost.dll"
SDK_ASSEMBLY = "MeowChan.PluginSdk.dll"
CORE_ASSEMBLY = "libmeowchan.dll"
WORKER_ASSEMBLY = "MeowChan.PluginWorker.dll"
FULL_SHA_RE = re.compile(r"^[0-9a-f]{40}$")
HEX_SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
DEFAULT_TIMEOUT_SECONDS = 1800
MAX_TIMEOUT_SECONDS = 1800
SCOPE = "offline legacy 1.0.2 Host/Worker boundary; not SDK/Core/AP04/reauthorization/release"

_FORBIDDEN_PATH_PARTS = {".ochan", "authstore"}
_FORBIDDEN_DB_SUFFIXES = {".db", ".sqlite", ".sqlite3"}
_REQUIRED_CANDIDATE = (HOST_ASSEMBLY, SDK_ASSEMBLY, CORE_ASSEMBLY)
_GENERATED_BUILD_DIRS = {"bin", "obj"}


class ProbeError(RuntimeError):
    """A preflight, build, execution, or evidence error."""


class UnsupportedEnvironment(ProbeError):
    """The local framework-only build/runtime cannot be started."""


class DeadlineExceeded(ProbeError):
    """The single monotonic runner budget expired."""


class ChildFailed(ProbeError):
    """A bounded child command returned a non-zero status."""


class Deadline:
    def __init__(self, seconds: int | float):
        self.started = time.monotonic()
        self.ends = self.started + float(seconds)

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
    return Path(os.path.abspath(os.fspath(path)))


def _path_has_forbidden_component(path: Path) -> bool:
    for part in path.parts:
        if part.casefold() in _FORBIDDEN_PATH_PARTS:
            return True
    return path.suffix.casefold() in _FORBIDDEN_DB_SUFFIXES


def _check_existing_components(path: Path, label: str) -> None:
    """Reject symlink/reparse and special components before reading an input."""
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
    root = _normalise_absolute(root, label)
    _check_existing_components(root, label)
    try:
        root_info = root.lstat()
    except FileNotFoundError as error:
        raise ProbeError(f"{label} does not exist: {root}") from error
    if stat.S_ISLNK(root_info.st_mode) or not stat.S_ISDIR(root_info.st_mode):
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


def _overlaps(first: Path, second: Path) -> bool:
    return first == second or first.is_relative_to(second) or second.is_relative_to(first)


def _ensure_input_roots(legacy_plugin: Path, candidate_runtime: Path,
                        worker_dir: Path, output: Path) -> tuple[Path, Path, Path, Path]:
    plugin = _normalise_absolute(legacy_plugin, "--legacy-plugin")
    candidate = _normalise_absolute(candidate_runtime, "--candidate-runtime")
    worker = _normalise_absolute(worker_dir, "--worker-dir")
    evidence = _normalise_absolute(output, "--output")
    for label, path in (("--legacy-plugin", plugin), ("--candidate-runtime", candidate),
                        ("--worker-dir", worker), ("--output", evidence)):
        _check_existing_components(path, label)

    _assert_regular_or_directory(plugin, "--legacy-plugin")
    if not plugin.is_file() or plugin.suffix.casefold() != ".dll":
        raise ProbeError("--legacy-plugin must be an existing regular DLL file.")
    for label, path in (("--candidate-runtime", candidate), ("--worker-dir", worker)):
        _assert_regular_or_directory(path, label)
        if not path.is_dir():
            raise ProbeError(f"{label} must be a real directory: {path}")

    if evidence.exists() or os.path.lexists(evidence):
        raise ProbeError(f"--output must be a new path; refusing an existing collision: {evidence}")
    if _overlaps(evidence, ROOT):
        raise ProbeError("--output must be outside the source checkout.")
    if _overlaps(plugin, candidate) or _overlaps(plugin, worker):
        raise ProbeError("--legacy-plugin overlaps an input runtime directory.")
    if _overlaps(candidate, worker):
        raise ProbeError("--candidate-runtime and --worker-dir must be disjoint real directories.")
    if _overlaps(evidence, plugin) or _overlaps(evidence, candidate) or _overlaps(evidence, worker):
        raise ProbeError("--output overlaps an input artifact.")

    evidence.parent.mkdir(parents=True, exist_ok=True)
    _check_existing_components(evidence.parent, "--output parent")
    evidence.mkdir()
    return plugin, candidate, worker, evidence


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
        raise ProbeError("Source checkout is dirty; refusing an unpinned Host/Worker probe.")
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
                if skip_build_dirs and entry.name.casefold() in _GENERATED_BUILD_DIRS:
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


def _private_environment(sandbox: Path) -> dict[str, str]:
    """Return a process environment with no user profile/store locations."""
    sandbox = _normalise_absolute(sandbox, "private sandbox")
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
        "OCHAN_EVIDENCE", "OCHAN_REAUTH_EVIDENCE", "NUGET_FALLBACK_PACKAGES",
        "NUGET_HTTP_CACHE_PATH", "MSBuildSDKsPath", "MSBUILD_EXE_PATH",
        "DOTNET_ROOT", "DOTNET_ROOT_X64",
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
        raise UnsupportedEnvironment("dotnet SDK/runtime is unavailable; Host/Worker probe is unsupported here.")
    return executable


def run_command(arguments: list[str], cwd: Path, environment: dict[str, str], log: Path,
                deadline: Deadline, label: str) -> str:
    """Run a child only through the shared bounded process helper."""
    try:
        return run_bounded_command(arguments, cwd, environment, log, deadline.remaining(), label)
    except CommandTimedOut as error:
        raise DeadlineExceeded(f"{label} exceeded the total timeout budget.") from error
    except CommandFailed as error:
        raise ChildFailed(str(error)) from error
    except CommandUnavailable as error:
        raise UnsupportedEnvironment(str(error)) from error


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


def inspect_project(project: Path) -> dict:
    project = _normalise_absolute(project, "Host/Worker probe project")
    _check_existing_components(project, "Host/Worker probe project")
    _assert_regular_or_directory(project, "Host/Worker probe project")
    if not project.is_file():
        raise ProbeError(f"Host/Worker probe project is not a file: {project}")
    text = project.read_text(encoding="utf-8")
    try:
        root = ET.fromstring(text)
    except ET.ParseError as error:
        raise ProbeError(f"Invalid Host/Worker probe project XML: {project}") from error
    local_names = [item.tag.rsplit("}", 1)[-1] for item in root.iter()]
    if "ProjectReference" in local_names:
        raise ProbeError("Host/Worker probe contains a forbidden ProjectReference.")
    if "PackageReference" in local_names:
        raise ProbeError("Host/Worker probe contains a forbidden PackageReference.")
    if "CandidateRuntimeDirectory" not in text or "ProbeDependencyDirectory" not in text:
        raise ProbeError("Host/Worker probe does not consume both explicit runtime directory properties.")
    if "net10.0" not in text:
        raise ProbeError("Host/Worker probe is not explicitly a net10.0 project.")
    assembly_names = [item.text.strip() for item in root.iter()
                      if item.tag.rsplit("}", 1)[-1] == "AssemblyName" and item.text]
    if assembly_names and assembly_names != [Path(PROBE_ASSEMBLY).stem]:
        raise ProbeError(f"Host/Worker probe declares an unexpected assembly name: {assembly_names}")
    if "<RestoreProjectStyle>None</RestoreProjectStyle>" in text:
        raise ProbeError("Host/Worker probe disabled framework restore.")
    return {
        "project": str(project),
        "output": PROBE_ASSEMBLY,
        "projectReferences": False,
        "packageReferences": False,
        "frameworkRestore": True,
    }


def _build_probe(candidate_runtime: Path, output: Path, deadline: Deadline, dotnet: str) -> dict:
    """Copy and build only the Host/Worker sample, never the solution/global build."""
    stage = output / "work" / "hostworker-build"
    project_copy = stage / "project"
    build_output = stage / "output"
    stage.mkdir(parents=True, exist_ok=False)
    _copy_tree(PROBE_ROOT, project_copy, "Host/Worker probe sample", skip_build_dirs=True)
    project = project_copy / PROBE_PROJECT.name
    project_info = inspect_project(project)
    global_json = ROOT / "global.json"
    if global_json.is_file():
        shutil.copy2(global_json, stage / "global.json")
    build_output.mkdir()
    feed = stage / "empty-local-feed"
    feed.mkdir()
    nuget_config = stage / "NuGet.Config"
    _write_nuget_config(nuget_config, feed)
    private_root = Path(tempfile.mkdtemp(prefix="l102-build-private-"))
    try:
        environment = _private_environment(private_root)
        intermediate = stage / "intermediate"
        properties = [
            f"-p:CandidateRuntimeDirectory={candidate_runtime}",
            f"-p:ProbeDependencyDirectory={candidate_runtime}",
            f"-p:BaseIntermediateOutputPath={intermediate}{os.sep}",
            "-p:RestoreAudit=false",
        ]
        restore = [
            dotnet, "restore", str(project), "--configfile", str(nuget_config),
            "--packages", str(private_root / "empty-packages"), "--no-cache",
            "--ignore-failed-sources", "--disable-parallel", "--nologo", *properties,
        ]
        run_command(restore, stage, environment, stage / "restore.log", deadline,
                    "legacy102 Host/Worker restore")
        build = [
            dotnet, "build", str(project), "--no-restore", "--nologo",
            "--configuration", "Release", "--output", str(build_output),
            "-p:GenerateDocumentationFile=false", "-p:UseSharedCompilation=false",
            "-p:BuildProjectReferences=false", *properties,
        ]
        run_command(build, stage, environment, stage / "build.log", deadline,
                    "legacy102 Host/Worker build")
    finally:
        shutil.rmtree(private_root, ignore_errors=True)

    required = {PROBE_ASSEMBLY, "MeowChan.PluginHost.Legacy102Probe.deps.json",
                "MeowChan.PluginHost.Legacy102Probe.runtimeconfig.json"}
    for name in required:
        _required_binary(build_output, name, "Host/Worker build output")
    return {
        **project_info,
        "stage": str(stage),
        "assemblyPath": str(build_output / PROBE_ASSEMBLY),
        "assemblySha256": sha256_file(build_output / PROBE_ASSEMBLY),
        "outputHashes": _tree_hashes(build_output, "Host/Worker build output"),
        "restore": {"localEmptyFeed": True, "networkSources": [], "packageInstallation": False},
        "properties": {
            "CandidateRuntimeDirectory": str(candidate_runtime),
            "ProbeDependencyDirectory": str(candidate_runtime),
        },
    }


def _tree_hashes(root: Path, label: str) -> dict[str, dict]:
    hashes = {}
    for path in _walk_tree(root, label):
        relative = path.relative_to(root).as_posix()
        hashes[relative] = {"bytes": path.stat().st_size, "sha256": sha256_file(path)}
    return dict(sorted(hashes.items()))


def _stage_probe_runtime(candidate_runtime: Path, build_output: Path, destination: Path) -> Path:
    """Stage candidate runtime first; copy only the probe's own output on top."""
    _copy_tree(candidate_runtime, destination, "candidate runtime execution stage")
    required = (
        PROBE_ASSEMBLY,
        "MeowChan.PluginHost.Legacy102Probe.deps.json",
        "MeowChan.PluginHost.Legacy102Probe.runtimeconfig.json",
    )
    for name in required:
        source = build_output / name
        _required_binary(build_output, name, "Host/Worker build output")
        target = destination / name
        if target.exists() or os.path.lexists(target):
            raise ProbeError(f"Runtime/probe copy collision: {target}")
        shutil.copy2(source, target)
    return destination / PROBE_ASSEMBLY


def _stage_worker(worker_dir: Path, destination: Path) -> Path:
    _copy_tree(worker_dir, destination, "current worker staging")
    worker = _required_binary(destination, WORKER_ASSEMBLY, "staged current worker")
    return worker


def _probe_json(output: str, label: str) -> dict:
    """Find the final JSON object despite bounded stderr diagnostics."""
    lines = [line.strip() for line in output.splitlines() if line.strip()]
    for line in reversed(lines):
        try:
            value = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(value, dict):
            return value
    raise ProbeError(f"{label} produced no JSON object.")


def _required_bool(value: dict, name: str, label: str) -> bool:
    actual = value.get(name)
    if type(actual) is not bool:
        raise ProbeError(f"{label} JSON field {name} must be a boolean.")
    return actual


def validate_hostworker_probe(value: dict, plugin_hash: str) -> dict:
    label = "legacy102 Host/Worker probe"
    expected_fields = {
        "scope", "passed", "legacyPluginSha256", "declaredSdkVersion",
        "workerProcessObserved", "describeOk", "sourceCallsOk",
    }
    if set(value) != expected_fields:
        raise ProbeError(f"{label} JSON must contain exactly the seven probe fields.")
    if value.get("scope") != "legacy102-hostworker":
        raise ProbeError(f"{label} reported the wrong scope.")
    if value.get("legacyPluginSha256") != plugin_hash or not HEX_SHA256_RE.fullmatch(plugin_hash):
        raise ProbeError(f"{label} reported a legacy plugin hash different from the supplied DLL.")
    if value.get("declaredSdkVersion") != "1.0.0":
        raise ProbeError(f"{label} did not report declared SDK version 1.0.0.")
    for name in ("passed", "workerProcessObserved", "describeOk", "sourceCallsOk"):
        if not _required_bool(value, name, label) or not value[name]:
            raise ProbeError(f"{label} observation {name} was false.")
    return value


def run_hostworker_probe(dotnet: str, probe: Path, legacy_plugin: Path,
                         worker: Path, profile: Path, run_root: Path,
                         sandbox: Path, output: Path, deadline: Deadline) -> tuple[dict, dict]:
    if not run_root.is_dir():
        raise ProbeError(f"Host/Worker runtime stage is missing: {run_root}")
    if not profile.is_dir() or not profile.is_relative_to(Path(tempfile.gettempdir())):
        raise ProbeError("Worker profile must be a new short-path directory outside evidence.")
    environment = _private_environment(sandbox)
    command = [dotnet, str(probe), "--plugin", str(legacy_plugin),
               "--worker", str(worker), "--profile", str(profile)]
    stdout = run_command(command, run_root, environment, output / "hostworker-probe.log",
                         deadline, "legacy102 Host/Worker probe")
    result = _probe_json(stdout, "legacy102 Host/Worker probe")
    return result, {
        "command": command,
        "workerStagedPath": str(worker),
        "profileExternal": True,
        "sandboxExternal": True,
        "stdoutSha256": sha256_bytes(stdout.encode("utf-8")),
    }


@contextmanager
def _owned_temp_directory(prefix: str):
    path = Path(tempfile.mkdtemp(prefix=prefix))
    try:
        yield path
    finally:
        shutil.rmtree(path, ignore_errors=True)


def _input_state(plugin: Path, candidate: Path, worker: Path) -> tuple[str, dict, dict]:
    return sha256_file(plugin), inventory_tree(candidate, "candidate runtime input"), inventory_tree(worker, "worker input")


def _inputs_unchanged(plugin: Path, plugin_hash: str, candidate: Path,
                      candidate_before: dict, worker: Path, worker_before: dict) -> bool:
    return (sha256_file(plugin) == plugin_hash
            and inventory_tree(candidate, "candidate runtime input after run")["files"] == candidate_before["files"]
            and inventory_tree(worker, "worker input after run")["files"] == worker_before["files"])


def execute(args, legacy_plugin: Path, candidate_runtime: Path, worker_dir: Path,
            output: Path, source_pin: dict, report: dict | None = None) -> dict:
    deadline = Deadline(args.timeout_seconds)
    plugin_hash, candidate_before, worker_before = _input_state(legacy_plugin, candidate_runtime, worker_dir)
    result = report if report is not None else {}
    result.update({
        "schema": "meowchan.legacy102.hostworker.v1",
        "passed": False,
        "scope": SCOPE,
        "ap04": "not_claimed",
        "reauthorization": "not_claimed",
        "release": "not_claimed",
        "networkPolicy": "framework restore from empty local feed; no provider/network request",
        "source": source_pin,
        "inputs": {
            "legacyPlugin": {"path": str(legacy_plugin), "sha256": plugin_hash, "prebuilt": True},
            "candidateRuntime": candidate_before,
            "workerDir": worker_before,
        },
        "build": {},
        "observation": {},
        "inputHashesUnchanged": False,
        "timeoutSeconds": args.timeout_seconds,
    })
    error: BaseException | None = None
    temporary_roots: list[Path] = []
    try:
        dotnet = _dotnet_executable()
        build = _build_probe(candidate_runtime, output, deadline, dotnet)
        result["build"] = build

        run_parent = Path(tempfile.mkdtemp(prefix="l102-host-run-"))
        worker_parent = Path(tempfile.mkdtemp(prefix="l102-worker-stage-"))
        profile_parent = Path(tempfile.mkdtemp(prefix="l102-worker-profile-"))
        sandbox_parent = Path(tempfile.mkdtemp(prefix="l102-probe-private-"))
        temporary_roots.extend((run_parent, worker_parent, profile_parent, sandbox_parent))
        run_root = run_parent / "runtime"
        worker_stage = worker_parent / "worker"
        profile = profile_parent / "profile"
        sandbox = sandbox_parent / "sandbox"
        probe = _stage_probe_runtime(candidate_runtime, Path(build["assemblyPath"]).parent, run_root)
        worker = _stage_worker(worker_dir, worker_stage)
        profile.mkdir()
        sandbox.mkdir()
        value, metadata = run_hostworker_probe(
            dotnet, probe, legacy_plugin, worker, profile, run_root, sandbox, output, deadline)
        validate_hostworker_probe(value, plugin_hash)
        result["observation"] = {"result": value, **metadata}
        if verify_source_pin(args.source_commit) != source_pin:
            raise ProbeError("Source pin changed during the Host/Worker probe.")
    except BaseException as caught:
        error = caught
    finally:
        try:
            result["inputHashesUnchanged"] = _inputs_unchanged(
                legacy_plugin, plugin_hash, candidate_runtime, candidate_before, worker_dir, worker_before)
        except BaseException as caught:
            if error is None:
                error = caught
        for temporary in reversed(temporary_roots):
            try:
                shutil.rmtree(temporary)
            except BaseException as caught:
                if error is None:
                    error = ProbeError(f"Could not clean owned temporary directory: {type(caught).__name__}")

    if error is not None:
        raise error
    if not result["inputHashesUnchanged"]:
        raise ProbeError("Artifact inputs changed during the Host/Worker probe.")
    result["passed"] = True
    return result


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--legacy-plugin", type=Path, required=True,
                        help="Absolute path to the prebuilt legacy plugin DLL; it is never compiled.")
    parser.add_argument("--candidate-runtime", type=Path, required=True,
                        help="Absolute current Host/SDK/Core runtime and dependency directory.")
    parser.add_argument("--worker-dir", type=Path, required=True,
                        help="Absolute separate current worker directory containing MeowChan.PluginWorker.dll.")
    parser.add_argument("--source-commit", required=True,
                        help="Full lowercase 40-character source SHA for this checkout.")
    parser.add_argument("--output", type=Path, required=True,
                        help="New absolute evidence/output directory.")
    parser.add_argument("--timeout-seconds", type=int, default=DEFAULT_TIMEOUT_SECONDS,
                        help=f"Single budget including restore/build/run ({DEFAULT_TIMEOUT_SECONDS} default, max {MAX_TIMEOUT_SECONDS}).")
    return parser


def _initial_result(source_commit: str) -> dict:
    return {
        "schema": "meowchan.legacy102.hostworker.v1",
        "passed": False,
        "scope": SCOPE,
        "ap04": "not_claimed",
        "reauthorization": "not_claimed",
        "release": "not_claimed",
        "sourceCommitRequested": source_commit,
        "dependencies": {
            "required": [".NET 10 SDK/runtime", "local Host/SDK/Core runtime directory",
                         "separate current MeowChan.PluginWorker.dll directory"],
            "missing": [], "packageInstallation": False,
            "networkSources": ["empty-local-feed only"],
        },
        "platformLimits": [
            "The parent must provide the central heavy.lock coordination.",
            "The runner proves hashes of supplied directories, not release provenance or signing.",
            "This probe is not SDK/Core/AP04/reauthorization, GUI, provider, upgrade, or release acceptance.",
        ],
    }


def main(argv: list[str] | None = None) -> int:
    parser = _parser()
    args = parser.parse_args(argv)
    if args.timeout_seconds < 1 or args.timeout_seconds > MAX_TIMEOUT_SECONDS:
        parser.error(f"--timeout-seconds must be between 1 and {MAX_TIMEOUT_SECONDS}.")

    result = _initial_result(args.source_commit)
    output: Path | None = None
    try:
        legacy, candidate, worker, output = _ensure_input_roots(
            args.legacy_plugin, args.candidate_runtime, args.worker_dir, args.output)
        result["source"] = verify_source_pin(args.source_commit)
        result["inputRoots"] = {
            "legacyPlugin": str(legacy), "candidateRuntime": str(candidate), "workerDir": str(worker),
        }
        result = execute(args, legacy, candidate, worker, output, result["source"], result)
        result["exitCode"] = 0
        _atomic_json(output / "RESULT.json", result)
        print(json.dumps({"passed": True, "scope": result["scope"],
                          "evidence": str(output / "RESULT.json")}, sort_keys=True))
        return 0
    except (ProbeError, OSError, ValueError, ET.ParseError) as error:
        result["error"] = {"type": type(error).__name__, "message": str(error)}
        result["exitCode"] = 2 if isinstance(error, UnsupportedEnvironment) else 1
        if output is not None:
            try:
                _atomic_json(output / "RESULT.json", result)
            except OSError as write_error:
                result["reportWriteError"] = type(write_error).__name__ + ": " + str(write_error)
        print(json.dumps({"passed": False, "scope": result["scope"],
                          "error": result["error"]}, sort_keys=True), file=sys.stderr)
        return result["exitCode"]
    except KeyboardInterrupt:
        result["error"] = {"type": "KeyboardInterrupt", "message": "interrupted"}
        result["exitCode"] = 130
        if output is not None:
            _atomic_json(output / "RESULT.json", result)
        return 130


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


if __name__ == "__main__":
    raise SystemExit(main())
