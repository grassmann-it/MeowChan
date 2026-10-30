#!/usr/bin/env python3
"""Fast wrapper/evidence contract tests. No GUI, X server, decoder, build or user display.

Run under the shared build lock; these do not establish media/Windows acceptance.
"""
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

SPEC = importlib.util.spec_from_file_location("media_smoke", Path(__file__).with_name("test-media-interaction-smoke.py"))
MEDIA = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MEDIA)


def evidence():
    binding = {"sourceDigest": "a" * 64, "assemblySha256": "b" * 64, "fixtureSha256": "c" * 64, "scale": "1"}
    trace = {**binding, "outcome": "passed", "backend": "Linux X11 XTest", "scale": 1,
             "failures": [], "dropped": 0, "completed": sorted(MEDIA.STAGES), "events": []}
    for stage in MEDIA.STAGES:
        for kind in ("assertions-passed", "pointer-PointerPressed", "key-tunnel"):
            trace["events"].append({"stage": stage, "kind": kind})
    return binding, trace


class EvidenceTests(unittest.TestCase):
    def test_default_fixture_supports_the_unconditional_volume_checks(self):
        args = MEDIA.parse_args(["--assembly", "fake.dll", "--output", "fake-output"])
        self.assertEqual(MEDIA.ROOT / "tests/Fixtures/release102/playback-vp8-audio.webm", args.fixture)
        self.assertEqual("a88f7ec3fa76c11c38719662ba41d5538a5775cc58afce5bf32e14e0cd5d1ad8",
                         MEDIA.sha256(args.fixture))

    def test_complete_native_trace(self):
        binding, trace = evidence()
        self.assertEqual([], MEDIA.validate_trace(trace, binding))

    def test_failure_overflow_missing_stage_and_wrong_inventory_reject(self):
        binding, original = evidence()
        for field, value in (("outcome", "skipped"), ("dropped", 1), ("completed", []),
                             ("fixtureSha256", "different"), ("assemblySha256", "different"),
                             ("sourceDigest", "different"), ("failures", ["hover froze"]), ("backend", "RaiseEvent"), ("scale", 2)):
            with self.subTest(field=field):
                trace = copy.deepcopy(original)
                trace[field] = value
                self.assertTrue(MEDIA.validate_trace(trace, binding))

    def test_missing_real_delivery_rejects_green_assertions(self):
        binding, trace = evidence()
        for kind in ("key-tunnel", "pointer-PointerPressed"):
            incomplete = {**trace, "events": [e for e in trace["events"] if e["kind"] != kind]}
            self.assertTrue(MEDIA.validate_trace(incomplete, binding))

    def test_git_overrides_never_escape_read_only_inventory_calls(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "source.cs").write_text("class Fixture {}")
            calls = []
            def read_git(command, **kwargs):
                calls.append((command, kwargs))
                return subprocess.CompletedProcess(command, 0, "a" * 40 if kwargs.get("text") else b"source.cs\0")
            with mock.patch.dict(MEDIA.os.environ, {
                    "GIT_DIR": "/must-not-inherit", "GIT_WORK_TREE": "/must-not-touch",
                    "GIT_INDEX_FILE": "/must-not-write", "MEOWCHAN_MEDIA_SOURCE_GIT_DIR": str(root)}), \
                    mock.patch.object(MEDIA.subprocess, "run", side_effect=read_git):
                self.assertTrue(MEDIA.source_inventory(root)["files"])
                self.assertFalse(any(key.startswith("GIT_") for key in MEDIA.clean_git_environment()))
            self.assertEqual(2, len(calls))
            for command, kwargs in calls:
                self.assertEqual(["git", "--git-dir", str(root), "--work-tree", str(root)], command[:5])
                self.assertFalse(any(key.startswith("GIT_") for key in kwargs["env"]))
                self.assertIn(command[5], {"ls-files", "rev-parse"})

    def test_source_inventory_includes_owned_new_files_and_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            def inventory_git(command, **kwargs):
                return subprocess.CompletedProcess(command, 0, "d" * 40 if kwargs.get("text") else b"new.cs\0")
            (root / "new.cs").write_text("class NativeInput {}")
            with mock.patch.object(MEDIA.subprocess, "run", side_effect=inventory_git):
                first = MEDIA.source_inventory(root)
                self.assertEqual("new.cs", first["files"][0]["path"])
                (root / "new.cs").write_text("class NativeInput { int Changed; }")
                self.assertNotEqual(first["sha256"], MEDIA.source_inventory(root)["sha256"])


    def test_source_inventory_excludes_workers_and_generated_sources_even_if_git_lists_them(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = "tests/MeowChan.DesktopSmoke/MediaNativeInput.cs"
            excluded = ["artifacts/media-ux-2026-10-28/workers/sol-media-smoke/Desktop/GalleryVideoPlayer.cs",
                        "artifacts/generated.cs", "tests/MeowChan.DesktopSmoke/obj/Generated.cs",
                        "tests/MeowChan.DesktopSmoke/bin/Generated.cs"]
            for name in [source, *excluded]:
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("class Source {}")
            def inventory_git(command, **kwargs):
                listing = "\0".join([source, *excluded]) + "\0"
                return subprocess.CompletedProcess(command, 0, "d" * 40 if kwargs.get("text") else listing.encode())
            with mock.patch.object(MEDIA.subprocess, "run", side_effect=inventory_git):
                first = MEDIA.source_inventory(root)
                self.assertEqual([source], [item["path"] for item in first["files"]])
                for name in excluded:
                    (root / name).write_text("class ChangedWorkerArtifact {}")
                self.assertEqual(first["sha256"], MEDIA.source_inventory(root)["sha256"])


class WrapperTests(unittest.TestCase):
    def test_stock_private_normal_runner_only_and_clean_environment(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            assembly = root / "MeowChan.DesktopSmoke.dll"
            fixture = root / "synthetic.webm"
            assembly.write_bytes(b"test assembly; not executable")
            fixture.write_bytes(b"test synthetic fixture; not a decoder test")
            output = root / "evidence"
            def runner(command, **kwargs):
                self.assertIn("--mode", command)
                self.assertEqual("normal", command[command.index("--mode") + 1])
                self.assertNotIn("--native-display", command)
                self.assertNotIn("--allow-native-desktop", command)
                env = kwargs["env"]
                self.assertEqual("1", env["MEOWCHAN_MEDIA_INTERACTION_ONLY"])
                self.assertNotIn("MEOWCHAN_NATIVE_IME_ONLY", env)
                (output / "desktop").mkdir()
                (output / "desktop/results.json").write_text(json.dumps({"results": [{"passed": True}]}))
                binding = json.loads((output / "binding.json").read_text())
                _, trace = evidence()
                for key in ("sourceDigest", "fixtureSha256", "assemblySha256"):
                    trace[key] = binding[key]
                Path(env["MEOWCHAN_MEDIA_INTERACTION_REPORT"]).write_text(json.dumps(trace))
                return subprocess.CompletedProcess(command, 0)
            with mock.patch.object(MEDIA.sys, "platform", "linux"), mock.patch.object(MEDIA.ctypes, "CDLL"), \
                 mock.patch.object(MEDIA, "source_inventory", return_value={"sha256": "a" * 64}), \
                 mock.patch.object(MEDIA.subprocess, "run", side_effect=runner), \
                 mock.patch.dict(MEDIA.os.environ, {"MEOWCHAN_NATIVE_IME_ONLY": "1", "DISPLAY": ":personal"}):
                self.assertEqual(0, MEDIA.main(["--assembly", str(assembly), "--fixture", str(fixture), "--output", str(output)]))
            self.assertTrue(json.loads((output / "result.json").read_text())["passed"])
            # Existing evidence is never silently replaced, even if it was failed.
            with self.assertRaises(FileExistsError):
                MEDIA.main(["--assembly", str(assembly), "--output", str(output)])

    def test_unsupported_and_missing_xtst_are_errors_not_successful_skips(self):
        with tempfile.TemporaryDirectory() as directory:
            for platform, library in (("win32", mock.Mock()), ("linux", mock.Mock(side_effect=OSError("no Xtst")))):
                output = Path(directory) / platform
                with mock.patch.object(MEDIA.sys, "platform", platform), mock.patch.object(MEDIA.ctypes, "CDLL", library), \
                     mock.patch.object(MEDIA.subprocess, "run") as runner:
                    self.assertEqual(1, MEDIA.main(["--assembly", "unused.dll", "--output", str(output)]))
                    runner.assert_not_called()
                self.assertFalse(json.loads((output / "result.json").read_text())["passed"])

    def test_stock_runner_scrubs_personal_display_and_profile(self):
        spec = importlib.util.spec_from_file_location("stock_runner", MEDIA.ROOT / "scripts/run-desktop-smoke.py")
        stock = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(stock)
        with tempfile.TemporaryDirectory() as directory:
            env = stock.prepare_environment({"DISPLAY": ":personal", "WAYLAND_DISPLAY": "personal",
                "XAUTHORITY": "/personal/auth", "DBUS_SESSION_BUS_ADDRESS": "personal", "MEOWCHAN_PROFILE": "/personal/profile"},
                True, Path(directory))
            for key in ("DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "DBUS_SESSION_BUS_ADDRESS"):
                self.assertNotIn(key, env)
            self.assertEqual("1", env["MEOWCHAN_DESKTOP_SMOKE_XVFB"])
            self.assertEqual(str(Path(directory) / "profile"), env["MEOWCHAN_PROFILE"])

    def test_bad_timeout_rejected_before_gui(self):
        for timeout in ("nan", "inf", "0", "-1"):
            with self.subTest(timeout=timeout), self.assertRaises(SystemExit):
                MEDIA.parse_args(["--assembly", "unused", "--output", "unused", "--timeout", timeout])


if __name__ == "__main__":
    unittest.main()
