"""Container orphan-reaping regression; optional real Docker proof uses an existing local image only."""
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('distribution_init_runner', ROOT / 'scripts/linux-distribution-gates.py')
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)

# Use the actual cleanup function, not a substitute which ignores zombies or weakens deadlines.
PROBE = r'''
import importlib.util, subprocess, sys, time
spec = importlib.util.spec_from_file_location('smoke', '/repo/scripts/run-desktop-smoke.py')
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)
child = subprocess.Popen([sys.executable, '-c',
    'import os,time; pid=os.fork(); time.sleep(0.1) if pid==0 else None; os._exit(0)'], start_new_session=True)
child.wait(timeout=5)
time.sleep(0.3)
try:
    smoke.stop_owned_processes(child)
except subprocess.TimeoutExpired as error:
    print('UNREAPED_GROUP: ' + str(error), flush=True)
    sys.exit(23)
print('OWNED_GROUP_REAPED', flush=True)
'''


def command(directory, image):
    args = SimpleNamespace(package_dir=directory, manifest=directory / 'manifest.json',
                           source_commit='a' * 40, runtime='linux-x64', scenario='bundled-vlc')
    owner = uuid.uuid4().hex
    return runner.docker_command(args, image, 'meowchan-init-test-' + owner, owner,
                                 directory, directory, ROOT / 'scripts', directory)


class DistributionInitContracts(unittest.TestCase):
    def test_all_scenarios_use_reaper_without_relaxing_isolation(self):
        cmd = command(Path('/owned/test'), 'sha256:' + 'b' * 64)
        self.assertEqual(cmd.count('--init'), 1)
        self.assertLess(cmd.index('--init'), cmd.index('sha256:' + 'b' * 64))
        for flag in ('--pull=never', '--network=none', '--read-only', '--cap-drop=ALL',
                     '--security-opt=no-new-privileges', '--pids-limit=256'):
            self.assertIn(flag, cmd)
        self.assertEqual(cmd[cmd.index('--user') + 1], f'{os.getuid()}:{os.getgid()}')

    @unittest.skipUnless(os.environ.get('MEOWCHAN_TEST_LOCAL_INIT_IMAGE'), 'requires explicit existing local Docker image')
    def test_actual_cleanup_rejects_unreaped_orphan_and_passes_with_init(self):
        image = os.environ['MEOWCHAN_TEST_LOCAL_INIT_IMAGE']
        image_id = subprocess.check_output(['docker', 'image', 'inspect', image, '--format', '{{.Id}}'],
                                           text=True, timeout=15).strip()
        with tempfile.TemporaryDirectory(prefix='meowchan-init-test-') as tmp:
            for use_init in (False, True):
                with self.subTest(init=use_init):
                    cmd = command(Path(tmp), image_id)
                    if not use_init:
                        cmd.remove('--init')
                    cmd = cmd[:cmd.index(image_id)] + ['--entrypoint', 'python3', image_id, '-B', '-c', PROBE]
                    name = cmd[cmd.index('--name') + 1]
                    try:
                        result = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
                        self.assertEqual(result.returncode, 0 if use_init else 23, result.stdout + result.stderr)
                        self.assertIn('OWNED_GROUP_REAPED' if use_init else 'UNREAPED_GROUP:', result.stdout)
                    finally:
                        # Only this test's unique container; --pull=never means no image/build side effects.
                        runner.remove_owned_container(name, cmd[cmd.index('--label') + 1].split('=', 1)[1])


if __name__ == '__main__':
    unittest.main()
