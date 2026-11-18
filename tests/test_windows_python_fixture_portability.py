"""Cross-host regressions for Windows fixtures; no build or graphical process."""
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

from tests import test_ap04_candidate_workflow as ap04
from tests import test_desktop_smoke_runner as smoke


class WindowsPythonFixturePortabilityTests(unittest.TestCase):
    def test_posix_cleanup_assertions_run_with_windows_missing_apis(self):
        # Run the original full assertions, not a reduced copy, with no host
        # killpg/SIGKILL available. The fixture must create and restore them.
        host_os = SimpleNamespace(name="nt")
        host_signal = SimpleNamespace()
        names = (
            "test_posix_cleanup_platform_contracts",
            "test_eperm_requires_exited_leader_and_complete_group_inventory",
            "test_permission_denial_during_probe_or_escalation_stays_fatal",
            "test_group_surviving_sigkill_is_cleanup_failure",
        )
        with mock.patch.object(smoke.runner, "os", host_os), \
                mock.patch.object(smoke.runner, "signal", host_signal), \
                mock.patch.object(smoke, "signal", host_signal):
            result = unittest.TestResult()
            unittest.TestSuite(smoke.DesktopSmokeRunnerTests(name) for name in names).run(result)
        self.assertEqual(4, result.testsRun)
        self.assertEqual([], result.errors)
        self.assertEqual([], result.failures)
        self.assertEqual([], result.skipped)
        self.assertFalse(hasattr(host_os, "killpg"))
        self.assertFalse(hasattr(host_signal, "SIGKILL"))
        self.assertFalse(hasattr(host_signal, "SIGTERM"))
        self.assertEqual("nt", host_os.name)

    def test_windows_selects_git_bash_not_path_wsl_launcher(self):
        with tempfile.TemporaryDirectory(prefix="git shell with spaces ") as temp:
            root = Path(temp)
            (root / "cmd").mkdir()
            git = root / "cmd/git.exe"
            git.touch()
            (root / "bin").mkdir()
            bash = root / "bin/bash.exe"
            bash.touch()
            with mock.patch.object(ap04.sys, "platform", "win32"), \
                    mock.patch.object(ap04.shutil, "which", return_value=str(git)) as which:
                self.assertEqual(str(bash.resolve()), ap04.syntax_bash())
            which.assert_called_once_with("git")

    def test_missing_windows_git_bash_fails_closed(self):
        with mock.patch.object(ap04.sys, "platform", "win32"), \
                mock.patch.object(ap04.shutil, "which", return_value=None):
            with self.assertRaisesRegex(RuntimeError, "Git for Windows Bash not found"):
                ap04.check_shell_syntax("echo valid\n")

    def test_shell_receives_lf_bytes_and_literal_executable_path(self):
        script = 'if true; then\n  echo "quoted path with spaces"\nfi\n'
        shell = "C:/Program Files/Git/bin/bash.exe"
        with mock.patch.object(ap04, "syntax_bash", return_value=shell), \
                mock.patch.object(ap04.subprocess, "run", return_value=SimpleNamespace(returncode=0)) as run:
            ap04.check_shell_syntax(script)
        run.assert_called_once_with([shell, "--noprofile", "--norc", "-n"],
                                    input=script.encode("utf-8"), capture_output=True, timeout=30)

    def test_shell_failure_reports_stdout_even_when_stderr_empty(self):
        with mock.patch.object(ap04, "syntax_bash", return_value="wrong-shell"), \
                mock.patch.object(ap04.subprocess, "run", return_value=subprocess.CompletedProcess(
                    [], 1, b"launcher unavailable", b"")):
            with self.assertRaisesRegex(AssertionError, "(?s)shell='wrong-shell'.*exit=1.*launcher unavailable.*stderr:"):
                ap04.check_shell_syntax("echo valid\n")

    def test_actual_shell_accepts_valid_script_and_rejects_invalid_syntax(self):
        ap04.check_shell_syntax('if true; then\n echo "path with spaces"\nfi\n')
        with self.assertRaisesRegex(AssertionError, "(?s)Bash syntax check failed.*stderr:"):
            ap04.check_shell_syntax("if true; then\n")


if __name__ == "__main__":
    unittest.main()
