#!/usr/bin/env python3
"""Run the synthetic plugin reauthorization process matrix offline.

This is a narrow process E2E gate for the existing
``PluginReauthorizationProcessTests`` matrix.  It builds a fresh copy of the
runtime test project and its real project dependencies, then runs only the
filtered xUnit matrix inside a Linux network namespace.  It is not a
DesktopSessionHost, AP04, upgrade, compatibility, or release gate.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parent))
from legacy102_process import (  # noqa: E402
    CommandFailed,
    CommandTimedOut,
    CommandUnavailable,
    run_bounded_command,
)

ROOT = Path(__file__).resolve().parents[1]
TEST_PROJECT_REL = Path("tests/MeowChan.PluginRuntime.Tests/MeowChan.PluginRuntime.Tests.csproj")
TEST_PROJECT_NAME = "MeowChan.PluginRuntime.Tests"
WORKER_PROJECT_REL = Path("MeowChan.PluginWorker/MeowChan.PluginWorker.csproj")
PROBE_PROJECT_REL = Path("tests/PluginReauthorizationProbe/PluginReauthorizationProbe.csproj")
CONFIGURATION = "Release"
TARGET_FRAMEWORK = "net10.0"
TEST_FILTER = "FullyQualifiedName~MeowChan.PluginRuntime.Tests.PluginReauthorizationProcessTests"
TEST_CLASS = "MeowChan.PluginRuntime.Tests.PluginReauthorizationProcessTests"
TRX_NAME = "plugin-reauthorization.trx"
SOURCE_SHA_RE = re.compile(r"^[0-9a-f]{40}$")
HASH_RE = re.compile(r"^[0-9a-f]{64}$")
PHASE_FILE_RE = re.compile(r"^(?P<sequence>[0-9]+)-(?P<phase>.+)\.(?P<suffix>json|pid|stderr)$")
WORKER_FILE_RE = re.compile(r"^(?P<phase>.+)\.worker-pid$")
DEFAULT_TIMEOUT_SECONDS = 1800
MAX_TIMEOUT_SECONDS = 1800
DEFAULT_LOCK_WAIT_SECONDS = 120
MAX_LOCK_WAIT_SECONDS = 1800
DEFAULT_HEAVY_LOCK = Path(os.environ.get("OCHAN_HEAVY_LOCK", "/tmp/meowchan-heavy.lock"))
SCOPE = "synthetic plugin reauthorization process E2E; not DesktopSessionHost/AP04/upgrade/release"
FORBIDDEN_PARTS = {".ochan", "authstore"}
FORBIDDEN_DB_SUFFIXES = {".db", ".sqlite", ".sqlite3"}

# These are the source directories needed by the existing test project's real
# ProjectReferences and linked Compile/Import items.  The copied root also
# carries MeowChan.sln because BuiltTestBinaries intentionally discovers the
# repository root from the physical test assembly location.
SOURCE_DIRECTORIES = (
    Path("MeowChan.PluginSdk"),
    Path("MeowChan.PluginHost"),
    Path("MeowChan.PluginWorker"),
    Path("libmeowchan"),
    Path("Shared"),
    Path("plugins/Official/Danbooru"),
    Path("plugins/Official/FourChan"),
    Path("plugins/Official/EightKun"),
    Path("tests/MeowChan.PluginRuntime.Tests"),
    Path("tests/MeowChan.PluginRuntime.Fixture"),
    Path("tests/PluginReauthorizationProbe"),
    Path("tests/Shared"),
)
SOURCE_FILES = (
    Path("MeowChan.sln"),
    Path("global.json"),
    Path("Directory.Build.props"),
    Path("Directory.Build.targets"),
    Path("LICENSE"),
    Path("plugins/Official/LibraryParsing.targets"),
)
EXPECTED_METHODS = {
    "ExactConsentIsRequiredAndSurvivesTwoIndependentHostRestarts": 4,
    "InvalidConsentInAnotherProcessPublishesNothingAndCannotEnableUse": 8,
    "MissingArtifactAfterConsentFailsClosedOnNextHostStart": 2,
}
EXPECTED_MATRIX_CASES = sum(EXPECTED_METHODS.values())
EXPECTED_PHASE_SEQUENCES = {
    (
        "seed", "stage-B", "review", "consent-no-rights", "start-B", "consent",
        "start-B", "start-B", "stage-C", "consent", "start-C",
    ),
    ("seed", "stage-B", "review", "consent-forged", "start-B"),
    ("seed", "stage-B", "review", "consent-cancel", "start-B"),
    ("seed", "stage-B", "review", "consent-missing", "start-B"),
    ("seed", "stage-B", "review", "bump-grant", "consent", "start-B"),
    ("seed", "stage-B", "review", "consent", "delete-target", "start-B"),
}
EXPECTED_PROCESS_INVOCATIONS = 98


class ProbeError(RuntimeError):
    """A preflight, build, execution, or evidence error."""


class UnsupportedEnvironment(ProbeError):
    """The requested Linux offline process gate cannot start."""


class DeadlineExceeded(ProbeError):
    """The single bounded runner budget expired."""


class ChildFailed(ProbeError):
    """A bounded child command failed."""


class Deadline:
    def __init__(self, seconds: int | float):
        self.started = time.monotonic()
        self.ends = self.started + float(seconds)

    def remaining(self) -> float:
        remaining = self.ends - time.monotonic()
        if remaining <= 0:
            raise DeadlineExceeded("The runner timeout budget expired.")
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


def _forbidden_path(path: Path) -> bool:
    return any(part.casefold() in FORBIDDEN_PARTS for part in path.parts) or path.suffix.casefold() in FORBIDDEN_DB_SUFFIXES


def _check_components(path: Path, label: str) -> None:
    if _forbidden_path(path):
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
        if not stat.S_ISDIR(info.st_mode) and not stat.S_ISREG(info.st_mode):
            raise ProbeError(f"{label} contains a special path component: {current}")


def _regular(path: Path, label: str) -> None:
    info = path.lstat()
    if stat.S_ISLNK(info.st_mode):
        raise ProbeError(f"{label} is a symlink/reparse alias: {path}")
    if not stat.S_ISREG(info.st_mode):
        raise ProbeError(f"{label} is not a regular file: {path}")


def _directory(path: Path, label: str) -> None:
    _check_components(path, label)
    try:
        info = path.lstat()
    except FileNotFoundError as error:
        raise ProbeError(f"{label} does not exist: {path}") from error
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISDIR(info.st_mode):
        raise ProbeError(f"{label} must be a real directory: {path}")


def _walk_tree(root: Path, label: str):
    root = _normalise_absolute(root, label)
    _directory(root, label)
    pending = [root]
    while pending:
        directory = pending.pop()
        with os.scandir(directory) as entries:
            for entry in sorted(entries, key=lambda item: item.name):
                path = Path(entry.path)
                if _forbidden_path(path):
                    raise ProbeError(f"{label} contains a forbidden profile/database path: {path}")
                info = entry.stat(follow_symlinks=False)
                if stat.S_ISLNK(info.st_mode):
                    raise ProbeError(f"{label} contains a symlink/reparse alias: {path}")
                if stat.S_ISDIR(info.st_mode):
                    pending.append(path)
                elif stat.S_ISREG(info.st_mode):
                    yield path
                else:
                    raise ProbeError(f"{label} contains a special file: {path}")


def inventory_tree(root: Path, label: str) -> dict:
    root = _normalise_absolute(root, label)
    files = []
    for path in _walk_tree(root, label):
        relative = path.relative_to(root).as_posix()
        files.append({"path": relative, "bytes": path.stat().st_size, "sha256": sha256_file(path)})
    files.sort(key=lambda item: item["path"])
    tree = "\n".join(f"{item['sha256']}  {item['path']}" for item in files).encode("utf-8")
    return {"root": str(root), "files": files, "fileCount": len(files), "treeSha256": sha256_bytes(tree)}


def _required_file(path: Path, label: str) -> Path:
    _check_components(path, label)
    try:
        _regular(path, label)
    except FileNotFoundError as error:
        raise ProbeError(f"Missing {label}: {path}") from error
    return path


def _overlaps(first: Path, second: Path) -> bool:
    return first == second or first.is_relative_to(second) or second.is_relative_to(first)


def _ensure_evidence(path: Path) -> Path:
    evidence = _normalise_absolute(path, "--evidence-dir")
    _check_components(evidence, "--evidence-dir")
    if _overlaps(evidence, ROOT):
        raise ProbeError("--evidence-dir must be outside this source checkout.")
    if evidence.exists() or os.path.lexists(evidence):
        raise ProbeError(f"--evidence-dir must be a new path: {evidence}")
    evidence.parent.mkdir(parents=True, exist_ok=True)
    _check_components(evidence.parent, "--evidence-dir parent")
    evidence.mkdir()
    return evidence


def _ensure_feed(path: Path, evidence: Path) -> Path:
    feed = _normalise_absolute(path, "--nuget-feed")
    _directory(feed, "--nuget-feed")
    if _overlaps(feed, evidence):
        raise ProbeError("--nuget-feed and --evidence-dir must be disjoint.")
    return feed


def _run_git(arguments: list[str]) -> str:
    if not isinstance(arguments, list) or any(not isinstance(item, str) for item in arguments):
        raise TypeError("git arguments must be a list of strings")
    result = subprocess.run(
        ["git", *arguments], cwd=ROOT, check=False, shell=False,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
    )
    if result.returncode:
        raise ProbeError(f"Git metadata command failed: git {' '.join(arguments)}")
    return result.stdout.strip()


def verify_source_pin(source_commit: str) -> dict:
    if not SOURCE_SHA_RE.fullmatch(source_commit):
        raise ProbeError("--source-commit must be a full lowercase 40-character SHA.")
    top = Path(_run_git(["rev-parse", "--show-toplevel"])).resolve()
    if top != ROOT.resolve():
        raise ProbeError(f"Git source root does not match this runner checkout: {top}")
    head = _run_git(["rev-parse", "HEAD"])
    if head != source_commit:
        raise ProbeError(f"Source HEAD {head} is not the requested --source-commit {source_commit}.")
    status = _run_git(["status", "--porcelain=v1", "--untracked-files=all"])
    if status:
        raise ProbeError("Source checkout is dirty; refusing an unpinned reauthorization gate.")
    return {"commit": source_commit, "headExact": True, "clean": True}


def _copy_file(source: Path, destination: Path, label: str) -> None:
    _check_components(source, label)
    _regular(source, label)
    if destination.exists() or os.path.lexists(destination):
        raise ProbeError(f"Refusing to overwrite copied source path: {destination}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, destination)


def _copy_tree(source: Path, destination: Path, label: str) -> None:
    source = _normalise_absolute(source, label)
    _directory(source, label)
    if destination.exists() or os.path.lexists(destination):
        raise ProbeError(f"Refusing to overwrite source stage: {destination}")
    destination.mkdir(parents=True)
    pending = [(source, destination)]
    while pending:
        current_source, current_destination = pending.pop()
        with os.scandir(current_source) as entries:
            for entry in sorted(entries, key=lambda item: item.name):
                if entry.name in {"bin", "obj"}:
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


def copy_source_stage(destination: Path) -> Path:
    destination = _normalise_absolute(destination, "source build stage")
    if destination.exists() or os.path.lexists(destination):
        raise ProbeError(f"Source build stage must be new: {destination}")
    destination.mkdir(parents=True)
    for relative in SOURCE_FILES:
        _copy_file(ROOT / relative, destination / relative, f"source file {relative}")
    for relative in SOURCE_DIRECTORIES:
        _copy_tree(ROOT / relative, destination / relative, f"source directory {relative}")
    return destination


def _write_nuget_config(path: Path, feed: Path) -> None:
    configuration = ET.Element("configuration")
    sources = ET.SubElement(configuration, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="provided-local-feed", value=str(feed))
    mapping = ET.SubElement(configuration, "packageSourceMapping")
    ET.SubElement(mapping, "clear")
    source = ET.SubElement(mapping, "packageSource", key="provided-local-feed")
    ET.SubElement(source, "package", pattern="*")
    ET.ElementTree(configuration).write(path, encoding="utf-8", xml_declaration=True)


def _private_environment(work: Path, evidence: Path, source_commit: str, dotnet: str, packages: Path) -> dict[str, str]:
    private = work / "private"
    paths = {
        "home": private / "home",
        "tmp": private / "tmp",
        "xdg": private / "xdg",
        "dotnet": private / "dotnet-cli-home",
        "nugetCache": private / "nuget-http-cache",
    }
    for directory in paths.values():
        directory.mkdir(parents=True, exist_ok=True)
    for directory in (paths["xdg"] / "config", paths["xdg"] / "cache", paths["xdg"] / "data", paths["xdg"] / "state"):
        directory.mkdir(parents=True, exist_ok=True)

    environment = {key: os.environ[key] for key in ("LANG", "LC_ALL", "TZ") if key in os.environ}
    environment.update({
        "PATH": str(Path(dotnet).parent) + ":/usr/bin:/bin",
        "HOME": str(paths["home"]),
        "USERPROFILE": str(paths["home"]),
        "APPDATA": str(paths["home"] / "AppData" / "Roaming"),
        "LOCALAPPDATA": str(paths["home"] / "AppData" / "Local"),
        "TMPDIR": str(paths["tmp"]),
        "TMP": str(paths["tmp"]),
        "TEMP": str(paths["tmp"]),
        "DOTNET_CLI_HOME": str(paths["dotnet"]),
        "NUGET_PACKAGES": str(packages),
        "NUGET_HTTP_CACHE_PATH": str(paths["nugetCache"]),
        "XDG_CONFIG_HOME": str(paths["xdg"] / "config"),
        "XDG_CACHE_HOME": str(paths["xdg"] / "cache"),
        "XDG_DATA_HOME": str(paths["xdg"] / "data"),
        "XDG_STATE_HOME": str(paths["xdg"] / "state"),
        "DOTNET_HOST_PATH": dotnet,
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
        "DOTNET_NOLOGO": "1",
        "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
        "MSBUILDDISABLENODEREUSE": "1",
        "NUGET_ENABLE_EXPERIMENTAL_HTTP_RETRY": "false",
        "NUGET_CERT_REVOCATION_MODE": "offline",
        "OCHAN_TEST_SOURCE_SHA": source_commit,
        "OCHAN_REAUTH_EVIDENCE": str(evidence / "process-evidence"),
    })
    # Deliberately no DISPLAY/WAYLAND/XAUTHORITY/DBus or profile/auth override
    # variables are copied from the caller.  The allow-list above is the guard.
    (evidence / "process-evidence").mkdir(parents=True, exist_ok=True)
    return environment


def _bwrap_command(inner: list[str], work: Path, evidence: Path, feed: Path, cwd: Path,
                   environment: dict[str, str], bwrap: str) -> list[str]:
    command = [
        bwrap, "--die-with-parent", "--unshare-net", "--ro-bind", "/", "/",
        "--tmpfs", "/tmp", "--bind", str(evidence), str(evidence),
        "--ro-bind", str(feed), str(feed), "--chdir", str(cwd), "--clearenv",
    ]
    for key, value in sorted(environment.items()):
        command.extend(("--setenv", key, value))
    command.extend(("--", *inner))
    return command


def _dotnet_host() -> str:
    executable = shutil.which("dotnet")
    if not executable:
        raise UnsupportedEnvironment("dotnet SDK/runtime is unavailable.")
    path = Path(executable).resolve(strict=True)
    _regular(path, "dotnet host")
    return str(path)


def _bwrap_host() -> str:
    if not sys.platform.startswith("linux"):
        raise UnsupportedEnvironment("The reauthorization gate requires Linux for --unshare-net.")
    executable = shutil.which("bwrap")
    if not executable:
        raise UnsupportedEnvironment("bwrap is unavailable; refusing a non-isolated test run.")
    path = Path(executable).resolve(strict=True)
    _regular(path, "bwrap host")
    return str(path)


def run_command(arguments: list[str], cwd: Path, environment: dict[str, str], log: Path,
                deadline: Deadline, label: str) -> str:
    try:
        return run_bounded_command(arguments, cwd, environment, log, deadline.remaining(), label)
    except CommandTimedOut as error:
        raise DeadlineExceeded(f"{label} exceeded the runner timeout budget.") from error
    except CommandFailed as error:
        raise ChildFailed(str(error)) from error
    except CommandUnavailable as error:
        raise UnsupportedEnvironment(str(error)) from error


@contextmanager
def acquire_lock(path: Path, deadline: Deadline, wait_seconds: float):
    if not sys.platform.startswith("linux"):
        raise UnsupportedEnvironment("The bounded heavy lock requires Linux flock in this gate.")
    lock = _normalise_absolute(path, "--heavy-lock")
    _check_components(lock, "--heavy-lock")
    if lock.exists():
        _regular(lock, "--heavy-lock")
    lock.parent.mkdir(parents=True, exist_ok=True)
    _check_components(lock.parent, "--heavy-lock parent")
    started = time.monotonic()
    with lock.open("a+") as handle:
        lock_deadline = min(deadline.ends, time.monotonic() + wait_seconds)
        while True:
            try:
                fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except BlockingIOError:
                if time.monotonic() >= lock_deadline:
                    raise DeadlineExceeded("The heavy lock wait budget expired.")
                deadline.remaining()
                time.sleep(min(0.1, lock_deadline - time.monotonic()))
        try:
            yield {"path": str(lock), "waitSeconds": time.monotonic() - started, "acquired": True}
        finally:
            fcntl.flock(handle.fileno(), fcntl.LOCK_UN)


def _project_contract(source_stage: Path) -> dict:
    project = source_stage / TEST_PROJECT_REL
    _required_file(project, "runtime test project")
    try:
        root = ET.fromstring(project.read_text(encoding="utf-8"))
    except ET.ParseError as error:
        raise ProbeError(f"Invalid runtime test project XML: {project}") from error
    names = [element.tag.rsplit("}", 1)[-1] for element in root.iter()]
    references = [element.get("Include", "").strip().replace("\\", "/") for element in root.iter()
                  if element.tag.rsplit("}", 1)[-1] == "ProjectReference" and element.get("Include")]
    required = {
        "../../MeowChan.PluginHost/MeowChan.PluginHost.csproj",
        "../../MeowChan.PluginWorker/MeowChan.PluginWorker.csproj",
        "../../plugins/Official/Danbooru/MeowChan.Plugin.Danbooru.csproj",
        "../MeowChan.PluginRuntime.Fixture/MeowChan.PluginRuntime.Fixture.csproj",
        "../PluginReauthorizationProbe/PluginReauthorizationProbe.csproj",
    }
    if not required.issubset(references):
        raise ProbeError("Runtime test project is missing a real helper/worker dependency ProjectReference.")
    if "PackageReference" not in names or "RestorePackagesWithLockFile" not in names:
        raise ProbeError("Runtime test project is not a locked package test project.")
    if "net10.0" not in project.read_text(encoding="utf-8"):
        raise ProbeError("Runtime test project is not net10.0.")
    return {"project": str(project), "projectReferences": sorted(references), "lockedRestore": True,
            "filter": TEST_FILTER, "configuration": CONFIGURATION}


def _build_paths(source_stage: Path) -> dict[str, Path]:
    return {
        "test": source_stage / "tests/MeowChan.PluginRuntime.Tests/bin/Release/net10.0",
        "worker": source_stage / "MeowChan.PluginWorker/bin/Release/net10.0",
        "probe": source_stage / "tests/PluginReauthorizationProbe/bin/Release/net10.0",
    }


def _pin_artifacts(paths: dict[str, Path]) -> dict:
    required = {
        "test": (f"{TEST_PROJECT_NAME}.dll", f"{TEST_PROJECT_NAME}.deps.json"),
        "worker": ("MeowChan.PluginWorker.dll", "MeowChan.PluginWorker.deps.json", "MeowChan.PluginWorker.runtimeconfig.json"),
        "probe": ("PluginReauthorizationProbe.dll", "PluginReauthorizationProbe.deps.json", "PluginReauthorizationProbe.runtimeconfig.json"),
    }
    pins = {"directories": {}, "required": {}}
    for label, directory in paths.items():
        _directory(directory, f"fresh {label} build output")
        pins["directories"][label] = inventory_tree(directory, f"fresh {label} build output")
        pins["required"][label] = {}
        for name in required[label]:
            path = _required_file(directory / name, f"fresh {label} artifact {name}")
            pins["required"][label][name] = {"path": str(path), "sha256": sha256_file(path), "bytes": path.stat().st_size}
    return pins


def build_fresh(source_stage: Path, work: Path, feed: Path, dotnet: str, bwrap: str,
                environment: dict[str, str], deadline: Deadline) -> dict:
    project = source_stage / TEST_PROJECT_REL
    contract = _project_contract(source_stage)
    config = work / "NuGet.Config"
    _write_nuget_config(config, feed)
    packages = Path(environment["NUGET_PACKAGES"])
    packages.mkdir(parents=True, exist_ok=True)
    restore_inner = [
        dotnet, "restore", str(project), "--configfile", str(config), "--packages", str(packages),
        "--locked-mode", "--disable-parallel", "--nologo", "-p:RestoreIgnoreFailedSources=false",
        "-p:NuGetAudit=false",
    ]
    restore = _bwrap_command(restore_inner, work, work.parent, feed, source_stage, environment, bwrap)
    run_command(restore, source_stage, environment, work / "restore.log", deadline, "plugin reauthorization locked restore")

    build_inner = [dotnet, "build", str(project), "--no-restore", "--configuration", CONFIGURATION, "--nologo"]
    build = _bwrap_command(build_inner, work, work.parent, feed, source_stage, environment, bwrap)
    run_command(build, source_stage, environment, work / "build.log", deadline, "plugin reauthorization fresh build")
    paths = _build_paths(source_stage)
    pins = _pin_artifacts(paths)
    return {**contract, "sourceStage": str(source_stage), "outputPaths": {key: str(value) for key, value in paths.items()},
            "pins": pins, "restore": {"lockedMode": True, "providedLocalFeed": str(feed), "networkSources": []}}


def run_filtered_test(source_stage: Path, work: Path, feed: Path, dotnet: str, bwrap: str,
                      environment: dict[str, str], deadline: Deadline) -> dict:
    project = source_stage / TEST_PROJECT_REL
    results = work / "test-results"
    results.mkdir(parents=True, exist_ok=False)
    trx = results / TRX_NAME
    inner = [
        dotnet, "test", str(project), "--no-restore", "--no-build", "--configuration", CONFIGURATION,
        "--filter", TEST_FILTER, "--logger", f"trx;LogFileName={TRX_NAME}",
        "--results-directory", str(results), "--nologo",
    ]
    command = _bwrap_command(inner, work, work.parent, feed, source_stage, environment, bwrap)
    output = run_command(command, source_stage, environment, work / "test.log", deadline,
                         "plugin reauthorization process matrix")
    _required_file(trx, "reauthorization TRX")
    return {"command": command, "stdoutTailSha256": sha256_bytes(output.encode("utf-8")), "trx": str(trx)}


def _counter(counters: ET.Element, name: str) -> int | None:
    value = counters.get(name)
    return None if value is None else int(value)


def validate_trx(path: Path) -> dict:
    path = _required_file(path, "reauthorization TRX")
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as error:
        raise ProbeError(f"Invalid reauthorization TRX: {path}") from error
    counters = root.find(".//{*}Counters")
    if counters is None:
        raise ProbeError("TRX has no Counters element.")
    total, passed, failed = (_counter(counters, name) for name in ("total", "passed", "failed"))
    if (total, passed, failed) != (EXPECTED_MATRIX_CASES, EXPECTED_MATRIX_CASES, 0):
        raise ProbeError(f"TRX counters are not the complete {EXPECTED_MATRIX_CASES}-case matrix: {counters.attrib}")
    for name in ("skipped", "notExecuted"):
        value = _counter(counters, name)
        if value is not None and value != 0:
            raise ProbeError(f"TRX reports skipped/not-executed cases: {counters.attrib}")

    results = root.findall(".//{*}UnitTestResult")
    method_counts = {name: 0 for name in EXPECTED_METHODS}
    for result in results:
        name = result.get("testName", "")
        if not name.startswith(TEST_CLASS + "."):
            raise ProbeError(f"TRX contains an unexpected test result: {name}")
        if result.get("outcome") != "Passed":
            raise ProbeError(f"Reauthorization matrix contains a non-passed result: {name} ({result.get('outcome')})")
        method = name[len(TEST_CLASS) + 1:].split("(", 1)[0].strip().split("[", 1)[0].strip()
        if method not in method_counts:
            raise ProbeError(f"TRX contains an unexpected reauthorization matrix method: {name}")
        method_counts[method] += 1
    if len(results) != EXPECTED_MATRIX_CASES or method_counts != EXPECTED_METHODS:
        raise ProbeError(f"TRX matrix case set differs: results={len(results)}, methods={method_counts}")
    return {"path": str(path), "sha256": sha256_file(path), "total": total, "passed": passed,
            "failed": failed, "skipped": 0, "methods": method_counts}


def _phase_jsons(root: Path) -> list[tuple[int, str, Path]]:
    found = []
    for path in root.iterdir():
        if path.is_symlink() or not path.is_file():
            continue
        match = PHASE_FILE_RE.fullmatch(path.name)
        if match and match.group("suffix") == "json":
            found.append((int(match.group("sequence")), match.group("phase"), path))
    return sorted(found)


def _int_field(value: object, name: str, *, positive: bool = False) -> int:
    if type(value) is not int or (positive and value <= 0):
        raise ProbeError(f"Process evidence field {name} is not a valid integer.")
    return value


def validate_process_evidence(parent: Path, source_commit: str, probe_hash: str, worker_hash: str) -> dict:
    parent = _normalise_absolute(parent, "process evidence")
    _directory(parent, "process evidence")
    roots = []
    with os.scandir(parent) as entries:
        for entry in entries:
            path = Path(entry.path)
            if entry.name.startswith("reauth-process-"):
                info = entry.stat(follow_symlinks=False)
                if stat.S_ISLNK(info.st_mode) or not stat.S_ISDIR(info.st_mode):
                    raise ProbeError(f"Process evidence root is not a real directory: {path}")
                roots.append(path)
    if len(roots) != EXPECTED_MATRIX_CASES:
        raise ProbeError(f"Expected {EXPECTED_MATRIX_CASES} persistent process evidence roots, found {len(roots)}.")

    sequence_counts: dict[tuple[str, ...], int] = {}
    process_count = 0
    worker_evidence_count = 0
    for root in sorted(roots, key=lambda path: path.name):
        phases = _phase_jsons(root)
        sequence = tuple(phase for _, phase, _ in phases)
        if sequence not in EXPECTED_PHASE_SEQUENCES:
            raise ProbeError(f"Unexpected process phase sequence in {root}: {sequence}")
        sequence_counts[sequence] = sequence_counts.get(sequence, 0) + 1
        expected_numbers = list(range(1, len(phases) + 1))
        if [number for number, _, _ in phases] != expected_numbers:
            raise ProbeError(f"Process evidence sequence numbers are not contiguous in {root}.")
        for number, phase, json_path in phases:
            prefix = f"{number:02d}-{phase}"
            pid_path = root / f"{prefix}.pid"
            stderr_path = root / f"{prefix}.stderr"
            _required_file(pid_path, f"process PID evidence {prefix}")
            _required_file(stderr_path, f"process stderr evidence {prefix}")
            try:
                process_pid = int(pid_path.read_text(encoding="utf-8").strip())
                value = json.loads(json_path.read_text(encoding="utf-8"))
            except (ValueError, json.JSONDecodeError) as error:
                raise ProbeError(f"Invalid process evidence JSON/PID for {prefix}.") from error
            if not isinstance(value, dict) or value.get("Phase") != phase:
                raise ProbeError(f"Process evidence phase mismatch for {json_path}.")
            _int_field(process_pid, f"{prefix}.pid", positive=True)
            _int_field(value.get("Pid"), f"{prefix}.Pid", positive=True)
            if value.get("SourceSha") != source_commit:
                raise ProbeError(f"Process evidence source SHA mismatch for {json_path}.")
            if value.get("ProbeHash") != probe_hash or value.get("WorkerHash") != worker_hash:
                raise ProbeError(f"Process evidence artifact hash mismatch for {json_path}.")
            if value.get("HttpRequests") != 0:
                raise ProbeError(f"Process evidence reports HTTP activity for {json_path}.")
            for field in ("StoreHash", "TargetExecution", "TargetAssemblyHash", "OldExecution"):
                if not isinstance(value.get(field), str) or not HASH_RE.fullmatch(value[field]):
                    raise ProbeError(f"Process evidence field {field} is not a pinned SHA-like value: {json_path}")
            worker_pid = value.get("WorkerPid")
            worker_path = root / f"{phase}.worker-pid"
            if worker_pid is None:
                if worker_path.exists() or os.path.lexists(worker_path):
                    raise ProbeError(f"Unexpected worker PID evidence for a non-worker phase: {worker_path}")
            else:
                _int_field(worker_pid, f"{prefix}.WorkerPid", positive=True)
                _required_file(worker_path, f"worker PID evidence {phase}")
                if worker_path.read_text(encoding="utf-8").strip() != str(worker_pid):
                    raise ProbeError(f"Worker PID evidence mismatch for {worker_path}.")
                worker_evidence_count += 1
            process_count += 1
        expected_json_names = {f"{number:02d}-{phase}.json" for number, phase, _ in phases}
        for path in root.iterdir():
            if path.is_symlink():
                raise ProbeError(f"Process evidence contains a symlink: {path}")
            if path.name.endswith((".pid", ".stderr", ".worker-pid")) and path.name not in {
                name.replace(".json", ".pid") for name in expected_json_names
            } and path.name not in {
                name.replace(".json", ".stderr") for name in expected_json_names
            } and path.name not in {phase + ".worker-pid" for _, phase, _ in phases}:
                raise ProbeError(f"Unmatched per-process evidence file: {path}")
        inventory_tree(root, f"persistent process evidence {root.name}")

    if process_count != EXPECTED_PROCESS_INVOCATIONS:
        raise ProbeError(f"Expected {EXPECTED_PROCESS_INVOCATIONS} process invocations, found {process_count}.")
    return {"root": str(parent), "persistent": True, "matrixRoots": len(roots),
            "processInvocations": process_count, "workerEvidenceFiles": worker_evidence_count,
            "phaseSequences": {" / ".join(key): value for key, value in sequence_counts.items()}}


def _artifact_hashes_unchanged(before: dict, paths: dict[str, Path]) -> bool:
    after = {label: inventory_tree(path, f"fresh {label} build output after test") for label, path in paths.items()}
    return all(after[label]["files"] == value["files"] for label, value in before["directories"].items())


def execute(args, evidence: Path, feed: Path, source_before: dict, result: dict) -> dict:
    deadline = Deadline(args.timeout_seconds)
    work = evidence / "work"
    work.mkdir()
    source_stage = work / "source"
    process_evidence = evidence / "process-evidence"
    dotnet = _dotnet_host()
    bwrap = _bwrap_host()
    environment = _private_environment(work, evidence, args.source_commit, dotnet, work / "private/nuget-packages")
    result.update({"sourceBefore": source_before, "tools": {"dotnet": dotnet, "bwrap": bwrap},
                   "network": {"mode": "Linux bwrap --unshare-net", "providerRequests": 0, "liveSources": []},
                   "build": {}, "test": {}, "processEvidence": {}, "artifactsUnchanged": False})
    source_stage = copy_source_stage(source_stage)
    build = build_fresh(source_stage, work, feed, dotnet, bwrap, environment, deadline)
    result["build"] = build
    paths = {label: Path(value) for label, value in build["outputPaths"].items()}
    artifact_before = {"directories": build["pins"]["directories"], "required": build["pins"]["required"]}
    result["artifacts"] = artifact_before
    test = run_filtered_test(source_stage, work, feed, dotnet, bwrap, environment, deadline)
    result["test"] = {**test, "trxValidation": validate_trx(Path(test["trx"]))}
    result["artifactsUnchanged"] = _artifact_hashes_unchanged(artifact_before, paths)
    if not result["artifactsUnchanged"]:
        raise ProbeError("Fresh build artifacts changed during the filtered test.")
    probe_hash = artifact_before["required"]["probe"]["PluginReauthorizationProbe.dll"]["sha256"]
    worker_hash = artifact_before["required"]["worker"]["MeowChan.PluginWorker.dll"]["sha256"]
    result["processEvidence"] = validate_process_evidence(process_evidence, args.source_commit, probe_hash, worker_hash)
    after = verify_source_pin(args.source_commit)
    result["sourceAfter"] = after
    if after != source_before:
        raise ProbeError("Source pin changed during the reauthorization matrix.")
    return result


def _initial_result(source_commit: str) -> dict:
    return {
        "schemaVersion": 1,
        "passed": False,
        "scope": SCOPE,
        "sourceCommitRequested": source_commit,
        "matrix": {"class": TEST_CLASS, "filter": TEST_FILTER, "expectedCases": EXPECTED_MATRIX_CASES,
                    "expectedProcessInvocations": EXPECTED_PROCESS_INVOCATIONS},
        "desktopSessionHost": "not_claimed",
        "ap04": "not_claimed",
        "release": "not_claimed",
        "upgrade": "not_claimed",
        "providerRequests": 0,
        "limits": [
            "Synthetic process E2E only; no DesktopSessionHost, GUI, provider, historical upgrade, AP04, or release acceptance.",
            "Fresh artifacts are built in a copied source stage and pinned by SHA-256; no pre-existing bin/obj output is used.",
            "The local NuGet feed is read-only input; restore is locked and network-isolated, with no package download claim.",
        ],
    }


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-commit", required=True, help="Full lowercase 40-character source SHA.")
    parser.add_argument("--evidence-dir", "--output", dest="evidence_dir", type=Path, required=True,
                        help="New external evidence directory.")
    parser.add_argument("--nuget-feed", type=Path, required=True,
                        help="Existing local NuGet feed; it is read-only and the only package source.")
    parser.add_argument("--heavy-lock", type=Path, default=DEFAULT_HEAVY_LOCK,
                        help="Shared heavy.lock path (defaults to OCHAN_HEAVY_LOCK or /tmp/meowchan-heavy.lock).")
    parser.add_argument("--timeout-seconds", type=int, default=DEFAULT_TIMEOUT_SECONDS,
                        help=f"Bounded restore/build/test budget, 1-{MAX_TIMEOUT_SECONDS} seconds.")
    parser.add_argument("--lock-wait-seconds", type=int, default=DEFAULT_LOCK_WAIT_SECONDS,
                        help=f"Bounded heavy.lock wait, 0-{MAX_LOCK_WAIT_SECONDS} seconds.")
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = _parser()
    args = parser.parse_args(argv)
    if not 1 <= args.timeout_seconds <= MAX_TIMEOUT_SECONDS:
        parser.error(f"--timeout-seconds must be between 1 and {MAX_TIMEOUT_SECONDS}.")
    if not 0 <= args.lock_wait_seconds <= MAX_LOCK_WAIT_SECONDS:
        parser.error(f"--lock-wait-seconds must be between 0 and {MAX_LOCK_WAIT_SECONDS}.")

    result = _initial_result(args.source_commit)
    evidence: Path | None = None
    lock_info = None
    try:
        evidence = _ensure_evidence(args.evidence_dir)
        result["evidenceDir"] = str(evidence)
        feed = _ensure_feed(args.nuget_feed, evidence)
        result["nugetFeed"] = {"path": str(feed), "before": inventory_tree(feed, "provided local NuGet feed")}
        source_before = verify_source_pin(args.source_commit)
        result["sourceBefore"] = source_before
        deadline = Deadline(args.timeout_seconds)
        with acquire_lock(args.heavy_lock, deadline, args.lock_wait_seconds) as lock_info:
            result["lock"] = lock_info
            source_locked = verify_source_pin(args.source_commit)
            if source_locked != source_before:
                raise ProbeError("Source pin changed while waiting for heavy.lock.")
            result = execute(args, evidence, feed, source_before, result)
        result["nugetFeed"]["after"] = inventory_tree(feed, "provided local NuGet feed after run")
        if result["nugetFeed"]["before"]["files"] != result["nugetFeed"]["after"]["files"]:
            raise ProbeError("Provided local NuGet feed changed during the gate.")
        result["passed"] = True
        result["exitCode"] = 0
        _atomic_json(evidence / "RESULT.json", result)
        print(json.dumps({"passed": True, "scope": SCOPE, "evidence": str(evidence / "RESULT.json")}, sort_keys=True))
        return 0
    except (ProbeError, OSError, ValueError, ET.ParseError) as error:
        result["error"] = {"type": type(error).__name__, "message": str(error)}
        result["exitCode"] = 2 if isinstance(error, UnsupportedEnvironment) else 1
        if evidence is not None:
            try:
                if "nugetFeed" in result and "after" not in result["nugetFeed"]:
                    result["nugetFeed"]["after"] = inventory_tree(Path(result["nugetFeed"]["path"]), "provided local NuGet feed after failure")
                _atomic_json(evidence / "RESULT.json", result)
            except (OSError, ProbeError) as report_error:
                result["reportWriteError"] = f"{type(report_error).__name__}: {report_error}"
        print(json.dumps({"passed": False, "scope": SCOPE, "error": result["error"]}, sort_keys=True), file=sys.stderr)
        return result["exitCode"]
    except KeyboardInterrupt:
        result["error"] = {"type": "KeyboardInterrupt", "message": "interrupted"}
        result["exitCode"] = 130
        if evidence is not None:
            _atomic_json(evidence / "RESULT.json", result)
        return 130


def _atomic_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    encoded = (json.dumps(value, indent=2, sort_keys=True) + "\n").encode("utf-8")
    temporary: Path | None = None
    try:
        with path.with_name(f".{path.name}.tmp").open("wb") as handle:
            temporary = Path(handle.name)
            handle.write(encoded)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()


if __name__ == "__main__":
    raise SystemExit(main())
