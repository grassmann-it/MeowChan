"""Runner safety fixtures. These checks never start Avalonia or a graphical desktop."""
from contextlib import contextmanager, redirect_stdout
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
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


@contextmanager
def posix_signal_constants():
    # These four tests simulate POSIX with a mocked killpg, even on Windows.
    # Never use these fixture constants to signal a real process.
    with mock.patch.object(runner.signal, "SIGTERM", 15, create=True), \
            mock.patch.object(runner.signal, "SIGKILL", 9, create=True):
        yield


class DesktopSmokeRunnerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="meowchan runner fixtures ")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.assembly = self.root / "fixture.dll"
        self.assembly.touch()
        self.output = self.root / "evidence"
        self.args = ["--assembly", str(self.assembly), "--output", str(self.output)]

    @posix_signal_constants()
    def test_posix_cleanup_platform_contracts(self):
        for platform in ("darwin", "linux"):
            for calls, expected in (
                ([ProcessLookupError()], [signal.SIGTERM]),
                ([None, ProcessLookupError()], [signal.SIGTERM, 0]),
                ([None, None, None, None, ProcessLookupError()],
                 [signal.SIGTERM, 0, signal.SIGKILL, 0, 0]),
            ):
                with self.subTest(platform=platform, expected=expected), \
                        mock.patch.object(runner.sys, "platform", platform), \
                        mock.patch.object(runner.os, "name", "posix"), \
                        mock.patch.object(runner.os, "killpg", create=True, side_effect=calls) as kill, \
                        mock.patch.object(runner.time, "monotonic", side_effect=[0, 3, 4, 5]), \
                        mock.patch.object(runner.time, "sleep"):
                    process = mock.Mock(pid=12345)
                    runner.stop_owned_processes(process)
                    self.assertEqual([mock.call(12345, sig) for sig in expected], kill.call_args_list)
                    process.wait.assert_called_once_with(timeout=10)

    @posix_signal_constants()
    def test_eperm_requires_exited_leader_and_complete_group_inventory(self):
        for platform in ("darwin", "linux"):
            for poll, status, table, absent in (
                (None, 0, "1 1\n", False),
                (0, 0, "1 1\n99 12345\n", False),
                (0, 0, "1 1\n99 99\n", True),
                (0, 1, "", False),
                (0, 0, "", False),
                (0, 0, "bad table\n", False),
            ):
                with self.subTest(platform=platform, poll=poll, table=table), \
                        mock.patch.object(runner.sys, "platform", platform), \
                        mock.patch.object(runner.os, "name", "posix"), \
                        mock.patch.object(runner.os, "killpg", create=True, side_effect=PermissionError(1, "denied")) as kill, \
                        mock.patch.object(runner.subprocess, "run", return_value=mock.Mock(
                            returncode=status, stdout=table)) as inventory:
                    process = mock.Mock(pid=12345)
                    process.poll.return_value = poll
                    if absent:
                        runner.stop_owned_processes(process)
                        process.wait.assert_called_once_with(timeout=10)
                    else:
                        with self.assertRaises(PermissionError):
                            runner.stop_owned_processes(process)
                        process.wait.assert_not_called()
                    kill.assert_called_once_with(12345, signal.SIGTERM)
                    if poll is not None:
                        self.assertEqual(["ps", "-A", "-o", "pid=,pgid="], inventory.call_args.args[0])
                    else:
                        inventory.assert_not_called()

    @posix_signal_constants()
    def test_permission_denial_during_probe_or_escalation_stays_fatal(self):
        for calls in ([None, PermissionError(1, "probe denied")],
                      [None, None, PermissionError(1, "kill denied")]):
            with self.subTest(calls=calls), mock.patch.object(runner.os, "name", "posix"), \
                    mock.patch.object(runner.os, "killpg", create=True, side_effect=calls) as kill, \
                    mock.patch.object(runner, "owned_process_group_is_empty", return_value=False), \
                    mock.patch.object(runner.time, "monotonic", side_effect=[0, 3]):
                with self.assertRaises(PermissionError):
                    runner.stop_owned_processes(mock.Mock(pid=12345))
                self.assertEqual(len(calls), kill.call_count)

    def test_inventory_failure_is_not_proof_of_absence(self):
        for error in (OSError("ps unavailable"), subprocess.TimeoutExpired("ps", 5)):
            with self.subTest(error=error), mock.patch.object(runner.os, "name", "posix"), \
                    mock.patch.object(runner.subprocess, "run", side_effect=error):
                process = mock.Mock(pid=12345)
                process.poll.return_value = 0
                self.assertFalse(runner.owned_process_group_is_empty(process))

    @posix_signal_constants()
    def test_group_surviving_sigkill_is_cleanup_failure(self):
        with mock.patch.object(runner.os, "name", "posix"), \
                mock.patch.object(runner.os, "killpg", create=True) as kill, \
                mock.patch.object(runner.time, "monotonic", side_effect=[0, 3, 4, 15]):
            process = mock.Mock(pid=12345)
            process.poll.return_value = 0  # Leader gone, descendant group still present.
            with self.assertRaises(subprocess.TimeoutExpired):
                runner.stop_owned_processes(process)
            self.assertEqual([signal.SIGTERM, 0, signal.SIGKILL, 0],
                             [call.args[1] for call in kill.call_args_list])

    def test_windows_cleanup_checks_taskkill_and_reaps_child(self):
        for status in (0, 1):
            with self.subTest(status=status), mock.patch.object(runner.os, "name", "nt"), \
                    mock.patch.object(runner.subprocess, "run", return_value=mock.Mock(returncode=status)) as kill:
                process = mock.Mock(pid=12345)
                process.poll.return_value = None
                if status:
                    with self.assertRaises(OSError):
                        runner.stop_owned_processes(process)
                else:
                    runner.stop_owned_processes(process)
                    process.wait.assert_called_once_with(timeout=10)
                self.assertEqual(["taskkill", "/PID", "12345", "/T", "/F"], kill.call_args.args[0])

    def test_fixture_children_drop_all_git_routing_and_config_overrides(self):
        environment = runner.prepare_environment({
            "GIT_DIR": "/project/.git", "GIT_WORK_TREE": "/frozen/source",
            "GIT_INDEX_FILE": "/project/.git/index", "GIT_CONFIG_COUNT": "1",
            "KEEP_ME": "retained"}, False, self.root)
        self.assertFalse(any(key.startswith("GIT_") for key in environment))
        self.assertEqual("retained", environment["KEEP_ME"])

    @mock.patch.object(runner.sys, "platform", "linux")
    def test_default_excludes_stress_and_stress_must_be_separate(self):
        self.assertNotIn("tray-lifecycle", runner.parse_args(self.args).modes)
        self.assertEqual(("tray-lifecycle",), runner.parse_args(self.args + ["--mode", "tray-lifecycle"]).modes)
        with self.assertRaises(SystemExit):
            runner.parse_args(self.args + ["--mode", "normal", "--mode", "tray-lifecycle"])

    @mock.patch.object(runner.sys, "platform", "linux")
    def test_board_admission_can_run_in_isolation_without_other_scenarios(self):
        self.assertEqual(("board-admission",), runner.parse_args(self.args + ["--mode", "board-admission"]).modes)
        self.assertNotIn("board-admission", runner.parse_args(self.args).modes)

    @mock.patch.object(runner.sys, "platform", "linux")
    def test_plugin_mode_is_explicit_and_uses_isolated_runner(self):
        arguments = runner.parse_args(self.args + ["--mode", "plugins"])
        self.assertEqual(("plugins",), arguments.modes)
        self.assertNotIn("plugins", runner.parse_args(self.args).modes)
        with self.assertRaises(SystemExit):
            runner.parse_args(self.args + ["--mode", "plugins", "--native-display"])

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
        self.assertEqual("1", prepared["MEOWCHAN_DISABLE_UPDATE_CHECK"])
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
            "MEOWCHAN_DISABLE_UPDATE_CHECK": "0",
            "DISPLAY": ":99",
        }, False, self.root)
        self.assertEqual(str(self.root / "profile"), prepared["MEOWCHAN_PROFILE"])
        self.assertNotIn("OCHAN_PROFILE", prepared)
        self.assertNotIn("MEOWCHAN_DESKTOP_SMOKE_XVFB", prepared)
        self.assertEqual("1", prepared["MEOWCHAN_DISABLE_UPDATE_CHECK"])
        self.assertEqual(":99", prepared["DISPLAY"])

    def test_children_run_with_motion_off_unless_the_caller_chose(self):
        # D1: captures and smokes must not depend on a transition; the motion smoke removes the variable inside its own process.
        self.assertEqual("off", runner.prepare_environment({}, False, self.root)["MEOWCHAN_MOTION"])
        self.assertEqual("on", runner.prepare_environment({"MEOWCHAN_MOTION": "on"}, False, self.root)["MEOWCHAN_MOTION"])

    def long_temp_base(self):
        """A TMPDIR whose private directory cannot hold the session bus socket."""
        base = self.root / ("long-" + "x" * 80)
        base.mkdir()
        return base

    @unittest.skipIf(os.name == "nt", "POSIX /tmp socket-path fixture")
    def test_short_tmpdir_keeps_the_private_directory_where_tempfile_puts_it(self):
        short = Path(tempfile.mkdtemp(prefix="mcs-t-", dir=runner.SHORT_DIRECTORY_BASE))
        self.addCleanup(shutil.rmtree, short, ignore_errors=True)
        with mock.patch.dict(os.environ, {"TMPDIR": str(short)}), mock.patch.object(tempfile, "tempdir", None):
            directory = runner.private_working_directory(True)
            self.addCleanup(directory.cleanup)
            self.assertEqual(short, Path(directory.name).parent)
            self.assertTrue(runner.socket_paths_fit(directory.name))

    @unittest.skipIf(os.name == "nt", "POSIX /tmp socket-path fixture")
    def test_long_tmpdir_moves_only_the_isolated_directory_to_a_short_base_and_cleans_it_up(self):
        base = self.long_temp_base()
        with mock.patch.dict(os.environ, {"TMPDIR": str(base)}), mock.patch.object(tempfile, "tempdir", None):
            self.assertFalse(runner.socket_paths_fit(tempfile.mkdtemp(prefix="meowchan-smoke-cwd-", dir=base)))
            native = runner.private_working_directory(False)
            self.assertEqual(base, Path(native.name).parent)
            native.cleanup()
            with runner.private_working_directory(True) as name:
                self.assertEqual(Path(runner.SHORT_DIRECTORY_BASE), Path(name).parent)
                self.assertTrue(runner.socket_paths_fit(name))
            self.assertFalse(Path(name).exists())
        self.assertEqual(1, len([entry for entry in base.iterdir() if entry.name.startswith("meowchan-smoke-cwd-")]),
                         "only the probe directory of this test is left below the long TMPDIR")

    def test_unusable_short_base_is_a_clear_error_and_leaves_nothing_behind(self):
        base = self.long_temp_base()
        with mock.patch.dict(os.environ, {"TMPDIR": str(base)}), mock.patch.object(tempfile, "tempdir", None), \
                mock.patch.object(runner, "SHORT_DIRECTORY_BASE", str(base)):
            with self.assertRaisesRegex(ValueError, "too long for the private session bus"):
                runner.private_working_directory(True)
        self.assertEqual([], list(base.iterdir()))

    @unittest.skipUnless(sys.platform.startswith("linux") and runner.shutil.which("dbus-run-session"),
                         "needs dbus-run-session (Linux)")
    def test_private_bus_starts_under_a_long_tmpdir_without_any_display(self):
        base = self.long_temp_base()
        with mock.patch.dict(os.environ, {"TMPDIR": str(base)}), mock.patch.object(tempfile, "tempdir", None), \
                runner.private_working_directory(True) as name:
            environment = runner.prepare_environment({"PATH": os.environ["PATH"]}, True, Path(name))
            started = subprocess.run(["dbus-run-session", "--config-file=" + name + "/bus.conf", "--", "true"],
                                     env=environment, capture_output=True, text=True, timeout=30)
        self.assertEqual(0, started.returncode, started.stderr)

    def test_missing_tools_fail_without_starting_any_process(self):
        with mock.patch.object(runner.sys, "platform", "linux"), \
                mock.patch.object(runner.shutil, "which", return_value=None), \
                mock.patch.object(runner.subprocess, "Popen") as start:
            self.assertEqual(1, runner.main(self.args + ["--mode", "normal"]))
            start.assert_not_called()
        result = json.loads((self.output / "results.json").read_text())
        self.assertFalse(result["passed"])
        self.assertEqual("unavailable", result["results"][0]["status"])

    def test_kwin_requires_its_own_tools_and_cannot_target_native_desktop(self):
        with mock.patch.object(runner.sys, "platform", "linux"), \
                mock.patch.dict(os.environ, {"DISPLAY": ":999", "WAYLAND_DISPLAY": "personal"}):
            with self.assertRaises(SystemExit):
                runner.parse_args(self.args + ["--window-manager", "kwin", "--native-display", "--mode", "window-geometry", "--allow-native-desktop"])
            with mock.patch.object(runner.shutil, "which", side_effect=lambda name: None if name == "kwin_wayland" else "fixture"), \
                    mock.patch.object(runner.subprocess, "Popen") as start:
                self.assertEqual(1, runner.main(self.args + ["--window-manager", "kwin", "--mode", "window-geometry"]))
                start.assert_not_called()
        result = json.loads((self.output / "results.json").read_text())
        self.assertIn("kwin_wayland", result["results"][0]["reason"])

    def test_kwin_launch_has_no_personal_display_or_xvfb_wrapper(self):
        def fake_process(command, **kwargs):
            self.assertIn("with-isolated-kwin.py", " ".join(command))
            self.assertNotIn("xvfb-run", command)
            self.assertNotIn("DISPLAY", kwargs["env"])
            self.assertNotIn("WAYLAND_DISPLAY", kwargs["env"])
            self.assertNotEqual("personal-bus", kwargs["env"].get("DBUS_SESSION_BUS_ADDRESS"))
            kwargs["stdout"].write("SMOKE render scaling: actual=1;\nSMOKE PASS: fixture\n")
            return mock.Mock(wait=mock.Mock(return_value=0))
        environment = {"MEOWCHAN_DESKTOP_SMOKE_KWIN": "1", "DISPLAY": ":999", "WAYLAND_DISPLAY": "personal", "DBUS_SESSION_BUS_ADDRESS": "personal-bus"}
        with mock.patch.object(runner.subprocess, "Popen", side_effect=fake_process), mock.patch.object(runner, "stop_owned_processes"):
            result = runner.run_mode("window-geometry", self.assembly, self.output, environment, True, 1)
        self.assertTrue(result["passed"])

    def test_failure_continues_sequence_and_later_pass_cannot_hide_failure(self):
        failure = {"mode": "normal", "passed": False, "status": "failed", "actual_render_scaling": 1}
        success = {**failure, "passed": True, "status": "passed"}
        with mock.patch.object(runner.sys, "platform", "linux"), \
                mock.patch.object(runner.shutil, "which", return_value="fixture"), \
                mock.patch.object(runner, "run_mode", side_effect=[failure] + [success] * 4) as run:
            self.assertEqual(1, runner.main(self.args))
            self.assertEqual(5, run.call_count)
        result = json.loads((self.output / "results.json").read_text())
        self.assertEqual(5, len(result["results"]))
        self.assertFalse(result["passed"])
        self.assertTrue(all(item["status"] == "passed" for item in result["results"][1:]))
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

    def test_xvfb_teardown_status_maps_back_only_to_a_recorded_harness_success(self):
        teardown = "SMOKE PASS: fixture\n/usr/bin/xvfb-run: line 185: kill: (944380) - No such process\n"
        exit_file = self.root / "harness-exit-code"
        self.assertFalse(runner.survived_xvfb_teardown(1, exit_file, teardown))
        exit_file.write_text("0", encoding="utf-8")
        self.assertTrue(runner.survived_xvfb_teardown(1, exit_file, teardown))
        self.assertFalse(runner.survived_xvfb_teardown(2, exit_file, teardown))
        self.assertFalse(runner.survived_xvfb_teardown(1, exit_file, "SMOKE PASS: fixture\n"))
        exit_file.write_text("1", encoding="utf-8")
        self.assertFalse(runner.survived_xvfb_teardown(1, exit_file, teardown))

    def test_isolated_run_passes_when_only_the_xvfb_teardown_failed(self):
        def fake_process(*args, **kwargs):
            Path(kwargs["env"]["MEOWCHAN_SMOKE_HARNESS_EXIT_FILE"]).write_text("0", encoding="utf-8")
            kwargs["stdout"].write("SMOKE PASS: fixture\n/usr/bin/xvfb-run: line 185: kill: (1) - No such process\n")
            return mock.Mock(wait=mock.Mock(return_value=1))
        with mock.patch.object(runner.subprocess, "Popen", side_effect=fake_process), \
                mock.patch.object(runner, "stop_owned_processes"):
            result = runner.run_mode("normal", self.assembly, self.output, {}, True, 1)
        self.assertTrue(result["passed"])
        self.assertTrue(result["xvfb_teardown_ignored"])
        self.assertEqual(0, result["exit_code"])

        def failing_harness(*args, **kwargs):
            Path(kwargs["env"]["MEOWCHAN_SMOKE_HARNESS_EXIT_FILE"]).write_text("1", encoding="utf-8")
            kwargs["stdout"].write("/usr/bin/xvfb-run: line 185: kill: (1) - No such process\n")
            return mock.Mock(wait=mock.Mock(return_value=1))
        with mock.patch.object(runner.subprocess, "Popen", side_effect=failing_harness), \
                mock.patch.object(runner, "stop_owned_processes"):
            result = runner.run_mode("normal", self.assembly, self.root / "failing", {}, True, 1)
        self.assertFalse(result["passed"])
        self.assertFalse(result["xvfb_teardown_ignored"])

    def test_scenario_id_is_derived_from_mode_and_selector_environment(self):
        self.assertEqual("normal", runner.derived_scenario("normal", {"MEOWCHAN_VIDEO_FIXTURE": "x", "MEOWCHAN_X_ONLY": "0"}))
        self.assertEqual("reader-ux+MEOWCHAN_GALLERY_CLOSE_ONLY",
                         runner.derived_scenario("reader-ux", {"MEOWCHAN_GALLERY_CLOSE_ONLY": "1"}))
        self.assertEqual("normal+MEOWCHAN_ARCHIVE_COMPARISON+MEOWCHAN_ARCHIVE_COMPARISON_EXACT_IDS_ONLY",
                         runner.derived_scenario("normal", {"MEOWCHAN_ARCHIVE_COMPARISON_EXACT_IDS_ONLY": "1",
                                                            "MEOWCHAN_ARCHIVE_COMPARISON": "1"}))
        self.assertEqual("normal+MEOWCHAN_URSA_NATIVE_SUITE=groups",
                         runner.derived_scenario("normal", {"MEOWCHAN_URSA_NATIVE_SUITE": "groups"}))
        self.assertEqual("plugins", runner.derived_scenario("plugins", {"MEOWCHAN_URSA_NATIVE_SUITE": "plugins"}))

    def run_with_log(self, log, environment, mode="normal", expected=None, name="scenario"):
        def fake_process(*args, **kwargs):
            kwargs["stdout"].write(log)
            return mock.Mock(wait=mock.Mock(return_value=0))
        with mock.patch.object(runner.subprocess, "Popen", side_effect=fake_process), \
                mock.patch.object(runner, "stop_owned_processes"):
            return runner.run_mode(mode, self.assembly, self.root / name, environment, False, 1, expected)

    def test_pass_from_another_scenario_is_not_reported_as_passed(self):
        selector = {"MEOWCHAN_READER_FIND_ONLY": "1"}
        result = self.run_with_log("SMOKE SCENARIO: normal+MEOWCHAN_READER_FIND_ONLY\nSMOKE PASS: fixture\n", selector)
        self.assertTrue(result["passed"])
        self.assertEqual("normal+MEOWCHAN_READER_FIND_ONLY", result["scenario"])
        result = self.run_with_log("SMOKE SCENARIO: normal\nSMOKE PASS: fixture\n", selector, name="fallthrough")
        self.assertFalse(result["passed"])
        self.assertIn("expected normal+MEOWCHAN_READER_FIND_ONLY", result["error"])

    def test_mac_screenshot_opt_in_and_warnings_are_independent_of_smoke_pass(self):
        warning = "WARNING macOS screenshot main-window: the captured screen is black or transparent"
        for platform, enabled, mode, scenario, capture in (
            ("darwin", True, "normal", "normal+MEOWCHAN_NORMAL_CORE_ONLY", True),
            ("darwin", True, "reader-ux", "reader-ux+MEOWCHAN_GALLERY_CLOSE_ONLY", True),
            ("darwin", True, "selection", "selection", True),
            ("darwin", True, "normal", "normal", False),
            ("darwin", False, "selection", "selection", False),
            ("win32", True, "selection", "selection", False),
        ):
            with self.subTest(platform=platform, enabled=enabled, scenario=scenario), \
                    tempfile.TemporaryDirectory(dir=self.root) as temporary:
                destination = Path(temporary) / "evidence"
                environment = {"MEOWCHAN_MAC_SCREENSHOTS": "1" if enabled else "0",
                               "MEOWCHAN_MAC_SCREENSHOT_DIR": str(self.root / "inherited")}

                def fake_process(*args, **kwargs):
                    self.assertEqual(kwargs["env"].get("MEOWCHAN_MAC_SCREENSHOT_DIR"),
                                     str(destination) if capture else None)
                    if capture:
                        kwargs["stdout"].write(warning + "\n")
                    kwargs["stdout"].write(f"SMOKE SCENARIO: {scenario}\nSMOKE PASS: fixture\n")
                    return mock.Mock(wait=mock.Mock(return_value=0))

                with mock.patch.object(runner.sys, "platform", platform), \
                        mock.patch.object(runner.subprocess, "Popen", side_effect=fake_process), \
                        mock.patch.object(runner, "stop_owned_processes"), redirect_stdout(io.StringIO()) as output:
                    result = runner.run_mode(mode, self.assembly, destination, environment, False, 1, scenario)
                self.assertTrue(result["passed"])
                self.assertEqual(warning in output.getvalue(), capture)

    def test_explicit_scenario_is_exact_and_requires_the_line(self):
        result = self.run_with_log("SMOKE SCENARIO: selection\nSMOKE PASS: fixture\n", {}, "selection", "selection")
        self.assertTrue(result["passed"])
        result = self.run_with_log("SMOKE PASS: fixture\n", {}, "selection", "selection", "missing")
        self.assertFalse(result["passed"])
        self.assertIn("Missing SMOKE SCENARIO", result["error"])
        # Without --expect-scenario an assembly that predates the line keeps working.
        self.assertTrue(self.run_with_log("SMOKE PASS: fixture\n", {}, name="older")["passed"])
        with mock.patch.object(runner.sys, "platform", "linux"):
            self.assertEqual("x", runner.parse_args(self.args + ["--mode", "normal", "--expect-scenario", "x"]).expect_scenario)
            with self.assertRaises(SystemExit):
                runner.parse_args(self.args + ["--expect-scenario", "normal"])

    REGISTRY = [
        {"id": "normal", "kind": "Mode", "mode": "normal", "environment": [], "scenario": "normal", "skip": None},
        {"id": "gallery-error", "kind": "Root", "mode": "normal", "scenario": "normal+MEOWCHAN_GALLERY_ERROR_ONLY", "skip": None,
         "environment": ["MEOWCHAN_GALLERY_ERROR_ONLY=1", "MEOWCHAN_VIDEO_FIXTURE={repo}/tests/Fixtures/gallery-playback-vp8.webm"]},
        {"id": "gallery-edge", "kind": "Nested", "mode": "normal", "skip": None,
         "scenario": "normal+MEOWCHAN_GALLERY_EDGE_ONLY+MEOWCHAN_GALLERY_PLAYBACK_FEATURES_ONLY",
         "environment": ["MEOWCHAN_GALLERY_PLAYBACK_FEATURES_ONLY=1", runner.FUNCTIONAL_AUDIO, "MEOWCHAN_GALLERY_EDGE_ONLY=1"]},
        {"id": "native-ime", "kind": "Root", "mode": "normal", "environment": ["MEOWCHAN_NATIVE_IME_ONLY=1"],
         "scenario": "normal+MEOWCHAN_NATIVE_IME_ONLY", "skip": "needs its independent native driver"},
        {"id": "broken", "kind": "Nested", "mode": "normal", "environment": ["MEOWCHAN_BROKEN_ONLY=1"],
         "scenario": "normal", "skip": None, "error": "unknown selector MEOWCHAN_BROKEN_ONLY=1"}]

    @staticmethod
    def child_runner(*outcomes):
        """Fake entry runners: each writes the results.json its real runner would and exits with the outcome's code."""
        pending = iter(outcomes)

        def run(command, **kwargs):
            outcome = next(pending)
            destination = Path(command[command.index("--output") + 1])
            mode = command[command.index("--mode") + 1]
            destination.mkdir(parents=True)
            (destination / "results.json").write_text(json.dumps({"results": [{
                "mode": mode, "passed": outcome.returncode == 0 and outcome.no_pass is not True,
                "scenario": command[command.index("--expect-scenario") + 1], "error": None}]}))
            return outcome
        return run

    def test_registry_is_linux_only_and_selects_its_own_modes(self):
        with mock.patch.object(runner.sys, "platform", "linux"):
            self.assertTrue(runner.parse_args(self.args + ["--registry", "--only", "normal"]).registry)
            for extra in (["--mode", "normal"], ["--expect-scenario", "normal"], ["--scale", "2"], ["--native-display"]):
                with self.assertRaises(SystemExit):
                    runner.parse_args(self.args + ["--registry", *extra])
            with self.assertRaises(SystemExit):
                runner.parse_args(self.args + ["--only", "normal"])
        with mock.patch.object(runner.sys, "platform", "darwin"), self.assertRaises(SystemExit):
            runner.parse_args(self.args + ["--registry", "--allow-native-desktop"])

    def test_registry_child_carries_exactly_its_entry_and_audio_wrapper(self):
        repository = Path("/repo root")
        command = runner.registry_command(self.REGISTRY[1], self.assembly, self.output / "gallery-error", 300, "openbox", repository)
        self.assertEqual("env", command[0])
        expected_fixture = "MEOWCHAN_VIDEO_FIXTURE=" + str(repository / "tests" / "Fixtures" / "gallery-playback-vp8.webm")
        self.assertIn(expected_fixture, command)
        self.assertEqual("normal+MEOWCHAN_GALLERY_ERROR_ONLY", command[command.index("--expect-scenario") + 1])
        self.assertEqual("normal", command[command.index("--mode") + 1])
        audio = runner.registry_command(self.REGISTRY[2], self.assembly, self.output / "gallery-edge", 300, "openbox", repository)
        self.assertTrue(audio[1].endswith("with-private-null-audio.py"))
        self.assertEqual(["--", "env"], audio[4:6])

    def test_required_registry_smokes_reject_skips_and_duplicate_results(self):
        required = self.args + ["--registry", "--require-passed", "--only"]
        with mock.patch.object(runner.sys, "platform", "linux"), \
                mock.patch.object(runner, "registry_entries", return_value=self.REGISTRY):
            self.assertEqual(1, runner.main(required + ["native-ime"]))
        report = json.loads((self.output / "registry.json").read_text())
        self.assertEqual(1, report["counts"]["skipped"])
        duplicate_output = self.output / "duplicates"
        args = ["--assembly", str(self.assembly), "--output", str(duplicate_output),
                "--registry", "--require-passed", "--only", "normal"]
        passed = mock.Mock(returncode=0, stdout="", stderr="", no_pass=False)
        with mock.patch.object(runner.sys, "platform", "linux"), \
                mock.patch.object(runner, "registry_entries", return_value=self.REGISTRY[:1] * 2), \
                mock.patch.object(runner.subprocess, "run", return_value=passed), \
                mock.patch.object(runner, "child_result", return_value={"child_passed": True}):
            self.assertEqual(1, runner.main(args))

    def test_registry_runs_every_entry_records_skips_and_retries_only_the_window_manager_flake(self):
        fake = self.child_runner(mock.Mock(returncode=1, stdout="RuntimeError: The isolated window manager exited during startup", stderr=""),
                                 mock.Mock(returncode=0, stdout="", stderr=""),
                                 mock.Mock(returncode=1, stdout="SMOKE check failed", stderr=""),
                                 mock.Mock(returncode=0, stdout="", stderr=""))
        environment = {"MEOWCHAN_READER_FIND_ONLY": "1", "MEOWCHAN_ARCHIVE_COMPARISON": "1", "KEEP": "1"}
        with mock.patch.object(runner.sys, "platform", "linux"), \
                mock.patch.dict(runner.os.environ, environment), \
                mock.patch.object(runner, "registry_entries", return_value=self.REGISTRY), \
                mock.patch.object(runner.subprocess, "run", side_effect=fake) as run:
            self.assertEqual(1, runner.main(self.args + ["--registry"]))
        self.assertEqual(4, run.call_count)
        for call in run.call_args_list:
            self.assertNotIn("MEOWCHAN_READER_FIND_ONLY", call.kwargs["env"])
            self.assertNotIn("MEOWCHAN_ARCHIVE_COMPARISON", call.kwargs["env"])
            self.assertEqual("1", call.kwargs["env"]["KEEP"])
        report = json.loads((self.output / "registry.json").read_text())
        # The failing entry does not stop the sweep; an entry the assembly rejects is a failure, not a skip.
        self.assertEqual({"passed": 2, "failed": 2, "skipped": 1}, report["counts"])
        self.assertEqual(["gallery-error", "broken"], report["failed"])
        self.assertEqual(2, len(report["results"][0]["attempts"]))
        self.assertEqual("normal", report["results"][0]["attempts"][-1]["observed_scenario"])
        self.assertEqual("skipped", report["results"][3]["status"])
        self.assertIn("invalid registry invocation", report["results"][4]["reason"])

    def test_registry_needs_the_entry_runners_own_pass_verdict_not_only_its_exit_code(self):
        silent = mock.Mock(returncode=0, stdout="", stderr="", no_pass=True)
        with mock.patch.object(runner.sys, "platform", "linux"), \
                mock.patch.object(runner, "registry_entries", return_value=self.REGISTRY[:1]), \
                mock.patch.object(runner.subprocess, "run", side_effect=self.child_runner(silent)):
            self.assertEqual(1, runner.main(self.args + ["--registry"]))
        self.assertEqual(["normal"], json.loads((self.output / "registry.json").read_text())["failed"])
        missing = self.root / "missing"
        with mock.patch.object(runner.sys, "platform", "linux"), \
                mock.patch.object(runner, "registry_entries", return_value=self.REGISTRY[:1]), \
                mock.patch.object(runner.subprocess, "run", return_value=mock.Mock(returncode=0, stdout="", stderr="")):
            self.assertEqual(1, runner.main(["--assembly", str(self.assembly), "--output", str(missing), "--registry"]))
        self.assertEqual("no results.json from the entry runner",
                         json.loads((missing / "registry.json").read_text())["results"][0]["attempts"][0]["error"])
        with mock.patch.object(runner.sys, "platform", "linux"), \
                mock.patch.object(runner, "registry_entries", return_value=self.REGISTRY), self.assertRaises(ValueError):
            runner.main(self.args + ["--registry", "--only", "normal"])  # evidence directory is not fresh

    def test_registry_report_survives_an_interrupted_sweep(self):
        passed = self.child_runner(mock.Mock(returncode=0, stdout="", stderr=""))
        calls = []

        def run(command, **kwargs):
            calls.append(command)
            if len(calls) == 2:
                raise KeyboardInterrupt
            return passed(command, **kwargs)
        with mock.patch.object(runner.sys, "platform", "linux"), \
                mock.patch.object(runner, "registry_entries", return_value=self.REGISTRY[:2]), \
                mock.patch.object(runner.subprocess, "run", side_effect=run):
            self.assertEqual(130, runner.main(self.args + ["--registry"]))
        report = json.loads((self.output / "registry.json").read_text())
        self.assertEqual((False, True), (report["complete"], report["interrupted"]))
        self.assertEqual(["normal"], [result["id"] for result in report["results"]])
        self.assertEqual({"passed": 1, "failed": 0, "skipped": 0}, report["counts"])

    def test_registry_failure_reports_the_harness_failure_line_or_the_timeout(self):
        destination = self.root / "entry"
        (destination / "normal").mkdir(parents=True)
        (destination / "normal" / "output.log").write_text(
            "SMOKE APP noise\nSMOKE FAILED: exit=0, checks=False; System.TimeoutException: at X.cs:12.\n   at Y\n")
        (destination / "results.json").write_text(json.dumps({"results": [
            {"mode": "normal", "passed": False, "timed_out": False, "exit_code": 1, "scenario": None, "error": None}]}))
        self.assertEqual("SMOKE FAILED: exit=0, checks=False; System.TimeoutException: at X.cs:12.",
                         runner.child_result(destination, "normal")["error"])
        (destination / "results.json").write_text(json.dumps({"results": [
            {"mode": "normal", "passed": False, "timed_out": True, "exit_code": 124, "scenario": None, "error": None}]}))
        self.assertEqual("timed out", runner.child_result(destination, "normal")["error"])

    @unittest.skipUnless(hasattr(os, "getloadavg"), "host load average is unavailable on this platform")
    def test_results_record_host_load_and_a_timeout_on_an_overloaded_host_says_so(self):
        self.assertEqual({"loadavg_1m", "cpus"}, set(runner.host_load()))
        with mock.patch.object(runner.os, "getloadavg", return_value=(99.0, 1.0, 1.0)), \
                mock.patch.object(runner.os, "cpu_count", return_value=4):
            self.assertEqual({"loadavg_1m": 99.0, "cpus": 4}, runner.host_load())
        with mock.patch.object(runner.os, "getloadavg", side_effect=OSError):
            self.assertIsNone(runner.host_load()["loadavg_1m"])

    def test_runs_sharing_one_assembly_bin_get_independent_copies_and_cleanup_on_failure(self):
        self.assembly.write_text("original")
        (self.root / "smoke-stale.json").write_text("old evidence")
        with self.assertRaisesRegex(RuntimeError, "fixture failure"):
            with runner.private_assembly(self.assembly) as first:
                with runner.private_assembly(self.assembly) as second:
                    self.assertNotEqual(first.parent, second.parent)
                    self.assertEqual(self.assembly.read_bytes(), first.read_bytes())
                    first.write_text("child modified its own dependency")
                    self.assertEqual("original", second.read_text())
                    self.assertEqual("original", self.assembly.read_text())
                    self.assertFalse(list(first.parent.glob("smoke-*")))
                    self.assertFalse(list(second.parent.glob(".smoke-run-*")))
                    raise RuntimeError("fixture failure")
        self.assertFalse(first.parent.exists())
        self.assertFalse(second.parent.exists())
        self.assertFalse(list(self.root.glob(".smoke-run-*")))

    @unittest.skipIf(os.name == "nt", "POSIX process-group fixture")
    def test_timeout_kills_only_the_owned_group_including_its_child(self):
        self.check_timeout_group_cleanup(startup_delay=0)

    @unittest.skipIf(os.name == "nt", "POSIX process-group fixture")
    def test_timeout_group_fixture_waits_for_child_readiness_after_slow_startup(self):
        # Longer than the app timeout: previously this killed the parent before it could write child.pid.
        self.check_timeout_group_cleanup(startup_delay=0.75)

    def check_timeout_group_cleanup(self, startup_delay):
        # A fake dotnet process forks a child that ignores TERM. No window or display is involved.
        executable = self.root / "dotnet"
        marker = self.root / "child.pid"
        executable.write_text(f"#!{sys.executable}\nimport os, signal, time\n"
                              f"time.sleep({startup_delay!r})\n"
                              "child = os.fork()\n"
                              "if child == 0:\n"
                              " signal.signal(signal.SIGTERM, signal.SIG_IGN)\n"
                              f" with open({str(marker) + '.tmp'!r}, 'w') as ready: ready.write(str(os.getpid()))\n"
                              f" os.replace({str(marker) + '.tmp'!r}, {str(marker)!r})\n"
                              " while True: time.sleep(1)\n"
                              "while True: time.sleep(1)\n")
        executable.chmod(0o700)
        environment = os.environ.copy()
        environment["PATH"] = str(self.root) + os.pathsep + environment.get("PATH", "")
        unrelated = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"], start_new_session=True)
        child = None
        popen = subprocess.Popen
        def start_ready(command, **kwargs):
            nonlocal child
            process = popen(command, **kwargs)
            if command[0] != "dotnet":
                return process
            # Start the cleanup-test clock only once its TERM-resistant descendant exists.
            # This bounds fixture startup separately; it does not extend the production app timeout.
            try:
                deadline = time.monotonic() + 10
                while not marker.exists() and process.poll() is None and time.monotonic() < deadline:
                    time.sleep(0.01)
                self.assertTrue(marker.exists(), "Fake dotnet child did not become ready before fixture deadline")
                child = int(marker.read_text())
                self.assertNotEqual(child, process.pid)
                self.assertEqual(os.getpgid(child), process.pid)
                self.assertTrue(self.process_is_live(child))
                return process
            except BaseException:
                runner.stop_owned_processes(process)
                raise
        try:
            with mock.patch.object(runner.subprocess, "Popen", side_effect=start_ready):
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

    @mock.patch.object(sys, "platform", "linux")
    def test_process_liveness_handles_reaping_but_not_permission_errors(self):
        for absent in (FileNotFoundError(2, "gone"), ProcessLookupError(3, "reaped")):
            with self.subTest(error=type(absent).__name__), mock.patch.object(Path, "read_text", side_effect=absent):
                self.assertFalse(self.process_is_live(123))
        with mock.patch.object(Path, "read_text", side_effect=PermissionError(13, "denied")):
            with self.assertRaises(PermissionError):
                self.process_is_live(123)
        for state, live in (("S", True), ("R", True), ("Z", False)):
            with self.subTest(state=state), mock.patch.object(Path, "read_text", return_value=f"123 (fixture child) {state}"):
                self.assertEqual(live, self.process_is_live(123))

    @staticmethod
    def process_is_live(pid):
        if sys.platform.startswith("linux"):
            try:
                return Path(f"/proc/{pid}/stat").read_text().split(") ", 1)[1][0] != "Z"
            except (FileNotFoundError, ProcessLookupError):
                # /proc can disappear after open but before read when the child is reaped.
                return False
        probe = subprocess.run(["ps", "-o", "stat=", "-p", str(pid)], capture_output=True, text=True)
        return probe.returncode == 0 and bool(probe.stdout.strip()) and not probe.stdout.strip().startswith("Z")


if __name__ == "__main__":
    unittest.main()
