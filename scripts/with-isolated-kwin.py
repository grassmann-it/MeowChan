#!/usr/bin/env python3
"""Run a private headless KWin/Wayland + XWayland session on the smoke bus."""
import os
from pathlib import Path
import shlex
import subprocess
import sys


def main() -> int:
    nested = os.environ.get("MEOWCHAN_DESKTOP_SMOKE_KWIN_NESTED") == "1"
    if os.environ.get("MEOWCHAN_DESKTOP_SMOKE_KWIN") != "1" or (not nested and os.environ.get("DISPLAY")) or os.environ.get("WAYLAND_DISPLAY"):
        raise RuntimeError("KWin helper requires the runner's private, display-free environment")
    if nested and (os.environ.get("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1" or not os.environ.get("DISPLAY")):
        raise RuntimeError("Nested KWin requires the runner's private Xvfb input display")
    if len(sys.argv) < 3 or sys.argv[1] != "--":
        raise ValueError("Expected -- followed by the harness command")
    runtime = Path(os.environ["XDG_RUNTIME_DIR"])
    session = runtime / "kwin-smoke-session.sh"
    result = runtime / "kwin-smoke-exit"
    session.write_text("#!/bin/sh\n"
                       "printf 'SMOKE isolated desktop ready: private KWin/Wayland + XWayland + DBus; display=%s; wayland=%s; nested-input=%s\\n' \"$DISPLAY\" \"$WAYLAND_DISPLAY\" \"$MEOWCHAN_KWIN_INPUT_DISPLAY\"\n"
                       "kwin_wayland --version\n"
                       "xprop -root _NET_SUPPORTING_WM_CHECK\n"
                       + shlex.join(sys.argv[2:]) + "\n"
                       + "code=$?\nprintf '%s' \"$code\" > " + shlex.quote(str(result)) + "\nexit \"$code\"\n", encoding="utf-8")
    session.chmod(0o700)
    environment = os.environ.copy()
    environment.update(QT_QPA_PLATFORM="offscreen", LIBGL_ALWAYS_SOFTWARE="1", XDG_SESSION_TYPE="wayland")
    platform = ["--virtual"]
    if nested:
        environment["MEOWCHAN_KWIN_INPUT_DISPLAY"] = environment["DISPLAY"]
        environment["MEOWCHAN_KWIN_INPUT_XAUTHORITY"] = environment.get("XAUTHORITY", "")
        platform = ["--x11-display", environment["DISPLAY"], "--fullscreen", "true"]
    completed = subprocess.run(["kwin_wayland", *platform, "--width", "3200", "--height", "2000",
                                "--xwayland", "--socket", "meowchan-smoke-wayland", "--no-lockscreen",
                                "--no-global-shortcuts", "--no-kactivities", "--exit-with-session", str(session)], env=environment)
    if not result.exists():
        raise RuntimeError(f"KWin exited ({completed.returncode}) before the smoke session completed")
    return int(result.read_text())


if __name__ == "__main__":
    raise SystemExit(main())
