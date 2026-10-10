#!/usr/bin/env python3
"""Run the source-built Avalonia harness, retaining precise per-mode evidence.

This does not execute the published oChan apphost and is not an artifact smoke.
Linux defaults to a private DBus session and Xvfb with software rendering and an
explicit no-tray fixture. --native-display uses the existing Linux GUI session.
Native Windows/macOS require a GUI session; a missing graphical session fails the
check, so it cannot silently count as pass.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import tempfile


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--assembly", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--timeout", type=int, default=60)
    parser.add_argument("--native-display", action="store_true",
                        help="Use the existing Linux graphical session instead of Xvfb/private DBus.")
    parser.add_argument("--scale", choices=("1", "1.5", "2"),
                        help="Request an Avalonia scale factor; this does not verify actual RenderScaling.")
    args = parser.parse_args()
    linux = sys.platform.startswith("linux")
    environment = os.environ.copy()
    if args.native_display:
        if not linux:
            parser.error("--native-display is only needed and supported on Linux.")
        if not any(environment.get(name, "").strip() for name in ("DISPLAY", "WAYLAND_DISPLAY")):
            parser.error("--native-display requires DISPLAY or WAYLAND_DISPLAY in the existing session.")
    if args.scale is not None:
        environment["AVALONIA_GLOBAL_SCALE_FACTOR"] = args.scale
    assembly = Path(args.assembly).resolve(strict=True)
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    linux_xvfb = linux and not args.native_display
    if linux_xvfb:
        for executable in ("xvfb-run", "dbus-run-session"):
            if shutil.which(executable) is None:
                raise RuntimeError(f"{executable} is required for the Linux graphical smoke.")
        environment["OCHAN_DESKTOP_SMOKE_XVFB"] = "1"
    else:
        environment.pop("OCHAN_DESKTOP_SMOKE_XVFB", None)
    modes = ("normal", "locked-profile", "no-tray", "tray-failure", "download-session")
    results: list[dict[str, object]] = []
    failed = False
    for mode in modes:
        destination = output / mode
        destination.mkdir(exist_ok=True)
        command = ["dotnet", str(assembly)]
        if mode != "normal":
            command.append("--" + mode)
        if linux_xvfb:
            command = ["dbus-run-session", "--", "xvfb-run", "--auto-servernum",
                       "--server-args=-screen 0 1280x1024x24", *command]
        previous = {image: image.stat().st_mtime_ns for image in assembly.parent.glob("smoke-*.png")}
        timed_out = False
        with tempfile.TemporaryDirectory(prefix="ochan-smoke-cwd-") as working_directory:
            with (destination / "output.log").open("w", encoding="utf-8") as log:
                process = subprocess.Popen(command, cwd=working_directory, env=environment, stdout=log,
                                           stderr=subprocess.STDOUT, start_new_session=os.name != "nt")
                try:
                    exit_code = process.wait(timeout=args.timeout)
                except subprocess.TimeoutExpired:
                    timed_out = True
                    if sys.platform == "darwin":
                        with (destination / "timeout-sample.log").open("w", encoding="utf-8") as diagnostic_log:
                            try:
                                sample = shutil.which("sample")
                                if sample is None:
                                    diagnostic_log.write("macOS sample tool is unavailable.\n")
                                else:
                                    diagnostic = subprocess.run(
                                        [sample, str(process.pid), "3", "1", "-file", str(destination / "timeout-sample.txt")],
                                        timeout=8, check=False, stdout=diagnostic_log, stderr=subprocess.STDOUT)
                                    diagnostic_log.write(f"sample exit code: {diagnostic.returncode}\n")
                            except (OSError, subprocess.TimeoutExpired) as exception:
                                diagnostic_log.write(f"Could not collect timeout sample: {exception}\n")
                    if os.name == "nt":
                        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], check=False,
                                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                    else:
                        try:
                            os.killpg(process.pid, signal.SIGKILL)
                        except ProcessLookupError:
                            pass
                    process.wait(timeout=10)
                    exit_code = 124
        log_text = (destination / "output.log").read_text(encoding="utf-8", errors="replace")
        print(f"--- Desktop harness {mode}: exit {exit_code} ---", flush=True)
        print(log_text, flush=True)
        for image in assembly.parent.glob("smoke-*.png"):
            if previous.get(image) != image.stat().st_mtime_ns:
                shutil.copy2(image, destination / image.name)
        passed = exit_code == 0 and "SMOKE PASS:" in log_text
        observed_scalings = sorted({float(value) for value in re.findall(
            r"^SMOKE render scaling: actual=([0-9]+(?:\.[0-9]+)?);", log_text, re.MULTILINE)})
        actual_scaling = observed_scalings[0] if len(observed_scalings) == 1 else None
        results.append({"mode": mode, "exit_code": exit_code, "timed_out": timed_out, "passed": passed,
                        "actual_render_scaling": actual_scaling, "observed_render_scalings": observed_scalings})
        failed = failed or not passed
    common_scaling = results[0]["actual_render_scaling"] if results else None
    if any(result["actual_render_scaling"] != common_scaling for result in results):
        common_scaling = None
    (output / "results.json").write_text(json.dumps({
        "kind": "source-built Avalonia harness; not published apphost",
        "platform": sys.platform,
        "environment": "Xvfb, private DBus session, software rendering, no native tray host"
            if linux_xvfb else "existing Linux graphical session; native renderer/tray setup, scenario fixtures apply"
            if linux else "native graphical session",
        "requested_scale_factor": environment.get("AVALONIA_GLOBAL_SCALE_FACTOR"),
        "scale_factor_source": "--scale" if args.scale is not None else "inherited environment"
            if "AVALONIA_GLOBAL_SCALE_FACTOR" in environment else "platform default",
        "actual_render_scaling": common_scaling,
        "results": results
    }, indent=2) + "\n", encoding="utf-8")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
