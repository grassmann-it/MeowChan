#!/usr/bin/env python3
"""Source-built GUI checks. Linux uses an isolated display; stress is opt-in.

These checks do not execute published apphosts. Never fall back to a user's
DISPLAY when the virtual-display tools are missing.
"""
from __future__ import annotations

import argparse
import contextlib
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time
from xml.sax.saxutils import escape


STANDARD_MODES = ("normal", "locked-profile", "no-tray", "tray-failure", "download-session")
STRESS_MODE = "tray-lifecycle"
ISOLATED_TOOLS = ("dbus-run-session", "xvfb-run", "Xvfb", "xauth", "openbox", "xprop")
SESSION_VARIABLES = ("DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "DBUS_SESSION_BUS_ADDRESS",
                     "DBUS_STARTER_ADDRESS", "DBUS_STARTER_BUS_TYPE", "SESSION_MANAGER", "AT_SPI_BUS_ADDRESS",
                     "XDG_CURRENT_DESKTOP", "XDG_SESSION_DESKTOP", "XDG_SESSION_ID", "DESKTOP_SESSION", "KDE_FULL_SESSION")
# Mirrors SmokeSelectors.BranchSelectors: scenario selectors without the _ONLY suffix.
BRANCH_SELECTORS = (("MEOWCHAN_TEMPLATE_COMPARISON", "1"), ("MEOWCHAN_ARCHIVE_COMPARISON", "1"),
                    ("MEOWCHAN_MEDIA_FIT_BENCHMARK", "1"), ("MEOWCHAN_MEDIA_SCALE_BENCHMARK", "1"),
                    ("MEOWCHAN_URSA_NATIVE_SUITE", "groups"))
# SmokeScenarios.FunctionalAudio: such registry entries run inside the private null-audio wrapper, as in CI.
FUNCTIONAL_AUDIO = "MEOWCHAN_REQUIRE_FUNCTIONAL_AUDIO=1"
# scripts/with-isolated-window-manager.py: the known sporadic startup failures of the private window manager.
WINDOW_MANAGER_STARTUP_FLAKE = re.compile(r"The isolated window manager (exited during startup|did not become ready)")


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assembly", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--timeout", type=float, default=60)
    parser.add_argument("--mode", action="append", choices=(*STANDARD_MODES, "visual", "reader-ux", "selection", "tray-host", "about-links", "board-admission", "reading-position", "window-geometry", "video-contrast", "plugins", "archive-cleanup", "shell-load", STRESS_MODE),
                        help="Select a scenario; repeat for a serial sequence. Default excludes stress.")
    parser.add_argument("--native-display", action="store_true",
                        help="Use a dedicated Linux desktop for one explicitly selected scenario.")
    parser.add_argument("--allow-native-desktop", action="store_true",
                        help="Explicitly opt into native windows on a disposable desktop/CI (required outside Xvfb).")
    parser.add_argument("--scale", choices=("1", "1.5", "2"))
    parser.add_argument("--window-manager", choices=("openbox", "kwin", "kwin-nested"), default="openbox",
                        help="Isolated Linux WM: Openbox, headless KWin, or KWin nested in private Xvfb; KWin apps use XWayland.")
    parser.add_argument("--expect-scenario",
                        help="Require this exact 'SMOKE SCENARIO:' id (one --mode only). By default the id is derived "
                             "from the mode and the MEOWCHAN_*_ONLY selectors in the environment.")
    parser.add_argument("--registry", action="store_true",
                        help="Run every entry of the assembly's scenario registry (--list-scenarios) as its own isolated "
                             "Linux run and continue after failures; entries the registry marks as skipped are recorded.")
    parser.add_argument("--only", action="append", default=[], metavar="ID",
                        help="With --registry: run only these registry ids (repeatable).")
    args = parser.parse_args(argv)
    if args.registry:
        if args.mode or args.expect_scenario is not None or args.native_display or args.scale is not None:
            parser.error("--registry selects modes, scenarios and scale per entry; omit --mode, --expect-scenario, --scale and --native-display")
        if not sys.platform.startswith("linux"):
            parser.error("--registry runs only on Linux in the isolated runner")
    elif args.only:
        parser.error("--only requires --registry")
    if not math.isfinite(args.timeout) or args.timeout <= 0:
        parser.error("--timeout must be finite and positive")
    args.modes = tuple(dict.fromkeys(args.mode or STANDARD_MODES))
    if args.window_manager != "openbox" and not sys.platform.startswith("linux"):
        parser.error("--window-manager selects an isolated Linux session only")
    if args.expect_scenario is not None and len(args.modes) != 1:
        parser.error("--expect-scenario requires exactly one --mode")
    if STRESS_MODE in args.modes and len(args.modes) != 1:
        parser.error("tray-lifecycle must run separately with --mode tray-lifecycle")
    if "tray-host" in args.modes and (not sys.platform.startswith("linux") or args.native_display):
        parser.error("tray-host requires Linux with the isolated DBus/Xvfb runner")
    if args.native_display:
        if args.window_manager != "openbox":
            parser.error("--window-manager selects an isolated session only")
        if not sys.platform.startswith("linux"):
            parser.error("--native-display is only supported on Linux")
        if not args.mode or len(args.modes) != 1:
            parser.error("--native-display requires exactly one explicitly selected --mode")
        if not os.environ.get("DISPLAY", "").strip():
            parser.error("--native-display requires an X11/XWayland DISPLAY in a dedicated session")
    if (not sys.platform.startswith("linux") or args.native_display) and not args.allow_native_desktop:
        parser.error("Native GUI checks require --allow-native-desktop on a disposable test desktop/CI")
    return args


# AF_UNIX sun_path holds 108 bytes including the NUL. The longest socket the isolated session creates below its
# private directory is the nested KWin Wayland lock file; dbus-daemon's "dbus-XXXXXXXX" is shorter.
SOCKET_PATH_LIMIT = 107
LONGEST_SOCKET_SUFFIX = "/runtime/meowchan-smoke-wayland.lock"
SHORT_DIRECTORY_BASE = "/tmp"


def socket_paths_fit(directory: str) -> bool:
    return len(os.fsencode(directory)) + len(LONGEST_SOCKET_SUFFIX) <= SOCKET_PATH_LIMIT


def private_working_directory(isolated: bool) -> tempfile.TemporaryDirectory:
    """The per-run private directory (profile, XDG dirs, bus.conf, bus socket).

    With a long TMPDIR the private session bus cannot bind below it: dbus-daemon stops with "Socket name too long"
    and dbus-run-session only reports "EOF reading address from bus daemon". Only then this run's directory moves
    to a short base; a TMPDIR that fits keeps it. The native (non-isolated) run starts no bus."""
    directory = tempfile.TemporaryDirectory(prefix="meowchan-smoke-cwd-")
    if not isolated or socket_paths_fit(directory.name):
        return directory
    long_name = directory.name
    directory.cleanup()
    short = tempfile.TemporaryDirectory(prefix="mcs-", dir=SHORT_DIRECTORY_BASE)
    if not socket_paths_fit(short.name):
        short.cleanup()
        raise ValueError(f"TMPDIR {long_name!r} is too long for the private session bus sockets "
                         f"(limit {SOCKET_PATH_LIMIT} bytes) and {SHORT_DIRECTORY_BASE} is no short fallback")
    return short


def prepare_environment(base: dict[str, str], isolated: bool, directory: Path) -> dict[str, str]:
    # A fixture/application child must never inherit routing to the project Git index.
    environment = {key: value for key, value in base.items() if not key.startswith("GIT_")}
    environment["MEOWCHAN_DISABLE_UPDATE_CHECK"] = "1"
    environment.pop("MEOWCHAN_PROFILE", None)
    environment.pop("OCHAN_PROFILE", None)
    environment["MEOWCHAN_PROFILE"] = str((directory / "profile").resolve())
    # GUI fixtures must never schedule real update requests in their private profiles.
    environment["MEOWCHAN_DISABLE_UPDATE_CHECK"] = "1"
    # D1: captures and smokes run with motion off, so no screenshot or timing depends on a transition; the "motion" smoke
    # (MEOWCHAN_MOTION_ONLY=1) removes it inside its own process, and an explicit value from the caller is kept.
    environment.setdefault("MEOWCHAN_MOTION", "off")
    if isolated:
        for name in SESSION_VARIABLES:
            environment.pop(name, None)
        # Profiles alone do not isolate the window manager, bus, or desktop services.
        for name, child in (("XDG_RUNTIME_DIR", "runtime"), ("XDG_CONFIG_HOME", "config"),
                            ("XDG_CACHE_HOME", "cache"), ("XDG_DATA_HOME", "data")):
            location = directory / child
            location.mkdir(mode=0o700)
            environment[name] = str(location)
        environment["MEOWCHAN_DESKTOP_SMOKE_XVFB"] = "1"
        environment["XDG_SESSION_TYPE"] = "x11"
        # No service directories: the test bus must not auto-start desktop portals,
        # accessibility daemons or user-systemd services from the host installation.
        (directory / "bus.conf").write_text(
            '<busconfig><type>session</type><listen>unix:tmpdir='
            + escape(environment["XDG_RUNTIME_DIR"]) + '</listen><auth>EXTERNAL</auth>'
            '<policy context="default"><allow send_destination="*" eavesdrop="true"/>'
            '<allow eavesdrop="true"/><allow own="*"/></policy></busconfig>', encoding="utf-8")
    else:
        environment.pop("MEOWCHAN_DESKTOP_SMOKE_XVFB", None)
    return environment


def owned_process_group_is_empty(process: subprocess.Popen) -> bool:
    """Confirm absence, not permission to signal, using a complete POSIX process table.

    Do not use ``ps -g``: its selector is not portable between BSD/macOS and
    Linux, and a no-match exit status is not proof that the query succeeded.
    Only an exited leader plus a successful, well-formed inventory is evidence.
    """
    if os.name == "nt" or process.poll() is None:
        return False
    try:
        probe = subprocess.run(["ps", "-A", "-o", "pid=,pgid="],
                               capture_output=True, text=True, check=False, timeout=5)
        if probe.returncode != 0 or not probe.stdout.strip():
            return False
        groups = []
        for line in probe.stdout.splitlines():
            pid, pgid = map(int, line.split())
            if pid <= 0 or pgid <= 0:
                return False
            groups.append(pgid)
        return process.pid not in groups
    except (OSError, subprocess.SubprocessError, ValueError):
        return False


def stop_owned_processes(process: subprocess.Popen) -> None:
    """Only signal the tree/group created by Popen(start_new_session=True).

    Never rediscover ownership from a name or current parent PID, nor send a
    final signal after observing group disappearance. A reaped leader alone
    does not prove that its descendants have exited.
    """
    if os.name == "nt":
        if process.poll() is None:
            result = subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], check=False,
                                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=10)
            if result.returncode != 0:
                raise OSError(f"taskkill failed for owned process tree (exit {result.returncode})")
        process.wait(timeout=10)
        return

    def signal_group(sig: int) -> bool:
        try:
            os.killpg(process.pid, sig)
            return True
        except ProcessLookupError:
            return False
        except PermissionError:
            # EPERM is not absence. Accept only independently verified absence;
            # an alive child, failed ps, or malformed inventory remains fatal.
            if owned_process_group_is_empty(process):
                return False
            raise

    def wait_for_group(seconds: float) -> bool:
        deadline = time.monotonic() + seconds
        while True:
            process.poll()  # Reap the leader without overlooking descendants.
            if not signal_group(0):
                return True
            if time.monotonic() >= deadline:
                return False
            time.sleep(0.05)

    if signal_group(signal.SIGTERM) and not wait_for_group(2):
        if signal_group(signal.SIGKILL) and not wait_for_group(10):
            raise subprocess.TimeoutExpired("owned process group cleanup", 10)
    process.wait(timeout=10)


def host_load() -> dict[str, object]:
    """Host load at a run's start/end: a 60 s deadline missed on a busy machine is not a product verdict."""
    try:
        average = os.getloadavg()[0]
    except (AttributeError, OSError):
        average = None
    return {"loadavg_1m": average, "cpus": os.cpu_count()}


def sample_timeout(process: subprocess.Popen, destination: Path) -> None:
    if sys.platform != "darwin":
        return
    with (destination / "timeout-sample.log").open("w", encoding="utf-8") as log:
        try:
            sample = shutil.which("sample")
            if sample:
                subprocess.run([sample, str(process.pid), "3", "1", "-file", str(destination / "timeout-sample.txt")],
                               timeout=8, check=False, stdout=log, stderr=subprocess.STDOUT)
            else:
                log.write("macOS sample tool is unavailable.\n")
        except (OSError, subprocess.TimeoutExpired) as exception:
            log.write(f"Could not collect timeout sample: {exception}\n")


XVFB_TEARDOWN_KILL = re.compile(r"xvfb-run: line [0-9]+: kill: \([0-9]+\) - No such process")


def survived_xvfb_teardown(exit_code: int, exit_file: Path, log_text: str) -> bool:
    """xvfb-run runs under set -e: when its Xvfb is already gone, the final kill fails and xvfb-run exits 1
    instead of passing on the harness status. Only that exact case maps back to the harness's own 0."""
    if exit_code != 1 or not XVFB_TEARDOWN_KILL.search(log_text):
        return False
    try:
        return exit_file.read_text(encoding="utf-8").strip() == "0"
    except OSError:
        return False


def derived_scenario(mode: str, environment: dict[str, str]) -> str:
    """The id DesktopSmoke prints for this mode and selector environment (SmokeSelectors.Check)."""
    tokens = [name for name, value in environment.items()
              if name.startswith("MEOWCHAN_") and name.endswith("_ONLY") and value == "1"]
    tokens += [name if value == "1" else name + "=" + value
               for name, value in BRANCH_SELECTORS if environment.get(name) == value]
    return "+".join([mode, *sorted(tokens)])


def check_scenario(log_text: str, expected: str, explicit: bool) -> tuple[str | None, str | None]:
    """Return (observed id, error). Without an explicit id an older assembly may lack the line."""
    observed = re.findall(r"^SMOKE SCENARIO: (\S+)\s*$", log_text, re.MULTILINE)
    if len(observed) > 1:
        return observed[-1], f"Several SMOKE SCENARIO lines: {observed}"
    if not observed:
        return None, f"Missing SMOKE SCENARIO line; expected {expected}" if explicit else None
    if observed[0] != expected:
        return observed[0], f"Ran scenario {observed[0]}; expected {expected}"
    return observed[0], None


@contextlib.contextmanager
def capture_bin_lock(directory: Path):
    """The fixtures write smoke-*.png/json next to the assembly and the runner collects them by mtime, so two
    concurrent runs of one build would collect each other's captures. Runs sharing a bin take turns (POSIX
    flock, released by the kernel if the runner dies); the wait is outside every run's own deadline."""
    try:
        import fcntl
    except ImportError:  # Windows: no cross-process lock here; callers must not share a bin concurrently.
        yield
        return
    with (directory / ".smoke-capture.lock").open("w") as handle:
        waited = time.monotonic()
        fcntl.flock(handle, fcntl.LOCK_EX)
        if time.monotonic() - waited > 1:
            print(f"Desktop harness waited {time.monotonic() - waited:.0f}s for another run using {directory}.", flush=True)
        try:
            yield
        finally:
            fcntl.flock(handle, fcntl.LOCK_UN)


def run_mode(mode: str, assembly: Path, destination: Path, environment: dict[str, str],
             isolated: bool, timeout: float, expected_scenario: str | None = None) -> dict[str, object]:
    with capture_bin_lock(assembly.parent):
        return run_mode_locked(mode, assembly, destination, environment, isolated, timeout, expected_scenario)


def run_mode_locked(mode: str, assembly: Path, destination: Path, environment: dict[str, str],
             isolated: bool, timeout: float, expected_scenario: str | None = None) -> dict[str, object]:
    destination = destination.resolve()
    destination.mkdir()
    command = ["dotnet", str(assembly)]
    if mode != "normal":
        command.append("--" + mode)
    previous = {image: image.stat().st_mtime_ns for pattern in ("smoke-*.png", "v0.9.0-*.png")
                for image in assembly.parent.glob(pattern)}
    timed_out = False
    interrupted = False
    error = None
    exit_code = 1
    load_at_start = host_load()
    with private_working_directory(isolated) as working_directory:
        child_environment = prepare_environment(environment, isolated, Path(working_directory))
        scenario = expected_scenario or derived_scenario(mode, child_environment)
        if not isolated:
            child_environment["MEOWCHAN_DESKTOP_SMOKE_NATIVE_ALLOWED"] = "1"
        if mode == "shell-load":
            # Bind current source bytes and all sibling managed/native dependencies, even for a standalone cell.
            spec = importlib.util.spec_from_file_location("shell_matrix_binding", Path(__file__).with_name("run-shell-load-matrix.py"))
            binding_module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(binding_module)
            source_root = Path(environment.get("MEOWCHAN_SHELL_LOAD_SOURCE_ROOT", Path(__file__).resolve().parents[1])).resolve()
            source_binding = binding_module.source_inventory(source_root)
            expected = environment.get("MEOWCHAN_SHELL_LOAD_SOURCE_HASH")
            if expected and expected != source_binding["sha256"]:
                raise ValueError("Source changed since A/B inventory")
            binaries = binding_module.binary_inventory(assembly)
            assembly_hash = binding_module.digest(assembly)
            expected_binary = environment.get("MEOWCHAN_SHELL_LOAD_ASSEMBLY_HASH")
            if expected_binary and expected_binary != assembly_hash:
                raise ValueError("Assembly changed since A/B inventory")
            (destination / "binding.json").write_text(json.dumps({"source_root": str(source_root), "source": source_binding,
                "assembly_sha256": assembly_hash, "binary_files": binaries,
                "limit": "Inventory binding; build owner verifies compiler provenance"}, indent=2) + "\n", encoding="utf-8")
            child_environment["MEOWCHAN_SHELL_LOAD_SOURCE_HASH"] = source_binding["sha256"]
            child_environment["MEOWCHAN_SHELL_LOAD_ASSEMBLY_HASH"] = assembly_hash
            child_environment["MEOWCHAN_SHELL_LOAD_PRIVATE_PROFILE"] = str((Path(working_directory) / "profile").resolve())
            child_environment["MEOWCHAN_SHELL_LOAD_REPORT"] = str(destination / "measurement.json")
            child_environment["MEOWCHAN_SHELL_LOAD_HEARTBEAT"] = str(destination / "heartbeat.json")
            watchdog = Path(__file__).with_name("shell-load-watchdog.py").resolve()
            command = [sys.executable, str(watchdog), "--heartbeat", str(destination / "heartbeat.json"),
                       "--report", str(destination / "watchdog.json"), "--timeout", str(max(0.1, timeout - 12)),
                       "--", *command]
        if isolated:
            if mode == "tray-host":
                host = Path(__file__).with_name("with-fake-tray-host.py").resolve()
                command = ["/usr/bin/python3", str(host), "--", *command]
            if environment.get("MEOWCHAN_DESKTOP_SMOKE_KWIN") == "1":
                manager = Path(__file__).with_name("with-isolated-kwin.py").resolve()
                command = [sys.executable, str(manager), "--", *command]
                if environment.get("MEOWCHAN_DESKTOP_SMOKE_KWIN_NESTED") == "1":
                    command = ["xvfb-run", "--auto-servernum", "--server-args=-screen 0 3200x2000x24 -nolisten tcp", *command]
            else:
                manager = Path(__file__).with_name("with-isolated-window-manager.py").resolve()
                child_environment["MEOWCHAN_SMOKE_HARNESS_EXIT_FILE"] = str(destination / "harness-exit-code")
                command = ["xvfb-run", "--auto-servernum",
                           "--server-args=-screen 0 3200x2000x24 -nolisten tcp",
                           sys.executable, str(manager), "--", *command]
            command = ["dbus-run-session", "--config-file=" + str(Path(working_directory) / "bus.conf"), "--", *command]
        with (destination / "output.log").open("w", encoding="utf-8") as log:
            process = None
            try:
                process = subprocess.Popen(command, cwd=working_directory, env=child_environment, stdout=log,
                                           stderr=subprocess.STDOUT, start_new_session=os.name != "nt")
                try:
                    exit_code = process.wait(timeout=timeout)
                except subprocess.TimeoutExpired:
                    timed_out = True
                    sample_timeout(process, destination)
                    exit_code = 124
            except (KeyboardInterrupt, InterruptedError):
                interrupted = True
                exit_code = 130
                error = "Runner interrupted"
            except OSError as exception:
                error = str(exception)
            finally:
                if process is not None:
                    try:
                        stop_owned_processes(process)
                    except (OSError, subprocess.TimeoutExpired) as exception:
                        error = f"Process cleanup failed: {exception}"
    log_text = (destination / "output.log").read_text(encoding="utf-8", errors="replace")
    xvfb_teardown_ignored = not timed_out and not interrupted and survived_xvfb_teardown(
        exit_code, destination / "harness-exit-code", log_text)
    if xvfb_teardown_ignored:
        exit_code = 0
    if timed_out:
        end_load = host_load()
        if end_load["loadavg_1m"] is not None and end_load["cpus"] and end_load["loadavg_1m"] > end_load["cpus"]:
            print(f"Desktop harness {mode}: timed out on an overloaded host (loadavg {end_load['loadavg_1m']:.1f} "
                  f"> {end_load['cpus']} CPUs); rerun with a larger --timeout before treating this as a product failure.",
                  flush=True)
    for image in (image for pattern in ("smoke-*.png", "v0.9.0-*.png") for image in assembly.parent.glob(pattern)):
        if previous.get(image) != image.stat().st_mtime_ns:
            shutil.copy2(image, destination / image.name)
    scales = sorted({float(value) for value in re.findall(
        r"^SMOKE render scaling: actual=([0-9]+(?:\.[0-9]+)?);", log_text, re.MULTILINE)})
    requested_scale = environment.get("AVALONIA_GLOBAL_SCALE_FACTOR")
    if requested_scale is not None:
        try:
            expected_scale = float(requested_scale)
        except ValueError:
            expected_scale = math.nan
        if not math.isfinite(expected_scale) or scales != [expected_scale]:
            error = error or f"Requested scale {requested_scale}; observed {scales}"
    watchdog_result = None
    if mode == "shell-load":
        try:
            watchdog_result = json.loads((destination / "watchdog.json").read_text(encoding="utf-8"))
            if exit_code == 0 and (watchdog_result.get("reason") != "exited" or watchdog_result.get("exit_code") != 0):
                error = error or "External watchdog did not confirm normal child exit"
            if exit_code == 0:
                measurement = json.loads((destination / "measurement.json").read_text(encoding="utf-8"))
                if measurement.get("failure") is not None:
                    error = error or "Measurement reported a failure"
        except (OSError, ValueError) as exception:
            error = error or f"Missing/invalid shell-load evidence: {exception}"
    observed_scenario, scenario_error = check_scenario(log_text, scenario, expected_scenario is not None)
    if "SMOKE PASS:" in log_text:
        error = error or scenario_error
    passed = exit_code == 0 and "SMOKE PASS:" in log_text and error is None
    print(f"Desktop harness {mode}: {'passed' if passed else 'FAILED'} (exit {exit_code}); {destination / 'output.log'}", flush=True)
    if not passed:
        if error:
            print(error, flush=True)
        print("\n".join(log_text.splitlines()[-20:]), flush=True)
    return {"mode": mode, "status": "passed" if passed else "failed", "exit_code": exit_code,
            "timed_out": timed_out, "interrupted": interrupted, "error": error, "passed": passed,
            "xvfb_teardown_ignored": xvfb_teardown_ignored,
            "expected_scenario": scenario, "scenario": observed_scenario,
            "watchdog": watchdog_result,
            "host_load": {"start": load_at_start, "end": host_load()},
            "actual_render_scaling": scales[0] if len(scales) == 1 else None, "observed_render_scalings": scales}


def registry_entries(assembly: Path) -> list[dict[str, object]]:
    """The assembly's own scenario registry; listing creates no profile or window."""
    listing = subprocess.run(["dotnet", str(assembly), "--list-scenarios"], capture_output=True, text=True, timeout=120,
                             env={key: value for key, value in os.environ.items() if not key.startswith("GIT_")})
    if listing.returncode != 0:
        raise ValueError(f"--list-scenarios failed with exit {listing.returncode}: {listing.stderr.strip()}")
    return [json.loads(line) for line in listing.stdout.splitlines() if line.startswith("{")]


def registry_command(entry: dict[str, object], assembly: Path, output: Path, timeout: float, window_manager: str,
                     repository: Path) -> list[str]:
    """One child runner per entry, with exactly its registry environment, as a CI step would start it."""
    def expand_repository(value: object) -> str:
        text = str(value)
        if "{repo}" not in text:
            return text
        prefix, suffix = text.split("{repo}", 1)
        # Registry fixtures use POSIX-looking source paths in the committed
        # manifest. Join the suffix through pathlib so Windows does not emit a
        # mixed ``\\root/tests`` path while POSIX retains its separators.
        return prefix + str(repository / suffix.lstrip("/\\"))

    environment = [expand_repository(value) for value in entry["environment"]]
    command = ["env", *environment, sys.executable, str(Path(__file__).resolve()), "--assembly", str(assembly),
               "--mode", str(entry["mode"]), "--timeout", str(timeout), "--window-manager", window_manager,
               "--expect-scenario", str(entry["scenario"]), "--output", str(output)]
    if FUNCTIONAL_AUDIO in environment:
        audio = Path(__file__).with_name("with-private-null-audio.py").resolve()
        command = [sys.executable, str(audio), "--report", str(output.with_name(output.name + "-audio.json")), "--", *command]
    return command


def run_registry(args: argparse.Namespace) -> int:
    assembly = Path(args.assembly).resolve(strict=True)
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    if any(output.iterdir()):
        raise ValueError("Choose a fresh --output directory; existing evidence is preserved")
    entries = registry_entries(assembly)
    unknown = sorted(set(args.only) - {str(entry["id"]) for entry in entries})
    if unknown:
        raise ValueError("Unknown registry ids: " + ", ".join(unknown))
    # A selector left in the caller's environment would change every entry's scenario.
    base = {key: value for key, value in os.environ.items()
            if not (key.startswith("MEOWCHAN_") and key.endswith("_ONLY"))
            and key not in {name for name, _ in BRANCH_SELECTORS}}
    repository = Path(__file__).resolve().parents[1]
    results: list[dict[str, object]] = []

    def write_report(complete: bool, interrupted: bool = False) -> tuple[list[str], dict[str, int]]:
        # Rewritten after every entry, so a sweep stopped by a job timeout or signal keeps its results.
        failed = [result["id"] for result in results if result["status"] == "failed"]
        counts = {status: sum(result["status"] == status for result in results) for status in ("passed", "failed", "skipped")}
        report = output / "registry.json"
        report.with_suffix(".json.tmp").write_text(json.dumps({
            "kind": "source-built Avalonia harness registry run; not published apphost",
            "platform": sys.platform, "window_manager": args.window_manager, "complete": complete,
            "interrupted": interrupted, "counts": counts, "failed": failed, "results": results
        }, indent=2) + "\n", encoding="utf-8")
        os.replace(report.with_suffix(".json.tmp"), report)
        return failed, counts

    try:
        for entry in entries:
            identifier = str(entry["id"])
            if args.only and identifier not in args.only:
                continue
            if entry.get("error"):
                # The assembly itself would reject this entry's invocation: a registry defect, not a skip.
                results.append({"id": identifier, "kind": entry.get("kind"), "status": "failed", "passed": False,
                                "reason": "invalid registry invocation: " + str(entry["error"]), "attempts": []})
                print(f"Registry {identifier}: FAILED (invalid registry invocation: {entry['error']})", flush=True)
                continue
            if entry.get("skip"):
                results.append({"id": identifier, "kind": entry.get("kind"), "status": "skipped", "passed": None,
                                "reason": entry["skip"]})
                print(f"Registry {identifier}: skipped ({entry['skip']})", flush=True)
                continue
            attempts = []
            for attempt in (1, 2):
                destination = output / (identifier if attempt == 1 else identifier + "-retry")
                command = registry_command(entry, assembly, destination, args.timeout, args.window_manager, repository)
                started = time.monotonic()
                try:
                    completed = subprocess.run(command, env=base, capture_output=True, text=True, timeout=args.timeout + 120)
                    exit_code, log = completed.returncode, completed.stdout + completed.stderr
                except subprocess.TimeoutExpired as expired:
                    exit_code, log = 124, str(expired)
                except OSError as error:
                    # A child that never launched cannot contaminate the next
                    # scene; retain its failure and finish the registry sweep.
                    exit_code, log = 127, f"Registry child launch failed: {error}"
                (output / (destination.name + ".log")).write_text(log, encoding="utf-8")
                attempts.append({"output": str(destination), "exit_code": exit_code, "seconds": round(time.monotonic() - started),
                                 **child_result(destination, entry["mode"])})
                # Only the known sporadic window-manager startup failure is retried, once.
                if exit_code == 0 or not WINDOW_MANAGER_STARTUP_FLAKE.search(log):
                    break
            passed = attempts[-1]["exit_code"] == 0 and attempts[-1].get("child_passed") is True
            results.append({"id": identifier, "kind": entry.get("kind"), "mode": entry["mode"], "scenario": entry["scenario"],
                            "status": "passed" if passed else "failed", "passed": passed, "attempts": attempts})
            print(f"Registry {identifier}: {'passed' if passed else 'FAILED'} ({entry['scenario']})", flush=True)
            write_report(False)
    except (KeyboardInterrupt, InterruptedError):
        failed, counts = write_report(False, interrupted=True)
        print(f"Registry interrupted after {len(results)} entries: " + ", ".join(f"{status} {count}" for status, count in counts.items()), flush=True)
        return 130
    failed, counts = write_report(True)
    print("Registry summary: " + ", ".join(f"{status} {count}" for status, count in counts.items())
          + (" ; failed: " + ", ".join(failed) if failed else ""), flush=True)
    return 1 if failed else 0


SMOKE_FAILURE_LINE = re.compile(r"^SMOKE (?:FAILED|CONFIGURATION ERROR|check failed):.*$", re.MULTILINE)


def child_result(destination: Path, mode: object) -> dict[str, object]:
    """What the entry's own runner recorded: its pass verdict, timeout, observed SMOKE SCENARIO id and error.
    Without a runner error, a failed entry reports the harness's own first failure line."""
    try:
        report = json.loads((destination / "results.json").read_text(encoding="utf-8"))
        result = next(result for result in report["results"] if result.get("mode") == mode)
    except (OSError, ValueError, KeyError, StopIteration):
        return {"child_passed": None, "timed_out": None, "observed_scenario": None,
                "error": "no results.json from the entry runner"}
    error = result.get("error") or result.get("reason")
    if error is None and not result.get("passed"):
        if result.get("timed_out"):
            error = "timed out"
        else:
            try:
                line = SMOKE_FAILURE_LINE.search((destination / str(mode) / "output.log").read_text(encoding="utf-8", errors="replace"))
            except OSError:
                line = None
            error = line.group(0)[:400] if line else f"exit {result.get('exit_code')} without a SMOKE failure line"
    return {"child_passed": result.get("passed"), "timed_out": result.get("timed_out"),
            "observed_scenario": result.get("scenario"), "error": error}


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    if args.registry:
        return run_registry(args)
    isolated = sys.platform.startswith("linux") and not args.native_display
    environment = os.environ.copy()
    environment.pop("MEOWCHAN_DESKTOP_SMOKE_KWIN", None)
    environment.pop("MEOWCHAN_DESKTOP_SMOKE_KWIN_NESTED", None)
    environment.pop("MEOWCHAN_KWIN_INPUT_DISPLAY", None)
    environment.pop("MEOWCHAN_KWIN_INPUT_XAUTHORITY", None)
    if args.window_manager in ("kwin", "kwin-nested"):
        environment["MEOWCHAN_DESKTOP_SMOKE_KWIN"] = "1"
    if args.window_manager == "kwin-nested":
        environment["MEOWCHAN_DESKTOP_SMOKE_KWIN_NESTED"] = "1"
    if args.scale is not None:
        environment["AVALONIA_GLOBAL_SCALE_FACTOR"] = args.scale
    assembly = Path(args.assembly).resolve(strict=True)
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    # Avoid overwriting an earlier failed run, or mixing stale screenshots into new evidence.
    if (output / "results.json").exists() or any((output / mode).exists() for mode in args.modes):
        raise ValueError("Choose a fresh --output directory; existing evidence is preserved")
    required_tools = ("dbus-run-session", "kwin_wayland", "Xwayland", "xprop") if args.window_manager in ("kwin", "kwin-nested") else ISOLATED_TOOLS
    if args.window_manager == "kwin-nested":
        required_tools += ("xvfb-run", "Xvfb", "xauth")
    missing = [name for name in required_tools if shutil.which(name) is None] if isolated else []
    results: list[dict[str, object]] = []
    failed = False
    unsafe = False
    for mode in args.modes:
        if missing or unsafe:
            reason = "Isolated display tools unavailable: " + ", ".join(missing) if missing else "Blocked after interruption or unsafe process cleanup"
            results.append({"mode": mode, "status": "unavailable" if missing else "skipped", "passed": False,
                            "exit_code": None, "reason": reason, "actual_render_scaling": None})
            print(f"Desktop harness {mode}: {reason}", flush=True)
            failed = True
            continue
        result = run_mode(mode, assembly, output / mode, environment, isolated, args.timeout, args.expect_scenario)
        results.append(result)
        # Assertion failures must not suppress independent modes or be erased by
        # a later pass. Never continue on a desktop with unproven cleanup.
        failed = failed or not result["passed"]
        unsafe = bool(result.get("interrupted")) or str(result.get("error", "")).startswith("Process cleanup failed:")
    common_scale = results[0]["actual_render_scaling"]
    if any(result["actual_render_scaling"] != common_scale for result in results):
        common_scale = None
    (output / "results.json").write_text(json.dumps({
        "kind": "source-built Avalonia harness; not published apphost",
        "platform": sys.platform,
        "environment": ("isolated KWin/Wayland nested in private Xvfb (3200x2000), XWayland, private DBus/runtime, software rendering"
            if args.window_manager == "kwin-nested" else "isolated headless KWin/Wayland (3200x2000), XWayland, private DBus/runtime, software rendering"
            if args.window_manager == "kwin" else "isolated Xvfb (3200x2000), Openbox, private DBus/runtime, software rendering, optional private tray-host fixture")
            if isolated else "explicit native graphical session; desktop isolation is not verified by this runner",
        "native_desktop_opt_in": args.allow_native_desktop and not isolated,
        "selected_modes": args.modes,
        "requested_scale_factor": environment.get("AVALONIA_GLOBAL_SCALE_FACTOR"),
        "scale_factor_source": "--scale" if args.scale is not None else "inherited environment"
            if "AVALONIA_GLOBAL_SCALE_FACTOR" in environment else "platform default",
        "actual_render_scaling": common_scale,
        "passed": not failed,
        "results": results
    }, indent=2) + "\n", encoding="utf-8")
    return 1 if failed else 0


def interrupted(signum: int, frame: object) -> None:
    raise InterruptedError(f"Received signal {signum}")


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, interrupted)
    raise SystemExit(main())
