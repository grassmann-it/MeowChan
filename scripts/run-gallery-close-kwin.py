#!/usr/bin/env python3
"""Run the reader-ux gallery-close probe in a private virtual KWin session.

Build MeowChan.DesktopSmoke first. Each invocation needs a fresh --output
directory and creates only its own Wayland, XWayland, DBus and smoke processes.
Video probes also start private PipeWire, WirePlumber policy and PulseAudio
with a null sink. Audio reaches a virtual sink and is intentionally not audible.
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import shlex
import shutil
import signal
import subprocess
import tempfile
from xml.sax.saxutils import escape


REPO = Path(__file__).resolve().parent.parent
DEFAULT_ASSEMBLY = REPO / "tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll"
SESSION_VARIABLES = (
    "DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "DBUS_SESSION_BUS_ADDRESS",
    "DBUS_STARTER_ADDRESS", "DBUS_STARTER_BUS_TYPE", "SESSION_MANAGER",
    "AT_SPI_BUS_ADDRESS", "XDG_CURRENT_DESKTOP", "XDG_SESSION_DESKTOP",
    "XDG_SESSION_ID", "DESKTOP_SESSION", "KDE_FULL_SESSION",
)


def private_session_environment() -> dict[str, str]:
    env = os.environ.copy()
    for name in SESSION_VARIABLES:
        env.pop(name, None)
    for name in ("QT_QPA_PLATFORM", "QT_PLUGIN_PATH", "MEOWCHAN_DESKTOP_SMOKE_XVFB",
                 "MEOWCHAN_DESKTOP_SMOKE_KWIN", "MEOWCHAN_VIDEO_FIXTURE",
                 "MEOWCHAN_GALLERY_CLOSE_MAXIMIZED", "MEOWCHAN_GALLERY_CLOSE_SKIP_FULLSCREEN",
                 "MEOWCHAN_PROFILE", "OCHAN_PROFILE", "PULSE_SERVER", "PULSE_SINK",
                 "PULSE_SOURCE", "PULSE_RUNTIME_PATH", "PULSE_COOKIE", "PIPEWIRE_REMOTE",
                 "PIPEWIRE_RUNTIME_DIR", "PIPEWIRE_CONFIG_DIR", "PIPEWIRE_CONFIG_NAME",
                 "ALSA_CONFIG_PATH", "ALSA_CONFIG_DIR", "ALSA_CARD",
                 "GI_TYPELIB_PATH", "GSETTINGS_SCHEMA_DIR", "LD_LIBRARY_PATH",
                 "LD_PRELOAD", "LD_AUDIT", "KWIN_SCREENSHOT_NO_PERMISSION_CHECKS"):
        env.pop(name, None)
    # Codex may be launched by a Snap. Its cached GLib/GTK modules can load
    # Snap's incompatible libc into the host WirePlumber and Qt processes.
    for name in tuple(env):
        if name.startswith(("SNAP", "GIO_", "GTK_", "GTK2_", "GDK_PIXBUF_", "GST_PLUGIN_")):
            env.pop(name)
    env["XDG_CONFIG_DIRS"] = "/etc/xdg"
    env["XDG_DATA_DIRS"] = "/usr/local/share:/usr/share"
    return env


def stop_owned_processes(process: subprocess.Popen) -> None:
    """Signal only the process group created by this runner."""
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    try:
        process.wait(timeout=2)
    except subprocess.TimeoutExpired:
        pass
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    process.wait(timeout=10)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assembly", type=Path, default=DEFAULT_ASSEMBLY)
    parser.add_argument("--output", type=Path, required=True, help="Fresh directory for this invocation's log and private config")
    parser.add_argument("--video-fixture", type=Path, help="Verified local WebM fixture; omit to test the seeded image")
    parser.add_argument("--audio", action="store_true", help="Start private PipeWire/WirePlumber/PulseAudio null sink (automatic for video)")
    parser.add_argument("--maximized", action="store_true", help="Enter fullscreen from a maximized gallery")
    parser.add_argument("--skip-fullscreen", action="store_true", help="Control for native close-caption input delivery")
    parser.add_argument("--capture", action="store_true", help="Capture the private KWin desktop using Spectacle")
    args = parser.parse_args()
    assembly = args.assembly.resolve(strict=True)
    fixture = args.video_fixture.resolve(strict=True) if args.video_fixture else None
    audio = args.audio or fixture is not None
    output = args.output.resolve()
    if not assembly.is_file() or fixture is not None and not fixture.is_file():
        parser.error("Assembly and video fixture must be files")
    if output.exists():
        parser.error("--output must name a fresh directory: " + str(output))
    output.mkdir(mode=0o700, parents=True)

    env = private_session_environment()
    with tempfile.TemporaryDirectory(prefix="mc-kwin-") as runtime:
        env["XDG_RUNTIME_DIR"] = runtime  # Short private socket path; mode 0700.
        for name, child in (("XDG_CONFIG_HOME", "config"), ("XDG_CACHE_HOME", "cache"),
                            ("XDG_DATA_HOME", "data"), ("XDG_STATE_HOME", "state")):
            directory = output / child
            directory.mkdir(mode=0o700)
            env[name] = str(directory)
        # XTest authorization belongs only to this disposable KWin compositor.
        (output / "config/kwinrc").write_text("[Xwayland]\nXwaylandEisNoPrompt=true\n", encoding="utf-8")
        env.update({
            "XDG_SESSION_TYPE": "wayland",
            "LIBGL_ALWAYS_SOFTWARE": "1",
            "GALLIUM_DRIVER": "llvmpipe",
            "QT_QUICK_BACKEND": "software",
            "QSG_RHI_BACKEND": "software",
            "MEOWCHAN_DESKTOP_SMOKE_KWIN": "1",
            "MEOWCHAN_GALLERY_CLOSE_ONLY": "1",
            "MEOWCHAN_FULLSCREEN_TRACE": "1",
        })
        if fixture:
            env["MEOWCHAN_VIDEO_FIXTURE"] = str(fixture)
        if audio:
            env["PIPEWIRE_RUNTIME_DIR"] = runtime
            env["PIPEWIRE_REMOTE"] = "pipewire-0"
            env["PULSE_SERVER"] = "unix:" + str(Path(runtime) / "pulse/native")
            env["PULSE_SINK"] = "smoke_null"
            pipewire_config = output / "config/pipewire/pipewire.conf.d"
            pipewire_config.mkdir(parents=True, mode=0o700)
            (pipewire_config / "smoke-null-sink.conf").write_text(
                "context.objects = [\n"
                "    { factory = adapter\n"
                "        args = {\n"
                "            factory.name = support.null-audio-sink\n"
                "            node.name = smoke_null\n"
                "            node.description = \"Private smoke-test null sink\"\n"
                "            media.class = \"Audio/Sink\"\n"
                "        }\n"
                "    }\n"
                "    { factory = metadata\n"
                "        args = {\n"
                "            metadata.name = default\n"
                "            metadata.values = [\n"
                "                { key = default.audio.sink value = { name = smoke_null } }\n"
                "            ]\n"
                "        }\n"
                "    }\n"
                "]\n", encoding="utf-8")
        if args.capture:
            env["MEOWCHAN_GALLERY_NATIVE_CAPTURE_DIR"] = str(output)
            spectacle = shutil.which("spectacle")
            if not spectacle:
                parser.error("--capture requires spectacle")
            # The smoke check invokes `import -window root`. In XWayland the
            # root is not KWin's composited scene, so replace only this private
            # runner's import command with KWin/Spectacle full-screen capture.
            capture_bin = output / "capture-bin"
            capture_bin.mkdir(mode=0o700)
            capture_import = capture_bin / "import"
            capture_import.write_text(
                "#!/bin/sh\nset -eu\n"
                "if [ \"$#\" -ne 3 ] || [ \"$1\" != -window ] || [ \"$2\" != root ]; then\n"
                "    echo 'Expected private full-screen capture: import -window root FILE' >&2\n"
                "    exit 2\n"
                "fi\n"
                "test -n \"${WAYLAND_DISPLAY:-}\" || { echo 'No private KWin Wayland display' >&2; exit 2; }\n"
                "QT_QPA_PLATFORM=wayland " + shlex.quote(spectacle)
                + " --background --fullscreen --nonotify --output \"$3\"\n"
                "test -s \"$3\"\n", encoding="utf-8")
            capture_import.chmod(0o700)
            env["PATH"] = str(capture_bin) + os.pathsep + env.get("PATH", os.defpath)
            # This flag belongs to the disposable compositor only. Its private
            # DBus and Wayland sockets remain isolated from the host session.
            env["KWIN_SCREENSHOT_NO_PERMISSION_CHECKS"] = "1"
        if args.maximized:
            env["MEOWCHAN_GALLERY_CLOSE_MAXIMIZED"] = "1"
        if args.skip_fullscreen:
            env["MEOWCHAN_GALLERY_CLOSE_SKIP_FULLSCREEN"] = "1"

        bus = output / "bus.conf"
        bus.write_text('<busconfig><type>session</type><listen>unix:tmpdir=' + escape(runtime)
                       + '</listen><auth>EXTERNAL</auth>'
                       '<policy context="default"><allow send_destination="*" eavesdrop="true"/>'
                       '<allow eavesdrop="true"/><allow own="*"/></policy></busconfig>', encoding="utf-8")
        launcher = output / "session.sh"
        launcher.write_text("#!/bin/sh\n"
                            "test -n \"${DISPLAY:-}\" || { echo 'No private XWayland DISPLAY' >&2; exit 2; }\n"
                            "exec " + shlex.quote(shutil.which("dotnet") or "/usr/bin/dotnet") + " "
                            + shlex.quote(str(assembly)) + " --reader-ux\n", encoding="utf-8")
        launcher.chmod(0o700)
        kwin = [shutil.which("kwin_wayland") or "/usr/bin/kwin_wayland", "--virtual", "--xwayland",
                "--width", "1600", "--height", "1000", "--no-lockscreen",
                "--no-global-shortcuts", "--no-kactivities", "--exit-with-session", str(launcher)]
        session_command = kwin
        if audio:
            pipewire = shutil.which("pipewire")
            wireplumber = shutil.which("wireplumber")
            pulse = shutil.which("pipewire-pulse")
            pactl = shutil.which("pactl")
            if not pipewire or not wireplumber or not pulse or not pactl:
                parser.error("--audio requires pipewire, wireplumber, pipewire-pulse and pactl")
            audio_launcher = output / "audio-session.sh"
            audio_launcher.write_text(
                "#!/bin/sh\nset -eu\n"
                + shlex.quote(pipewire) + " >" + shlex.quote(str(output / "pipewire.log")) + " 2>&1 &\n"
                + "for i in $(seq 1 40); do [ -S \"$XDG_RUNTIME_DIR/pipewire-0\" ] && break; sleep 0.1; done\n"
                # WirePlumber's built-in policy profile links streams, but does
                # not load ALSA, Bluetooth, MIDI or video hardware monitors.
                + shlex.quote(wireplumber) + " --profile policy >"
                + shlex.quote(str(output / "wireplumber.log")) + " 2>&1 &\n"
                + shlex.quote(pulse) + " >" + shlex.quote(str(output / "pipewire-pulse.log")) + " 2>&1 &\n"
                + "for i in $(seq 1 40); do " + shlex.quote(pactl) + " info >/dev/null 2>&1 && break; sleep 0.1; done\n"
                + shlex.quote(pactl) + " info >/dev/null\n"
                + "for i in $(seq 1 40); do " + shlex.quote(pactl)
                + " list short sinks | cut -f2 | grep -Fxq smoke_null && break; sleep 0.1; done\n"
                + shlex.quote(pactl) + " list short sinks | cut -f2 | grep -Fxq smoke_null\n"
                + shlex.quote(pactl) + " set-default-sink smoke_null\n"
                + shlex.quote(pactl) + " list short sinks\n"
                + "exec " + shlex.join(kwin) + "\n", encoding="utf-8")
            audio_launcher.chmod(0o700)
            session_command = ["/bin/sh", str(audio_launcher)]
        command = [shutil.which("dbus-run-session") or "/usr/bin/dbus-run-session", "--config-file", str(bus),
                   "--", *session_command]
        print("Private KWin command:", shlex.join(command), flush=True)
        print("Output:", output / "output.log", flush=True)
        with (output / "output.log").open("w", encoding="utf-8") as log:
            process = subprocess.Popen(command, env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            try:
                code = process.wait(timeout=60)
            except subprocess.TimeoutExpired:
                log.write("\nPROBE TIMEOUT after 60 seconds\n")
                log.flush()
                code = 124
            finally:
                stop_owned_processes(process)
    print("Probe exit:", code, flush=True)
    return code


if __name__ == "__main__":
    raise SystemExit(main())
