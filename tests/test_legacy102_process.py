"""Real, lightweight subprocess coverage for the legacy 1.0.2 process helper."""
from __future__ import annotations

import importlib.util
import os
from pathlib import Path
import sys
import tempfile
import time
import unittest
from unittest import mock


class SyntheticBaseException(BaseException):
    pass


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "legacy102_process.py"
SPEC = importlib.util.spec_from_file_location("legacy102_process", SCRIPT)
PROCESS = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(PROCESS)


class Legacy102ProcessTests(unittest.TestCase):
    def command_environment(self) -> dict[str, str]:
        # The fixture needs no user profile or package state.  In particular,
        # do not pass the complete test runner environment to the child.
        return {
            "PATH": os.environ.get("PATH", ""),
            "PYTHONIOENCODING": "utf-8",
            "PYTHONUNBUFFERED": "1",
        }

    def run_command(self, root: Path, *arguments: str, timeout: float = 2.0) -> str:
        return PROCESS.run_bounded_command(
            [sys.executable, *arguments],
            root,
            self.command_environment(),
            root / "command.log",
            timeout,
            "fixture command",
        )

    @staticmethod
    def process_tree_code(marker: Path) -> tuple[str, str, str]:
        descendant_code = (
            "import pathlib, sys, time; time.sleep(0.3); "
            "pathlib.Path(sys.argv[1]).write_text('survived', encoding='utf-8')"
        )
        parent_code = (
            "import subprocess, sys, time; "
            "subprocess.Popen([sys.executable, '-c', sys.argv[1], sys.argv[2]]); "
            "time.sleep(0.8)"
        )
        return parent_code, descendant_code, str(marker)

    @unittest.skipUnless(os.name == "posix", "requires POSIX process groups")
    def test_keyboard_interrupt_mid_wait_kills_fixture_process_group(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            marker = root / "keyboard-descendant-survived"
            parent_code, descendant_code, marker_arg = self.process_tree_code(marker)
            with mock.patch.object(
                PROCESS.selectors.DefaultSelector,
                "select",
                side_effect=KeyboardInterrupt,
            ):
                with self.assertRaises(KeyboardInterrupt):
                    self.run_command(
                        root,
                        "-c",
                        parent_code,
                        descendant_code,
                        marker_arg,
                        timeout=2.0,
                    )
            time.sleep(0.7)
            self.assertFalse(marker.exists())

    @unittest.skipUnless(os.name == "posix", "requires POSIX process groups")
    def test_other_base_exception_mid_wait_kills_fixture_process_group(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            marker = root / "base-exception-descendant-survived"
            parent_code, descendant_code, marker_arg = self.process_tree_code(marker)
            with mock.patch.object(
                PROCESS.selectors.DefaultSelector,
                "select",
                side_effect=SyntheticBaseException("synthetic wait failure"),
            ):
                with self.assertRaises(SyntheticBaseException):
                    self.run_command(
                        root,
                        "-c",
                        parent_code,
                        descendant_code,
                        marker_arg,
                        timeout=2.0,
                    )
            time.sleep(0.7)
            self.assertFalse(marker.exists())

    @unittest.skipUnless(os.name == "posix", "requires POSIX process groups")
    def test_read_error_cleans_up_child_and_bounded_output(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            marker = root / "read-error-child-survived"
            code = (
                "import pathlib, time; print('before-read-error', flush=True); "
                "time.sleep(0.8); pathlib.Path(r'%s').write_text('survived', encoding='utf-8')"
                % marker
            )
            with mock.patch.object(
                PROCESS,
                "_read_nonblocking",
                side_effect=OSError("synthetic pipe read failure"),
            ):
                with self.assertRaisesRegex(OSError, "synthetic pipe read failure"):
                    self.run_command(root, "-c", code, timeout=2.0)
            time.sleep(0.7)
            self.assertFalse(marker.exists())
            self.assertLessEqual((root / "command.log").stat().st_size, PROCESS.MAX_TAIL_BYTES)

    def test_zero_timeout_does_not_start_a_child(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with mock.patch.object(PROCESS.subprocess, "Popen", side_effect=AssertionError("started")):
                with self.assertRaises(PROCESS.CommandTimedOut):
                    PROCESS.run_bounded_command(
                        [sys.executable, "-c", "raise SystemExit(99)"],
                        root,
                        self.command_environment(),
                        root / "zero-timeout.log",
                        0,
                        "zero timeout",
                    )
            self.assertEqual(b"", (root / "zero-timeout.log").read_bytes())

    def test_empty_and_invalid_argument_lists_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with self.assertRaises(ValueError):
                PROCESS.run_bounded_command(
                    [], root, self.command_environment(), root / "empty.log", 1.0, "empty"
                )
            with self.assertRaises(TypeError):
                PROCESS.run_bounded_command(
                    [sys.executable, 7], root, self.command_environment(), root / "invalid.log", 1.0, "invalid"
                )
            with self.assertRaises(TypeError):
                PROCESS.run_bounded_command(
                    (sys.executable,), root, self.command_environment(), root / "tuple.log", 1.0, "tuple"
                )

    def test_success_captures_stdout_and_stderr_in_bounded_log(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            output = self.run_command(
                root,
                "-c",
                "import sys; print('stdout'); print('stderr', file=sys.stderr)",
            )
            self.assertIn("stdout", output)
            self.assertIn("stderr", output)
            self.assertEqual(output.encode("utf-8"), (root / "command.log").read_bytes())

    def test_nonzero_exit_raises_command_failed_and_keeps_tail(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with self.assertRaises(PROCESS.CommandFailed) as raised:
                self.run_command(
                    root,
                    "-c",
                    "import sys; print('failure', file=sys.stderr); raise SystemExit(7)",
                )
            self.assertEqual(7, raised.exception.returncode)
            self.assertIn("failure", raised.exception.output)
            self.assertIn("failure", (root / "command.log").read_text(encoding="utf-8"))

    def test_timeout_kills_child_process_group_and_descendant(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            marker = root / "descendant-survived"
            descendant_code = (
                "import pathlib, sys, time; time.sleep(0.7); "
                "pathlib.Path(sys.argv[1]).write_text('survived', encoding='utf-8')"
            )
            parent_code = (
                "import subprocess, sys, time; "
                "subprocess.Popen([sys.executable, '-c', sys.argv[1], sys.argv[2]]); "
                "time.sleep(30)"
            )
            started = time.monotonic()
            with self.assertRaises(PROCESS.CommandTimedOut):
                self.run_command(
                    root,
                    "-c",
                    parent_code,
                    descendant_code,
                    str(marker),
                    timeout=0.25,
                )
            elapsed = time.monotonic() - started
            self.assertLess(elapsed, 3.0)
            # Give an incorrectly surviving descendant enough time to perform
            # its write, without ever touching a process outside this fixture.
            time.sleep(0.9)
            self.assertFalse(marker.exists())

    def test_exit_does_not_wait_for_grandchild_pipe_eof(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            marker = root / "grandchild-finished"
            descendant_code = (
                "import pathlib, sys, time; time.sleep(0.8); "
                "pathlib.Path(sys.argv[1]).write_text('finished', encoding='utf-8')"
            )
            parent_code = (
                "import subprocess, sys; "
                "subprocess.Popen([sys.executable, '-c', sys.argv[1], sys.argv[2]])"
            )
            started = time.monotonic()
            self.run_command(
                root,
                "-c",
                parent_code,
                descendant_code,
                str(marker),
                timeout=0.5,
            )
            self.assertLess(time.monotonic() - started, 0.7)
            time.sleep(1.0)
            self.assertTrue(marker.exists())

    def test_very_long_output_is_limited_to_two_mib_tail(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            output = self.run_command(
                root,
                "-c",
                (
                    "import sys; sys.stdout.write('x' * (2 * 1024 * 1024 + 123)); "
                    "sys.stdout.write('FINAL-TAIL'); sys.stdout.flush()"
                ),
            )
            encoded = output.encode("utf-8")
            log = (root / "command.log").read_bytes()
            self.assertLessEqual(len(encoded), PROCESS.MAX_TAIL_BYTES)
            self.assertLessEqual(len(log), PROCESS.MAX_TAIL_BYTES)
            self.assertTrue(output.endswith("FINAL-TAIL"))
            self.assertEqual(encoded, log)

    def test_missing_executable_raises_command_unavailable(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with self.assertRaises(PROCESS.CommandUnavailable):
                PROCESS.run_bounded_command(
                    [str(root / "does-not-exist")],
                    root,
                    self.command_environment(),
                    root / "missing.log",
                    1.0,
                    "missing command",
                )
            self.assertEqual(b"", (root / "missing.log").read_bytes())

    def test_argument_list_does_not_allow_shell_injection(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            marker = root / "shell-was-used"
            payload = f"literal; touch {marker}"
            output = self.run_command(
                root,
                "-c",
                "import sys; print(sys.argv[1])",
                payload,
            )
            self.assertIn(payload, output)
            self.assertFalse(marker.exists())


if __name__ == "__main__":
    unittest.main()
