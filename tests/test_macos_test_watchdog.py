"""Bounded watchdog fixtures; these never invoke dotnet or a desktop."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[1] / 'scripts/macos-test-watchdog.py'
SPEC = importlib.util.spec_from_file_location('macos_test_watchdog', SCRIPT)
watchdog = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(watchdog)


@unittest.skipUnless(os.name == 'posix', 'macOS watchdog uses POSIX process groups')
class WatchdogTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='meowchan-watchdog-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def test_real_timeout_cleanup_and_later_success_remain_distinct(self):
        failed = watchdog.run_one('failed', [sys.executable, '-c', 'raise SystemExit(7)'],
                                  self.root / 'failed', 2)
        timed_out = watchdog.run_one('timeout', [sys.executable, '-c', 'import time; time.sleep(60)'],
                                     self.root / 'timeout', 0.1)
        success = watchdog.run_one('success', [sys.executable, '-c', 'print("continued")'],
                                   self.root / 'success', 2)
        self.assertEqual(7, failed['exit_code'])
        self.assertFalse(failed['timeout'])
        self.assertTrue(timed_out['timeout'])
        self.assertTrue(timed_out['cleanup']['complete'])
        self.assertFalse(watchdog.live_processes(timed_out['pid']))
        self.assertEqual(0, success['exit_code'])
        self.assertTrue(success['cleanup']['complete'])
        self.assertEqual(timed_out, json.loads((self.root / 'timeout/result.json').read_text()))

    def test_denied_group_signal_is_visible_but_empty_reaped_group_is_complete(self):
        child = SimpleNamespace(pid=987654, returncode=0, wait=mock.Mock(return_value=0))
        with mock.patch.object(watchdog.os, 'getpgrp', return_value=321), \
                mock.patch.object(watchdog.os, 'killpg', side_effect=PermissionError(1, 'Operation not permitted')) as kill, \
                mock.patch.object(watchdog, 'live_processes', return_value=[]):
            result = watchdog.cleanup_group(child)
        self.assertTrue(result['complete'])
        self.assertEqual(2, len(result['errors']))
        self.assertIn('PermissionError', result['errors'][0]['error'])
        self.assertEqual([mock.call(child.pid, signal.SIGTERM), mock.call(child.pid, signal.SIGKILL)], kill.call_args_list)

    def test_denied_signals_with_live_members_fail_cleanup_without_targeting_other_pids(self):
        child = SimpleNamespace(pid=987654, returncode=0, wait=mock.Mock(return_value=0))
        row = (987655, '987655 1 987654 S 00:04 fixture', 'fixture')
        with mock.patch.object(watchdog.os, 'getpgrp', return_value=321), \
                mock.patch.object(watchdog.os, 'killpg', side_effect=PermissionError(1, 'Operation not permitted')), \
                mock.patch.object(watchdog.os, 'kill') as kill_pid, \
                mock.patch.object(watchdog, 'live_processes', return_value=[row]), \
                mock.patch.object(watchdog.time, 'monotonic', side_effect=[0, 4]):
            result = watchdog.cleanup_group(child)
        self.assertFalse(result['complete'])
        self.assertEqual([row[1]], result['remaining'])
        kill_pid.assert_not_called()

    def test_unverified_group_cannot_be_reported_as_cleaned(self):
        child = SimpleNamespace(pid=987654, returncode=0, wait=mock.Mock(return_value=0))
        with mock.patch.object(watchdog, 'kill_group'), \
                mock.patch.object(watchdog, 'live_processes', side_effect=subprocess.TimeoutExpired('ps', 5)):
            result = watchdog.cleanup_group(child)
        self.assertFalse(result['complete'])
        self.assertEqual('verify-process-group', result['errors'][-1]['operation'])

    def test_failed_process_listing_cannot_be_mistaken_for_an_empty_group(self):
        tools = self.root / 'bin'
        tools.mkdir()
        fake_ps = tools / 'ps'
        fake_ps.write_text('#!' + sys.executable + '\nraise SystemExit(1)\n')
        fake_ps.chmod(0o700)
        with mock.patch.dict(os.environ, {'PATH': str(tools)}):
            with self.assertRaises(subprocess.CalledProcessError):
                watchdog.processes(987654)

    def test_deadline_remains_failed_even_if_child_finishes_during_diagnostics(self):
        child = SimpleNamespace(pid=987654, returncode=0,
                                wait=mock.Mock(side_effect=subprocess.TimeoutExpired('fixture', 300)))
        cleanup = dict(complete=True, errors=[dict(signal='SIGTERM', error='PermissionError')], remaining=[])
        with mock.patch.object(watchdog.subprocess, 'Popen', return_value=child), \
                mock.patch.object(watchdog, 'capture_timeout') as capture, \
                mock.patch.object(watchdog, 'cleanup_group', return_value=cleanup):
            result = watchdog.run_one('deadline', ['fixture'], self.root / 'deadline', 300)
        self.assertTrue(result['timeout'])
        self.assertEqual(0, result['exit_code'])
        self.assertEqual(cleanup, result['cleanup'])
        capture.assert_called_once_with(child.pid, self.root / 'deadline')

    def test_main_rejects_incomplete_cleanup_and_continues_the_other_suite(self):
        calls = []

        def run(name, command, output, timeout):
            calls.append((name, timeout))
            return dict(name=name, exit_code=0, timeout=False, cleanup=dict(complete=name != 'core'))

        with mock.patch.object(sys, 'argv', [str(SCRIPT), '--output', str(self.root)]), \
                mock.patch.object(watchdog, 'run_one', side_effect=run), contextlib.redirect_stdout(io.StringIO()):
            result = watchdog.main()
        self.assertEqual(1, result)
        self.assertEqual([('core', 300), ('desktop', 300)], calls)
        self.assertEqual(2, len(json.loads((self.root / 'summary.json').read_text())))

    def test_live_group_check_ignores_only_defunct_members(self):
        zombie = (11, '11 1 10 Z 00:01 dead', 'dead')
        live = (12, '12 1 10 S 00:01 fixture', 'fixture')
        with mock.patch.object(watchdog, 'processes', return_value=[zombie, live]):
            self.assertEqual([live], watchdog.live_processes(10))

    def test_desktop_budget_does_not_extend_core_or_accept_a_timeout_as_success(self):
        calls = []

        def run(name, command, output, timeout):
            calls.append((name, timeout))
            return dict(name=name, exit_code=0, timeout=name == 'desktop', cleanup=dict(complete=True))

        with mock.patch.object(sys, 'argv', [str(SCRIPT), '--output', str(self.root), '--desktop-timeout', '600']), \
                mock.patch.object(watchdog, 'run_one', side_effect=run), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(1, watchdog.main())
        self.assertEqual([('core', 300), ('desktop', 600)], calls)

    def test_desktop_budget_cannot_be_disabled_or_unbounded(self):
        for value in ('0', '601'):
            with self.subTest(value=value), \
                    mock.patch.object(sys, 'argv', [str(SCRIPT), '--desktop-timeout', value]), \
                    contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
                watchdog.main()
            self.assertEqual(2, error.exception.code)

    def test_watchdog_and_system_groups_are_never_signalled(self):
        with mock.patch.object(watchdog.os, 'getpgrp', return_value=321), \
                mock.patch.object(watchdog.os, 'killpg') as kill:
            for group in (-1, 0, 1, 321):
                with self.subTest(group=group), self.assertRaises(ValueError):
                    watchdog.kill_group(group, signal.SIGTERM)
        kill.assert_not_called()


if __name__ == '__main__':
    unittest.main()
