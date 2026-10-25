"""Pure snapshots and mocked observer only; no process, signals, Docker or product launches.

Prepared from Candidate03 dispatch a9928e3f14cbdfd58e03ec27850c760df5a62229e5332146d792e58243efac43.
These checks are not Linux distribution acceptance proof.
"""
import copy
import hashlib
import importlib.util
import io
from pathlib import Path
import signal
from types import SimpleNamespace
import unittest
from unittest import mock

SPEC = importlib.util.spec_from_file_location(
    'linux_distribution_worker_gates', Path(__file__).resolve().parents[1] / 'scripts/linux-distribution-gates.py')
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)

WORK = Path('/owned')
ROOT = WORK / 'tmp/appimage'
MEMBER = 'usr/lib/meowchan/MeowChan'
WORKER = 'usr/lib/meowchan/plugin-worker/MeowChan.PluginWorker'
WORKER_BYTES = b'known worker descriptor fixture'
WORKER_HASH = hashlib.sha256(WORKER_BYTES).hexdigest()
APP_HASH = 'b' * 64


def snapshots():
    parent_identity = dict(pid=1238, starttime=5136882, ppid=1237, pgid=1214, sid=1214, uid=1000, state='S')
    child = dict(pid=1292, starttime=5137247, ppid=1238, pgid=1214, sid=1214, uid=1000, state='D')
    app_exe = dict(path=str(ROOT / MEMBER), device=50, inode=43797050, size=97516384, mtimeNs=1)
    worker_exe = dict(path=str(ROOT / WORKER), device=50, inode=43797935, size=len(WORKER_BYTES), mtimeNs=2)
    parent = dict(pid=1238, processIdentity=parent_identity, finalExecutable=app_exe,
                  payloadRoot=str(ROOT), exeSha256=APP_HASH)
    attempt = dict(pid=1292, initialIdentity=child.copy(), identityBeforeSignal=child.copy(),
                   stopIdentity=dict(child, state='T'), stopConfirmed=True, stopTaskStates={'1292': 'T'},
                   initialExecutable=app_exe.copy(), executableBeforeSignal=app_exe.copy(),
                   stoppedExecutable=worker_exe, expectedHash=APP_HASH, actualHash=WORKER_HASH)
    artifact = dict(apphost_member=MEMBER, payload_files={MEMBER: APP_HASH, WORKER: WORKER_HASH})
    return attempt, {1238: parent}, artifact, parent_identity.copy(), app_exe.copy()


class WorkerBindingTests(unittest.TestCase):
    def binding(self, values):
        return runner.packaged_worker_binding(*values, WORK, None)

    def test_child_exec_to_exact_worker_is_classified_without_mutating_observed(self):
        values = snapshots()
        before = copy.deepcopy(values)
        parent, root, member, digest = self.binding(values)
        self.assertEqual((parent, root, member, digest), (values[1][1238], ROOT, WORKER, WORKER_HASH))
        self.assertEqual(values, before)

    def test_wrong_hash_path_unlisted_or_other_helper_are_rejected(self):
        for mutation in ('hash', 'path', 'unlisted', 'compiler'):
            values = snapshots()
            attempt, _, artifact, _, _ = values
            if mutation == 'hash':
                attempt['actualHash'] = 'f' * 64
            elif mutation == 'path':
                attempt['stoppedExecutable']['path'] = '/other/' + WORKER
            elif mutation == 'unlisted':
                del artifact['payload_files'][WORKER]
            else:
                compiler = 'usr/lib/meowchan/plugin-compiler/MeowChan.PluginCompiler'
                artifact['payload_files'][compiler] = WORKER_HASH
                attempt['stoppedExecutable']['path'] = str(ROOT / compiler)
            with self.subTest(mutation=mutation), self.assertRaisesRegex(ValueError, 'exact manifest-bound'):
                self.binding(values)

    def test_wrong_missing_or_changed_direct_parent_is_rejected(self):
        for mutation in ('missing', 'different_authenticated', 'before_ppid', 'stop_ppid'):
            values = snapshots()
            attempt, observed, _, _, _ = values
            if mutation == 'missing':
                observed.clear()
            elif mutation == 'different_authenticated':
                observed[1238] = copy.deepcopy(observed[1238])
                observed[1238]['processIdentity']['pid'] = 999
            elif mutation == 'before_ppid':
                attempt['identityBeforeSignal']['ppid'] = 999
            else:
                attempt['stopIdentity']['ppid'] = 999
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                self.binding(values)

    def test_parent_reuse_or_image_change_is_rejected(self):
        for field in ('pid', 'starttime', 'pgid', 'sid', 'uid', 'image', 'zombie'):
            values = snapshots()
            if field == 'image':
                values[4]['inode'] += 1
            elif field == 'zombie':
                values[3]['state'] = 'Z'
            else:
                values[3][field] += 1
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, 'parent lost'):
                self.binding(values)

    def test_child_reused_identity_or_non_inherited_image_is_rejected(self):
        for field in ('pid', 'starttime', 'pgid', 'sid', 'uid', 'initial_exe', 'before_exe'):
            values = snapshots()
            if field.endswith('_exe'):
                key = 'initialExecutable' if field == 'initial_exe' else 'executableBeforeSignal'
                values[0][key]['inode'] += 1
            else:
                values[0]['stopIdentity'][field] += 1
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.binding(values)

    def test_previously_authenticated_apphost_can_never_be_a_worker(self):
        values = snapshots()
        values[1][1292] = copy.deepcopy(values[1][1238])
        with self.assertRaisesRegex(ValueError, 'never be reclassified'):
            self.binding(values)

    def test_unconfirmed_or_partial_stop_is_rejected(self):
        for field in ('confirmed', 'process', 'tasks', 'empty_tasks'):
            values = snapshots()
            if field == 'confirmed':
                values[0]['stopConfirmed'] = False
            elif field == 'process':
                values[0]['stopIdentity']['state'] = 'R'
            elif field == 'tasks':
                values[0]['stopTaskStates']['1300'] = 'R'
            else:
                values[0]['stopTaskStates'] = {}
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, 'confirmed stop'):
                self.binding(values)

    def test_fixed_payload_root_or_owned_tmp_escape_is_rejected(self):
        values = snapshots()
        with self.assertRaisesRegex(ValueError, 'work root'):
            runner.packaged_worker_binding(*values, WORK, Path('/owned/other'))
        values[1][1238]['payloadRoot'] = '/foreign'
        with self.assertRaisesRegex(ValueError, 'work root'):
            self.binding(values)


class MockedObserverTests(unittest.TestCase):
    def test_existing_apphost_image_change_or_pid_reuse_remains_fatal_before_signals(self):
        for mutation in ('image', 'reuse'):
            attempt, observed, artifact, _, _ = snapshots()
            identity = copy.deepcopy(observed[1238]['processIdentity'])
            executable = copy.deepcopy(observed[1238]['finalExecutable'])
            if mutation == 'image':
                executable['path'] = str(ROOT / WORKER)
            else:
                identity['starttime'] += 1
            with (self.subTest(mutation=mutation), mock.patch.object(runner, 'descendants', return_value={1238}),
                  mock.patch.object(runner.os, 'open', return_value=1), mock.patch.object(runner.os, 'close'),
                  mock.patch.object(runner, 'proc_stat', return_value=identity),
                  mock.patch.object(runner, 'proc_executable', return_value=executable),
                  mock.patch.object(runner, 'packaged_worker_binding') as classify,
                  mock.patch.object(runner.signal, 'pidfd_send_signal') as send_signal):
                attempts = []
                with self.assertRaisesRegex(ValueError, 'authenticated apphost'):
                    runner.observe_apphosts(100, observed, artifact, 'linux-x64', WORK,
                                            attempts=attempts, deadline=10)
                classify.assert_not_called()
                send_signal.assert_not_called()
                self.assertFalse(attempts[0]['passed'])

    def test_stopped_child_is_payload_verified_resumed_and_never_counted_as_apphost(self):
        fixture, observed, artifact, parent_identity, parent_exe = snapshots()
        attempts = []
        child_identities = iter([fixture['initialIdentity'], fixture['identityBeforeSignal'],
                                 fixture['stopIdentity'], fixture['stopIdentity']])
        child_executables = iter([fixture['initialExecutable'], fixture['executableBeforeSignal'],
                                  fixture['stoppedExecutable'], fixture['stoppedExecutable']])

        def open_descriptor(path, flags, *, dir_fd=None):
            return {'/proc/1292': 1 if dir_fd is None else 3,
                    '/proc/1238': 5, 'exe': 4}[path]

        # Initial /proc candidate and current numeric validation need separate
        # descriptors, but no actual descriptor or process exists in this test.
        opened_child = 0

        def opened(path, flags, *, dir_fd=None):
            nonlocal opened_child
            if path == '/proc/1292':
                opened_child += 1
                return 1 if opened_child == 1 else 3
            return open_descriptor(path, flags, dir_fd=dir_fd)

        def identity(fd):
            return (next(child_identities) if fd == 1 else
                    fixture['identityBeforeSignal'] if fd == 3 else parent_identity.copy())

        def executable(fd):
            return next(child_executables) if fd == 1 else parent_exe.copy()

        details = fixture['stoppedExecutable']
        descriptor_stat = SimpleNamespace(st_dev=details['device'], st_ino=details['inode'],
                                          st_size=details['size'], st_mtime_ns=details['mtimeNs'])
        proof = dict(sha256='c' * 64, bytes=100, apphostSha256=APP_HASH, elfMachine=62)
        with (mock.patch.object(runner, 'descendants', return_value={1292}),
              mock.patch.object(runner.os, 'open', side_effect=opened), mock.patch.object(runner.os, 'close'),
              mock.patch.object(runner.os, 'getuid', return_value=1000),
              mock.patch.object(runner.os, 'pidfd_open', return_value=2),
              mock.patch.object(runner.os, 'fstat', return_value=descriptor_stat),
              mock.patch.object(runner.os, 'dup', return_value=6),
              mock.patch.object(runner.os, 'fdopen', return_value=io.BytesIO(WORKER_BYTES)),
              mock.patch.object(runner, 'proc_stat', side_effect=identity),
              mock.patch.object(runner, 'proc_executable', side_effect=executable),
              mock.patch.object(runner, 'proc_tasks', return_value={'1292': 'T'}),
              mock.patch.object(runner.time, 'monotonic', return_value=1),
              mock.patch.object(runner, 'verify_payload', return_value=proof) as verify,
              mock.patch.object(runner.signal, 'pidfd_send_signal') as send_signal):
            runner.observe_apphosts(100, observed, artifact, 'linux-x64', WORK,
                                    attempts=attempts, deadline=10)
            verify.assert_called_once_with(ROOT, artifact, 'linux-x64')
            self.assertEqual(send_signal.call_args_list, [mock.call(2, signal.SIGSTOP), mock.call(2, signal.SIGCONT)])
        self.assertEqual(set(observed), {1238})
        self.assertEqual(len(attempts), 1)
        self.assertTrue(attempts[0]['passed'])
        self.assertFalse(attempts[0]['authenticatedAsApphost'])
        self.assertEqual(attempts[0]['classification'], 'packaged-plugin-worker')
        self.assertEqual(attempts[0]['expectedHash'], APP_HASH)
        self.assertEqual(attempts[0]['actualHash'], WORKER_HASH)
        self.assertEqual(attempts[0]['parentIdentityBeforePayload'], attempts[0]['parentIdentityAfterPayload'])
        self.assertTrue(attempts[0]['resumeSignalSent'])


if __name__ == '__main__':
    unittest.main()
