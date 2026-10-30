#!/usr/bin/env python3
"""MU-00/08 bounded real-input regression using the existing normal desktop runner.

Does NOT build or lock. Parent serializes build/run with
  flock --close /tmp/meowchan-ui-1.0-build.lock python3 scripts/test-media-interaction-smoke.py ...
No new runner mode, xdotool, personal DISPLAY, product dependency or Windows PASS.
Requires Linux plus the stock runner tools: dotnet, dbus-run-session, xvfb-run,
Xvfb, xauth, openbox, xprop; libX11.so.6, libXtst.so.6 and the VLC runtime.
ffmpeg/xdotool are not required to run the checked-in fixture.
Only synthetic local fixtures: default is pinned 12s VP8 + Vorbis for the native
volume/state checks. These require a usable isolated audio output (e.g. a private
null sink on Linux); physical speaker output is never inferred. Use --fixture for
another explicitly supplied 8-60s local WebM with audio. MP4 is not inferred.
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import math
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
STAGES = {"hover-click", "focused-pointer-away", "paused-drag-key", "custom-routing-loop", "retired-gesture-transition"}


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def clean_git_environment(environment=None) -> dict:
    # Never pass Git routing/index/config overrides into fixture or application children.
    # Snapshot inventory may select a Git directory ONLY as arguments on its two read calls.
    return {key: value for key, value in (os.environ if environment is None else environment).items()
            if not key.startswith("GIT_")}


def source_inventory(root: Path) -> dict:
    # Include owned new files even before git add -N; paths remain repository-relative.
    git = ["git"]
    inventory_git_dir = os.environ.get("MEOWCHAN_MEDIA_SOURCE_GIT_DIR")
    if inventory_git_dir:
        git += ["--git-dir", str(Path(inventory_git_dir).resolve(strict=True)), "--work-tree", str(root.resolve())]
    git_environment = clean_git_environment()
    tracked = subprocess.run([*git, "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
                             cwd=root, env=git_environment, capture_output=True, check=True).stdout.decode().split("\0")
    # Never bind nested worker checkouts or generated build/evidence sources, even
    # if a future ignore change (or tracked artifact) makes Git return them.
    files = sorted(set(name for name in tracked if name and
                       Path(name).parts[0] != "artifacts" and
                       not any(part in {"bin", "obj", ".git"} for part in Path(name).parts) and
                       (name.endswith((".cs", ".axaml", ".csproj", ".props", ".targets")) or
                        name.startswith("scripts/test-media-") or name in {"scripts/run-desktop-smoke.py", "global.json", "Directory.Packages.props", "NuGet.Config"})))
    inventory = [{"path": name, "sha256": sha256(root / name)} for name in files if (root / name).is_file()]
    digest = hashlib.sha256(json.dumps(inventory, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    if not inventory:
        raise RuntimeError("Empty source inventory; select the snapshot Git directory for read-only inventory explicitly.")
    head = subprocess.run([*git, "rev-parse", "HEAD"], cwd=root, env=git_environment,
                          capture_output=True, text=True, check=True).stdout.strip()
    return {"head": head, "sha256": digest, "files": inventory,
            "limit": "Source-byte inventory, including owned untracked inputs; not proof that supplied binaries were built from these bytes."}


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--assembly", required=True, type=Path, help="Parent-built MeowChan.DesktopSmoke.dll; never builds here")
    parser.add_argument("--output", required=True, type=Path, help="Fresh evidence directory; failed attempts are preserved")
    parser.add_argument("--fixture", type=Path, default=ROOT / "tests/Fixtures/release102/playback-vp8-audio.webm")
    parser.add_argument("--timeout", type=float, default=180)
    parser.add_argument("--scale", choices=("1", "1.5", "2"), default="1")
    parser.add_argument("--allow-native-desktop", action="store_true")
    parser.add_argument("--require-audio-output", action="store_true",
                        help="Require rendered native audio and functional volume coverage in both transport stages")
    args = parser.parse_args(argv)
    if not math.isfinite(args.timeout) or args.timeout <= 0:
        parser.error("--timeout must be finite and positive")
    return args


def validate_trace(trace: dict, binding: dict) -> list[str]:
    errors = []
    if trace.get("outcome") != "passed" or trace.get("failures") or trace.get("dropped") != 0:
        errors.append("Interaction trace failed, is partial, or dropped events")
    if set(trace.get("completed", [])) != STAGES:
        errors.append("Missing expected native interaction stages")
    for key in ("sourceDigest", "assemblySha256", "fixtureSha256"):
        if trace.get(key) != binding[key]:
            errors.append("Trace inventory mismatch: " + key)
    if trace.get("scale") != float(binding["scale"]):
        errors.append("Actual render scale does not match requested cell")
    platform = binding.get("platform", "linux")
    backend = "XTest" if platform.startswith("linux") else "Win32 SendInput" if platform == "win32" else "macOS CGEvent" if platform == "darwin" else None
    if backend is None or backend not in trace.get("backend", ""):
        errors.append("No matching native input backend evidence")
    for stage in STAGES:
        events = [e for e in trace.get("events", []) if e.get("stage") == stage]
        if not any(e.get("kind") == "assertions-passed" for e in events):
            errors.append("Missing assertions: " + stage)
        if not any(e.get("kind", "").startswith("pointer-") for e in events):
            errors.append("Missing real pointer delivery: " + stage)
        if stage in {"paused-drag-key", "custom-routing-loop", "retired-gesture-transition"} and not any(
                e.get("kind") == "key-tunnel" for e in events):
            errors.append("Missing real key delivery: " + stage)
    if binding.get("requireAudioOutput"):
        for stage in ("focused-pointer-away", "paused-drag-key"):
            samples = [event.get("data", {}).get("evidence", {}) for event in trace.get("events", [])
                       if event.get("stage") == stage and event.get("kind") == "sample"
                       and event.get("data", {}).get("label") == "volume-availability-before"]
            if not any(sample.get("AudioAvailable") is True and sample.get("AudioTracks", 0) > 0
                       and sample.get("PlayedAudioBuffers", 0) > 0 for sample in samples):
                errors.append("Missing required functional audio/volume evidence: " + stage)
        if any(event.get("kind") == "coverage-limit" for event in trace.get("events", [])):
            errors.append("A required functional-audio run used unavailable-output coverage")
    return errors


def main(argv=None) -> int:
    args = parse_args(argv)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    result = {"status": "failed", "passed": False,
              "scope": "Source-built native input and actual video for the recorded OS; no physical audio/GPU or unrun-platform claim"}
    try:
        if sys.platform not in ("linux", "win32", "darwin"):
            raise RuntimeError("UNSUPPORTED native input platform")
        if not sys.platform.startswith("linux") and not args.allow_native_desktop:
            raise RuntimeError("UNSUPPORTED without explicit --allow-native-desktop on a disposable session")
        if sys.platform.startswith("linux"):
            xtst = ctypes.CDLL("libXtst.so.6")
            getattr(xtst, "XTestFakeMotionEvent")
            ctypes.CDLL("libX11.so.6")
        assembly = args.assembly.resolve(strict=True)
        fixture = args.fixture.resolve(strict=True)
        source = source_inventory(ROOT)
        # Inventory every shipped sibling managed/native binary so substituted dependencies are visible.
        binaries = [{"path": str(p.relative_to(assembly.parent)), "sha256": sha256(p)}
                    for p in sorted(assembly.parent.rglob("*")) if p.is_file()
                    and (p.suffix in {".dll", ".dylib"} or ".so" in p.name or p.name.endswith(".deps.json"))]
        binding = {"sourceDigest": source["sha256"], "assemblySha256": sha256(assembly), "fixtureSha256": sha256(fixture),
                   "source": source, "binaries": binaries, "fixture": {"name": fixture.name, "bytes": fixture.stat().st_size},
                   "platform": sys.platform, "scale": args.scale, "requireAudioOutput": args.require_audio_output}
        (output / "binding.json").write_text(json.dumps(binding, indent=2) + "\n")
        environment = {key: value for key, value in clean_git_environment().items() if not key.startswith(("MEOWCHAN_", "OCHAN_"))}
        environment.update(MEOWCHAN_MEDIA_INTERACTION_ONLY="1", MEOWCHAN_VIDEO_FIXTURE=str(fixture),
                           MEOWCHAN_MEDIA_INTERACTION_REPORT=str(output / "interaction.json"),
                           MEOWCHAN_MEDIA_SOURCE_DIGEST=binding["sourceDigest"])
        if args.require_audio_output:
            environment["MEOWCHAN_REQUIRE_FUNCTIONAL_AUDIO"] = "1"
        command = [sys.executable, str(ROOT / "scripts/run-desktop-smoke.py"), "--assembly", str(assembly),
                   "--output", str(output / "desktop"), "--mode", "normal", "--timeout", str(args.timeout), "--scale", args.scale]
        if args.allow_native_desktop:
            command.append("--allow-native-desktop")
        # Only the stock runner creates the display/WM/DBus/profile and cleans its process group.
        result["runner_exit_code"] = subprocess.run(command, env=environment, check=False).returncode
        trace = json.loads((output / "interaction.json").read_text())
        errors = validate_trace(trace, binding)
        runner = json.loads((output / "desktop/results.json").read_text())
        if result["runner_exit_code"] != 0 or not runner.get("results") or not all(r.get("passed") for r in runner["results"]):
            errors.append("Stock desktop runner did not pass (including shutdown)")
        if source_inventory(ROOT)["sha256"] != binding["sourceDigest"] or sha256(assembly) != binding["assemblySha256"]:
            errors.append("Source/assembly changed during the run; evidence is not a frozen cell")
        result.update(errors=errors, status="passed" if not errors else "failed", passed=not errors)
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError, AttributeError) as exception:
        # Preserve failed output and any stock-runner artifacts, never downgrade to a successful skip.
        result["error"] = str(exception)
    finally:
        (output / "result.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result, indent=2))
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
