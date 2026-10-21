"""Runner safety fixtures. These checks never start Avalonia or a graphical desktop."""
import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "run-desktop-smoke.py"
SPEC = importlib.util.spec_from_file_location("desktop_smoke_runner", SCRIPT)
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)


class DesktopSmokeRunnerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="meowchan runner fixtures ")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.assembly = self.root / "fixture.dll"
        self.assembly.touch()
        self.output = self.root / "evidence"
        self.args = ["--assembly", str(self.assembly), "--output", str(self.output)]

    @mock.patch.object(runner.sys, "platform", "linux")
    def test_default_excludes_stress_and_stress_must_be_separate(self):
        self.assertNotIn("tray-lifecycle", runner.parse_args(self.args).modes)
        self.assertEqual(("tray-lifecycle",), runner.parse_args(self.args + ["--mode", "tray-lifecycle"]).modes)
        with self.assertRaises(SystemExit):
            runner.parse_args(self.args + ["--mode", "normal", "--mode", "tray-lifecycle"])

    def test_tray_host_cannot_run_on_a_personal_native_desktop(self):
        with mock.patch.object(runner.sys, "platform", "linux"):
            self.assertEqual(("tray-host",), runner.parse_args(self.args + ["--mode", "tray-host"]).modes)
            with self.assertRaises(SystemExit):
                runner.parse_args(self.args + ["--mode", "tray-host", "--native-display", "--allow-native-desktop"])
        for platform in ("win32", "darwin"):
            with mock.patch.object(runner.sys, "platform", platform), self.assertRaises(SystemExit):
                runner.parse_args(self.args + ["--mode", "tray-host", "--allow-native-desktop"])

    def test_native_desktop_requires_one_explicit_scenario(self):
        with mock.patch.object(runner.sys, "platform", "linux"), mock.patch.dict(os.environ, {"DISPLAY": ":999"}):
            with self.assertRaises(SystemExit):
                runner.parse_args(self.args + ["--native-display"])
            with self.assertRaises(SystemExit):
                runner.parse_args(self.args + ["--native-display", "--mode", "normal", "--mode", "no-tray"])
            with self.assertRaises(SystemExit):
                runner.parse_args(self.args + ["--native-display", "--mode", "normal"])
            self.assertEqual(("normal",), runner.parse_args(self.args + ["--native-display", "--allow-native-desktop", "--mode", "normal"]).modes)

    def test_native_windows_and_macos_require_explicit_opt_in(self):
        for platform in ("win32", "darwin"):
            with mock.patch.object(runner.sys, "platform", platform), self.assertRaises(SystemExit):
                runner.parse_args(self.args)

    def test_nonfinite_deadlines_are_rejected(self):
        for value in ("nan", "inf", "0", "-1"):
            with self.subTest(value=value), self.assertRaises(SystemExit):
                runner.parse_args(self.args + ["--timeout", value])

    def test_isolation_removes_all_inherited_session_endpoints(self):
        base = {key: "personal-session-endpoint" for key in runner.SESSION_VARIABLES}
        base["PATH"] = "fixture-tools"
        base["MEOWCHAN_PROFILE"] = "personal-new-profile"
        base["OCHAN_PROFILE"] = "personal-old-profile"
        prepared = runner.prepare_environment(base, True, self.root)
        self.assertTrue(all(key not in prepared for key in runner.SESSION_VARIABLES))
        self.assertEqual("personal-session-endpoint", base["DISPLAY"])
        self.assertEqual("fixture-tools", prepared["PATH"])
        self.assertEqual("1", prepared["MEOWCHAN_DESKTOP_SMOKE_XVFB"])
        self.assertEqual(str(self.root / "profile"), prepared["MEOWCHAN_PROFILE"])
        self.assertNotIn("OCHAN_PROFILE", prepared)
        self.assertNotIn("servicedir", (self.root / "bus.conf").read_text())
        for key in ("XDG_RUNTIME_DIR", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME"):
            self.assertTrue(Path(prepared[key]).is_relative_to(self.root))
        if os.name != "nt":
            self.assertEqual(0o700, Path(prepared["XDG_RUNTIME_DIR"]).stat().st_mode & 0o777)

    def test_native_runner_uses_private_profile_and_drops_isolated_display_flag(self):
        prepared = runner.prepare_environment({
            "MEOWCHAN_PROFILE": "personal-new-profile",
            "OCHAN_PROFILE": "personal-old-profile",
            "MEOWCHAN_DESKTOP_SMOKE_XVFB": "1",
            "DISPLAY": ":99",
        }, False, self.root)
        self.assertEqual(str(self.root / "profile"), prepared["MEOWCHAN_PROFILE"])
        self.assertNotIn("OCHAN_PROFILE", prepared)
        self.assertNotIn("MEOWCHAN_DESKTOP_SMOKE_XVFB", prepared)
        self.assertEqual(":99", prepared["DISPLAY"])

    def test_missing_tools_fail_without_starting_any_process(self):
        with mock.patch.object(runner.sys, "platform", "linux"), \
                mock.patch.object(runner.shutil, "which", return_value=None), \
                mock.patch.object(runner.subprocess, "Popen") as start:
            self.assertEqual(1, runner.main(self.args + ["--mode", "normal"]))
            start.assert_not_called()
        result = json.loads((self.output / "results.json").read_text())
        self.assertFalse(result["passed"])
        self.assertEqual("unavailable", result["results"][0]["status"])

    def test_failure_stops_sequence_and_records_unrun_scenarios(self):
        failure = {"mode": "normal", "passed": False, "status": "failed", "actual_render_scaling": 1}
        with mock.patch.object(runner.sys, "platform", "linux"), \
                mock.patch.object(runner.shutil, "which", return_value="fixture"), \
                mock.patch.object(runner, "run_mode", return_value=failure) as run:
            self.assertEqual(1, runner.main(self.args))
            self.assertEqual(1, run.call_count)
        result = json.loads((self.output / "results.json").read_text())
        self.assertEqual(5, len(result["results"]))
        self.assertTrue(all(item["status"] == "skipped" for item in result["results"][1:]))
        with mock.patch.object(runner.sys, "platform", "linux"), self.assertRaises(ValueError):
            runner.main(self.args)

    def test_wrong_scale_and_cleanup_failure_cannot_be_reported_as_passed(self):
        def fake_process(*args, **kwargs):
            kwargs["stdout"].write("SMOKE render scaling: actual=1;\nSMOKE PASS: fixture\n")
            return mock.Mock(wait=mock.Mock(return_value=0))
        with mock.patch.object(runner.subprocess, "Popen", side_effect=fake_process), \
                mock.patch.object(runner, "stop_owned_processes"):
            result = runner.run_mode("normal", self.assembly, self.output,
                                     {"AVALONIA_GLOBAL_SCALE_FACTOR": "2"}, False, 1)
        self.assertFalse(result["passed"])
        self.assertIn("Requested scale", result["error"])
        with mock.patch.object(runner.subprocess, "Popen", side_effect=fake_process), \
                mock.patch.object(runner, "stop_owned_processes", side_effect=OSError("fixture cleanup failure")):
            result = runner.run_mode("normal", self.assembly, self.root / "cleanup", {}, False, 1)
        self.assertFalse(result["passed"])
        self.assertIn("Process cleanup failed", result["error"])

    @unittest.skipIf(os.name == "nt", "POSIX process-group fixture")
    def test_timeout_kills_only_the_owned_group_including_its_child(self):
        # A fake dotnet process forks a child that ignores TERM. No window or display is involved.
        executable = self.root / "dotnet"
        marker = self.root / "child.pid"
        executable.write_text(f"#!{sys.executable}\nimport os, signal, time\n"
                              "child = os.fork()\n"
                              "if child == 0:\n"
                              " signal.signal(signal.SIGTERM, signal.SIG_IGN)\n"
                              " while True: time.sleep(1)\n"
                              f"open({str(marker)!r}, 'w').write(str(child))\n"
                              "while True: time.sleep(1)\n")
        executable.chmod(0o700)
        environment = os.environ.copy()
        environment["PATH"] = str(self.root) + os.pathsep + environment.get("PATH", "")
        unrelated = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"], start_new_session=True)
        child = None
        try:
            result = runner.run_mode("normal", self.assembly, self.output, environment, False, 0.5)
            self.assertTrue(result["timed_out"])
            self.assertEqual(124, result["exit_code"])
            self.assertFalse(result["passed"])
            child = int(marker.read_text())
            deadline = time.monotonic() + 3
            while self.process_is_live(child) and time.monotonic() < deadline:
                time.sleep(0.05)
            self.assertFalse(self.process_is_live(child), "The timed-out fixture left a live child")
            self.assertIsNone(unrelated.poll(), "Cleanup touched a process outside its owned group")
        finally:
            unrelated.terminate()
            unrelated.wait(timeout=5)
            if child is not None and self.process_is_live(child):
                os.kill(child, signal.SIGKILL)

    @staticmethod
    def process_is_live(pid):
        if sys.platform.startswith("linux"):
            try:
                return Path(f"/proc/{pid}/stat").read_text().split(") ", 1)[1][0] != "Z"
            except FileNotFoundError:
                return False
        probe = subprocess.run(["ps", "-o", "stat=", "-p", str(pid)], capture_output=True, text=True)
        return probe.returncode == 0 and bool(probe.stdout.strip()) and not probe.stdout.strip().startswith("Z")


if __name__ == "__main__":
    unittest.main()
