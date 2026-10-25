"""Fork snapshots must not inflate independently executed apphost PID evidence."""
import copy
import importlib.util
from pathlib import Path
import unittest
from unittest import mock

SPEC = importlib.util.spec_from_file_location('worker_gates',
    Path(__file__).resolve().parents[1] / 'scripts/linux-distribution-gates.py')
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)


class ForkSnapshotTests(unittest.TestCase):
    def fixture(self):
        parent = dict(pid=40, ppid=32, pgid=17, sid=17, uid=1001, starttime=84543, state='R')
        child = dict(parent, pid=68, ppid=40, starttime=84924)
        image = dict(path='/work/payload/MeowChan', device=1, inode=3, size=100, mtimeNs=1)
        observed = {40: dict(processIdentity=parent.copy(), finalExecutable=image.copy(), exeSha256='a'*64)}
        return parent, child, image, observed

    def test_inherited_child_is_never_stopped_or_counted_as_apphost(self):
        parent, child, image, observed = self.fixture()
        before = copy.deepcopy(observed)
        attempts = []
        with (mock.patch.object(runner, 'descendants', return_value={68}),
              mock.patch.object(runner.os, 'open', side_effect=[1, 2]), mock.patch.object(runner.os, 'close'),
              mock.patch.object(runner, 'proc_stat', side_effect=[child, parent]),
              mock.patch.object(runner, 'proc_executable', return_value=image),
              mock.patch.object(runner.signal, 'pidfd_send_signal') as signals,
              mock.patch.object(runner, 'verify_payload') as verify):
            runner.observe_apphosts(17, observed, {}, 'linux-arm64', Path('/work'), attempts=attempts, deadline=10)
        self.assertEqual(observed, before)
        self.assertEqual(attempts[0]['classification'], 'inherited-child-before-exec')
        self.assertFalse(attempts[0]['authenticatedAsApphost'])
        signals.assert_not_called()
        verify.assert_not_called()

    def test_reused_dead_or_changed_parent_cannot_exempt_a_child(self):
        for field in ('starttime', 'uid', 'sid', 'image', 'dead'):
            parent, child, image, observed = self.fixture()
            current_image = image.copy()
            if field == 'image': current_image['inode'] += 1
            elif field == 'dead': parent['state'] = 'Z'
            else: parent[field] += 1
            with (self.subTest(field=field), mock.patch.object(runner, 'descendants', return_value={68}),
                  mock.patch.object(runner.os, 'open', side_effect=[1, 2]), mock.patch.object(runner.os, 'close'),
                  mock.patch.object(runner, 'proc_stat', side_effect=[child, parent, child]),
                  mock.patch.object(runner, 'proc_executable', side_effect=[image, current_image, image]),
                  self.assertRaisesRegex(ValueError, 'authenticated apphost parent')):
                runner.observe_apphosts(17, observed, {}, 'linux-arm64', Path('/work'), attempts=[], deadline=10)

    def test_authenticated_main_image_change_or_identity_reuse_stays_fatal(self):
        for change in ('image', 'identity'):
            parent, child, image, observed = self.fixture()
            if change == 'image': image['path'] = '/work/payload/plugin-worker/MeowChan.PluginWorker'
            else: parent['starttime'] += 1
            with (self.subTest(change=change), mock.patch.object(runner, 'descendants', return_value={40}),
                  mock.patch.object(runner.os, 'open', return_value=1), mock.patch.object(runner.os, 'close'),
                  mock.patch.object(runner, 'proc_stat', return_value=parent),
                  mock.patch.object(runner, 'proc_executable', return_value=image),
                  mock.patch.object(runner.signal, 'pidfd_send_signal') as signals,
                  self.assertRaisesRegex(ValueError, 'authenticated apphost')):
                runner.observe_apphosts(17, observed, {}, 'linux-arm64', Path('/work'), attempts=[], deadline=10)
            signals.assert_not_called()


if __name__ == '__main__':
    unittest.main()
