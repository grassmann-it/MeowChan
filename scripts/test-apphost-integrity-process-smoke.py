#!/usr/bin/env python3
"""Narrow lifecycle classification tests; never launches a desktop or child process."""
import importlib.util
import copy
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

_spec = importlib.util.spec_from_file_location('policy_process_runner', Path(__file__).with_name('apphost-integrity-process-smoke.py'))
runner = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(runner)


class BuildClosureTests(unittest.TestCase):
    def test_direct_build_retains_network_and_readonly_guards_but_allows_installed_runtime(self):
        archive = ['bwrap', '--unshare-net', '--ro-bind', '/usr', '/usr', '--tmpfs', '/usr/share/dotnet',
                   '--setenv', 'PATH', '/no-sdk', '--setenv', 'DOTNET_ROOT', '/no-sdk', '/owned/MeowChan', '--smoke-plugin-workflow']
        with patch.object(runner.plugin, 'sandbox', return_value=archive):
            command = runner.build_sandbox(None, None, None)
        self.assertEqual(command, ['bwrap', '--unshare-net', '--ro-bind', '/usr', '/usr',
            '--setenv', 'PATH', '/no-sdk', '/owned/MeowChan', '--smoke-plugin-workflow'])
        self.assertIn('--tmpfs', archive)  # The established archive sandbox is not mutated.

    def seed(self, root):
        for name in ('MeowChan', 'MeowChan.dll', 'MeowChan.deps.json', 'MeowChan.runtimeconfig.json', 'libmeowchan.dll'):
            (root / name).write_bytes(('fixture-' + name).encode())
        return root / 'MeowChan'

    def test_exact_build_tree_detects_added_removed_and_modified_payload(self):
        for fault in ('added', 'removed', 'changed'):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                apphost = self.seed(root)
                closure = runner.build_closure(root, apphost)
                self.assertEqual(closure['binding'], 'frozen-build-directory')
                self.assertEqual(closure['fileCount'], 5)
                self.assertEqual(runner.workflow.verify_extracted_closure(root, closure), closure['contentSha256'])
                if fault == 'added': (root / 'foreign.dll').write_bytes(b'foreign')
                if fault == 'removed': (root / 'MeowChan.dll').unlink()
                if fault == 'changed': (root / 'MeowChan.dll').write_bytes(b'changed')
                with self.assertRaises(ValueError):
                    runner.workflow.verify_extracted_closure(root, closure)

    def test_no_archive_mode_still_rejects_escape_links_and_missing_runtime_payload(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            apphost = self.seed(root)
            (root / 'escape').symlink_to('../outside')
            with self.assertRaises(ValueError): runner.build_closure(root, apphost)
            (root / 'escape').unlink()
            (root / 'MeowChan.runtimeconfig.json').unlink()
            with self.assertRaisesRegex(ValueError, '^Archive contains a broken or cyclic link$') as failure:
                runner.build_closure(root, apphost)
            self.assertIsInstance(failure.exception.__cause__, FileNotFoundError)
            self.assertEqual(failure.exception.__cause__.filename, str(root / 'MeowChan.runtimeconfig.json'))


class ActivationEvidenceTests(unittest.TestCase):
    def policy(self, active, granted, revision):
        return dict(IsActive=active, IsGranted=granted, Snapshot=dict(
            ProfileId='/owned/profile', PluginOwnerId=runner.OWNER, SourceId=runner.SOURCE,
            AllowProviderSizeMd5Mismatch=active and granted, Revision=revision))

    def acquired(self):
        before, after = self.policy(False, False, 0), self.policy(True, True, 2)
        return dict(initialPolicy=before, activationEvidence=dict(before=before, after=after,
                    status='Succeeded', requestsBeforeAdmission=0, watchesBeforeAdmission=0), urlAdmissionEvidence=None)

    def test_default_off_combined_consent_and_fresh_revision_are_required(self):
        good = self.acquired()
        runner.validate_activation_metrics(good, 'acquire', Path('/owned/profile'))
        for fault in ('missing', 'http', 'watch', 'old-revision', 'wrong-owner', 'not-granted', 'was-active'):
            bad = copy.deepcopy(good)
            activation = bad['activationEvidence']
            if fault == 'missing': bad['activationEvidence'] = None
            if fault == 'http': activation['requestsBeforeAdmission'] = 1
            if fault == 'watch': activation['watchesBeforeAdmission'] = 1
            if fault == 'old-revision': activation['after']['Snapshot']['Revision'] = 1
            if fault == 'wrong-owner': activation['after']['Snapshot']['PluginOwnerId'] = 'other-owner'
            if fault == 'not-granted': activation['after']['IsGranted'] = False
            if fault == 'was-active': activation['before']['IsActive'] = True
            with self.subTest(fault=fault), self.assertRaises(ValueError):
                runner.validate_activation_metrics(bad, 'acquire', Path('/owned/profile'))

    def test_later_url_must_preserve_exact_revoked_policy_and_zero_http(self):
        policy = self.policy(True, False, 6)
        good = dict(finalPolicy=policy, activationEvidence=None, urlAdmissionEvidence=dict(
            url='https://wizchan.org/demo/res/124.html', before=policy, after=policy, watches=2, requests=0))
        runner.validate_activation_metrics(good, 'deny', Path('/owned/profile'))
        for fault in ('regrant', 'revision', 'http', 'watch', 'activation-on-restart'):
            bad = copy.deepcopy(good)
            # The runner receives separate objects after JSON decoding.
            bad['urlAdmissionEvidence']['after'] = copy.deepcopy(policy)
            if fault == 'regrant': bad['urlAdmissionEvidence']['after']['IsGranted'] = True
            if fault == 'revision': bad['urlAdmissionEvidence']['after']['Snapshot']['Revision'] += 1
            if fault == 'http': bad['urlAdmissionEvidence']['requests'] = 1
            if fault == 'watch': bad['urlAdmissionEvidence']['watches'] = 1
            if fault == 'activation-on-restart': bad['activationEvidence'] = self.acquired()['activationEvidence']
            with self.subTest(fault=fault), self.assertRaises(ValueError):
                runner.validate_activation_metrics(bad, 'deny', Path('/owned/profile'))


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
