#!/usr/bin/env python3
"""Source-built GUI checks. Linux uses an isolated display; stress is opt-in.

These checks do not execute published apphosts. Never fall back to a user's
DISPLAY when the virtual-display tools are missing.
"""
from __future__ import annotations

import argparse
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
    args = parser.parse_args(argv)
    if not math.isfinite(args.timeout) or args.timeout <= 0:
        parser.error("--timeout must be finite and positive")
    args.modes = tuple(dict.fromkeys(args.mode or STANDARD_MODES))
    if args.window_manager != "openbox" and not sys.platform.startswith("linux"):
        parser.error("--window-manager selects an isolated Linux session only")
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


def prepare_environment(base: dict[str, str], isolated: bool, directory: Path) -> dict[str, str]:
    # A fixture/application child must never inherit routing to the project Git index.
    environment = {key: value for key, value in base.items() if not key.startswith("GIT_")}
    environment["MEOWCHAN_DISABLE_UPDATE_CHECK"] = "1"
    environment.pop("MEOWCHAN_PROFILE", None)
    environment.pop("OCHAN_PROFILE", None)
    environment["MEOWCHAN_PROFILE"] = str((directory / "profile").resolve())
    # GUI fixtures must never schedule real update requests in their private profiles.
    environment["MEOWCHAN_DISABLE_UPDATE_CHECK"] = "1"
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


def stop_owned_processes(process: subprocess.Popen) -> None:
    """Only touch the process/tree or POSIX group created by this invocation."""
    if os.name == "nt":
        if process.poll() is None:
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], check=False,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=10)
    else:
        try:
            os.killpg(process.pid, signal.SIGTERM)
            deadline = time.monotonic() + 2
            while time.monotonic() < deadline:
                process.poll()  # Reap the leader; its children may still need cleanup.
                try:
                    os.killpg(process.pid, 0)
                except ProcessLookupError:
                    break
                time.sleep(0.05)
        except ProcessLookupError:
            pass
        finally:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
    process.wait(timeout=10)


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


def run_mode(mode: str, assembly: Path, destination: Path, environment: dict[str, str],
             isolated: bool, timeout: float) -> dict[str, object]:
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
    with tempfile.TemporaryDirectory(prefix="meowchan-smoke-cwd-") as working_directory:
        child_environment = prepare_environment(environment, isolated, Path(working_directory))
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
    passed = exit_code == 0 and "SMOKE PASS:" in log_text and error is None
    print(f"Desktop harness {mode}: {'passed' if passed else 'FAILED'} (exit {exit_code}); {destination / 'output.log'}", flush=True)
    if not passed:
        if error:
            print(error, flush=True)
        print("\n".join(log_text.splitlines()[-20:]), flush=True)
    return {"mode": mode, "status": "passed" if passed else "failed", "exit_code": exit_code,
            "timed_out": timed_out, "interrupted": interrupted, "error": error, "passed": passed,
            "watchdog": watchdog_result,
            "actual_render_scaling": scales[0] if len(scales) == 1 else None, "observed_render_scalings": scales}


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
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
    for mode in args.modes:
        if missing or failed:
            reason = "Isolated display tools unavailable: " + ", ".join(missing) if missing else "Stopped after previous failure"
            results.append({"mode": mode, "status": "unavailable" if missing else "skipped", "passed": False,
                            "exit_code": None, "reason": reason, "actual_render_scaling": None})
            print(f"Desktop harness {mode}: {reason}", flush=True)
            failed = True
            continue
        result = run_mode(mode, assembly, output / mode, environment, isolated, args.timeout)
        results.append(result)
        failed = not result["passed"]
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
