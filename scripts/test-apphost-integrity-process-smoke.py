#!/usr/bin/env python3
"""Narrow lifecycle classification tests; never launches a desktop or child process."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

_spec = importlib.util.spec_from_file_location('policy_process_runner', Path(__file__).with_name('apphost-integrity-process-smoke.py'))
runner = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(runner)


class GroupExitTests(unittest.TestCase):
    def setUp(self):
        self.supervisor = dict(pid=100, processGroupId=100, sessionId=100, startTimeTicks=200)

    def row(self, state='S', pid=101, started=201, session=100):
        return dict(pid=pid, state=state, processGroupId=100, sessionId=session, startTimeTicks=started,
                    terminal=state in runner.TERMINAL_PROCESS_STATES)

    def proc(self, root, pid, state='S', started=201, session=100, name='helper (with) spaces'):
        directory = root / str(pid)
        directory.mkdir(exist_ok=True)
        fields = [state, '1', '100', str(session)] + ['0'] * 15 + [str(started)]
        (directory / 'stat').write_text(str(pid) + ' (' + name + ') ' + ' '.join(fields) + '\n')

    def test_proc_inventory_preserves_birth_identity_and_zombie_state(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.proc(root, 101, state='Z')
            rows = runner.group_inventory(self.supervisor, {100: 200}, root)
            self.assertEqual(len(rows), 1)
            self.assertEqual(rows[0]['name'], 'helper (with) spaces')
            self.assertEqual(rows[0]['startTimeTicks'], 201)
            self.assertTrue(rows[0]['terminal'])
            self.assertIsNone(rows[0]['executable'])

    def test_reused_pid_or_changed_session_fails_before_signals(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for started, session in ((202, 100), (201, 999), (199, 100)):
                with self.subTest(started=started, session=session):
                    self.proc(root, 101, started=started, session=session)
                    with patch.object(runner.os, 'killpg') as signal_group:
                        with self.assertRaises(runner.ProcessIdentityError):
                            runner.group_inventory(self.supervisor, {100: 200, 101: 201}, root)
                        signal_group.assert_not_called()

    def test_zombies_are_terminated_and_not_signalled(self):
        result = {}
        with patch.object(runner, 'group_inventory', return_value=[self.row('Z')]), \
                patch.object(runner.os, 'killpg') as signal_group, patch.object(runner.time, 'sleep') as sleep, \
                patch.object(runner.time, 'monotonic', return_value=1):
            self.assertTrue(runner.observe_normal_group_exit(self.supervisor, 2, result, {100: 200}))
            self.assertEqual(result['processGroupExit']['disposition'], 'terminated-zombies-only')
            self.assertFalse(result['processGroupExit']['signalsSent'])
            signal_group.assert_not_called()
            sleep.assert_not_called()

    def test_live_helper_may_exit_normally_inside_original_deadline(self):
        result = {}
        with patch.object(runner, 'group_inventory', side_effect=[[self.row()], []]), \
                patch.object(runner.time, 'monotonic', return_value=1), patch.object(runner.time, 'sleep'), \
                patch.object(runner.os, 'killpg') as signal_group:
            self.assertTrue(runner.observe_normal_group_exit(self.supervisor, 2, result, {100: 200}))
            self.assertEqual(len(result['processGroupExit']['observations']), 2)
            self.assertEqual(result['processGroupExit']['disposition'], 'empty')
            signal_group.assert_not_called()

    def test_live_then_empty_observed_after_deadline_still_fails(self):
        result = {}
        with patch.object(runner, 'group_inventory', side_effect=[[self.row()], []]), \
                patch.object(runner.time, 'monotonic', side_effect=[119.99, 120.01]), \
                patch.object(runner.time, 'sleep'), patch.object(runner.os, 'killpg') as signal_group:
            self.assertFalse(runner.observe_normal_group_exit(self.supervisor, 120, result, {100: 200}))
            self.assertEqual(result['processGroupExit']['disposition'], 'completion-observed-after-original-deadline')
            self.assertEqual(result['processGroupExit']['finalMembers'], [])
            self.assertLess(result['processGroupExit']['finalObservedRemainingSeconds'], 0)
            signal_group.assert_not_called()

    def test_any_live_process_at_deadline_still_fails(self):
        result = {}
        with patch.object(runner, 'group_inventory', return_value=[self.row('D')]), \
                patch.object(runner.time, 'monotonic', return_value=2), patch.object(runner.time, 'sleep') as sleep:
            self.assertFalse(runner.observe_normal_group_exit(self.supervisor, 2, result, {100: 200}))
            self.assertEqual(result['processGroupExit']['disposition'], 'live-members-at-original-deadline')
            sleep.assert_not_called()

    def test_cleanup_does_not_signal_a_group_that_already_terminated(self):
        process, result = Mock(), {'processGroupExit': {'signalsSent': False}}
        with patch.object(runner, 'group_inventory', return_value=[self.row('Z')]), \
                patch.object(runner.os, 'killpg') as signal_group:
            runner.cleanup_bound_group(process, self.supervisor, {100: 200}, result)
            signal_group.assert_not_called()
            self.assertNotIn('forcedCleanup', result)

    def test_cleanup_rebinds_identity_before_escalation(self):
        process, result = Mock(), {'processGroupExit': {'signalsSent': False}}
        with patch.object(runner, 'group_inventory', side_effect=[[self.row()], runner.ProcessIdentityError('PID reused')]), \
                patch.object(runner.time, 'monotonic', side_effect=[0, 3]), \
                patch.object(runner.os, 'killpg') as signal_group:
            with self.assertRaises(runner.ProcessIdentityError):
                runner.cleanup_bound_group(process, self.supervisor, {100: 200}, result)
            signal_group.assert_called_once_with(100, runner.signal.SIGTERM)
            self.assertTrue(result['forcedCleanup'])
            self.assertTrue(result['processGroupExit']['signalsSent'])


if __name__ == '__main__':
    unittest.main()
