#!/usr/bin/env python3
"""Start only inside the runner's private Xvfb/DBus session, never on the user's display."""
import os
from pathlib import Path
import signal
import subprocess
import sys
import time


def stop_owned_window_manager(manager: subprocess.Popen) -> None:
    """Stop Openbox without leaving its descendants in the smoke process group."""
    if manager.poll() is not None:
        manager.wait(timeout=3)
        return
    if os.name == "nt":
        manager.terminate()
        manager.wait(timeout=3)
        return
    try:
        os.killpg(manager.pid, signal.SIGTERM)
    except ProcessLookupError:
        manager.wait(timeout=3)
        return
    try:
        manager.wait(timeout=3)
    except subprocess.TimeoutExpired:
        try:
            os.killpg(manager.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        manager.wait(timeout=3)


def main() -> int:
    if os.environ.get("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1" or not os.environ.get("DISPLAY"):
        raise RuntimeError("This helper requires the isolated smoke runner's Xvfb environment")
    if len(sys.argv) < 3 or sys.argv[1] != "--":
        raise ValueError("Expected -- followed by the harness command")
    config = Path(os.environ["XDG_CONFIG_HOME"]) / "openbox.xml"
    config.write_text('<openbox_config xmlns="http://openbox.org/3.4/rc">'
                      '<focus><focusNew>yes</focusNew></focus>'
                      '<placement><policy>Smart</policy></placement>'
                      '</openbox_config>', encoding="utf-8")
    manager = subprocess.Popen(["openbox", "--sm-disable", "--config-file", str(config)],
                               start_new_session=os.name != "nt")
    try:
        deadline = time.monotonic() + 5
        last_probe = "no xprop response"
        while True:
            if manager.poll() is not None:
                raise RuntimeError("The isolated window manager exited during startup")
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("The isolated window manager did not become ready: " + last_probe)
            try:
                probe = subprocess.run(["xprop", "-root", "_NET_SUPPORTING_WM_CHECK"],
                                       capture_output=True, text=True, timeout=min(1, remaining))
            except subprocess.TimeoutExpired:
                last_probe = "xprop root probe timed out"
            else:
                if probe.returncode == 0 and "window id # 0x" in probe.stdout and not probe.stdout.strip().endswith("0x0"):
                    break
                last_probe = "xprop exit " + str(probe.returncode) + ": " + (probe.stderr or probe.stdout).strip()[:240]
            if time.monotonic() >= deadline:
                raise TimeoutError("The isolated window manager did not become ready: " + last_probe)
            time.sleep(min(0.05, max(0, deadline - time.monotonic())))
        print("SMOKE isolated desktop ready: private Xvfb + Openbox + DBus; display=" + os.environ["DISPLAY"], flush=True)
        code = subprocess.call(sys.argv[2:])
        # xvfb-run may replace this status with its own when Xvfb is already gone at teardown;
        # the runner reads the harness's own status from this file.
        exit_file = os.environ.get("MEOWCHAN_SMOKE_HARNESS_EXIT_FILE")
        if exit_file:
            Path(exit_file).write_text(str(code), encoding="utf-8")
        return code
    finally:
        stop_owned_window_manager(manager)


if __name__ == "__main__":
    raise SystemExit(main())
