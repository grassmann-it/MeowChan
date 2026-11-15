#!/usr/bin/env python3
"""Run the offline .NET test shards with bounded, cross-platform cleanup.

The runner deliberately owns process lifetime instead of relying on a shell
``timeout`` implementation.  Each dotnet invocation gets its own process
session/group, and all eight shard workers are allowed to finish independently.
"""

from __future__ import annotations

import argparse
import json
import os
import signal
import subprocess
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import asdict, dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable, Iterable, Mapping, Sequence


# Keep every solution test project in exactly one group.  The two largest
# process-heavy assemblies remain alone; the smaller assemblies share workers
# but are still executed sequentially within their shard.
SHARDS: Mapping[str, tuple[str, ...]] = {
    "shard-01": ("tests/MeowChan.Tests/MeowChan.Tests.csproj",),
    "shard-02": ("tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj",),
    "shard-03": ("tests/MeowChan.PluginRuntime.Tests/MeowChan.PluginRuntime.Tests.csproj",),
    "shard-04": ("tests/MeowChan.PluginFilters.Tests/MeowChan.PluginFilters.Tests.csproj",),
    "shard-05": ("tests/MeowChan.SourceAdapter.Tests/MeowChan.SourceAdapter.Tests.csproj",),
    "shard-06": (
        "tests/MeowChan.OfficialPlugins.Tests/MeowChan.OfficialPlugins.Tests.csproj",
        "tests/MeowChan.ExamplePlugins.Tests/MeowChan.ExamplePlugins.Tests.csproj",
    ),
    "shard-07": (
        "tests/MeowChan.PluginCompiler.Tests/MeowChan.PluginCompiler.Tests.csproj",
        "tests/MeowChan.PluginHost.Configuration.Tests/MeowChan.PluginHost.Configuration.Tests.csproj",
        "tests/MeowChan.PluginSdk.Tests/MeowChan.PluginSdk.Tests.csproj",
    ),
    "shard-08": (
        "tests/MeowChan.ImageHeader.Tests/MeowChan.ImageHeader.Tests.csproj",
        "tests/MeowChan.MetadataFilters.Tests/MeowChan.MetadataFilters.Tests.csproj",
        "tests/MeowChan.OriginalMediaHttp.Tests/MeowChan.OriginalMediaHttp.Tests.csproj",
    ),
}

LIGHT_SHARDS = frozenset(("shard-06", "shard-07", "shard-08"))
DEFAULT_SHARD_TIMEOUT_SECONDS = 1500.0
DEFAULT_LIGHT_SHARD_TIMEOUT_SECONDS = 900.0
DEFAULT_PROJECT_TIMEOUT_SECONDS = 1500.0
DEFAULT_LIGHT_PROJECT_TIMEOUT_SECONDS = 900.0
DEFAULT_CLEANUP_GRACE_SECONDS = 30.0


@dataclass
class ProjectResult:
    project: str
    project_name: str
    status: str
    exit_code: int | None
    duration_seconds: float
    timeout_seconds: float
    results_directory: str
    log_file: str
    command: list[str]
    error: str | None = None


@dataclass
class ShardResult:
    shard: str
    status: str
    exit_code: int
    duration_seconds: float
    timeout_seconds: float
    projects: list[dict] = field(default_factory=list)
    error: str | None = None


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def process_creation_kwargs(is_windows: bool | None = None) -> dict[str, object]:
    """Return subprocess isolation flags for the current platform."""

    if is_windows is None:
        is_windows = os.name == "nt"
    if is_windows:
        # CREATE_NEW_PROCESS_GROUP lets CTRL_BREAK reach console-aware tools;
        # taskkill /T below is the fallback for processes that ignore it.
        return {"creationflags": getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)}
    # setsid makes the dotnet host the leader of a private process group/session.
    return {"start_new_session": True}


def _wait_for_exit(process: subprocess.Popen, timeout: float) -> bool:
    try:
        process.wait(timeout=max(0.0, timeout))
        return True
    except subprocess.TimeoutExpired:
        return False


def terminate_process_tree(
    process: subprocess.Popen,
    *,
    cleanup_grace_seconds: float = DEFAULT_CLEANUP_GRACE_SECONDS,
    is_windows: bool | None = None,
) -> None:
    """Terminate *process* and descendants without waiting indefinitely.

    POSIX uses the private process group created by ``start_new_session``.
    Windows first gives a new process group CTRL_BREAK, then uses taskkill's
    documented tree switch, and finally falls back to Popen.kill().
    """

    if is_windows is None:
        is_windows = os.name == "nt"
    grace = max(0.0, cleanup_grace_seconds)

    if is_windows:
        try:
            ctrl_break = getattr(signal, "CTRL_BREAK_EVENT")
            process.send_signal(ctrl_break)
        except (AttributeError, OSError, ProcessLookupError, ValueError):
            pass
        if _wait_for_exit(process, grace):
            return
        try:
            subprocess.run(
                ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                check=False,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
        except OSError:
            pass
        if _wait_for_exit(process, grace):
            return
        try:
            process.kill()
        except (OSError, ProcessLookupError):
            pass
        _wait_for_exit(process, grace)
        return

    try:
        process_group = os.getpgid(process.pid)
    except (OSError, ProcessLookupError):
        process_group = None
    try:
        if process_group is not None:
            os.killpg(process_group, signal.SIGTERM)
        else:
            process.terminate()
    except (OSError, ProcessLookupError):
        pass
    if _wait_for_exit(process, grace):
        return
    try:
        if process_group is not None:
            os.killpg(process_group, signal.SIGKILL)
        else:
            process.kill()
    except (OSError, ProcessLookupError):
        pass
    _wait_for_exit(process, grace)


def build_test_command(dotnet: str, project: str, results_directory: Path) -> list[str]:
    project_name = Path(project).stem
    return [
        dotnet,
        "test",
        project,
        "--configuration",
        "Release",
        "--no-build",
        "--no-restore",
        "-m:1",
        "--logger",
        f"trx;LogFileName={project_name}.trx",
        "--logger",
        "console;verbosity=normal",
        "--blame-hang",
        "--blame-hang-timeout",
        "2min",
        "--blame-hang-dump-type",
        "mini",
        "--results-directory",
        str(results_directory),
    ]


def _write_json(path: Path, payload: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def _append_log(path: Path, message: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as stream:
        stream.write(message.rstrip("\n") + "\n")


def _project_result(
    *,
    project: str,
    status: str,
    exit_code: int | None,
    duration_seconds: float,
    timeout_seconds: float,
    results_directory: Path,
    log_file: Path,
    command: Sequence[str],
    error: str | None = None,
) -> ProjectResult:
    result = ProjectResult(
        project=project,
        project_name=Path(project).stem,
        status=status,
        exit_code=exit_code,
        duration_seconds=round(max(0.0, duration_seconds), 3),
        timeout_seconds=timeout_seconds,
        results_directory=str(results_directory),
        log_file=str(log_file),
        command=list(command),
        error=error,
    )
    _write_json(results_directory / "status.json", asdict(result))
    return result


def skipped_project(
    project: str,
    *,
    results_directory: Path,
    timeout_seconds: float,
    reason: str,
) -> ProjectResult:
    results_directory.mkdir(parents=True, exist_ok=True)
    log_file = results_directory / "project.log"
    command = build_test_command("dotnet", project, results_directory)
    _append_log(log_file, f"SKIPPED project={project} reason={reason}")
    return _project_result(
        project=project,
        status="skipped",
        exit_code=None,
        duration_seconds=0.0,
        timeout_seconds=timeout_seconds,
        results_directory=results_directory,
        log_file=log_file,
        command=command,
        error=reason,
    )


def run_project(
    project: str,
    *,
    shard_directory: Path,
    repo_root: Path,
    dotnet: str,
    timeout_seconds: float,
    cleanup_grace_seconds: float,
    popen_factory: Callable[..., subprocess.Popen] = subprocess.Popen,
    is_windows: bool | None = None,
) -> ProjectResult:
    """Run one project, recording its own TRX directory and process log."""

    project_name = Path(project).stem
    results_directory = shard_directory / project_name
    results_directory.mkdir(parents=True, exist_ok=True)
    log_file = results_directory / "project.log"
    command = build_test_command(dotnet, project, results_directory)
    _append_log(log_file, f"BEGIN project={project} timeout_seconds={timeout_seconds}")
    started = time.monotonic()
    process = None
    try:
        with log_file.open("ab") as output:
            process = popen_factory(
                command,
                cwd=str(repo_root),
                stdout=output,
                stderr=subprocess.STDOUT,
                **process_creation_kwargs(is_windows),
            )
            try:
                process.wait(timeout=max(0.001, timeout_seconds))
            except subprocess.TimeoutExpired:
                terminate_process_tree(
                    process,
                    cleanup_grace_seconds=cleanup_grace_seconds,
                    is_windows=is_windows,
                )
                duration = time.monotonic() - started
                _append_log(log_file, f"TIMEOUT project={project} duration_seconds={duration:.3f}")
                result = _project_result(
                    project=project,
                    status="timed_out",
                    exit_code=124,
                    duration_seconds=duration,
                    timeout_seconds=timeout_seconds,
                    results_directory=results_directory,
                    log_file=log_file,
                    command=command,
                    error="hard deadline exceeded; process tree terminated",
                )
                return result
            exit_code = process.returncode
    except OSError as exc:
        duration = time.monotonic() - started
        _append_log(log_file, f"RUNNER_ERROR project={project} error={exc!r}")
        return _project_result(
            project=project,
            status="runner_error",
            exit_code=127,
            duration_seconds=duration,
            timeout_seconds=timeout_seconds,
            results_directory=results_directory,
            log_file=log_file,
            command=command,
            error=str(exc),
        )

    duration = time.monotonic() - started
    status = "passed" if exit_code == 0 else "failed"
    _append_log(log_file, f"END project={project} status={status} exit_code={exit_code}")
    return _project_result(
        project=project,
        status=status,
        exit_code=exit_code,
        duration_seconds=duration,
        timeout_seconds=timeout_seconds,
        results_directory=results_directory,
        log_file=log_file,
        command=command,
    )


def _error_project(
    project: str,
    *,
    shard_directory: Path,
    timeout_seconds: float,
    error: str,
) -> ProjectResult:
    results_directory = shard_directory / Path(project).stem
    results_directory.mkdir(parents=True, exist_ok=True)
    log_file = results_directory / "project.log"
    command = build_test_command("dotnet", project, results_directory)
    _append_log(log_file, f"RUNNER_ERROR project={project} error={error}")
    return _project_result(
        project=project,
        status="runner_error",
        exit_code=127,
        duration_seconds=0.0,
        timeout_seconds=timeout_seconds,
        results_directory=results_directory,
        log_file=log_file,
        command=command,
        error=error,
    )


def run_shard(
    shard: str,
    projects: Iterable[str],
    *,
    results_root: Path,
    repo_root: Path,
    dotnet: str,
    shard_timeout_seconds: float,
    project_timeout_seconds: float,
    cleanup_grace_seconds: float,
    project_runner: Callable[..., ProjectResult] = run_project,
    is_windows: bool | None = None,
) -> ShardResult:
    """Run projects sequentially, bounded by both project and shard deadlines."""

    shard_directory = results_root / shard
    shard_directory.mkdir(parents=True, exist_ok=True)
    shard_log = shard_directory / "shard.log"
    started = time.monotonic()
    projects = tuple(projects)
    results: list[ProjectResult] = []
    _append_log(shard_log, f"BEGIN shard={shard} timeout_seconds={shard_timeout_seconds}")

    for project in projects:
        elapsed = time.monotonic() - started
        remaining = shard_timeout_seconds - elapsed
        if remaining <= 0:
            result = skipped_project(
                project,
                results_directory=shard_directory / Path(project).stem,
                timeout_seconds=project_timeout_seconds,
                reason="shard hard deadline exhausted",
            )
        else:
            try:
                result = project_runner(
                    project,
                    shard_directory=shard_directory,
                    repo_root=repo_root,
                    dotnet=dotnet,
                    timeout_seconds=min(project_timeout_seconds, remaining),
                    cleanup_grace_seconds=cleanup_grace_seconds,
                    is_windows=is_windows,
                )
            except Exception as exc:  # keep later projects in this shard collectible
                result = _error_project(
                    project,
                    shard_directory=shard_directory,
                    timeout_seconds=min(project_timeout_seconds, remaining),
                    error=repr(exc),
                )
        results.append(result)
        _append_log(
            shard_log,
            f"END project={project} status={result.status} exit_code={result.exit_code}",
        )

    duration = time.monotonic() - started
    status = "passed" if results and all(result.status == "passed" for result in results) else "failed"
    exit_code = 0 if status == "passed" else 1
    shard_result = ShardResult(
        shard=shard,
        status=status,
        exit_code=exit_code,
        duration_seconds=round(duration, 3),
        timeout_seconds=shard_timeout_seconds,
        projects=[asdict(result) for result in results],
    )
    _write_json(shard_directory / "status.json", asdict(shard_result))
    _append_log(shard_log, f"END shard={shard} status={status} exit_code={exit_code}")
    return shard_result


def _failed_shard_result(
    shard: str, *, results_root: Path, timeout_seconds: float, error: str
) -> ShardResult:
    shard_directory = results_root / shard
    shard_directory.mkdir(parents=True, exist_ok=True)
    _append_log(shard_directory / "shard.log", f"RUNNER_ERROR shard={shard} error={error}")
    result = ShardResult(
        shard=shard,
        status="runner_error",
        exit_code=1,
        duration_seconds=0.0,
        timeout_seconds=timeout_seconds,
        error=error,
    )
    _write_json(shard_directory / "status.json", asdict(result))
    return result


def run_all(
    *,
    results_root: Path,
    repo_root: Path,
    dotnet: str,
    shard_timeout_seconds: float,
    light_shard_timeout_seconds: float,
    project_timeout_seconds: float,
    light_project_timeout_seconds: float,
    cleanup_grace_seconds: float,
    shard_runner: Callable[..., ShardResult] = run_shard,
    is_windows: bool | None = None,
) -> int:
    """Launch all shards concurrently and aggregate every result."""

    results_root.mkdir(parents=True, exist_ok=True)
    started_at = utc_now()
    started = time.monotonic()
    shard_results: dict[str, ShardResult] = {}

    with ThreadPoolExecutor(max_workers=len(SHARDS), thread_name_prefix="dotnet-shard") as pool:
        futures = {}
        for shard, projects in SHARDS.items():
            light = shard in LIGHT_SHARDS
            futures[pool.submit(
                shard_runner,
                shard,
                projects,
                results_root=results_root,
                repo_root=repo_root,
                dotnet=dotnet,
                shard_timeout_seconds=(light_shard_timeout_seconds if light else shard_timeout_seconds),
                project_timeout_seconds=(light_project_timeout_seconds if light else project_timeout_seconds),
                cleanup_grace_seconds=cleanup_grace_seconds,
                is_windows=is_windows,
            )] = shard

        # as_completed deliberately waits for every independent shard, even if
        # one future fails or a different shard has already timed out.
        for future in as_completed(futures):
            shard = futures[future]
            timeout = light_shard_timeout_seconds if shard in LIGHT_SHARDS else shard_timeout_seconds
            try:
                shard_results[shard] = future.result()
            except Exception as exc:  # evidence must survive a runner defect
                shard_results[shard] = _failed_shard_result(
                    shard, results_root=results_root, timeout_seconds=timeout, error=repr(exc)
                )
            print(
                f"{shard}: {shard_results[shard].status} "
                f"({shard_results[shard].duration_seconds:.1f}s)",
                flush=True,
            )

    ordered_results = [asdict(shard_results[shard]) for shard in SHARDS]
    aggregate_status = "passed" if all(item["status"] == "passed" for item in ordered_results) else "failed"
    aggregate = {
        "schema_version": 1,
        "runner": "scripts/run-dotnet-shards.py",
        "started_at": started_at,
        "completed_at": utc_now(),
        "duration_seconds": round(time.monotonic() - started, 3),
        "platform": {"os_name": os.name, "platform": sys.platform},
        "dotnet": dotnet,
        "project_timeout_seconds": project_timeout_seconds,
        "light_project_timeout_seconds": light_project_timeout_seconds,
        "shard_timeout_seconds": shard_timeout_seconds,
        "light_shard_timeout_seconds": light_shard_timeout_seconds,
        "cleanup_grace_seconds": cleanup_grace_seconds,
        "aggregate_status": aggregate_status,
        "exit_code": 0 if aggregate_status == "passed" else 1,
        "shards": ordered_results,
    }
    _write_json(results_root / "aggregate-status.json", aggregate)
    _write_json(results_root / "aggregate.json", aggregate)
    (results_root / "aggregate-status.txt").write_text(
        f"aggregate_status={aggregate_status}\n"
        f"exit_code={aggregate['exit_code']}\n"
        f"shards={len(ordered_results)}\n",
        encoding="utf-8",
    )
    with (results_root / "shard-status.tsv").open("w", encoding="utf-8") as stream:
        stream.write("shard\tstatus\texit_code\tduration_seconds\n")
        for item in ordered_results:
            stream.write(
                f"{item['shard']}\t{item['status']}\t{item['exit_code']}\t"
                f"{item['duration_seconds']}\n"
            )
    print(json.dumps(aggregate, indent=2, sort_keys=True))
    return int(aggregate["exit_code"])


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results-root", type=Path, default=Path("test-results/offline-shards"))
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--dotnet", default=os.environ.get("DOTNET", "dotnet"))
    parser.add_argument("--shard-timeout", type=float, default=DEFAULT_SHARD_TIMEOUT_SECONDS)
    parser.add_argument("--light-shard-timeout", type=float, default=DEFAULT_LIGHT_SHARD_TIMEOUT_SECONDS)
    parser.add_argument("--project-timeout", type=float, default=DEFAULT_PROJECT_TIMEOUT_SECONDS)
    parser.add_argument("--light-project-timeout", type=float, default=DEFAULT_LIGHT_PROJECT_TIMEOUT_SECONDS)
    parser.add_argument("--cleanup-grace", type=float, default=DEFAULT_CLEANUP_GRACE_SECONDS)
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv)
    return run_all(
        results_root=args.results_root,
        repo_root=args.repo_root,
        dotnet=args.dotnet,
        shard_timeout_seconds=args.shard_timeout,
        light_shard_timeout_seconds=args.light_shard_timeout,
        project_timeout_seconds=args.project_timeout,
        light_project_timeout_seconds=args.light_project_timeout,
        cleanup_grace_seconds=args.cleanup_grace,
    )


if __name__ == "__main__":
    raise SystemExit(main())
