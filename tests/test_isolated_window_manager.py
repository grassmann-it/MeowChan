"""Process-group ownership contract for the private Linux desktop helper."""
import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    'isolated_window_manager', ROOT / 'scripts/with-isolated-window-manager.py')
helper = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(helper)


class FakeManager:
    pid = 7311

    def __init__(self, running=True, waits=None):
        self.running = running
        self.waits = list(waits or [None])
        self.wait_calls = 0

    def poll(self):
        return None if self.running else 0

    def wait(self, timeout=None):
        self.wait_calls += 1
        outcome = self.waits.pop(0) if self.waits else None
        if isinstance(outcome, BaseException):
            raise outcome
        self.running = False
        return 0

    def terminate(self):
        self.running = False


class IsolatedWindowManagerTests(unittest.TestCase):
    def test_manager_cleanup_escalates_only_inside_its_owned_session(self):
        manager = FakeManager(waits=[subprocess.TimeoutExpired('openbox', 3), None])
        with mock.patch.object(helper.os, 'killpg') as killpg:
            helper.stop_owned_window_manager(manager)
        self.assertEqual([mock.call(manager.pid, helper.signal.SIGTERM),
                          mock.call(manager.pid, helper.signal.SIGKILL)], killpg.call_args_list)
        self.assertEqual(2, manager.wait_calls)

    def test_manager_cleanup_reaps_already_exited_manager_without_signalling_reused_pgid(self):
        manager = FakeManager(running=False)
        with mock.patch.object(helper.os, 'killpg') as killpg:
            helper.stop_owned_window_manager(manager)
        killpg.assert_not_called()
        self.assertEqual(1, manager.wait_calls)

    def test_main_starts_openbox_in_a_private_process_session(self):
        manager = FakeManager()
        with tempfile.TemporaryDirectory(prefix='isolated-wm-contract-') as tmp:
            with mock.patch.dict(helper.os.environ, {
                'MEOWCHAN_DESKTOP_SMOKE_XVFB': '1',
                'DISPLAY': ':991',
                'XDG_CONFIG_HOME': tmp,
            }, clear=False), mock.patch.object(helper.sys, 'argv', [
                str(ROOT / 'scripts/with-isolated-window-manager.py'), '--', sys.executable, '-c', 'pass']), \
                    mock.patch.object(helper.subprocess, 'Popen', return_value=manager) as popen, \
                    mock.patch.object(helper.subprocess, 'run', return_value=subprocess.CompletedProcess(
                        ['xprop'], 0, stdout='_NET_SUPPORTING_WM_CHECK(WINDOW): window id # 0x1\n', stderr='')), \
                    mock.patch.object(helper.subprocess, 'call', return_value=0), \
                    mock.patch.object(helper.os, 'killpg'):
                self.assertEqual(0, helper.main())
        self.assertTrue(popen.call_args.kwargs['start_new_session'])


if __name__ == '__main__':
    unittest.main()
