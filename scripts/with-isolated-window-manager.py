#!/usr/bin/env python3
"""Start only inside the runner's private Xvfb/DBus session, never on the user's display."""
import os
from pathlib import Path
import subprocess
import sys
import time


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
    manager = subprocess.Popen(["openbox", "--sm-disable", "--config-file", str(config)])
    try:
        deadline = time.monotonic() + 5
        while True:
            if manager.poll() is not None:
                raise RuntimeError("The isolated window manager exited during startup")
            probe = subprocess.run(["xprop", "-root", "_NET_SUPPORTING_WM_CHECK"],
                                   capture_output=True, text=True, timeout=1)
            if probe.returncode == 0 and "window id # 0x" in probe.stdout and not probe.stdout.strip().endswith("0x0"):
                break
            if time.monotonic() >= deadline:
                raise TimeoutError("The isolated window manager did not become ready")
            time.sleep(0.05)
        print("SMOKE isolated desktop ready: private Xvfb + Openbox + DBus; display=" + os.environ["DISPLAY"], flush=True)
        return subprocess.call(sys.argv[2:])
    finally:
        if manager.poll() is None:
            manager.terminate()
            try:
                manager.wait(timeout=3)
            except subprocess.TimeoutExpired:
                manager.kill()
                manager.wait(timeout=3)


if __name__ == "__main__":
    raise SystemExit(main())
