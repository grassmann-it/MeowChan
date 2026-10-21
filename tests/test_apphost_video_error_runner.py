"""Video-error runner contracts/safety using tiny fake processes, not product proof."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path, PurePosixPath
import struct
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock
import zipfile
import zlib


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('video_error_runner', ROOT / 'scripts/apphost-video-error-smoke.py')
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)
KIND = 'SystemBackendUnavailable'


def metrics(kind=KIND, root=Path('/owned')):
    return dict(schemaVersion=1, completed=True, failure=None, expectedKind=kind, observedKind=kind,
                checks=sorted(runner.CHECKS), processId=123, workingDirectory=str(root), profile=str(root / 'profile'),
                assemblyInformationalVersion='fixture-only', renderScaling=1, message='Install the supported system VLC package.',
                windowVisible=True, profileAbsent=True, normalExitButtonClicked=True, exitCode=2)


def png_bytes():
    def chunk(kind, payload):
        return struct.pack('>I', len(payload)) + kind + payload + struct.pack('>I', zlib.crc32(kind + payload))
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', 1, 1, 8, 2, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(b'\x00\x00\x00\x00')) + chunk(b'IEND', b''))


def fake_apphost(base, behavior='pass'):
    path = base / 'fake-apphost'
    path.write_text('#!' + sys.executable + '\n' + f'behavior = {behavior!r}\nchecks = {sorted(runner.CHECKS)!r}\npng = {png_bytes()!r}\n' + '''
import json, os, sys, time
from pathlib import Path
assert sys.argv[1] == '--smoke-video-error'
root = Path.cwd()
profile = Path(os.environ['MEOWCHAN_PROFILE'])
assert profile == root / 'profile' and not profile.exists()
assert 'OCHAN_PROFILE' not in os.environ
for key in ('DISPLAY', 'WAYLAND_DISPLAY', 'DBUS_SESSION_BUS_ADDRESS'):
    assert key not in os.environ
assert os.environ['AVALONIA_GLOBAL_SCALE_FACTOR'] == '1'
assert Path(os.environ['XDG_RUNTIME_DIR']).parent == root
m = dict(schemaVersion=1, completed=True, failure=None, expectedKind=sys.argv[2], observedKind=sys.argv[2],
         checks=checks, processId=os.getpid(), workingDirectory=str(root), profile=str(profile),
         assemblyInformationalVersion='fake-runner-fixture', renderScaling=1,
         message='Install the supported system VLC package.', windowVisible=True, profileAbsent=True,
         normalExitButtonClicked=True, exitCode=2)
if behavior == 'wrong-kind':
    m['observedKind'] = 'BundleDamaged'
if behavior == 'session-check-missing':
    m['checks'].remove('session-not-created')
if behavior == 'profile-write':
    profile.mkdir()
if behavior == 'lock-write':
    Path(str(profile) + '.lock').write_text('unexpected session lock')
Path(str(profile) + '.video-error.json').write_text(json.dumps(m))
picture = Path(str(profile) + '.video-error.png')
if behavior == 'evidence-link':
    other = root / 'other.png'
    other.write_bytes(png)
    picture.symlink_to(other)
else:
    picture.write_bytes(png)
print(json.dumps(dict(profile=str(profile),pid=os.getpid())),flush=True)
if behavior == 'timeout':
    time.sleep(30)
if behavior == 'output-flood':
    print('X' * 2048,flush=True)
if behavior == 'misleading-marker':
    print('prefix APPHOST VIDEO ERROR PASS suffix',flush=True)
elif behavior != 'no-marker':
    print('APPHOST VIDEO ERROR PASS',flush=True)
sys.exit(0 if behavior == 'exit-zero' else 2)
''', encoding='utf-8')
    path.chmod(0o755)
    return path


def direct_fake_command(apphost, kind, root):
    return [str(apphost), '--smoke-video-error', kind]


class VideoErrorContractTests(unittest.TestCase):
    def test_all_actual_error_kinds_and_required_ui_session_checks(self):
        root = Path('/owned')
        for kind in runner.KINDS:
            runner.validate_metrics(metrics(kind), kind, root, root / 'profile')
        for check in runner.CHECKS:
            bad = metrics()
            bad['checks'].remove(check)
            with self.subTest(check=check), self.assertRaisesRegex(ValueError, 'omitted'):
                runner.validate_metrics(bad, KIND, root, root / 'profile')
        for field, value in [('completed', False), ('failure', 'failure'), ('schemaVersion', True),
                             ('expectedKind', 'BundleDamaged'), ('observedKind', 'BundleDamaged'),
                             ('checks', 'preflight-real'), ('checks', [None]), ('processId', True),
                             ('windowVisible', False), ('profileAbsent', False), ('normalExitButtonClicked', False),
                             ('exitCode', 0), ('exitCode', True), ('message', ''), ('assemblyInformationalVersion', ''),
                             ('renderScaling', 2), ('renderScaling', float('nan')), ('renderScaling', True),
                             ('workingDirectory', str(root / 'other')), ('profile', str(root / 'other'))]:
            bad = metrics()
            bad[field] = value
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                runner.validate_metrics(bad, KIND, root, root / 'profile')

    def test_runtime_is_observed_from_the_actual_elf_header(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'apphost'
            for machine, expected in ((62, 'linux-x64'), (183, 'linux-arm64')):
                header = bytearray(20)
                header[:6] = b'\x7fELF\x02\x01'
                struct.pack_into('<H', header, 18, machine)
                path.write_bytes(header)
                self.assertEqual(runner.inspect_runtime(path), expected)
            path.write_bytes(b'not an ELF apphost')
            with self.assertRaises(ValueError):
                runner.inspect_runtime(path)

    def test_cli_has_no_personal_desktop_fallback_and_bounds_inputs(self):
        base = ['apphost', '--expected-kind', KIND, '--runtime', 'linux-x64', '--source-commit', 'a' * 40,
                '--expected-apphost-sha256', 'b' * 64, '--evidence-dir', 'fresh']
        with contextlib.redirect_stderr(io.StringIO()), mock.patch.object(runner.sys, 'platform', 'linux'), mock.patch.object(runner.shutil, 'which', return_value='/tool'):
            self.assertEqual(runner.parse_args(base).timeout, 120)
            for extra in (['--timeout', '121'], ['--timeout', 'nan'], ['--timeout', '0'], ['--archive', 'candidate.tar.gz'],
                          ['--source-commit', 'short'], ['--expected-apphost-sha256', 'short'], ['--allow-native-desktop']):
                with self.subTest(extra=extra), self.assertRaises(SystemExit):
                    runner.parse_args(base + extra)
            with mock.patch.object(runner.sys, 'platform', 'darwin'), self.assertRaises(SystemExit):
                runner.parse_args(base)
            with mock.patch.object(runner.shutil, 'which', return_value=None), self.assertRaises(SystemExit):
                runner.parse_args(base)

    def test_private_desktop_command_is_explicit(self):
        command = runner.private_command(PurePosixPath('/candidate/MeowChan'), KIND, PurePosixPath('/owned'))
        self.assertEqual(command[0], 'dbus-run-session')
        self.assertIn('--config-file=/owned/bus.conf', command)
        self.assertIn('xvfb-run', command)
        self.assertTrue(any('with-isolated-window-manager.py' in part for part in command))
        self.assertEqual(command[-3:], ['/candidate/MeowChan', '--smoke-video-error', KIND])

    def test_archive_and_expected_hash_are_bound_before_launch(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            app = base / 'apphost'
            app.write_bytes(b'fixture apphost')
            archive = base / 'candidate.zip'
            with zipfile.ZipFile(archive, 'w') as target:
                target.writestr('MeowChan', app.read_bytes())
            args = SimpleNamespace(apphost=app, expected_kind=KIND, runtime='linux-x64', source_commit='a' * 40,
                                   expected_apphost_sha256=runner.workflow.sha256(app), evidence_dir=base / 'evidence',
                                   archive=archive, archive_member='MeowChan', timeout=5)
            def fake_check(*unused):
                report = json.loads((args.evidence_dir / 'video-error.json').read_text())
                self.assertFalse(report['completed'])
                self.assertFalse(report['passed'])
                self.assertEqual(report['archiveBinding']['memberSha256'], args.expected_apphost_sha256)
                return dict(passed=True, exitCode=2, observedKind=KIND, profileAbsent=True)
            with mock.patch.object(runner, 'parse_args', return_value=args), mock.patch.object(runner, 'inspect_runtime', return_value='linux-x64'), mock.patch.object(runner, 'run_check', side_effect=fake_check), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(runner.main(), 0)
            report = json.loads((args.evidence_dir / 'video-error.json').read_text())
            self.assertTrue(report['passed'])
            self.assertEqual(report['expectedApphostSha256'], report['observedApphostSha256'])
            self.assertEqual(report['observedApphostSha256'], report['apphostSha256After'])
            self.assertEqual(report['observedRuntime'], 'linux-x64')
            self.assertEqual(report['declaredSourceCommit'], 'a' * 40)
            args.expected_apphost_sha256 = '0' * 64
            with mock.patch.object(runner, 'parse_args', return_value=args), mock.patch.object(runner, 'run_check') as run:
                with self.assertRaisesRegex(ValueError, 'expected SHA256'):
                    runner.main()
                run.assert_not_called()

    def test_runtime_mismatch_prevents_launch_and_post_run_binary_changes_fail(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            app = base / 'apphost'
            app.write_bytes(b'original fixture')
            args = SimpleNamespace(apphost=app, expected_kind=KIND, runtime='linux-x64', source_commit='a' * 40,
                                   expected_apphost_sha256=runner.workflow.sha256(app), evidence_dir=base / 'evidence',
                                   archive=None, archive_member=None, timeout=5)
            with mock.patch.object(runner, 'parse_args', return_value=args), mock.patch.object(runner, 'inspect_runtime', return_value='linux-arm64'), mock.patch.object(runner, 'run_check') as run:
                with self.assertRaisesRegex(ValueError, 'expected runtime'):
                    runner.main()
                run.assert_not_called()
            def mutate(*unused):
                app.write_bytes(b'changed during check')
                return dict(passed=True, exitCode=2, observedKind=KIND, profileAbsent=True)
            with mock.patch.object(runner, 'parse_args', return_value=args), mock.patch.object(runner, 'inspect_runtime', return_value='linux-x64'), mock.patch.object(runner, 'run_check', side_effect=mutate), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(runner.main(), 1)
            report = json.loads((args.evidence_dir / 'video-error.json').read_text())
            self.assertFalse(report['passed'])
            self.assertNotEqual(report['observedApphostSha256'], report['apphostSha256After'])
            self.assertIn('changed during', report['error'])


@unittest.skipIf(os.name == 'nt', 'POSIX fake subprocesses; no actual native app is launched')
class VideoErrorProcessTests(unittest.TestCase):
    def test_success_keeps_profile_absent_retains_evidence_and_cleans_temp_root(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            app = fake_apphost(base)
            inherited = dict(MEOWCHAN_PROFILE='/personal/profile', OCHAN_PROFILE='/old/profile', DISPLAY=':0',
                             WAYLAND_DISPLAY='wayland-0', DBUS_SESSION_BUS_ADDRESS='personal-bus')
            with mock.patch.dict(os.environ, inherited), mock.patch.object(runner, 'private_command', side_effect=direct_fake_command):
                result = runner.run_check(app, KIND, base / 'evidence', 5)
            self.assertTrue(result['passed'], result)
            self.assertEqual(result['exitCode'], 2)
            self.assertTrue(result['profileAbsent'])
            self.assertFalse(Path(result['workingDirectory']).exists())
            self.assertTrue((base / 'evidence/metrics.json').is_file())
            self.assertTrue((base / 'evidence/error.png').is_file())
            self.assertEqual(result['observedKind'], KIND)

    def test_marker_exit_kind_and_no_session_are_independently_required(self):
        for behavior in ('wrong-kind', 'session-check-missing', 'no-marker', 'misleading-marker', 'exit-zero',
                         'profile-write', 'lock-write', 'evidence-link'):
            with self.subTest(behavior=behavior), tempfile.TemporaryDirectory() as temporary:
                base = Path(temporary)
                app = fake_apphost(base, behavior)
                with mock.patch.object(runner, 'private_command', side_effect=direct_fake_command):
                    result = runner.run_check(app, KIND, base / 'evidence', 5)
                self.assertFalse(result['passed'])
                self.assertIn('error', result)
                self.assertFalse(Path(result['workingDirectory']).exists())
                if behavior in ('profile-write', 'lock-write'):
                    self.assertFalse(result['profileAbsent'])
                    self.assertTrue(result['unexpectedProfileArtifacts'])

    def test_timeout_kills_owned_process_and_retains_existing_sidecars(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            app = fake_apphost(base, 'timeout')
            with mock.patch.object(runner, 'private_command', side_effect=direct_fake_command):
                result = runner.run_check(app, KIND, base / 'evidence', .4)
            self.assertFalse(result['passed'])
            self.assertTrue(result['timedOut'])
            self.assertTrue((base / 'evidence/metrics.json').is_file())
            self.assertTrue((base / 'evidence/error.png').is_file())
            with self.assertRaises(ProcessLookupError):
                os.kill(result['runnerProcessId'], 0)
            self.assertFalse(Path(result['workingDirectory']).exists())

    def test_output_and_sidecar_sizes_are_bounded(self):
        for limit, size, behavior in [('MAX_LOG_BYTES', 1024, 'output-flood'), ('MAX_JSON_BYTES', 10, 'pass'), ('MAX_PNG_BYTES', 10, 'pass')]:
            with self.subTest(limit=limit), tempfile.TemporaryDirectory() as temporary:
                base = Path(temporary)
                app = fake_apphost(base, behavior)
                with mock.patch.object(runner, 'private_command', side_effect=direct_fake_command), mock.patch.object(runner, limit, size):
                    result = runner.run_check(app, KIND, base / 'evidence', 5)
                self.assertFalse(result['passed'])
                if limit == 'MAX_LOG_BYTES':
                    self.assertLessEqual((base / 'evidence/output.log').stat().st_size, size)

    def test_disk_reserve_prevents_launch(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            app = fake_apphost(base)
            with mock.patch.object(runner.shutil, 'disk_usage', return_value=mock.Mock(free=runner.workflow.RESERVE_BYTES - 1)), mock.patch.object(runner.subprocess, 'Popen') as launch:
                result = runner.run_check(app, KIND, base / 'evidence', 5)
            self.assertFalse(result['passed'])
            self.assertIn('2 GiB', result['error'])
            launch.assert_not_called()


if __name__ == '__main__':
    unittest.main()
